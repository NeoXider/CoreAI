# Group 06 independent PR review

- Baseline: `69d5bcc37502cd4bb464a3bf9ac0c53b1624b804`.
- Candidate: `bc3fd74f0a889bfe1b1e7babc6bc02ed48a7c05e` (pinned source and line references).
- Scope: all 33 group 06 primary paths; adjacent contracts/callers are contextual reads only.
- Constraints: only this report is written; static review only, with no builds, tests, Unity, provider/network calls, agents, or Git mutations.
- Goal and invariants: identify actionable introduced defects in strict execution, storage/migration, publication ordering/retry, cancellation/lifetimes, and optional sandbox composition while preserving public APIs and runtime/WebGL support. Host session/auth policy is external.
- Instructions read: `AGENTS.md` and `Docs/ARCHITECTURE_RULES.md`.
- Recovery: the original CLI worker recorded this scope header, then provider/watchdog failure prevented a completed audit. A native takeover independently reviews every assigned path/hunk; no CLI completion claim is adopted.
- Progress: native static review complete. Three source-confirmed findings and the full 33-path/240-hunk ledger follow below. Root owns adjudication and executable proofs.

## Review questions

Before planning: the goal is to identify introduced Mods defects with concrete changed-line causality across all 33 primary paths. Required behavior includes strict persistence receipts, legacy best-effort compatibility, canonical staging/migration and path/UTF-8 safety, source/version/world publication ordering, cancellation/disposal, trusted iterator scope restoration, finite no-Rbx budgets, custom-store/public API compatibility, and WebGL-safe execution. Host authentication/lifetimes and the documented shared-world preset are outside this review.

Before delegation: this is an exclusive recovery leaf; additional agents and implementation are prohibited. Before accepting: each counted finding needs a supported trigger, expected/actual outcome, changed source cause, meaningful regression plan and stated uncertainty. Source inspection is not an executed proof.

## Confirmed findings

### PR-MODS-001 [P2] Preserve the legacy backend path through world source facades

**Changed target:** `Assets/CoreAIMods/Runtime/Infrastructure/RbxWorldPackageContracts.cs:4207-4208,4224-4232,4255-4263` (same capability projection in `SessionSourceStore:3545-3546`).

**Supported trigger:** a host supplies a custom `ILuaModSourceStore`/`IRbxWorldModSourceStore` without the optional strict interfaces, which is still an accepted world-session backend. A Hub mod operation receives the controller's source facade and chooses strict versus legacy persistence by its implemented interface.

**Expected/actual:** legacy save/delete must still reach that backend as best-effort operations and remain unconfirmed. The candidate facade always implements strict writer/remover, even when the current backend cannot. The Hub therefore never enters its existing legacy branch: the strict facade throws `NotSupportedException` before the backend's requested save/delete is called. In particular, a Hub delete can unload the live mod while retaining its legacy stored package, instead of performing the legacy removal and reporting it unconfirmed.

**Causal path:** `HubModServiceBase` selects strict save at lines 528-535 and strict delete at 378-385; both explicitly retain best-effort fallback branches. The added facade interfaces turn that selection true regardless of backend capability, then the new strict forwarders refuse. The strict method itself must not silently call legacy Save/Delete and return a false acknowledgement; the capability projection/selection must stay truthful. The added `WorldSourceFacade_LegacyStoreCannotAcknowledgeStrictMutations` test correctly proves that directly calling a strict method cannot acknowledge a legacy backend, but it does not test the real consumer's now-unreachable legacy fallback.

**Minimal regression plan:** compose the real world controller and Hub service with the existing `DelegateModSourceStore`-style legacy backend. Save/edit and delete through the Hub path; assert the requested backend calls occur, deletion removes the stored package, and persistence remains Unconfirmed/false. Repeat with strict-capable stores to preserve confirmed success, failed strict acknowledgement and retry behavior. Cover both initial active sources and a published replacement session.

**Uncertainty:** static integration trace only, no Hub/Unity proof run. This is a wrapper compatibility defect, not a request to claim strict success from a best-effort backend. The direct strict-refusal test should remain valid.

### PR-MODS-002 [P2] Keep a failed first canonical source save retryable

**Changed target:** `Assets/CoreAIMods/Runtime/Infrastructure/FileLuaModSourceStore.cs:679-685` (`ResolveModDirectory` identity check on every existing canonical directory).

**Supported trigger:** SaveStrict for a new mod creates its canonical directory, then the first manifest write fails before installation because of a transient disk-full/access/I/O fault. The fault is corrected and the identical operation is retried.

**Expected/actual:** a failed operation whose typed exception instructs retry should be retryable after the I/O fault is removed, without deleting foreign/corrupt identity evidence. The candidate leaves the newly created directory behind, but the next save sees an existing canonical directory without a readable manifest and refuses before writing. Every later retry is stuck until administrative filesystem repair.

**Causal path:** `SaveCore:163-166` creates the directory before the first manifest write. Its catch/finally does not roll back or mark an owned incomplete first installation. The new `ResolveModDirectory` requires a readable exact-id manifest whenever that directory exists. Baseline saves did not impose that new precondition and could complete the first installation on retry. The new incomplete-write test faults `main.lua.tmp` only after an earlier successful save, so a valid manifest already exists and hides this case.

**Minimal regression plan:** add a bounded fault seam just before the first manifest installation, fail a new-id save after its directory is created, remove the fault, and retry. Assert exact source/manifest installation, a successful strict acknowledgement, and no mutation of separately seeded corrupt/mismatching manifest evidence. Stage a new package or track/clean only its owned incomplete creation; do not weaken existing identity validation for arbitrary populated directories.

**Uncertainty:** source-based failure trace only. No disk fault or test was executed. A save that already has a valid manifest is not claimed affected by this exact trigger.

### PR-MODS-003 [P2] Reject malformed UTF-16 before deriving a namespace digest

**Changed target:** `Assets/CoreAIMods/Runtime/Infrastructure/LuaModStoreId.cs:34`.

**Supported trigger:** two supplied store ids are distinct CLR strings such as `"tenant-\uD800"` and `"tenant-\uD801"`, each containing a different unpaired surrogate. A literal replacement character can produce the same collision class. Neither ApplyTo nor the store constructors reject those ids.

**Expected/actual:** exact composition namespaces must remain distinct, or malformed identifiers must be rejected before I/O. The new digest hashes `Encoding.UTF8.GetBytes(id)` with replacement fallback. Both malformed suffixes become the same replacement bytes, so the full SHA-256 is identical and both stores use the same directory. Source, KV and startup namespaces therefore alias; a KV write under one namespace can be read/cleared through the other.

**Causality:** this collision is introduced by replacing the baseline sanitized namespace mapping with permissive UTF-8 hashing. The retained baseline `SanitizedFolderName` hashes each original UTF-16 char using FNV; for this pair the replacement underscores are followed by different hash suffixes. A complete digest cannot recover information already erased by the encoder. The new tests cover sanitizer/case collisions but not malformed string encoding.

**Minimal regression plan:** compare the two exact ApplyTo results and create/reopen same-mod KV stores for those namespaces; require rejection before directory creation, not shared bytes. Use a strict UTF-8 encoder (`UTF8Encoding(false, true)`) or equivalent explicit malformed-string validation for new key derivation. Keep valid supplementary-character identifiers and case isolation covered. The test must not infer safety merely from digest length.

**Uncertainty:** no encoder or storage probe was executed. This is deterministic source/encoding behavior; it does not depend on authentication or the host treating role ids as player ids.

## Repair review and uncounted boundaries

- Strict file source methods distinguish requested local completion from persistence arming and propagate cancellation. Identical delete retries request arming when already absent. The legacy Save/Delete paths still log arming refusal; no browser acknowledgement is inferred. PR-MODS-001 concerns optional-capability projection to a real legacy consumer, not granting strict acknowledgement to an incapable store.
- Exact replacement snapshots incoming manifests/sources before awaiting; canonical roots keep hex names, owned compact versions encode complete GUID/digest bits and require bounded layout markers. Windows source/manifest and future temporary paths are checked before cleanup or source mutation. Missing/corrupt/mismatching identities remain preserved. PR-MODS-002 isolates the newly created first-install directory case; it does not recommend overwriting corrupt evidence.
- Shared world-package transaction ownership was checked through holder/waiter refcounts, cancellation-before-acquisition, constructor TryEnter cleanup, quota admission, persistence/rollback and final release. Default filesystem identity is shared; custom adapters are explicitly required to share an instance when sharing storage. No new confirmed lease ownership defect was found.
- Overlay changes stage memory, write/arm before publication, retain old live state on failure and retry pending confirmations. Empty originals now use history existence rather than payload contents. Lua version writes require WorldEdit plus an explicit key policy; the trusted-host registration remains separate. Existing cross-instance overlay coordination debt is not presented as a newly introduced issue.
- Mod retirement is checked before remaining timer/handler batch work. Timer/event reservations preserve a global 256 invocation limit and rotate saturated mod passes. Added deterministic tests exercise full event batches and later timers. No timer modulo-zero defect exists: the snapshot has an explicit nonempty guard.
- Trusted iterator installation captures the native table iterator before script/registry replacement, accesses real protected metatables internally, and invokes `__iter` through the counted cancellation/yield boundary. Production source gates receive the optional backend resolver. Table/userdata, triples, cancellation, step-limit and error/yield cases were read; VM execution remains unverified here. No-Rbx startup retains finite defaults, as explicitly documented, and is not an unlimited-budget finding.
- HTTP lifetime/backpressure integration and its deferred fake tests were read at the factory/interface boundary. The adapter/scheduler implementation is primary scope of the scripting/Rbx owners; this report does not claim an exhaustive review of those unassigned runtime files or a real network result.
- Manage-mods cancellation is checked before identity and under dispatch authorization, including substituted gate-token cases. The synchronous runtime does not promise arbitrary callback preemption. Explicit host world/account/shared-world choices are not recast as framework bugs.
- A process crash, malformed snapshot or arbitrary logger failure was not promoted into an additional finding without introduced changed-line causality. Historical findings/receipts below were read as prior claims, not current proof.

## Complete read coverage

All 33 owner-06 primary paths and all 240 zero-context diff hunks were independently read against the immutable range. Counts below use `git diff --unified=0`; they establish read coverage only, not semantic/test/runtime coverage. No primary ownership gap remains. Unchanged large source/fixture bodies were read around relevant contracts; no whole-file claim is made for them unless stated.

| Primary path | Hunks read | Actual read depth |
| --- | ---: | --- |
| `Assets/CoreAI/Docs/LUA_NATIVE_APIS.md` | 1/1 | Complete changed Luau/trusted iterator/limit documentation. |
| `Assets/CoreAI/Runtime/Core/Features/RuntimeVersioning/MemoryDataOverlayVersionStore.cs` | 4/4 | Every changed baseline/history/import hunk with staged-overlay callers and empty-baseline tests. |
| `Assets/CoreAIMods/Runtime/Infrastructure/FileLuaModSourceStore.cs` | 53/53 | Every changed hunk; construction/layout, save/load/list/active/delete, full exact preparation/persistence path, identity resolution, path/temporary budgets, codec/marker validation, verification, atomic write and retention; replacement ownership context. |
| `Assets/CoreAIMods/Runtime/Infrastructure/FileLuaModStore.cs` | 10/10 | All constructor/verifier, best-effort arming and legacy migration changes; namespace/key mapping and consumer context. |
| `Assets/CoreAIMods/Runtime/Infrastructure/FileRbxWorldPackageStore.cs` | 16/16 | Complete new transaction coordinator; every gate replacement/constructor cleanup hunk; related quota, startup and rollback tests. |
| `Assets/CoreAIMods/Runtime/Infrastructure/LuaCsGameToolExecutor.cs` | 2/2 | Complete changed source-gate call and instance context exposing the engine resolver. |
| `Assets/CoreAIMods/Runtime/Infrastructure/LuaCsGameplayBindings.cs` | 10/10 | All lifetime interface, constructor forwarding, HTTP option and scoped version-registration hunks; owner/capability context. |
| `Assets/CoreAIMods/Runtime/Infrastructure/LuaCsModRuntimeFactory.cs` | 6/6 | Every new policy/budget option, validation and actual binding/runtime propagation hunk. |
| `Assets/CoreAIMods/Runtime/Infrastructure/LuaCsVersioningRuntimeBindings.cs` | 22/22 | All public constructor/registration changes, complete key authorization/read/write split and publication path. |
| `Assets/CoreAIMods/Runtime/Infrastructure/LuaModStoreId.cs` | 4/4 | Entire candidate file; baseline/current identity encodings compared directly. |
| `Assets/CoreAIMods/Runtime/Infrastructure/RbxWorldPackageContracts.cs` | 6/6 | Every new strict facade declaration/forwarder; full session source wrapper and session-change path, active source selection and real Hub consumers. Unchanged large world-controller body not claimed exhaustive. |
| `Assets/CoreAIMods/Runtime/LuaExecution/ILuaModSourceStore.cs` | 1/1 | Entire added strict save/remove interface contracts and legacy store boundary. |
| `Assets/CoreAIMods/Runtime/LuaExecution/LuaCsAiEnvelopeProcessor.cs` | 1/1 | Complete changed resolver-advertisement source-gate call with original-source version test. |
| `Assets/CoreAIMods/Runtime/LuaExecution/LuaCsModRuntime.cs` | 13/13 | All changed options/cursors, compilation, replacement/shutdown retirement, full timer/event tick/budget dispatch context. |
| `Assets/CoreAIMods/Runtime/LuaExecution/LuaModsLlmTool.cs` | 7/7 | Every cancellation/dispatch change; actual authorized action methods, queued gate and backup-cancellation tests. |
| `Assets/CoreAIMods/Runtime/LuaExecution/README.md` | 1/1 | Entire added 119-line runtime contract. |
| `Assets/CoreAIMods/Runtime/LuaExecution/README.md.meta` | 1/1 | Entire seven-line metadata; GUID search found one companion metadata. |
| `Assets/CoreAIMods/Runtime/Scripting/LuaCs/LuaCsSecureEnvironment.cs` | 7/7 | Every new trusted resolver/installation/entry-point hunk; counted-call boundary contract and all new production-path iterator tests. |
| `Assets/CoreAIMods/Tests/EditMode/FileLuaModSourceStoreEditModeTests.cs` | 7/7 | All added strict save/remove/cancellation/arming, ordinary collision/migration/namespace tests and changed corrupt/admission expectations; setup/fakes retained. |
| `Assets/CoreAIMods/Tests/EditMode/FileLuaModStoreEditModeTests.cs` | 7/7 | Every new best-effort arming/verifier/collision case and changed namespace expectation. |
| `Assets/CoreAIMods/Tests/EditMode/LuaCsModRuntimeEditModeTests.cs` | 1/1 | Complete changed saturation expectation and runtime dispatch contract. |
| `Assets/CoreAIMods/Tests/EditMode/LuaModsLlmToolEditModeTests.cs` | 4/4 | All imports and added gate/identity fakes, pre-cancelled action matrix and queued cancellation case. |
| `Assets/CoreAIMods/Tests/EditMode/ModsContractRegressionTests.cs` | 1/1 | Entire added 790-line fixture: grants, retirement, logging, timer/event fairness, protected iteration, HTTP lifetime and shared world transactions, including all fakes/helpers. |
| `Assets/CoreAIMods/Tests/EditMode/ModsContractRegressionTests.cs.meta` | 1/1 | Entire two-line metadata; GUID search found one companion metadata. |
| `Assets/CoreAIMods/Tests/EditMode/RbxApi/Acceptance/Mvp3WorldPackageEditModeTests.cs` | 11/11 | Every changed cancellation/result-shape/state/manual-slot hunk and shared awaited assertion helper. |
| `Assets/CoreAIMods/Tests/EditMode/RbxApi/Acceptance/Mvp3WorldPackageFollowUpEditModeTests.cs` | 11/11 | All new legacy strict facade, compact/reopen/capture/startup, marker/collision/Windows preflight cases; every changed fake/namespace path hunk. |
| `Assets/CoreAIMods/Tests/EditMode/RbxApi/Acceptance/RungZeroEnvelopeEditModeTests.cs` | 1/1 | Entire added tween target/replay and outbound-expansion cases; Rbx mutation implementation remains its primary owner's scope. |
| `Assets/CoreAiUnity/Runtime/Source/Features/Lua/Infrastructure/FileDataOverlayVersionStore.cs` | 23/23 | Every staged mutation, lock, pending confirmation, typed receipt and atomic cleanup hunk with Lua publication caller and failure/retry tests. |
| `Assets/CoreAiUnity/Tests/EditMode/DataOverlayVersionStoreEditModeTests.cs` | 4/4 | All imports and added/changed I/O failure, empty baseline, Lua no-publication, refused-arm/live-versus-file/retry cases. |
| `Docs/CoreAIMods/mod-system.md` | 1/1 | Complete changed runtime-contract cross-reference. |
| `dev-docs/audits/2026-10-23/05-mods-runtime-sandbox.md` | 1/1 | Entire added 310-line historical audit; no old confirmed/DONE/green claim adopted as a new PR finding. |
| `dev-docs/fix-wave-2026-10-24/14-unity-mods-integration.md` | 1/1 | Entire added 92-line strict-forwarding, staging/marker/path integration receipt and its stated limits. |
| `dev-docs/fix-wave-2026-10-24/workdoc05-mods.md` | 1/1 | Entire added 140-line repair contract, checkpoint/hash manifest and cancellation reconciliation; claims checked against current source where in primary scope. |

Supporting read-only contracts/callers: `HubModServiceBase` strict/legacy persistence selection; controller session source-change notification; source admission and replacement contracts; actor and key/grant/cancellation seams; source/overlay/store test fakes. Cross-reads do not transfer primary ownership of Hub, Rbx domain, HTTP adapter/scheduler or Luau compiler internals to this report.

## Verification and limits

- Static `git diff`, immutable `git show`, numbered source reads and `rg` searches only. No test/encoder/filesystem behavior probe was executed.
- Final HEAD remained the candidate SHA; the scoped runtime/test diff against it was empty. Direct report checks found all 33 primary paths, three finding headings, zero trailing-whitespace lines and zero Cyrillic prose lines.
- No build, portable/Unity test, Editor/player/WebGL/browser/device, real HTTP/DNS/provider, benchmark or Git mutation was run by the native takeover.
- Only this report is writable. The CLI scope header is retained above as recovery history, not completion evidence. Root owns reconciliation, executable regression proof, fixes and publication after all original audits finish.
- Final acceptance question: what did the new tests miss? A real legacy Hub consumer behind the strict-advertising facade, the first-manifest failure rather than a later source-file failure, and strict identifier encoding rather than only digest length/case/sanitizer pairs.
