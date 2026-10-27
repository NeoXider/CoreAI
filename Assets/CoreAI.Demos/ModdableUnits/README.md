# CoreAI — Unit Forge (mod-driven game)

**What you will see:** an arena with nothing in it, and an AI that fills it with units, armies and endless
waves — every one of them defined by a Lua mod written on the spot.

> An arena that ships **empty**. There are no units, no waves and no behaviour in the
> scene — **every bit of gameplay is added by Lua mods** written through chat. Ask the
> AI to forge a unit type, deploy an army, and stream reinforcements; the host only runs
> a tiny auto-battle loop on whatever the mods create.

This demonstrates that CoreAI mods are not limited to tweaking numbers (`logic_define`
slots, as in the Wave Auto-Battler demo) — they can **introduce brand-new content** and
extend the game at runtime through a host-defined Lua API.

## How it works

The scene controller [`ModdableUnitsDemoController`](Scripts/ModdableUnitsDemoController.cs)
implements `IUnitForge` and `ICoreAiLuaGameplayBindingsProvider`. Its lazy
[`UnitForgeLuaBindings`](Scripts/UnitForgeLuaBindings.cs) callbacks register through
`LuaCsModStackOptions.AdditionalGameplayBindings` at each Lua state's effective capability tier.

The opt-in is explicit: assign this scene's controller to
`CoreAiModsLifetimeScope.gameplayBindingsProviders`, then assign that same scope to the controller's
`modsScope`. Each scope snapshots its assigned providers during composition; an empty provider list
exposes no forge globals. Other games can implement the provider interface without referencing this
demo. Code-only hosts can supply an `Action<LuaCsApiRegistry, LuaCapabilities>` through the required
trailing parameter of the extended `RegisterCoreAiMods` overload, or directly through the factory option.
The original optional installer overload remains available with the same defaults.

The callback reaches persistent mods, persisted rehydrate, replacement-world stacks and `execute_lua`.
It does not grant additional capabilities: the factory intersects the mod request or one-off tier with
the host ceiling before invoking the provider.

| Function | Required capability | Effect |
|---|---|---|
| `forge_define(name, team, hp, dmg, speed, range, color)` | WorldEdit | Register/overwrite a unit archetype. `team` is `"ally"` or `"enemy"`; `color` is a hex string (or omit it). Trailing args are optional and fall back to sane defaults. Returns `true`. |
| `forge_spawn(name, x, z)` | WorldEdit | Spawn a live instance of a defined archetype at `(x, z)`. Returns its instance id (`0` on failure). |
| `forge_count(team)` | Read | Live unit count for `"ally"`, `"enemy"`, or `"all"`. |
| `forge_clear()` | WorldEdit | Remove all live units, keep definitions. |
| `forge_reset()` | WorldEdit | Remove all units **and** definitions. |

The host loop walks each unit toward its nearest enemy and attacks in range. Deaths,
spawns and team wipes are emitted back to mods as events:

| Event | Payload |
|---|---|
| `unit_spawned` | `name:team` |
| `unit_died` | `name:team` |
| `team_wiped` | `team` |

A mod can react to its world with `hooks_on(...)` and drive it over time with `hooks_every(...)`.
Persistent startup chunks may call forge functions before the controller's `Start`: the unit root is
created lazily and UI updates wait for the panel. `Start` retains those archetypes and visuals.
The controller attaches its event bridge to the explicitly assigned scope in `Start`; an embedding host
can attach its real runtime and trusted host identity earlier through `AttachModRuntime`.
Replacing that attachment removes the previous listeners, and controller teardown removes its listeners.

## Requirements

- `COREAI_LUA` defined (the demo controller compiles only with it).
- `COREAI_LLM` defined and an LLM endpoint configured in `Resources/CoreAISettings` (LM Studio /
  OpenAI-compatible).
- The scene's `CoreAI` scope uses the built-in **Programmer** role (already wired in the
  scene) so `manage_mods` is available in chat.
- The explicit provider and `modsScope` references described above are assigned on the scene components.

## Try it

Press Play and type the requests into the chat panel (the scene's `ChatPromptButtonsController` is a
GUI-less driver that nothing renders, and this scene does not enable the chat's example-prompt menu, so
there are currently no ready-made prompt buttons on screen):

1. **Starter armies** — forges `knight` (ally) and `goblin` (enemy) and deploys both sides.
2. **Archers and ogre boss** — adds a long-range `archer` and a heavy `ogre`.
3. **Endless waves** — `hooks_every` tops up goblins and `hooks_on('team_wiped', ...)`
   revives defenders, so the fight never ends.

### Example mod (what the AI writes)

```lua
-- name: Starter Armies
-- description: Forges a knight line and a goblin swarm, then deploys them.
-- forge_define(name, team, hp, dmg, speed, range, color)
forge_define("knight", "ally",  60, 8, 1.4, 1.1, "#3aa0ff")
forge_define("goblin", "enemy", 25, 4, 2.0, 1.0, "#3cc452")

for i = 1, 3 do forge_spawn("knight", -5, (i - 2) * 1.5) end
for i = 1, 4 do forge_spawn("goblin",  5, (i - 2) * 1.2) end
report("Starter armies forged.")
```

```lua
-- name: Endless Waves
-- description: Keeps the battle alive using forge events and timers.
hooks_every(3.0, function()
  if forge_count("enemy") < 6 then
    forge_spawn("goblin", 6, math.random(-3, 3))
  end
end)

hooks_on("team_wiped", function(_, team)
  if team == "ally" then forge_spawn("knight", -5, 0) end
end)
report("Endless waves armed.")
```

## Safety

`forge_count` requires **Read**. Every forge mutation requires **WorldEdit**; unavailable functions are
absent from the Lua globals, so a read-only mod cannot spawn, redefine or clear units.
The Lua-CSharp sandbox (no `io`/`load`, `os` limited to `os.time`/`os.clock`), per-call
instruction/time limits and the quarantine-after-repeated-errors policy (the mod stays loaded with
dispatch suspended until a reload) all still apply. Spawns are capped (`MaxUnits`)
and positions are clamped to the arena. Unit visuals are created on Unity's main thread
during mod ticks, which the mod-runtime ticker guarantees.

The forge is host-owned gameplay state shared by the mods in its assigned scene. Its plain Unity unit
visuals are outside the Rbx instance registry: unloading a mod does not remove its forged units, and a
world-package replacement does not snapshot or roll back the controller's unit state. Use explicit
gameplay cleanup for a complete arena reset. Persisted mod source can recreate startup units on a fresh
scene entry; this does not persist an in-progress battle. Read-only provider registration itself does
not touch the arena or create UI.

## Related

- [LUA_GAME_API.md](../../CoreAI/Docs/LUA_GAME_API.md) — full Lua game API.
- [LiveMechanicsMods README](../LiveMechanicsMods/README.md) — Wave Auto-Battler (number-tuning mods).
- [LUA_ACCESS_MODES.md](../../CoreAI/Docs/LUA_ACCESS_MODES.md) — capability tiers (Read → Full).
