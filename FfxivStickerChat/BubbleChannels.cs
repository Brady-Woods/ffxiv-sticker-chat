using System;
using System.Collections.Generic;
using Dalamud.Game.Text;

namespace FfxivStickerChat;

/// <summary>A chat channel that can produce a bubble, paired with the game setting that controls it.</summary>
/// <param name="Type">The log kind the bubble hook reports.</param>
/// <param name="Label">Human name for the UI.</param>
/// <param name="ShowTypeOption">
/// Name of the game's own per-channel bubble setting, or empty when there isn't one.
/// </param>
public sealed record BubbleChannel(XivChatType Type, string Label, string ShowTypeOption)
{
    public ushort Id => (ushort)Type;
}

/// <summary>
/// The chat channels the game is willing to draw bubbles for.
/// </summary>
/// <remarks>
/// <para>
/// Patch 7.3 gave every channel its own bubble setting — <c>LogChatBubbleFcShowType</c> and friends. A
/// channel switched off there produces no bubble at all, so this plugin never hears about it: our hook
/// only fires once the game has decided to show one.
/// </para>
/// <para>
/// That distinction is worth surfacing rather than hiding. A channel enabled here but disabled in the
/// game will silently never fire, and without seeing both switches together that looks like a bug.
/// </para>
/// </remarks>
public static class BubbleChannels
{
    public static readonly IReadOnlyList<BubbleChannel> All =
    [
        new(XivChatType.Say, "Say", "LogChatBubbleSayShowType"),
        new(XivChatType.Yell, "Yell", "LogChatBubbleYellShowType"),
        new(XivChatType.Shout, "Shout", "LogChatBubbleShoutShowType"),
        new(XivChatType.TellIncoming, "Tell (incoming)", "LogChatBubbleTellShowType"),
        new(XivChatType.TellOutgoing, "Tell (outgoing)", "LogChatBubbleTellShowType"),
        new(XivChatType.Party, "Party", "LogChatBubblePartyShowType"),
        new(XivChatType.CrossParty, "Cross-world party", "LogChatBubblePartyShowType"),
        new(XivChatType.Alliance, "Alliance", "LogChatBubbleAllianceShowType"),
        new(XivChatType.FreeCompany, "Free Company", "LogChatBubbleFcShowType"),
        new(XivChatType.NoviceNetwork, "Novice Network", "LogChatBubbleBeginnerShowType"),
        new(XivChatType.PvPTeam, "PvP Team", "LogChatBubblePvpteamShowType"),

        new(XivChatType.Ls1, "Linkshell 1", "LogChatBubbleLs1ShowType"),
        new(XivChatType.Ls2, "Linkshell 2", "LogChatBubbleLs2ShowType"),
        new(XivChatType.Ls3, "Linkshell 3", "LogChatBubbleLs3ShowType"),
        new(XivChatType.Ls4, "Linkshell 4", "LogChatBubbleLs4ShowType"),
        new(XivChatType.Ls5, "Linkshell 5", "LogChatBubbleLs5ShowType"),
        new(XivChatType.Ls6, "Linkshell 6", "LogChatBubbleLs6ShowType"),
        new(XivChatType.Ls7, "Linkshell 7", "LogChatBubbleLs7ShowType"),
        new(XivChatType.Ls8, "Linkshell 8", "LogChatBubbleLs8ShowType"),

        new(XivChatType.CrossLinkShell1, "CWLS 1", "LogChatBubbleCwls1ShowType"),
        new(XivChatType.CrossLinkShell2, "CWLS 2", "LogChatBubbleCwls2ShowType"),
        new(XivChatType.CrossLinkShell3, "CWLS 3", "LogChatBubbleCwls3ShowType"),
        new(XivChatType.CrossLinkShell4, "CWLS 4", "LogChatBubbleCwls4ShowType"),
        new(XivChatType.CrossLinkShell5, "CWLS 5", "LogChatBubbleCwls5ShowType"),
        new(XivChatType.CrossLinkShell6, "CWLS 6", "LogChatBubbleCwls6ShowType"),
        new(XivChatType.CrossLinkShell7, "CWLS 7", "LogChatBubbleCwls7ShowType"),
        new(XivChatType.CrossLinkShell8, "CWLS 8", "LogChatBubbleCwls8ShowType"),
    ];

    /// <summary>Whether a log kind appears in the curated list above.</summary>
    /// <remarks>
    /// The list is hand-written from Dalamud's <see cref="XivChatType"/> and is demonstrably incomplete —
    /// the game emits bubbles on log kinds that enum does not name, such as 33. Anything unrecognised is
    /// therefore allowed rather than dropped, so an unknown channel behaves as it did before filtering
    /// existed.
    /// </remarks>
    public static bool IsKnown(ushort logKindId)
    {
        foreach (var channel in All)
        {
            if (channel.Id == logKindId)
                return true;
        }

        return false;
    }

    /// <summary>Log kinds seen this session that the curated list does not name.</summary>
    public static IReadOnlyCollection<ushort> UnknownSeen => unknownSeen;

    private static readonly HashSet<ushort> unknownSeen = [];

    /// <summary>Records an unrecognised log kind so the UI can offer a toggle for it.</summary>
    public static void NoteUnknown(ushort logKindId)
    {
        if (IsKnown(logKindId))
            return;

        if (unknownSeen.Add(logKindId))
            Services.Log.Information($"Saw an unrecognised chat log kind: {logKindId} (allowed by default).");
    }

    public static string Describe(ushort logKindId)
    {
        foreach (var channel in All)
        {
            if (channel.Id == logKindId)
                return channel.Label;
        }

        return $"log kind {logKindId}";
    }

    /// <summary>
    /// Whether the game itself will draw a bubble for this channel.
    /// </summary>
    /// <returns>
    /// <see langword="null"/> when the setting could not be read, so the UI can say "unknown" rather
    /// than claim a channel is off.
    /// </returns>
    public static bool? IsEnabledInGame(BubbleChannel channel)
    {
        if (string.IsNullOrEmpty(channel.ShowTypeOption))
            return null;

        try
        {
            if (!Services.GameConfig.UiConfig.TryGetUInt(channel.ShowTypeOption, out var showType))
                return null;

            // 0 means the channel never shows a bubble; anything else is one of the display modes.
            return showType != 0;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
