using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;

namespace FfxivStickerChat.Packs;

/// <summary>
/// On-disk home for sticker packs.
/// </summary>
/// <remarks>
/// <para>Layout, under the plugin's config directory:</para>
/// <code>
/// packs/&lt;packId&gt;/pack.json
/// packs/&lt;packId&gt;/media/&lt;sha256&gt;.png
/// </code>
/// <para>
/// Each pack owns its media, so removing one is a single directory delete with nothing left behind and
/// no reference counting to get wrong. Sharing images across packs would save space, but with a pack
/// capped at 120 stickers of 512 KB the worst case is about 60 MB — not worth trading clean removal for.
/// </para>
/// <para>
/// Media is still named by content hash within a pack, so re-adding the same image twice stores it once
/// and the texture cache keys on a stable path.
/// </para>
/// </remarks>
public sealed class PackStore
{
    private const string ManifestName = "pack.json";
    private const string MediaFolder = "media";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    private readonly Dictionary<string, StickerPack> packs = new(StringComparer.Ordinal);

    /// <summary>Priority-ordered view, rebuilt only when the set of packs changes.</summary>
    private List<StickerPack>? ordered;

    public PackStore(string rootDirectory)
    {
        RootDirectory = rootDirectory;
        PacksDirectory = Path.Combine(rootDirectory, "packs");
    }

    public string RootDirectory { get; }

    public string PacksDirectory { get; }

    /// <summary>All loaded packs, in resolution order.</summary>
    public IReadOnlyList<StickerPack> Packs => ordered ??= BuildOrdered();

    public static bool IsSupportedImage(string path) => StickerLimits.IsAllowedExtension(path);

    public void EnsureDirectories() => Directory.CreateDirectory(PacksDirectory);

    public string GetPackDirectory(string packId) => Path.Combine(PacksDirectory, packId);

    public string GetMediaDirectory(string packId) => Path.Combine(GetPackDirectory(packId), MediaFolder);

    public string GetMediaPath(string packId, string hash, string extension)
        => Path.Combine(GetMediaDirectory(packId), hash + extension);

    /// <summary>
    /// Finds the image for a phrase sent by <paramref name="sender"/>, or null if nothing binds it.
    /// </summary>
    /// <remarks>
    /// Runs once per bubble, so it walks the cached ordering rather than re-sorting, and touches no
    /// image data — only the manifests already in memory.
    /// </remarks>
    public string? Resolve(uint group, uint key, string phrase, string sender, ushort worldId)
    {
        foreach (var pack in Packs)
        {
            if (!pack.Enabled || !pack.AppliesToSender(sender, worldId))
                continue;

            var entry = pack.Find(group, key, phrase);
            if (entry is null)
                continue;

            // Self-heal: a binding made from the dropdown carries the sheet's key, which the game does
            // not use. Adopt the observed values so it matches on id from now on.
            if (entry.Group != group || entry.Key != key)
            {
                Services.Log.Information(
                    $"Corrected binding \"{entry.Phrase}\": ({entry.Group},{entry.Key}) -> ({group},{key})");

                entry.Group = group;
                entry.Key = key;
                Save(pack);
            }

            var path = ResolveMedia(pack, entry);
            if (path is not null)
                return path;
        }

        return null;
    }

    public void LoadAll()
    {
        EnsureDirectories();
        MigrateFlatLayout();

        packs.Clear();
        ordered = null;

        foreach (var directory in Directory.EnumerateDirectories(PacksDirectory))
        {
            var manifest = Path.Combine(directory, ManifestName);
            if (!File.Exists(manifest))
                continue;

            try
            {
                var pack = JsonSerializer.Deserialize<StickerPack>(File.ReadAllText(manifest), JsonOptions);

                if (pack is null || string.IsNullOrWhiteSpace(pack.Id))
                {
                    Services.Log.Warning($"Skipping unreadable pack manifest: {manifest}");
                    continue;
                }

                packs[pack.Id] = pack;
            }
            catch (Exception ex)
            {
                Services.Log.Error(ex, $"Failed to load pack manifest {manifest}");
            }
        }

        Services.Log.Information($"Loaded {packs.Count} sticker pack(s).");
    }

    public StickerPack? Get(string packId) => packs.GetValueOrDefault(packId);

    /// <summary>Packs authored on this client, in resolution order.</summary>
    public IEnumerable<StickerPack> LocalPacks => Packs.Where(p => p.IsLocal);

    /// <summary>
    /// The pack new bindings land in by default, created on first use.
    /// </summary>
    /// <remarks>
    /// With several authored packs the first by priority wins, so "bind this phrase" always has an
    /// unambiguous destination without asking.
    /// </remarks>
    public StickerPack GetOrCreateLocal()
        => LocalPacks.FirstOrDefault() ?? CreateLocal("My stickers");

    /// <summary>Creates a new pack authored by this character.</summary>
    public StickerPack CreateLocal(string name)
    {
        var pack = new StickerPack
        {
            Name = string.IsNullOrWhiteSpace(name) ? "New pack" : name.Trim(),
            IsLocal = true,

            // Sits after everything that already exists, so creating a pack never silently takes over
            // a phrase an older one already binds.
            Priority = packs.Count == 0 ? 0 : packs.Values.Max(p => p.Priority) + 1,
        };

        StampOwner(pack);
        Save(pack);
        return pack;
    }

    /// <summary>
    /// Writes the logged-in character onto a pack, if it has no owner yet.
    /// </summary>
    /// <remarks>
    /// Called lazily rather than once at load: the local player is not available while still at the
    /// title screen, so a pack created then would otherwise be stamped blank forever.
    /// </remarks>
    public bool StampOwner(StickerPack pack)
    {
        if (!pack.IsLocal || !string.IsNullOrEmpty(pack.OwnerName))
            return false;

        var name = Services.PlayerState.CharacterName;
        if (string.IsNullOrEmpty(name))
            return false;

        pack.OwnerName = name;
        pack.OwnerWorldId = (ushort)Services.PlayerState.HomeWorld.RowId;
        pack.OwnerWorldName = Services.PlayerState.HomeWorld.ValueNullable?.Name.ExtractText() ?? string.Empty;

        Services.Log.Information($"Stamped local pack as {pack.OwnerDisplay}.");
        return true;
    }

    public void Save(StickerPack pack)
    {
        packs[pack.Id] = pack;
        ordered = null;

        var directory = GetPackDirectory(pack.Id);

        try
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, ManifestName), JsonSerializer.Serialize(pack, JsonOptions));
        }
        catch (Exception ex)
        {
            Services.Log.Error(ex, $"Failed to save pack {pack.Name}");
        }
    }

    /// <summary>Removes a pack and everything it owns.</summary>
    public bool Delete(string packId)
    {
        packs.Remove(packId);
        ordered = null;

        var directory = GetPackDirectory(packId);

        try
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);

            return true;
        }
        catch (Exception ex)
        {
            Services.Log.Error(ex, $"Failed to delete pack {packId}");
            return false;
        }
    }

    /// <summary>Total bytes a pack occupies on disk.</summary>
    public long GetPackSize(string packId)
    {
        var directory = GetPackDirectory(packId);
        if (!Directory.Exists(directory))
            return 0;

        try
        {
            return Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
                .Sum(f => new FileInfo(f).Length);
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>
    /// Validates an image and copies it into a pack, returning its content hash.
    /// </summary>
    public (string Hash, string Extension)? ImportMedia(StickerPack pack, string sourcePath, out string error)
    {
        var validation = StickerLimits.Validate(sourcePath);
        if (!validation.Ok)
        {
            error = validation.Message;
            return null;
        }

        try
        {
            var bytes = File.ReadAllBytes(sourcePath);
            var extension = Path.GetExtension(sourcePath).ToLowerInvariant();
            error = string.Empty;
            return (StoreMedia(pack.Id, bytes, extension), extension);
        }
        catch (Exception ex)
        {
            Services.Log.Error(ex, $"Failed to import image {sourcePath}");
            error = ex.Message;
            return null;
        }
    }

    /// <summary>Writes bytes into a pack's media folder under their hash, and returns that hash.</summary>
    public string StoreMedia(string packId, byte[] bytes, string extension)
    {
        var directory = GetMediaDirectory(packId);
        Directory.CreateDirectory(directory);

        var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var destination = Path.Combine(directory, hash + extension);

        // Content-addressed within the pack: same bytes, same name, so an existing file is correct.
        if (!File.Exists(destination))
            File.WriteAllBytes(destination, bytes);

        return hash;
    }

    /// <summary>
    /// Resolves an entry to something <see cref="StickerRegistry"/> can load.
    /// </summary>
    /// <returns>
    /// An absolute file path, or a <c>icon:&lt;id&gt;</c> key for artwork that lives in the game files.
    /// Null when the entry has no usable source.
    /// </returns>
    public string? ResolveMedia(StickerPack pack, PackEntry entry)
    {
        if (entry.GameIconId != 0)
            return StickerRegistry.GameIconKey(entry.GameIconId);

        if (string.IsNullOrEmpty(entry.Media))
            return null;

        var path = GetMediaPath(pack.Id, entry.Media, entry.Extension);
        return File.Exists(path) ? path : null;
    }

    /// <summary>Deletes media inside a pack that no entry references.</summary>
    public int PruneUnusedMedia(StickerPack pack)
    {
        var directory = GetMediaDirectory(pack.Id);
        if (!Directory.Exists(directory))
            return 0;

        var referenced = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in pack.Entries)
        {
            if (!string.IsNullOrEmpty(entry.Media))
                referenced.Add(entry.Media + entry.Extension);
        }

        var removed = 0;

        foreach (var file in Directory.EnumerateFiles(directory))
        {
            if (referenced.Contains(Path.GetFileName(file)))
                continue;

            try
            {
                File.Delete(file);
                removed++;
            }
            catch (Exception ex)
            {
                Services.Log.Warning(ex, $"Could not prune {file}");
            }
        }

        return removed;
    }

    private List<StickerPack> BuildOrdered() =>
        packs.Values
            .OrderBy(p => p.Priority)
            .ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>
    /// Moves packs written by the earlier flat layout into per-pack folders.
    /// </summary>
    /// <remarks>
    /// The first version stored <c>packs/&lt;id&gt;.json</c> beside a single shared <c>media/</c>. Only
    /// media a pack actually references is carried across; the old shared folder is left in place rather
    /// than deleted, since being cautious costs a few megabytes and being wrong loses artwork.
    /// </remarks>
    private void MigrateFlatLayout()
    {
        string[] legacyManifests;

        try
        {
            legacyManifests = Directory.GetFiles(PacksDirectory, "*.json");
        }
        catch
        {
            return;
        }

        if (legacyManifests.Length == 0)
            return;

        var sharedMedia = Path.Combine(RootDirectory, "media");

        foreach (var manifest in legacyManifests)
        {
            try
            {
                var pack = JsonSerializer.Deserialize<StickerPack>(File.ReadAllText(manifest), JsonOptions);
                if (pack is null || string.IsNullOrWhiteSpace(pack.Id))
                    continue;

                var mediaDirectory = GetMediaDirectory(pack.Id);
                Directory.CreateDirectory(mediaDirectory);

                foreach (var entry in pack.Entries)
                {
                    if (string.IsNullOrEmpty(entry.Media))
                        continue;

                    var source = Path.Combine(sharedMedia, entry.Media + entry.Extension);
                    var destination = Path.Combine(mediaDirectory, entry.Media + entry.Extension);

                    if (File.Exists(source) && !File.Exists(destination))
                        File.Copy(source, destination);
                }

                File.WriteAllText(Path.Combine(GetPackDirectory(pack.Id), ManifestName),
                    JsonSerializer.Serialize(pack, JsonOptions));

                File.Delete(manifest);
                Services.Log.Information($"Migrated pack \"{pack.Name}\" into its own folder.");
            }
            catch (Exception ex)
            {
                Services.Log.Error(ex, $"Failed to migrate {manifest}");
            }
        }
    }
}
