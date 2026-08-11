using System;
using FFXIVClientStructs.FFXIV.Client.Graphics.Kernel;
using FFXIVClientStructs.FFXIV.Client.System.Memory;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace FfxivStickerChat;

/// <summary>
/// A private <see cref="AtkUldPartsList"/> holding exactly one part bound to a sticker texture.
/// </summary>
/// <remarks>
/// <para>
/// This exists because every bubble in <c>MiniTalkPlayer</c> shares one parts list — the live dump shows
/// <c>partCount=35</c> against <c>ui/uld/MiniTalkPlayer_hr1.tex</c> on all 200 slots. Writing a texture
/// into any of those parts would change that element in every bubble at once, and the game frees those
/// assets in <c>AtkUldManager.Finalizer()</c>.
/// </para>
/// <para>
/// So instead of editing the game's parts, we allocate our own and swap a single pointer on the image
/// node (<c>PartsList</c>, plus <c>PartId</c> to 0). Restoring is putting the original pointer back. The
/// game's memory is never written to beyond those two fields.
/// </para>
/// <para>
/// Allocation goes through the game's UI heap rather than the managed one, because the renderer will
/// dereference these structures on its own thread.
/// </para>
/// </remarks>
public sealed unsafe class StickerParts : IDisposable
{
    private AtkUldPartsList* partsList;
    private AtkUldPart* part;
    private AtkUldAsset* asset;

    private StickerParts(AtkUldPartsList* partsList, AtkUldPart* part, AtkUldAsset* asset, ushort width, ushort height)
    {
        this.partsList = partsList;
        this.part = part;
        this.asset = asset;
        SourceWidth = width;
        SourceHeight = height;
    }

    /// <summary>The parts list to assign to an image node's <c>PartsList</c> field.</summary>
    public AtkUldPartsList* PartsList => partsList;

    /// <summary>Pixel dimensions of the source image, for aspect-preserving fit.</summary>
    public ushort SourceWidth { get; }

    /// <inheritdoc cref="SourceWidth"/>
    public ushort SourceHeight { get; }

    /// <summary>
    /// Allocates a one-part list pointing at <paramref name="texture"/>, or null if the UI heap refuses.
    /// </summary>
    public static StickerParts? Create(Texture* texture, ushort width, ushort height)
    {
        var uiSpace = IMemorySpace.GetUISpace();
        if (uiSpace is null || texture is null)
            return null;

        var partsList = uiSpace->Malloc<AtkUldPartsList>();
        var part = uiSpace->Malloc<AtkUldPart>();
        var asset = uiSpace->Malloc<AtkUldAsset>();

        if (partsList is null || part is null || asset is null)
        {
            // Malloc is all-or-nothing for our purposes; release whatever did come back.
            if (partsList is not null) IMemorySpace.Free(partsList);
            if (part is not null) IMemorySpace.Free(part);
            if (asset is not null) IMemorySpace.Free(asset);
            return null;
        }

        // Malloc does not zero, and these structs have fields the renderer reads that we never set.
        new Span<byte>(partsList, sizeof(AtkUldPartsList)).Clear();
        new Span<byte>(part, sizeof(AtkUldPart)).Clear();
        new Span<byte>(asset, sizeof(AtkUldAsset)).Clear();

        // A texture from ConvertToKernelTexture does not reliably carry its own dimensions, and the
        // renderer needs them to turn the part's pixel rect into UVs. Left at zero the sampling runs off
        // the top of the image, which shows up as a band of garbage along the sticker's top edge.
        if (texture->ActualWidth == 0 || texture->ActualHeight == 0)
        {
            texture->ActualWidth = width;
            texture->ActualHeight = height;
        }

        // AllocatedWidth can exceed ActualWidth when the surface is padded; sample the actual region.
        var sampleWidth = (ushort)Math.Clamp(texture->ActualWidth, 1u, ushort.MaxValue);
        var sampleHeight = (ushort)Math.Clamp(texture->ActualHeight, 1u, ushort.MaxValue);

        LogSafe(
            $"Sticker texture: actual {texture->ActualWidth}x{texture->ActualHeight}, " +
            $"allocated {texture->AllocatedWidth}x{texture->AllocatedHeight}, source {width}x{height}");

        asset->Id = 0;
        asset->AtkTexture.KernelTexture = texture;
        asset->AtkTexture.TextureType = TextureType.KernelTexture;

        part->UldAsset = asset;
        part->U = 0;
        part->V = 0;
        part->Width = sampleWidth;
        part->Height = sampleHeight;

        partsList->Id = 0;
        partsList->PartCount = 1;
        partsList->Parts = part;

        return new StickerParts(partsList, part, asset, sampleWidth, sampleHeight);
    }

    private static void LogSafe(string message)
    {
        try
        {
            Services.Log.Information(message);
        }
        catch
        {
            // Logging must never take down a texture bind.
        }
    }

    public void Dispose()
    {
        if (asset is not null)
        {
            // The texture belongs to StickerRegistry, not to us — detach without releasing it, or the
            // game's cleanup path would try to free a Dalamud-owned texture.
            asset->AtkTexture.KernelTexture = null;
            asset->AtkTexture.TextureType = 0;
            IMemorySpace.Free(asset);
            asset = null;
        }

        if (part is not null)
        {
            IMemorySpace.Free(part);
            part = null;
        }

        if (partsList is not null)
        {
            IMemorySpace.Free(partsList);
            partsList = null;
        }
    }
}
