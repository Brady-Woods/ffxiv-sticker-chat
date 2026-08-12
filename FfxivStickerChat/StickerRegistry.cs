using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Textures.TextureWraps;
using FFXIVClientStructs.FFXIV.Client.Graphics.Kernel;

namespace FfxivStickerChat;

/// <summary>
/// A loaded sticker: the native texture, its dimensions, and the region actually worth drawing.
/// </summary>
/// <param name="ContentX">Left edge of the opaque region within the image.</param>
/// <param name="ContentY">Top edge of the opaque region within the image.</param>
/// <param name="ContentWidth">Width of the opaque region.</param>
/// <param name="ContentHeight">Height of the opaque region.</param>
public readonly record struct StickerTexture(
    nint Pointer,
    ushort Width,
    ushort Height,
    ushort ContentX,
    ushort ContentY,
    ushort ContentWidth,
    ushort ContentHeight);

/// <summary>
/// Loads sticker images off disk and hands out native <see cref="Texture"/> pointers the game's renderer
/// can bind to an <c>AtkImageNode</c>.
/// </summary>
/// <remarks>
/// <para>
/// The conversion path is <c>ITextureProvider.GetFromFile → RentAsync → ConvertToKernelTexture</c>. The
/// returned pointer is only valid while the backing <see cref="IDalamudTextureWrap"/> is alive, so this
/// class owns both and releases them together.
/// </para>
/// <para>
/// Textures are cached against a byte budget and the least recently used are dropped once it is
/// exceeded. Since a shared pack library can hold far more art than belongs in VRAM at once, nothing is
/// loaded until a sticker actually needs it.
/// </para>
/// </remarks>
// Deliberately not `unsafe`: an unsafe context forbids `await`, and the async load below needs it. The
// native pointer is carried as an nint and only reinterpreted at the call site that binds it to a node.
public sealed class StickerRegistry : IDisposable
{
    private sealed class Entry
    {
        public required IDalamudTextureWrap Wrap { get; init; }
        public required StickerTexture Texture { get; init; }
        public required long Bytes { get; init; }
        public long LastUsedTicks;
    }

    private readonly Configuration config;
    private readonly ConcurrentDictionary<string, Entry> entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> inFlight = new(StringComparer.OrdinalIgnoreCase);

    private long tick;
    private bool disposed;

    public StickerRegistry(Configuration config)
    {
        this.config = config;
    }

    /// <summary>Prefix marking a cache key as a game icon rather than a file on disk.</summary>
    private const string GameIconPrefix = "icon:";

    /// <summary>Builds the cache key for a game icon.</summary>
    public static string GameIconKey(uint iconId) => GameIconPrefix + iconId.ToString(CultureInfo.InvariantCulture);

    /// <summary>Approximate decoded size of everything currently cached.</summary>
    public long TotalBytes => entries.Values.Sum(e => e.Bytes);

    public int Count => entries.Count;

    /// <summary>
    /// Resolves <paramref name="path"/> to a native texture. Returns <see langword="false"/> if it is not
    /// resident yet — the first call starts an async load, so call again on a later frame.
    /// </summary>
    public bool TryGet(string path, out StickerTexture sticker)
    {
        sticker = default;

        if (disposed || string.IsNullOrWhiteSpace(path))
            return false;

        if (entries.TryGetValue(path, out var entry))
        {
            // Cheap monotonic counter rather than a clock: only the ordering matters.
            entry.LastUsedTicks = Interlocked.Increment(ref tick);
            sticker = entry.Texture;
            return true;
        }

        BeginLoad(path);
        return false;
    }

    /// <summary>Drops a cached texture so an edited file is picked up on next use.</summary>
    public void Invalidate(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;

        inFlight.TryRemove(path, out _);

        if (entries.TryRemove(path, out var entry))
            entry.Wrap.Dispose();
    }

    /// <summary>
    /// Returns paths that should be dropped to get back under budget, least recently used first.
    /// </summary>
    /// <param name="pinned">
    /// Paths currently bound into a visible bubble. Releasing one of these would leave the renderer
    /// holding a freed texture, so they are never offered even when over budget.
    /// </param>
    public IReadOnlyList<string> GetEvictionCandidates(IReadOnlySet<string> pinned)
    {
        var budget = Math.Max(16, config.TextureCacheBudgetMb) * 1024L * 1024L;
        var total = TotalBytes;

        if (total <= budget)
            return [];

        var candidates = new List<string>();

        foreach (var pair in entries.OrderBy(e => e.Value.LastUsedTicks))
        {
            if (total <= budget)
                break;

            if (pinned.Contains(pair.Key))
                continue;

            candidates.Add(pair.Key);
            total -= pair.Value.Bytes;
        }

        return candidates;
    }

    /// <summary>Frees a cached texture. The caller must already have unbound it from every node.</summary>
    public void Release(string path)
    {
        if (entries.TryRemove(path, out var entry))
        {
            entry.Wrap.Dispose();

            if (config.VerboseLogging)
                Services.Log.Information($"Evicted texture {Path.GetFileName(path)} ({entry.Bytes / 1024} KB)");
        }
    }

    private void BeginLoad(string path)
    {
        if (disposed || entries.ContainsKey(path))
            return;

        // Claim the slot so concurrent frames don't queue the same file repeatedly.
        if (!inFlight.TryAdd(path, 0))
            return;

        var isGameIcon = path.StartsWith(GameIconPrefix, StringComparison.Ordinal);

        if (!isGameIcon && !File.Exists(path))
        {
            Services.Log.Warning($"Sticker image not found: {path}");
            inFlight.TryRemove(path, out _);
            return;
        }

        Task.Run(async () =>
        {
            try
            {
                // A game icon comes from the player's own installation, so it loads through the same
                // path as a file but without anything being copied or shipped.
                var shared = isGameIcon
                    ? Services.TextureProvider.GetFromGameIcon(
                        new GameIconLookup(uint.Parse(path[GameIconPrefix.Length..], CultureInfo.InvariantCulture)))
                    : Services.TextureProvider.GetFromFile(path);

                var wrap = await shared.RentAsync().ConfigureAwait(false);

                // Artwork is rarely centred inside its own canvas — one of these had 74 transparent
                // pixels at the top and none at the bottom, so centring the canvas put the picture low.
                // Measuring the opaque region lets the sticker be positioned by what you can actually see.
                var content = await MeasureContentAsync(wrap, path).ConfigureAwait(false);

                // ConvertToKernelTexture and the dictionary write both happen on the framework thread so we
                // never hand a half-published pointer to the renderer.
                await Services.Framework.Run(() =>
                {
                    if (disposed)
                    {
                        wrap.Dispose();
                        return;
                    }

                    var texture = Services.TextureProvider.ConvertToKernelTexture(wrap, leaveWrapOpen: true);
                    if (texture == nint.Zero)
                    {
                        Services.Log.Error($"ConvertToKernelTexture returned null for {path}");
                        wrap.Dispose();
                        return;
                    }

                    entries[path] = new Entry
                    {
                        Wrap = wrap,
                        Texture = new StickerTexture(
                            texture,
                            (ushort)wrap.Width,
                            (ushort)wrap.Height,
                            content.X,
                            content.Y,
                            content.Width,
                            content.Height),
                        // Four bytes per pixel is the decoded cost; the file size on disk is irrelevant.
                        Bytes = (long)wrap.Width * wrap.Height * 4,
                        LastUsedTicks = Interlocked.Increment(ref tick),
                    };

                    Services.Log.Information(
                        $"Sticker ready: {(isGameIcon ? path : Path.GetFileName(path))} " +
                        $"({wrap.Width}x{wrap.Height})");
                }).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Services.Log.Error(ex, $"Failed to load sticker {path}");
            }
            finally
            {
                inFlight.TryRemove(path, out _);
            }
        });
    }

    /// <summary>
    /// Finds the bounding box of non-transparent pixels, falling back to the whole image.
    /// </summary>
    /// <remarks>
    /// Readback can fail or return an unexpected format; a wrong crop would be far worse than no crop,
    /// so anything unexpected falls back to the full canvas.
    /// </remarks>
    private static async Task<(ushort X, ushort Y, ushort Width, ushort Height)> MeasureContentAsync(
        IDalamudTextureWrap wrap,
        string path)
    {
        var full = ((ushort)0, (ushort)0, (ushort)wrap.Width, (ushort)wrap.Height);

        try
        {
            var (spec, data) = await Services.TextureReadback
                .GetRawImageAsync(wrap, leaveWrapOpen: true)
                .ConfigureAwait(false);

            // Both BGRA and RGBA put alpha in the fourth byte, which is all this needs.
            var bytesPerPixel = spec.Pitch / Math.Max(1, spec.Width);
            if (bytesPerPixel < 4)
                return full;

            int minX = spec.Width, minY = spec.Height, maxX = -1, maxY = -1;

            for (var y = 0; y < spec.Height; y++)
            {
                var row = y * spec.Pitch;
                if (row + (spec.Width * bytesPerPixel) > data.Length)
                    break;

                for (var x = 0; x < spec.Width; x++)
                {
                    if (data[row + (x * bytesPerPixel) + 3] == 0)
                        continue;

                    if (x < minX) minX = x;
                    if (x > maxX) maxX = x;
                    if (y < minY) minY = y;
                    if (y > maxY) maxY = y;
                }
            }

            if (maxX < 0 || maxY < 0)
                return full; // fully transparent

            var result = ((ushort)minX, (ushort)minY, (ushort)(maxX - minX + 1), (ushort)(maxY - minY + 1));

            if (result.Item3 != wrap.Width || result.Item4 != wrap.Height)
            {
                Services.Log.Information(
                    $"Trimmed {Path.GetFileName(path)} to its content: " +
                    $"{wrap.Width}x{wrap.Height} -> {result.Item3}x{result.Item4} at ({result.Item1},{result.Item2})");
            }

            return result;
        }
        catch (Exception ex)
        {
            Services.Log.Warning(ex, $"Could not measure content bounds for {path}; using the full image");
            return full;
        }
    }

    public void Dispose()
    {
        if (disposed)
            return;

        disposed = true;

        foreach (var entry in entries.Values)
            entry.Wrap.Dispose();

        entries.Clear();
    }
}
