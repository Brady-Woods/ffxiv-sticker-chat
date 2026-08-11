using System;
using System.Buffers.Binary;
using System.IO;

namespace FfxivStickerChat.Packs;

/// <summary>Why an image was rejected, or <see cref="Ok"/>.</summary>
public sealed record StickerValidation(bool Ok, string Message, int Width = 0, int Height = 0)
{
    public static StickerValidation Pass(int width, int height) => new(true, "ok", width, height);

    public static StickerValidation Fail(string message) => new(false, message);
}

/// <summary>
/// Size, format and count rules for sticker packs, following Telegram's static sticker spec.
/// </summary>
/// <remarks>
/// <para>
/// Adopting an established spec means art made for Telegram works here unmodified, and it puts a
/// predictable ceiling on a pack: 120 × 512 KB is about 60 MB worst case, which is what makes per-pack
/// folders affordable.
/// </para>
/// <para>
/// PNG only. Telegram also allows WebP, but whether Dalamud's texture loader decodes WebP under Proton
/// could not be verified, and accepting a format that validates and then silently fails to render is
/// worse than not accepting it.
/// </para>
/// </remarks>
public static class StickerLimits
{
    /// <summary>One side must be exactly this many pixels.</summary>
    public const int RequiredEdge = 512;

    /// <summary>Largest accepted file, matching Telegram's static sticker limit.</summary>
    public const long MaxFileBytes = 512 * 1024;

    /// <summary>Most stickers allowed in one pack.</summary>
    public const int MaxEntriesPerPack = 120;

    public static bool IsAllowedExtension(string path)
        => Path.GetExtension(path).Equals(".png", StringComparison.OrdinalIgnoreCase);

    /// <summary>Checks a file against every rule without decoding it.</summary>
    public static StickerValidation Validate(string path)
    {
        if (!File.Exists(path))
            return StickerValidation.Fail("File not found.");

        if (!IsAllowedExtension(path))
            return StickerValidation.Fail("Stickers must be .png.");

        var length = new FileInfo(path).Length;
        if (length > MaxFileBytes)
            return StickerValidation.Fail($"File is {length / 1024} KB; the limit is {MaxFileBytes / 1024} KB.");

        byte[] header;
        try
        {
            header = ReadHeader(path);
        }
        catch (Exception ex)
        {
            return StickerValidation.Fail($"Could not read the file: {ex.Message}");
        }

        return ValidateBytes(header, length, Path.GetExtension(path));
    }

    /// <summary>Checks already-loaded bytes, for content arriving from an archive.</summary>
    public static StickerValidation ValidateBytes(ReadOnlySpan<byte> bytes, long totalLength, string extension)
    {
        if (totalLength > MaxFileBytes)
            return StickerValidation.Fail($"File is {totalLength / 1024} KB; the limit is {MaxFileBytes / 1024} KB.");

        if (!TryReadDimensions(bytes, out var width, out var height))
            return StickerValidation.Fail("Not a readable PNG image.");

        return ValidateDimensions(width, height);
    }

    /// <summary>
    /// Enforces the shape rule: exactly 512 on one side, no more than 512 on the other.
    /// </summary>
    public static StickerValidation ValidateDimensions(int width, int height)
    {
        if (width <= 0 || height <= 0)
            return StickerValidation.Fail("Image has no size.");

        var hasRequiredEdge = width == RequiredEdge || height == RequiredEdge;
        var withinBounds = width <= RequiredEdge && height <= RequiredEdge;

        if (!hasRequiredEdge || !withinBounds)
        {
            return StickerValidation.Fail(
                $"Image is {width}x{height}. One side must be exactly {RequiredEdge} and the other " +
                $"{RequiredEdge} or less.");
        }

        return StickerValidation.Pass(width, height);
    }

    private static byte[] ReadHeader(string path)
    {
        using var stream = File.OpenRead(path);

        // 24 bytes covers the PNG signature and IHDR; read a little more for safety.
        var buffer = new byte[64];
        var read = stream.ReadAtLeast(buffer, Math.Min(buffer.Length, (int)stream.Length), throwOnEndOfStream: false);
        return buffer[..read];
    }

    /// <summary>
    /// Pulls width and height straight out of the PNG header.
    /// </summary>
    /// <remarks>
    /// Parsing the header avoids decoding the whole image just to reject it, and works the same for a
    /// file on disk as for bytes pulled from an archive.
    /// </remarks>
    public static bool TryReadDimensions(ReadOnlySpan<byte> data, out int width, out int height)
    {
        width = 0;
        height = 0;

        ReadOnlySpan<byte> signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

        if (data.Length < 24 || !data[..8].SequenceEqual(signature))
            return false;

        if (!data.Slice(12, 4).SequenceEqual("IHDR"u8))
            return false;

        // IHDR stores width then height as big-endian 32-bit values.
        width = BinaryPrimitives.ReadInt32BigEndian(data.Slice(16, 4));
        height = BinaryPrimitives.ReadInt32BigEndian(data.Slice(20, 4));
        return width > 0 && height > 0;
    }
}
