using System.Collections.Generic;
using System.Linq;
using System.Text;
using Dalamud.Game.Text;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;

namespace FfxivStickerChat;

/// <summary>
/// Identifies chat messages whose entire visible content is auto-translate tokens.
/// </summary>
/// <remarks>
/// Auto-translate is a good sticker transport because <see cref="AutoTranslatePayload.Group"/> and
/// <see cref="AutoTranslatePayload.Key"/> are language-independent integers: the same phrase produces the
/// same pair on a JP client as on an EN one. Players without this plugin just see the phrase as normal text.
/// </remarks>
public static class AutoTranslateDetector
{
    /// <summary>
    /// Returns the auto-translate tokens in <paramref name="message"/> if — and only if — the message
    /// contains at least one token and no other visible content.
    /// </summary>
    /// <remarks>
    /// Formatting payloads (italics, colour) and whitespace-only text are tolerated, since the game
    /// inserts them around emotes and channel decoration. Any real text disqualifies the message.
    /// </remarks>
    public static bool TryGetTokens(SeString message, out IReadOnlyList<AutoTranslatePayload> tokens)
    {
        var found = new List<AutoTranslatePayload>();

        foreach (var payload in message.Payloads)
        {
            switch (payload)
            {
                case AutoTranslatePayload autoTranslate:
                    found.Add(autoTranslate);
                    break;

                // Whitespace between tokens is expected; anything else printable is not.
                case TextPayload text when string.IsNullOrWhiteSpace(StripDecoration(text.Text)):
                    break;

                // Pure formatting carries no visible glyphs.
                case EmphasisItalicPayload:
                case UIForegroundPayload:
                case UIGlowPayload:
                    break;

                default:
                    tokens = [];
                    return false;
            }
        }

        tokens = found;
        return found.Count > 0;
    }

    /// <summary>
    /// Normalises a rendered auto-translate string for comparison against a chat bubble's text node.
    /// </summary>
    /// <remarks>
    /// <see cref="AutoTranslatePayload.Text"/> wraps its phrase in the private-use bracket glyphs
    /// <see cref="SeIconChar.AutoTranslateOpen"/>/<see cref="SeIconChar.AutoTranslateClose"/>. The bubble
    /// renders the same glyphs, but padding around them is not worth trusting — strip both and compare cores.
    /// </remarks>
    public static string Normalize(string? raw)
    {
        if (string.IsNullOrEmpty(raw))
            return string.Empty;

        var builder = new StringBuilder(raw.Length);
        foreach (var c in raw)
        {
            if (c == (char)SeIconChar.AutoTranslateOpen || c == (char)SeIconChar.AutoTranslateClose)
                continue;
            builder.Append(c);
        }

        // Collapse internal runs of whitespace so " Good  Morning " and "Good Morning" match.
        return string.Join(' ', builder.ToString().Split((char[]?)null, System.StringSplitOptions.RemoveEmptyEntries));
    }

    private static string StripDecoration(string? text)
        => text is null ? string.Empty : Normalize(text);
}
