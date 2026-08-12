using System;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using FfxivStickerChat.Packs;
using FfxivStickerChat.Sync;

namespace FfxivStickerChat.Windows;

/// <summary>
/// Sharing packs with players you are already paired with in Snowcloak.
/// </summary>
/// <remarks>
/// Both directions are off until switched on, and the tab explains what each one does before it does it —
/// sharing publishes a link to your packs, and receiving contacts the host holding someone else's, which
/// discloses your IP address to that host the same way opening any web page would.
/// </remarks>
public sealed class SyncTab
{
    private static readonly Vector4 Good = new(0.5f, 0.85f, 0.5f, 1f);
    private static readonly Vector4 Warn = new(1f, 0.8f, 0.4f, 1f);
    private static readonly Vector4 Bad = new(1f, 0.5f, 0.4f, 1f);

    private readonly Plugin plugin;
    private readonly Configuration configuration;

    public SyncTab(Plugin plugin)
    {
        this.plugin = plugin;
        configuration = plugin.Configuration;
    }

    public void Draw()
    {
        var bridge = plugin.Snowcloak;
        var sync = plugin.PackSync;

        DrawTransport(bridge);
        ImGui.Separator();

        if (!configuration.SyncNoticeShown)
        {
            DrawFirstRunNotice();
            return;
        }

        DrawSwitches(sync);
        ImGui.Separator();

        DrawOffers(sync);
        ImGui.Separator();

        DrawStorage(sync);
        DrawBlocked(sync);
    }

    private void DrawTransport(SnowcloakBridge bridge)
    {
        var colour = bridge.State switch
        {
            BridgeState.Registered => Good,
            BridgeState.NotDetected => Bad,
            _ => Warn,
        };

        ImGui.TextColored(colour, bridge.Status);

        if (bridge.State == BridgeState.PermissionDenied)
        {
            // Permission is granted in Snowcloak's window, not ours — saying so is the difference between
            // a dead end and a next step.
            if (ImGui.Button("Open Snowcloak's plugin integrations"))
            {
                if (!bridge.OpenPermissionsWindow())
                    Services.Log.Warning("Snowcloak would not open its integrations window.");
            }
        }
        else if (bridge.State == BridgeState.NotDetected)
        {
            ImGui.TextWrapped(
                "Only Snowcloak can carry this. The other sync plugins have no way for a third-party " +
                "plugin's data to travel with a character, so there is nothing to fall back to — pass " +
                "packs around as a zip or a link instead.");
        }
    }

    /// <summary>
    /// The one-time explanation, shown before either direction can be turned on.
    /// </summary>
    /// <remarks>
    /// Receiving fetches from whatever host a pair names, which reveals your IP to that host. That is a
    /// normal consequence of downloading anything, but it is the first time this plugin does it on its
    /// own, so it is stated before rather than after.
    /// </remarks>
    private void DrawFirstRunNotice()
    {
        ImGui.TextWrapped("Pack sync sends a link, never your images or your chat.");
        ImGui.Spacing();

        ImGui.BulletText("Sharing publishes your packs' names and download links to your Snowcloak pairs.");
        ImGui.BulletText("Receiving downloads what they publish, automatically.");

        ImGui.Spacing();
        ImGui.TextWrapped(
            "Installing a pack means contacting the site hosting it, which tells that site your IP " +
            "address — the same as opening the link in a browser. Sync will only download from these " +
            "hosts:");

        ImGui.TextDisabled("    " + string.Join(", ", PackDownloader.SyncHosts));

        ImGui.Spacing();
        ImGui.TextWrapped(
            "A pack will only install if it belongs to the character sharing it, so nobody can put " +
            "stickers over someone else's head.");

        ImGui.Spacing();
        if (ImGui.Button("Got it"))
        {
            configuration.SyncNoticeShown = true;
            configuration.Save();
        }
    }

    private void DrawSwitches(PackSyncService sync)
    {
        var share = configuration.SyncShare;
        if (ImGui.Checkbox("Share my packs with sync pairs", ref share))
        {
            configuration.SyncShare = share;
            configuration.Save();
            sync.ForceRepublish();
        }

        ImGui.TextDisabled("    " + sync.PublishStatus);
        ImGui.Spacing();

        var receive = configuration.SyncReceive;
        if (ImGui.Checkbox("Install packs my pairs share", ref receive))
        {
            configuration.SyncReceive = receive;
            configuration.Save();
        }

        ImGui.TextDisabled("    " + sync.ReceiveStatus);
    }

    private void DrawOffers(PackSyncService sync)
    {
        var offers = sync.Offers().ToList();

        ImGui.TextUnformatted($"Advertised by pairs ({offers.Count} not installed)");

        if (offers.Count == 0)
        {
            ImGui.TextDisabled(
                "Nothing waiting. A pair's packs appear here when they are nearby and sharing.");
            return;
        }

        if (!ImGui.BeginTable("##offers", 4, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg))
            return;

        ImGui.TableSetupColumn("Pack", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("From", ImGuiTableColumnFlags.WidthFixed, 120);
        ImGui.TableSetupColumn("Status", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("##actions", ImGuiTableColumnFlags.WidthFixed, 110);
        ImGui.TableHeadersRow();

        foreach (var (uid, pointer, blocker) in offers)
        {
            ImGui.TableNextRow();
            ImGui.PushID(pointer.Id);

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(pointer.Name);

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(uid);

            ImGui.TableNextColumn();
            if (blocker.Length == 0)
                ImGui.TextColored(Good, "queued");
            else
                ImGui.TextWrapped(blocker);

            ImGui.TableNextColumn();
            if (ImGui.SmallButton("Retry"))
                sync.RetryNow(pointer.Id);

            ImGui.SameLine();
            if (ImGui.SmallButton("Block"))
                sync.BlockPair(uid);

            ImGui.PopID();
        }

        ImGui.EndTable();
    }

    private void DrawStorage(PackSyncService sync)
    {
        var used = sync.SyncedBytes;
        var budget = (long)configuration.SyncStorageBudgetMb * 1024 * 1024;

        ImGui.TextUnformatted($"Storage: {used / (1024 * 1024)} MB of {configuration.SyncStorageBudgetMb} MB");

        var mb = configuration.SyncStorageBudgetMb;
        ImGui.SetNextItemWidth(200);
        if (ImGui.SliderInt("Budget (MB)", ref mb, 64, 4096))
        {
            configuration.SyncStorageBudgetMb = mb;
            configuration.Save();
        }

        if (ImGui.IsItemDeactivatedAfterEdit())
            sync.EnforceStorageBudget();

        if (used > budget)
            ImGui.TextColored(Warn, "Over budget — the least recently seen packs will be removed.");

        ImGui.TextDisabled("Only packs that arrived over sync count, and only those are ever removed.");
    }

    private void DrawBlocked(PackSyncService sync)
    {
        if (configuration.SyncBlockedUids.Count == 0)
            return;

        ImGui.Separator();
        ImGui.TextUnformatted($"Blocked pairs ({configuration.SyncBlockedUids.Count})");

        foreach (var uid in configuration.SyncBlockedUids.ToList())
        {
            ImGui.PushID(uid);
            ImGui.TextUnformatted(uid);
            ImGui.SameLine();

            if (ImGui.SmallButton("Unblock"))
                sync.UnblockPair(uid);

            ImGui.PopID();
        }
    }
}
