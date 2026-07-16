# Test Quality & CI Audit — post-5.8.10 routing feature (2026-07-16)

Auditor scope: first-party code and tests (`Assets/CoreAI`, `Assets/CoreAiUnity`, `Assets/CoreAIMods`,
`Assets/CoreAIHub`, `Assets/CoreAIBenchmark`, `Assets/CoreAI.Demos`, tests, `.github/workflows`).
Focus: test quality/coverage of the unreleased 5.9.0 work (commits `222e6eae`, `92681445`, `fa37a523`):
runtime multi-endpoint LLM routing, endpoint readiness probes, Hub endpoint settings UI, Qwen demos.
All claims below were verified by reading the actual test and production code (no code modified).

## Scope & goal alignment

The new feature surface and where its tests live:

| Area | Production code | Tests |
|---|---|---|
| Portable contracts (`ILlmClientRegistry`, descriptors, profiles) | `Assets/CoreAI/Runtime/Core/Features/LlmRouting/ILlmClientRegistry.cs`, `LlmEndpointContracts.cs` | `Assets/CoreAiUnity/Tests/EditMode/LlmEndpointContractsEditModeTests.cs` |
| Readiness probes (policy, HttpClient, UnityWebRequest) | `LlmEndpointReadiness.cs`, `Assets/CoreAI/Runtime/Core/Features/Llm/HttpClientOpenAiReadinessProbe.cs`, `Assets/CoreAiUnity/.../UnityWebRequestOpenAiReadinessProbe.cs` | `Assets/CoreAI/Tests/EditMode/LlmEndpointReadinessEditModeTests.cs`, `Assets/CoreAiUnity/Tests/PlayMode/FastNoLlm/LlmRuntimeRegistryPlayModeTests.cs` |
| Runtime registry (activation, generations, drain, persistence, secrets) | `Assets/CoreAiUnity/Runtime/Source/Features/Llm/Infrastructure/LlmClientRegistry.cs` (~1200 lines), `LlmEndpointClientFactory.cs`, `LlmEndpointRegistryPersistence.cs` | `LlmEndpointRegistryPersistenceEditModeTests.cs` (25 tests), `LlmRuntimeRegistryPlayModeTests.cs` (12 UnityTests) |
| Router | `RoutingLlmClient.cs` | `RoutingLlmClientEditModeTests.cs` |
| Hub settings UI / chat API selector | `Assets/CoreAIHub/Runtime/HubSettingsPage.cs` (+751 lines), `CoreAiRoutingUiController.cs`, `CoreAiChatPanel.cs` | `CoreAiRoutingUiEditModeTests.cs`, `CoreAiChatRoutingPlayModeTests.cs` |
| LLMUnity activation / logs | `LlmEndpointClientFactory.cs`, `LlmUnityAutostartEntryPoint.cs` | `LlmUnityActivationLogEditModeTests.cs` |
| Qwen demos | `Assets/CoreAI.Demos/QwenDemo/*` | `QwenDemoScenesEditModeTests.cs`, `QwenDemoSafetyPlayModeTests.cs` |

Overall verdict: the flagship routing feature is **well-tested at the unit/lifecycle level** — better than
typical for a feature this fresh — but has **specific untested contract corners** (CancelInFlight, KeepWarm,
`Changed` event, fallback cycles) and **no test at all for the highest-risk user scenario: switching
endpoints mid-conversation with history preservation**. No hollow tests were found in the new wave.

## Confirmed problems

### High

- **H1. No test for endpoint switching mid-conversation preserving history.**
  The only "switch" test is `RouteSwitch_AffectsNextRequestWithoutRecreatingRegistry`
  (`Assets/CoreAiUnity/Tests/PlayMode/FastNoLlm/LlmRuntimeRegistryPlayModeTests.cs:370`) — it asserts the
  *next request routes to the new client* but never involves conversation state. Nothing anywhere asserts
  that a chat/agent conversation started on endpoint A carries its accumulated history into the first
  request after switching to endpoint B (via `AssignRoleProfile`, the chat panel dropdown, or
  `RoutingProfileId`). History tests exist (`AiOrchestratorHistoryEditModeTests.cs`,
  `AgentBuilderChatHistoryEditModeTests.cs`) but none combines history with a profile/endpoint change.
  Why it matters: this is the advertised product scenario of the 5.9.0 flagship feature, and history is
  owned by a different layer (orchestrator/agent memory) than routing — an accidental keying of history
  by profile/endpoint would ship undetected.
  Suggested fix: an EditMode test with a capturing `ILlmClient` per profile — send turn 1 on profile A,
  reassign the role to profile B, send turn 2, assert the second client's `LlmCompletionRequest` contains
  the turn-1 exchange.

### Medium

- **M1. `LlmEndpointRemovalMode.CancelInFlight` has zero test coverage — and its behavior is ambiguous.**
  Implementation: `Assets/CoreAiUnity/Runtime/Source/Features/Llm/Infrastructure/LlmClientRegistry.cs:715`
  unconditionally returns `Task.FromResult(false)` for `CancelInFlight`. The contract
  (`Assets/CoreAI/Runtime/Core/Features/LlmRouting/LlmEndpointContracts.cs:40-44`) says registries that
  cannot prove cancellation "must reject removal instead of reporting a false success" — so `false` is the
  intended rejection, but no test pins this, and `false` is indistinguishable from "endpoint not found"
  (`LlmClientRegistry.cs:740`). A future refactor could silently make `CancelInFlight` remove the endpoint
  while claiming failure, or vice versa. Suggested fix: a test asserting `RemoveEndpointAsync(id,
  CancelInFlight)` returns false *and* the endpoint remains registered and routable.

- **M2. `KeepWarm` lifecycle untested.** Real branching exists at `LlmClientRegistry.cs:634` (persist-time
  activation of inactive-but-warm endpoints), `:668` (deactivation only when `!active && !keepWarm`),
  `:1048` and `:1283`. It is also exposed on `SetEndpointActiveAsync(..., keepWarm)`. No test in the repo
  mentions `KeepWarm` (verified by grep across all test trees). A regression that releases the owned
  LLMUnity host despite keep-warm (expensive multi-second native restart) would pass all suites.
  Suggested fix: extend `LlmEndpointRegistryPersistenceEditModeTests` — deactivate with `keepWarm: true`,
  assert `ReleaseOwnedHostAsync` was *not* called and the client still resolves after reactivation.

- **M3. `ILlmEndpointRegistry.Changed` event never asserted.** It fires at 8 sites in
  `LlmClientRegistry.cs` (208, 641, 696, 768, 793, 831, 852, 868, 1101) and both the Hub settings page and
  the chat API selector rely on it for UI refresh, but no test subscribes to it on the real registry
  (grep for `.Changed +=` in tests: zero hits; the UI tests use a `FakeController` that invokes its own
  event). A dropped `Changed?.Invoke()` in a refactor leaves the Hub UI stale with all tests green.
  Suggested fix: assert `Changed` fires once per mutation (add/update endpoint, profile, role assignment,
  removal) and does not fire on rejected mutations.

- **M4. CI silently skips all Unity test jobs when `UNITY_LICENSE` is absent — except on the merge queue.**
  `.github/workflows/ci.yml:104-113` and `:182-191`: on `push` (including direct pushes to `main`, which
  is this repo's actual workflow — recent commits land directly on `main`) and non-fork PRs, a missing or
  expired secret produces a green run with a `::notice`, not a failure. The `merge-queue-gate` job
  (`ci.yml:47-60`) hard-fails only for `merge_group` events. Combined with the static README badge (L4
  below), a lapsed license means the repo advertises "1,500+ passing" while nothing runs.
  Suggested fix: extend the license hard-fail to `push` events on `main` (same job, broader `if:`), or add
  a final job that fails when the test jobs were skipped on `main`.

- **M5. Known freeze hazard reintroduced in new EditMode fixtures: sync-over-async NUnit asserts.**
  New tests use `Assert.ThrowsAsync` / `Assert.CatchAsync` in EditMode:
  `LlmEndpointRegistryPersistenceEditModeTests.cs:399` (`ThrowsAsync<KeyNotFoundException>`), `:582`
  (`CatchAsync<OperationCanceledException>`), `LlmEndpointReadinessEditModeTests.cs:105`,
  `LlmUnityActivationLogEditModeTests.cs:247`. Project history (TODO/audit notes) documents that exactly
  this pattern has caused real interactive Test Runner freezes (sync-over-async deadlock); batchmode is
  reliable but the team also runs interactively. All four current call sites complete their awaited task
  synchronously or from a threadpool continuation, so they *probably* don't hang today — but the pattern
  is the documented hazard. Suggested fix: replace with the project's established `try { await ... ;
  Assert.Fail(...) } catch (ExpectedException) {}` pattern used elsewhere in the same wave
  (`RoutingLlmClientEditModeTests.cs:150-157`).

### Low

- **L1. Source-text "grep test" instead of behavior test.**
  `LlmUnityActivationLogEditModeTests.cs:186-194` (`NativeActivationSource_WaitsForReadinessWithoutWarmupPrompt`)
  does `File.ReadAllText` on `LlmEndpointClientFactory.cs` and asserts substrings
  (`llm.WaitUntilReady()`, absence of `agent.Warmup`, `FindObjectsInactive.Include`). Brittle (breaks on
  rename/refactor of unrelated text), passes if the string appears in a comment, and proves nothing about
  runtime behavior. Acceptable as a stopgap for LLMUnity-gated code, but should be flagged for
  replacement with a behavioral test behind `COREAI_HAS_LLMUNITY`.

- **L2. Timing-sensitive asserts in new EditMode tests (flake risk on slow runners).**
  - `LlmEndpointRegistryPersistenceEditModeTests.cs:375`: `Restore_ResolvesPersistedSecretReferenceBeforeActivation`
    relies on a single `await Task.Yield()` for the constructor-initiated activation to have called the
    factory. Works today because `FakeFactory` completes synchronously; fragile if activation gains a real
    async hop.
  - `:621`: `ConcurrentPersistenceWrites_CannotFinishWithOlderSnapshot` uses `await Task.Delay(50)` before
    asserting `store.Calls == 1`.
  - `:512-515`: `DrainRemoval...` polls up to 20 `Task.Yield()`s for the deferred release.
  Suggested fix: gate on explicit signals (TCS) instead of yields/delays where possible.

- **L3. Unbounded completion-wait loops and non-`finally` cleanup in `LlmRuntimeRegistryPlayModeTests.cs`.**
  All 12 UnityTests use `while (!task.IsCompleted) yield return null;` with no local deadline — a
  regression that never completes manifests as the 3-minute default UTF timeout (or an interactive-runner
  hang) instead of a fast, descriptive failure. `Object.Destroy(settings)` is at the end of the test body,
  not in a `finally`/TearDown, so a failed assert leaks the `CoreAISettingsAsset` instance into subsequent
  tests (benign today, but it is shared-fixture hygiene).

- **L4. README badge is hand-maintained, not CI-derived.** `README.md:10` —
  `img.shields.io/badge/EditMode-1,500+ passing` is a static badge that can never turn red. Current
  numbers are honest (grep counts ~1,676 `[Test]`/`[TestCase]`/`[UnityTest]` attribute occurrences across
  EditMode trees; TODO.md's 2026-07-15 verification says 1,691 passed / 0 failed, so "1,500+" is
  understated, which is fine) — but nothing keeps the badge and reality coupled, and the adjacent CI badge
  covers only what CI runs (see M4). Note only; suggested fix: derive the count badge from the uploaded
  `editmode-results.xml` artifact, or reword to reference the CI badge.

- **L5. Null-conditional reflection helpers can silently no-op.**
  `CoreAiRoutingUiEditModeTests.cs:296-318`: `SetField`/`Invoke`/`InvokePage` use `?.Invoke` / `?.SetValue`
  — if a private member is renamed, the call silently does nothing instead of failing at the call site.
  In most tests a downstream assert still fails, but `SetField(panel, "HeaderTitle", title)` before
  `EnableApiSwitching` is load-bearing setup; a rename would shift the failure to a confusing place (or,
  for future asserts written against defaults, not fail at all). Same pattern at
  `QwenDemoScenesEditModeTests.cs:71,89`. Suggested fix: `Assert.NotNull(memberInfo)` inside the helpers.

## Coverage gaps (dynamic-routing feature)

Beyond the confirmed problems above (H1, M1-M3):

1. **Fallback chains beyond one hop.** `TryResolveReadyRuntimeEndpointLocked` (`LlmClientRegistry.cs:900-937`)
   implements BFS with a `visited` set (cycle-safe by construction), but the only fallback test is one-hop
   (`ReadyFallback_ReportsEffectiveProfileContextAndMode`,
   `LlmEndpointRegistryPersistenceEditModeTests.cs:405`). No test for: multi-hop chains, A→B→A cycles at
   *resolution* time (validation only rejects direct self-fallback, `LlmEndpointContracts.cs:155`), or a
   fallback target whose profile references a missing endpoint.
2. **`RemoveProfile(profileId, replacementProfileId)`** — no direct test of profile removal with
   replacement re-pointing role assignments (endpoint removal with replacement is tested; profile removal
   is not).
3. **Role pattern matching / `sortOrder`.** `AssignRoleProfile(string rolePattern, string profileId,
   int sortOrder)` accepts a *pattern* and priority; every test uses exact role ids and default sortOrder.
   Wildcard/pattern semantics and priority collisions are untested.
4. **Hub settings UI ↔ real registry integration.** All `CoreAiRoutingUiEditModeTests` run against
   `FakeController`; `CoreAiRoutingUiController` (171 lines, the real adapter between the Hub page and
   `LlmClientRegistry`) has no direct test. The write-only session-key contract is verified only against
   the fake.
5. **LLMUnity-kind endpoint lifecycle.** Native activation is covered only by `COREAI_HAS_LLMUNITY`-gated
   EditMode tests of helpers (`ResolveAgent`, `ApplyNativeConfiguration`, coordinator) plus the L1 grep
   test. There is no test of a full `LlmEndpointKind.LlmUnity` activation path through the registry (even
   with a fake `LLM` host); acceptable given hardware constraints, but the seam between
   `LlmEndpointClientFactory` and `LlmUnityAutostartEntryPoint` (reworked twice in this wave) is thin.
6. **Readiness probe timeout semantics in the Unity adapter.** `UnityWebRequestOpenAiReadinessProbe` is
   tested for status policy and cancellation-aborts-connection (excellent), but `TimeoutSeconds` itself
   (expiry → not-ready without exception) is only tested on the HttpClient probe.

What *is* covered well for the feature (verified, not hollow): descriptor/profile validation; legacy
default-interface overloads; generation hot-swap keeping in-flight calls on the old client
(`LlmRuntimeRegistryPlayModeTests.cs:634`); failed hot-swap leaving the old generation routable (`:686`);
concurrent activation coalescing; per-caller cancellation not killing shared activation; drain removal
deferring owned-host release; secrets never persisted (asserted on serialized JSON,
`LlmEndpointRegistryPersistenceEditModeTests.cs:191-194`); write-only session key with blank-means-preserve
and explicit-empty-means-clear; readiness status-policy matrix on both HTTP adapters against real loopback
TCP servers; explicit request profile overriding role assignment; profile propagation end-to-end
(AgentBuilder → AgentConfig → AiTaskRequest → LlmCompletionRequest).

## Hollow-test regression check (new tests only)

Checked every new/changed test file from the three commits for can't-fail assertions, swallowed
exceptions, stub-returns-nothing passes, and hidden `[Ignore]`/`[Explicit]`:

- **No new `[Ignore]` or `[Explicit]` attributes** were added (all existing `[Explicit]` are live-model
  suites with stated reasons — compliant with `Assets/CoreAiUnity/Tests/README.md` policy).
- **try/catch usage is sound**: catches in `RoutingLlmClientEditModeTests.cs:150-157,174-181` are the
  correct expected-exception pattern with `Assert.Fail` on the non-throw path; the reworked
  `CoreAiChatServiceEditModeTests` timeout test (commit `fa37a523`) added a `finally` restoring
  `Time.timeScale` and asserts the exact exception type — an improvement, not a swallow.
- **Stubs verify interactions, not just non-null**: `FakeFactory` records session keys/cancellation
  tokens/call counts; `MemoryStore` counts saves; negative paths assert `factory.Calls == 0`
  (`InactiveAdd_DoesNotCallFactory`) and `LastSavedEndpoint == null`
  (`HubEndpointEditor_InvalidHttpUrl_DoesNotCallRegistry`).
- Borderline items are recorded as L1 (grep test), L2 (timing), L5 (null-conditional reflection) —
  none is an outright hollow pass today.

## Test infrastructure

- **FastNoLlm stays genuinely LLM-free.** The new `LlmRuntimeRegistryPlayModeTests` uses in-process
  loopback `TcpListener` HTTP servers and fake factories/clients — no model, no external network, no
  `[Explicit]` escape hatches. `CoreAI.Tests.PlayMode.FastNoLlm.asmdef` has no live-LLM references.
- **Static state is reset correctly in the new tests**: `CoreAiRoutingUi.Controller` (a static production
  hook) is nulled in `[TearDown]` (`CoreAiRoutingUiEditModeTests.cs:27-31`) and in `finally`
  (`CoreAiChatRoutingPlayModeTests.cs:44`); `CoreAIAgent.Reset()` in
  `LlmEndpointContractsEditModeTests.cs:38-42`; `LlmUnityActivationCoordinator.Release(agent)` in
  `finally` (`LlmUnityActivationLogEditModeTests.cs:251`). Registries are instance-scoped per test.
- **Sync-over-async**: the four `ThrowsAsync/CatchAsync` sites (M5) are the only new instances; the new
  PlayMode tests correctly poll with `yield return null` instead of blocking.
- Settings assets created per-test are destroyed (though not always in `finally` — L3).

## CI (.github/workflows/ci.yml)

What actually runs in CI on push/PR/merge_group:
1. `package-graph` — license-free lockstep/dependency check (always runs).
2. `merge-queue-gate` — hard-fails a merge_group run if `UNITY_LICENSE` is missing (good design).
3. `analyzer` — dotnet Roslyn analyzer build + tests (always runs, license-free).
4. `editmode-tests` — full EditMode in a 3-config matrix (`lua`, `no-lua` via `COREAI_NO_LUA`, `no-llm`
   via `COREAI_NO_LLM`), **includes all new routing/readiness/Hub/Qwen EditMode tests** since they live in
   the standard EditMode assemblies. Has an anti-hollow guard: fails if the Lua sandbox fixture produced
   <3 result entries (`ci.yml:154-165`).
5. `playmode-fastnollm` — PlayMode filtered to the `CoreAI.Tests.PlayMode.FastNoLlm` assembly, **includes
   the new `LlmRuntimeRegistryPlayModeTests`, `CoreAiChatRoutingPlayModeTests`, `QwenDemoSafetyPlayModeTests`,
   `CoreAiLuaWorldModulePlayModeTests`**. Anti-hollow guard: fails if <10 `PlayModeTests` occurrences in
   results (`ci.yml:217-227`).

CI-gated vs manual-only: **EditMode and PlayMode FastNoLlm are CI-gated** (subject to M4's license-skip
hole). **LlmInfra, LlmVerification, Scenarios, and Benchmark suites are manual-only** — reasonable since
they need a live model server (`coreai-live-tests.local.json`), and the README's test section describes
this split; the risk is confined to the M4 skip behavior, not to a misleading suite claim.

The new routing code paths are therefore covered by CI in all three define configurations (including
`COREAI_NO_LLM`, which proves the routing layer compiles out cleanly).

## README badge claim

`README.md:10` claims "EditMode — 1,500+ passing". Grep count of `[Test]`/`[TestCase]`/`[UnityTest]`
attribute lines across all first-party EditMode test trees: ~1,676 (161 files). TODO.md's last full
verification (2026-07-15) reports 1,691 EditMode passed / 0 failed / 4 ignored, PlayMode FastNoLlm 73
passed. **The badge is consistent with reality (understated) — no discrepancy today.** The structural
weakness is that the badge is static (L4) and CI can skip silently (M4), so the claim is maintained by
discipline rather than automation.

## What is done well

- The new registry tests exercise **real concurrency and lifecycle races** with deterministic gates
  (TaskCompletionSource-gated factories/clients) instead of sleeps: hot-swap generation semantics,
  activation coalescing, superseding startup cancelling the old generation, drain-deferred host release.
  This is exactly the level the feature's risk demands.
- **Readiness probes are tested against real loopback HTTP servers** in PlayMode (including proving that
  cancellation aborts the native connection — `LlmRuntimeRegistryPlayModeTests.cs:548-576`) plus a full
  status-code policy matrix shared between the .NET and Unity adapters.
- **Security-relevant assertions are direct**: session keys asserted absent from serialized persistence
  JSON; probe error messages asserted not to leak host names; unsafe base URLs (file://, userinfo, query,
  fragment) rejected without sending.
- CI has **anti-hollow guards** (minimum result-count checks) born from the previous "hollow test" audits
  — a rare and valuable pattern.
- The wave respects the project's test-suite discipline: no new `[Ignore]`/`[Explicit]`, FastNoLlm kept
  deterministic, statics reset, and the one fixed flaky test (`Time.timeScale` in
  `CoreAiChatServiceEditModeTests`) was fixed by making the product behavior real-time-based, not by
  loosening the assert.
