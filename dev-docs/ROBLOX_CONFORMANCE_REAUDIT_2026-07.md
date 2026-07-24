# Roblox-API Conformance RE-AUDIT (post v6.4.0)

Date: 2026-07-24
Scope: READ-ONLY. No code changes, no git, no Play Mode.
Reference: local Roblox mirror `D:\Git\RobloxDocs` (`api-dump/Full-API-Dump.json`, creator-docs).

## BOTTOM LINE

- The three bundled samples are **portable Roblox Lua — YES**. All three use only
  `game:GetService`, `RunService.Heartbeat:Connect`, `UserInputService`, `Instance.new`,
  `Vector3/Color3/CFrame/Enum`, part properties, `print`, and standard Lua. Zero stopgap
  globals (`hooks_*`, `mods_*`, `store_*`, `events_emit`, `report`, `camera_*`, `coreai_world_*`,
  `unity_*`, `mod_id`) — a repo-wide grep of the samples directory returns no matches.
- The new v6.4.0 RunService surface is **1:1 with Roblox** for class name and all three signal
  names/argument shapes.
- No NEW non-Roblox runtime surface was introduced by v6.4.0. The one remaining trap is in the
  **skill's examples** (they use `report(...)`, not `print(...)`) — a MEDIUM doc issue, not a
  runtime one. A mod author who copies the samples is safe; one who copies the skill's inline
  examples ships a non-portable `report` call.

---

## 1. The three bundled samples — CLEAN

| Sample | File | Verdict |
|---|---|---|
| Welcome | `Assets\CoreAIMods\Runtime\Resources\CoreAIMods\sample_welcome.lua` | Pure Roblox |
| Lane Racer | `Assets\CoreAIMods\Runtime\Resources\CoreAIMods\sample_lane_racer.lua` | Pure Roblox |
| Tetris 3D | `Assets\CoreAIMods\Runtime\Resources\CoreAIMods\sample_tetris3d.lua` | Pure Roblox |

Surface actually used, all Roblox-real:
- `game:GetService("RunService")`, `game:GetService("UserInputService")`
- `RunService.Heartbeat:Connect(function(dt) ... end)` as the single game loop, `dt`-scaled motion
- `UserInputService:IsKeyDown(Enum.KeyCode.X)` with rising-edge polling
- `Instance.new("Folder"|"Part")`, `.Parent = workspace`, `:Destroy()`
- `Vector3.new`, `Color3.fromRGB`, `CFrame.lookAt`, `Enum.KeyCode.*`, `Enum.CameraType.Scriptable`
- `workspace.CurrentCamera.CameraType` / `.CFrame` writes
- part props `Name/Size/Color/Position/Anchored`, `print`, and stock Lua (`math/table/ipairs`)

Grep of `sample_*.lua` for the full stopgap token list: **no matches**. No offenders to list.

Minor (LOW, informational — NOT a portability break): the samples read `workspace.CurrentCamera`
and set `CameraType = Enum.CameraType.Scriptable`, then drive the pose with `CFrame.lookAt`. That
is exactly the Roblox-idiomatic scripted-camera pattern, so it exports cleanly.

## 2. RunService surface — 1:1 (HIGH-severity checks all PASS)

`Assets\CoreAIMods\Runtime\RbxApi\Instances\RbxRunService.cs`

- Class name: `Name = "RunService"` (line 18). Matches api-dump class `RunService`. PASS.
- Signal members present: `Heartbeat` (l.23), `Stepped` (l.27), `RenderStepped` (l.31). All three
  exist on Roblox's `RunService`. PASS.
- Argument shapes vs `Full-API-Dump.json` RunService events:
  - api-dump `Heartbeat(deltaTime: double)` ⟷ CoreAI `Heartbeat.Fire(delta)` (l.60). PASS.
  - api-dump `RenderStepped(deltaTime: double)` ⟷ CoreAI `RenderStepped.Fire(delta)` (l.65). PASS.
  - api-dump `Stepped(time: double, deltaTime: double)` ⟷ CoreAI `Stepped.Fire(_runTime, delta)`
    (l.55), where `_runTime` is the accumulated frame time. PASS — first arg is time, second is dt,
    in the correct order.
- Lua exposure: `LuaCsRobloxInstanceBindings.cs` `TryReadRunService` (l.806-830) maps keys
  `Heartbeat/Stepped/RenderStepped` to the wrapped signals; reads are Read-tier (no WorldEdit),
  correct since connecting a listener mutates nothing.

No name or arg mismatch found. Nothing to flag as HIGH here.

LOW (intra-frame ordering, does not affect portability): CoreAI fires `Stepped → Heartbeat →
RenderStepped` in one pump (`RbxRunService.Step`, l.41-67). Real Roblox spreads these across the
render/physics pipeline (RenderStepped before render, Stepped before physics, Heartbeat after
physics) and, additionally, `RenderStepped` is client-only and errors on a server context. CoreAI
fires all three unconditionally each frame. For mod code that connects any single signal this is
behaviourally indistinguishable; only a mod depending on relative cross-signal ordering or on
server-side RenderStepped-throws would notice. Acceptable for MVP; the class doc already TODO-flags
the MVP2 scheduler replacement (l.40). No fix required now.

Note (MEDIUM disclosure gap, not a trap): newer Roblox deprecates `Stepped`/`RenderStepped` in
favour of `PreSimulation`/`PreRender`/`PostSimulation` (also in the api-dump). CoreAI implements
the classic names, which still exist and fire identical args in Roblox, so exports stay valid.
The modern aliases are simply not yet provided — MEDIUM (missing/partial), not wrong.
Fix (optional, future): alias `PreSimulation→Stepped-args`, `PreRender→RenderStepped`,
`PostSimulation→Heartbeat` when the scheduler lands.

## 3. Signal / connection semantics — matches Roblox (except Wait, correctly stubbed)

`Assets\CoreAIMods\Runtime\RbxApi\Instances\RbxScriptSignal.cs`,
`RbxScriptConnection` (same file, l.10-39),
Lua binding `Assets\CoreAIMods\Runtime\Scripting\LuaCs\LuaCsRobloxDatatypeBindings.cs`
(signal method dispatch l.916-990; connection members l.1060-1090).

- `signal:Connect(fn)` returns an `RbxScriptConnection` — typeof name is `"RBXScriptConnection"`
  and signal typeof is `"RBXScriptSignal"` (`LuaCsRobloxValues.cs` l.256-257), matching Roblox
  `typeof` exactly. PASS.
- `connection.Connected` (bool) and `connection:Disconnect()` are exposed
  (`LuaCsRobloxDatatypeBindings.cs` l.1064-1069; `RbxScriptConnection` l.26,29). Disconnect is
  idempotent (`Connected` guard, l.31-37). PASS.
- `:Once(fn)` — REAL, matches Roblox semantics: it stores a connection and disconnects **before**
  invoking the handler (`RbxScriptSignal.Dispatch` l.149-152), so a re-fire from inside the handler
  can't double-invoke. Available on any dispatch-enabled signal (UserInputService AND now
  RunService). PASS.
- `:Wait()` — STUBBED. Raises `NOT_IMPLEMENTED` (`RbxScriptSignal.Wait` l.95-98 →
  `Stub("Wait")` l.192-196) because it needs the MVP2 coroutine scheduler. This is a loud,
  honest stub (LOW/MEDIUM: missing feature, correctly signalled, matches the skill's own
  documentation). The samples don't use it.
- Re-entrancy: `Dispatch` iterates a snapshot buffer so a handler may Connect/Disconnect the same
  signal mid-fire (l.121-165) — Roblox-consistent behaviour.

## 4. The Rbx API skill — MOSTLY clean, ONE MEDIUM trap in examples

`Assets\CoreAiUnity\Resources\AgentSkills\RbxApi.txt`

PASS items:
- The primary game-loop example (§11, l.196-224) uses `RunService.Heartbeat:Connect(function(dt)
  ... end)` with `dt`-scaled motion and `UserInputService:IsKeyDown` — NOT `hooks_every`. Correct.
- §10 explicitly redirects `task.wait/spawn/...` to `RunService.Heartbeat` (l.158-160). Correct.
- §1 discloses that stopgap APIs (`hooks_every`, `store_set`, `coreai_world_*`) live in a separate
  "Lua Modding" skill (l.4-7). Per the audit brief this is acceptable disclosure, not a trap.

MEDIUM — the skill's own examples teach `report(...)`, a CoreAI-only global, as the way to print:
- `report(tostring(p.Position))` — l.192
- `report(m:GetAttribute("Team") .. ...)`, `report(tostring(m:HasTag("Enemy")))` — l.234-235
- `report(err)`, `report(err2)` — l.241,246
  Roblox has no `report`; the portable call is `print`. A model that mirrors these examples ships
  a non-portable line (it would error in Roblox and does not round-trip). The three shipped samples
  correctly use `print`, so this is a skill-example inconsistency, not a runtime one.
  Severity MEDIUM (silently non-portable if copied). Minimal fix: replace `report(` with `print(`
  in the §11 examples of `RbxApi.txt` (5 occurrences) — no runtime change, doc-only.

LOW — §7 (l.116-118) advertises convenience globals `camera_set_cframe(cf)` / `camera_follow(inst)`.
These are non-Roblox, but the section leads with the Roblox-correct `CurrentCamera.CFrame` /
`CameraSubject` writes and labels the globals as a convenience shorthand. Mild steer only; the
samples use the Roblox way. Optional fix: move the two convenience globals to the "Lua Modding"
skill so `RbxApi.txt` stays 100% portable-surface.

## 5. Broader surface sanity — earlier findings UNCHANGED

Confirmed against `RbxApi.txt` and the binding sources; no regression, no new trap:
- `Instance.new` creatable classes still limited to **Part / Folder / Model** (§4 l.65). Matches
  prior audit.
- `game:GetService` whitelist now includes **RunService** alongside the prior services (§4 l.68-70)
  — the intended v6.4.0 addition; all names are real Roblox services.
- Part `Material` / `Orientation` / `Rotation` still **NOT_IMPLEMENTED** (loud), use CFrame for
  rotation (§5 l.93-94, §10 l.165). Unchanged.
- `task.*` still stubbed (§10 l.158-160). Unchanged.
- Instance-tree signals (`ChildAdded`, `WaitForChild`-absent, etc.) still DO NOT dispatch (§10
  l.161-164) — but **UserInputService AND now RunService** signals DO dispatch. The
  `SupportsDispatch` split in `RbxScriptSignal` (l.62-79) is exactly what draws that line: input +
  RunService signals are constructed with `supportsDispatch: true`; tree signals are not, and their
  `Connect/Once/Wait` raise the MVP2 stub (l.174-176, 192-196). Consistent with the documented
  contract.
- No NEW non-Roblox runtime global introduced by v6.4.0. RunService is the only added surface and
  it is Roblox-real.

## Severity summary

| # | Finding | Severity | Location |
|---|---|---|---|
| A | Samples clean, 1:1 portable | PASS | `sample_*.lua` |
| B | RunService class + Heartbeat/Stepped/RenderStepped names & args match api-dump | PASS | `RbxRunService.cs` |
| C | Connection `.Connected`/`:Disconnect`, `:Once` real; `:Wait` loud stub | PASS / LOW | `RbxScriptSignal.cs`, `RbxScriptConnection` |
| D | Skill §11 examples use `report(` instead of `print(` | **MEDIUM** | `RbxApi.txt` l.192,234,235,241,246 |
| E | `Stepped`/`RenderStepped` classic names only; no `PreSimulation`/`PreRender`/`PostSimulation` aliases | MEDIUM (missing) | `RbxRunService.cs` |
| F | `camera_set_cframe`/`camera_follow` convenience globals in an otherwise-portable skill | LOW | `RbxApi.txt` §7 |
| G | Single-pump intra-frame signal order; RenderStepped fires on all contexts | LOW | `RbxRunService.cs` Step |

Only finding D is a copy-me trap, and it lives in the skill's inline examples, not in shipped code
or the runtime. Fixing those five `report(` → `print(` edits closes the last portability gap a mod
author following the skill could hit.
