using System;
using System.IO;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.ImGuiFileDialog;
using Dalamud.Interface.Windowing;

namespace FfxivStickerChat.Windows;

/// <summary>
/// Binds auto-translate phrases to sticker images.
/// </summary>
/// <remarks>
/// Phrases come from the game's own <c>Completion</c> sheet rather than from chat history, so any phrase
/// can be bound up front and a binding can be repointed later without deleting it.
/// </remarks>
public sealed class ConfigWindow : Window, IDisposable
{
    private const float PreviewSize = 48f;

    private readonly Plugin plugin;
    private readonly Configuration config;
    private readonly StickerRegistry registry;
    private readonly FileDialogManager fileDialogs = new();
    private readonly PackTab packTab;

    private string phraseFilter = string.Empty;

    public ConfigWindow(Plugin plugin, StickerRegistry registry)
        : base("Sticker Chat###StickerChatConfig")
    {
        this.plugin = plugin;
        this.registry = registry;
        config = plugin.Configuration;
        packTab = new PackTab(plugin, registry, fileDialogs);

        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(640, 420),
            MaximumSize = new Vector2(1600, 1200),
        };
    }

    public void Dispose() { }

    public override void Draw()
    {
        if (ImGui.BeginTabBar("##tabs"))
        {
            if (ImGui.BeginTabItem("Packs"))
            {
                packTab.Draw();
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("Seen phrases"))
            {
                DrawSeenPhrases();
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("Settings"))
            {
                DrawGeneral();
                ImGui.EndTabItem();
            }

            ImGui.EndTabBar();
        }

        // The dialog renders over this window and must be pumped every frame while open.
        fileDialogs.Draw();
    }

    private void DrawGeneral()
    {
        var enabled = config.Enabled;
        if (ImGui.Checkbox("Enabled", ref enabled))
        {
            config.Enabled = enabled;
            config.Save();
        }

        ImGui.SameLine();
        var onlyLocal = config.OnlyLocalPlayer;
        if (ImGui.Checkbox("Only my own messages", ref onlyLocal))
        {
            config.OnlyLocalPlayer = onlyLocal;
            config.Save();
        }

        ImGui.SameLine();
        var fit = config.FitBubbleToSticker;
        if (ImGui.Checkbox("Fit bubble", ref fit))
        {
            config.FitBubbleToSticker = fit;
            config.Save();
        }

        ImGui.SetNextItemWidth(220);
        var size = config.StickerMaxSize;
        if (ImGui.SliderFloat("Sticker max edge (px)", ref size, 32f, 1024f, "%.0f"))
        {
            config.StickerMaxSize = size;
            config.Save();
        }

        if (fit)
        {
            ImGui.SetNextItemWidth(220);
            var padding = config.BubblePadding;
            if (ImGui.SliderFloat("Bubble padding", ref padding, 0f, 64f, "%.0f"))
            {
                config.BubblePadding = padding;
                config.Save();
            }
        }

        ImGui.Separator();
        DrawChannels();

        ImGui.Separator();
        DrawAdvanced();
    }

    private void DrawAdvanced()
    {
        if (!ImGui.TreeNode("Advanced"))
            return;

        var neutralize = config.NeutralizeStickerTint;
        if (ImGui.Checkbox("Remove bubble tint from sticker", ref neutralize))
        {
            config.NeutralizeStickerTint = neutralize;
            config.Save();
        }

        var includeNpc = config.IncludeNpcBubbles;
        if (ImGui.Checkbox("Also decorate NPC balloons", ref includeNpc))
        {
            config.IncludeNpcBubbles = includeNpc;
            config.Save();
        }

        ImGui.SetNextItemWidth(160);
        var nodeId = (int)config.StickerNodeId;
        if (ImGui.InputInt("Sticker node id", ref nodeId))
        {
            config.StickerNodeId = (uint)Math.Max(0, nodeId);
            config.Save();
        }

        ImGui.SetNextItemWidth(160);
        var offsetX = config.StickerOffsetX;
        if (ImGui.SliderFloat("Nudge X", ref offsetX, -200f, 200f, "%.0f"))
        {
            config.StickerOffsetX = offsetX;
            config.Save();
        }

        ImGui.SetNextItemWidth(160);
        var offsetY = config.StickerOffsetY;
        if (ImGui.SliderFloat("Nudge Y", ref offsetY, -200f, 200f, "%.0f"))
        {
            config.StickerOffsetY = offsetY;
            config.Save();
        }

        ImGui.SameLine();
        if (ImGui.Button("Reset nudge"))
        {
            config.StickerOffsetX = 0f;
            config.StickerOffsetY = 0f;
            config.Save();
        }

        var verbose = config.VerboseLogging;
        if (ImGui.Checkbox("Verbose logging", ref verbose))
        {
            config.VerboseLogging = verbose;
            config.Save();
        }

        ImGui.SameLine();
        if (ImGui.Button("Bubble inspector"))
            plugin.ToggleDebugUi();

        ImGui.SetNextItemWidth(160);
        var budget = config.TextureCacheBudgetMb;
        if (ImGui.SliderInt("Texture cache (MB)", ref budget, 16, 512))
        {
            config.TextureCacheBudgetMb = budget;
            config.Save();
        }

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Decoded stickers held in memory. Least recently used are dropped past this.");

        ImGui.TextDisabled(plugin.DecoratorStatus);
        ImGui.TreePop();
    }

    /// <summary>
    /// Auto-translate phrases observed this session, each bindable in one click.
    /// </summary>
    /// <remarks>
    /// Faster than hunting the full dictionary when you already know the phrase was just sent, and it
    /// doubles as confirmation that the hook is seeing traffic at all.
    /// </remarks>
    /// <summary>
    /// Per-channel toggles, shown alongside whether the game will draw a bubble there at all.
    /// </summary>
    /// <remarks>
    /// Both switches must be on. Showing only ours would make a channel the game has disabled look
    /// broken — which is exactly how Free Company behaves out of the box.
    /// </remarks>
    private void DrawChannels()
    {
        ImGui.SetNextItemOpen(true, ImGuiCond.FirstUseEver);
        if (!ImGui.TreeNode("Channels"))
            return;

        ImGui.TextDisabled(
            "A sticker needs this box ticked AND chat bubbles enabled for that channel in the game\n" +
            "(Character Configuration > Log Window Settings > Chat Bubbles).");

        if (ImGui.SmallButton("All"))
        {
            config.DisabledChannels.Clear();
            config.Save();
        }

        ImGui.SameLine();
        if (ImGui.SmallButton("None"))
        {
            config.DisabledChannels.Clear();
            foreach (var channel in BubbleChannels.All)
                config.DisabledChannels.Add(channel.Id);

            foreach (var unknown in BubbleChannels.UnknownSeen)
                config.DisabledChannels.Add(unknown);

            config.Save();
        }

        if (ImGui.BeginTable("##channels", 3, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg))
        {
            ImGui.TableSetupColumn("Replace", ImGuiTableColumnFlags.WidthFixed, 60);
            ImGui.TableSetupColumn("Channel", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("Game bubbles", ImGuiTableColumnFlags.WidthFixed, 110);
            ImGui.TableHeadersRow();

            foreach (var channel in BubbleChannels.All)
            {
                ImGui.TableNextRow();
                ImGui.PushID(channel.Id);

                ImGui.TableNextColumn();
                DrawChannelToggle(channel.Id);

                ImGui.TableNextColumn();
                ImGui.TextUnformatted(channel.Label);

                ImGui.TableNextColumn();
                var inGame = BubbleChannels.IsEnabledInGame(channel);

                if (inGame is null)
                    ImGui.TextDisabled("unknown");
                else if (inGame.Value)
                    ImGui.TextColored(new Vector4(0.5f, 0.9f, 0.5f, 1f), "on");
                else
                    ImGui.TextColored(new Vector4(1f, 0.6f, 0.3f, 1f), "OFF in game");

                ImGui.PopID();
            }

            // Log kinds the curated list does not name, discovered at runtime. Listing them makes an
            // unrecognised channel toggleable instead of invisible.
            foreach (var unknown in BubbleChannels.UnknownSeen)
            {
                ImGui.TableNextRow();
                ImGui.PushID(1000 + unknown);

                ImGui.TableNextColumn();
                DrawChannelToggle(unknown);

                ImGui.TableNextColumn();
                ImGui.TextUnformatted($"Log kind {unknown}");

                ImGui.TableNextColumn();
                ImGui.TextDisabled("unknown");

                ImGui.PopID();
            }

            ImGui.EndTable();
        }

        ImGui.TreePop();
    }

    private void DrawChannelToggle(ushort logKindId)
    {
        var enabled = config.IsChannelEnabled(logKindId);
        if (!ImGui.Checkbox("##on", ref enabled))
            return;

        if (enabled)
            config.DisabledChannels.Remove(logKindId);
        else if (!config.DisabledChannels.Contains(logKindId))
            config.DisabledChannels.Add(logKindId);

        config.Save();
    }

    private void DrawSeenPhrases()
    {
        var seen = plugin.SeenPhrases;

        ImGui.TextUnformatted($"Seen this session ({seen.Count})");

        if (seen.Count == 0)
        {
            ImGui.TextDisabled("Nothing yet. Send or receive an auto-translate phrase.");
            return;
        }

        var local = plugin.PackStore.GetOrCreateLocal();

        foreach (var entry in seen)
        {
            ImGui.PushID($"seen{entry.Group}:{entry.Key}");

            var bound = local.Entries.Exists(m => m.Group == entry.Group && m.Key == entry.Key);

            if (bound)
            {
                ImGui.TextDisabled($"{entry.Text}  (in {local.Name})");
            }
            else
            {
                if (ImGui.Button("Bind"))
                {
                    local.Entries.Add(new Packs.PackEntry
                    {
                        Group = entry.Group,
                        Key = entry.Key,
                        Phrase = entry.Text,
                    });
                    plugin.PackStore.Save(local);
                }

                ImGui.SameLine();
                ImGui.TextUnformatted(entry.Text);
            }

            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip(
                    $"group {entry.Group}, key {entry.Key}\n" +
                    $"from {(string.IsNullOrEmpty(entry.Sender) ? "unknown" : entry.Sender)} " +
                    $"at {entry.SeenAt:HH:mm:ss}");
            }

            ImGui.PopID();
        }
    }

    /// <summary>Reopens the browser where the last image came from, falling back to the plugin folder.</summary>
    private static string GetStartDirectory(string currentPath)
    {
        if (!string.IsNullOrWhiteSpace(currentPath))
        {
            var directory = Path.GetDirectoryName(currentPath);
            if (!string.IsNullOrEmpty(directory) && Directory.Exists(directory))
                return directory;
        }

        return Services.PluginInterface.AssemblyLocation.Directory?.FullName ?? string.Empty;
    }
}
