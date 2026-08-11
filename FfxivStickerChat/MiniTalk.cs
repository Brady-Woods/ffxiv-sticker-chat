using System;
using System.Collections.Generic;
using Dalamud.Game.Text.SeStringHandling;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace FfxivStickerChat;

/// <summary>
/// Locates chat-bubble nodes inside the game's bubble addons.
/// </summary>
/// <remarks>
/// <para>
/// Patch 7.3 split bubbles across two addons. <c>_MiniTalk</c> (leading underscore) still renders NPC
/// speech balloons and is mapped in FFXIVClientStructs as <c>AddonMiniTalk</c>. Player chat bubbles — the
/// ones this plugin cares about — render in <c>MiniTalkPlayer</c>, which has no struct at all.
/// </para>
/// <para>
/// Because no public node-ID map exists for either ULD, everything here discovers nodes by
/// <see cref="NodeType"/> rather than by ID. The one published ID data point for each addon disagrees
/// about what ID 4 means, so IDs are not portable between them. Node types are.
/// </para>
/// </remarks>
public static unsafe class MiniTalk
{
    /// <summary>Addon that renders player chat bubbles (patch 7.3+).</summary>
    public const string PlayerAddon = "MiniTalkPlayer";

    /// <summary>Addon that renders NPC speech balloons.</summary>
    public const string NpcAddon = "_MiniTalk";

    /// <summary>
    /// The nodes of one bubble.
    /// </summary>
    /// <remarks>
    /// A bubble contains more than one node of each interesting kind — the live dump shows two image
    /// nodes (a 14×24 at id 8 and a 32×32 at id 6) and two nine-grids (ids 9 and 11). Taking "the first
    /// one found" would pick arbitrarily, so every match is kept and the sticker target is chosen by
    /// explicit node id.
    /// </remarks>
    public sealed class BubbleNodes
    {
        public AtkComponentNode* Component;
        public AtkTextNode* Text;
        public AtkImageNode* Sticker;
        public readonly List<nint> Images = [];
        public readonly List<nint> NineGrids = [];

        public bool IsUsable => Component is not null && Sticker is not null;
    }

    /// <summary>A loaded addon, as seen by the addon scan.</summary>
    public readonly record struct LoadedAddon(string Name, nint Address, bool IsVisible, int NodeCount);

    /// <remarks>
    /// Uses the generic overload — the non-generic one returns an <c>AtkUnitBasePtr</c> wrapper rather
    /// than a raw pointer.
    /// </remarks>
    public static AtkUnitBase* GetAddon(string name)
        => Services.GameGui.GetAddonByName<AtkUnitBase>(name, 1);

    /// <summary>
    /// Scans every depth layer for loaded addons whose name contains <paramref name="filter"/>.
    /// </summary>
    /// <remarks>
    /// Looking an addon up by name only answers "is it loaded under the name I guessed". This answers the
    /// more useful question — what bubble-ish addons actually exist right now — which is how you catch a
    /// wrong addon name rather than silently finding nothing.
    /// </remarks>
    public static List<LoadedAddon> ScanAddons(string filter)
    {
        var results = new List<LoadedAddon>();

        var manager = RaptureAtkUnitManager.Instance();
        if (manager is null)
            return results;

        ref var unitManager = ref manager->AtkUnitManager;

        Span<AtkUnitList> layers =
        [
            unitManager.DepthLayerOneList, unitManager.DepthLayerTwoList,
            unitManager.DepthLayerThreeList, unitManager.DepthLayerFourList,
            unitManager.DepthLayerFiveList, unitManager.DepthLayerSixList,
            unitManager.DepthLayerSevenList, unitManager.DepthLayerEightList,
            unitManager.DepthLayerNineList, unitManager.DepthLayerTenList,
            unitManager.DepthLayerElevenList, unitManager.DepthLayerTwelveList,
        ];

        for (var l = 0; l < layers.Length; l++)
        {
            var list = layers[l];
            var entries = list.Entries;

            for (var i = 0; i < list.Count && i < entries.Length; i++)
            {
                var addon = entries[i].Value;
                if (addon is null)
                    continue;

                var name = addon->NameString;
                if (string.IsNullOrEmpty(name))
                    continue;

                if (filter.Length > 0 && !name.Contains(filter, StringComparison.OrdinalIgnoreCase))
                    continue;

                results.Add(new LoadedAddon(name, (nint)addon, addon->IsVisible, addon->UldManager.NodeListCount));
            }
        }

        return results;
    }

    /// <summary>
    /// Returns every component node in <paramref name="addon"/> — one per bubble slot.
    /// </summary>
    /// <remarks>
    /// Reads <c>UldManager.NodeList</c> rather than walking root siblings. An earlier version only
    /// followed <c>RootNode->ChildNode->PrevSiblingNode</c>, which finds nothing when the bubbles are
    /// nested deeper than one level — the node list is flat and covers the whole addon regardless of
    /// tree shape.
    /// </remarks>
    public static List<nint> EnumerateBubbleComponents(AtkUnitBase* addon)
    {
        var result = new List<nint>();
        if (addon is null)
            return result;

        foreach (var pointer in addon->UldManager.Nodes)
        {
            if (IsComponent(pointer.Value))
                result.Add((nint)pointer.Value);
        }

        return result;
    }

    /// <summary>
    /// Finds the text, image and nine-grid nodes inside one bubble component by walking its node list.
    /// </summary>
    /// <param name="stickerNodeId">
    /// Node id of the image node to use for the sticker. Falls back to the largest image node when no
    /// node carries that id, so a ULD change degrades to a guess rather than to nothing.
    /// </param>
    public static BubbleNodes ResolveNodes(AtkComponentNode* component, uint stickerNodeId)
    {
        var result = new BubbleNodes { Component = component };

        var componentBase = component is null ? null : component->Component;
        if (componentBase is null)
            return result;

        foreach (var pointer in componentBase->UldManager.Nodes)
        {
            var node = pointer.Value;
            if (node is null)
                continue;

            switch (node->Type)
            {
                case NodeType.Text:
                    if (result.Text is null)
                        result.Text = (AtkTextNode*)node;
                    break;

                case NodeType.Image:
                    result.Images.Add((nint)node);
                    if (node->NodeId == stickerNodeId)
                        result.Sticker = (AtkImageNode*)node;
                    break;

                case NodeType.NineGrid:
                    result.NineGrids.Add((nint)node);
                    break;
            }
        }

        if (result.Sticker is null && result.Images.Count > 0)
        {
            var best = (AtkImageNode*)result.Images[0];
            foreach (var candidate in result.Images)
            {
                var image = (AtkImageNode*)candidate;
                if (image->AtkResNode.Width * image->AtkResNode.Height >
                    best->AtkResNode.Width * best->AtkResNode.Height)
                {
                    best = image;
                }
            }

            result.Sticker = best;
        }

        return result;
    }

    /// <summary>
    /// Product of every ancestor's scale, so a size set on a node can be corrected to on-screen pixels.
    /// </summary>
    /// <remarks>
    /// Bubble nodes sit under parents scaled 0.5 and 3.0, so a node sized 512 does not render at 512.
    /// </remarks>
    public static (float X, float Y) CumulativeScale(AtkResNode* node, bool includeSelf = true)
    {
        var x = 1f;
        var y = 1f;

        var start = includeSelf ? node : node is null ? null : node->ParentNode;

        for (var current = start; current is not null; current = current->ParentNode)
        {
            x *= current->ScaleX;
            y *= current->ScaleY;
        }

        return (x == 0f ? 1f : x, y == 0f ? 1f : y);
    }

    /// <summary>
    /// True if <paramref name="node"/> is a component node.
    /// </summary>
    /// <remarks>
    /// Do not compare against <see cref="NodeType.Component"/>. That constant is 10000, which is what
    /// <c>AtkResNode.GetNodeType()</c> returns, but the raw <c>Type</c> field on a component node holds
    /// <c>1000 + ComponentType</c> instead — 1001 for a Button, and so on. Comparing the field to the
    /// constant is never true, which is exactly how this silently found zero bubbles.
    /// </remarks>
    public static bool IsComponent(AtkResNode* node)
        => node is not null && (ushort)node->Type >= 1000;

    /// <summary>Human-readable node type, expanding component nodes to their component kind.</summary>
    public static string DescribeType(AtkResNode* node)
    {
        if (node is null)
            return "<null>";

        var raw = (ushort)node->Type;
        if (raw < 1000)
            return node->Type.ToString();

        var componentType = (ComponentType)(raw - 1000);
        return $"Component:{componentType}({raw})";
    }

    /// <summary>Reads a text node's visible text, with SeString payloads decoded away.</summary>
    /// <remarks>
    /// <c>NodeText.ToString()</c> is not enough. A bubble showing an auto-translate phrase stores raw
    /// SeString bytes — <c>02 12 02 37 03 "Hello!" 02 12 02 38 03</c> — where <c>0x02…0x03</c> wraps a
    /// macro payload (here the auto-translate bracket icons). Treating that as UTF-8 yields control
    /// characters embedded in the string, which will never compare equal to a phrase from the chat event.
    /// Parsing and taking <c>TextValue</c> reduces both sides to the same plain text.
    /// </remarks>
    public static string ReadText(AtkTextNode* text)
    {
        if (text is null)
            return string.Empty;

        var span = text->NodeText.AsSpan();
        if (span.IsEmpty)
            return string.Empty;

        // 0x02 marks the start of a payload. Without one there is nothing to decode, so skip the
        // allocation — this runs for every visible bubble every frame.
        if (!span.Contains((byte)0x02))
            return text->NodeText.ToString();

        try
        {
            return SeString.Parse(span.ToArray()).TextValue;
        }
        catch (Exception ex)
        {
            Services.Log.Warning(ex, "Could not parse bubble text as SeString; falling back to raw");
            return text->NodeText.ToString();
        }
    }

    /// <summary>Describes the asset behind a texture — the fastest way to identify an unknown node.</summary>
    public static string DescribeTexture(AtkTexture* texture)
    {
        if (texture is null)
            return "<null>";

        switch (texture->TextureType)
        {
            case TextureType.Resource:
                var resource = texture->Resource;
                if (resource is null)
                    return "<resource null>";

                var handle = resource->TexFileResourceHandle;
                return handle is null ? "<handle null>" : handle->ResourceHandle.FileName.ToString();

            case TextureType.KernelTexture:
                return $"<kernel {(nint)texture->KernelTexture:X}>";

            default:
                return $"<none / {(byte)texture->TextureType}>";
        }
    }

    /// <summary>
    /// Computes the on-screen size for a sticker, fitted inside a square of
    /// <paramref name="maxEdge"/> with aspect ratio preserved.
    /// </summary>
    public static (ushort Width, ushort Height) FitToBox(ushort sourceWidth, ushort sourceHeight, float maxEdge)
    {
        if (sourceWidth == 0 || sourceHeight == 0 || maxEdge <= 0f)
            return (1, 1);

        var scale = Math.Min(maxEdge / sourceWidth, maxEdge / sourceHeight);
        var width = (ushort)Math.Clamp(MathF.Round(sourceWidth * scale), 1f, ushort.MaxValue);
        var height = (ushort)Math.Clamp(MathF.Round(sourceHeight * scale), 1f, ushort.MaxValue);
        return (width, height);
    }
}
