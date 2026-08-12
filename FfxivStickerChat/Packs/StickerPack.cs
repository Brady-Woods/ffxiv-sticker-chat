using System;
using System.Collections.Generic;

namespace FfxivStickerChat.Packs;

/// <summary>One phrase bound to one image within a pack.</summary>
public sealed class PackEntry
{
    /// <summary>Auto-translate group id.</summary>
    public uint Group { get; set; }

    /// <summary>Auto-translate key within <see cref="Group"/>.</summary>
    public uint Key { get; set; }

    /// <summary>The rendered phrase. Display only — matching is on group/key.</summary>
    public string Phrase { get; set; } = string.Empty;

    /// <summary>SHA-256 of the image bytes, lowercase hex. Names the file in the pack's media folder.</summary>
    public string Media { get; set; } = string.Empty;

    /// <summary>
    /// Game icon id to use instead of a file, or 0 for a normal image.
    /// </summary>
    /// <remarks>
    /// Lets a pack point at artwork the player already has installed rather than carrying a copy of it.
    /// Nothing is downloaded, nothing is redistributed, and the art always matches their game version.
    /// </remarks>
    public uint GameIconId { get; set; }

    /// <summary>File extension including the dot, e.g. <c>.png</c>.</summary>
    public string Extension { get; set; } = ".png";

    public bool Enabled { get; set; } = true;
}

/// <summary>
/// A named collection of phrase bindings, the unit of sharing.
/// </summary>
/// <remarks>
/// Media is not stored inside the pack. Entries reference a content hash, and the bytes live once in the
/// shared media store, so the same image used by five packs costs one file and one GPU texture.
/// </remarks>
public sealed class StickerPack
{
    /// <summary>Stable identity, preserved across export and import so updates replace rather than duplicate.</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string Name { get; set; } = "Untitled pack";

    public string Author { get; set; } = string.Empty;

    /// <summary>Bumped on export so an importer can tell a newer copy from an older one.</summary>
    public int Version { get; set; } = 1;

    public string Description { get; set; } = string.Empty;

    public List<PackEntry> Entries { get; set; } = [];

    /// <summary>
    /// Character this pack belongs to, stamped when it is created.
    /// </summary>
    /// <remarks>
    /// Not editable. A pack is built for one character and carries that identity through export, so
    /// importing a friend's pack pairs it to them automatically with nothing to configure. Making this
    /// editable would let anyone retarget someone else's pack at an arbitrary character, which would
    /// make ownership meaningless.
    /// </remarks>
    public string OwnerName { get; set; } = string.Empty;

    /// <summary>Home world of <see cref="OwnerName"/>, so identical names on different worlds differ.</summary>
    public ushort OwnerWorldId { get; set; }

    /// <summary>Display name of the owner's world, for the UI only.</summary>
    public string OwnerWorldName { get; set; } = string.Empty;

    /// <summary>False hides the pack from resolution without deleting it.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Lower numbers win when two enabled packs bind the same phrase.</summary>
    public int Priority { get; set; }

    /// <summary>True for the pack this user edits and exports.</summary>
    public bool IsLocal { get; set; }

    /// <summary>
    /// True for a pack the plugin ships and manages. Not editable, not exportable.
    /// </summary>
    /// <remarks>
    /// A built-in pack references game icons only, so it can be regenerated on every launch instead of
    /// being stored, and it never carries artwork that is not already on the player's disk.
    /// </remarks>
    public bool IsBuiltIn { get; set; }

    /// <summary>Finds an enabled entry for a phrase id.</summary>
    public PackEntry? Find(uint group, uint key)
    {
        foreach (var entry in Entries)
        {
            if (entry.Enabled && entry.Group == group && entry.Key == key)
                return entry;
        }

        return null;
    }

    /// <summary>
    /// Whether this pack should be consulted for a message from a given speaker.
    /// </summary>
    /// <remarks>
    /// An unstamped pack applies to everyone, which keeps packs made before ownership existed working.
    /// The world id is only compared when both sides know it, since the game does not always supply one.
    /// </remarks>
    public bool AppliesToSender(string sender, ushort worldId)
    {
        if (string.IsNullOrEmpty(OwnerName))
            return true;

        if (!OwnerName.Equals(sender, StringComparison.OrdinalIgnoreCase))
            return false;

        if (OwnerWorldId != 0 && worldId != 0 && OwnerWorldId != worldId)
            return false;

        return true;
    }

    /// <summary>Owner as <c>Name@World</c>, or a placeholder when unstamped.</summary>
    public string OwnerDisplay =>
        string.IsNullOrEmpty(OwnerName)
            ? "(unassigned - applies to everyone)"
            : string.IsNullOrEmpty(OwnerWorldName)
                ? OwnerName
                : $"{OwnerName}@{OwnerWorldName}";
}
