# MOD runtime repair checkpoint

Baseline: `69d5bcc37502cd4bb464a3bf9ac0c53b1624b804`. Target release: `7.54.0`.
Requested model: `gpt-6.1-sol`, reasoning effort `max`; this worker cannot change or independently verify its model configuration.

## Ownership and verification

Work is limited to the assigned MOD runtime, non-Rbx Lua adapters, the two explicitly leased overlay stores, related non-Rbx tests and package-specific guides. No agents, Git mutations, builds, test execution, Unity/MCP, shared release files or asset metadata. Root owns integrated verification and release. Framework contracts apply to supplied identity, cancellation, isolation, bounds and persistence; authentication and game-session policy remain host responsibilities.

Existing package adapters and tests are the reuse baseline. Framework packages deliberately do not depend on NeoxiderTools. VM imports remain confined to Scripting. Root/other workers own excluded Rbx, Luau compiler and Hub files.

## Finding review and acceptance

| ID | Reproduce / existing guard review | Disposition | Regression acceptance | Proposed files |
|---|---|---|---|---|
| MOD001 | Read registers version mutations; host capability intersection does not make that read-only. | confirmedfix | Read cannot mutate histories/overlays or emit commands; explicit writer retains supported operations. | `Infrastructure/LuaCsGameplayBindings.cs`, `LuaCsVersioningRuntimeBindings.cs`, factory options, `ModsContractRegressionTests.cs`, access guide |
| MOD002 | Overlay memory advances before persistence; atomic file replacement protects old disk data only; arming refusal is ignored. | confirmedfix | Failed write/refused arming is observable, emits no apply, and identical retry persists. Local commit and persistence arming remain distinct. | leased `FileDataOverlayVersionStore.cs`, version bindings, overlay store tests |
| MOD003 | No-Rbx startup uses finite one-shot defaults; handler options only promise Rbx main-resume configuration. | documentedlimit (P2 configuration limit) | Document defaults honestly; no security-bypass claim. Optional startup seam only if justified. | package Lua guide |
| MOD004 | Replacement/shutdown leaves captured legacy tick objects live; removal guards otherwise exist. | confirmedfix | Reentrant reload/shutdown stops old timer/event batches and permits replacement next frame. | `LuaExecution/LuaCsModRuntime.cs`, runtime tests |
| MOD005 | Per-instance semaphore does not serialize shared directories, cleanup or quotas. | confirmedfix | Same canonical-root stores serialize admission/install/rollback, including waiters; no unbounded path registry or racy eviction. | `Infrastructure/FileRbxWorldPackageStore.cs`, new coordinator if needed, regression tests |
| MOD006 | Default networking refuses; host opt-in pending resolver/transport receives no lifetime token or bound. | confirmedfix | Deferred fake requests cancel on generation/world retirement, no late transport dispatch, bounded outstanding work, detach on dispose. | `Scripting/LuaCs/LuaCsRbxHttpServiceAdapter.cs`, scheduler adapter, factory; binding disposal hook requires Rbx-owner contract |
| MOD007 | Ring counts bound entries per key, not keys/messages; clear retains buffers. | confirmedfix | Configured key/message/retained-text bounds, deterministic eviction, clear releases buffers, sequence ordering preserved. | `Logging/LuaLogService.cs`, logging tests/README |
| MOD008 | Sanitized valid/invalid identities collide; exact replacement already has safe hashed mapping. | confirmedfix | Ordinary source/KV/startup namespaces isolate collision pairs and case; legacy reads verify identity and never overwrite ambiguous data. | `Infrastructure/FileLuaModSourceStore.cs`, `LuaModStoreId.cs`, `FileLuaModStore.cs`, source/store tests |
| MOD009 | Standalone tool omits cancellation checks when backup gate is absent. | confirmedfix | Pre-cancelled mutation actions have no runtime/store/sink effects, with/without optional gate; cancellation propagates. | `LuaExecution/LuaModsLlmTool.cs`, tool tests |
| MOD010 | Empty payload is accepted but used as baseline-existence sentinel. | confirmedfix | Empty original survives resets, non-overwriting seed and reopen. | leased `MemoryDataOverlayVersionStore.cs`, overlay store tests |
| MOD011 | Continuously due timers consume fixed frame budget before events/later mods. | confirmedfix | Deterministic bounded timer/event/mod service under saturation, total frame work remains capped. | `LuaExecution/LuaCsModRuntime.cs`, runtime tests |
| LUAU007 | Compiler requires reserved trusted iterator resolver captured at chunk entry; ordinary protected metatables must remain hidden. | integration | Actual table/userdata `__iter` is invoked once under finite/cancellable guard; triples/multiargs unchanged, raw tables and callable values preserve behavior. | `LuaExecution/LuauSourceGate.cs`, factory, `Scripting/LuaCs/LuaCsScriptEngine.cs`, secure environment, regression tests |

## Contracts / dependencies

- Overlay failure contract implemented without interface changes: `CoreAI.Infrastructure.Lua.DataOverlayPersistenceException : IOException`, with `bool LocalCommitCompleted`. Before-replacement failure keeps live memory and old disk unchanged. Refused persistence arming keeps live memory unchanged and reports local replacement explicitly; identical retries run persistence again. Lua apply publishes only after the void store call returns successfully. Hub callers must allow/catch this exception as failure, never claim durable success. No durability callback or browser wait is added.
- Rbx-owner HTTP hook required: implement `CoreAI.Ai.LuaCs.ILuaCsRuntimeLifetimeHost` on `LuaCsRbxApiBindings`. Exact members: `void RegisterRuntimeLifetime(IDisposable participant)` (dispose each participant once when bindings dispose, immediately if already disposed), and `CancellationToken CurrentScriptLifetimeToken { get; }` (current scheduler `LuaCsRbxScriptThread.LifetimeToken`; return an already-cancelled token outside a live owned scheduler thread). This worker adds the interface and thread token; the adapter refuses outbound requests if the interface is absent. The Rbx owner must add those members/interface and disposal storage in its exclusively owned file. Existing thread teardown kills the matching outgoing generation, and killing/completing that thread cancels its lazy lifetime token. No auth/session policy added.
- Scoped versioning contract implemented: `ILuaVersioningWritePolicy.CanWrite(string ownerModId, ActorContext? actorContext, string key, bool luaHistory)` supplied through `LuaCsModStackOptions.VersioningWritePolicy`. Read exposes inspection only. Scoped mutation requires WorldEdit plus this policy; globals and baseline overwrite are excluded from scoped registrations. `RegisterGameplayApis` remains the explicit trusted host registration surface.
- Compiler contract supplied by root: immutable `LuauDownlevelOptions` and `Process(source, chunkName, options)`, trusted name `__coreai_luau_iter`. No compiler edits by this worker.
- Tests are authored here; root runs serial portable Lua and Unity EditMode filters. Source inspection is not a passing test result.

## Progress

- 2026-10-25 resumption: the previous implementation is partial and has no final acceptance. This worker owns the original module05 lease only; root owns all executable checks and shared project links. The current source checkpoint is reviewed below before further edits.

| ID | Current source status at resumption | Files under review |
|---|---|---|
| MOD001 | Scoped write policy and read-only registration authored; API/behavior review pending. | gameplay/versioning bindings, factory, `ModsContractRegressionTests.cs` |
| MOD002 | Typed persistence failure and delayed publication authored; retry/arming regression review pending. | leased file overlay store, versioning bindings, overlay tests |
| MOD003 | Finite default guard retained; configuration limit remains documented. | Lua execution README, factory/engine guard wiring |
| MOD004 | Generation fence authored; reload/shutdown event/timer tests under review. | `LuaCsModRuntime.cs`, runtime regression fixture |
| MOD005 | Refcounted canonical-root transaction lease authored; waiter cleanup/rollback review pending. | `FileRbxWorldPackageStore.cs`, regression fixture |
| MOD006 | Lifetime seam, adapter cancellation and request bounds partially authored; Rbx implementation is a separate owner's integration. | HTTP adapter, scheduler adapter, lifetime interface, factory |
| MOD007 | Log bounds and retained-buffer release authored; deterministic ordering review pending. | `LuaLogService.cs`, logging README, regression fixture |
| MOD008 | Injective disk keys and explicit legacy migration authored; namespace regression review pending. | source/KV stores and identity helper, regression fixture |
| MOD009 | Pre-cancellation checks authored; mutation side-effect tests under review. | `LuaModsLlmTool.cs`, tool regression fixture |
| MOD010 | Empty baseline marker authored; reset/reopen tests under review. | leased memory overlay store, overlay tests |
| MOD011 | Timer/event/mod fairness authored; bounded service tests under review. | `LuaCsModRuntime.cs`, regression fixture |
| LUAU007 | Trusted iterator integration partially authored; every VM ingestion path and protected-metatable semantics require source review. | source gate, factory, script engine, secure environment, regression fixture |

- Read repository rules, architecture rules, assigned audit and applicable Unity skill. Confirmed named lease and preexisting unrelated owner changes.
- Initial dispositions above reflect audit call chains and root adjudication; implementation source review and exact file list follow incrementally.
- Landed first edits for MOD001/002/004/007/008/009/010/011. Shared-root refcounted world-store transaction leases cover operation holders and waiters; constructor cleanup skips an in-flight root. No gate survives its last operation. New namespace hashing never claims an ambiguous legacy directory; host-verified migration is explicit.
- All executable verification remains pending root; no builds or tests have run in this worker.

## Source-final handoff, 2026-10-25

Real goal: close confirmed framework isolation, persistence, lifetime and bounded-dispatch defects while preserving replaceable blocks and host-owned authentication/session policy. Source review reused the existing guard, scheduler, storage and binding seams. No new dependency, runtime authentication policy, shared project link, metadata or excluded-owner file was added here.

| ID | Final source disposition | Regression / remaining gate |
|---|---|---|
| MOD001 | confirmedfix; Read registers inspection only; scoped writes require WorldEdit plus per-key policy; trusted host API retained. | Read/writer/key/history denial, missing policy, explicit trusted-host compatibility tests authored. |
| MOD002 | confirmedfix; overlays stage state and throw typed failures; strict optional source writer exposes local-install/arming failure while legacy source/KV paths retain best-effort logging. | Write failure, refused arming, no apply publication, exact retry, strict source partial install/rearm and source/KV logging tests authored. Hub consumption of strict receipts is root-owned. |
| MOD003 | documentedlimit, P2; finite no-Rbx defaults retained and verified in source. | 50,000,000 steps, 10,000 ms, 256 MiB live-heap growth; no independent factory startup-budget option claimed. |
| MOD004 | confirmedfix; swap and shutdown retire captured outgoing objects before further dispatch. | Reentrant timer/event reload and shutdown regressions authored. |
| MOD005 | confirmedfix; canonical-root/filesystem transaction entries retain holders and waiters, releasing after last use. | Controlled two-store quota, canceled waiter, constructor cleanup and autosave rollback/reopen regressions authored. |
| MOD006 | confirmedfix; thread lifetime cancellation reaches DNS/transport; late work is detached/observed; pending requests/completions are bounded. | Unload/reload/world-dispose DNS, parked transport and cross-window bounds tests authored. Rbx owner reports lifetime-host join and its teardown regressions source-final. |
| MOD007 | confirmedfix; retained ids/text bounded and cleared buffers released; exact ids are preserved and counted in the character budget. | Eviction, concurrent ordering, caller mutation, long-id collision and oversized-id regressions authored. |
| MOD008 | confirmedfix; ordinary source/KV/startup namespace mappings are injective for the supported sanitized/case collision cases; legacy migration verifies identity and never merges. | Source/KV/startup reopen, deactivate/delete isolation and legacy mismatch/migration regressions authored. |
| MOD009 | confirmedfix; cancellation checked before identity resolution and again inside dispatch/gate entry; cancellation propagates. | All six mutations with/without backup gate, queued cancellation with substituted token, zero identity/runtime events and preserved history regressions authored. |
| MOD010 | confirmedfix; baseline existence is independent of empty payload contents. | Empty seed/reset/reset-all and file-reopen regression authored. |
| MOD011 | confirmedfix; configured event reservation plus deterministic mod rotation preserves bounded timer and full-event-batch service under continuous load. | Saturation tests cover both reservation bounds, deterministic order, every later mod, full 64-handler batches and global 256-invocation ceiling. |
| LUAU007 | integrated; backend-neutral optional resolver advertisement reaches every source gate; secured synchronous/asynchronous and scheduler chunk paths restore the reserved resolver before user code. | Protected table/userdata, iterator triples, raw tables, capture against reassignment, finite step/cancellation, error/yield fence and persistent/one-off/envelope production-path tests authored. |

### Optional contracts supplied

- `CoreAI.Ai.ILuaModSourceStrictWriter.SaveStrict(string id, string source, LuaModManifest manifest)` confirms local source+manifest installation and automatic persistence arming on return, never browser durability completion. `FileLuaModSourceStore` implements it. `CoreAI.Infrastructure.Lua.LuaModSourcePersistenceException.LocalCommitCompleted` distinguishes an incomplete pair of local writes from a completed local install followed by arm refusal. Existing `ILuaModSourceStore.Save` remains best effort. Root approved this contract and owns Hub integration.
- `CoreAI.Ai.LuaCs.ILuaVersioningWritePolicy.CanWrite(string ownerModId, ActorContext? actorContext, string key, bool luaHistory)` is optional and configured through `LuaCsModStackOptions.VersioningWritePolicy`; no default grant exists.
- `CoreAI.Ai.LuaCs.ILuaCsRuntimeLifetimeHost` remains exactly `RegisterRuntimeLifetime(IDisposable)` and `CancellationToken CurrentScriptLifetimeToken { get; }`. The Rbx owner implemented the host side separately. The token is canceled outside a live factory-owned executing coroutine and on owning-thread completion/kill.
- `CoreAI.Scripting.IScriptGeneralizedIteratorSupport.TrustedIteratorResolverName` is optional; `IScriptEngine` implementers gain no required member. Lua-CSharp advertises the reserved `__coreai_luau_iter`; actual metatable access stays private to the resolver.

### Source checks and execution handoff

- Reviewed the assigned audit, original prompt, all in-scope runtime diffs, changed regression paths and the Rbx lifetime join contract. Source `git diff --check` returned 0; only preexisting CRLF normalization notices were printed. Added runtime lines and the new fixture contain no implicit `var` declaration or Cyrillic prose. These are source checks, not executable acceptance.
- `ModsContractRegressionTests.cs` and both file-store fixtures are already linked by the root-owned portable Lua project at handoff. No further new fixture link is requested. `LuaModsLlmToolEditModeTests` and `DataOverlayVersionStoreEditModeTests` need Unity execution because their existing host dependencies are outside this portable fixture set.
- Root portable command: `dotnet test tools/portable/LuaTests/CoreAI.Portable.LuaTests.csproj -c Release --filter "FullyQualifiedName~ModsContractRegressionTests|FullyQualifiedName~FileLuaModSourceStoreEditModeTests|FullyQualifiedName~FileLuaModStoreEditModeTests|FullyQualifiedName~LuaCsModRuntimeEditModeTests|FullyQualifiedName~LuaLogServiceEditModeTests|FullyQualifiedName~ScriptingSeamHonestyEditModeTests|FullyQualifiedName~LoggingSeamHonestyEditModeTests"`; expect zero failures, finite controlled timeouts, no external DNS/HTTP.
- Root Unity filters: `CoreAI.Tests.EditMode.ModsContractRegressionTests`, `CoreAI.Tests.EditMode.FileLuaModSourceStoreEditModeTests`, `CoreAI.Tests.EditMode.FileLuaModStoreEditModeTests`, `CoreAI.Tests.EditMode.LuaModsLlmToolEditModeTests`, `CoreAI.Tests.EditMode.DataOverlayVersionStoreEditModeTests`, plus the existing runtime/logging/seam and Rbx lifetime fixtures. Root owns serial scheduling and live Editor identity.
- NOT RUN here: builds, portable/Unity tests, player/WebGL/browser/device or performance checks. Earlier Core801/Lua69 results predate this checkpoint. No live provider, actual DNS/HTTP, installation, service, Unity/MCP or Git mutation was used.
- Source is final for root review; no background worker or active wait remains in this module. Authentication/account/matchmaking/session choices remain host-owned. Ordinary best-effort APIs still cannot supply durable-save receipts; strict callers must consume the optional contract or report an unconfirmed result.

### Final source SHA-256 manifest

The following paths include inherited module05 implementation completed in this continuation. Excluded-owner files and shared project links are intentionally absent.

| Path | SHA-256 |
|---|---|
| `Assets/CoreAI/Runtime/Core/Features/RuntimeVersioning/MemoryDataOverlayVersionStore.cs` | `82dc0d0b4720e1afd7f7d9265584037e3a4bf9afed4af6facd3c42db60b84c1f` |
| `Assets/CoreAiUnity/Runtime/Source/Features/Lua/Infrastructure/FileDataOverlayVersionStore.cs` | `5737474c7ec72541f1a61f507900b21066e7cbc4c7c99e4d8ed0933d0abb2142` |
| `Assets/CoreAIMods/Runtime/Infrastructure/FileLuaModSourceStore.cs` | `9c3c4b2aa5785ac291076f4fca47e8d43a3f1695cc8fa98ff4cb6d292c9108a4` |
| `Assets/CoreAIMods/Runtime/Infrastructure/FileLuaModStore.cs` | `15e28948df277ffd14fec20d30d4fe825963b3594e91c8553a1ada8c8e1c5341` |
| `Assets/CoreAIMods/Runtime/Infrastructure/FileRbxWorldPackageStore.cs` | `3450a5479b07f9b7d07de5c55e2e51316e44200d659d917b11fc25302c6ddd6f` |
| `Assets/CoreAIMods/Runtime/Infrastructure/LuaCsGameToolExecutor.cs` | `a6f4f250f03d52c2fdaad43c6e1d414e921e323a83df4130b512a57ade2158aa` |
| `Assets/CoreAIMods/Runtime/Infrastructure/LuaCsGameplayBindings.cs` | `9fe768e5e2bd109e874dcb11a5f76b3a3a991b6a40aadec17c67885847efe077` |
| `Assets/CoreAIMods/Runtime/Infrastructure/LuaCsModRuntimeFactory.cs` | `68489fd8002588a761fe18b3e0eb550015be2f045b26bbb027920bc3bda7ecee` |
| `Assets/CoreAIMods/Runtime/Infrastructure/LuaCsVersioningRuntimeBindings.cs` | `9aefcb763557470f56c3b451840ed486a42401be97929595b9f29efa72809f1c` |
| `Assets/CoreAIMods/Runtime/Infrastructure/LuaModStoreId.cs` | `fa7c88880b906097d773bb4bfba0478a2f7bf89281241b796906d14c9c59ebb4` |
| `Assets/CoreAIMods/Runtime/Logging/LuaLogService.cs` | `94b68289a15416f7664129aa0779fd01c4f2084460fdc62a33d39fa86c5ac70e` |
| `Assets/CoreAIMods/Runtime/Logging/README.md` | `63c12c42bfb32f8c86067ceb5dd8c89f1fa1af17d86c979cddc2076345c2b191` |
| `Assets/CoreAIMods/Runtime/LuaExecution/ILuaModSourceStore.cs` | `f3cf22266893cb707514d53057b58385f086196d90ec99dd5991ac4eae91b940` |
| `Assets/CoreAIMods/Runtime/LuaExecution/LuaCsAiEnvelopeProcessor.cs` | `f0f79c8afb820bb1d1bb29573cf7b48261581af921ff95d9068818ba98716db1` |
| `Assets/CoreAIMods/Runtime/LuaExecution/LuaCsModRuntime.cs` | `40cc239b7f40270776e4479ed4a357e33a847d1d85c359e8f1f72b66a8e43ca4` |
| `Assets/CoreAIMods/Runtime/LuaExecution/LuaModsLlmTool.cs` | `6ddcb2723b10800756ee5a9820d5f548ac604cdd0f71d4ca07f83a51f858f53a` |
| `Assets/CoreAIMods/Runtime/LuaExecution/LuauSourceGate.cs` | `0cbc445868e77b1fc19a422c8f36bccce9dd1519f46fc12a68b44ce3c735596f` |
| `Assets/CoreAIMods/Runtime/LuaExecution/README.md` | `a9836e7a3b376f2c66b0cb546b4ddbbeebc46c9daaadc5fc4c942266b85d1d22` |
| `Assets/CoreAIMods/Runtime/Scripting/IScriptEngine.cs` | `e49d172c237a28cde6f4bc8e501f115e45d84e2f4882db2706ebfc8dc9511e5f` |
| `Assets/CoreAIMods/Runtime/Scripting/LuaCs/LuaCsRbxHttpServiceAdapter.cs` | `20ed9b4c44dfecf7ce5a1b648b773f192559df5fb47539f907dbb7af7b23cec6` |
| `Assets/CoreAIMods/Runtime/Scripting/LuaCs/LuaCsRbxSchedulerAdapter.cs` | `c0d5664a30877bedfc1e9f905510c644d86db935919afe64638ac2647f2bb4b3` |
| `Assets/CoreAIMods/Runtime/Scripting/LuaCs/LuaCsScriptEngine.cs` | `20e7be08b7884311a1b9b4e37a51e2e4a22b80f293b5b52a390ccf17e923394c` |
| `Assets/CoreAIMods/Runtime/Scripting/LuaCs/LuaCsSecureEnvironment.cs` | `063bb45428cbb088772f0da2151ebc02cd50ab27dffb63e22291fc036d25ffb7` |
| `Assets/CoreAIMods/Tests/EditMode/ModsContractRegressionTests.cs` | `3343b88cbf33b00652e984dce060ce12f0123616fd7e5d549c1b8ed35ce73ec4` |
| `Assets/CoreAIMods/Tests/EditMode/FileLuaModSourceStoreEditModeTests.cs` | `6147825baf6207634003ab2f6f35b24abbcd5a2df9a9ca7b22a37a05ee13b2ca` |
| `Assets/CoreAIMods/Tests/EditMode/FileLuaModStoreEditModeTests.cs` | `28d12630aabc6b9714f806727309ff238498666aca60985b6247ff2a0e6ea349` |
| `Assets/CoreAIMods/Tests/EditMode/LuaModsLlmToolEditModeTests.cs` | `fbc270b03be5108a86206aaf1bc8c7955eacfd30be667656ee1205c29cb99794` |
| `Assets/CoreAiUnity/Tests/EditMode/DataOverlayVersionStoreEditModeTests.cs` | `889a6d5b1885db633f60d17c153af1f2c95a4a1f6f0656052920bd96d5e1936f` |
| `Docs/CoreAIMods/mod-system.md` | `332604c3761826b3773039571c2706b33d487aa1b9ac93bc862f2aee7c16c6f2` |

## Backup-cancellation fixture reconciliation, 2026-10-25

- Root supplied the actual full Lua receipt `artifacts/fix-wave-20261025/lua-full-results/lua-full.trx`: 1,925 Passed, 3 Failed, 41 NotExecuted across 1,969 result rows. Two failures are legacy backup fixtures expecting failure JSON from an injected `OperationCanceledException`; MOD009 deliberately propagates cancellation. The third failure belongs to another owner's corrupt-folder admission fixture.
- Added lease is limited to `Assets/CoreAIMods/Tests/EditMode/RbxApi/Acceptance/Mvp3WorldPackageEditModeTests.cs` and this addendum. Production source is frozen. Goal: assert propagated cancellation explicitly while retaining every action's runtime/source/revision snapshot, deterministic autosave trigger and manual-slot invariants; ordinary refusal and I/O modes must still assert failure JSON.
- Source inspection confirmed both failures exit before their no-mutation checks because they unconditionally await/parse JSON. The shared helper already covers all six mutations and all three failure modes; the repair changes only result-shape expectations for the cancellation branch. No executable checks are run by this worker.
- SOURCE-FINAL: both cancellation paths now await `AssertManageModsBackupCancellationAsync`, which requires the injected `OperationCanceledException` and fails if any JSON result is returned. False-result and I/O-exception modes still parse JSON, require `success == false`, and assert the matching injected failure message. The action/failure matrix remains all 18 combinations.
- Every matrix case still executes the complete before/after runtime/source/revision snapshot, exact deterministic autosave trigger, zero manual writes and unchanged manual-slot list checks after validating its failure channel. The standalone reload-cancellation regression now also uses the existing memory source store and full state/manual-slot snapshots while retaining its explicit live-source and revision-count assertions.
- Static verification: reviewed the actual fixture diff; `git diff --check` returned 0 with no output. Fixture SHA-256: `9fbe00e9a9067b3e39b31edf0d17eff88102849e5a38ab5f192371b4b7009954`. Production source and all other files remain unchanged in this follow-up.
- Root rerun filter: `FullyQualifiedName~ConfirmedBackup_ManageModsReloadCancellation_PreservesSourceAndRevisionLedger|FullyQualifiedName~ConfirmedBackup_AllManageModsMutations_FailClosedForEveryBackupFailure` in `tools/portable/LuaTests/CoreAI.Portable.LuaTests.csproj`; expect both tests to pass, including every mutation/failure combination. The full-suite receipt above is pre-reconciliation evidence, not acceptance of this edit. No build, test, Unity/MCP or Git mutation was executed here; no active wait remains.
