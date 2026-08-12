using System;
using System.Collections.Generic;
using Dalamud.Configuration;

namespace FfxivStickerChat;

/// <summary>
/// One auto-translate phrase bound to one image. The binding is 1:1 in both directions.
/// </summary>
[Serializable]
public class StickerMapping
{
    /// <summary>Auto-translate group id, as reported by <c>AutoTranslatePayload.Group</c>.</summary>
    public uint Group { get; set; }

    /// <summary>Auto-translate key/row id within <see cref="Group"/>.</summary>
    public uint Key { get; set; }

    /// <summary>
    /// The rendered phrase, normalised. Used both to display the binding in the config UI and to match
    /// against a live bubble's text node.
    /// </summary>
    public string Phrase { get; set; } = string.Empty;

    /// <summary>Absolute path to the image shown in place of the bubble.</summary>
    public string ImagePath { get; set; } = string.Empty;

    public bool Enabled { get; set; } = true;
}

[Serializable]
public class Configuration : IPluginConfiguration
{
    /// <summary>
    /// Schema version. Bumped when a default changes in a way an existing config must follow — changing
    /// a default alone does nothing for anyone who has already saved one.
    /// </summary>
    public int Version { get; set; } = CurrentVersion;

    public const int CurrentVersion = 6;

    /// <summary>Applies migrations for configs written by an older build.</summary>
    public void Migrate()
    {
        if (Version < 2)
        {
            // v1 defaulted the sticker to node 6, which is the bubble's tail — using it consumed the
            // tail and left the bubble unanchored. Node 8 sits inside the body instead.
            if (StickerNodeId == 6)
            {
                StickerNodeId = 8;
                Services.Log.Information("Config migrated: sticker node 6 (tail) -> 8 (body)");
            }

            Version = 2;
            Save();
        }

        if (Version < 5)
        {
            // The allowlist is gone. Anything previously ticked stays on, and anything unknown — which an
            // allowlist would have dropped — now works by default.
            DisabledChannels.Clear();

            if (EnabledChannels.Count > 0)
            {
                foreach (var channel in BubbleChannels.All)
                {
                    if (!EnabledChannels.Contains(channel.Id))
                        DisabledChannels.Add(channel.Id);
                }
            }

            EnabledChannels.Clear();
            Version = 5;
            Save();
            Services.Log.Information("Config migrated: channel filter is now a blocklist.");
        }

        if (Version < 6)
        {
            // Pack sync arrives off by default, and stays off for anyone upgrading. The new bool
            // properties would deserialise to false anyway; the point of this block is to advance the
            // version and to make sure the one-time explainer is shown before anything leaves the
            // machine.
            SyncShare = false;
            SyncReceive = false;
            SyncNoticeShown = false;

            Version = 6;
            Save();
            Services.Log.Information("Config migrated: pack sync added, disabled by default.");
        }
    }

    /// <summary>
    /// Moves the old flat binding list into the local pack.
    /// </summary>
    /// <remarks>
    /// Runs after the pack store is available, since it needs to copy each image into the content
    /// addressed media store. Legacy entries are cleared once moved so the migration is not repeated.
    /// </remarks>
    public void MigrateMappingsToPack(Packs.PackStore store)
    {
        if (Version >= 3)
            return;

        if (Mappings.Count > 0)
        {
            var local = store.GetOrCreateLocal();
            var moved = 0;

            foreach (var mapping in Mappings)
            {
                if (string.IsNullOrWhiteSpace(mapping.ImagePath))
                    continue;

                var stored = store.ImportMedia(local, mapping.ImagePath, out var error);
                if (stored is null)
                {
                    Services.Log.Warning($"Could not migrate {mapping.ImagePath}: {error}");
                    continue;
                }

                local.Entries.Add(new Packs.PackEntry
                {
                    Group = mapping.Group,
                    Key = mapping.Key,
                    Phrase = mapping.Phrase,
                    Media = stored.Value.Hash,
                    Extension = stored.Value.Extension,
                    Enabled = mapping.Enabled,
                });

                moved++;
            }

            store.Save(local);
            Services.Log.Information($"Migrated {moved} binding(s) into the local pack.");
        }

        Mappings.Clear();
        Version = 3;
        Save();
    }

    /// <summary>
    /// Legacy flat bindings, superseded by packs.
    /// </summary>
    /// <remarks>Retained only so an existing config can be migrated; cleared once moved.</remarks>
    public List<StickerMapping> Mappings { get; set; } = [];

    /// <summary>Budget for decoded sticker textures before the least recently used are dropped.</summary>
    public int TextureCacheBudgetMb { get; set; } = 96;

    /// <summary>Master switch, so the user can disable replacement without losing their bindings.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Hide the bubble's nine-grid background so the sticker floats bare.</summary>
    public bool HideBubbleBackground { get; set; } = true;

    /// <summary>
    /// Also decorate NPC speech balloons (<c>_MiniTalk</c>). Off by default — player chat lives in the
    /// separate <c>MiniTalkPlayer</c> addon, so this is only useful for testing against NPC dialogue.
    /// </summary>
    public bool IncludeNpcBubbles { get; set; }

    /// <summary>Only replace bubbles for your own messages.</summary>
    public bool OnlyLocalPlayer { get; set; }

    /// <summary>
    /// Chat channels whose bubbles must NOT be replaced, as log kind ids.
    /// </summary>
    /// <remarks>
    /// Deliberately a blocklist. An allowlist silently dropped any log kind missing from our hand-written
    /// channel list — the game uses ids Dalamud's enum does not name — which turned adding a filter into
    /// a regression. Blocking only what is explicitly unticked means an unrecognised channel keeps
    /// working, and the game's own per-channel bubble setting remains the real gate.
    /// </remarks>
    public List<ushort> DisabledChannels { get; set; } = [];

    /// <summary>Legacy allowlist, migrated to <see cref="DisabledChannels"/>.</summary>
    public List<ushort> EnabledChannels { get; set; } = [];

    /// <summary>Whether a bubble on this channel may be replaced.</summary>
    public bool IsChannelEnabled(ushort logKindId) => !DisabledChannels.Contains(logKindId);

    /// <summary>
    /// Longest edge, in pixels, the sticker may occupy. The image is fitted inside a square of this size
    /// with its aspect ratio preserved, so a 512×512 source and a 512×256 one both fit without squashing
    /// and neither needs pre-scaling. This is the on-screen size, not the source size: the value is
    /// divided back out through the node's own and its ancestors' scale factors before being applied.
    /// </summary>
    public float StickerMaxSize { get; set; } = 128f;

    /// <summary>
    /// Shrink the bubble frame to wrap the sticker instead of hiding it.
    /// </summary>
    /// <remarks>
    /// The frame is a nine-grid, so it rescales to any rect without distorting its corners. When on,
    /// <see cref="HideBubbleBackground"/> is ignored — the point is to keep the frame and fit it.
    /// </remarks>
    public bool FitBubbleToSticker { get; set; } = true;

    /// <summary>Gap between the sticker and the inside of the bubble frame, in node units.</summary>
    public float BubblePadding { get; set; } = 12f;

    /// <summary>Nudge the sticker horizontally from the centre of the bubble body, in node units.</summary>
    public float StickerOffsetX { get; set; }

    /// <summary>Nudge the sticker vertically from the centre of the bubble body, in node units.</summary>
    public float StickerOffsetY { get; set; }

    /// <summary>
    /// Node id of the image node inside a bubble to use as the sticker surface.
    /// </summary>
    /// <remarks>
    /// A bubble has more than one image node. In the observed <c>MiniTalkPlayer</c> layout, id 6 is the
    /// 32×32 tail under a 1×1 parent and id 8 sits inside the body alongside the frame nine-grid. Id 8 is
    /// the default: it shares a coordinate space with the frame, so centring and resizing are trivial, and
    /// it leaves the tail alone to keep pointing at the speaker. Exposed as a setting because the ULD is
    /// undocumented and may differ; when no node matches, the largest image
    /// node is used instead.
    /// </remarks>
    public uint StickerNodeId { get; set; } = 8;

    /// <summary>
    /// Strip the bubble's colour tint from the sticker so the artwork renders at its true colours.
    /// </summary>
    /// <remarks>
    /// Patch 7.3 added per-channel bubble colours, applied through the node's additive and multiplicative
    /// colour fields. Those apply to whatever the node draws, so a sticker inherits the channel's tint —
    /// typically a warm cast on Say. Turn this off to let stickers take the channel colour deliberately.
    /// </remarks>
    public bool NeutralizeStickerTint { get; set; } = true;

    /// <summary>Also neutralise the tint on the sticker's ancestor nodes, not just the node itself.</summary>
    public bool NeutralizeAncestorTint { get; set; } = true;

    /// <summary>Whether to touch the multiplicative colour fields at all.</summary>
    public bool NeutralizeMultiply { get; set; } = true;

    /// <summary>
    /// Value written to the multiplicative colour fields when neutralising.
    /// </summary>
    /// <remarks>
    /// Exposed because the scale is not settled: 255 rendered black and 100 still left a cast. Adjust
    /// live and read the tint dump in the log rather than guessing.
    /// </remarks>
    public byte NeutralMultiplyValue { get; set; } = 100;

    /// <summary>Log every bubble mutation. Noisy; for bring-up only.</summary>
    public bool VerboseLogging { get; set; }

    // ---- Pack sync ------------------------------------------------------------------------------
    //
    // Off by default, both directions. Sharing publishes a pointer to your packs to the players you are
    // already paired with in a sync client; receiving fetches theirs from the URL they advertise, which
    // means contacting that host directly. Neither should start without being asked for.

    /// <summary>Advertise this character's packs to sync pairs.</summary>
    public bool SyncShare { get; set; }

    /// <summary>Download packs advertised by sync pairs.</summary>
    public bool SyncReceive { get; set; }

    /// <summary>Whether the one-time explanation of what sync sends has been shown.</summary>
    public bool SyncNoticeShown { get; set; }

    /// <summary>
    /// Opaque per-install token, if the sync client's extension API wants one to register with.
    /// </summary>
    /// <remarks>
    /// Generated once and kept, rather than per session, so a pair sees a stable identity across logins.
    /// Not derived from anything about the character or machine.
    /// </remarks>
    public string SyncToken { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>Sync pairs whose packs are never fetched.</summary>
    public List<string> SyncBlockedUids { get; set; } = [];

    /// <summary>
    /// Which pair a synced pack first arrived from, as <c>packId -> uid</c>.
    /// </summary>
    /// <remarks>
    /// Pins a pack to its origin so a second pair cannot take it over by advertising the same id. Lives
    /// here rather than on <see cref="Packs.StickerPack"/> deliberately: a field there would travel inside
    /// the exported zip, which would make the value attacker-supplied and useless for this.
    /// </remarks>
    public Dictionary<string, string> SyncPackOrigin { get; set; } = [];

    /// <summary>When each synced pack was last advertised by a visible pair, as unix seconds.</summary>
    /// <remarks>Drives eviction once <see cref="SyncStorageBudgetMb"/> is reached.</remarks>
    public Dictionary<string, long> SyncLastSeen { get; set; } = [];

    /// <summary>
    /// Disk budget for packs received over sync, in megabytes.
    /// </summary>
    /// <remarks>
    /// A pack is up to about 60 MB, so a few dozen pairs would otherwise grow without bound. The default
    /// holds roughly eight full-size packs.
    /// </remarks>
    public int SyncStorageBudgetMb { get; set; } = 512;

    /// <summary>Whether a pair's packs may be fetched.</summary>
    public bool IsSyncPairAllowed(string uid) => !SyncBlockedUids.Contains(uid);

    public void Save() => Services.PluginInterface.SavePluginConfig(this);
}
