# Stud / metre boundary audit — Rbx (Lua-facing) API

CONTRACT: Lua-facing Rbx API is STUDS everywhere. Unity metres appear ONLY at the
binder boundary via `RbxSpace` (`DefaultMetersPerStud = 0.28`, 1:1 selectable).
Scale is session-constant (`RbxWorldHost.cs:98` calls `RbxSpace.Configure` once).

Method: enumerated every production `RbxSpace` caller (binder, camera rig, click-pick,
world-package session adapter, texture provider), then walked
`Runtime/RbxApi/Binding/`, `Runtime/Scripting/LuaCs/`, `Runtime/WorldBindings/`,
`Runtime/Infrastructure/LuaCs*` for any second crossing. No double conversion found:
each boundary converts exactly once, at the binder/rig.

## Table (worst first)

| Surface | Direction | Converted? | file:line | Consequence |
|---|---|---|---|---|
| `coreai_world_pos` (non-Rbx world-query tool) | Unity→Lua | **no** | `Runtime/WorldBindings/LuaCsWorldQueryBindings.cs:57-63` | Returns raw Unity **metres** `{x,y,z}` to Lua. No units documented (searched `Docs/`, zero hits). Mixed with studs `Part.Position` mis-scales by 1/0.28 ≈ 3.57×. Worst finding. |
| `coreai_world_raycast` (non-Rbx world-query tool) | Lua→Unity / Unity→Lua | **no** | `Runtime/WorldBindings/LuaCsWorldQueryBindings.cs:119-134` | Origin/direction/maxDistance consumed raw, hit `{x,y,z,distance}` returned raw — all metres, zero `RbxSpace` calls. Same 3.57× trap vs the Rbx API. |
| `unity_get/set_position`, `unity_get_transform`, `unity_set_scale` (Full-Unity surface) | both | **no** | `Runtime/Infrastructure/LuaCsFullUnityRuntimeBindings.cs:339`, `:350`, `:380` | Raw `transform.position`/`localScale` in metres, by surface design (non-Rbx reflection API). Hazard only if handles are mixed with Rbx instances. |
| `AdoptWorldObject` Size under a scaled ancestor | Unity→Lua | **no** (parent scale uncompensated) | `Runtime/RbxApi/Binding/InstanceGameObjectBinder.cs:186` (plus nesting TODO `:29-31`) | `SizeFromUnity(transform.localScale)` ignores ancestor world scale, so an adopted part nested under a Size-scaled part reads back a wrong `Size`. Position (world-space `:185`) is unaffected. |
| Unanchored-body reverse sync (Position/CFrame after host physics moves a body) | Unity→Lua | **no** (path missing, documented) | `Runtime/RbxApi/Binding/IPartPropertySink.cs:12`; Rigidbody created `:888-894` in `InstanceGameObjectBinder.cs` | Nothing writes Unity motion back to the sink, so Lua reads the last-set studs value while the body is elsewhere. No metres leak to Lua, but motion is invisible/stale. |
| Part CFrame / Position / Size (+Orientation/Rotation writes) | Lua→Unity | **yes** | `Runtime/RbxApi/Binding/InstanceGameObjectBinder.cs:528-533` (`ToUnityPose` + `SizeToUnity`); writes via `Runtime/Scripting/LuaCs/LuaCsRbxInstanceBindings.cs:1502-1540` | Sink stores pure Rbx space; single conversion at `ApplyTransform`. Correct. |
| Part CFrame / Position / Size / Orientation / Rotation reads | Unity→Lua | **yes** (sink echo, no re-read of Unity) | `Runtime/Scripting/LuaCs/LuaCsRbxInstanceBindings.cs:1436-1458` | Reads return stored Rbx values; Unity transform is never sampled, so no inverse leak. Round trip is identity. Angles are unitless — no scale applies. Correct. |
| `PivotTo` / `GetPivot` / `Model.WorldPivot` | both | **yes** (pure Rbx math, no crossing) | `Runtime/Scripting/LuaCs/LuaCsRbxInstanceBindings.cs:1304-1415` | AABB pivot math in studs (`:1342-1347`); reaches the binder only via `SetCFrame`. Correct. |
| Camera CFrame (`workspace.CurrentCamera`, `camera_set_cframe`, `camera_follow`) | both | **yes** | `Runtime/RbxApi/Binding/UnityCameraRig.cs:44-50`; globals `Runtime/Scripting/LuaCs/LuaCsRbxApiBindings.cs:1365-1404` | Single conversion each way through the rig. Follow `Offset` (`UnityCameraRig.cs:55,67`; `RbxCameraFollower.cs:30`) stays internal Unity metres, never exposed to Lua. Correct. |
| Click pick distance vs `MaxActivationDistance` | Unity→Lua / compare | **yes** | `Runtime/RbxApi/Binding/UnityClickPickSource.cs:68` (`LengthFromUnity`); gate `Runtime/Scripting/LuaCs/LuaCsRbxApiBindings.cs:1128` | Distance converted to studs before the studs-space comparison. Correct. |
| `Vector3.Magnitude`, `(a-b).Magnitude`, Dot/Cross/Lerp, CFrame ops | n/a (no crossing) | **yes** (studs in, studs out) | `Runtime/Scripting/LuaCs/LuaCsRbxDatatypeBindings.cs:314-315,324-339` | Pure `RbxVector3` math, never touches Unity. Correct. |
| World package capture/restore (parts + camera) | both | **yes** | `Runtime/Infrastructure/RbxWorldPackageSerializer.cs:287-288` (scale first), `:310-324` (parts via sink, camera via rig); stored studs `:548-549` | Scale travels with the payload (`MetersPerStud`), geometry stays studs end to end. `CameraSnapshot` (`RbxWorldPackageContracts.cs:780-804`) is internal Unity metres, never serialized. Correct. |
| RemoteEvent/RemoteFunction Vector3/CFrame payloads | both | **yes** (components preserved, no scale) | `Runtime/Scripting/LuaCs/LuaCsRbxNetworkCodec.cs:379-402` | Raw-component tagging both ways; studs preserved across the wire. Correct. |
| `report()` / `print()` | Unity→Lua: n/a | n/a (string-only) | `Runtime/LuaExecution/LuaCsModRuntime.cs:2490-2517` | String passthrough, no numeric path — nothing to convert. Correct. |
| Headless doubles (`InMemoryPartPropertySink`, `InMemoryCameraRig`, `InMemoryClickPickSource`) | both | **yes** (identity in Rbx space) | `Runtime/RbxApi/Binding/InMemoryPartPropertySink.cs:19-38`; `InMemoryCameraRig.cs:19-27`; `InMemoryClickPickSource.cs:14-19` | Store/return Rbx space directly; pick stub reports no hit. Correct. |
| Velocity / AssemblyLinearVelocity / AssemblyAngularVelocity / ApplyImpulse | n/a — no Lua path exists | n/a | `Runtime/RbxApi/Instances/ClassCatalog.cs:426-432` (Backlog, workaround string); `VelocityToUnity/FromUnity` (`RbxSpace.cs:147-155`) have zero production callers | No crossing, no leak — but movement-over-time via velocity is impossible (parity gap, not a boundary bug). |
| `workspace:Raycast`, `GetPartBoundsInBox`, `GetTouchingParts`, Overlap/Region3 | n/a — no Rbx Lua path exists | n/a | `Runtime/RbxApi/Instances/ClassCatalog.cs:402-405` (Raycast planned MVP8); searched `Runtime/**/*.cs` for `GetPartBoundsInBox|GetTouchingParts|OverlapParams|Region3|RaycastParams` — zero code paths, one workaround string | No crossing, no leak. The only Lua raycast is the metres-based `coreai_world_raycast` above. |
| Humanoid (`WalkSpeed`, `JumpPower`/`JumpHeight`, `HipHeight`) | n/a — no Humanoid class in `Runtime/` | n/a | Searched `Runtime/` for `Humanoid|WalkSpeed|JumpPower|JumpHeight|HipHeight` — hits only in `Tests/` corpus and one test comment | No crossing, no leak (parity gap). |
| Camera `FieldOfView`, Focus/focus-distance, zoom | n/a — no Rbx Lua surface | n/a | Searched `Runtime/**/*.cs` for `FieldOfView|Focus.*Distance|Zoom` — zero Rbx code paths | No crossing, no leak (parity gap). |

## Round trips that are NOT identity

1. `coreai_world_pos(name)` → `Part.Position` comparison/assignment: metres vs studs, off by ~3.57× at default scale.
2. `coreai_world_raycast` distance/point → studs world (e.g. vs `ClickDetector.MaxActivationDistance` or `(a-b).Magnitude`): metres vs studs.
3. `unity_get/set_position/scale` ↔ Rbx `Position`/`Size`/`CFrame` on the same object: metres vs studs cross-surface.
4. `AdoptWorldObject` of a part nested under a Size-scaled ancestor → `Size` read-back is wrong (localScale without parent compensation).
5. Unanchored part: set `CFrame`/`Position`, host physics moves the body, read back → stale last-set value (reverse sync is a documented MVP8 TODO, not a leak).
