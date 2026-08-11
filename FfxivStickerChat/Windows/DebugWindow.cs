using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using CSFramework = FFXIVClientStructs.FFXIV.Client.System.Framework.Framework;

namespace FfxivStickerChat.Windows;

/// <summary>
/// Live inspector for the chat-bubble addons.
/// </summary>
/// <remarks>
/// No public node-ID map exists for either <c>MiniTalk.uld</c> or <c>minitalkplayer.uld</c>, so this
/// window reports what is actually there rather than assuming a layout. The addon scan at the top is the
/// first thing to check: if <c>MiniTalkPlayer</c> never appears while player bubbles are on screen, the
/// addon name is wrong and nothing downstream can work.
/// </remarks>
public sealed unsafe class DebugWindow : Window, IDisposable
{
    private static readonly string[] AddonNames = [MiniTalk.PlayerAddon, MiniTalk.NpcAddon];

    private int selectedAddon;
    private string filter = "MiniTalk";
    private bool onlyVisible;

    public DebugWindow() : base("Sticker Chat — Bubble Inspector###StickerChatDebug")
    {
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(660, 440),
            MaximumSize = new Vector2(1800, 1400),
        };
    }

    public void Dispose() { }

    public override void Draw()
    {
        DrawAddonScan();
        ImGui.Separator();

        ImGui.SetNextItemWidth(220);
        ImGui.Combo("Addon", ref selectedAddon, AddonNames, AddonNames.Length);
        ImGui.SameLine();
        ImGui.Checkbox("Only visible nodes", ref onlyVisible);

        var addonName = AddonNames[selectedAddon];
        var addon = MiniTalk.GetAddon(addonName);

        ImGui.SameLine();
        if (ImGui.Button("Dump to /xllog"))
        {
            foreach (var line in BuildDump(addonName, addon))
                Services.Log.Information(line);

            Services.Log.Information("--- end dump ---");
        }

        if (addon is null)
        {
            ImGui.TextColored(new Vector4(1f, 0.6f, 0.3f, 1f),
                $"{addonName} is not loaded. Bubbles only exist while someone is speaking.");
            return;
        }

        ImGui.TextUnformatted(
            $"visible={addon->IsVisible} nodeListCount={addon->UldManager.NodeListCount} " +
            $"rootNode={(addon->RootNode is null ? "null" : "ok")}");

        var components = MiniTalk.EnumerateBubbleComponents(addon);
        ImGui.TextUnformatted($"component nodes found: {components.Count}");

        if (components.Count == 0)
        {
            ImGui.TextColored(new Vector4(1f, 0.4f, 0.4f, 1f),
                "No component nodes. Replacement cannot work until this is non-zero — check the tree below.");
        }

        ImGui.Separator();

        if (ImGui.CollapsingHeader("Node tree", ImGuiTreeNodeFlags.DefaultOpen))
            DrawTree(addon);

        if (ImGui.CollapsingHeader("Bubble components (resolved by type)"))
            DrawResolved(components);

        if (ImGui.CollapsingHeader("AgentScreenLog balloon queue"))
            DrawBalloonQueue();
    }

    private void DrawAddonScan()
    {
        ImGui.SetNextItemWidth(200);
        ImGui.InputText("Addon name filter", ref filter, 64);

        var found = MiniTalk.ScanAddons(filter);
        ImGui.TextUnformatted($"Loaded addons matching \"{filter}\": {found.Count}");

        foreach (var entry in found)
        {
            ImGui.BulletText(
                $"{entry.Name} — visible={entry.IsVisible} nodes={entry.NodeCount} @ {entry.Address:X}");
        }

        if (found.Count == 0)
        {
            ImGui.TextColored(new Vector4(1f, 0.6f, 0.3f, 1f),
                "None loaded right now. Get a chat bubble on screen and check again.");
        }
    }

    private void DrawTree(AtkUnitBase* addon)
    {
        if (addon->RootNode is null)
        {
            ImGui.TextUnformatted("<no root node>");
            return;
        }

        foreach (var line in DescribeTree(addon, includeHidden: !onlyVisible))
            ImGui.TextUnformatted(line);
    }

    private void DrawResolved(List<nint> components)
    {
        for (var i = 0; i < components.Count; i++)
        {
            var component = (AtkComponentNode*)components[i];
            var nodes = MiniTalk.ResolveNodes(component, 6);
            var text = MiniTalk.ReadText(nodes.Text);

            ImGui.TextUnformatted(
                $"[{i}] vis={component->AtkResNode.IsVisible()} " +
                $"text={(nodes.Text is null ? "none" : $"\"{Truncate(text, 30)}\"")} " +
                $"images={nodes.Images.Count} nineGrids={nodes.NineGrids.Count} " +
                $"sticker={(nodes.Sticker is null ? "none" : $"id{nodes.Sticker->AtkResNode.NodeId}")}");
        }
    }

    /// <summary>
    /// Renders the whole addon as indented text. Shared by the on-screen view and the log dump so what
    /// you paste is exactly what you saw.
    /// </summary>
    private static List<string> DescribeTree(AtkUnitBase* addon, bool includeHidden)
    {
        var lines = new List<string>();
        if (addon is null || addon->RootNode is null)
            return lines;

        Walk(addon->RootNode, 0, lines, includeHidden);
        return lines;
    }

    private static void Walk(AtkResNode* node, int depth, List<string> lines, bool includeHidden)
    {
        // The tree can nest arbitrarily; a depth cap keeps a malformed cycle from hanging the UI thread.
        if (node is null || depth > 12)
            return;

        for (var current = node; current is not null; current = current->PrevSiblingNode)
        {
            var visible = current->IsVisible();
            if (includeHidden || visible)
            {
                var pad = new string(' ', depth * 2);
                lines.Add(
                    $"{pad}id={current->NodeId} {MiniTalk.DescribeType(current)} " +
                    $"pos=({current->X:0.#},{current->Y:0.#}) size={current->Width}x{current->Height} " +
                    $"scale=({current->ScaleX:0.##},{current->ScaleY:0.##}) a={current->Color.A} vis={visible}");

                switch (current->Type)
                {
                    case NodeType.Text:
                        lines.Add($"{pad}  text=\"{Truncate(((AtkTextNode*)current)->NodeText.ToString(), 60)}\"");
                        break;

                    case NodeType.Image:
                        DescribeImage((AtkImageNode*)current, pad, lines);
                        break;
                }
            }

            if (MiniTalk.IsComponent(current))
            {
                var componentBase = ((AtkComponentNode*)current)->Component;
                if (componentBase is not null)
                {
                    if (componentBase->UldManager.RootNode is not null)
                    {
                        Walk(componentBase->UldManager.RootNode, depth + 1, lines, includeHidden);
                    }
                    else
                    {
                        // Some components expose no root but still populate the flat node list.
                        var pad = new string(' ', (depth + 1) * 2);
                        foreach (var pointer in componentBase->UldManager.Nodes)
                        {
                            var child = pointer.Value;
                            if (child is null)
                                continue;

                            lines.Add(
                                $"{pad}[flat] id={child->NodeId} {MiniTalk.DescribeType(child)} " +
                                $"size={child->Width}x{child->Height} vis={child->IsVisible()}");

                            if (child->Type == NodeType.Text)
                                lines.Add($"{pad}  text=\"{Truncate(((AtkTextNode*)child)->NodeText.ToString(), 60)}\"");
                            else if (child->Type == NodeType.Image)
                                DescribeImage((AtkImageNode*)child, pad, lines);
                        }
                    }
                }
            }
            else if (current->ChildNode is not null)
            {
                Walk(current->ChildNode, depth + 1, lines, includeHidden);
            }
        }
    }

    private static void DescribeImage(AtkImageNode* imageNode, string pad, List<string> lines)
    {
        var partsList = imageNode->PartsList;
        if (partsList is null)
        {
            lines.Add($"{pad}  image: PartsList=null (unused node — free to take)");
            return;
        }

        lines.Add($"{pad}  image: partId={imageNode->PartId} partCount={partsList->PartCount} flags={imageNode->Flags}");

        for (var p = 0; p < partsList->PartCount && p < 4; p++)
        {
            var part = &partsList->Parts[p];
            var texture = part->UldAsset is null ? null : &part->UldAsset->AtkTexture;

            lines.Add(
                $"{pad}    part[{p}] uv=({part->U},{part->V}) size={part->Width}x{part->Height} " +
                $"tex={MiniTalk.DescribeTexture(texture)}");
        }
    }

    private static List<string> BuildDump(string addonName, AtkUnitBase* addon)
    {
        var lines = new List<string> { $"=== Sticker Chat dump: {addonName} ===" };

        foreach (var entry in MiniTalk.ScanAddons("MiniTalk"))
            lines.Add($"scan: {entry.Name} visible={entry.IsVisible} nodes={entry.NodeCount}");

        if (addon is null)
        {
            lines.Add($"{addonName} not loaded.");
            return lines;
        }

        lines.Add($"visible={addon->IsVisible} nodeListCount={addon->UldManager.NodeListCount}");
        lines.Add($"component nodes: {MiniTalk.EnumerateBubbleComponents(addon).Count}");
        lines.AddRange(DescribeTree(addon, includeHidden: true));
        return lines;
    }

    private static void DrawBalloonQueue()
    {
        var agent = (AgentScreenLog*)CSFramework.Instance()->GetUIModule()->GetAgentModule()
            ->GetAgentByInternalId(AgentId.ScreenLog);

        if (agent is null)
        {
            ImGui.TextUnformatted("AgentScreenLog unavailable.");
            return;
        }

        ImGui.TextUnformatted($"BalloonCounter={agent->BalloonCounter} HasUpdate={agent->BalloonsHaveUpdate}");

        var queue = agent->BalloonQueue;
        ImGui.TextUnformatted($"BalloonQueue: {queue.Count} entries");

        for (var i = 0; i < queue.Count; i++)
        {
            ref var info = ref queue[i];
            ImGui.TextUnformatted(
                $"  [{i}] BalloonId={info.BalloonId} ObjectId={info.ObjectId.ObjectId:X} " +
                $"bone={info.ParentBone} \"{Truncate(info.FormattedText.ToString(), 40)}\"");
        }
    }

    private static string Truncate(string value, int max)
        => value.Length <= max ? value : value[..max] + "…";
}
