# One-click play world

`CoreAI/Setup/Create Play + Create World` adds one GameObject, **CoreAI Play World**, to the open scene. Press
Play and you stand on a baseplate in a Roblox-style world: walk with WASD (or the left stick), jump with Space,
double-tap Space to fly, turn the third-person camera with the right mouse button and zoom with the wheel.
Press **T** or **Enter** to talk to the AI, **/** to type a command. Everything is composed at runtime by
components, so a built player (WebGL included) gets the same world as the Editor.

## What the menu adds

| Component | Slot | What it does |
|---|---|---|
| `CoreAiPlayWorldBootstrap` | - | Creates what the scene lacks (camera, sun, `CoreAILifetimeScope`, `CoreAiModsLifetimeScope`, `RbxWorldHost`), runs the modules, then builds the scopes: the world host first, the CoreAI scope, then the mods scope. A scope, world host or camera already in the scene is used instead. |
| `CoreAiPlayerControlsModule` | Controls | Joins the local player into every world the mods scope publishes, so the character spawns; the bundled `coreai_player_controls` mod and the default camera drive it. **Use Default Camera** off hands the camera to your game. |
| `CoreAiPlayWorldLookModule` | Look | The default look (tonemapping, bloom, anti-aliasing, sky, fog) or your `CoreAiLookProfile`; **Mode** Off leaves rendering to the game. |
| `CoreAiDefaultWorldContent` | World content | A baseplate (2048 x 16 x 2048 studs, top at height 0) and a spawn pad under the spawn point. |
| `CoreAiOverlayChatModule` | Overlay chat | Switches the in-world chat on and lets the **Programmer** role answer, so the first line can build. |

Each slot takes any component that implements `ICoreAiPlayWorldModule`; `additionalModules` and
`bootstrap.AddModule(...)` add more. A module gets `OnComposing` before the scopes build (it may still configure
them) and `OnComposed` after.

## The Hub on F2 (AI settings in the running world)

`CoreAiPlayWorldHubModule` (with the Hub package, `COREAI_HAS_HUB`) is the module for "set up the model from inside
the game". Add it to `additionalModules` and give it the Hub prefab `Assets/CoreAIHub/Runtime/CoreAiHub.prefab`:

- **F2** opens the CoreAI Hub; F2 or Escape closes it. A hint at the top centre of the screen names the key: "F2  Set
  up the AI model" until a model is set up, then "F2  AI settings". F2 does nothing while the chat input or another
  CoreAI text field has the keyboard.
- With no model it opens on **AI Settings**: type the server's address (for LM Studio `http://127.0.0.1:1234/v1`),
  a key only if the server wants one, and the model, then press **Test connection**. The line under the button
  says what happened (connected and which model answered, or why not: nothing answering, the browser's CORS or
  mixed-content rule, a missing or refused key, a model the server does not have, a timeout). When the model
  answers, the chat (T), the villagers and every other role on Automatic routing use it from the next request.
- The device remembers the address and the model (`persistentDataPath/CoreAI/remembered-backend.json`, the page's
  storage in a browser) and the next start uses them before the world builds, without a click. The key is never
  saved: a server that needs one makes the next start offline, and the chat's line says to press F2 and enter the
  key again.
- While the Hub is open it holds the keyboard (`ICoreAiUiFocus.HoldsKeyboard`): the world takes no keys or clicks,
  the chat's open keys and the mod console key do nothing, and the default camera frees the pointer.
- The offline Programmer reply in this scene ends with "Press F2 to open the AI settings, enter a model server and
  press Test connection, then ask again." (`CoreAISettingsAsset.SetSessionModelSetupHint`).
- A player build needs nothing else: the scene's reference to the prefab brings the Hub's assets, WebGL included.

## The default content

The baseplate and the pad are built by a preset mod, `coreai_play_world_content`, that is loaded into every
published world and never written to the mod store. Its parts are mod-owned, which means:

- they are never stored in a world package or an autosave, and never in the undo history: `/undo` cannot remove
  the floor;
- a world that a load, a startup restore or an undo publishes gets them again;
- a world that already has a part named `Baseplate` (or `SpawnPad`) keeps its own.

Edit the sizes and colours on the component. Leave the slot empty for a world that brings its own ground.

## Chat commands

The mods scope adds these commands to the in-world chat (**Chat Commands** on `CoreAiModsLifetimeScope`, on by
default; a command your game registered first under the same name is kept):

| Command | What it does |
|---|---|
| `/undo` | Takes back your latest AI change to the world. With nothing to undo, or after something else changed the world since, it says why and changes nothing. A change the AI's self-repair fixed is taken back together with its repair. |
| `/redo` | Puts back the change `/undo` took back, under the same rule. |
| `/fly` | Toggles flight of your character (Space rises, Left Shift descends). |
| `/save` | Only in a world kept between starts (below): records the world now and says what it saved. After a start whose saved world could not be opened, it keeps this world instead and replaces that one. |

An AI change is anything one AI call changed that a saved world keeps: world-owned instances and their properties,
Terrain cells, mod sources, and the settings without a revision of their own (the Lighting day cycle, StarterPlayer
and StarterGui settings, PointLight and GUI properties), so "make it night" alone can be undone. A read-only call
is not one. `/undo` refuses when the world changed since: a player, a mod script or the Hub edited an instance, or
another agent made an AI change (its name is in the reason). A script that changes those settings every frame (a
day cycle, a flickering torch) does not refuse it and is not recorded; the undo puts back the values of the moment
before your change, and the script carries on from there. Copies of StarterGui in a player's PlayerGui and the
player's character are not part of the world, so a respawn never refuses an undo. Land a world generator streams in
(Terrain it writes while it sets the workspace attribute `CoreAITerrainGenerating`, as the Block World does for
every column) does not refuse it either: that land comes back from the seed, so the undo restores the world before
your change and the generator makes it again. A block a player, a mod or the Hub writes still refuses it.

When a mod the AI wrote starts failing and the AI repairs it on its own, the repair joins the history entry of the
change that wrote the mod, as long as nothing else changed the world in between: one `/undo` then takes back your
request and its repair, and `/redo` puts back the repaired version. With another change in between, the repair is its
own step, named as one ("The AI's repair of mod 'tower' was undone."). A version an undo puts back is never repaired
again on its own; `/fixmod <id>` asks for it. See [MOD_CONSOLE.md](MOD_CONSOLE.md#repairs-and-undo).

## The world between starts

By default a fresh start opens the default world again: the mods come back from the mod store (they are written at
every load and reload), but the world itself does not, so a block the player broke or a tower the AI built is gone
after a page reload unless the player once loaded a save. **Keep World Between Starts** on `CoreAiModsLifetimeScope`
(`SetKeepWorldBetweenStarts(true)` from code before the scope builds; off by default, the Block World demo turns it
on) keeps it:

- The default world becomes the world that opens on the next start, recorded as a whole world package (tree, Terrain
  cells, the exact mod sources) once it stopped changing: 2 s after the last change
  (`RbxWorldRuntimeSessionController.KeptWorldQuietTime`), and at the latest 15 s after the first change of a burst
  (`KeptWorldRefreshInterval`) for a player who never stops. A change is a Terrain write, an AI change, a mod source
  change, an instance of the world added or removed outside the AI's calls, or a change of the Workspace number
  `CoreAIWorldEdits` (a mod raises it for what it keeps itself, such as the Block World's torch list). Nothing is
  recorded while nothing changes. A record captures the world in one frame (about 20 ms at a Block World's radius 8,
  most of it reading the mod sources back) and encodes it a few milliseconds per frame over the frames that follow;
  `/save` records at once. See WORLD_PACKAGE.md, "Encoding a frame at a time".
- A player's Terrain change in land the last record holds (a placed or broken block) is also written at once, in its
  own frame, to the change journal next to that record, and the next start replays it: such a block survives a
  reload made right after it. Everything else waits for the record above. See WORLD_PACKAGE.md, "The change journal
  of a kept world".
- The world says how its record stands on the Workspace, every frame, for a HUD:
  `CoreAIWorldSaveState` is `"saved"` (everything is stored), `"pending"` (a change waits for the record: a reload now
  loses it), `"saving"` (a record or a journal write is being written, or the browser still stores it) or `"failed"`
  (the latest change was not recorded, or the saved world could not be opened), and `CoreAIWorldSaveNote` says why
  for `"failed"`, or warns that another page of the game is open in the browser (the two pages overwrite each
  other's saves); it is empty otherwise. Both are absent while the world is not kept, and no record, backup or save
  holds them. From C#: `KeptWorldSaveState`, `KeptWorldSaveNote` and `KeptWorldSaveStateChanged` on the session
  controller. The in-world chat says "The world is not saved: ..." once per reason, and "The world is saved again."
  after it.
- In a browser, the page asks before a reload or a close ("Leave site?", the browser's own words) while the state is
  `"pending"` or `"saving"`; `GuardPageLeaveWhileUnsaved = false` on the session controller turns that off.
- A saved world that cannot be opened at a start (a damaged file, a mod the restore refuses) is kept: the default
  world opens, the state is `"failed"` with the reason, and nothing of this start is recorded until the player
  types `/save`, which keeps this world instead and replaces the saved one, or chooses "Start with the default world
  next time" on the Hub's World Loads page.
- A world generator marks its own Terrain writes by setting the workspace attribute `CoreAITerrainGenerating` to
  true around them: cells made from a seed come back from it, so streaming alone records nothing. A Terrain write
  made while it is false (a player's block, a falling block) marks the world for its next record.
- A mod's own instances are never stored in a world package (the mod makes them again); a mod that keeps
  something of its own across a restart lists it in a Workspace attribute, which the package keeps, and raises
  `CoreAIWorldEdits` when it changes.
- A mod the AI (or any trusted host actor) loaded starts after a restart with the host's rights it had then;
  a mod a restricted actor loaded stays restricted (`LuaModManifest.OwnerHadHostAuthority`, written only when
  true). A mod that throws while the kept world restores stays stopped and is reported; the rest of the world
  opens.
- `/save` records the world now. In a browser the page stores it a moment later: Unity persists the storage on its
  own since 6.3 and gives no completion signal; the save state reads `"saving"` until the page's storage is idle again.
- A restored world takes a strictly newer version of a bundled sample, by the seeder's rule (a bundle whose
  `version:` is higher than the one the copy was seeded at replaces it, a mod the player wrote under the same id is
  never touched); the replaced source stays in the mod's revision history.

## Typing never moves the character

The chat overlay, the chat panel, the Hub and runtime UI screens all report into the CoreAI scope's
`ICoreAiUiFocus`. While any of them holds the keyboard (`HoldsKeyboard`: a focused text field, or a Hub open over
the world), the mods scope marks every input event with `gameProcessedEvent = true`, so the control script ignores
it, and the overlay's open keys do nothing (the T you type into the chat panel stays there).

## Configure, replace or leave out

- From code: create the bootstrap with `composeOnAwake` off, set `Factory`, add modules, then call `Compose()`.
- The camera preset is `CoreAiPlayerCameraSettings` on the mods scope (`useDefaultCamera`, `ModuleOptions`,
  `ModuleFactory` for your own `IRbxCameraModule`).
- The rate limit, undo history and backup timing are `CoreAiInstantEditSettings` (**Instant Edit** on the
  bootstrap for a created mods scope).
- Without the mods package there is no world: the CoreAI scope alone still gives the chat, the overlay and
  runtime UI.
