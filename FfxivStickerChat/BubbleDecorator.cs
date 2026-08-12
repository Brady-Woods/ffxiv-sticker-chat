using System;
using System.Collections.Generic;
using FFXIVClientStructs.FFXIV.Client.Graphics.Kernel;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace FfxivStickerChat;

/// <summary>
/// Replaces the contents of native chat bubbles with sticker images.
/// </summary>
/// <remarks>
/// <para>
/// <b>Approach.</b> Every bubble already contains image nodes, so rather than allocating and parenting a
/// node of our own we retarget one of them. Crucially we do <i>not</i> write into the game's parts: all
/// 200 bubble slots share a single 35-part list backed by <c>MiniTalkPlayer_hr1.tex</c>, so editing a
/// part would change that element in every bubble and hand the game's finaliser a texture it does not
/// own. Instead each sticker gets a private one-part list (see <see cref="StickerParts"/>) and we swap
/// two fields on the node — <c>PartsList</c> and <c>PartId</c> — restoring both afterwards.
/// </para>
/// <para>
/// <b>Matching.</b> Bubbles are matched by rendered text, not by actor. The phrase↔sticker mapping is
/// 1:1, so the text alone identifies the sticker, which sidesteps mapping a bubble back to a GameObject.
/// If per-actor behaviour is ever needed, the documented route is hooking
/// <c>RaptureLogModule.ShowMiniTalkPlayer</c>, which supplies sender, message and world.
/// </para>
/// </remarks>
public sealed unsafe class BubbleDecorator : IDisposable
{
    /// <summary>
    /// Neutral value for a node's multiplicative colour fields, which are percentages, not bytes.
    /// </summary>
    private const byte NeutralMultiply = 100;

    /// <summary>Image node wrap mode that maps the part onto the node's rect. None=0, Tile=1, Stretch=2.</summary>
    private const byte WrapModeStretch = 2;

    /// <summary>How long a detected phrase stays eligible to claim a bubble.</summary>
    private static readonly TimeSpan PendingLifetime = TimeSpan.FromSeconds(15);

    private readonly Configuration config;
    private readonly StickerRegistry registry;
    private readonly BubbleHook hook;

    /// <summary>Components that were visible last frame, to detect a bubble opening.</summary>
    private readonly HashSet<nint> visibleLastFrame = [];
    private readonly HashSet<nint> visibleThisFrame = [];

    /// <summary>Saved state per bubble component. Components are recycled, so the pointer is the key.</summary>
    private readonly Dictionary<nint, SlotState> states = [];

    /// <summary>One private parts list per image, built lazily and reused across bubbles.</summary>
    private readonly Dictionary<string, StickerParts> partsByPath = new(StringComparer.OrdinalIgnoreCase);

    private int addonsSeen;
    private int componentsSeen;
    private int visibleBubbles;
    private int textsSeen;
    private int newlyVisibleThisFrame;
    private DateTime lastStatusLog = DateTime.MinValue;
    private bool disposed;

    public BubbleDecorator(Configuration config, StickerRegistry registry, BubbleHook hook)
    {
        this.config = config;
        this.registry = registry;
        this.hook = hook;
    }

    /// <summary>
    /// One-line summary of what the last frame actually saw. Surfaced in the config window so a silent
    /// failure reads as "addons: 0" or "components: 0" instead of just nothing happening.
    /// </summary>
    public string LastStatus { get; private set; } = "not running yet";

    private sealed class SlotState
    {
        public string Phrase = string.Empty;
        public string ImagePath = string.Empty;
        public StickerParts? Parts;

        public bool LoggedFrames;
        public float OriginalScaleX;
        public float OriginalScaleY;
        public float OriginalOriginX;
        public float OriginalOriginY;
        public byte OriginalWrapMode;

        public uint OriginalColor;
        public short OriginalAddRed, OriginalAddGreen, OriginalAddBlue;
        public short OriginalAddRed2, OriginalAddGreen2, OriginalAddBlue2;
        public byte OriginalMultiplyRed, OriginalMultiplyGreen, OriginalMultiplyBlue;
        public byte OriginalMultiplyRed2, OriginalMultiplyGreen2, OriginalMultiplyBlue2;

        public AtkImageNode* Node;
        public AtkUldPartsList* OriginalPartsList;
        public ushort OriginalPartId;
        public ushort OriginalWidth;
        public ushort OriginalHeight;
        public float OriginalX;
        public float OriginalY;
        public bool OriginalVisible;

        public readonly List<(nint Node, bool Visible)> Hidden = [];
        public readonly List<(nint Node, float X, float Y, ushort Width, ushort Height)> Resized = [];

        public bool LoggedTint;

        public readonly List<(nint Node, uint Color, short AddR, short AddG, short AddB,
            byte MulR, byte MulG, byte MulB)> TintedAncestors = [];
    }

    public void OnFrameworkUpdate()
    {
        if (disposed)
            return;

        TrimTextureCache();
    }

    /// <summary>
    /// Drops cold textures once the cache is over budget.
    /// </summary>
    /// <remarks>
    /// A texture bound into a visible bubble must never be freed — the renderer would be left holding a
    /// dangling pointer. Anything a live sticker references is pinned, and its private parts list is torn
    /// down before the texture it points at is released.
    /// </remarks>
    private void TrimTextureCache()
    {
        var pinned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var state in states.Values)
        {
            if (!string.IsNullOrEmpty(state.ImagePath))
                pinned.Add(state.ImagePath);
        }

        foreach (var path in registry.GetEvictionCandidates(pinned))
        {
            // Unbind before freeing: the parts list holds the texture pointer the node reads.
            if (partsByPath.Remove(path, out var parts))
                parts.Dispose();

            registry.Release(path);
        }
    }

    /// <summary>
    /// Does all node work, driven by the addon's own draw cycle.
    /// </summary>
    /// <remarks>
    /// This must not run from <c>Framework.Update</c>. That fires before the game lays the addon out, so
    /// any geometry written there is overwritten before it reaches the screen — which is exactly why the
    /// size setting appeared to do nothing while the position (written once, never recomputed by the
    /// game) survived.
    /// </remarks>
    public void OnAddonPostDraw(string addonName, AtkUnitBase* addon)
    {
        if (disposed || addon is null)
            return;

        if (addonName == MiniTalk.NpcAddon && !config.IncludeNpcBubbles)
            return;

        addonsSeen = 0;
        componentsSeen = 0;
        visibleBubbles = 0;
        textsSeen = 0;
        newlyVisibleThisFrame = 0;
        visibleThisFrame.Clear();

        ProcessAddon(addon);

        visibleLastFrame.Clear();
        foreach (var pointer in visibleThisFrame)
            visibleLastFrame.Add(pointer);

        LastStatus =
            $"addons={addonsSeen} components={componentsSeen} visible={visibleBubbles} " +
            $"withText={textsSeen} hook={(hook.IsActive ? "on" : "OFF")} " +
            $"pending={hook.PendingCount} applied={states.Count}";

        // Throttled, because PostDraw runs every frame. Without this the decorator side is invisible
        // unless the config window happens to be open.
        if (config.VerboseLogging && DateTime.UtcNow - lastStatusLog > TimeSpan.FromSeconds(2))
        {
            lastStatusLog = DateTime.UtcNow;
            Services.Log.Information($"[{addonName}] {LastStatus} newlyVisible={newlyVisibleThisFrame}");
        }
    }

    private void ProcessAddon(AtkUnitBase* addon)
    {
        addonsSeen++;

        foreach (var pointer in MiniTalk.EnumerateBubbleComponents(addon))
        {
            componentsSeen++;

            var component = (AtkComponentNode*)pointer;
            var nodes = MiniTalk.ResolveNodes(component, config.StickerNodeId);
            if (!nodes.IsUsable)
                continue;

            var visible = component->AtkResNode.IsVisible();
            if (visible)
                visibleBubbles++;

            var phrase = visible ? AutoTranslateDetector.Normalize(MiniTalk.ReadText(nodes.Text)) : string.Empty;
            if (!string.IsNullOrEmpty(phrase))
                textsSeen++;

            if (visible)
                visibleThisFrame.Add(pointer);

            states.TryGetValue(pointer, out var state);

            // Bubble closed, or we were switched off — undo first.
            if (state is not null && (!visible || !config.Enabled))
            {
                Restore(state);
                states.Remove(pointer);
                state = null;
            }

            if (state is not null)
            {
                if (state.Parts is not null)
                    ApplyGeometry(nodes, state.Parts, state);

                continue;
            }

            if (!visible || !config.Enabled)
                continue;

            // Match by the bubble's own text rather than by arrival order. Only messages that resolved
            // to a sticker are waiting, so ordinary chat cannot take the entry meant for this one.
            // Deliberately does not consume: the first attempt often fails while the texture decodes.
            var chosen = hook.Peek(phrase);

            if (chosen is null)
            {
                if (config.VerboseLogging && !visibleLastFrame.Contains(pointer) && !string.IsNullOrEmpty(phrase))
                {
                    newlyVisibleThisFrame++;
                    // Print both sides: if a sticker is pending but the texts differ, the two
                    // normalisations disagree and that is the bug, not the pairing.
                    Services.Log.Information(
                        $"  bubble opened (text=\"{phrase}\") -> no match; " +
                        $"pending=[{string.Join(" | ", hook.PendingTexts)}]");
                }

                continue;
            }

            newlyVisibleThisFrame++;

            if (config.VerboseLogging)
                Services.Log.Information($"matched \"{phrase}\" -> {System.IO.Path.GetFileName(chosen)}");

            var parts = GetParts(chosen);
            if (parts is null)
            {
                // Normal on first use while the image decodes. The pending entry is intentionally still
                // there, so the next frame tries again.
                if (config.VerboseLogging)
                    Services.Log.Information("  texture still decoding; retrying next frame");

                continue;
            }

            var applied = Apply(nodes, phrase, parts);
            if (applied is null)
            {
                Services.Log.Warning("  Apply() returned null - sticker node missing on this bubble");
                continue;
            }

            applied.ImagePath = chosen;
            states[pointer] = applied;

            // Only now is the sticker really on screen, so retire the pending entry.
            hook.Consume(phrase);
        }
    }

    /// <summary>Returns the private parts list for an image, building it on first use.</summary>
    private StickerParts? GetParts(string path)
    {
        if (partsByPath.TryGetValue(path, out var existing))
            return existing;

        if (!registry.TryGet(path, out var sticker))
            return null;

        var created = StickerParts.Create((Texture*)sticker.Pointer, sticker);
        if (created is null)
        {
            Services.Log.Error($"Could not allocate UI-space parts list for {path}");
            return null;
        }

        partsByPath[path] = created;
        return created;
    }

    /// <summary>
    /// Sizes the sticker node and centres it on the bubble body. Returns the size applied.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Position is computed from the body rect, never from the sticker's own size — deriving one from the
    /// other made the size slider move the sticker instead of resizing it.
    /// </para>
    /// <para>
    /// The body is the nine-grid sharing the sticker node's parent, so both live in the same coordinate
    /// space and centring is a plain subtraction. Falling back to the parent's own rect keeps this sane if
    /// the sticker node is retargeted somewhere without a sibling nine-grid.
    /// </para>
    /// <para>
    /// Called every frame while a sticker is applied, because the game owns these nodes and rewrites
    /// their geometry as the bubble tracks its speaker.
    /// </para>
    /// </remarks>
    private (ushort Width, ushort Height) ApplyGeometry(
        MiniTalk.BubbleNodes nodes,
        StickerParts parts,
        SlotState? state = null)
    {
        var node = nodes.Sticker;
        ref var res = ref node->AtkResNode;

        // An image node's Width/Height is the sampling rect over its part, not a display size — shrinking
        // them crops rather than scales. Keep the rect at the source size and resize via ScaleX/Y.
        res.SetWidth(parts.SourceWidth);
        res.SetHeight(parts.SourceHeight);

        // Scaling pivots on Origin. Left wherever the game had it, the node grows about an arbitrary
        // point and lands off-centre by a fixed amount — the offset you would otherwise dial out by hand.
        res.OriginX = 0f;
        res.OriginY = 0f;

        if (config.NeutralizeStickerTint)
        {
            // Neutral means white multiply and zero add. Alpha is left alone so the bubble's fade in and
            // out still applies to the sticker.
            res.Color.R = 255;
            res.Color.G = 255;
            res.Color.B = 255;

            res.AddRed = 0;
            res.AddGreen = 0;
            res.AddBlue = 0;
            res.AddRed_2 = 0;
            res.AddGreen_2 = 0;
            res.AddBlue_2 = 0;

            if (config.NeutralizeMultiply)
            {
                res.MultiplyRed = config.NeutralMultiplyValue;
                res.MultiplyGreen = config.NeutralMultiplyValue;
                res.MultiplyBlue = config.NeutralMultiplyValue;
                res.MultiplyRed_2 = config.NeutralMultiplyValue;
                res.MultiplyGreen_2 = config.NeutralMultiplyValue;
                res.MultiplyBlue_2 = config.NeutralMultiplyValue;
            }

            if (config.NeutralizeAncestorTint)
            {
                // Colour composes down the tree, so a tint on a parent reaches the sticker regardless of
                // what the sticker itself says. Clearing those parents cleans the sticker but also drains
                // the bubble frame, which inherits from the very same nodes.
                //
                // So the tint is moved rather than deleted: captured on the way up, cleared from the
                // parents, then re-applied to the frame layers directly. Bubble keeps its channel colour,
                // sticker renders clean.
                var captured = default(CapturedTint);

                for (var ancestor = res.ParentNode;
                     ancestor is not null && !MiniTalk.IsComponent(ancestor);
                     ancestor = ancestor->ParentNode)
                {
                    if (!captured.HasTint && IsTinted(ancestor))
                        captured = CapturedTint.From(ancestor);

                    if (state is not null && !state.TintedAncestors.Exists(a => a.Node == (nint)ancestor))
                    {
                        state.TintedAncestors.Add(((nint)ancestor, ancestor->Color.RGBA,
                            ancestor->AddRed, ancestor->AddGreen, ancestor->AddBlue,
                            ancestor->MultiplyRed, ancestor->MultiplyGreen, ancestor->MultiplyBlue));
                    }

                    Neutralize(ancestor);
                }

                if (captured.HasTint)
                {
                    foreach (var candidate in nodes.NineGrids)
                        ApplyTint((AtkResNode*)candidate, captured, state);

                    // The tail is frame too, so it should match rather than turn white.
                    foreach (var candidate in nodes.Images)
                    {
                        if (candidate != (nint)node)
                            ApplyTint((AtkResNode*)candidate, captured, state);
                    }
                }
            }

            if (state is not null && !state.LoggedTint)
            {
                state.LoggedTint = true;
                LogTintChain(nodes.Sticker);
            }
        }

        // Stretch the part to fill the node's rect. The values are None=0, Tile=1, Stretch=2 — this was
        // previously set to 1 on the assumption that it meant clamp, which instead tiled the texture and
        // drew a repeat of the sticker beside the bubble whenever the rect exceeded the part.
        node->WrapMode = WrapModeStretch;

        var longestEdge = Math.Max(parts.SourceWidth, parts.SourceHeight);
        if (longestEdge == 0)
            return (0, 0);

        // Ancestors only: the node's own scale is what we are about to set.
        var (ancestorX, ancestorY) = MiniTalk.CumulativeScale(&node->AtkResNode, includeSelf: false);
        var scale = config.StickerMaxSize / longestEdge;
        res.ScaleX = scale / ancestorX;
        res.ScaleY = scale / ancestorY;

        // Footprint in the parent's coordinate space, for centring.
        var footprintWidth = parts.SourceWidth * res.ScaleX;
        var footprintHeight = parts.SourceHeight * res.ScaleY;

        var parent = res.ParentNode;
        var padding = Math.Max(0f, config.BubblePadding);
        var frameWidth = (ushort)Math.Clamp(footprintWidth + (padding * 2f), 1f, ushort.MaxValue);
        var frameHeight = (ushort)Math.Clamp(footprintHeight + (padding * 2f), 1f, ushort.MaxValue);

        // Rect the sticker will be centred in — the sibling frame's new bounds, or the parent's if the
        // frame is not being resized.
        float targetX = 0, targetY = 0, targetWidth = 0, targetHeight = 0;

        if (config.FitBubbleToSticker && nodes.NineGrids.Count > 0)
        {
            // Every nine-grid, not just the sticker's siblings: the bubble is drawn as stacked frame
            // layers under different parents, and resizing one leaves the others at full size.
            foreach (var candidate in nodes.NineGrids)
            {
                var frame = (AtkResNode*)candidate;

                var originalX = frame->X;
                var originalY = frame->Y;
                var originalWidth = frame->Width;
                var originalHeight = frame->Height;

                if (state is not null && !state.Resized.Exists(r => r.Node == candidate))
                    state.Resized.Add((candidate, originalX, originalY, originalWidth, originalHeight));

                // Grow upward: the bottom edge is where the tail meets the bubble, so pinning it keeps
                // the tail attached and stops the bubble from descending over the character's head.
                var newX = originalX + ((originalWidth - frameWidth) / 2f);
                var newY = originalY + originalHeight - frameHeight;

                frame->SetWidth(frameWidth);
                frame->SetHeight(frameHeight);
                frame->SetPositionFloat(newX, newY);

                if (frame->ParentNode == parent)
                {
                    targetX = newX;
                    targetY = newY;
                    targetWidth = frameWidth;
                    targetHeight = frameHeight;
                }
            }

            if (state is not null && !state.LoggedFrames)
            {
                state.LoggedFrames = true;
                Services.Log.Information(
                    $"Bubble fit: {nodes.NineGrids.Count} frame layer(s) -> {frameWidth}x{frameHeight}, " +
                    $"sticker footprint {footprintWidth:0}x{footprintHeight:0}, padding {padding:0}");
            }
        }

        if (targetWidth <= 0)
        {
            // Not fitting: centre on the body as the game sized it.
            foreach (var candidate in nodes.NineGrids)
            {
                var frame = (AtkResNode*)candidate;
                if (frame->ParentNode != parent)
                    continue;

                targetX = frame->X;
                targetY = frame->Y;
                targetWidth = frame->Width;
                targetHeight = frame->Height;
                break;
            }
        }

        if (targetWidth <= 0 && parent is not null)
        {
            targetWidth = parent->Width;
            targetHeight = parent->Height;
        }

        res.SetPositionFloat(
            targetX + ((targetWidth - footprintWidth) / 2f) + config.StickerOffsetX,
            targetY + ((targetHeight - footprintHeight) / 2f) + config.StickerOffsetY);

        return ((ushort)footprintWidth, (ushort)footprintHeight);
    }

    private SlotState? Apply(MiniTalk.BubbleNodes nodes, string phrase, StickerParts parts)
    {
        var node = nodes.Sticker;
        if (node is null)
            return null;

        ref var res = ref node->AtkResNode;

        var state = new SlotState
        {
            Phrase = phrase,
            Parts = parts,
            Node = node,
            OriginalPartsList = node->PartsList,
            OriginalPartId = node->PartId,
            OriginalWidth = res.Width,
            OriginalHeight = res.Height,
            OriginalX = res.X,
            OriginalY = res.Y,
            OriginalScaleX = res.ScaleX,
            OriginalScaleY = res.ScaleY,
            OriginalOriginX = res.OriginX,
            OriginalOriginY = res.OriginY,
            OriginalWrapMode = node->WrapMode,
            OriginalColor = res.Color.RGBA,
            OriginalAddRed = res.AddRed,
            OriginalAddGreen = res.AddGreen,
            OriginalAddBlue = res.AddBlue,
            OriginalAddRed2 = res.AddRed_2,
            OriginalAddGreen2 = res.AddGreen_2,
            OriginalAddBlue2 = res.AddBlue_2,
            OriginalMultiplyRed = res.MultiplyRed,
            OriginalMultiplyGreen = res.MultiplyGreen,
            OriginalMultiplyBlue = res.MultiplyBlue,
            OriginalMultiplyRed2 = res.MultiplyRed_2,
            OriginalMultiplyGreen2 = res.MultiplyGreen_2,
            OriginalMultiplyBlue2 = res.MultiplyBlue_2,
            OriginalVisible = res.IsVisible(),
        };

        node->PartsList = parts.PartsList;
        node->PartId = 0;
        res.ToggleVisibility(true);

        var (width, height) = ApplyGeometry(nodes, parts, state);

        if (nodes.Text is not null)
        {
            state.Hidden.Add(((nint)nodes.Text, nodes.Text->AtkResNode.IsVisible()));
            nodes.Text->AtkResNode.ToggleVisibility(false);
        }

        if (config.HideBubbleBackground && !config.FitBubbleToSticker)
        {
            foreach (var nineGrid in nodes.NineGrids)
            {
                var target = (AtkResNode*)nineGrid;
                state.Hidden.Add((nineGrid, target->IsVisible()));
                target->ToggleVisibility(false);
            }

            // Other image nodes are bubble furniture (tail, decoration) — hide them too.
            foreach (var image in nodes.Images)
            {
                if (image == (nint)node)
                    continue;

                var target = (AtkResNode*)image;
                state.Hidden.Add((image, target->IsVisible()));
                target->ToggleVisibility(false);
            }
        }

        // Unconditional: this is the single line that proves the pipeline reached the renderer, so it
        // must not depend on a setting being switched on.
        var (scaleX, scaleY) = MiniTalk.CumulativeScale(&node->AtkResNode);

        Services.Log.Information(
            $"Sticker applied to \"{phrase}\": node id={res.NodeId} " +
            $"size {state.OriginalWidth}x{state.OriginalHeight} -> {width}x{height} " +
            $"(cumulative scale {scaleX:0.##},{scaleY:0.##}) " +
            $"pos ({state.OriginalX:0.#},{state.OriginalY:0.#}) -> ({res.X:0.#},{res.Y:0.#}) " +
            $"partsList {(nint)state.OriginalPartsList:X} -> {(nint)node->PartsList:X}");

        return state;
    }

    /// <summary>
    /// Logs the colour state of the sticker node and every ancestor up to the addon root.
    /// </summary>
    /// <remarks>
    /// Colour composes down the tree, so a tint can originate anywhere above the node being drawn.
    /// Printing the whole chain turns "still tinted" into a specific node and a specific field.
    /// </remarks>
    private static void LogTintChain(AtkImageNode* node)
    {
        var depth = 0;

        for (var current = &node->AtkResNode; current is not null && depth < 12; current = current->ParentNode, depth++)
        {
            Services.Log.Information(
                $"tint[{depth}] id={current->NodeId} {MiniTalk.DescribeType(current)} " +
                $"color=({current->Color.R},{current->Color.G},{current->Color.B},{current->Color.A}) " +
                $"add=({current->AddRed},{current->AddGreen},{current->AddBlue}) " +
                $"add2=({current->AddRed_2},{current->AddGreen_2},{current->AddBlue_2}) " +
                $"mul=({current->MultiplyRed},{current->MultiplyGreen},{current->MultiplyBlue}) " +
                $"mul2=({current->MultiplyRed_2},{current->MultiplyGreen_2},{current->MultiplyBlue_2})");
        }
    }

    /// <summary>A node's colour state, lifted off one node so it can be put on another.</summary>
    private readonly struct CapturedTint
    {
        public required bool HasTint { get; init; }
        public required uint Color { get; init; }
        public required short AddR { get; init; }
        public required short AddG { get; init; }
        public required short AddB { get; init; }
        public required byte MulR { get; init; }
        public required byte MulG { get; init; }
        public required byte MulB { get; init; }

        public static CapturedTint From(AtkResNode* node) => new()
        {
            HasTint = true,
            Color = node->Color.RGBA,
            AddR = node->AddRed,
            AddG = node->AddGreen,
            AddB = node->AddBlue,
            MulR = node->MultiplyRed,
            MulG = node->MultiplyGreen,
            MulB = node->MultiplyBlue,
        };
    }

    /// <summary>True when a node carries any colour other than neutral.</summary>
    private static bool IsTinted(AtkResNode* node)
        => node->Color.R != 255 || node->Color.G != 255 || node->Color.B != 255
        || node->AddRed != 0 || node->AddGreen != 0 || node->AddBlue != 0
        || node->MultiplyRed != NeutralMultiply
        || node->MultiplyGreen != NeutralMultiply
        || node->MultiplyBlue != NeutralMultiply;

    private static void Neutralize(AtkResNode* node)
    {
        node->Color.R = 255;
        node->Color.G = 255;
        node->Color.B = 255;
        node->AddRed = 0;
        node->AddGreen = 0;
        node->AddBlue = 0;
        node->MultiplyRed = NeutralMultiply;
        node->MultiplyGreen = NeutralMultiply;
        node->MultiplyBlue = NeutralMultiply;
    }

    private static void ApplyTint(AtkResNode* node, CapturedTint tint, SlotState? state)
    {
        if (state is not null && !state.TintedAncestors.Exists(a => a.Node == (nint)node))
        {
            state.TintedAncestors.Add(((nint)node, node->Color.RGBA,
                node->AddRed, node->AddGreen, node->AddBlue,
                node->MultiplyRed, node->MultiplyGreen, node->MultiplyBlue));
        }

        var alpha = node->Color.A;
        node->Color.RGBA = tint.Color;
        node->Color.A = alpha;

        node->AddRed = tint.AddR;
        node->AddGreen = tint.AddG;
        node->AddBlue = tint.AddB;
        node->MultiplyRed = tint.MulR;
        node->MultiplyGreen = tint.MulG;
        node->MultiplyBlue = tint.MulB;
    }

    private static void Restore(SlotState state)
    {
        var node = state.Node;
        if (node is not null)
        {
            ref var res = ref node->AtkResNode;

            // Put the game's own list back. Our allocation stays alive in partsByPath for reuse.
            node->PartsList = state.OriginalPartsList;
            node->PartId = state.OriginalPartId;
            res.SetWidth(state.OriginalWidth);
            res.SetHeight(state.OriginalHeight);
            res.SetPositionFloat(state.OriginalX, state.OriginalY);
            res.ScaleX = state.OriginalScaleX;
            res.ScaleY = state.OriginalScaleY;
            res.OriginX = state.OriginalOriginX;
            res.OriginY = state.OriginalOriginY;
            node->WrapMode = state.OriginalWrapMode;

            res.Color.RGBA = state.OriginalColor;
            res.AddRed = state.OriginalAddRed;
            res.AddGreen = state.OriginalAddGreen;
            res.AddBlue = state.OriginalAddBlue;
            res.AddRed_2 = state.OriginalAddRed2;
            res.AddGreen_2 = state.OriginalAddGreen2;
            res.AddBlue_2 = state.OriginalAddBlue2;
            res.MultiplyRed = state.OriginalMultiplyRed;
            res.MultiplyGreen = state.OriginalMultiplyGreen;
            res.MultiplyBlue = state.OriginalMultiplyBlue;
            res.MultiplyRed_2 = state.OriginalMultiplyRed2;
            res.MultiplyGreen_2 = state.OriginalMultiplyGreen2;
            res.MultiplyBlue_2 = state.OriginalMultiplyBlue2;
            res.ToggleVisibility(state.OriginalVisible);
        }

        foreach (var (pointer, visible) in state.Hidden)
            ((AtkResNode*)pointer)->ToggleVisibility(visible);

        foreach (var (pointer, color, addR, addG, addB, mulR, mulG, mulB) in state.TintedAncestors)
        {
            var ancestor = (AtkResNode*)pointer;
            ancestor->Color.RGBA = color;
            ancestor->AddRed = addR;
            ancestor->AddGreen = addG;
            ancestor->AddBlue = addB;
            ancestor->MultiplyRed = mulR;
            ancestor->MultiplyGreen = mulG;
            ancestor->MultiplyBlue = mulB;
        }

        foreach (var (pointer, x, y, width, height) in state.Resized)
        {
            var frame = (AtkResNode*)pointer;
            frame->SetWidth(width);
            frame->SetHeight(height);
            frame->SetPositionFloat(x, y);
        }

        state.Hidden.Clear();
        state.Resized.Clear();
        state.TintedAncestors.Clear();
    }

    /// <summary>Undoes every outstanding edit.</summary>
    public void RestoreAll()
    {
        foreach (var state in states.Values)
            Restore(state);

        states.Clear();
    }

    public void Dispose()
    {
        if (disposed)
            return;

        disposed = true;

        try
        {
            // Detach from every node before freeing the lists those nodes point at.
            RestoreAll();
        }
        catch (Exception ex)
        {
            Services.Log.Error(ex, "Failed to restore chat bubbles on dispose");
        }

        foreach (var parts in partsByPath.Values)
            parts.Dispose();

        partsByPath.Clear();
        visibleLastFrame.Clear();
        visibleThisFrame.Clear();
    }
}
