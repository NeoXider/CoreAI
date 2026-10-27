# Mods runtime adversarial audit — 2026-10-27

Status: source review complete; root execution and release acceptance remain pending. Reviewer: native GPT-6.1 Sol, max reasoning; independent of implementation. Baseline: `demo`, `bc3fd74f0a889bfe1b1e7babc6bc02ed48a7c05e` (published 7.54.0). Review includes tracked changes and untracked source under `Assets/CoreAIMods/Runtime`.

Goal: inspect exact-source preconditions, native Lua invocation budgets/cancellation, active-world authority, capability enforcement and persistence, optional gameplay providers, callbacks/disposal, and eight new bundled Lua samples. Editor view presentation is assigned to another reviewer.

Scope: source read only; no source mutation, Git mutation, builds, tests, Unity, network, or model calls. Initial invocation budgets intentionally apply only to the initial main resume; later callbacks use host budgets. External source-store writer serialization is a host obligation rather than an asserted atomic edit contract.

## Findings

No additional confirmed Mods runtime P1/P2/P3 finding in this review. The editor reviewer owns the language/input defects described in [EDITOR_REVIEW.md](EDITOR_REVIEW.md), including the Lua length-operator autoclose P3 discovered during this source review. Those are actionable editor findings and must not be erased by the absence of an additional VM/store finding here.

## Review decisions and source evidence

### Explicit interpreter request and exact source expectation

- Read all of `LuaCodePlaygroundRuntime`, the portable request/result contracts and the actual `HubModServiceBase.SaveOrReload` transaction. Unsupported language/operation/service and missing exact expectation return before the save transaction. A precancelled request returns its own IDs with no dispatch. The adapter compares existence and exact ordinal source against `CodeSourceExpectation` immediately before invoking the existing save transaction (`LuaCodePlaygroundRuntime.cs:50-104`).
- `TryGetSource` intentionally prefers the live source, then stored-only source (`HubModServiceBase.cs:212-227`). This is an exact precondition, not an atomic compare-and-swap across unrelated external writers. The host owns serialization of those external writers; no concurrent external-write exploit is claimed from the documented gap.
- Result correlation includes document, language, mod, revision and run ID. Live execution and strict persistence acknowledgement remain separate. A real save receipt can report executed code with failed/unconfirmed persistence, and a local committed pair can still lack automatic persistence arming. The adapter does not turn a legacy source writer into a confirmed store.
- The request is synchronous on the caller thread; no worker or deferred execution is introduced. It rejects a second active run, targets Stop by run ID, cancels on Dispose, isolates status/message observer exceptions and restores Ready/Disposed in a finally block. Existing runtime/service ownership remains with the host.
- The initial VM guard is not a transaction rollback promise. A script can have effects before a later failure; the existing runtime intentionally retains objects from a failed first load, and a callback under trusted host APIs is not made preemptible merely by an instruction budget. This existing semantic boundary is not reclassified as an atomic-edit or zero-effects guarantee.

### Native initial-resume limits and active-world facade

- Read all changed invocation-limit, coroutine, scheduler and runtime hunks plus their real engine/scheduler joins. `LuaModInvocationLimits` rejects nonpositive steps and timeouts outside 1-1000 ms. The runtime computes the initial limit as the more restrictive explicit invocation and configured script budget (`LuaCsModRuntime.cs:585-634,2049-2052`).
- With no Rbx API, the concrete engine receives its guard and cancellation token. With Rbx, the callable carries one initial override/token; the scheduler consumes and clears them on that first resume, and `LuaCsCoroutineHandle` registers caller cancellation while the native resume is active. Typed timeout/step/memory causes survive the Lua error wrapper for adapter classification.
- Initial invocation limits intentionally do not replace later host handler/scheduler policy. A yielded main chunk later uses the ordinary host budget; Stop after the synchronous invocation has completed does not pretend to cancel the persistent mod. The tests cover this explicit distinction rather than silently extending an editor token to future frames.
- Independently read the final `ActiveLuaModRuntime` repair (`RbxWorldPackageContracts.cs:4377-4445,4460-4585`). It stores an immutable invocation policy in AsyncLocal, captures the actual concrete runtime for dispatch, and enters that runtime's invocation scope for load, both reload overloads, import and revert. The scope therefore follows dispatch after a world replacement instead of being attached only to the outgoing VM.
- Nested scopes enforce reverse disposal and restore the outer policy. Validation and precancellation occur before replacing policy. Import retains its existing false-on-failed-load contract; ownership snapshots are restored on false/exception. No source edit was made during root's Unity run.

### Strict capability projection and first installation

- Read the original PR-MODS-001/002/003 findings, current repair receipt and every production diff. The original strict writer/remover interfaces retain their members. The optional capability interface distinguishes a facade that implements those interfaces from its current backend actually supporting confirmed operations.
- `SessionSourceStore` and `ActiveLuaModSourceStore` consult the current backend capability; strict methods refuse unsupported backends before forwarding effects. Hub save/delete/active-state paths use legacy methods when appropriate and report unconfirmed persistence instead of fabricating a strict receipt.
- New canonical installation writes source and manifest in one unique, root-bounded staging directory, then publishes the pair with `Directory.Move` (`FileLuaModSourceStore.cs:165-231`). A failed first write cleans its owned staging; the canonical directory is not published half initialized. Existing corrupt/canonical evidence is preserved, and root-level fail-fast coordination remains in place.
- Existing-directory updates still use the established per-file atomic writes; this repair is not described as adding a general two-file atomic transaction to every update. Automatic persistence arming occurs after local completion, and strict exceptions retain `LocalCommitCompleted` separately.
- `LuaModStoreId.ApplyTo` uses strict UTF-8 encoding before filesystem work. Distinct malformed UTF-16 identifiers no longer alias through replacement bytes, while valid supplementary characters retain normal digest behavior.

### Optional gameplay providers, materials, downleveling and logs

- Read the full new `ICoreAiLuaGameplayBindingsProvider` and both composition diffs. Authored scene providers are explicit, validated and deduplicated; there is no scene-wide discovery or global provider. The composed callback captures its provider list and skips destroyed Unity components. Original installer overloads forward without changing grants.
- Both initial and replacement-world stacks receive the callback. The existing factory invokes it after built-in binding registration with effective capabilities, including the host ceiling; Full is not inferred from a mod's raw header request. Providers are trusted host code and must honor those capabilities and tolerate pre-Start rehydrate.
- Read material lease replacement/teardown and the current changed guard. `AddBinding` initializes the entry ID before the initial `Apply` (`InstanceGameObjectBinder.cs:604-612`). After an injected acquisition returns, the binder checks host retirement, exact current entry identity and renderer liveness before publishing the returning lease; an invalid acquisition is released immediately (`InstanceGameObjectBinder.cs:1778-1803`). Provider ownership remains borrowed. Arbitrary asynchronous provider behavior and native memory reclamation were not tested here.
- The downleveler rejects `_ENV` as a configured resolver name and uses a lazy floor getter bound to the lexical chunk environment, including all three floor-division emission sites. This preserves the repair's required lookup-before-operands order while leaving unused floor operations unevaluated. No new eager access was found.
- Lua log truncation now preserves a valid surrogate pair at its boundary. Constructor bounds leave room for the marker; per-mod/global retention remains bounded.

### Eight actual bundled sources

Read all eight new Lua files in full, including their headers and callbacks; these are real resource files rather than reconstructed snippets. Every header is opt-in (`active: false`).

| Resource | Bounded behavior inspected |
| --- | --- |
| `sample_maze_solver.lua` | Fixed 16x10 board; at most 24 BFS cell expansions per frame; predecessor reconstruction at most 160 cells; the switch reuses the board and resets finite search tables |
| `sample_orbit_gallery.lua` | Twelve persistent parts, bounded Heartbeat iteration, pause retains its elapsed clock |
| `sample_recycling_conveyor.lua` | Twelve reusable crates, four absolute-time position updates per frame |
| `sample_respawning_coins.lua` | Eight reusable parts, duplicate click refusal while cooling down, bounded respawn loop |
| `sample_round_clock.lua` | Three finite states, at most one phase transition per callback, observable attributes |
| `sample_saved_score.lua` | Mod-scoped state store separated from source persistence; one retained root and bounded display size |
| `sample_tween_gate.lua` | Cancels its previous tween before replacement; existing TweenService caps completed/cancelled retained tweens per actor and destroys a mod's tweens on unload |
| `sample_waypoint_platform.lua` | Four explicit waypoints; one bounded movement/arrival operation per frame; no navigation system dependency |

The examples intentionally clamp large deltas and are not authoritative wall clocks. Static bounded loops do not establish measured frame time or long-running device memory behavior. The cancelled NPC navigation system and ignored staging artifacts are outside the intended shipment and were not treated as implemented features.

## Source identity and scope inventory

The read-only changed/untracked inventory identifies 34 `.cs`, `.lua` and `.asmdef` paths. Manifest construction matches [CORE.md](CORE.md): sorted `path|lowercase-SHA256` lines, LF separators/no final LF, UTF-8 without BOM. Manifest SHA-256: `a3bc971041c34047ab28bc5d8f7b31c295148d1939afb876320d783b2efcf798`.

| Area | Changed/new files |
| --- | --- |
| Composition | `CoreAiModsInstaller.cs`, `CoreAiModsLifetimeScope.cs`, `ICoreAiLuaGameplayBindingsProvider.cs` |
| HubIntegration | `CoreAI.Mods.Hub.asmdef`, `HubModEditorPage.cs`, `HubModServiceBase.cs`, `LuaCsModRuntimeHubService.cs` |
| HubIntegration/CodeEditing | `CodeEditorSurface.cs`, `CodeEditorView.cs`, `CodeLanguageProfiles.cs`, `CompletionPlacement.cs`, `LuaCodePlaygroundRuntime.cs` |
| Infrastructure | `FileLuaModSourceStore.cs`, `LuaModStoreId.cs`, `RbxWorldPackageContracts.cs` |
| LuaExecution | `ILuaModRuntime.cs`, `ILuaModSourceStore.cs`, `LuaCsModRuntime.cs` |
| LuauDownlevel | `DownlevelResult.cs`, `LuauRewriteParser.cs` |
| Logging / RbxApi/Binding | `LuaLogService.cs`, `InstanceGameObjectBinder.cs` |
| Scripting/LuaCs | `LuaCsCoroutineHandle.cs`, `LuaCsRbxApiBindings.cs`, `LuaCsRbxSchedulerAdapter.cs`, `LuaCsScriptCoroutine.cs` |
| Resources/CoreAIMods | All eight `sample_*.lua` files named above |

All changed runtime hunks and all new non-view source were read, with contextual joins. `CodeEditorView` presentation/input and complete Hub page lifecycle are independently assigned to the editor/host reviewer; this report reads their relevant interpreter/language integration paths and does not claim a second full 2,531-line view audit. Their inclusion in the hash inventory pins the same source state across reports.

Representative full file hashes:

- `LuaCodePlaygroundRuntime.cs`: `1d4a232cf0b230a5584184b617ef908ece7fedad59bd58a3b7c72b1e01265b88`.
- `LuaCsModRuntime.cs`: `1edf1421e20c2b0fcc1d42b3c28b41d895470959bbfc0a165205af2bdfb538a6`.
- `RbxWorldPackageContracts.cs`: `ee69a15c2aaed5768da37ec31f623a96a99cac23cc0e5cbc4fd40bf16d1e99bf`.
- `FileLuaModSourceStore.cs`: `22d9b9fad9756a750a6f6f19273cab8c45a7cf9bcaad8e726119b6c28424b3f5`.
- `CodeLanguageProfiles.cs`: `eefe12840c0ea3b99a1b5174f76a7c32fbbd75d617e1e858ce679dd8a608e235`.

## Evidence and remaining gates

Read `LuaModInvocationLimitsEditModeTests`, `ActiveWorldInvocationBudgetEditModeTests`, `CodeEditorRuntimeEditModeTests`, sample regression coverage and prior persistence/material/downlevel repair reports. Test declarations cover real VM entry, source receipts, native cancellation, initial-vs-later policy, replacement-world dispatch, zero-effect unsupported/precondition cases, active run cancellation/disposal, observer isolation, store retry and sample lifecycle. These declarations are not passed test results.

No reviewer build, test, Unity session, model, network, device or profiler was run. Root's portable Core receipts, independently counted in [CORE.md](CORE.md), do not prove native Mods execution. At dispatch, root's latest focused native compilation required three test namespace corrections, and its successful rerun was still pending; this review does not replace that gate.

NOT DONE: execute the source-final focused/native and full Unity suites; verify the separate editor findings and their regressions; perform actual OpenRouter API PlayMode acceptance; interactive input, built-player, IL2CPP/WebGL, device/rendering/physics, real network/load and performance gates. A raw HTTP tool-call probe is not Unity acceptance.

CHANGED: `CORE.md`, `MODS.md`, `PR_FINDINGS_CORE_MODS.md` only. VERIFY OUTPUT: actual source/diff/context reads, complete eight-source read, hash inventory and existing Core TRX accounting. CONCERNS: editor findings remain actionable under their owning review; native/platform acceptance remains separate. QUESTIONS: none.
