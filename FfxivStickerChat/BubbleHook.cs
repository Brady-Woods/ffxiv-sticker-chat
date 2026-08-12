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

/// <summary>A bubble the game is about to open that has a sticker waiting for it.</summary>
/// <param name="MatchText">
/// The message as the bubble will render it, normalised. Used to tie this specific message to the
/// bubble that shows it.
/// </param>
public sealed record PendingSticker(
    string Sender,
    ushort WorldId,
    ushort LogKindId,
    bool IsLocalPlayer,
    string MatchText,
    string ImagePath,
    DateTime Expires);

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

    /// <summary>
    /// Messages with a sticker, waiting for their bubble to appear.
    /// </summary>
    /// <remarks>
    /// Only messages that actually resolve to a sticker are tracked. An earlier version queued every
    /// bubble the game opened, so ordinary chatter filled the queue and the entry for a real sticker was
    /// evicted or claimed by an unrelated bubble before it could be used.
    /// </remarks>
    private readonly List<PendingSticker> pending = [];

    /// <summary>How long a message waits for its bubble before being discarded.</summary>
    private static readonly TimeSpan PendingLifetime = TimeSpan.FromSeconds(10);

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

    /// <summary>
    /// Claims the sticker for a bubble showing <paramref name="bubbleText"/>, or null if none matches.
    /// </summary>
    /// <remarks>
    /// Matching on the rendered text ties a bubble to the message that produced it, rather than assuming
    /// bubbles open in the same order the game announces them. Whether a sticker exists at all is still
    /// decided by auto-translate id, so text is only used to pick which bubble — never to decide that
    /// something is a sticker.
    /// </remarks>
    public string? Peek(string bubbleText)
    {
        Expire();

        if (string.IsNullOrEmpty(bubbleText))
            return null;

        foreach (var candidate in pending)
        {
            if (string.Equals(candidate.MatchText, bubbleText, StringComparison.Ordinal))
                return candidate.ImagePath;
        }

        return null;
    }

    /// <summary>
    /// Drops a pending sticker once its bubble has actually been decorated.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="Peek"/> on purpose. Consuming at match time loses the sticker whenever
    /// the first attempt fails — most importantly while the texture is still decoding, which is exactly
    /// the case the first time any given image is used. Keeping the entry until the apply succeeds lets
    /// the next frame retry.
    /// </remarks>
    public void Consume(string bubbleText)
    {
        for (var i = 0; i < pending.Count; i++)
        {
            if (!string.Equals(pending[i].MatchText, bubbleText, StringComparison.Ordinal))
                continue;

            pending.RemoveAt(i);
            return;
        }
    }

    /// <summary>The texts currently waiting, for diagnosing a failed match.</summary>
    public IReadOnlyList<string> PendingTexts
    {
        get
        {
            Expire();
            return pending.ConvertAll(p => p.MatchText);
        }
    }

    /// <summary>Number of messages still waiting for a bubble.</summary>
    public int PendingCount
    {
        get
        {
            Expire();
            return pending.Count;
        }
    }

    public void Clear() => pending.Clear();

    private void Expire()
    {
        var now = DateTime.UtcNow;
        pending.RemoveAll(p => p.Expires < now);
    }

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
        var matchText = string.Empty;

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
                // How the bubble will render this message, so the right bubble can be identified later.
                matchText = AutoTranslateDetector.Normalize(parsed.TextValue);

                if (channelAllowed)
                    imagePath = packs.Resolve(first.Group, first.Key, matchText, senderName, worldId);

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

        // Only messages with a sticker are worth tracking. Everything else is ordinary chat and must not
        // occupy a slot the real thing needs.
        if (imagePath is null)
            return;

        Expire();

        pending.Add(new PendingSticker(
            senderName,
            worldId,
            logKindId,
            isLocalPlayer,
            matchText,
            imagePath,
            DateTime.UtcNow + PendingLifetime));

        // Bounded even so: a message whose bubble never appears would otherwise linger until it expires.
        while (pending.Count > 16)
            pending.RemoveAt(0);
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
        pending.Clear();
    }
}
