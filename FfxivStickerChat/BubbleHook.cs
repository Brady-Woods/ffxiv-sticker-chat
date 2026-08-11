using System;
using System.Collections.Generic;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Hooking;
using FFXIVClientStructs.FFXIV.Client.System.String;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using FfxivStickerChat.Packs;

namespace FfxivStickerChat;

/// <summary>An auto-translate phrase seen going past, offered in the config UI for one-click binding.</summary>
public sealed record SeenPhrase(uint Group, uint Key, string Text, string Sender, DateTime SeenAt);

/// <summary>One chat bubble the game is about to open, and the sticker it should carry.</summary>
public sealed record BubbleCreation(
    string Sender,
    ushort WorldId,
    ushort LogKindId,
    bool IsLocalPlayer,
    string? ImagePath);

/// <summary>
/// Intercepts bubble creation to learn who is speaking and what they actually said.
/// </summary>
/// <remarks>
/// <para>
/// Matching on the bubble's rendered text cannot work correctly. By the time text reaches a bubble the
/// game has already expanded auto-translate into an icon-plus-literal-text payload, so the
/// <c>(Group, Key)</c> pair is gone — anyone typing the same words by hand looks identical, and every
/// bubble showing those words matches.
/// </para>
/// <para>
/// <c>ShowMiniTalkPlayer</c> runs before that expansion. It hands over the sender, their world, whether
/// they are the local player, and the message with its payloads intact, which is where the auto-translate
/// ids still exist. That makes matching exact and per-speaker.
/// </para>
/// </remarks>
public sealed unsafe class BubbleHook : IDisposable
{
    private readonly Configuration config;
    private readonly PackStore packs;
    private readonly Hook<RaptureLogModule.Delegates.ShowMiniTalkPlayer>? hook;

    /// <summary>Bubbles opened since the last frame, oldest first.</summary>
    private readonly Queue<BubbleCreation> queued = new();

    /// <summary>Auto-translate phrases seen this session, newest first.</summary>
    private readonly List<SeenPhrase> seen = [];

    private const int MaxSeenPhrases = 40;

    private bool disposed;

    public BubbleHook(Configuration config, PackStore packs)
    {
        this.config = config;
        this.packs = packs;

        try
        {
            hook = Services.GameInterop.HookFromAddress<RaptureLogModule.Delegates.ShowMiniTalkPlayer>(
                RaptureLogModule.MemberFunctionPointers.ShowMiniTalkPlayer,
                Detour);

            hook.Enable();
            Services.Log.Information("Hooked ShowMiniTalkPlayer.");
        }
        catch (Exception ex)
        {
            Services.Log.Error(ex, "Could not hook ShowMiniTalkPlayer; per-speaker matching is unavailable");
        }
    }

    public bool IsActive => hook is { IsEnabled: true };

    /// <summary>
    /// Auto-translate phrases observed this session, newest first.
    /// </summary>
    /// <remarks>
    /// Sourced here rather than from the chat log: this hook already sees every message that produces a
    /// bubble, with its group/key intact, so binding candidates come for free without the plugin needing
    /// to read chat at all.
    /// </remarks>
    public IReadOnlyList<SeenPhrase> Seen => seen;

    /// <summary>Takes the oldest pending bubble, or null when none are waiting.</summary>
    public BubbleCreation? Dequeue() => queued.Count > 0 ? queued.Dequeue() : null;

    public void Clear() => queued.Clear();

    private void Detour(
        RaptureLogModule* thisPtr,
        ushort logKindId,
        Utf8String* sender,
        Utf8String* message,
        ushort worldId,
        bool isLocalPlayer)
    {
        try
        {
            Inspect(logKindId, sender, message, worldId, isLocalPlayer);
        }
        catch (Exception ex)
        {
            Services.Log.Error(ex, "ShowMiniTalkPlayer detour failed");
        }

        hook!.Original(thisPtr, logKindId, sender, message, worldId, isLocalPlayer);
    }

    private void Inspect(ushort logKindId, Utf8String* sender, Utf8String* message, ushort worldId, bool isLocalPlayer)
    {
        if (disposed || !config.Enabled || message is null)
            return;

        var senderName = sender is null ? string.Empty : sender->ToString();

        if (config.OnlyLocalPlayer && !isLocalPlayer)
            return;

        // The channel filter still records the phrase as seen, so a message on a disabled channel can
        // be bound without having to switch the channel on first.
        BubbleChannels.NoteUnknown(logKindId);
        var channelAllowed = config.IsChannelEnabled(logKindId);

        string? imagePath = null;

        var span = message->AsSpan();

        if (config.VerboseLogging)
        {
            // Log every call, not just ones that match. Otherwise a message that never reaches the hook
            // and a message the hook rejects look identical from the log — which is exactly the gap when
            // one way of sending works and another does not.
            Services.Log.Information(
                $"ShowMiniTalkPlayer: kind={logKindId} ({BubbleChannels.Describe(logKindId)}) " +
                $"sender=\"{senderName}\" local={isLocalPlayer} world={worldId} " +
                $"bytes={span.Length} raw={Describe(span)}");
        }

        if (!span.IsEmpty)
        {
            var parsed = SeString.Parse(span.ToArray());

            if (AutoTranslateDetector.TryGetTokens(parsed, out var tokens))
            {
                var first = tokens[0];

                // Match on the auto-translate id, not the rendered words: the pair is language
                // independent and cannot be reproduced by typing. Packs are consulted in priority order
                // and may be scoped to a specific sender, which is how an imported pack follows its owner.
                if (channelAllowed)
                    imagePath = packs.Resolve(first.Group, first.Key, senderName, worldId);

                RecordSeen(first.Group, first.Key, first.Text, senderName);

                if (config.VerboseLogging)
                {
                    Services.Log.Information(
                        $"Bubble for {senderName} on {BubbleChannels.Describe(logKindId)} " +
                        $"(local={isLocalPlayer}): auto-translate group={first.Group} key={first.Key} -> " +
                        $"{(!channelAllowed ? "channel disabled" : imagePath ?? "no binding")}");
                }
            }
            else if (config.VerboseLogging)
            {
                Services.Log.Information(
                    $"  not auto-translate only: payloads=[{string.Join(", ", parsed.Payloads.ConvertAll(x => x.Type.ToString()))}]");
            }
        }

        queued.Enqueue(new BubbleCreation(senderName, worldId, logKindId, isLocalPlayer, imagePath));

        // A bubble the game opens but we never claim would leave a stale entry ahead of the next real
        // one, so the queue is bounded and drained every frame.
        while (queued.Count > 16)
            queued.Dequeue();
    }

    /// <summary>Readable preview of a message's raw bytes, so payload structure is visible.</summary>
    private static string Describe(ReadOnlySpan<byte> span)
    {
        if (span.IsEmpty)
            return "<empty>";

        var builder = new System.Text.StringBuilder();

        for (var i = 0; i < span.Length && i < 48; i++)
        {
            var b = span[i];

            // Printable ASCII as-is; everything else as hex, since payload markers are what matter here.
            if (b is >= 0x20 and < 0x7F)
                builder.Append((char)b);
            else
                builder.Append('[').Append(b.ToString("X2")).Append(']');
        }

        if (span.Length > 48)
            builder.Append("...");

        return builder.ToString();
    }

    private void RecordSeen(uint group, uint key, string payloadText, string sender)
    {
        // Prefer the catalogue's clean text; it also cross-checks that the sheet's group/key numbering
        // lines up with what the payload decoder reports.
        var text = AutoTranslateCatalog.Find(group, key)?.Text ?? payloadText;

        seen.RemoveAll(p => p.Group == group && p.Key == key);
        seen.Insert(0, new SeenPhrase(group, key, text, sender, DateTime.Now));

        if (seen.Count > MaxSeenPhrases)
            seen.RemoveRange(MaxSeenPhrases, seen.Count - MaxSeenPhrases);
    }

    public void Dispose()
    {
        if (disposed)
            return;

        disposed = true;
        hook?.Disable();
        hook?.Dispose();
        queued.Clear();
    }
}
