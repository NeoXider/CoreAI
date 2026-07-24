# Roblox API Conformance Audit — CoreAI Lua Surface

**Date:** 2026-07-24
**Type:** READ-ONLY audit (no code changes). DEV doc — not user-facing.
**Reference truth:** `D:\Git\RobloxDocs\api-dump\Full-API-Dump.json` (official class/enum/member dump)
and `D:\Git\RobloxDocs\creator-docs` (official datatype docs).
**Audited surface:** the Lua globals and Instance/datatype members mods author against, in
`Assets\CoreAIMods\Runtime\Scripting\LuaCs\`, `Assets\CoreAIMods\Runtime\RbxApi\`, and the
agent-facing skill `Assets\CoreAI\Runtime\Core\Features\AgentPrompts\BuiltInRbxApiSkillText.cs`.

## Executive summary

The **Rbx surface is high fidelity**. Every enum value, datatype constructor/member, and Instance
method that IS implemented matches Roblox 1:1 (verified against the API dump). There are **no HIGH
"silently wrong vs Roblox" defects** in the implemented surface — where CoreAI deviates it is a
**loud NOT_IMPLEMENTED / BAD_ARGUMENT stub**, not a wrong value returned silently.

Two families of deviation exist, both expected:
1. A large set of **bespoke CoreAI globals** (`hooks_*`, `store_*`, `events_*`, `mods_*`,
   `coreai_world_*`, `camera_set_cframe`, `camera_follow`) that do not exist in Roblox. This is
   **known, standing debt** — the directive is "Roblox-API-only until MVP" (memory:
   `coreai-roblox-api-only-until-mvp`) and the skill text openly presents these as the "classic"
   mixable surface. Framed here as **replace-before-MVP**, not bugs. List is complete below.
2. **Missing/partial** Roblox surface that is correctly *named* but stubbed (task library,
   instance-tree signals, WaitForChild yield, Model pivot, BasePart Material/Orientation/Rotation,
   Vector2:Angle, narrow Instance.new / GetService whitelists).

Note on the prompt's `camera_capture`: it is **not** a Lua mod global. It is a chat/vision LLM tool
(`Assets\CoreAiUnity\Runtime\Source\Features\Vision\CameraLlmTool.cs`) and is out of scope for the
Lua surface. Only `camera_set_cframe` and `camera_follow` exist as mod globals.

---

## List 1 — NON-ROBLOX surface (bespoke CoreAI, replace-before-MVP)

All of these are registered into every mod state and do not exist in the Roblox API. None is a bug;
each needs a Roblox idiom before the "Roblox-API-only" MVP gate. Ranked by how load-bearing /
visible the global is.

### 1.1 Classic modding globals — `LuaCsModRuntime.cs` (documented at lines 38–44)

| CoreAI global | Roblox idiom that should replace it | Notes |
|---|---|---|
| `hooks_every(seconds, fn)` | `RunService.Heartbeat:Connect(fn)` (or a `task.wait` loop) | periodic tick |
| `hooks_on(event, fn)` | `BindableEvent.Event:Connect` (same-place) / `RemoteEvent.OnClientEvent` | named-event handler |
| `events_emit(name, payload)` | `BindableEvent:Fire(payload)` / `RemoteEvent:FireClient` | payload is plain-data-copied |
| `store_set(key, v)` / `store_get(key)` | `DataStoreService:GetDataStore():SetAsync/GetAsync` (persistent) or Instance attributes (transient) | per-mod k/v |
| `mod_id()` | `script.Name` / a ModuleScript's identity | no direct 1:1 |
| `mods_export` / `mods_get` / `mods_call` / `mods_list_exports` | `require(ModuleScript)` returning a function table | cross-mod call surface |

Severity: **N/A (expected debt)** — documented pre-MVP stopgap. Fix: port each to the Roblox idiom
listed, or keep behind a non-Roblox "platform" namespace clearly outside the Rbx surface.

### 1.2 World-command globals — `LuaCsWorldRuntimeBindings.cs` (names at lines 29–45)

14 globals, all WorldEdit-gated: `coreai_world_spawn`, `coreai_world_change`,
`coreai_world_destroy`, `coreai_world_load_scene`, `coreai_world_reload_scene`,
`coreai_world_set_active`, `coreai_world_set_color`, `coreai_world_spawn_batch`,
`coreai_world_grid`, `coreai_world_begin`, `coreai_world_commit`, `coreai_world_rollback`,
`coreai_world_play_animation`, `coreai_world_play_sound`.

Roblox idioms:
- spawn/change/destroy/set_color/set_active/grid/spawn_batch → the **Rbx surface already covers
  this**: `Instance.new("Part")` + `.Parent`, `part.CFrame/Position/Size`, `part.Color`,
  `instance:Destroy()`, and a Lua loop for grids/batches.
- `play_animation` → `Animator:LoadAnimation(anim):Play()` (AnimationTrack).
- `play_sound` → a `Sound` instance + `Sound:Play()`.
- `load_scene` / `reload_scene` → **no Roblox equivalent** (Roblox has no scene-load; nearest is
  TeleportService/place swap). Engine-control primitive; keep out of the Rbx surface.
- `begin` / `commit` / `rollback` → **no Roblox equivalent** (world transactions are a CoreAI
  concept). Keep as a platform primitive, not Rbx.

Severity: **N/A (expected debt)**. Fix: migrate the spatial ones onto the Rbx Instance surface;
keep scene/transaction primitives in an explicitly non-Roblox namespace.

### 1.3 Camera convenience globals — `LuaCsRobloxApiBindings.cs`

- `camera_set_cframe(cf)` — registered lines 203–204, built 216–225. Roblox idiom:
  `workspace.CurrentCamera.CFrame = cf`.
- `camera_follow(instance_or_nil)` — registered 205–206, built 227–250. Roblox idiom:
  `workspace.CurrentCamera.CameraSubject = inst` (nil to clear).

Both already have exact Roblox-member equivalents implemented in the same file (see §7 of the
skill), so these two globals are pure sugar and are the cheapest to drop.
Severity: **N/A (expected debt)** — thin aliases over faithful members; remove before MVP.

---

## List 2 — INCORRECT / DIVERGENT (named like Roblox but deviates)

No HIGH silent-wrong defects found. All items below are **loud** (throw NOT_IMPLEMENTED or
BAD_ARGUMENT) or **missing**, ranked by portability impact.

### 2.1 MEDIUM — Vector2 is missing `:Angle`
- CoreAI: `LuaCsRobloxDatatypeBindings.cs` Vector2 method table lines 325–343 (Dot, Cross, Lerp,
  FuzzyEq, Abs, Ceil, Floor, Sign, Max, Min — **no Angle**). Skill text
  `BuiltInRbxApiSkillText.cs:57` explicitly documents "same methods except no Angle".
- Roblox: `creator-docs/.../datatypes/Vector2.yaml` lists `Vector2:Angle` (and the dump/docs give
  `Vector2:Angle(other, isSigned)`).
- Impact: a real Roblox method is absent; corpus scripts calling `v2:Angle(...)` error.
- Fix: add `Angle(other, isSigned)` to the Vector2 method table backed by an `RbxVector2.Angle`;
  update the skill line.

### 2.2 MEDIUM — `WaitForChild` ignores the `timeout` param and throws instead of yielding
- CoreAI: `LuaCsRobloxInstanceBindings.cs:268–282` — reads only `name` (arg 1); when the child is
  absent it throws NOT_IMPLEMENTED rather than yielding.
- Roblox: `Instance:WaitForChild(childName: string, timeOut: number?)` **yields** until the child
  exists (or returns nil after `timeOut`).
- Impact: the second argument is silently unread; blocking semantics differ (loud, MVP2-gated).
- Fix (MVP2 scheduler): implement the yield + optional timeout; until then, at least surface the
  ignored-timeout in the message.

### 2.3 MEDIUM — Instance-tree signals do not dispatch
- CoreAI: signals (`ChildAdded`, `ChildRemoved`, `DescendantAdded`, `AncestryChanged`,
  `Destroying`, `AttributeChanged`, `GetPropertyChangedSignal`, …) are wrapped
  (`LuaCsRobloxInstanceBindings.cs:122–133, 377–380`) but `:Connect/:Once/:Wait` raise the MVP2
  stub for non-dispatch signals (`LuaCsRobloxDatatypeBindings.cs:938–970`). Only UserInputService
  signals actually fire.
- Roblox: all these `RBXScriptSignal`s connect and fire.
- Impact: MVP2-gated; loud. Fix: land the signal scheduler.

### 2.4 MEDIUM — `task.*` library is stubbed
- CoreAI: `LuaCsRobloxApiBindings.cs:296–330` — `task.wait/spawn/defer/delay/cancel` throw
  NOT_IMPLEMENTED; `task.synchronize/desynchronize` are logged no-ops.
- Roblox: `task` is a real Luau global library with all of these.
- Impact: loud, documented MVP2. Fix: ModScheduler + TaskLibrary (already TODO-marked).

### 2.5 MEDIUM — BasePart `Material`, `Orientation`, `Rotation` throw
- CoreAI: `LuaCsRobloxInstanceBindings.cs:100–103, 575–582, 632–637, 913–922` — these three are
  routed to a loud NOT_IMPLEMENTED stub.
- Roblox: all three are standard BasePart properties.
- Impact: loud. Fix: Material needs the material catalog; Orientation/Rotation need Euler
  decomposition (both already scheduled follow-ups).

### 2.6 MEDIUM — `Model:PivotTo` / `Model:GetPivot` throw
- CoreAI: `LuaCsRobloxInstanceBindings.cs:394–400`.
- Roblox: `PVInstance:PivotTo(CFrame)` / `:GetPivot()`.
- Impact: loud, MVP2. Fix: aggregate child-part CFrames.

### 2.7 MEDIUM — `Instance.new` creatable whitelist is very narrow
- CoreAI: only `Part`, `Folder`, `Model` are creatable (`ClassCatalog.cs:105–136`,
  IsCreatable=true rows; skill text `BuiltInRbxApiSkillText.cs:89`). Any other class name errors.
- Roblox: hundreds of classes are creatable (`Sound`, `Attachment`, `SpotLight`, `Script`,
  `ScreenGui`, `Weld`, `ProximityPrompt`, …).
- Impact: scoping debt, loud BAD_ARGUMENT. Fix: expand the catalog as each class's behavior lands.

### 2.8 MEDIUM — `game:GetService` whitelist is narrow
- CoreAI services: Workspace, Lighting, ReplicatedStorage, ServerStorage, ServerScriptService,
  StarterPlayer, UserInputService (`ClassCatalog.cs:113, 126–134`).
- Roblox: many more (Players, RunService, TweenService, CollectionService, SoundService, …).
- Impact: scoping, loud. Fix: register services as their MVP phase lands.
- Sub-note: `Lighting` is structure-only — `ClockTime`/`Ambient`/etc. are absent and hit the loud
  stub (`ClassCatalog.cs:126`). Correct-by-design for MVP1, flagged for completeness.

### 2.9 LOW — `DataModel:BindToClose(fn)` silently discards its callback
- CoreAI: `LuaCsRobloxInstanceBindings.cs:387–391` calls `BindToClose(null)` — the passed Lua
  function (arg 1) is never read and never fires.
- Roblox: `DataModel.BindToClose(function)` runs the function on game shutdown.
- Impact: this is the one place that *silently* accepts and drops a Roblox argument (no error). Low
  real impact (mods have no shutdown), but it is a portability trap.
- Fix: either read+store the callback for a future shutdown hook, or raise NOT_IMPLEMENTED so the
  drop is loud rather than silent.

### 2.10 LOW — `Instance.fromExisting` throws (backlog, not scheduled)
- CoreAI: `LuaCsRobloxApiBindings.cs:289–291`. Roblox: `Instance.fromExisting(instance)`.
- Impact: loud; `Clone()` covers the corpus. Fix: backlog.

### 2.11 LOW — input semantics simplifications (cosmetic, not wrong values)
- `gameProcessedEvent` is always `false` (`RbxUserInputService.cs:30, 158/169/202/223`); Roblox
  sets it true when a GUI consumed the input. No GUI layer exists yet.
- `GetMouseLocation()` returns the raw top-left pixel; Roblox subtracts the GUI inset. No topbar
  concept yet (`RbxUserInputService.cs:116–120`).
- Fix: revisit when the GUI slice lands. Both LOW.

---

## List 3 — DONE-RIGHT (verified 1:1 with Roblox)

Confirmed against `Full-API-Dump.json` unless noted.

1. **All 10 registered Enum types — names AND numeric values match exactly** (`RbxEnum.cs:107–231`):
   KeyCode (letters A=97..Z=122, digits 48+, Space=32, Return=13, F1=282..F15=296, World0=160,
   KeypadZero=256, gamepad Button/DPad/Thumbstick 1000–1017, Euro=321, Undo=322), CameraType
   (Fixed=0…Orbital=7), UserInputType (…Touch=7, Keyboard=8, TextInput=20, InputMethod=21, None=22),
   UserInputState (Begin=0…None=4), MouseBehavior (Default/LockCenter/LockCurrentPosition),
   PartType (Ball=0…CornerWedge=4), NormalId (Right=0…Front=5), Axis (X/Y/Z), RotationOrder
   (XYZ=0…ZYX=5), Material (Plastic=256, SmoothPlastic=272, Neon=288, Wood=512 … Rubber=2311).
2. **Instance navigation/lifecycle method names & signatures** — FindFirstChild(name, recursive),
   FindFirstChildOfClass, FindFirstChildWhichIsA(cls, recursive), FindFirstAncestor*,
   GetChildren, GetDescendants, GetFullName, IsDescendantOf, IsAncestorOf, Clone, Destroy,
   ClearAllChildren (`LuaCsRobloxInstanceBindings.cs:246–331`). FindFirstChild's `(name, recursive)`
   matches the dump exactly.
3. **`IsA` on every instance** — Roblox defines `IsA` on `Object` (dump: `Object.IsA(className)`);
   CoreAI exposes it on all instances via `ClassCatalog.IsA` ancestry walk. Correct placement.
4. **ServiceProvider members on the DataModel** — `GetService`/`FindService` and, crucially,
   `BindToClose` are gated to `RbxDataModel` (`LuaCsRobloxInstanceBindings.cs:383–391, 471–482`).
   The dump confirms `BindToClose` is a `DataModel` member, **not** `ServiceProvider` — CoreAI
   places it correctly (behavior stub aside, see 2.9).
5. **Class ancestry** matches the dump: PVInstance:Instance, Model:PVInstance, BasePart:PVInstance,
   WorldRoot:Model, Workspace:WorldRoot, Camera:Instance, DataModel:ServiceProvider
   (`ClassCatalog.cs:108–134`).
6. **Datatype constructors & members** (`LuaCsRobloxDatatypeBindings.cs`, backing structs verified):
   - Vector3 — `.new/.zero/.one/.xAxis/.yAxis/.zAxis`, FromNormalId, FromAxis, fields
     X/Y/Z/Magnitude/Unit, methods Dot/Cross/Lerp/Angle(other, axis?)/FuzzyEq/Abs/Ceil/Floor/Sign/Max/Min,
     operators + - * / unary-. Signatures match (`RbxVector3.cs:31,56,102`).
   - CFrame — new() identity, new(x,y,z), new(pos), new(pos,lookAt) deprecated overload,
     7-arg quaternion, 12-arg matrix; identity/lookAt(at,lookAt,up?)/lookAlong/Angles/
     fromEulerAngles(...,RotationOrder?)/fromEulerAnglesXYZ/YXZ/fromOrientation/fromAxisAngle/
     fromMatrix(pos,vX,vY,vZ?); fields Position/X/Y/Z/Rotation/Right-Up-Look/X-Y-ZVector; methods
     Inverse/ToWorld-ObjectSpace/Point*/Vector*/Lerp/Orthonormalize/FuzzyEq/GetComponents. Signatures
     match the dump (`RbxCFrame.cs:69,75,105,112,140`).
   - Color3 — new(0..1), fromRGB, fromHSV, fromHex (3- and 6-digit), fields R/G/B, methods
     Lerp/ToHSV/ToHex; tostring "r, g, b" (`RbxColor3.cs`). Matches.
   - Vector2 (except Angle, 2.1), UDim (Scale/Offset, + - unary-), UDim2 (new(sx,ox,sy,oy) /
     new(udimX,udimY) / fromScale / fromOffset, fields X/Y/Width/Height, Lerp), Random
     (new()/new(seed), NextNumber()/NextNumber(min,max)/NextInteger(min,max)/NextUnitVector/
     Clone/Shuffle). All names/shapes match.
   - Enum tostring `"Enum.<Type>.<Item>"` and identity equality (`RbxEnum.cs:25,91`).
7. **UserInputService** — InputBegan/InputEnded/InputChanged fire `(InputObject, gameProcessedEvent)`;
   IsKeyDown(Enum.KeyCode)→bool, GetKeysPressed()→InputObject[], GetMouseLocation()→Vector2,
   MouseBehavior read+write; InputObject fields KeyCode/UserInputType/UserInputState/Position/Delta
   (`RbxUserInputService.cs`, `LuaCsRobloxInstanceBindings.cs:673–785`). Member names match Roblox.
8. **Camera** — `workspace.CurrentCamera` resolves the Camera; `CFrame`/`CameraType`/`CameraSubject`
   read+write; default CameraType = `Custom` (Roblox default) (`LuaCsRobloxApiBindings.cs:80–84`,
   `LuaCsRobloxInstanceBindings.cs:791–851`).
9. **`Instance.new(class, parent)`** — the deprecated parent arg is accepted with a once-per-mod
   deprecation note, matching Roblox's "works but discouraged" status
   (`LuaCsRobloxApiBindings.cs:257–287`).

---

## Method

- Enum names/values cross-checked programmatically against `Full-API-Dump.json` (all 10 types, plus
  spot checks on KeyCode letters/digits/F-keys/gamepad/World/Keypad ranges and Material values).
- Member ownership (IsA→Object, BindToClose→DataModel, FindFirstChild params) resolved from the dump.
- Vector2 method set cross-checked against `creator-docs/.../datatypes/Vector2.yaml`.
- Datatype and Instance signatures read directly from the CoreAI `RbxApi` structs and the LuaCs
  binding dispatch.

No files were modified.
