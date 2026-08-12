# Sticker Chat

A Dalamud plugin for FFXIV. When someone sends a chat message made up **only** of auto-translate phrases,
their chat bubble is replaced with a sticker image.

**Receive-only.** The plugin never sends chat and never reads your chat log — you send auto-translate
yourself, the normal way.

> **Alpha.** Bubble replacement works and is confirmed in-game. Expect rough edges, and see
> [Not done yet](#not-done-yet).

## Why auto-translate

An auto-translate phrase decodes to a `(Group, Key)` integer pair that is identical on every client
regardless of game language. That makes it a clean, language-independent id for a sticker.

Matching is on that id, never on the words — so typing the same text by hand does nothing. Players
*without* the plugin just see an ordinary auto-translate phrase, so the degradation is graceful.

## How it works

The plugin hooks `RaptureLogModule.ShowMiniTalkPlayer`, which the game calls to open a chat bubble. That
runs *before* the game expands auto-translate into brackets-and-text, so the `(Group, Key)` pair is still
intact — and it supplies the sender, their world, and whether they are the local player.

Rendering reuses an `AtkImageNode` already inside the bubble: the node is pointed at a private one-part
list bound to the sticker texture, resized, and the bubble's text hidden. Every field touched is
snapshotted first and restored when the bubble is recycled or the plugin unloads.

Patch 7.3 split bubbles across two addons — `_MiniTalk` for NPC balloons and **`MiniTalkPlayer`** for
player chat. The latter has no struct in FFXIVClientStructs and no published node-ID map, so nodes are
found by *type* rather than by id.

## Sticker packs

A pack is the unit of sharing: a name, an owner, and a set of phrase→image bindings.

```
pluginConfigs/FfxivStickerChat/
  packs/<packId>/pack.json
  packs/<packId>/media/<sha256>.png
```

Each pack owns its media, so deleting one is a single directory removal with nothing orphaned. Images are
named by content hash, so adding the same image twice stores it once.

**Ownership is stamped, not chosen.** A pack records the character it was created on and carries that
through export, so importing a friend's pack pairs it to them automatically — and nobody can retarget
someone else's pack at an arbitrary character. Imported packs are read-only; you only author your own.

### Limits

Following [Telegram's static sticker spec](https://core.telegram.org/stickers), so art made for Telegram
works here unmodified:

| | |
|---|---|
| Format | `.png` |
| Size | one side exactly 512px, the other ≤512px |
| File | ≤512 KB |
| Per pack | ≤120 stickers |

PNG only. Telegram also allows WebP, but whether Dalamud decodes WebP under Proton could not be verified,
and a format that validates then silently fails to render is worse than one refused up front.

### Sharing

Export writes a zip; import reads one, either from disk or from an https URL. A pack can carry a
**download URL**, which travels with it on export — so recipients pull your updates themselves instead of
you sending the file again.

An archive is untrusted input, and so is the URL. Fetching is https only, redirects are followed manually
and re-checked at each hop, hosts resolving to private or loopback addresses are refused, the download is
capped while streaming, and the payload must actually be a zip. Passing that only gets the bytes as far
as the importer, which independently re-validates every entry — paths confined to `media/`, sizes capped,
and each file hashed against the name it claims.

The manifest gets the same treatment, because it decides where files land and how the pack behaves:

- **The pack id must be a plain 32-hex-digit GUID.** It names a folder, and `Path.Combine` discards its
  base directory when handed a rooted path — so an id of `C:\Windows\Temp\evil` would write outside the
  store entirely. Requiring the exact generated form rejects every such shape rather than blocklisting
  the ones somebody thought of.
- **Priority is assigned locally, never read from the archive**, so an imported pack cannot outrank the
  packs you already have.
- **An import can never replace a pack you authored**, even one claiming the same id.
- **A pack must name an owner.** An unowned pack matches every speaker, which would put its stickers
  over everyone's head.

Share links point at a web page rather than the file, so they are rewritten to the host's direct
download form automatically — paste the link you'd normally send someone:

| host | works |
|---|---|
| GitHub release asset | yes, already direct |
| Google Drive | yes, rewritten |
| Dropbox | yes, rewritten |
| OneDrive / SharePoint | yes, rewritten |
| **Proton Drive** | **no** |

Proton Drive share links are end-to-end encrypted: the decryption key lives in the URL fragment, which
browsers never send to the server, and only their web app can decrypt the file. That is a property of the
encryption, not something more code can work around — export the zip and host it elsewhere.

Very large packs on Google Drive can hit its virus-scan interstitial, which serves a page instead of the
file. The plugin says so rather than failing obscurely.

## Installing

### Custom repository

Dalamud → **Settings → Experimental → Custom Plugin Repositories**, add:

```
https://raw.githubusercontent.com/Brady-Woods/ffxiv-sticker-chat/main/repo.json
```

Save, and **Sticker Chat** appears in `/xlplugins`. It is an alpha — the 0.x version and the `-alpha`
release tag say so — but it is not gated behind Dalamud's testing-builds toggle, which would otherwise
hide it unless you had that enabled.

### Dev plugin

Build, then add the folder containing `FfxivStickerChat.dll` under **Dev Plugin Locations**.

## Usage

1. `/stickerchat` → **Packs** tab → your pack is created automatically.
2. **Add sticker** → pick a phrase from the dropdown → **Choose image**.
3. **Preview** shows the sticker on your own character. Local only; nothing is sent.
4. Send that auto-translate phrase in chat.

`/stickerchat debug` opens a node inspector for the bubble addons.

### If a sticker doesn't appear

The game has its **own** per-channel bubble settings, and a channel disabled there produces no bubble at
all — so the plugin never sees the message. Check **Character Configuration → Log Window Settings → Chat
Bubbles**. The Channels list shows both switches side by side for exactly this reason.

Native bubbles are also suppressed during combat, in PvP, and while using performance actions.

## Building

Requires the .NET 10 SDK and a Dalamud install. Targets `Dalamud.NET.Sdk/15.0.0` (API level 15).

```
dotnet build -c Debug
```

If the SDK can't locate Dalamud, point it at one explicitly — useful on Linux, where XIVLauncher.Core
keeps it under `~/.xlcore`:

```
dotnet build -c Debug -p:DalamudLibPath="$HOME/.xlcore/dalamud/Hooks/dev/"
```

A Release build also produces `bin/x64/Release/FfxivStickerChat/latest.zip`, the artifact published to
releases.

## Releasing

```
git tag v0.1.0-alpha.3 && git push origin v0.1.0-alpha.3
```

That builds, attaches `latest.zip` to a GitHub release, and commits the new version back into `repo.json`.

Note that Dalamud requires a four-part numeric `AssemblyVersion`, which cannot carry a semver prerelease
identifier. The assembly is versioned `0.1.0.0`; the `-alpha.N` lives on the git tag and the release.

## Not done yet

- **Syncing packs between players.** Snowcloak exposes a third-party extension API, but it allows 4 KB per
  plugin — enough for a manifest, nowhere near enough for images. Manual zip export/import works today.
- **A default sticker pack.** Job icons would mean redistributing Square Enix artwork; the intended route
  is referencing the icons already in the player's own game install.
- **Chat log replacement.** The log is a single text node with no per-line geometry, so inline images
  would mean reconstructing layout the game doesn't expose.

## Prior art

[Haplo064/ChatBubbles](https://github.com/Haplo064/ChatBubbles) — retired July 2025, days before patch 7.3
made it redundant. Worth reading for its per-frame node manipulation, though its struct offsets are stale
and the balloon-triggering machinery is obsolete now that the game spawns player bubbles natively.

## Licence

AGPL-3.0-or-later.
