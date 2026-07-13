# CoreAI — Unit Forge (mod-driven game)

> An arena that ships **empty**. There are no units, no waves and no behaviour in the
> scene — **every bit of gameplay is added by Lua mods** written through chat. Ask the
> AI to forge a unit type, deploy an army, and stream reinforcements; the host only runs
> a tiny auto-battle loop on whatever the mods create.

This demonstrates that CoreAI mods are not limited to tweaking numbers (`logic_define`
slots, as in the Wave Auto-Battler demo) — they can **introduce brand-new content** and
extend the game at runtime through a host-defined Lua API.

## How it works

The scene controller [`ModdableUnitsDemoController`](Scripts/ModdableUnitsDemoController.cs)
implements `IUnitForge` and authors [`UnitForgeLuaBindings`](Scripts/UnitForgeLuaBindings.cs)
as a Lua-CSharp `ILuaCsGameRuntimeBindings` set intended for the **WorldEdit** capability tier.
When wired into the mod runtime, a mod granted WorldEdit (the default tier) gets these extra
Lua functions:

> NOTE: the mod runtime now exposes the seam (`LuaCsModStackOptions.AdditionalGameplayBindings`), but the
> demo's composition layer does not yet thread it through, so the forge functions below are authored and
> ready but not currently surfaced to running mods. They become live once the demo wires that option through
> `CoreAiModsInstaller.RegisterCoreAiMods` / `CoreAiModsLifetimeScope` with a lazy forge lookup — tracked as
> `TODO(moddableunits-binding-seam)`.

| Function | Effect |
|---|---|
| `forge_define(name, team, hp, dmg, speed, range, color)` | Register/overwrite a unit archetype. `team` is `"ally"` or `"enemy"`; `color` is a hex string (or omit it). Trailing args are optional and fall back to sane defaults. Returns `true`. |
| `forge_spawn(name, x, z)` | Spawn a live instance of a defined archetype at `(x, z)`. Returns its instance id (`0` on failure). |
| `forge_count(team)` | Live unit count for `"ally"`, `"enemy"`, or `"all"`. |
| `forge_clear()` | Remove all live units, keep definitions. |
| `forge_reset()` | Remove all units **and** definitions. |

The host loop walks each unit toward its nearest enemy and attacks in range. Deaths,
spawns and team wipes are emitted back to mods as events:

| Event | Payload |
|---|---|
| `unit_spawned` | `name:team` |
| `unit_died` | `name:team` |
| `team_wiped` | `team` |

The intended design lets a mod react to its own world with `hooks_on(...)` and drive it over time with
`hooks_every(...)`, so a complete game emerges from mods alone. See the NOTE below: the `forge_*` scene
bindings are authored but not yet threaded into the mod runtime, so this demo is currently aspirational.

## Requirements

- `COREAI_NO_LUA` **not** defined.
- An LLM endpoint configured in `Resources/CoreAISettings` (LM Studio / OpenAI-compatible).
- The scene's `CoreAI` scope uses the built-in **Programmer** role (already wired in the
  scene) so `manage_mods` is available in chat.

## Try it

Press Play and use the **Unit Forge mod prompts** buttons (or type your own):

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

`forge_*` lives behind the **WorldEdit** tier — read-only mods cannot spawn anything.
The Lua-CSharp sandbox (no `io`/`os`/`load`), per-call instruction/time limits and the
automatic unload-after-repeated-errors policy all still apply. Spawns are capped (`MaxUnits`)
and positions are clamped to the arena. Unit visuals are created on Unity's main thread
during mod ticks, which the mod-runtime ticker guarantees.

## Related

- `Assets/CoreAI/Docs/LUA_GAME_API.md` — full Lua game API.
- `Assets/CoreAI.Demos/LiveMechanicsMods/README.md` — Wave Auto-Battler (number-tuning mods).
- `Assets/CoreAI/Docs/LUA_ACCESS_MODES.md` — capability tiers (Read → Full).
