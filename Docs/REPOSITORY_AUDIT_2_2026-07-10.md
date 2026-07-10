# CoreAI Repository Audit #2 — Post-Remediation Verification

**Date:** 2026-07-10 (same day as Audit #1, after the remediation wave)
**Baseline:** `HEAD 259a447b` + the uncommitted remediation working tree (~118 files)
**Prior audit:** [REPOSITORY_AUDIT_2026-07-10.md](REPOSITORY_AUDIT_2026-07-10.md) (findings F-01…F-25)
**Scope:** verification of the remediation wave, new defects introduced by it, current stability /
correctness / optimization posture, release-goal alignment.
**Method:** direct code inspection of every claimed fix (grep + file reads of the actual working
tree), cross-checked against the eleven `PROGRESS.fix-*.md` reports and the live `Editor.log`.
Multi-agent deep sweep was attempted but aborted by an API session limit; every statement below is
therefore backed by a cited file — items that could not be re-read are explicitly marked
**UNVERIFIED** instead of guessed.

---

## 1. Executive verdict

The remediation wave is **real, substantial, and largely faithful to the Audit #1
recommendations** — every P0/P1 code fix that a PROGRESS report claims was spot-checked and found
present in the working tree, usually with new tests. However, the wave shipped with **two defects
of its own that broke compilation of the entire test tree**, and — the single most important fact
of this audit — **not one of the eleven fix sessions ever saw a successful compile or test run**
(the Unity Editor was unresponsive to MCP in all of them). The wave was flying blind; this audit
caught what the missing compile gate let through.

Both compile blockers were fixed during this audit (restored to the HEAD versions; see A-01).
After that restoration the last-known error set in `Editor.log` is empty, but a fresh compile +
full EditMode run is still **mandatory before commit** — the Editor currently holds the project
lock, so batchmode could not be run from this session either.

### State of the Audit #1 findings

| Status | Count | Findings |
|---|---|---|
| Verified fixed in code | 13 | F-01, F-02, F-04, F-05, F-07(core), F-08, F-09, F-10, F-11, F-13, F-16, F-23, +GAP1-3 (dynamic-worlds) |
| Partially fixed | 3 | F-06 (WebGL sync only in WorldState + audit writer), F-15 (resolve cache only), F-19/F-22 (benchmark extracted, dev project still heavy) |
| Not addressed (still open) | 6 | F-03, F-12, F-14, F-17, F-18, F-20, F-21 |
| Not re-checked this pass | 2 | F-24, F-25 |

### New findings of this audit

| ID | Priority | Summary | Status |
|---|---|---|---|
| A-01 | P0 | Working tree did not compile: two accidental test-tree regressions | **Fixed during this audit** |
| A-02 | P0 (process) | Entire remediation wave landed with zero compile/test verification | Open — gate before commit |
| A-03 | P1 | Package version lockstep broken (coreaiunity 5.0.10 vs 5.1.0 elsewhere) | **Fixed during this audit** (all five packages + dependency pins at 5.1.0, changelog entries added) |
| A-04 | P2 | CHANGELOG release-flow ambiguity (`[Unreleased]` holds already-versioned entries) | Open (5.1.0 entries added in the existing style; flow decision still pending) |
| A-05 | P2 | Repo root polluted with 14 PROGRESS.\*/TestRun\_\*.log working files | **Fixed during this audit** (moved to `Docs/Local/Progress/`) |
| A-06 | P1 (carried) | Audit writer still serializes JSON + SHA-256 on the main thread per entry | Open (acknowledged residual of F-07) |

---

## 2. What was verified and how

Verification was performed per finding by reading the current working-tree source (not the
PROGRESS claims). Symbols and line references below are from the tree as of this audit.

### 2.1 F-01 — mutating tools parallel/duplicate execution — **VERIFIED FIXED**

`Assets/CoreAI/Runtime/Core/Features/Llm/ToolExecutionPolicy.cs` (1681 lines) now contains the
full claimed model:

- Per-call duplicate signatures: `BuildDuplicatePlan` (line ~187), `MarkDuplicate` (~241),
  `TryBuildDuplicateSignature` (~255) — only *succeeded* calls register a signature, so a failed
  call can always be retried.
- Single serialized mutation chain: `SerializedMutatingToolNames` (~884) +
  canonical-name-aware `IsSerializedTool` (~894) covering `world_command`, `component_command`,
  `execute_lua`, `call_skill_tool`, plus the pre-existing memory/mods/skills tools.
- Streamed mutation deferral: `ExecuteStreamedAsync` (~1184) buffers mutating calls;
  `CompleteStreamedTurn` (~1289) throws if deferred work is still in flight; the async
  `CompleteStreamedTurnAsync` (~1325) executes them serially in arrival order.
- Cross-turn echo produces a structured `{ok:true, duplicate:true}` no-op payload.

Tests: `ToolExecutionPolicyEditModeTests.cs` is modified in the tree (6 new cases per the
PROGRESS report — intra-batch repeats, echo no-op, failed-retry-not-suppressed, partial-success,
streamed deferral, serialization order). **Not yet executed** (A-02).

### 2.2 F-02 — package dependency graph — **VERIFIED FIXED** (one residual, see A-03)

- Hub-facing Mods code physically moved to `Assets/CoreAIMods/Runtime/HubIntegration/` with a new
  optional assembly `CoreAI.Mods.Hub.asmdef` (present, with Unity-generated `.meta`).
- `CoreAI.Mods.asmdef` no longer references `CoreAI.Hub.UI` (grep: zero matches).
- `Assets/CoreAIHub/package.json` now honestly declares `com.neoxider.coreaiunity` — but pins
  `5.0.10` while the packages are being bumped to `5.1.0` (A-03).

### 2.3 F-04 — WorldState inactive objects / Reset — **VERIFIED FIXED**

`Assets/CoreAiUnity/Runtime/Source/Features/World/WorldStateManager.cs`:
`FindObjectsInactive.Include` at lines ~170 (Save) and ~441 (DestroyAllWorldObjects);
`_unresolvedObjects.Clear()` at ~412 inside Reset. Three new PlayMode tests exist in
`WorldStateManagerPlayModeTests.cs`. **Not yet executed.**

### 2.4 F-05 / GAP1 — `load_scene` premature success — **VERIFIED FIXED**

`Assets/CoreAiUnity/Runtime/Source/Features/World/Infrastructure/CoreAiWorldCommandExecutor.cs`:
`IsSceneInBuildSettings` (line ~748) is checked before `SceneManager.LoadScene` (~736). The tree
goes further than the audit asked: a per-scope scene whitelist (`allowedLuaScenes` on
`CoreAILifetimeScope` / `WorldCommandsInstaller`) gates Lua's `coreai_world_load_scene`.

### 2.5 F-06 — WebGL durability — **PARTIALLY FIXED**

- `WorldStateManager.Save()` now calls `CoreAi_PersistFsSync()` (IDBFS flush) — verified at
  lines 26–33.
- The audit writer flushes through the new shared helper
  `Assets/CoreAiUnity/Runtime/Source/Infrastructure/CoreAiWebGlPersistence.cs`.
- **Residual:** the other file stores (memory, mods, skills, version stores) still use their own
  per-store `DllImport` pattern; the shared helper was deliberately not retrofitted. Acceptable,
  but the unification remains open.

### 2.6 F-07 — AuditLogWriter loss/backlog/rotation — **VERIFIED FIXED (core)**, A-06 residual

`Assets/CoreAiUnity/Runtime/Source/Features/Audit/AuditLogWriter.cs`:

- Bounded queue with drop-oldest + `_droppedCount` marker (lines ~72–91, `Interlocked` counters).
- seq/prevHash now committed only after a successful write; failed batch re-queued at front.
- Rotation writes a `RotationMarker` (old file) + `RotationAnchor` (new file, line ~248);
  `AuditLogVerifier` accepts anchored genesis and gained `VerifyChainedSet`.
- Dispose drains with a ~2s deadline; `FlushBatch` is serialized by a lock.

**Residual (A-06):** JSON serialization + SHA-256 chaining still run on the main thread per
entry, and the flush loop is `UniTask.Delay`-driven. Under a tool-call burst this is measurable
frame cost. Carried forward as an open P1 optimization.

### 2.7 F-08 — Lua allocation bombs — **VERIFIED FIXED**

- `Assets/CoreAIMods/Runtime/Sandbox/InstructionLimitDebugger.cs`: per-instruction
  `GC.GetAllocatedBytesForCurrentThread()` budget check against a baseline captured in `Reset`,
  `DefaultMaxAllocatedBytesBudget = 64 MB` (lines 23–95). Checked **every** instruction — the
  right call, since `s = s .. s` doubling is exponential and a sampled check can lose the race.
- Same design mirrored in `LuaCsExecutionGuard.cs` for the Lua-CSharp VM.
- `table.concat` output capped in both secure environments (mirrors the existing
  `string.rep` cap).
- Tests added on both VMs, including the new `LuaCsSecureSandboxEditModeTests.cs`
  (required adding `Lua.dll` to `CoreAI.Mods.Tests.asmdef` `precompiledReferences` — a justified,
  documented infra change).

### 2.8 F-09 — fallback skipped on real streaming/timeout failure — **VERIFIED FIXED**

`Assets/CoreAiUnity/Runtime/Source/Features/Llm/Infrastructure/FallbackLlmClientDecorator.cs`:

- `catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)` rethrows
  genuine user cancellation (line ~64/118); a plain OCE (internal transport timeout) now falls
  back to the secondary.
- Streaming "commitment" redefined as the first chunk with visible text, a tool call, or an
  error — the benign `BufferedStreamingNoToolBinding` control chunk no longer commits the
  primary (lines ~90–143). A terminal empty stream now triggers fallback.
- 3 new tests in `ResilienceFeaturesEditModeTests.cs`; the 6 pre-existing fallback tests were
  hand-traced by the fix author. **Not yet executed.**

### 2.9 F-10 — unbounded orchestrator queue / Dispose — **VERIFIED FIXED**

`Assets/CoreAI/Runtime/Core/Features/Orchestration/QueuedAiOrchestrator.cs`:
`MaxPending` (default 64) admission cap with `AiOrchestrationQueueFullException` (~441),
`InsertSorted` binary-search insertion replacing full re-sort (~374, safe because the comparer
includes a unique `Sequence`), `_lifetimeCts` linked into all in-flight work (~32, ~194, ~239)
and cancelled on Dispose; post-Dispose enqueue throws while `CancelTasks` stays a safe no-op
(preserving the pre-existing test contract). 6 new tests in
`QueuedAiOrchestratorEditModeTests.cs`. **Not yet executed.**

### 2.10 F-11 — unbounded version stores — **VERIFIED FIXED**

- New `Assets/CoreAI/Runtime/Core/Features/RuntimeVersioning/VersionRetentionPolicy.cs`
  (keep original + current + last N intermediates, 2 MB byte budget).
- `MemoryLuaScriptVersionStore` / `MemoryDataOverlayVersionStore`: monotonic `NextIndex`
  decoupled from list position; `ImportFromRecords` re-enforces retention on legacy files.
- The fix also caught and repaired a **latent silent-wrong-revision bug**: both
  `LuaCsModRuntime.TryRevertMod` and `LuaModRuntime.TryRevertMod` used positional indexing where
  the UI contract is the stable `Index` field — with eviction those diverge. Good catch;
  index-based lookup verified in the tree.
- File stores gained dirty-flag write skipping.

### 2.11 F-13 / F-16 / F-23 — **VERIFIED FIXED**

- F-13: both mod runtimes now raise every event through `GetInvocationList()` with a per-
  subscriber try/catch (`LuaCsModRuntime.cs` lines ~854–938 and the MoonSharp twin), so one
  throwing subscriber cannot break `LoadMod` or starve other subscribers. Tests added.
- F-16: `InMemoryAgentTurnTraceSink` capped (`DefaultMaxRoles=32`) + `Clear()`. The six keyed-
  lock `ConcurrentDictionary<string, SemaphoreSlim>` registries were deliberately **not** given
  eviction — the TOCTOU reasoning (evict-then-GetOrAdd hands out a second semaphore for the same
  resource) is correct; the growth bound is documented at each declaration. Accepted.
- F-23: `OrchestrationDashboard` / `CoreAiTokenBudgetOverlay` now rebuild their strings at most
  4×/sec (`RefreshViewModelIfDue`, `Time.unscaledTime` throttle); OnGUI reads cached fields.

### 2.12 Hub/Mods UX + optimization review items — **VERIFIED PRESENT** (sample)

The `fix-hub-modux` and `fix-optreview` waves (version history/revert UI, bundled-update badge,
export/import via clipboard, mod-list caching + 250 ms search debounce, `LuaModRuntimeTicker` and
other dead code deleted, `Resolve(int)` ConcurrentDictionary cache in
`LuaCsFullUnityRuntimeBindings`, shared `CoreAiDemoScope`, `MaterialPropertyBlock` tinting in the
Wave Auto-Battler) are present per spot checks of `HubIntegration/` and demo controllers. These
partially advance F-15 (resolve cache) but the general visited-budget for O(N) scene walks
remains open.

### 2.13 Still open from Audit #1 (no fix attempted)

- **F-03** — release/normative docs still don't describe the (now five-) package product line;
  the benchmark extraction (`com.neoxider.coreaibenchmark`, `Assets/CoreAIBenchmark/`) makes this
  more stale, not less.
- **F-12** — CI (`.github/workflows/ci.yml`) still does not prove the release surface; after
  this wave it also doesn't compile-gate the new asmdef graph. Combined with A-02 this is the
  most dangerous open process gap.
- **F-14** (additive entry-point ownership), **F-17** (oversized classes), **F-18** (floating
  git deps), **F-20** (perf regression suite), **F-21** (timing-flaky tests) — untouched.
- **F-24, F-25** — not re-checked this pass; treat as open. Note the README test badge
  ("1314 EditMode") is now certainly stale given the dozens of added tests.

---

## 3. New findings (this audit)

### A-01 — P0 — The remediation wave broke compilation of the test tree — **FIXED during audit**

The freshest compile block in `Editor.log` (09:03 today) ended with **26 unique errors**:

1. `PlayModeTestAwait.cs` had been reverted in the working tree to a pre-HEAD version, deleting
   the 4-argument `WaitTask(Task, float, string, CancellationTokenSource)` overload while ~25
   call sites across 13 PlayMode test files (committed at HEAD) still pass the CTS argument →
   `CS1501` ×228 accumulated over the session.
2. `AiGameCommandRouterMainThreadPlayModeTests.cs` had been edited (moonsharp `#if` guard removed,
   `lua` passed as a 4th ctor arg) against a router whose Lua parameter is compiled out of
   `CoreAI.Source` → `CS1729`.

Neither change is claimed by any `PROGRESS.fix-*` report — they are collateral damage from
parallel agent sessions editing the same tree without a compile gate. **Both files were restored
to their HEAD versions during this audit** (`git checkout`), which resolves every error in the
final compile block. Anything else must be confirmed by a fresh compile (A-02).

### A-02 — P0 (process) — Zero compile/test verification behind the whole wave

All eleven PROGRESS reports contain the same disclaimer: Unity Editor unresponsive over MCP,
no local batchmode fallback executed, correctness argued by manual re-reading. That is not a
substitute: A-01 proves the tree was uncompilable for an unknown span of the wave.

**Required gate before committing this wave** (in order):

1. Focus/reopen the Editor (it currently holds `Temp/UnityLockfile`) and let it recompile, or
   close it and run batchmode.
2. `Unity.exe -batchmode -projectPath . -runTests -testPlatform EditMode` with results written
   outside `Temp/` (per the established workflow); zero red.
3. PlayMode `FastNoLlm` suite (no backend needed); zero red.
4. Only then commit, in logical batches.

### A-03 — P1 — Version lockstep broken across the package family

Found: `coreai 5.1.0`, `coreaiunity 5.0.10`, `coreaimods 5.1.0`, `coreaihub 5.1.0`,
`coreaibenchmark 5.1.0`, with every package's dependency pins still at `5.0.10`. The house rule is
that `coreai` and `coreaiunity` ship in lockstep with identical versions.
**Fixed during this audit:** `coreaiunity` bumped to `5.1.0`, all dependency pins across the five
packages aligned to `5.1.0`, and `5.1.0` changelog entries added to both changelogs describing the
remediation wave.

### A-04 — P2 — CHANGELOG flow ambiguity

Both changelogs keep already-versioned entries (`### 5.0.11…5.0.13`) under a `## [Unreleased]`
heading while `package.json` has moved past some of them. Decide: either entries get their
version at release time (keep true Unreleased), or drop the Unreleased wrapper. Today the file
cannot be read as a release record.

### A-05 — P2 — Working-tree clutter at repo root — **FIXED during audit**

14 `PROGRESS.*.md` session reports sat in the repo root next to `TestRun_*.log` files and a stray
`Assets/dev/.../Lua.dll.bak-0.5.5` (which Unity was importing). During this audit the PROGRESS
files were moved to `Docs/Local/Progress/` (gitignored, the established place for non-shipping
material) and the `.dll.bak` was moved out of `Assets/` with its `.meta` removed. `TestRun_*.log`
are already gitignored.

### A-06 — P1 — Audit writer main-thread serialization (carried residual)

See §2.6. Acknowledged out-of-scope by the F-07 fix; listed here so it does not silently vanish:
move JSON + SHA-256 to a worker (or at least amortize), keeping only the queue handoff on the
main thread.

---

## 4. Current posture

**Stability/correctness.** With the wave in place, every P0/P1 *code* finding from Audit #1 has
either a verified fix or a documented, deliberate residual. The dominant remaining risk is not a
specific code path but the **unverified state of ~4,700 inserted lines** (A-02). Secondary risks:
F-14 (lifecycle ownership), F-21 (flaky timing tests) — both can surface as intermittent CI red
once the compile gate exists.

**Optimization.** The cheap, high-yield items from Audit #1 §6.2 are done (IMGUI caching, mod
list caching, resolve cache, insertion sort, dirty-flag writes, material pooling). The remaining
meaningful items are structural: A-06 (audit writer off-main-thread), F-15 visited budgets for
scene walks, F-20 regression suite so none of this regresses silently.

**Goal alignment.** The product story ("production layer between a chat box and gameplay")
is materially *more* true after this wave: mutation safety (F-01), durability (F-04–F-07),
sandbox hardening (F-08), backpressure (F-10) are exactly "production layer" work. The gaps that
remain are release-engineering gaps (F-03, F-12, F-18, A-03, A-04) — the code outruns the
packaging/CI/docs truth, which is the pattern Audit #1 already called the strategic risk.

---

## 5. Recommended sequence from here

1. **Now:** compile gate + EditMode + FastNoLlm PlayMode (A-02). Fix whatever red appears.
2. **Same session as commit:** ~~version lockstep 5.1.0 (A-03), changelog entries, root cleanup
   (A-05)~~ — done during this audit; remaining: README badge/test-count refresh (F-25).
3. **Next release:** CI release-surface gates (F-12) — this is what prevents the next A-01/A-02.
4. **Next 1–2 releases:** audit-writer worker thread (A-06), F-14, F-15 visited budgets, F-20,
   F-18 pinned deps, F-03 five-package docs.

---

## 6. Conclusion

Audit #1 said the idea was convincingly implemented but "scales to production" outran the
reliability of several system boundaries. The remediation wave closed most of those boundaries —
and then demonstrated the *next* weakest boundary by breaking the build in a way none of its
eleven sessions could see. The code is now in the best shape it has been; the process (compile
gates, CI truth, release hygiene) is what stands between this repository and the production
credibility it advertises.
