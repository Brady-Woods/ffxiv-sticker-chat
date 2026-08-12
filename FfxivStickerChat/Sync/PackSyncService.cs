using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FfxivStickerChat.Packs;

namespace FfxivStickerChat.Sync;

/// <summary>How a synced pack stands right now, for showing beside it in the pack list.</summary>
public enum SyncState
{
    /// <summary>Not involved in sync.</summary>
    None,

    /// <summary>Advertised to pairs.</summary>
    Shared,

    /// <summary>Advertised by a pair and not yet fetched.</summary>
    Offered,

    /// <summary>Being fetched now.</summary>
    Downloading,

    /// <summary>Fetched, and matching what its origin advertises.</summary>
    Installed,

    /// <summary>A newer copy is advertised than the one held.</summary>
    Outdated,

    /// <summary>Refused, or the fetch failed. See <see cref="PackSyncService.GetStatusFor"/>.</summary>
    Failed,
}

/// <summary>
/// Decides what to do about packs other players advertise.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately knows nothing about any sync client. It is fed remote payloads through
/// <see cref="InjectRemoteData"/> and pair identities through <see cref="SetPairIdentity"/>, and it hands
/// back a payload to publish through <see cref="BuildLocalPayload"/>. That keeps every policy decision
/// here — and testable without a second game client, or even a first one.
/// </para>
/// <para>
/// Downloads are automatic: anything a pair advertises is fetched without asking, which is what makes
/// sync worth having. Three rules keep that from being a liability. The URL must be on a known host, so a
/// pair cannot aim your client at something they run. A pack must belong to the character advertising it,
/// so nobody can put stickers over a third party's head. And a pack is pinned to the pair it first came
/// from, so a second pair cannot take it over by claiming the same id.
/// </para>
/// </remarks>
public sealed class PackSyncService : IDisposable
{
    /// <summary>How long a failed pack is left alone before being tried again.</summary>
    private static readonly TimeSpan RetryAfter = TimeSpan.FromMinutes(30);

    private readonly Configuration configuration;
    private readonly PackStore store;

    /// <summary>What each pair is currently advertising, by uid.</summary>
    private readonly Dictionary<string, IReadOnlyList<SyncPointer>> advertised = new(StringComparer.Ordinal);

    /// <summary>Character behind each pair uid, learned while they are visible.</summary>
    private readonly Dictionary<string, (string Name, ushort WorldId)> identities = new(StringComparer.Ordinal);

    /// <summary>Packs waiting to be fetched, oldest first.</summary>
    private readonly Queue<PendingDownload> queue = new();

    /// <summary>Pack ids already queued or downloading, so one is never enqueued twice.</summary>
    private readonly HashSet<string> inFlight = new(StringComparer.Ordinal);

    /// <summary>
    /// Why a pack was last refused or failed, and what was advertised when it happened.
    /// </summary>
    /// <remarks>
    /// Keyed by pack id and holding the advertised hash, so a genuinely updated pack is retried
    /// immediately while an unchanged broken one is not retried at all until the backoff expires. Without
    /// this a pack whose hash does not match its file retries forever, once per tick.
    /// </remarks>
    private readonly Dictionary<string, Failure> failures = new(StringComparer.Ordinal);

    private readonly CancellationTokenSource cancellation = new();

    private bool downloading;
    private string? lastPublished;
    private int completedThisSession;

    private sealed record PendingDownload(string Uid, SyncPointer Pointer);

    private sealed record Failure(string Hash, DateTimeOffset When, string Reason);

    public PackSyncService(Configuration configuration, PackStore store)
    {
        this.configuration = configuration;
        this.store = store;
    }

    /// <summary>Raised when something a status line depends on changed.</summary>
    public event Action? Changed;

    /// <summary>Set by the bridge so status text can name the transport's own state.</summary>
    public string TransportStatus { get; set; } = "Not connected.";

    /// <summary>Whether the transport is usable. Governs whether publishing is attempted at all.</summary>
    public bool TransportReady { get; set; }

    // ---- Inbound ------------------------------------------------------------------------------------

    /// <summary>
    /// Accepts what a pair is advertising.
    /// </summary>
    /// <remarks>
    /// The primary test seam. Feeding a payload here exercises parsing, eligibility, the queue, the
    /// download, ownership checking and import — every part except the transport itself.
    /// </remarks>
    public void InjectRemoteData(string uid, string? payload)
    {
        if (string.IsNullOrEmpty(uid))
            return;

        var pointers = SyncManifest.Parse(payload);

        if (pointers.Count == 0)
            advertised.Remove(uid);
        else
            advertised[uid] = pointers;

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        foreach (var pointer in pointers)
            configuration.SyncLastSeen[pointer.Id] = now;

        Evaluate(uid);
        Changed?.Invoke();
    }

    /// <summary>Forgets a pair, e.g. when they are unpaired or go offline.</summary>
    public void RemovePair(string uid)
    {
        advertised.Remove(uid);
        identities.Remove(uid);
        Changed?.Invoke();
    }

    /// <summary>
    /// Records which character a pair uid belongs to.
    /// </summary>
    /// <remarks>
    /// Needed to enforce that a pack belongs to whoever advertises it. A pair whose character is not yet
    /// known has their packs held rather than refused, since the mapping is learned only while they are
    /// rendered nearby.
    /// </remarks>
    public void SetPairIdentity(string uid, string characterName, ushort worldId)
    {
        if (string.IsNullOrEmpty(uid) || string.IsNullOrEmpty(characterName))
            return;

        if (identities.TryGetValue(uid, out var known) &&
            known.Name == characterName && known.WorldId == worldId)
        {
            return;
        }

        identities[uid] = (characterName, worldId);

        // Their packs may have been waiting on exactly this.
        Evaluate(uid);
        Changed?.Invoke();
    }

    // ---- Outbound -----------------------------------------------------------------------------------

    /// <summary>
    /// The payload to publish, or an empty string when there is nothing to say.
    /// </summary>
    /// <remarks>
    /// Only the local player's own packs are advertised. Re-advertising a pack received from someone else
    /// would make a stranger's URL look like it came from you, and would let a pack circulate with its
    /// origin rewritten at every hop.
    /// </remarks>
    public string BuildLocalPayload()
    {
        if (!configuration.SyncShare)
            return string.Empty;

        return SyncManifest.Build(store.LocalPacks.Where(p => p.Enabled));
    }

    /// <summary>
    /// The payload if it differs from what was last published, else null.
    /// </summary>
    /// <remarks>
    /// Comparison rather than change notification, because the pack list is saved on every keystroke in
    /// the editor — anything hooked to saving would need debouncing, and this needs none.
    /// </remarks>
    public string? TakePayloadIfChanged()
    {
        var payload = BuildLocalPayload();

        if (string.Equals(payload, lastPublished, StringComparison.Ordinal))
            return null;

        lastPublished = payload;
        return payload;
    }

    /// <summary>Forces the next <see cref="TakePayloadIfChanged"/> to publish, e.g. after reconnecting.</summary>
    public void ForceRepublish() => lastPublished = null;

    // ---- Queue --------------------------------------------------------------------------------------

    /// <summary>
    /// Starts the next download if nothing is in flight. Call from the framework tick.
    /// </summary>
    /// <remarks>
    /// One at a time on purpose. Zoning into a crowded area can reveal a dozen pairs at once, and a dozen
    /// simultaneous 60 MB downloads would be indistinguishable from the plugin having broken.
    /// </remarks>
    public void Pump()
    {
        if (downloading || queue.Count == 0)
            return;

        var next = queue.Dequeue();
        downloading = true;

        _ = RunDownloadAsync(next);
    }

    private async Task RunDownloadAsync(PendingDownload pending)
    {
        var pointer = pending.Pointer;

        // Captured before the import, which forces Enabled true — a pack the user deliberately turned off
        // must not come back on just because its author published an update.
        var wasDisabled = store.Get(pointer.Id) is { Enabled: false };

        PackTransferResult result;

        try
        {
            result = await PackDownloader.DownloadAndImportAsync(
                pointer.Url,
                store,
                pointer.Hash,
                cancellation.Token,
                restrictToSyncHosts: true,
                approve: pack => ApproveOwnership(pack, pending.Uid)).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Plugin unloading; nothing to record.
            return;
        }
        catch (Exception ex)
        {
            result = new PackTransferResult(false, ex.Message);
        }

        // Back to the game thread before touching shared state or saving config.
        await Services.Framework.Run(() =>
        {
            downloading = false;
            inFlight.Remove(pointer.Id);

            if (result.Success && result.Pack is not null)
            {
                failures.Remove(pointer.Id);
                completedThisSession++;

                // Pin it to the pair it came from, so nobody else can claim this id later.
                configuration.SyncPackOrigin[pointer.Id] = pending.Uid;

                if (wasDisabled)
                {
                    result.Pack.Enabled = false;
                    store.Save(result.Pack);
                }

                configuration.Save();
                Services.Log.Information($"Sync: installed \"{result.Pack.Name}\" from {pending.Uid}.");

                EnforceStorageBudget();
            }
            else
            {
                failures[pointer.Id] = new Failure(pointer.Hash, DateTimeOffset.UtcNow, result.Message);
                Services.Log.Warning($"Sync: {pointer.Name} from {pending.Uid} was not installed — {result.Message}");
            }

            Changed?.Invoke();
        });
    }

    /// <summary>
    /// Refuses a pack that does not belong to the player advertising it.
    /// </summary>
    /// <remarks>
    /// A pack's owner is self-declared inside the zip, and the owner is what sticker matching compares a
    /// speaker against — so without this rule any pair could publish a pack owned by someone else and put
    /// their stickers over that person's head. Runs before anything is written, so a refused pack never
    /// touches the disk.
    /// </remarks>
    internal string? ApproveOwnership(StickerPack pack, string uid)
    {
        if (!identities.TryGetValue(uid, out var identity))
        {
            return "The character behind this sync pair is not known yet, so the pack cannot be " +
                   "confirmed as theirs. It will be retried once they are nearby.";
        }

        if (!pack.OwnerName.Equals(identity.Name, StringComparison.OrdinalIgnoreCase))
        {
            return $"Refused: the pack says it belongs to {pack.OwnerDisplay}, but it is being shared by " +
                   $"{identity.Name}. A pack can only be shared by the character it was made for.";
        }

        // World ids are only compared when both sides know one; the game does not always supply it.
        if (pack.OwnerWorldId != 0 && identity.WorldId != 0 && pack.OwnerWorldId != identity.WorldId)
        {
            return $"Refused: the pack claims world {pack.OwnerWorldId} but {identity.Name} is on " +
                   $"{identity.WorldId}.";
        }

        return null;
    }

    // ---- Eligibility --------------------------------------------------------------------------------

    /// <summary>Queues whatever a pair advertises that is worth fetching.</summary>
    private void Evaluate(string uid)
    {
        if (!configuration.SyncReceive)
            return;

        if (!configuration.IsSyncPairAllowed(uid))
            return;

        if (!advertised.TryGetValue(uid, out var pointers))
            return;

        foreach (var pointer in pointers)
        {
            if (!ShouldFetch(uid, pointer, out _))
                continue;

            inFlight.Add(pointer.Id);
            queue.Enqueue(new PendingDownload(uid, pointer));
        }
    }

    /// <summary>
    /// Whether a pointer should be fetched, and why not when it should not.
    /// </summary>
    /// <remarks>Pure apart from reading current state, so the UI can call it to explain itself.</remarks>
    public bool ShouldFetch(string uid, SyncPointer pointer, out string reason)
    {
        reason = string.Empty;

        if (inFlight.Contains(pointer.Id))
        {
            reason = "already queued";
            return false;
        }

        // Pinned to whoever sent it first. Checked before anything is fetched, so a second pair claiming
        // the id never even causes a request.
        if (configuration.SyncPackOrigin.TryGetValue(pointer.Id, out var origin) &&
            !string.Equals(origin, uid, StringComparison.Ordinal))
        {
            reason = $"that pack id already belongs to {origin}";
            return false;
        }

        // Refuse a pack that would replace one this user authored, before spending a download on it.
        var existing = store.Get(pointer.Id);
        if (existing is { IsLocal: true })
        {
            reason = "you authored a pack with that id";
            return false;
        }

        if (existing is not null &&
            string.Equals(existing.ArchiveHash, pointer.Hash, StringComparison.OrdinalIgnoreCase))
        {
            reason = "already up to date";
            return false;
        }

        if (!PackDownloader.TryValidateUrl(pointer.Url, out var uri, out var urlReason))
        {
            reason = urlReason;
            return false;
        }

        if (!PackDownloader.IsAllowedSyncHost(uri))
        {
            reason = $"{uri.Host} is not a host sync downloads from";
            return false;
        }

        // A failure against this exact hash is not retried until the backoff expires. A new hash means the
        // author changed something, which is worth another try immediately.
        if (failures.TryGetValue(pointer.Id, out var failure) &&
            string.Equals(failure.Hash, pointer.Hash, StringComparison.OrdinalIgnoreCase) &&
            DateTimeOffset.UtcNow - failure.When < RetryAfter)
        {
            reason = failure.Reason;
            return false;
        }

        return true;
    }

    /// <summary>Clears the backoff so a pack is retried on the next evaluation.</summary>
    public void RetryNow(string packId)
    {
        failures.Remove(packId);

        foreach (var uid in advertised.Keys.ToList())
            Evaluate(uid);

        Changed?.Invoke();
    }

    /// <summary>Stops fetching from a pair and forgets what they advertise.</summary>
    public void BlockPair(string uid)
    {
        if (!configuration.SyncBlockedUids.Contains(uid))
        {
            configuration.SyncBlockedUids.Add(uid);
            configuration.Save();
        }

        advertised.Remove(uid);
        Changed?.Invoke();
    }

    public void UnblockPair(string uid)
    {
        if (configuration.SyncBlockedUids.Remove(uid))
            configuration.Save();

        Evaluate(uid);
        Changed?.Invoke();
    }

    // ---- Storage ------------------------------------------------------------------------------------

    /// <summary>
    /// Deletes the least recently advertised synced packs until the budget is met.
    /// </summary>
    /// <remarks>
    /// Only ever touches packs that arrived over sync — a pack the user imported by hand or authored is
    /// theirs, and is never evicted no matter the budget.
    /// </remarks>
    public void EnforceStorageBudget()
    {
        var budget = (long)Math.Max(0, configuration.SyncStorageBudgetMb) * 1024 * 1024;
        if (budget == 0)
            return;

        var synced = store.Packs
            .Where(p => configuration.SyncPackOrigin.ContainsKey(p.Id))
            .Select(p => (Pack: p, Size: store.GetPackSize(p.Id)))
            .ToList();

        var total = synced.Sum(s => s.Size);
        if (total <= budget)
            return;

        // Oldest last-seen goes first; a pack never seen sorts oldest of all.
        foreach (var (pack, size) in synced
                     .OrderBy(s => configuration.SyncLastSeen.GetValueOrDefault(s.Pack.Id, 0)))
        {
            if (total <= budget)
                break;

            if (!store.Delete(pack.Id))
                continue;

            configuration.SyncPackOrigin.Remove(pack.Id);
            configuration.SyncLastSeen.Remove(pack.Id);
            total -= size;

            Services.Log.Information(
                $"Sync: removed \"{pack.Name}\" ({size / (1024 * 1024)} MB) to stay within the " +
                $"{configuration.SyncStorageBudgetMb} MB budget.");
        }

        configuration.Save();
    }

    /// <summary>Bytes currently held by packs that arrived over sync.</summary>
    public long SyncedBytes => store.Packs
        .Where(p => configuration.SyncPackOrigin.ContainsKey(p.Id))
        .Sum(p => store.GetPackSize(p.Id));

    // ---- Status -------------------------------------------------------------------------------------

    /// <summary>
    /// Why nothing is being published, or what is.
    /// </summary>
    /// <remarks>
    /// Ranked most-blocking first, and never returns a bare "idle". Every silent failure in this plugin so
    /// far — a release hidden by a flag, a download link that 404ed, a sticker claimed before it was used —
    /// looked exactly like nothing happening, so a status line that cannot explain itself is a bug.
    /// </remarks>
    public string PublishStatus
    {
        get
        {
            if (!TransportReady)
                return TransportStatus;

            if (!configuration.SyncShare)
                return "Sharing is off. Your packs are not advertised to anyone.";

            var local = store.LocalPacks.Where(p => p.Enabled).ToList();

            if (local.Count == 0)
                return "You have no enabled packs of your own to share.";

            var shareable = local.Where(p => SyncManifest.IsAdvertisable(p, out _)).ToList();

            if (shareable.Count == 0)
            {
                SyncManifest.IsAdvertisable(local[0], out var why);
                return $"None of your {local.Count} pack(s) can be shared — {why}. " +
                       "Sync sends a link, not the images, so a pack needs a download URL and an export.";
            }

            var held = local.Count - shareable.Count;
            var note = held > 0 ? $" ({held} cannot be: no URL or no export yet)" : string.Empty;

            return $"Advertising {shareable.Count} pack(s){note}.";
        }
    }

    /// <summary>Why nothing is arriving, or what is.</summary>
    public string ReceiveStatus
    {
        get
        {
            if (!TransportReady)
                return TransportStatus;

            if (!configuration.SyncReceive)
                return "Receiving is off. Packs other players share are ignored.";

            if (advertised.Count == 0)
                return "No sync pair is advertising any packs.";

            if (downloading || queue.Count > 0)
            {
                var remaining = queue.Count + (downloading ? 1 : 0);
                return $"Downloading — {remaining} pack(s) to go.";
            }

            var blocked = advertised
                .SelectMany(kv => kv.Value.Select(p => (Uid: kv.Key, Pointer: p)))
                .Count(x => failures.ContainsKey(x.Pointer.Id));

            if (blocked > 0)
            {
                return $"{blocked} pack(s) could not be installed. See the list below for why; " +
                       "\"Retry\" tries again without waiting.";
            }

            var offered = advertised.Values.Sum(v => v.Count);
            var installed = advertised.Values
                .SelectMany(v => v)
                .Count(p => store.Get(p.Id) is not null);

            return installed == offered
                ? $"Up to date — {installed} pack(s) from {advertised.Count} pair(s)."
                : $"{installed} of {offered} advertised pack(s) installed.";
        }
    }

    /// <summary>Whether the pack is involved in sync, and what it is doing.</summary>
    public SyncState GetStateFor(StickerPack pack)
    {
        if (pack.IsLocal)
            return SyncManifest.IsAdvertisable(pack, out _) && configuration.SyncShare
                ? SyncState.Shared
                : SyncState.None;

        if (inFlight.Contains(pack.Id))
            return SyncState.Downloading;

        if (failures.ContainsKey(pack.Id))
            return SyncState.Failed;

        var pointer = FindPointer(pack.Id);
        if (pointer is null)
            return configuration.SyncPackOrigin.ContainsKey(pack.Id) ? SyncState.Installed : SyncState.None;

        return string.Equals(pack.ArchiveHash, pointer.Hash, StringComparison.OrdinalIgnoreCase)
            ? SyncState.Installed
            : SyncState.Outdated;
    }

    /// <summary>A sentence explaining a pack's sync state, or empty when there is nothing to say.</summary>
    public string GetStatusFor(StickerPack pack)
    {
        if (failures.TryGetValue(pack.Id, out var failure))
            return failure.Reason;

        if (pack.IsLocal && !SyncManifest.IsAdvertisable(pack, out var why))
            return configuration.SyncShare ? $"Not shared — {why}." : string.Empty;

        return string.Empty;
    }

    /// <summary>Everything currently advertised that is not installed, for offering it in the UI.</summary>
    public IEnumerable<(string Uid, SyncPointer Pointer, string Blocker)> Offers()
    {
        foreach (var (uid, pointers) in advertised)
        {
            foreach (var pointer in pointers)
            {
                if (store.Get(pointer.Id) is not null && !failures.ContainsKey(pointer.Id))
                    continue;

                ShouldFetch(uid, pointer, out var blocker);
                yield return (uid, pointer, blocker);
            }
        }
    }

    /// <summary>Number of packs installed over sync since the plugin loaded.</summary>
    public int CompletedThisSession => completedThisSession;

    private SyncPointer? FindPointer(string packId)
    {
        foreach (var pointers in advertised.Values)
        {
            foreach (var pointer in pointers)
            {
                if (string.Equals(pointer.Id, packId, StringComparison.Ordinal))
                    return pointer;
            }
        }

        return null;
    }

    public void Dispose()
    {
        cancellation.Cancel();
        cancellation.Dispose();
    }
}
