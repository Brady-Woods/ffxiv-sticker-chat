using System;
using System.Collections.Generic;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using FfxivStickerChat.Packs;
using FfxivStickerChat.Sync;
using FfxivStickerChat.Windows;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace FfxivStickerChat;

public sealed unsafe class Plugin : IDalamudPlugin
{
    private const string ConfigCommand = "/stickerchat";

    private readonly PackStore packStore;
    private readonly PackSyncService packSync;
    private readonly SnowcloakBridge snowcloak;
    private readonly StickerRegistry registry;
    private readonly BubbleHook bubbleHook;
    private readonly BubbleDecorator decorator;
    private readonly ConfigWindow configWindow;
    private readonly DebugWindow debugWindow;
    private readonly IAddonLifecycle.AddonEventDelegate onAddonPostDraw;

    private static readonly string[] BubbleAddons = [MiniTalk.PlayerAddon, MiniTalk.NpcAddon];

    /// <summary>How often the sync queue is pumped, in milliseconds.</summary>
    private const long SyncTickIntervalMs = 1000;

    private long lastSyncTick;

    public Configuration Configuration { get; }

    public WindowSystem WindowSystem { get; } = new("FfxivStickerChat");

    /// <summary>Auto-translate phrases seen this session, newest first.</summary>
    public IReadOnlyList<SeenPhrase> SeenPhrases => bubbleHook.Seen;

    /// <summary>What the decorator saw on the last frame — shown in the config window for diagnosis.</summary>
    public string DecoratorStatus => decorator.LastStatus;

    public Plugin(IDalamudPluginInterface pluginInterface)
    {
        pluginInterface.Create<Services>();

        Configuration = Services.PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        Configuration.Migrate();

        packStore = new PackStore(Services.PluginInterface.ConfigDirectory.FullName);
        packStore.LoadAll();
        Configuration.MigrateMappingsToPack(packStore);

        packSync = new PackSyncService(Configuration, packStore);
        snowcloak = new SnowcloakBridge(packSync);

        registry = new StickerRegistry(Configuration);

        bubbleHook = new BubbleHook(Configuration, packStore);
        decorator = new BubbleDecorator(Configuration, registry, bubbleHook);

        configWindow = new ConfigWindow(this, registry);
        debugWindow = new DebugWindow(packSync);
        WindowSystem.AddWindow(configWindow);
        WindowSystem.AddWindow(debugWindow);

        Services.Framework.Update += OnFrameworkUpdate;

        // Node geometry is written from PostDraw, not the framework tick: the game lays these addons out
        // after Framework.Update, so anything written there is overwritten before it renders.
        onAddonPostDraw = OnAddonPostDraw;
        Services.AddonLifecycle.RegisterListener(AddonEvent.PostDraw, BubbleAddons, onAddonPostDraw);
        Services.PluginInterface.UiBuilder.Draw += WindowSystem.Draw;
        Services.PluginInterface.UiBuilder.OpenConfigUi += ToggleConfigUi;
        Services.PluginInterface.UiBuilder.OpenMainUi += ToggleConfigUi;

        Services.CommandManager.AddHandler(ConfigCommand, new CommandInfo(OnCommand)
        {
            HelpMessage = "Open Sticker Chat settings. \"/stickerchat debug\" opens the bubble inspector.",
        });

        Services.Log.Information("Sticker Chat loaded.");
    }

    /// <summary>Opens the inspector window; also reachable via <c>/stickerchat debug</c>.</summary>
    public void ToggleDebugUi() => debugWindow.Toggle();

    /// <summary>The pack store, for the config UI.</summary>
    public PackStore PackStore => packStore;

    /// <summary>Pack sync, for the config UI.</summary>
    public PackSyncService PackSync => packSync;

    /// <summary>The Snowcloak transport, for the config UI.</summary>
    public SnowcloakBridge Snowcloak => snowcloak;

    /// <summary>
    /// Previews a binding by showing a real bubble on your own character.
    /// </summary>
    /// <remarks>
    /// Local display only — nothing is sent and no one else sees it. It runs the full matching path, so
    /// a preview that shows the sticker proves the binding actually works.
    /// </remarks>
    public string PreviewSticker(uint group, uint key)
    {
        // A preview must survive the channel filter, or testing a binding would require un-blocking Say.
        var say = (ushort)Dalamud.Game.Text.XivChatType.Say;
        var wasBlocked = Configuration.DisabledChannels.Remove(say);

        var error = BubblePreview.Show(group, key);

        if (wasBlocked)
            Configuration.DisabledChannels.Add(say);

        return error;
    }

    private void OnFrameworkUpdate(IFramework framework)
    {
        try
        {
            decorator.OnFrameworkUpdate();
        }
        catch (Exception ex)
        {
            // A throw here would fire every frame; log once-ish and keep the game usable.
            Services.Log.Error(ex, "Sticker Chat frame update failed");
        }

        // Sync work is second-scale, not frame-scale: it walks the pack list and hashes nothing, but
        // there is no reason to do it 100 times a second.
        if (Environment.TickCount64 - lastSyncTick < SyncTickIntervalMs)
            return;

        lastSyncTick = Environment.TickCount64;

        try
        {
            snowcloak.Tick();
            packSync.Pump();
        }
        catch (Exception ex)
        {
            Services.Log.Error(ex, "Sticker Chat pack sync failed");
        }
    }

    private void OnAddonPostDraw(AddonEvent type, AddonArgs args)
    {
        try
        {
            if (args.Addon.IsNull)
                return;

            decorator.OnAddonPostDraw(args.AddonName, (AtkUnitBase*)args.Addon.Address);
        }
        catch (Exception ex)
        {
            Services.Log.Error(ex, "Sticker Chat addon draw failed");
        }
    }

    private void OnCommand(string command, string args)
    {
        if (args.Trim().Equals("debug", StringComparison.OrdinalIgnoreCase))
            debugWindow.Toggle();
        else
            configWindow.Toggle();
    }

    private void ToggleConfigUi() => configWindow.Toggle();

    public void Dispose()
    {
        Services.Framework.Update -= OnFrameworkUpdate;
        Services.AddonLifecycle.UnregisterListener(onAddonPostDraw);
        Services.PluginInterface.UiBuilder.Draw -= WindowSystem.Draw;
        Services.PluginInterface.UiBuilder.OpenConfigUi -= ToggleConfigUi;
        Services.PluginInterface.UiBuilder.OpenMainUi -= ToggleConfigUi;

        Services.CommandManager.RemoveHandler(ConfigCommand);

        WindowSystem.RemoveAllWindows();
        configWindow.Dispose();
        debugWindow.Dispose();

        // Stop new payloads arriving before the service they feed goes away, then cancel any download
        // in flight before the store it would write into does.
        snowcloak.Dispose();
        packSync.Dispose();

        // Order matters: stop new bubbles arriving, undo edits, then release textures.
        bubbleHook.Dispose();
        decorator.Dispose();
        registry.Dispose();
    }
}
