# CoreAI live-world-mutation testbed — game proposals

> Purpose: pick a game to build/adopt that exercises CoreAI's core value — **the game changes LIVE while
> you play** (AI agents + Lua-CSharp mods rewrite mechanics and mutate the world at runtime). Two research
> tracks: **A** — adopt an existing open-source Unity game; **B** — build an original CoreAI-native concept.
> Compiled 2026-07-11 from two parallel scouting agents (repos verified by fetching their GitHub pages;
> unverified fields are flagged inline).

---

## Section A — Existing open-source Unity games to bolt CoreAI onto

**Honest caveat:** no **natively Unity 6 (6000.x)** open-source game surfaced in a CoreAI-friendly genre
(arena-survivors / tower-defense / roguelike). Every candidate below is Unity 2021–2022 LTS and needs a
(usually minor, unverified-per-repo) engine + URP upgrade.

| # | Repo | License | Unity | Genre | CoreAI fit | Effort | Top risk |
|---|------|---------|-------|-------|-----------|--------|----------|
| 1 | [getsentry/sentaur-survivors](https://github.com/getsentry/sentaur-survivors) | Apache-2.0 ✓ | 2022.3.7f1 ✓ | arena survivors | **Excellent** — spawn/wave curve, per-weapon damage + upgrade path, XP curve, enemy HP/contact-damage, level-up choice table; dense field of GameObjects | S–M | soft-archived |
| 2 | [matthiasbroske/VampireSurvivorsClone](https://github.com/matthiasbroske/VampireSurvivorsClone) | MIT ✓ | 2021.3+ ✓ | arena survivors | **Very strong** — keyframe spawn-rate curve + ScriptableObject weapon defs (damage/cooldown/projectile count) are almost purpose-built for Lua logic slots; pooled projectiles/items/chests | S–M | maintenance unconfirmed |
| 3 | [stasiandr/open-unity-survivor-game](https://github.com/stasiandr/open-unity-survivor-game) | CC0-1.0 ✓ | unverified ⚠ | arena survivors | Good breadth — wave spawn, contact-damage, XP, loot/chest, gold economy, ultimate-form unlocks | M | unverified Unity + heavy custom shaders |
| 4 | [Chizaruu/2D-Roguelike-Kit](https://github.com/Chizaruu/2D-Roguelike-Kit) | MIT ✓ | 2021.3+ ✓ | roguelike template | Great genre — damage/loot/room-count/turn slots + tile-grid world; but combat/AI half-built (you add mechanics as much as rewrite) | M | feature-incomplete, stale 2023 |
| 5 | [Tomiinek/Unity_Tower_Defence](https://github.com/Tomiinek/Unity_Tower_Defence) | MIT ✓ | unverified ⚠ | tower defense | Natural genre — tower damage/range/fire-rate, wave composition, upgrade curve, enemy HP/speed | M | old/unknown Unity, bare 4-commit repo |

**Ranked recommendation (adopt track):**
1. **getsentry/sentaur-survivors** — best overall: survivors is the single strongest fit for "the game
   changes LIVE" (dense fast Update loop full of spawnable GameObjects + obvious tunable formulas), a
   complete/organized hack-week codebase, permissive, closest to Unity 6. "Make enemies explode into coins"
   / "double the spawn rate" map onto real seams with minimal plumbing.
2. **matthiasbroske/VampireSurvivorsClone** — close second, best if you want max star-credibility and the
   cleanest live-tuning story (keyframe spawn curve + SO weapon defs = ideal Lua slots; MIT).

No strong permissive **current-Unity** open-source tower-defense exists — build a fresh Unity 6 TD from a
current tutorial rather than adopting the stale repos, if a second genre is wanted.

---

## Section B — Original CoreAI-native game concepts (with seed repos or build-from-example)

Three concepts deliberately *shallow as static games, deep only when the rules mutate at runtime* — each
stresses a different CoreAI failure axis.

### Concept 1 — OVERLORD (AI Dungeon Master roguelike)
- **Pitch:** an adversarial Dungeon Master agent rewrites the next room *in reaction to how you just played*.
- **Loop:** clear room → DM agent reads telemetry (time-to-clear, HP lost, favored weapon) → authors/edits a
  Lua mod reshaping the next room's layout, spawn table, and damage math → you adapt or die.
- **CoreAI exercised:** chat→mod authoring (the DM's "move" is a mod-authoring turn); world commands
  (spawn/move/reparent/scale/destroy to lay walls/hazards/packs); logic slots (`damage_formula`/`loot_table`/
  `spawn_count` swapped per room); mods+hooks (per-mod K/V "grudge model", `hooks_on('tick')` hazards);
  reflection (`unity_find` + `unity_set_member` speed/HP/aggro on whatever component the seed uses).
- **Break-the-system:** formula-slot churn + world-command burst per room transition; agent authoring a
  *broken* mod (syntax error, infinite `hooks_every`, spawn storm) is a first-class sandbox/bound stress case.
- **MVP:** one room, three enemy prefabs, two slots (`damage_formula`, `spawn_count`); stub DM picks one of
  three canned mods by HP-lost; prove telemetry-in → slot swap + spawn burst → different room. Then swap in a real director agent.
- **Seed:** [damarindra/Unity-Dungeon-Generator](https://github.com/damarindra/Unity-Dungeon-Generator) —
  MIT ✓, Unity 2019.4.11f1 ✓ (BSP+Delaunay, data-only → easy upgrade; lift the algorithm, small repo).
  3D alt: [triofyx/dunger](https://github.com/triofyx/dunger) (MIT). Or build from RogueliteArena (already
  has arena spawning + CoreAI wiring; add a BSP pass, treat each "wave" as a "room").

### Concept 2 — TURING TOWERS (chat-authored tower defense)
- **Pitch:** towers ship with *no behaviour* — you type what each should do mid-wave.
- **Loop:** place blank chassis → chat an agent to author each tower's targeting/effect as a Lua mod →
  towers hot-reload behaviour live as enemies pour in → refine between waves. Your prompt history is the tech tree.
- **CoreAI exercised:** chat→mod hot-reload (headline demo: "prioritize flyers" edits the mod, tower changes
  without pausing); logic slots (damage/targeting/price per tower type); mods+event-bus+cross-mod calls
  (beacon broadcasts, guns subscribe+buff = the aura/combo system); `hooks_every` cooldowns/DoTs; world
  commands (per-shot spawn/move/destroy, rotate-to-face, recolor); reflection (towers `unity_find` enemies,
  `unity_set_member` HP/speed on arbitrary components).
- **Break-the-system:** cross-mod interplay + reflection-under-load — dozens of tower mods all firing timers,
  pub/subbing on one bus, cross-calling each tick; plus concurrent authoring (edit tower A while B ticks).
- **MVP:** one grid, one path (BFS), two chassis, one behaviour slot (targeting+damage); prove "slow the
  fastest enemy in range" reloads live. Then add the event bus for a beacon+gun combo.
- **Seed:** [frangam/TowerDefense](https://github.com/frangam/TowerDefense) — MIT ✓, 3D, tower class
  hierarchy + sphere-collider detection + **BFS recalc on placement** + waves + grid; Unity version
  unpublished (assume older LTS, plan a Unity 6/URP upgrade). Near-ideal skeleton to strip default behaviour
  and route decisions into CoreAI logic slots.

### Concept 3 — PETRI (living-ecosystem sandbox where mods *are* the creatures)
- **Pitch:** every species is a Lua mod you (or an agent) write; drop species into the dish and watch — or
  chat-tweak — an emergent food web.
- **Loop:** type a species into being → CoreAI authors it as a self-contained mod with its own tick loop →
  species eat/breed/migrate via the shared event bus → rebalance live by editing rules or world commands
  (spread grass, raise a wall, cull a plague).
- **CoreAI exercised:** a whole game loop per mod (`hooks_on('tick')` sense→decide→act + per-mod K/V state —
  the canonical "an entire game loop lives in one mod", × N species); cross-mod calls + event bus (predation/
  symbiosis = mods calling/subscribing); logic slots (`birth_rate`/`energy_decay`/`carrying_capacity` retune
  the whole biosphere); world commands + reflection (spawn/destroy on birth/death, recolor by energy,
  `unity_find` neighbours); chat ("introduce a scavenger that only eats corpses" into a *running* sim).
- **Break-the-system:** sustained-population + spawn/destroy churn endurance; cross-mod call volume scales
  with population² — the harshest interplay/latency test; authoring into a live loop must not stall ticks.
- **MVP:** two species (grass spreads on a timer; grazer seeks grass, breeds, starves) + one global slot
  (`birth_rate`); prove boom/bust emerges, then chat a predator into the running dish. Object-pool spawns from day one.
- **Seed:** none suitably-licensed — [AlexandreSajus/Unity-Ecosystem](https://github.com/AlexandreSajus/Unity-Ecosystem)
  is GPL-3.0 + paid assets; [Callum-A/Unity-Ecosystem-Simulation](https://github.com/Callum-A/Unity-Ecosystem-Simulation)
  has no license (all-rights-reserved) — study its behavior model only. **Build from RogueliteArena**
  (reuse spawn + agent-movement plumbing, delete combat, move sense/decide/act into per-species mods).

**Stress-axis coverage:** OVERLORD = logic-slot churn + reshape bursts + agent-authored broken mods ·
TURING TOWERS = event-bus fan-out + reflection-per-shot + concurrent authoring · PETRI = spawn/destroy
endurance + population² cross-mod calls + authoring into a live loop.

---

## Recommendation (synthesis)

- **Fastest credible demo (adopt):** fork **getsentry/sentaur-survivors** (Apache-2.0), upgrade to Unity 6 +
  URP, and wire CoreAI onto its spawn-wave curve + weapon-damage formulas + on-death loot hook. Lowest effort
  to a shareable "watch me retune the game by chatting at it" clip. RogueliteArena already proves the wiring
  pattern, so this is mostly formula-seam extraction.
- **Strongest CoreAI-native showcase (build):** **TURING TOWERS** — hot-reloading a tower's brain mid-wave by
  typing at it is the clearest single expression of CoreAI's thesis, and its cross-mod event bus doubles as a
  throughput/interplay stress test. Seed from **frangam/TowerDefense** (MIT).
- **Best pure stress test:** **PETRI**, built from RogueliteArena — population² cross-mod traffic and
  spawn/destroy churn will surface CoreAI's real scaling limits (world-command GC, tick-scheduler, hot-reload
  safety) better than any adopted game.

Suggested order: (1) bolt CoreAI onto sentaur-survivors for a quick marketing clip; (2) build TURING TOWERS
MVP as the flagship CoreAI-native demo; (3) use PETRI as the ongoing load/endurance testbed.
