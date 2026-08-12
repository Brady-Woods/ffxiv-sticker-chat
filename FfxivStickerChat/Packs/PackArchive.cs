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

            using var stream = File.Create(destinationPath);
            using var archive = new ZipArchive(stream, ZipArchiveMode.Create);

            // Export a copy so bumping the version does not mutate the live pack.
            var exported = Clone(pack);
            exported.Version = pack.Version + 1;

            // Ownership travels with the pack: that is what pairs it to its author on the far side.
            // IsLocal does not, since the recipient does not author it.
            exported.IsLocal = false;

            var written = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var missing = 0;

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
            using (var manifestStream = manifest.Open())
            using (var writer = new StreamWriter(manifestStream))
            {
                writer.Write(JsonSerializer.Serialize(exported, JsonOptions));
            }

            pack.Version = exported.Version;
            store.Save(pack);

            var note = missing > 0 ? $" ({missing} entries had missing images and were skipped)" : string.Empty;
            return new PackTransferResult(true, $"Exported {written.Count} image(s){note}.", exported);
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

            var existing = store.Get(pack.Id);
            if (existing is not null)
            {
                pack.Priority = existing.Priority;

                // Keep a URL the recipient already had if the archive does not carry one.
                if (string.IsNullOrEmpty(pack.SourceUrl))
                    pack.SourceUrl = existing.SourceUrl;
            }

            store.Save(pack);

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
