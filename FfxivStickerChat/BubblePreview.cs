using System;
using Dalamud.Game.Text;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using FFXIVClientStructs.FFXIV.Client.System.String;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;

namespace FfxivStickerChat;

/// <summary>
/// Shows a chat bubble on your own character to preview a binding.
/// </summary>
/// <remarks>
/// <para>
/// <c>ShowMiniTalkPlayer</c> is the function the game calls to put a bubble on screen. Calling it
/// directly renders a bubble locally and sends nothing — it is not a chat message, no packet leaves the
/// client, and nobody else sees it. That keeps sending strictly human-driven while still letting a
/// binding be checked.
/// </para>
/// <para>
/// The preview goes through the same path a real message does: the message carries a genuine
/// auto-translate payload, so the hook decodes it, resolves it against packs, and the decorator applies
/// the result exactly as it would for someone else's bubble. A preview that rendered the image directly
/// would prove nothing about whether the binding works.
/// </para>
/// </remarks>
public static unsafe class BubblePreview
{
    /// <summary>Shows a bubble for a phrase on the local character.</summary>
    /// <returns>An error message, or empty on success.</returns>
    public static string Show(uint group, uint key, XivChatType channel = XivChatType.Say)
    {
        var module = RaptureLogModule.Instance();
        if (module is null)
            return "Chat module unavailable.";

        var name = Services.PlayerState.CharacterName;
        if (string.IsNullOrEmpty(name))
            return "You need to be logged in to preview.";

        Utf8String* sender = null;
        Utf8String* message = null;

        try
        {
            // A real auto-translate payload, so the hook sees the same bytes it would from another
            // player rather than a plain string that could never match.
            var encoded = new SeString(new AutoTranslatePayload(group, key)).Encode();

            sender = Utf8String.FromString(name);
            message = Utf8String.FromSequence(encoded);

            if (sender is null || message is null)
                return "Could not allocate the preview message.";

            module->ShowMiniTalkPlayer(
                (ushort)channel,
                sender,
                message,
                (ushort)Services.PlayerState.HomeWorld.RowId,
                isLocalPlayer: true);

            return string.Empty;
        }
        catch (Exception ex)
        {
            Services.Log.Error(ex, "Bubble preview failed");
            return $"Preview failed: {ex.Message}";
        }
        finally
        {
            // The game copies what it needs, so these are ours to release either way.
            if (sender is not null)
                sender->Dtor(true);

            if (message is not null)
                message->Dtor(true);
        }
    }
}
