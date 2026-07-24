# Mod Instance Ownership & Cleanup — Design + Plan

> Goal: objects a mod spawns are **owned** by that mod, so unload / reload / forget / quarantine-removal
> **or** an explicit "cleanup" button removes everything it spawned. No leaked GameObjects that outlive
> the mod. The model must stay as close to Roblox 1:1 as possible and be **invisible** to mod authors —
> cleanup "just happens".

---

## TL;DR recommendation

The ownership ledger **already exists and is already threaded end-to-end** — the only thing missing is
that **nothing calls the teardown sweep on unload**. `InstanceRegistry.GetOwnedBy(modId)` was written
for exactly this ("Hot-reload teardown sweep") and is never invoked, and `TeardownModEffects` only
clears logic-slot overrides today.

**MVP = one wiring change, zero author-facing API change:** subscribe to the already-designed
`LuaCsModRuntime.ModTearingDown` seam (documented as the hook where "instance registries … subscribe
here to release the mod's effects") and, on `Unload`, sweep `registry.GetOwnedBy(modId)` → `Destroy()`.
Because `RbxInstance.Destroy()` is recursive and drives the binder's `OnDestroyed`, that releases every
backing GameObject the mod created. This preserves Roblox 1:1 (no auto-parenting, `Instance.new` stays
parentless) and is invisible.

**Phase 2** adds the ergonomic Roblox-idiomatic layer: a per-mod container `Folder` under `workspace`
plus a "reparent-out-to-persist" opt-out, so the mental model ("my objects live under my folder; move
one out to keep it") is visible when authors want it. **Phase 3** brings the WorldEdit path
(`coreai_world_spawn`) under the same ownership umbrella and adds the Hub "Cleanup" button.

---

## Step 1 — Audit of what already exists

### The ownership ledger (already complete)

| Concern | Where | Notes |
|---|---|---|
| Owner + origin stored per instance | `InstanceRecord.cs:23-27,32-38` | `OwnerModId` ("Teardown owner (hot reload); null = host") + `OriginTag` fields on every record. |
| Origin tag scheme | `OriginTag.cs:9-19` | `mod:<id>` / `console:<inv>` / `ai:<id>` / null. Doc string: "Tags enable selective cleanup/undo". |
| Registry stamps owner at creation | `InstanceRegistry.cs:63-68` (`Create`), `:72-85` (`CreateScripted`), `:136-151` (`RegisterNew`) | `ownerModId`/`originTag` are first-class creation params; `RegisterNew` validates the tag and builds the `InstanceRecord`. |
| **Teardown sweep (exists, UNUSED)** | `InstanceRegistry.cs:247-260` `GetOwnedBy(modId)` | Comment: "Hot-reload teardown sweep (roadmap §3.3 / MVP5)". Returns every live instance whose `OwnerModId == modId`. **Nothing in the codebase calls it.** |
| Destroy → binder release | `InstanceRegistry.cs:356-378` `OnInstanceDestroyed` | Removes record from all 3 identity maps and calls `_binder.OnDestroyed(record)`. |

### How `ownerModId` flows (mod id → tagged instance)

1. `LuaCsGameplayBindings.Register(registry, caps, ownerModId)` — `LuaCsGameplayBindings.cs:116`; comment
   at `:129-131`: "the Roblox surface … threads the owner mod id so created instances land in the
   ownership ledger" → calls `_roblox.Register(registry, effective, ownerModId)` (`:131`).
2. `LuaCsRobloxApiBindings.Register(registry, caps, ownerModId)` — `LuaCsRobloxApiBindings.cs:152-181`.
   Builds the origin tag: `ownerModId != null ? OriginTag.FromMod(ownerModId) : OriginTag.FromConsole(...)`
   (`:172-178`) and packs both into a `LuaCsRobloxModContext` (`:180-181`).
3. `Instance.new` — `LuaCsRobloxApiBindings.cs:257-261`:
   `_registry.CreateScripted(className, context.OwnerModId, context.OriginTag)`.

So **every `Instance.new('Part')` a mod runs already lands in the ledger tagged `mod:<id>`.** The plumbing
is done; the sweep is simply never triggered.

### Rbx instance model & binder

- `InstanceRegistry` (`InstanceRegistry.cs:16-36`): single owner of identity; one `InstanceRecord` per
  live instance; drives the backing binder.
- `RbxInstance.Destroy()` (`RbxInstance.cs:375-397`): atomic, **recursive** (destroys all children,
  `:388-394`), idempotent, ends in `Registry.OnInstanceDestroyed(this)`. `ClearAllChildren()` at `:399-408`.
- `InstanceGameObjectBinder` (`InstanceGameObjectBinder.cs:34`, `IInstanceBackingBinder`+`IPartPropertySink`):
  materializes the whole DataModel tree into Unity. **Root transform** = the `RobloxWorldHost` transform,
  passed as `_worldParent` (`:99-104`; `RobloxWorldHost.cs:62`). The DataModel entry reuses the host GO and
  is flagged `OwnsGameObject=false` so teardown never destroys the host (`:311-323`, `:162-178`).
  `OnDestroyed` destroys the owned GameObject (`:162-178`).
- **No per-mod subtree today.** `Instance.new` returns a **parentless** instance (Roblox default). It does
  not materialize until parented into the scene subtree (`OnEnteredWorld`, `:124-145`). Parked/detached
  objects re-parent under `_worldParent` (`:147-160`). So the only existing "owner" grouping is the
  **tag**, not a container.
- `RobloxWorldHost` (`RobloxWorldHost.cs:26-31,54-72`): owns `Registry`/`Game`/`Binder`; `OnDestroy`
  (`:74-84`) tears the whole world down via `Game.Destroy()`.
- Workspace bootstrap: `DataModelBootstrap.CreateGame` (`DataModelBootstrap.cs:12,19`) registers `Workspace`
  as the physical-world root — the natural parent for a per-mod container in Phase 2.

### Mod lifecycle & today's teardown

- Interface: `ILuaModRuntime` — `UnloadMod` (`ILuaModRuntime.cs:28`), `ReloadMod` (`:24`),
  `ForgetMod` (`:37`), `LoadMod` (`:21`). No `SetActive` (persistence dormancy is via the source store).
- `LuaCsModRuntime.UnloadMod` (`LuaCsModRuntime.cs:573-610`) → `TeardownModEffects(modId, Unload)` (`:590`).
- `ReloadMod` (`:621-657`) → `TeardownModEffects(..., Reload, replacement.State)` **before** the swap (`:646`).
- `ForgetMod` (`:1620-1634`) → `UnloadMod` (`:1623`) → same `Unload` teardown, then deletes persistence.
- Quarantine (`:910`) → `TeardownModEffects(..., Quarantine)` but **keeps the mod loaded**.
- **`TeardownModEffects` (`:921-941`) today: clears logic-slot overrides + raises `ModTearingDown`. It does
  NOT touch owned Rbx instances.** ← the leak.
- **The seam already exists:** `ModTearingDown` event (`LuaCsModRuntime.cs:213-220`), reason enum
  `LuaModTeardownReason { Unload, Reload, Quarantine }` (`:16-21`). Its own doc says: *"future subsystems
  (instance registries, signals) subscribe here to release the mod's effects at the same point."*
- **Current manual pattern (Tetris):** `LuaPlatformExampleController.cs` TetrisSource does cleanup by hand
  on the **WorldEdit** path — a per-load generation counter names one root model `TetrisRoot_g<gen>`
  (`:392-397`) and on reload destroys the previous generation's root, which removes the whole field in one
  command (`:407-411`, comment: "reloads never leave orphans"). This is precisely the behavior we want to
  make automatic and general.

### WorldEdit path vs Rbx path — does both need ownership?

- Rbx path (`Instance.new`): **already tagged**, needs only the sweep.
- WorldEdit path (`coreai_world_spawn` → `IAiGameCommandSink` → `CoreAiWorldCommandExecutor`): **no
  ownerModId threading at all** (grep of `CoreAiWorldCommandExecutor.cs` for `ownerModId` = none; objects
  are name-keyed in a flat world namespace). This is why Tetris must do manual per-generation naming.
- For the Programmer role the WorldEdit *build* bindings are intentionally **off**
  (`CoreAiModsInstaller.cs:118-125`, `RegisterWorldEditBuildBindings = false`) — mods build via the Rbx
  surface. So **Rbx is the primary path and the MVP target**; WorldEdit ownership is a Phase-3 extension
  (needed for the Tetris-style demos and any host that re-enables the build bindings).

### Existing "Reset World" precedent

`WorldStateHubPage.cs:16,76-83,116-123`: a red **"Reset World"** button calls `IWorldStateManager.Reset()`
("Reset all AI/mod-spawned world objects and delete the save file"). This is the precedent + home for the
per-mod "Cleanup" action (Phase 3) and shows the Hub already owns a world-nuke control.

---

## Step 2 — Design options (Roblox-idiomatic)

### Roblox constraint (from memory: *CoreAI Rbx API = Roblox 1:1*)

In Roblox, `Instance.new('Part')` returns a **parentless** instance that does nothing until you set
`.Parent`. **Auto-parenting every `Instance.new` to a hidden container would break that 1:1 contract.**
So any container must be *offered*, not *forced*, and the **ownership tag** (not containment) has to be the
source of truth for cleanup — otherwise a parentless-but-owned object leaks.

### Option A — Ownership tag + sweep-destroy on unload (uses the existing ledger)

Sweep `GetOwnedBy(modId)` and `Destroy()` each on teardown.

- **+** Zero author-facing API change; invisible; preserves Roblox 1:1 (`Instance.new` stays parentless).
- **+** Reuses code that already exists (`InstanceRecord.OwnerModId`, `GetOwnedBy`, recursive `Destroy`).
- **+** Catches **parentless** owned objects and objects reparented anywhere in the tree — containment can't.
- **−** "I want this object to persist past unload" needs an explicit opt-out (tag doesn't clear on reparent).
- **−** Cross-mod reparent: creator's tag wins (see edge cases).

### Option B — Per-mod container Folder, destroy the subtree on unload

A `Folder`/`Model` named per mod (e.g. under `workspace`) that the mod's objects live under; unload
destroys the one container → everything under it (this is the generalized Tetris `TetrisRoot_g<gen>`
pattern, and the Roblox parallel of a plugin parenting its objects under one Instance).

- **+** Matches the Roblox mental model authors already know ("parent under my folder"); the "reparent out
  to persist" opt-out is *native* Roblox semantics (persistence == parenting).
- **+** One `Destroy()` call; visible in the explorer/Unity hierarchy; great for a "cleanup" button UX.
- **−** If auto-parented, breaks 1:1 (`Instance.new` should be parentless). If **not** auto-parented, it
  only cleans what the author remembered to parent under it → parentless/forgotten objects leak. So a pure
  container cannot be the safety net.
- **−** Cross-mod object moved *under another mod's* container is ambiguous by containment alone.

### Option C (recommended) — Tag is the safety net; container is optional ergonomic sugar

Keep the **tag sweep (A) as the guaranteed cleanup**, and in Phase 2 add the **per-mod container (B) as an
opt-in convenience** plus a **persist sanctuary** for the opt-out. Best of both: invisible correctness now,
Roblox-familiar ergonomics + explicit persistence later, 1:1 intact.

| | A: Tag sweep | B: Container subtree | C: Tag + optional container |
|---|---|---|---|
| Author API change | none | `Instance.new` reparented (breaks 1:1) or manual | none required; container is opt-in |
| Catches parentless owned objects | yes | no | yes |
| Catches reparented-away objects | yes (tag follows) | no | yes |
| "Persist past unload" opt-out | needs explicit rule | native (reparent out) | reparent to sanctuary → clear tag |
| Roblox 1:1 preserved | yes | only if not auto-parented | yes |
| Visible/inspectable grouping | no | yes | yes (when author uses the folder) |
| Cleanup-button target | iterate tag | one `Destroy()` | either |

### Opt-out for persistence (how a mod keeps an object alive past unload)

Mirror Roblox where objects persist by *where they are parented*:

- **Persist sanctuary:** a well-known instance that the sweep treats as "not owned by anyone" — reparenting
  an owned object under it (e.g. `game.ReplicatedStorage`, or an explicit `workspace.Persistent` folder)
  makes the sweep **skip it and null its `OwnerModId`**. Semantics-equivalent to Roblox "move it out of my
  container to keep it".
- Requires a small addition: `OwnerModId` is currently get-only on `InstanceRecord` (`InstanceRecord.cs:23`).
  Add an internal `ClearOwner(InstanceId)` on the registry (or "adopt" on reparent into the sanctuary).
- Alternative, more explicit: an ownership attribute/flag (`instance:SetAttribute("CoreAIPersist", true)`)
  the sweep honors — no new C# surface, uses existing attributes (`RbxInstance.cs:423` `SetAttribute`). Good
  as a secondary opt-out for objects the author does not want to move.

### Explicit "Cleanup" button

- Add `ILuaModRuntime.CleanupModInstances(modId)` (or reuse the sweep directly) that destroys a mod's owned
  instances **without unloading the mod** — the mod keeps running and can respawn. Wire a per-mod
  **"Cleanup"** button in the Hub **Mods** tab next to load/unload.
- Keep the global **"Reset World"** (`WorldStateHubPage.cs:76-123`) as the nuke-everything path; optionally
  route it through per-mod sweeps + the WorldEdit reset so both namespaces clear.

### Interaction with world-state save/restore & reload

- Ordering contract already handled: mod rehydrate runs **after** startup world restore via
  `WorldRestoreGate` (`CoreAiModsInstaller.cs:201-217`) — a mod re-spawning its objects won't fight the
  snapshot. The unload sweep runs at teardown, unrelated to restore ordering, so it composes cleanly.
- **Reload safety on the Rbx path is better than WorldEdit:** `InstanceId`s are never reused
  (`InstanceRecord.cs:11`), so a reload that destroys the old owner's instances and creates new ones gets
  fresh ids and fresh GameObjects — **no same-frame name collision** like the WorldEdit path has (which is
  the entire reason for Tetris's per-generation naming, `LuaPlatformExampleController.cs:392-397`). Note the
  binder destroys GameObjects via `Object.Destroy` (deferred) in play mode (`:648-663`), but since ids/refs
  differ, deferred destroy of the old set never clashes with the new set.
- **Decision — do NOT sweep on `Reload` by default.** Reload keeps the same `OwnerModId`; a blanket sweep
  would nuke objects the reloaded code intends to keep, and Roblox hot-reload of a Script does not delete
  what a previous run created. Leave reload cleanup to the author (as Tetris already does). Make
  reload-sweep an opt-in later if a mod manifest wants "clean rebuild on reload".

### Edge cases

- **Quarantine:** `TeardownModEffects(..., Quarantine)` keeps the mod loaded (`:910`). **Do not sweep** —
  the objects must survive so auto-repair / `ReloadMod` can fix the mod. Sweep only on real removal
  (`Unload`, and `ForgetMod` which routes through `Unload`).
- **Cross-mod objects:** an instance created by A but reparented under B. Tag = A (creator). Rule: **tag
  wins** — A's unload destroys it (recursively taking its children), even if it currently sits under B.
  Document this; it is the least-surprising "you made it, you own it".
- **Hot-reload same-frame destroy+respawn:** safe on the Rbx path (unique ids, see above). The
  WorldEdit path needs the per-generation trick until Phase 3.
- **DontDestroyOnLoad ticker outliving the host (known audit finding):** the ticker is `DontDestroyOnLoad`
  (`CoreAiModsInstaller.cs:169-171`) and disposed via `RegisterDisposeCallback` (`:146-153`). The sweep on
  scope-dispose should also run for **all** loaded mods, so a scope teardown that kills the ticker does not
  leave that runtime's instances orphaned. Add a "sweep all owned" on dispose alongside the ticker destroy.
- **Mod crash mid-frame:** unload is deferred out of the dispatch callback in existing code
  (`LuaPlatformExampleController.cs:101-111`); the sweep runs from `TeardownModEffects` which is already the
  safe teardown point, so re-entrancy is not introduced.
- **WebGL:** single-threaded, `Object.Destroy` deferred still fine; no threading concerns (registry is
  main-thread-only by invariant, `InstanceRegistry.cs:11-14`).
- **`Instance.new` parented to a soon-destroyed parent:** already handled — detached objects park under the
  world parent (`InstanceGameObjectBinder.cs:147-160`), so the sweep still finds and destroys them by tag.

---

## Step 3 — Recommendation & phased plan

**Chosen approach: Option C** — the **tag sweep is the guaranteed safety net (MVP)**; the **per-mod
container + persist sanctuary are opt-in ergonomics (Phase 2)**; **WorldEdit ownership + Cleanup button
(Phase 3)**.

### MVP (small, invisible, uses the existing ledger + existing seam)

Wiring-only; no Lua API change; no author change. Cleanup "just happens" on unload/forget.

1. **Subscribe the Rbx registry to the teardown seam.** In `CoreAiModsInstaller.RegisterCoreAiMods`
   (`CoreAiModsInstaller.cs:155-225`, where both the concrete `LuaCsModRuntime` and
   `robloxApi.Registry` are in scope), do:
   ```
   runtime.ModTearingDown += (modId, reason) =>
   {
       if (reason != LuaModTeardownReason.Unload) return;      // not Reload, not Quarantine
       foreach (var inst in registry.GetOwnedBy(modId).ToArray())
           inst.Destroy();                                     // recursive → binder.OnDestroyed
   };
   ```
   `GetOwnedBy` already exists (`InstanceRegistry.cs:248`); `Destroy` is recursive + idempotent
   (`RbxInstance.cs:375`); `OnInstanceDestroyed` releases the GameObject (`:396` → binder `:162`).
2. **Also sweep on scope dispose** (all loaded mods) inside the existing `RegisterDisposeCallback`
   (`CoreAiModsInstaller.cs:146-153`), so the `DontDestroyOnLoad` ticker teardown never orphans instances.
3. **Tests:** load a mod that `Instance.new('Part')`s N parts + parents under `workspace`; assert
   `binder.BoundCount` (`InstanceGameObjectBinder.cs:107`) drops to baseline after `UnloadMod`; assert
   parentless-but-owned parts are also destroyed; assert `Reload` does **not** sweep; assert `Quarantine`
   does **not** sweep. (EditMode, no host GameObject needed for the registry-level asserts.)

*API surface added in MVP: none public.* Optionally promote `ModTearingDown` onto `ILuaModRuntime` if the
subscription is done through the interface instead of the concrete type.

### Phase 2 — Roblox-idiomatic ergonomics + persist opt-out

4. **Per-mod container:** lazily create a `Folder` named per mod (e.g. `mod:<id>` or a friendly label)
   under `workspace` on first `Instance.new` from that mod, and expose it to Lua as an obvious global
   (Roblox-style `script.Parent`-like handle, e.g. `mod_root` / `script`). Authors *may* parent under it;
   they are never forced to. Unload still sweeps by tag (the container is a child of that sweep).
5. **Persist sanctuary:** define `workspace.Persistent` (or accept `game.ReplicatedStorage`); reparenting an
   owned instance under it clears `OwnerModId` so the sweep skips it. Add internal
   `InstanceRegistry.ClearOwner(id)` (make `InstanceRecord.OwnerModId` internally settable,
   `InstanceRecord.cs:23`) invoked from the reparent pipeline. Secondary opt-out: honor a
   `SetAttribute("CoreAIPersist", true)` flag in the sweep (no new C# surface).
6. **Docs/skill:** one paragraph in the RbxApi skill — "objects you create are cleaned up when your mod
   unloads; to keep one, move it under `workspace.Persistent`." Keeps the contract obvious.

### Phase 3 — WorldEdit parity + Cleanup UI + global reset

7. **Thread `ownerModId` through the WorldEdit path** (`coreai_world_spawn` → `IAiGameCommandSink` →
   `CoreAiWorldCommandExecutor`) and keep a per-mod name registry so name-keyed objects sweep too; this lets
   the Tetris demo drop its manual per-generation trick.
8. **Hub "Cleanup" button** per mod in the Mods tab → `CleanupModInstances(modId)` (sweep without unload).
   Model the UX on the existing "Reset World" control (`WorldStateHubPage.cs:76-123`).
9. **Global "Reset World"** optionally routes through all per-mod sweeps + the WorldEdit reset so both the
   Rbx and world namespaces clear from one button.

### What to defer

- Reload-time sweep (opt-in via mod manifest) — not default.
- `console:`/`ai:` origin cleanup ("remove everything from invocation N") — the tags exist
  (`OriginTag.cs`) but selective per-invocation undo is a separate tooling feature.
- Signals firing `Destroying`/`AncestryChanged` on teardown (already a tracked MVP2 TODO,
  `RbxInstance.cs:385`).

---

## Minimal touch-point summary

| Change | File:line | Phase |
|---|---|---|
| Subscribe sweep to `ModTearingDown` (Unload only) | `CoreAiModsInstaller.cs:155-225` | MVP |
| Sweep-all on scope dispose | `CoreAiModsInstaller.cs:146-153` | MVP |
| (reuse) owned-instance query | `InstanceRegistry.cs:248` `GetOwnedBy` | MVP |
| (reuse) recursive destroy → binder release | `RbxInstance.cs:375`, `InstanceGameObjectBinder.cs:162` | MVP |
| Per-mod container Folder + Lua handle | new, parented under `DataModelBootstrap` Workspace (`:19`) | P2 |
| `ClearOwner` / persist sanctuary | `InstanceRecord.cs:23`, `InstanceRegistry.cs` | P2 |
| `ownerModId` on WorldEdit path | `CoreAiWorldCommandExecutor.cs` | P3 |
| Per-mod Cleanup button | Hub Mods tab (cf. `WorldStateHubPage.cs:76-123`) | P3 |
