# Work item 06: Rbx API repair

Baseline: `69d5bcc37502cd4bb464a3bf9ac0c53b1624b804`. Target: `7.54.0`.
Owner: module repair engineer; root owns Git, release, shared documentation, builds and tests.

## Contract and boundaries

Repair framework isolation, arithmetic, lifetime, mutation-envelope and bounded serialization contracts. Identity, authentication, matchmaking and game-session policy remain host responsibilities. Preserve public APIs, valid wire payloads, C# 9 compatibility and optional-module isolation. No additional agents, builds, tests, Unity operations, metadata or shared project-file changes are authorized for this worker.

The audit and current-source review establish the defects below. All eight dispositions are `confirmedfix`; implementation and regressions are present. The October 25 continuation completed source review and the Mods05 lifetime join. New regression execution remains root-owned and is not claimed here.

## October 25 continuation checkpoint

The goal is to finish the existing repairs, preserve optional assembly isolation and hand root a coherent source checkpoint. Correctness, bounded allocation, per-world ownership, callback failure containment, reentrancy, cancellation and deterministic cleanup apply. Host identity/session policy, shaders, other LuaCs adapters, shared project files and execution of build/test/Unity gates remain outside this lease.

| ID | Current source checkpoint | Remaining review or implementation |
| --- | --- | --- |
| RBX001 | Reviewed the converted displacement, normalization and metre range; eight real-physics endpoint cases exist. | Root real Unity physics gate. |
| RBX002 | Reviewed both promoted axes and finite/result bounds; Datatypes uses only RbxApiStubException. | Root core/Lua gates. |
| RBX003 | Reviewed replacement, binding removal, provider/world teardown and faulting/reentrant release cases. | Root real Unity material gate. |
| RBX004 | Reviewed provider-local mutable records, source retirement and separate-world handles/maps/tiling cases. | Root real Unity material gate. |
| RBX005 | Reviewed both silent subclass hooks and default/override ownership semantics; extended C# cases to every access scope. | Root core/Lua gates. |
| RBX006 | Reviewed once-per-Destroy custom cleanup, actor storage, queued invocation order and repeated destruction cases. | Root core/Lua gates. |
| RBX007 | Reviewed target guards before allocation/control, generated envelopes and preserved service ACL checks. | Root real executor/Lua gate. |
| RBX008 | Reviewed scalar/tag/table traversal, replaced-value graph guards, string prechecks and capped UTF-8 stream writes. | Root allocation/boundary/spy-bridge Lua gate. |

The Mods05 integration is authored in `LuaCsRbxApiBindings` through the existing `ILuaCsRuntimeLifetimeHost`. Its current factory's live owned coroutine supplies its token; other calls return an already-cancelled token. A locked registration list and reference-identity weak table release each participant once despite duplicate registration, callback errors, logger errors or reentry. Registrations after disposal are released immediately. Mods05 confirmed the factory-instance CurrentThread plus IsOwnCoroutineRunning guard is sufficient; no thread API extension was needed. Mods05 owns the interface, thread and HTTP adapter. Two teardown fixture cases cover participant ownership and real-Lua main/task/nested/pooled-handler token retirement; their execution remains root-owned. The earlier statement that no ApiBindings integration was required is superseded by this checkpoint.

| ID | Reproduce and guard review | Disposition | Regression acceptance | Proposed files |
| --- | --- | --- | --- | --- |
| RBX001 | The current adapter multiplied an unnormalized direction by its magnitude; Domain guards did not repair that calculation. | confirmedfix | Eight real-physics cases reject beyond-endpoint hits and accept sub-unit and diagonal rays at 0.28 and 1 metre per stud. The previous squared range reverses these outcomes. | `Binding/UnityRbxPhysicsPort.cs`; `Unity/RbxMaterialLifetimeRegressionTests.cs` |
| RBX002 | Signed offsets were subtracted before promotion. The fix uses double precision, explicit representability checks and the existing datatype exception convention. | confirmedfix | Both axes, reversed endpoints, signed extrema, alpha endpoints, midpoint, nearest-even rounding and invalid extrapolation; C# and Lua. Previous midpoint/extreme endpoint results fail these assertions. | `Datatypes/RbxUDim.cs`; `RbxContractRegressionTests.cs`; `LuaBindings/RbxApiLuaBindingsEditModeTests.cs` |
| RBX003 | The static variant cache had no production release path. Mutable records now have provider ownership, renderer leases, immediate final-release eviction and a 32-entry idle bound. | confirmedfix | Repeated replacement/destroy, pinned handles surviving eviction, world teardown, faulting/reentrant release and throwing map loader; no reset is used as the cleanup action under test. | `Unity/RbxTextureMaterialProvider.cs`; `Binding/InstanceGameObjectBinder.cs`; `Datatypes/IRbxMaterialProvider.cs`; `Unity/RbxMaterialLifetimeRegressionTests.cs` |
| RBX004 | Cache keys were global while sources were per provider. Mutable records are now per provider; changing the source retires old entries without mutating acquired handles. | confirmedfix | Two actual providers retain distinct handles/maps/tiling, including source edits and disposal. Old code aliases the handles and overwrites the first world's material. | Same provider/binder files as RBX003; `Unity/RbxMaterialLifetimeRegressionTests.cs` |
| RBX005 | Both subclass copy hooks were missing. Default C# Clone also omitted source actor/access metadata; the ownership-override overload must retain its existing caller-attribution behavior. | confirmedfix | C# and Lua hierarchy clones preserve all implemented writable properties, fresh identities, detached roots and independent signals. C# source ACL metadata remains intact. | `Instances/RbxClickDetector.cs`; `Instances/RbxMaterialVariant.cs`; `Instances/RbxInstance.cs`; `RbxContractRegressionTests.cs`; `LuaBindings/RbxMaterialVariantLuaBindingsEditModeTests.cs` |
| RBX006 | Server and actor signals were outside the base destruction path. The server signal now belongs to the base table and a once-per-Destroy hook releases all actor signals. | confirmedfix | Both classes, direct/ancestor/repeated Destroy, unused actor slots, pending call ordering, throwing/reentrant callback and bounded connection ledger; C# and Lua. Old code leaves Connected true. | `Instances/RbxInstance.cs`; `Instances/Networking/RbxRemotes.cs`; `RbxContractRegressionTests.cs`; `LuaBindings/RbxSignalConnectionTeardownEditModeTests.cs` |
| RBX007 | Create/Play/Pause/Cancel omitted target validation. They now enter ordinary generated envelopes and validate the explicit envelope against Tween.Target before side effects; existing WorldEdit and actor ACL guards remain. | confirmedfix | Real executor refuses A's envelope for creation/control of B without an extra tween or state change; B's envelope succeeds and replays once. Existing capability/cross-actor fixtures remain the ACL baseline. | `Scripting/LuaCs/LuaCsRbxInstanceBindings.cs`; `Acceptance/RungZeroEnvelopeEditModeTests.cs` |
| RBX008 | Complete JSON string and byte copies preceded quota checks. Outbound traversal now streams into a capped UTF-8 buffer, with string length guarded before escaping and graph guards retained on overwritten values. | confirmedfix | Allocation bounds on modest repeated-string and oversized-string inputs; exact escaping, Unicode, keys/tags/delimiter boundaries; preserved cycle diagnostics; spy bridge receives no rejected payload and remains usable. | `Scripting/LuaCs/LuaCsRbxNetworkCodec.cs`; `LuaBindings/RbxJsonContractEditModeTests.cs`; `Acceptance/RungZeroEnvelopeEditModeTests.cs` |

Runtime paths above are relative to `Assets/CoreAIMods/Runtime/RbxApi` except `Scripting/LuaCs`, which is relative to `Assets/CoreAIMods/Runtime`. Test paths are relative to `Assets/CoreAIMods/Tests/EditMode/RbxApi`. Module documentation is in `Runtime/RbxApi/Instances/README.md`. Shader/projection files, Luau/Crafting, scheduler and WorldPackage fixtures remain excluded.

## Progress

- Read repository instructions, architecture rules and audit 06. Created this work record before implementation.
- Rechecked the actual source and implemented the eight framework repairs without changing host account/session policy. The October 25 continuation adds the separately coordinated ApiBindings lifetime host required by Mods05, without editing its interface, thread or HTTP adapter.
- Added engine-free, real Lua-stack and real Unity regressions. Updated the existing variant-rendering fixture to dispose its provider instances now that mutable caches have instance lifetimes.
- Reviewed the runtime changes for failure cleanup and preserved the Datatypes assembly boundary: `RbxUDim` uses `RbxApiStubException`, never `Instances.RbxError`.
- Reviewed the codec's runtime overload path: its bounded buffer wraps Stream rather than inheriting MemoryStream, preventing newer runtime span writes from bypassing the byte-array guard. Stringified-key replacement still validates discarded graphs without escaping discarded string contents.
- Root reported central compile success after the datatype exception correction, plus selected Core 801 and Lua 69 passes. These are root-reported partial checks, not execution of the complete new regression set or the latest checkpoint. This worker ran no build/test/Unity operation.
- Read-only PowerShell source-integrity scan after the October 25 lifetime join: exit 0, `SOURCE_INTEGRITY files=22 missing=0 trailing_whitespace=0 cyrillic=0`. An attempted Python stdin scan returned exit 1 without output; PowerShell performed the file/text check directly. This is text inspection, not compilation or behavior verification.
- Source checkpoint handed to root after all edits. No Git mutations were made. Root's version changes and excluded owners' shader, guide and metadata work were preserved.

## API contract changes

- `LuaCsRbxApiBindings` implements existing `ILuaCsRuntimeLifetimeHost` with `RegisterRuntimeLifetime(IDisposable)` and `CurrentScriptLifetimeToken`. Participants are owned once by reference, released before other host cleanup callbacks and contained individually; late registration releases immediately. A token is live only inside the current owned non-dead scheduler coroutine, and pooled handler iterations never share a retired lifetime.
- Added `IRbxMaterialLeaseProvider<TMaterial> : IRbxMaterialProvider<TMaterial>` with `bool TryAcquireMaterial(in RbxMaterialId material, out TMaterial visualMaterial)` and `void ReleaseMaterial(TMaterial visualMaterial)`. Existing provider implementations remain supported.
- `RbxTextureMaterialProvider` implements that optional seam and IDisposable. Mutable handles are scoped to the provider; plain catalog sharing remains finite. Raw lookup handles are borrowed, 32 idle variants may be cached, acquired handles survive eviction/source replacement, final release destroys the mutable material, and Dispose invalidates all owned mutable handles. Release and Dispose are safe after repeated teardown.
- The binder acquires the replacement before releasing the previous handle; all binding-removal paths and BeginHostTeardown release leases. It disposes only a provider it constructed. Injected providers remain host-owned. Faulting cleanup is reported and does not stop other releases.
- Added protected `RbxInstance.DisconnectCustomSignals()` called once during Destroy; remote subclasses clear actor signals there. No existing public signatures were removed.
- Default C# Clone now preserves the source actor/access metadata; the ownership-override overload and Lua caller-attribution path remain unchanged.
- Explicit tween control envelopes name the tweened instance. This is admission of scheduled behavior, not authentication or session management. Scheduled interpolation continues after the initiating envelope scope exits.
- Wire tags, value types, non-finite number spelling, dictionary replacement semantics and the 65,536-byte limit remain compatible. Oversize diagnostics no longer claim an unmeasured complete byte count.

## Exact file list

Runtime files:

- `Assets/CoreAIMods/Runtime/RbxApi/Binding/InstanceGameObjectBinder.cs`
- `Assets/CoreAIMods/Runtime/RbxApi/Binding/UnityRbxPhysicsPort.cs`
- `Assets/CoreAIMods/Runtime/RbxApi/Datatypes/IRbxMaterialProvider.cs`
- `Assets/CoreAIMods/Runtime/RbxApi/Datatypes/RbxUDim.cs`
- `Assets/CoreAIMods/Runtime/RbxApi/Instances/Networking/RbxRemotes.cs`
- `Assets/CoreAIMods/Runtime/RbxApi/Instances/RbxClickDetector.cs`
- `Assets/CoreAIMods/Runtime/RbxApi/Instances/RbxInstance.cs`
- `Assets/CoreAIMods/Runtime/RbxApi/Instances/RbxMaterialVariant.cs`
- `Assets/CoreAIMods/Runtime/RbxApi/Unity/RbxTextureMaterialProvider.cs`
- `Assets/CoreAIMods/Runtime/Scripting/LuaCs/LuaCsRbxApiBindings.cs`
- `Assets/CoreAIMods/Runtime/Scripting/LuaCs/LuaCsRbxInstanceBindings.cs`
- `Assets/CoreAIMods/Runtime/Scripting/LuaCs/LuaCsRbxNetworkCodec.cs`

Test files:

- `Assets/CoreAIMods/Tests/EditMode/RbxApi/RbxContractRegressionTests.cs` (new; root's portable link is present)
- `Assets/CoreAIMods/Tests/EditMode/RbxApi/Unity/RbxMaterialLifetimeRegressionTests.cs` (new; real Unity only)
- `Assets/CoreAIMods/Tests/EditMode/RbxApi/Acceptance/RungZeroEnvelopeEditModeTests.cs`
- `Assets/CoreAIMods/Tests/EditMode/RbxApi/LuaBindings/RbxApiLuaBindingsEditModeTests.cs`
- `Assets/CoreAIMods/Tests/EditMode/RbxApi/LuaBindings/RbxJsonContractEditModeTests.cs`
- `Assets/CoreAIMods/Tests/EditMode/RbxApi/LuaBindings/RbxMaterialVariantLuaBindingsEditModeTests.cs`
- `Assets/CoreAIMods/Tests/EditMode/RbxApi/LuaBindings/RbxSignalConnectionTeardownEditModeTests.cs`
- `Assets/CoreAIMods/Tests/EditMode/RbxApi/Unity/RbxMaterialVariantRenderingEditModeTests.cs`

Documentation:

- `Assets/CoreAIMods/Runtime/RbxApi/Instances/README.md`
- `dev-docs/fix-wave-2026-10-24/workdoc06-rbx.md`

No worker edits to package versions, shared project files, metadata, shader/projection files, root trackers or aggregate documentation. The existing new-test metadata belongs to root's Unity import.

## Root verification handoff

Root's owned `tools/portable/Tests/CoreAI.Portable.Tests.csproj` already includes the new core fixture at line 34. No worker project-file edit was made. The explicit link is:

```xml
<Compile Include="../../../Assets/CoreAIMods/Tests/EditMode/RbxApi/RbxContractRegressionTests.cs" />
```

PowerShell, one command at a time; root owns the output paths:

```powershell
dotnet test tools/portable/Tests/CoreAI.Portable.Tests.csproj -c Release --artifacts-path artifacts/portable --filter "FullyQualifiedName~RbxContractRegressionTests|FullyQualifiedName~RbxDatatypeMiscEditModeTests|FullyQualifiedName~NetworkBridgeEditModeTests" --logger "trx;LogFileName=repair06-rbx-core.trx"
dotnet test tools/portable/LuaTests/CoreAI.Portable.LuaTests.csproj -c Release --artifacts-path artifacts/portable-lua --filter "FullyQualifiedName~RbxApiLuaBindingsEditModeTests|FullyQualifiedName~RbxMaterialVariantLuaBindingsEditModeTests|FullyQualifiedName~RbxSignalConnectionTeardownEditModeTests|FullyQualifiedName~RbxJsonContractEditModeTests|FullyQualifiedName~RungZeroEnvelopeEditModeTests|FullyQualifiedName~RungZeroEnvelopeProductionPathsEditModeTests|FullyQualifiedName~R6_5_CloneEditModeTests" --logger "trx;LogFileName=repair06-rbx-lua.trx"
```

For the SingleUnityOperator, EditMode filter:

```text
CoreAI.Tests.EditMode.RbxApi.Unity.RbxMaterialLifetimeRegressionTests;CoreAI.Tests.EditMode.RbxApi.Unity.RbxMaterialVariantRenderingEditModeTests;CoreAI.Tests.EditMode.RbxApi.Binding.InstanceGameObjectBinderEditModeTests
```

Expected: every selected new regression is discovered and passes; enumerate all skips/refusals and failures. Portable Unity stubs cannot establish material/physics correctness. The new real Unity fixture must not be linked into a portable runner and requires root-owned metadata/import. Run the existing MVP8 physics PlayMode fixture when root's Unity verification window permits.

## Remaining verification and concerns

- No worker-executed regression results, IL2CPP/WebGL/device validation, render capture or native profiler measurements. Root's reported partial checks do not replace the new core/Lua/Unity gates above.
- Unity normally defers native Destroy until the end of a Play Mode frame; provider dictionaries release references synchronously. Tests asserting immediate native null run in EditMode.
- Separate providers are required for independently configured worlds. Injected provider disposal remains with its supplying composition; the binder releases its own leases regardless.
- The ApiBindings side of Mods05's lifetime contract is now authored; HTTP cancellation/transport tests remain with the Mods05 owner and root. No questions remain for implementation; root owns integration, independent QA, release and push.
