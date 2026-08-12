using System;
using Snowcloak.Ipc;

namespace FfxivStickerChat.Sync;

/// <summary>Where the bridge has got to. Ordered by how far along it is.</summary>
public enum BridgeState
{
    /// <summary>Snowcloak is not installed, or is too old to have the extension API.</summary>
    NotDetected,

    /// <summary>Present, but not registered yet.</summary>
    Detected,

    /// <summary>Present and registered, but the user has not granted what is needed.</summary>
    PermissionDenied,

    /// <summary>Registered with everything needed.</summary>
    Registered,
}

/// <summary>
/// The only place Snowcloak is referenced.
/// </summary>
/// <remarks>
/// <para>
/// Every policy decision lives in <see cref="PackSyncService"/>; this translates between that and one
/// specific sync client. Snowcloak is the only client this can be written against — the other Mare forks
/// have no extension API to carry a third-party plugin's data, and adding one to Lightless would be a
/// change to its server as well as its client.
/// </para>
/// <para>
/// No public member throws. Snowcloak may be absent, present but disconnected, present but not
/// permitted, or mid-upgrade with a changed API, and none of those are exceptional enough to take the
/// plugin down with them — each is a state with a sentence explaining it.
/// </para>
/// </remarks>
public sealed class SnowcloakBridge : IDisposable
{
    /// <summary>
    /// What this plugin asks Snowcloak for.
    /// </summary>
    /// <remarks>
    /// The minimum that works. <c>ReadPairData</c> resolves a pair to the character standing in front of
    /// you, which is what makes it possible to refuse a pack claiming to belong to somebody else. The
    /// other two carry the manifest each way. Deliberately excludes the <c>OutOfRange</c> variants: they
    /// grant data about pairs who are not nearby, and the ownership check needs them nearby anyway.
    /// </remarks>
    private const SnowcloakIpcCapability Required =
        SnowcloakIpcCapability.ReadPairData |
        SnowcloakIpcCapability.TransmitExtensionData |
        SnowcloakIpcCapability.ReceiveExtensionData;

    /// <summary>How often to retry registration while it has not succeeded, in milliseconds.</summary>
    private const long RetryIntervalMs = 15_000;

    private readonly PackSyncService service;

    private SnowcloakIpc? ipc;
    private ExtensionGrant? grant;
    private long lastAttempt = -RetryIntervalMs;
    private bool disposed;

    public SnowcloakBridge(PackSyncService service)
    {
        this.service = service;

        try
        {
            ipc = new SnowcloakIpc(Services.PluginInterface);
            Subscribe();
        }
        catch (Exception ex)
        {
            // Most likely Snowcloak is not installed at all. Not an error worth alarming anyone about.
            Services.Log.Information($"Snowcloak IPC unavailable: {ex.Message}");
            ipc = null;
        }

        PushStatus();
    }

    public BridgeState State { get; private set; } = BridgeState.NotDetected;

    /// <summary>A sentence describing <see cref="State"/>, aimed at the person reading it.</summary>
    public string Status { get; private set; } = "Snowcloak was not found.";

    /// <summary>Snowcloak's own connection state, when it is present.</summary>
    public bool Connected { get; private set; }

    /// <summary>
    /// Registers if needed, and publishes the payload when it has changed. Call about once a second.
    /// </summary>
    public void Tick()
    {
        if (disposed || ipc is null)
            return;

        try
        {
            if (State != BridgeState.Registered)
            {
                if (Environment.TickCount64 - lastAttempt < RetryIntervalMs)
                    return;

                lastAttempt = Environment.TickCount64;
                TryRegister();
                return;
            }

            RefreshVisiblePairs();

            var payload = service.TakePayloadIfChanged();
            if (payload is null)
                return;

            // Snowcloak enforces a minimum interval between pushes itself and reports Unchanged or
            // LimitExceeded rather than failing, so there is nothing to debounce here.
            var result = ipc.SetLocalDataWithResult(payload);

            if (result is not null && !result.Success && result.Code != SnowcloakOperationCode.Unchanged)
            {
                Services.Log.Warning($"Snowcloak refused the sync payload: {result.Code} — {result.Reason}");

                // Republish next tick; a transient refusal must not leave us silently unpublished.
                service.ForceRepublish();
            }
        }
        catch (Exception ex)
        {
            Services.Log.Error(ex, "Snowcloak bridge tick failed");
            State = BridgeState.Detected;
            PushStatus();
        }
    }

    private void TryRegister()
    {
        if (ipc is null)
            return;

        if (!ipc.TryGetApiVersion(out var version))
        {
            State = BridgeState.NotDetected;
            PushStatus();
            return;
        }

        grant = ipc.Register(Required);

        if (grant is null || !grant.Accepted)
        {
            State = BridgeState.PermissionDenied;
            PushStatus(grant?.Reason);
            return;
        }

        if (!grant.Allows(SnowcloakIpcCapability.TransmitExtensionData) ||
            !grant.Allows(SnowcloakIpcCapability.ReceiveExtensionData) ||
            !grant.Allows(SnowcloakIpcCapability.ReadPairData))
        {
            State = BridgeState.PermissionDenied;
            PushStatus();
            return;
        }

        // The payload budget is negotiated, not fixed. Taking it from the grant means a future change on
        // Snowcloak's side shrinks our manifest rather than getting it rejected.
        if (grant.MaxBytesPerPlugin > 0)
            service.PayloadBudgetBytes = grant.MaxBytesPerPlugin;

        State = BridgeState.Registered;
        Connected = ipc.GetConnectionState() == SnowcloakConnectionState.Connected;

        Services.Log.Information(
            $"Snowcloak extension registered (API {version.Major}.{version.Minor}, " +
            $"{grant.MaxBytesPerPlugin} bytes, {grant.MinPushIntervalMs} ms between pushes).");

        // Everything a pair already advertises is waiting to be read; nothing will be re-raised for it.
        SeedFromExistingPairs();

        service.ForceRepublish();
        PushStatus();
    }

    // ---- Events -------------------------------------------------------------------------------------

    private void Subscribe()
    {
        if (ipc is null)
            return;

        ipc.Available += OnAvailable;
        ipc.Unavailable += OnUnavailable;
        ipc.ConnectionStateChanged += OnConnectionStateChanged;
        ipc.PermissionsChanged += OnPermissionsChanged;
        ipc.RemoteDataStateChanged += OnRemoteDataStateChanged;
        ipc.PairVisibilityChanged += OnPairVisibilityChanged;
        ipc.PairRemoved += OnPairRemoved;
    }

    private void Unsubscribe()
    {
        if (ipc is null)
            return;

        ipc.Available -= OnAvailable;
        ipc.Unavailable -= OnUnavailable;
        ipc.ConnectionStateChanged -= OnConnectionStateChanged;
        ipc.PermissionsChanged -= OnPermissionsChanged;
        ipc.RemoteDataStateChanged -= OnRemoteDataStateChanged;
        ipc.PairVisibilityChanged -= OnPairVisibilityChanged;
        ipc.PairRemoved -= OnPairRemoved;
    }

    /// <summary>
    /// Runs work on the game thread, swallowing anything it throws.
    /// </summary>
    /// <remarks>
    /// Callbacks arrive on whatever thread Snowcloak raises them from, and everything downstream — the
    /// pack store, the object table, the config — expects the framework thread. An exception escaping
    /// back into another plugin's event dispatch would also be its problem rather than ours to cause.
    /// </remarks>
    private void OnFramework(Action action)
    {
        if (disposed)
            return;

        _ = Services.Framework.Run(() =>
        {
            if (disposed)
                return;

            try
            {
                action();
            }
            catch (Exception ex)
            {
                Services.Log.Error(ex, "Snowcloak bridge callback failed");
            }
        });
    }

    private void OnAvailable() => OnFramework(() =>
    {
        // Snowcloak restarted or finished loading; registration does not survive that.
        State = BridgeState.Detected;
        lastAttempt = -RetryIntervalMs;
        PushStatus();
    });

    private void OnUnavailable() => OnFramework(() =>
    {
        State = BridgeState.NotDetected;
        grant = null;
        Connected = false;
        PushStatus();
    });

    private void OnConnectionStateChanged(SnowcloakConnectionState state) => OnFramework(() =>
    {
        Connected = state == SnowcloakConnectionState.Connected;

        // Nothing was published while disconnected, so say it again now.
        if (Connected)
            service.ForceRepublish();

        PushStatus();
    });

    private void OnPermissionsChanged(ExtensionGrant updated) => OnFramework(() =>
    {
        grant = updated;

        var ok = updated.Accepted &&
                 updated.Allows(SnowcloakIpcCapability.TransmitExtensionData) &&
                 updated.Allows(SnowcloakIpcCapability.ReceiveExtensionData) &&
                 updated.Allows(SnowcloakIpcCapability.ReadPairData);

        State = ok ? BridgeState.Registered : BridgeState.PermissionDenied;

        if (ok && updated.MaxBytesPerPlugin > 0)
            service.PayloadBudgetBytes = updated.MaxBytesPerPlugin;

        PushStatus(updated.Reason);
    });

    private void OnRemoteDataStateChanged(RemoteExtensionDataState state) => OnFramework(() =>
    {
        switch (state.Availability)
        {
            case RemoteExtensionDataAvailability.Available:
            case RemoteExtensionDataAvailability.AvailableOutOfRange:
                // Identity first: the ownership check needs to know who this uid is before deciding
                // whether to trust what they advertise.
                if (state.ObjectIndex is { } index)
                    LearnIdentity(state.Uid, index);

                service.InjectRemoteData(state.Uid, state.Data);
                break;

            case RemoteExtensionDataAvailability.NoData:
            case RemoteExtensionDataAvailability.Reverted:
                // They withdrew what they were advertising. Packs already installed stay; this only
                // stops them being offered.
                service.InjectRemoteData(state.Uid, null);
                break;

            case RemoteExtensionDataAvailability.NotVisible:
            case RemoteExtensionDataAvailability.Offline:
                break;
        }
    });

    private void OnPairVisibilityChanged(string uid, ushort objectIndex, bool visible) => OnFramework(() =>
    {
        if (visible)
            LearnIdentity(uid, objectIndex);
    });

    private void OnPairRemoved(string uid) => OnFramework(() => service.RemovePair(uid));

    // ---- Identity -----------------------------------------------------------------------------------

    /// <summary>
    /// Works out which character a pair uid is, and tells the service.
    /// </summary>
    /// <remarks>
    /// Snowcloak's <c>PairInfo</c> carries a uid and an alias but no character name — the alias is
    /// user-chosen and proves nothing. The object index is the reliable part: looking it up in the object
    /// table gives the name and home world the game itself is rendering, which is exactly what a sticker
    /// pack's stamped owner has to match.
    /// </remarks>
    private void LearnIdentity(string uid, ushort objectIndex)
    {
        var obj = Services.ObjectTable[objectIndex];

        if (obj is not Dalamud.Game.ClientState.Objects.SubKinds.IPlayerCharacter player)
            return;

        var name = player.Name.TextValue;
        if (string.IsNullOrEmpty(name))
            return;

        service.SetPairIdentity(uid, name, (ushort)player.HomeWorld.RowId);
    }

    /// <summary>
    /// Reads what pairs are already advertising at registration time.
    /// </summary>
    /// <remarks>
    /// Events only fire on change. Registering while standing in a crowded hub would otherwise see
    /// nothing until somebody happened to update their packs.
    /// </remarks>
    private void SeedFromExistingPairs()
    {
        if (ipc is null)
            return;

        // Nothing from the IPC assembly is annotated for nullability, so every result is treated as
        // possibly absent rather than assumed present.
        foreach (var pair in ipc.GetVisiblePairs() ?? [])
        {
            if (pair is null)
                continue;

            if (pair.ObjectIndex is { } index)
                LearnIdentity(pair.Uid, index);

            var state = ipc.GetRemoteDataState(pair.Uid);

            if (state is not null && state.Availability is RemoteExtensionDataAvailability.Available
                    or RemoteExtensionDataAvailability.AvailableOutOfRange)
            {
                service.InjectRemoteData(pair.Uid, state.Data);
            }
        }
    }

    private void RefreshVisiblePairs()
    {
        if (ipc is null)
            return;

        // Visibility events can be missed across a zone change, and identity is what gates installing
        // anything, so it is re-read rather than assumed to still be current.
        foreach (var pair in ipc.GetVisiblePairs() ?? [])
        {
            if (pair is { IsPaused: false, ObjectIndex: { } index })
                LearnIdentity(pair.Uid, index);
        }
    }

    // ---- Status -------------------------------------------------------------------------------------

    /// <summary>
    /// Hands the service a sentence explaining the transport, so its own status lines can defer to it.
    /// </summary>
    private void PushStatus(string? reason = null)
    {
        Status = State switch
        {
            BridgeState.NotDetected =>
                "Snowcloak was not found. Pack sync needs it — no other sync plugin can carry this.",

            BridgeState.Detected =>
                "Snowcloak is installed but has not accepted this plugin yet. Retrying.",

            BridgeState.PermissionDenied =>
                string.IsNullOrEmpty(reason)
                    ? "Snowcloak has not granted Sticker Chat permission. Allow it under Snowcloak's own " +
                      "settings, in its plugin integrations window."
                    : $"Snowcloak declined: {reason}. Grant it under Snowcloak's plugin integrations window.",

            BridgeState.Registered when !Connected =>
                "Registered with Snowcloak, but it is not connected to its server.",

            BridgeState.Registered =>
                "Connected to Snowcloak.",

            _ => "Unknown state.",
        };

        service.TransportStatus = Status;
        service.TransportReady = State == BridgeState.Registered && Connected;
    }

    /// <summary>Opens Snowcloak's own integrations window, where permission is granted.</summary>
    /// <returns>False when Snowcloak could not be asked.</returns>
    public bool OpenPermissionsWindow()
    {
        try
        {
            return ipc?.OpenPluginIntegrations().Success ?? false;
        }
        catch (Exception ex)
        {
            Services.Log.Warning(ex, "Could not open Snowcloak's integrations window");
            return false;
        }
    }

    public void Dispose()
    {
        disposed = true;

        try
        {
            Unsubscribe();

            // Withdraw what we advertised rather than leaving a stale pointer for pairs to act on.
            if (State == BridgeState.Registered)
            {
                ipc?.SetLocalData(string.Empty);
                ipc?.UnregisterExtension();
            }

            ipc?.Dispose();
        }
        catch (Exception ex)
        {
            Services.Log.Warning(ex, "Snowcloak bridge shutdown was not clean");
        }

        ipc = null;
    }
}
