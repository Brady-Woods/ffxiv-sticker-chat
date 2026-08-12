using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace FfxivStickerChat.Packs;

/// <summary>Outcome of an import or export, for reporting in the UI.</summary>
public sealed record PackTransferResult(bool Success, string Message, StickerPack? Pack = null);

/// <summary>
/// Reads and writes sticker packs as zip archives, for sharing a pack by hand.
/// </summary>
/// <remarks>
/// <para>Archive layout:</para>
/// <code>
/// pack.json
/// media/&lt;sha256&gt;.png
/// </code>
/// <para>
/// An archive is untrusted input: it arrives from another player. Every entry path is checked to stay
/// inside the destination, extensions are restricted to known image types, sizes are capped, and each
/// file's bytes are hashed and compared against the name it claims. A file whose contents do not match
/// its hash is discarded rather than written.
/// </para>
/// </remarks>
public static class PackArchive
{
    /// <summary>
    /// Largest total uncompressed payload, bounding a decompression bomb.
    /// </summary>
    /// <remarks>
    /// Derived from the per-sticker rules rather than picked arbitrarily: a full pack is at most
    /// 120 × 512 KB, and the allowance is doubled so a pack carrying a few unreferenced leftovers still
    /// imports.
    /// </remarks>
    public static readonly long MaxTotalBytes = StickerLimits.MaxEntriesPerPack * StickerLimits.MaxFileBytes * 2;

    private const int MaxEntries = StickerLimits.MaxEntriesPerPack * 2;

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static PackTransferResult Export(StickerPack pack, PackStore store, string destinationPath)
    {
        if (string.IsNullOrEmpty(pack.OwnerName))
        {
            return new PackTransferResult(false,
                "This pack has no owner yet. Log in to the character it belongs to and reopen the window.");
        }

        try
        {
            var directory = Path.GetDirectoryName(destinationPath);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            // Export a copy so bumping the version does not mutate the live pack.
            var exported = Clone(pack);
            exported.Version = pack.Version + 1;

            // Ownership travels with the pack: that is what pairs it to its author on the far side.
            // IsLocal does not, since the recipient does not author it.
            exported.IsLocal = false;

            // A file cannot contain its own hash. It is computed from the finished archive below and
            // kept on the author's pack, to be published next to the URL.
            exported.ArchiveHash = string.Empty;

            var written = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var missing = 0;

            // Scoped so the archive is flushed and closed before it is hashed.
            using (var stream = File.Create(destinationPath))
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
            {
                foreach (var entry in exported.Entries)
                {
                    var source = store.ResolveMedia(pack, entry);
                    if (source is null)
                    {
                        missing++;
                        continue;
                    }

                    var name = "media/" + entry.Media + entry.Extension;
                    if (!written.Add(name))
                        continue;

                    archive.CreateEntryFromFile(source, name, CompressionLevel.Optimal);
                }

                var manifest = archive.CreateEntry("pack.json", CompressionLevel.Optimal);
                using var manifestStream = manifest.Open();
                using var writer = new StreamWriter(manifestStream);
                writer.Write(JsonSerializer.Serialize(exported, JsonOptions));
            }

            pack.Version = exported.Version;
            pack.ArchiveHash = HashFile(destinationPath);
            store.Save(pack);

            var note = missing > 0 ? $" ({missing} entries had missing images and were skipped)" : string.Empty;
            return new PackTransferResult(
                true,
                $"Exported {written.Count} image(s){note}. Archive hash {pack.ArchiveHash[..12]}...",
                exported);
        }
        catch (Exception ex)
        {
            Services.Log.Error(ex, "Pack export failed");
            return new PackTransferResult(false, $"Export failed: {ex.Message}");
        }
    }

    public static PackTransferResult Import(string archivePath, PackStore store)
    {
        try
        {
            // Recorded so the copy held here can later be compared against whatever is advertised.
            var archiveHash = HashFile(archivePath);

            using var archive = ZipFile.OpenRead(archivePath);

            if (archive.Entries.Count > MaxEntries)
                return new PackTransferResult(false, $"Archive has too many entries ({archive.Entries.Count}).");

            var manifestEntry = archive.GetEntry("pack.json");
            if (manifestEntry is null)
                return new PackTransferResult(false, "Archive has no pack.json.");

            StickerPack? pack;
            using (var manifestStream = manifestEntry.Open())
            using (var reader = new StreamReader(manifestStream))
            {
                pack = JsonSerializer.Deserialize<StickerPack>(reader.ReadToEnd(), JsonOptions);
            }

            if (pack is null || string.IsNullOrWhiteSpace(pack.Id))
                return new PackTransferResult(false, "pack.json is not a valid sticker pack.");

            // Checked before anything is written, because the id names the folder every image below
            // lands in. An id like "..\..\..\evil" or "C:\Windows\Temp\evil" would otherwise place
            // attacker-chosen files anywhere on disk, and a later delete of that pack would take the
            // whole target directory with it.
            if (!PackStore.IsValidPackId(pack.Id))
            {
                return new PackTransferResult(false,
                    "That pack has a malformed id. It was not written by this plugin, and importing it " +
                    "would write files outside the sticker folder.");
            }

            // The owner is what pairs a pack to a speaker. An empty one matches every player, so a pack
            // without one would put its stickers over everybody's head.
            if (string.IsNullOrEmpty(pack.OwnerName))
            {
                return new PackTransferResult(false,
                    "That pack has no owner. A pack must record the character it was made for, or it " +
                    "would apply to everyone.");
            }

            // Replacing by id is how updates work, but it must never consume a pack this user authors:
            // their entries would be discarded, ownership reassigned, and PruneUnusedMedia would then
            // delete their artwork.
            var existing = store.Get(pack.Id);
            if (existing is not null && existing.IsLocal)
            {
                return new PackTransferResult(false,
                    $"That archive claims the same id as your own pack \"{existing.Name}\". Refusing to " +
                    "overwrite a pack you authored.");
            }

            if (pack.Entries.Count > StickerLimits.MaxEntriesPerPack)
            {
                return new PackTransferResult(false,
                    $"Pack declares {pack.Entries.Count} stickers; the limit is {StickerLimits.MaxEntriesPerPack}.");
            }

            var total = 0L;
            var imported = 0;
            var rejected = 0;

            foreach (var entry in archive.Entries)
            {
                if (entry.FullName.Equals("pack.json", StringComparison.OrdinalIgnoreCase))
                    continue;

                if (!IsSafeMediaEntry(entry.FullName))
                {
                    rejected++;
                    continue;
                }

                if (entry.Length > StickerLimits.MaxFileBytes)
                {
                    Services.Log.Warning(
                        $"Discarding {entry.FullName}: {entry.Length / 1024} KB exceeds the " +
                        $"{StickerLimits.MaxFileBytes / 1024} KB limit.");
                    rejected++;
                    continue;
                }

                total += entry.Length;
                if (total > MaxTotalBytes)
                    return new PackTransferResult(false, "Archive exceeds the total size limit.");

                using var entryStream = entry.Open();
                using var buffer = new MemoryStream();
                entryStream.CopyTo(buffer);
                var bytes = buffer.ToArray();

                // The filename claims a content hash; verify rather than trust it.
                var claimed = Path.GetFileNameWithoutExtension(entry.FullName);
                var actual = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

                if (!string.Equals(claimed, actual, StringComparison.OrdinalIgnoreCase))
                {
                    Services.Log.Warning($"Discarding {entry.FullName}: content does not match its hash.");
                    rejected++;
                    continue;
                }

                // Enforce the sticker rules on arrival too. The sender's copy of the plugin may be older,
                // patched, or simply lying about what it packed.
                var extension = Path.GetExtension(entry.FullName).ToLowerInvariant();
                var validation = StickerLimits.ValidateBytes(bytes, bytes.LongLength, extension);
                if (!validation.Ok)
                {
                    Services.Log.Warning($"Discarding {entry.FullName}: {validation.Message}");
                    rejected++;
                    continue;
                }

                store.StoreMedia(pack.Id, bytes, extension);
                imported++;
            }

            // An imported pack is somebody else's; it never becomes the local editable one, and its
            // stamped owner is taken as authoritative rather than being reassignable here.
            pack.IsLocal = false;
            pack.Enabled = true;
            pack.ArchiveHash = archiveHash;

            // Priority is never taken from the archive. It decides which pack wins when two bind the same
            // phrase, so a hostile int.MinValue would outrank everything the recipient owns.
            store.WithLock(() =>
            {
                // Re-read under the lock: the check above ran before the images were unpacked, and a
                // pack could have been created in between.
                var current = store.Get(pack.Id);

                if (current is not null)
                {
                    pack.Priority = current.Priority;

                    // Keep a URL the recipient already had if the archive does not carry one.
                    if (string.IsNullOrEmpty(pack.SourceUrl))
                        pack.SourceUrl = current.SourceUrl;
                }
                else
                {
                    // New arrivals sort behind everything already installed.
                    pack.Priority = store.NextPriority();
                }

                store.Save(pack);
            });

            var note = rejected > 0 ? $", {rejected} rejected" : string.Empty;
            return new PackTransferResult(
                true,
                $"Imported \"{pack.Name}\" — {pack.Entries.Count} binding(s), {imported} image(s){note}.",
                pack);
        }
        catch (InvalidDataException)
        {
            return new PackTransferResult(false, "That file is not a readable zip archive.");
        }
        catch (Exception ex)
        {
            Services.Log.Error(ex, "Pack import failed");
            return new PackTransferResult(false, $"Import failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Rejects anything that is not a plain image directly inside <c>media/</c>.
    /// </summary>
    /// <remarks>
    /// Guards against zip-slip: an entry named <c>media/../../foo</c> would otherwise escape the store.
    /// </remarks>
    private static bool IsSafeMediaEntry(string entryName)
    {
        if (!entryName.StartsWith("media/", StringComparison.OrdinalIgnoreCase))
            return false;

        if (entryName.Contains("..", StringComparison.Ordinal) ||
            entryName.Contains('\\', StringComparison.Ordinal))
        {
            return false;
        }

        var name = entryName["media/".Length..];
        if (name.Length == 0 || name.Contains('/', StringComparison.Ordinal))
            return false;

        return PackStore.IsSupportedImage(name);
    }

    /// <summary>SHA-256 of a file, lowercase hex.</summary>
    private static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static StickerPack Clone(StickerPack pack) => new()
    {
        Id = pack.Id,
        Name = pack.Name,
        Author = pack.Author,
        Version = pack.Version,
        Description = pack.Description,
        Enabled = pack.Enabled,
        Priority = pack.Priority,
        SourceUrl = pack.SourceUrl,
        ArchiveHash = pack.ArchiveHash,
        IsLocal = pack.IsLocal,
        OwnerName = pack.OwnerName,
        OwnerWorldId = pack.OwnerWorldId,
        OwnerWorldName = pack.OwnerWorldName,
        Entries = pack.Entries.ConvertAll(e => new PackEntry
        {
            Group = e.Group,
            Key = e.Key,
            Phrase = e.Phrase,
            Media = e.Media,
            Extension = e.Extension,
            Enabled = e.Enabled,
        }),
    };
}
