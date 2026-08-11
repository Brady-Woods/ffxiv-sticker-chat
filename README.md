# Sticker Chat

A Dalamud plugin proof of concept: when someone sends a chat message made up **only** of auto-translate
phrases, replace their chat bubble with a sticker image.

Receive-only. This plugin never sends chat — you send auto-translate yourself, the normal way.

## Why auto-translate

Auto-translate tokens decode to a `(Group, Key)` integer pair that is identical on every client regardless
of game language. That makes them a clean, language-independent transport for a sticker ID. Players
*without* the plugin see an ordinary auto-translate phrase rather than garbage, so the degradation is
graceful.

The binding is 1:1 — one phrase, one sticker.

## Status

| Piece | State |
|---|---|
| Auto-translate detection | Works. `AutoTranslateDetector` + the Dalamud 15 `IChatGui.ChatMessage` API. |
| Sticker loading | Works. Disk PNG → `ConvertToKernelTexture` → native `Texture*`. |
| Config / binding UI | Works. Binds from a list of phrases actually seen in chat. |
| Bubble inspector | Works. Read-only. |
| Bubble replacement | **Unverified — needs an in-game pass.** See below. |

## The thing to verify first

Patch 7.3 (Aug 2025) added native chat bubbles and split them across two addons:

- `_MiniTalk` — NPC speech balloons. Mapped in FFXIVClientStructs as `AddonMiniTalk`.
- `MiniTalkPlayer` — **player** chat bubbles. No struct exists for it anywhere.

Everything this plugin cares about is in `MiniTalkPlayer`, and no public node-ID map exists for its ULD.
`MiniTalk.cs` therefore finds nodes by **type** rather than by ID, because the one published ID data point
for each addon disagrees about what ID 4 means.

Two things need eyes on them in-game. Open the inspector with `/stickerchat debug`:

1. **What is the image node?** Best evidence says it's the bubble's tail — a 32×32 sprite at texture
   coordinates near `(0, 992)` that slides horizontally to point at the speaker. The inspector flags a part
   matching that fingerprint. If it matches, `BubbleDecorator` is currently stretching the tail into a
   sticker, which works but is a bit of a hack; the clean fix is allocating our own node.
   If the node has **no** PartsList, it's spare and taking it is free.

2. **Does the balloon queue cover player bubbles?** `AgentScreenLog.BalloonQueue` carries an `ObjectId`,
   which would give per-actor targeting with no signature hook. FFXIVClientStructs flags the
   slot↔bubble correspondence as unverified. If the queue stays empty while player bubbles are on screen,
   that route is NPC-only and per-actor work needs a hook on `RaptureLogModule.ShowMiniTalkPlayer`
   (fully signatured, gives sender/message/world).

Until #1 is confirmed, treat sticker rendering as a hypothesis, not a feature.

## Design notes

**Matching is by text, not by actor.** Since phrase↔sticker is 1:1, the bubble's rendered text alone
identifies the sticker. That deliberately sidesteps mapping a bubble back to a `GameObject` — a mapping
the game doesn't expose cleanly. The tradeoff: two people sending the same sticker at once are
indistinguishable, which is harmless because they'd get the same sticker anyway.

**Every edit is reversible.** Before overwriting a node we snapshot the texture union pointer, its
`TextureType`, the part rect, and node geometry/visibility, and restore all of it when the bubble is
recycled or the plugin unloads. We never call `ReleaseTexture` on a texture the game owns.

**Known hazard:** `AtkUldAsset`s are shared across an addon's parts, so retargeting one bubble's image may
change the same element in other simultaneously-visible bubbles. Blast radius is small and fully undone on
restore. The clean fix is allocating our own `AtkUldPartsList` and node — see
[KamiToolKit](https://github.com/MidoriKami/KamiToolKit), whose `ImGuiImageNode` does exactly this, and
Ktisis's `BalloonNode.cs` for a working chat balloon built that way.

## Installing

### Via custom repository

Dalamud settings → **Experimental** → *Custom Plugin Repositories* → add:

```
https://raw.githubusercontent.com/bradywoods/ffxiv-sticker-chat/main/repo.json
```

> ⚠️ **Change the owner/repo first.** `repo.json`, the workflows, and `FfxivStickerChat.csproj` all say
> `bradywoods/ffxiv-sticker-chat` as a placeholder. Update those three before publishing, or the download
> links resolve to nothing.

The download links point at `/releases/latest/download/latest.zip`, so they never need editing — cutting a
release is enough:

```
git tag v0.0.0.2 && git push origin v0.0.0.2
```

That builds, attaches `latest.zip` to a GitHub release, and commits the new `AssemblyVersion` /
`LastUpdate` back into `repo.json`.

### Via dev plugin (no repo needed)

Faster for iterating. Build, then in Dalamud settings → **Experimental** → *Dev Plugin Locations*, add the
folder containing `FfxivStickerChat.dll`:

```
FfxivStickerChat\bin\x64\Debug\
```

`DalamudPackager` also writes a ready-to-ship bundle to
`FfxivStickerChat\bin\x64\Release\FfxivStickerChat\latest.zip` on a Release build.

## Building

Requires **Windows**, .NET 10 SDK, and XIVLauncher/Dalamud installed (the SDK resolves game assemblies
from `%APPDATA%\XIVLauncher\addon\Hooks\dev\`).

```
dotnet build -c Debug
```

Then add `bin\x64\Debug\FfxivStickerChat.json` as a dev plugin in Dalamud settings.

Targets `Dalamud.NET.Sdk/15.0.0` (API 15). Compiles clean (0 errors, 0 warnings) against the Dalamud 15
dev distribution.

If the SDK can't find your Dalamud install, point it at one explicitly:

```
dotnet build -c Debug -p:DalamudLibPath=/path/to/dalamud/Hooks/dev/
```

## Usage

1. Drop PNGs somewhere on disk.
2. Have someone send an auto-translate-only message (or send one yourself).
3. `/stickerchat` → the phrase appears under "seen this session" → **Bind** → paste the image path.
4. Next time that phrase is sent, the bubble becomes the sticker.

`/stickerchat debug` opens the inspector.

## Limitations inherited from native bubbles

Native bubbles are suppressed in combat, during PvP, and while using performance actions. Stickers go
quiet in those contexts, and that can't be overridden without either patching the game's check or
abandoning native bubbles for an ImGui overlay.

## Layout

```
FfxivStickerChat/
  Plugin.cs                  wiring, chat handler, seen-phrase log
  Services.cs                Dalamud service container
  Configuration.cs           settings + phrase→image bindings
  AutoTranslateDetector.cs   "is this message only auto-translate?"
  StickerRegistry.cs         PNG → native Texture*, cached, async
  MiniTalk.cs                addon/node discovery by type
  BubbleDecorator.cs         apply + restore
  Windows/ConfigWindow.cs
  Windows/DebugWindow.cs     the inspector
```

## Prior art

[Haplo064/ChatBubbles](https://github.com/Haplo064/ChatBubbles) — retired July 2025, days before 7.3 made
it redundant. Worth reading for its per-frame node manipulation, but don't fork it: its struct offsets are
stale, it targets API 12, and the balloon-hijacking machinery that made it clever is obsolete now that the
game spawns player bubbles natively.
