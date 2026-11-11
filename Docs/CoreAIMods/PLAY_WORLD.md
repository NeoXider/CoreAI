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

## The default content

The baseplate and the pad are built by a preset mod, `coreai_play_world_content`, that is loaded into every
published world and never written to the mod store. Its parts are mod-owned, which means:

- they are never stored in a world package or an autosave, and never in the undo history: `/undo` cannot remove
  the floor;
- a world that a load, a startup restore or an undo publishes gets them again;
- a world that already has a part named `Baseplate` (or `SpawnPad`) keeps its own.

Edit the sizes and colours on the component. Leave the slot empty for a world that brings its own ground.

## Chat commands

The mods scope adds three commands to the in-world chat (**Chat Commands** on `CoreAiModsLifetimeScope`, on by
default; a command your game registered first under the same name is kept):

| Command | What it does |
|---|---|
| `/undo` | Takes back your latest AI change to the world. With nothing to undo, or after something else changed the world since, it says why and changes nothing. |
| `/redo` | Puts back the change `/undo` took back, under the same rule. |
| `/fly` | Toggles flight of your character (Space rises, Left Shift descends). |

## Typing never moves the character

The chat overlay, the chat panel, the Hub and runtime UI screens all report into the CoreAI scope's
`ICoreAiUiFocus`. While any of them has a focused text field, the mods scope marks every input event with
`gameProcessedEvent = true`, so the control script ignores it, and the overlay's open keys do nothing (the T you
type into the chat panel stays there).

## Configure, replace or leave out

- From code: create the bootstrap with `composeOnAwake` off, set `Factory`, add modules, then call `Compose()`.
- The camera preset is `CoreAiPlayerCameraSettings` on the mods scope (`useDefaultCamera`, `ModuleOptions`,
  `ModuleFactory` for your own `IRbxCameraModule`).
- The rate limit, undo history and backup timing are `CoreAiInstantEditSettings` (**Instant Edit** on the
  bootstrap for a created mods scope).
- Without the mods package there is no world: the CoreAI scope alone still gives the chat, the overlay and
  runtime UI.
