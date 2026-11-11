# Block World

A small Minecraft-style game whose logic and interface are entirely Lua mods on CoreAI's Roblox-shaped API:
a seeded voxel world in `workspace.Terrain`, building and breaking at the crosshair, a hotbar, day and night,
torches, villagers who talk, and an autopilot. It is also the template for "a game like this": every part is a
short mod you can read in one sitting, change while the game runs, or ask the AI to change.

## Controls

| Input | What it does |
|---|---|
| W A S D / arrows | Walk (the bundled `coreai_player_controls` mod) |
| Space | Jump; double-tap to fly, then Space rises and Left Shift sinks |
| Mouse | Look (first person; the pointer is locked) |
| Left click | Break the outlined block (hold to keep breaking) |
| Right click | Place the hotbar block on the outlined face (hold to keep placing) |
| 1 - 9, mouse wheel | Choose a hotbar slot (or click a slot while the pointer is free) |
| G | Creative (endless blocks) or survival (you place what you broke) |
| V | First or third person |
| E | Talk to the villager you look at, or the nearest one |
| P, or `/autopilot on`, `/autopilot off`, `/autopilot status` | Autopilot on or off; any movement key also takes the controls back |
| L | Time of day: noon, dusk, midnight, dawn |
| T or Enter | Open the chat; `/` opens it with a command started |

Reach is five blocks. Bedrock, the bottom layer, cannot be broken, and a block is never placed where you
stand.

## Prompts to try

Point the crosshair at the ground first: the AI builds where you look, in cells.

- "Build a watchtower here." Then `/undo` takes it back, and `/redo` puts it back.
- "Put a small brick house with a door on this spot."
- "Make a staircase from here up to the top of that hill."
- "Dig a 5 x 5 pool here and fill it with water."
- "Light this cave: put torches every few blocks along the walls."
- "Make it night." or "Make the days twice as long."
- "Give the hotbar a darker look with gold borders." (the HUD is UXML and USS in `blockworld_hud`)
- "Tobin, what should I build next?" (said in the chat: a line that names a villager, follows up, or is said
  near one goes to that villager instead of the AI)

## What needs a model

| Feature | Without a model | With a model |
|---|---|---|
| Walking, flying, building, breaking, hotbar, day and night, torches | works | works |
| "Build here" and other chat requests | the chat answers that the AI is not connected | the AI writes cells at the crosshair; `/undo` restores them |
| Villagers | stand, turn to face you; say a model is needed | answer by name, in the chat and a bubble, and remember you |
| Autopilot | a fixed rule keeps the current goal, else wanders | the decision model picks the goal from the mod's list |

Villagers also need the game to allow AI from Lua, the framework setting `CoreAiLuaAiOptions.AllowAgentsFromLua`.
It is off in a game unless the game turns it on, and this demo's scene turns it on. With it off the villagers
stand still and say why, naming the setting. The autopilot walks for wander, follow and idle itself; gather (break
nearby wood and stone), build (a small plank shelter with a doorway) and home are carried out by the mod.

## How the mods are organised

Seven bundled mods in one mod folder, `Samples/Block World` (their `category:`), under
`Assets/CoreAIMods/Runtime/Resources/CoreAIMods/BlockWorld/`. Turn the folder on as a whole; each mod also
reloads on its own.

| Mod | Owns | Talks to the others through |
|---|---|---|
| `blockworld_world` | the seeded world: hills, sea, shore, stone, bedrock, trees, streamed around you in budgeted steps; the spawn | `BlockWorldSeed`, `BlockWorldSpawn`, `BlockWorldReady`, `BlockWorldColumns` |
| `blockworld_build` | the crosshair target and its outline, breaking and placing, reach, creative or survival, the view | `CrosshairCell`, `CrosshairPlaceCell`, `CrosshairNormal`, `CrosshairPartName`, `CrosshairModelName`, `BlockWorldCreative`, `BlockWorldPlaced`, `BlockWorldBroken`, `BlockWorldCount_<Material>` |
| `blockworld_hud` | the whole interface (UXML and USS text through `UxmlService`) and the hotbar choice | `BlockWorldSelected`, `BlockWorldSlot` |
| `blockworld_daynight` | the clock (`Lighting.ClockTime`), the night ambient, `/time` and the L key | `BlockWorldTime` (set it to "noon", "18:30", "stop", "go") |
| `blockworld_torches` | torches with a PointLight and a flame, the light budget | `BlockWorldTorches` |
| `blockworld_villagers` | Mira, Tobin and Elda: character Models under `workspace`, their `AIService` agents and brains | on each Model `NpcAgent`, `NpcBrain`, `NpcState` (the host writes `NpcAction`); `BlockWorldVillagerReply` |
| `blockworld_autopilot` | the block world's goals and facts for the game's autopilot, and the goals only a block world knows: gather, build, home | on the Player `Autopilot`, `AutopilotGoals`, `AutopilotState` (the host writes `AutopilotGoal`); `BlockWorldAutopilot` |

Why this cut: each mod is one thing a player can see and one thing you might want to replace. The mods never
call each other (Lua mods have no `require` here, and `BindableEvent` is not built yet); they share state the
Roblox way, as attributes on `workspace`, written by exactly one owner and read by anyone. Any notice a mod
wants shown goes to `BlockWorldNotice`, which the HUD displays. So you can drop the HUD and write your own,
replace the generator with a flat world, or remove the villagers, and the rest keeps working.

Knobs you can set as workspace attributes before the world mod loads: `BlockWorldSeed` (another world),
`BlockWorldViewRadius` (1 to 8 columns of 64 studs; smaller for WebGL).

## Budgets

- World generation never does more than a few dozen pillars per frame, well inside the per-resume instruction
  budget every Heartbeat handler gets; the four columns at the spawn are made while the mod loads.
- The torch mod keeps at most 48 lit torches and says so when it is full. The host adds its own light budget:
  desktop (Forward+) keeps 64 lights, the WebGL tier keeps the 8 nearest on and reports it.
- The HUD rewrites a label only when its text changes, so an idle frame writes nothing to the UI.
