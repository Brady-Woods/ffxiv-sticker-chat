using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using FfxivStickerChat.Packs;

namespace FfxivStickerChat.Sync;

/// <summary>One pack a player is advertising: where to get it and how to know it arrived intact.</summary>
/// <param name="Id">The pack id, which is also the identity an update replaces.</param>
/// <param name="Name">Display name, for showing what is on offer before fetching it.</param>
/// <param name="Hash">SHA-256 of the archive, lowercase hex.</param>
/// <param name="Url">Direct https download for the archive.</param>
public sealed record SyncPointer(string Id, string Name, string Hash, string Url);

/// <summary>
/// The payload published to other players over a sync client's extension channel.
/// </summary>
/// <remarks>
/// <para>
/// The channel allows 4096 bytes per plugin, so images cannot travel through it — a single sticker is up
/// to 512 KB. It carries pointers instead: a pack id, a name, an archive hash and a URL. The zip at the
/// far end already contains the bindings and the artwork, and
/// <see cref="PackDownloader.DownloadAndImportAsync"/> already knows how to fetch and verify one.
/// </para>
/// <para>
/// Every limit here is a construction guarantee rather than a runtime check: at
/// <see cref="MaxPacks"/> pointers with maximum-length fields the payload cannot reach the cap, so
/// <see cref="TryBuild"/> has no failure mode that depends on what the packs happen to contain.
/// </para>
/// <para>
/// Parsing is total. The input is written by another player's client, which may be a different version, a
/// modified build, or hostile — so every malformed shape yields an empty result rather than an exception.
/// </para>
/// </remarks>
public static class SyncManifest
{
    /// <summary>Bytes the extension channel allows one plugin.</summary>
    public const int MaxPayloadBytes = 4096;

    /// <summary>Most packs one player may advertise at once.</summary>
    public const int MaxPacks = 8;

    /// <summary>Longest advertised pack name, in UTF-8 bytes.</summary>
    public const int MaxNameBytes = 64;

    /// <summary>Longest advertised URL, in UTF-8 bytes.</summary>
    public const int MaxUrlBytes = 256;

    /// <summary>Length of a SHA-256 in lowercase hex.</summary>
    private const int HashLength = 64;

    /// <summary>
    /// Relaxed escaping keeps non-ASCII names at their UTF-8 cost.
    /// </summary>
    /// <remarks>
    /// The default encoder escapes anything non-ASCII as <c>\uXXXX</c>, which turns one Japanese character
    /// into six bytes and would make the size budget depend on the language a pack is named in.
    /// </remarks>
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Wire shape. Field names are single letters because the budget is 4096 bytes.</summary>
    private sealed class Wire
    {
        [JsonPropertyName("v")]
        public int V { get; set; }

        [JsonPropertyName("p")]
        public List<WirePointer>? P { get; set; }
    }

    private sealed class WirePointer
    {
        [JsonPropertyName("i")]
        public string? I { get; set; }

        [JsonPropertyName("n")]
        public string? N { get; set; }

        [JsonPropertyName("h")]
        public string? H { get; set; }

        [JsonPropertyName("u")]
        public string? U { get; set; }
    }

    /// <summary>Schema version, so a future change can be recognised rather than guessed at.</summary>
    private const int SchemaVersion = 1;

    /// <summary>
    /// Builds the payload advertising the given packs.
    /// </summary>
    /// <remarks>
    /// Packs that cannot be fetched by a recipient — no URL, no archive hash, or a URL that is not plain
    /// https — are skipped rather than advertised, since a pointer nobody can act on only wastes budget
    /// and produces a failure on the far side.
    /// </remarks>
    /// <returns>The JSON payload, or an empty string when there is nothing worth advertising.</returns>
    public static string Build(IEnumerable<StickerPack> packs)
    {
        var pointers = new List<WirePointer>();

        foreach (var pack in packs)
        {
            if (pointers.Count >= MaxPacks)
                break;

            if (!IsAdvertisable(pack, out _))
                continue;

            pointers.Add(new WirePointer
            {
                I = pack.Id,
                N = Truncate(pack.Name, MaxNameBytes),
                H = pack.ArchiveHash.ToLowerInvariant(),
                U = pack.SourceUrl.Trim(),
            });
        }

        if (pointers.Count == 0)
            return string.Empty;

        var json = JsonSerializer.Serialize(new Wire { V = SchemaVersion, P = pointers }, JsonOptions);

        // Should be unreachable: the field caps bound the worst case below the limit. If it ever fires,
        // a cap was raised without re-checking the arithmetic, and dropping pointers beats being silently
        // rejected by the transport.
        while (Encoding.UTF8.GetByteCount(json) > MaxPayloadBytes && pointers.Count > 0)
        {
            Services.Log.Warning(
                $"Sync payload is {Encoding.UTF8.GetByteCount(json)} bytes with {pointers.Count} pack(s); " +
                "dropping the last one. The size caps need revisiting.");

            pointers.RemoveAt(pointers.Count - 1);

            json = pointers.Count == 0
                ? string.Empty
                : JsonSerializer.Serialize(new Wire { V = SchemaVersion, P = pointers }, JsonOptions);
        }

        return json;
    }

    /// <summary>
    /// Whether a pack can be advertised, and why not when it cannot.
    /// </summary>
    /// <remarks>
    /// Exposed so the UI can explain a pack's absence from the manifest in the same words the builder
    /// used to exclude it, rather than leaving it silently missing.
    /// </remarks>
    public static bool IsAdvertisable(StickerPack pack, out string reason)
    {
        reason = string.Empty;

        if (!PackStore.IsValidPackId(pack.Id))
        {
            reason = "malformed pack id";
            return false;
        }

        if (string.IsNullOrWhiteSpace(pack.SourceUrl))
        {
            reason = "no download URL set";
            return false;
        }

        if (Encoding.UTF8.GetByteCount(pack.SourceUrl.Trim()) > MaxUrlBytes)
        {
            reason = $"URL is longer than {MaxUrlBytes} bytes";
            return false;
        }

        if (!PackDownloader.TryValidateUrl(pack.SourceUrl, out _, out var urlReason))
        {
            reason = urlReason;
            return false;
        }

        // Normalised first: our own exporter writes lowercase, but a hand-edited manifest may not, and
        // case should not decide whether a pack is shareable.
        if (!IsHash(pack.ArchiveHash?.ToLowerInvariant()))
        {
            reason = string.IsNullOrEmpty(pack.ArchiveHash)
                ? "no archive hash — export the pack again to record one"
                : "archive hash is not a SHA-256";
            return false;
        }

        return true;
    }

    /// <summary>
    /// Reads a payload written by another player.
    /// </summary>
    /// <remarks>
    /// Never throws and never returns a partially valid pointer: a pointer with any bad field is dropped,
    /// and anything unparseable yields an empty list. Callers can treat the result as trustworthy in shape
    /// only — whether a pointer should be acted on is a separate decision.
    /// </remarks>
    public static IReadOnlyList<SyncPointer> Parse(string? payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
            return [];

        // Bounded before parsing: a sender is not obliged to respect the transport's limit, and there is
        // no reason to allocate a parser over megabytes of junk.
        if (Encoding.UTF8.GetByteCount(payload) > MaxPayloadBytes)
        {
            Services.Log.Warning("Ignoring an oversized sync payload.");
            return [];
        }

        Wire? wire;

        try
        {
            wire = JsonSerializer.Deserialize<Wire>(payload, JsonOptions);
        }
        catch (JsonException)
        {
            // Expected for a malformed or differently-shaped payload; not worth an error-level log.
            return [];
        }

        if (wire is null || wire.P is null)
            return [];

        if (wire.V != SchemaVersion)
        {
            Services.Log.Information($"Ignoring a sync payload with unknown schema version {wire.V}.");
            return [];
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var pointers = new List<SyncPointer>();

        foreach (var p in wire.P)
        {
            if (pointers.Count >= MaxPacks)
                break;

            if (p is null)
                continue;

            var id = p.I ?? string.Empty;
            var hash = (p.H ?? string.Empty).ToLowerInvariant();
            var url = (p.U ?? string.Empty).Trim();
            var name = Truncate(p.N ?? string.Empty, MaxNameBytes);

            // The id is validated here as well as at import, because it is used as a dictionary key and
            // shown in the UI long before anything is downloaded.
            if (!PackStore.IsValidPackId(id) || !IsHash(hash))
                continue;

            if (Encoding.UTF8.GetByteCount(url) > MaxUrlBytes)
                continue;

            if (!PackDownloader.TryValidateUrl(url, out _, out _))
                continue;

            // One pointer per pack. A duplicated id would otherwise queue the same download twice.
            if (!seen.Add(id))
                continue;

            pointers.Add(new SyncPointer(id, name, hash, url));
        }

        return pointers;
    }

    /// <summary>Whether a string is a SHA-256 in lowercase hex.</summary>
    private static bool IsHash(string? value)
    {
        if (value is null || value.Length != HashLength)
            return false;

        foreach (var c in value)
        {
            if (c is not (>= '0' and <= '9' or >= 'a' and <= 'f'))
                return false;
        }

        return true;
    }

    /// <summary>
    /// Cuts a string to a UTF-8 byte budget without splitting a character.
    /// </summary>
    /// <remarks>
    /// Trimming by <see cref="string.Length"/> would measure UTF-16 units and overshoot the byte budget
    /// for any non-Latin name; cutting mid-rune would produce invalid UTF-8 or a broken emoji.
    /// </remarks>
    internal static string Truncate(string value, int maxBytes)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;

        if (Encoding.UTF8.GetByteCount(value) <= maxBytes)
            return value;

        var span = value.AsSpan();
        var taken = 0;
        var bytes = 0;

        while (taken < span.Length)
        {
            // Rune.DecodeFromUtf16 keeps surrogate pairs together, so a truncated name never ends in half
            // an astral character.
            if (System.Text.Rune.DecodeFromUtf16(span[taken..], out var rune, out var consumed)
                != System.Buffers.OperationStatus.Done)
            {
                break;
            }

            if (bytes + rune.Utf8SequenceLength > maxBytes)
                break;

            bytes += rune.Utf8SequenceLength;
            taken += consumed;
        }

        return value[..taken];
    }
}
