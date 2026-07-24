# Package audit — v6.6.0 (2026-07-24)

Read-only audits of `Assets/CoreAIHub/`, `Assets/CoreAI/` and `Assets/CoreAiUnity/` against
`Docs/ARCHITECTURE_RULES.md` and `CONTRIBUTING.md`. **Nothing here is fixed yet** — this is the
backlog. Lua/runtime performance is a separate document: `LUA_PERF_AUDIT_v6.6_2026-07.md`.

Findings are as reported by the audit pass, each anchored to a file:line. They have NOT been
individually re-verified by running the code; treat the correctness items as high-confidence leads to
confirm with a test, not as proven defects.

---

## CoreAIHub — correctness

1. **Sub-tab lifecycle stops firing after the first switch.** `HubSubTabView.Select` builds content
   only on a cache miss, but `HubSubTabPage.BuildChild` owns the whole `OnActivated`/`OnDeactivated`
   handoff — so on revisiting a tab (`Mods → Logs → Mods`) the visible page never gets `OnActivated`
   and the hidden one never gets `OnDeactivated`, permanently desyncing `_activeChild`.
   `HubSubTabPage.cs:58-72`, `HubSubTabView.cs:101-105`. No test exists for this class at all.
2. **Remove-confirm can be silently disarmed → one-click endpoint deletion.** `RebuildEndpointList`
   recreates rows without consulting `_removeConfirmEndpointId`, so a routing-change refresh leaves a
   button labelled "Remove" while the pending id is still armed; the next single click deletes with no
   confirmation. `HubSettingsPage.cs:611-646, 811-832`.
3. **Lossy backend-mode round-trip downgrades the mode.** `ModeToOption` folds `ClientOwnedApi`,
   `ClientLimited` and `ServerManagedApi` onto one "HTTP API" label; `OptionToMode` maps it back to
   `ClientOwnedApi` only. Editing any unrelated field and clicking Apply silently rewrites the
   execution mode. `HubSettingsPage.cs:1511-1514, 1529-1531`.
4. **Full-bleed padding lost after any UI rebuild.** `ApplyFullBleed` runs only from `ActivatePage`,
   while `Rebuild` clones a fresh content element. `CoreAiHubWindow.cs:502-507` vs `:616, 732-753`.
5. **NRE on a null world-state manager** — `WorldStateHubPage.cs:49` dereferences `_manager` unguarded
   while every other member treats it as nullable; the exception is swallowed upstream and the World
   tab renders permanently empty.
6. **Six `async void` handlers with no failure path**, none taking a `CancellationToken`
   (`HubSettingsPage.cs:735, 811, 1184, 1213, 1248, 1283`). Status text freezes on "Saving…" and the
   exception escapes to the sync context.
7. **Error-log spam every frame** when the shell UXML lacks `coreai-hub-root` — that path is not
   covered by the `_missingShellWarned` latch, and it re-clones the tree each frame.
   `CoreAiHubWindow.cs:213-218`.
8. **Re-registering the active page id kicks the user to tab 0** — `CoreAiHubWindow.cs:446-456`.

## CoreAI (core) — correctness

1. **Deadlock: cancellation-registration disposed under the pump lock.** `TryPumpLocked` runs while
   holding `_lock` and calls `w.PendingCancellation.Dispose()`, which blocks until the concurrently
   running `CancelPending` callback returns — and that callback takes `_lock`. Two threads wedge and
   the orchestrator queue freezes permanently. `QueuedAiOrchestrator.cs:188, 230` vs `:535, 550`.
2. **`OperationCanceledException` swallowed and retried as a provider fault.** The guard is
   `when (cancellationToken.IsCancellationRequested)`, so an OCE from a *different* token (the timeout
   decorator's linked CTS, the per-read idle timer) falls through to the generic handler, becomes a
   retryable `ProviderError` and re-opens the stream against a backend that just timed out.
   `RetryingStreamingLlmClientDecorator.cs:119-126`; same shape at `AiOrchestrator.cs:555/570, 870/882`.
   Directly violates ARCHITECTURE_RULES §3.
3. **`CircuitBreakerLlmClientDecorator` silently drops routing capability.** `ILlmClient` declares
   `SupportsNativeToolCallingForRole` / `ResolveContextWindowTokensForRole` as *default interface
   members*; the breaker overrides neither, so it compiles but returns the default `null` and discards
   the routing profile. Every sibling decorator delegates both. `:68-78`.
4. **Half-open probe slot leaks → breaker permanently open.** The enumerator is acquired outside the
   `try`, so a synchronous throw skips the `finally` that releases the probe. `:151-153, 205`.
5. **Bare `catch { }`** at `AiOrchestrator.cs:703-706`, `ToolExecutionPolicy.cs:1050-1053, 1491-1494` —
   a cancelled predecessor is indistinguishable from a faulted one, so an outer cancel does not
   short-circuit the remaining serialized tool calls.
6. **Unbounded static lock table** keyed by model-controlled ids and never pruned, and keyed by store
   *type* rather than instance so unrelated stores serialize against each other.
   `ISkillStore.cs:122-123`, `IAgentMemoryStore.cs:118`.
7. **Blocking `SemaphoreSlim.Wait()` on the main thread** in `FileConversationSummaryStore` (`:80, 141,
   196`) — stalls the render loop, and on WebGL (where `RunOffThread` runs inline) deadlocks outright.
8. **`CoreAISettings` lock guarantees a property it does not provide** — `ResetOverrides` takes the
   lock, ~30 readers do not, so a half-reset override set is observable. The comment claims otherwise.

## CoreAiUnity — correctness

**P0 (FIXED in this commit) — shipped default prompts silently shadowed the C# consts.**
`AgentPromptsInstaller.cs:24-25` chains `ResourcesAgentSystemPromptProvider` **before**
`BuiltInDefaultAgentSystemPromptProvider`, so a `.txt` under `Resources/AgentPrompts/System/` always
wins. The package shipped copies of seven built-in prompts there, which means every prompt edit made
in C# never reached a Unity host. Verified directly: `Programmer.txt` (2946 chars) had diverged from
`BuiltInAgentSystemPromptTexts.Programmer` (3158 chars) and was missing the anti-tool-spam rule
("answer plain questions … do NOT call read_skill"), the `report()/logic_*` globals list and the
`Forbidden: io, os, require, load, loadfile, dofile, debug` line. The other six differed only in line
wrapping. `PlayerChat.txt` was dead entirely — the role id became `PlainChat`.
Fixed by deleting the eight shadowing/dead assets (keeping `DeveloperSampleAgent.txt`, which has no
const and is that role's only prompt) and replacing the per-file test with
`NoBuiltInRolePromptIsShadowedByAShippedResourceCopy`, which fails if such a copy is ever
reintroduced. Root cause of the silence: two sibling tests asserted the const and the `.txt`
separately and never that they agree.

Remaining, NOT fixed:

1. **All three editor build guards fail open** — a committed provider API key produces
   `Debug.LogWarning(… "Building anyway.")` and ships in the player with CI green.
   `CoreAIResourcesApiKeyBuildGuard.cs:52`, `CoreAIProductionSettingsValidator.cs:31, 79`. The latter
   also validates `FindAssets(...)[0]`, i.e. an arbitrary asset when several exist.
2. **`Packages/manifest.json` can be corrupted into unopenable JSON** — `CoreAIDependencyInstaller.cs:165`
   inserts a trailing-comma entry after `dependencies {` with no empty-object check and no validation
   of the result before writing.
3. **Endpoint registry save is non-atomic** — write `.tmp` → `File.Delete` → `File.Move`
   (`LlmEndpointRegistryPersistence.cs:96`); a crash in that window loses every endpoint, profile and
   role assignment. Sibling stores use `File.Replace`.
4. **Cancelling the vision probe permanently marks a vision-capable model text-only** — a caller-token
   cancel lands in the generic handler and writes `VisionSupportMode.Off` into the settings asset.
   `VisionSelfProbe.cs:138-144`.
5. **WebGL camera turns hang forever** — `finally { await UniTask.SwitchToThreadPool(); }` on a
   platform with no thread pool. `CoreAiChatService.cs:678`, `CameraLlmTool.cs:82`.
6. **Activation/release race can boot a second llama.cpp host on a live port** —
   `LlmClientRegistry.cs:1141` reads `HostReleaseTask` under `_gate`, but it is assigned outside it.
7. **Parallel `list_components` returns another object's answer** — unsynchronized
   `LastListedComponents` on a singleton while `ToolExecutionPolicy` runs tool calls in parallel.
8. **Editor code writes into the consumer's project on import** — `CoreAIBuildMenu.cs:125-163`
   unconditionally creates settings assets; `CoreAISettingsAssetEditor.cs:570-584` regex-rewrites six
   `.asmdef` files by hard-coded `Assets/...` path, which under UPM resolves into the consumer's tree.
9. **StreamingAssets WebGL guard moves folders before writing its restore manifest** into `Library/`,
   which is routinely deleted. `CoreAIWebGlStreamingAssetsGuard.cs:85` vs `:102`.
10. **Three IMGUI `MonoBehaviour` overlays still instantiated in shipped demo scenes**
    (`CoreAiTokenBudgetOverlay`, `OrchestrationDashboard`, `AiDashboardPresenter`), though allowlisted
    in the ban ratchet — UITK replacements already exist.
11. **~18 test files have machine-mangled comments** (Cyrillic stripped, leaving `///  StubLlmClient
    JSON .`), plus 21 files with non-English comments and 215 `WHY:` against 3 `TODO:` in
    `Runtime/`+`Editor/`.
12. **`Assets/CoreAiUnity/CHANGELOG.md` stops at 6.0.0** while the package is 6.6.0.

### CoreAiUnity — test quality

- **Unbounded waits with no `[Timeout]`**: `SharedLlmUnity.cs:74` (`while (_initializing)`) — a
  cancelled initializer leaves the flag set forever and every later LLM test spins;
  `QwenDemoSafetyPlayModeTests.cs:28`; 600 s waits in `LlmUnityWarmup.cs:53`.
- **`.Result` on the Unity main thread inside `[UnityTest]`** with no fixture timeout —
  `GameConfigPlayModeTests.cs:62, 71, 97, 119`. Safe only while `GameConfigTool` stays synchronous.
- **`PlayModeTestAwait.cs:30`** — the 3-arg overload never cancels the underlying task on timeout, so
  it runs on into the next test.
- **Missing `Assert.Ignore` self-skip** (§5): TOCTOU port assertion in
  `LlmRuntimeRegistryPlayModeTests.cs:501-516`, fixed port 13333 in `PlayModeLlmUnityTestHarness.cs:40`,
  `Type.GetType(throwOnError: true)` in `QwenDemoSafetyPlayModeTests.cs:16`, hard scene loads in
  `FullAccessDemoScenePlayModeTests.cs` / `CoreAiChatPanelStopPlayModeTests.cs`.
- **`CoreAiDemoScenesSmokePlayModeTests.cs:49` mutates the committed shared `CoreAISettings` asset**,
  restoring it only in memory — an aborted run leaves the developer switched to Offline.
- **Weak assertions**: `CoreAiSseFetchWebGlBridgePlayModeTests.cs:80-90` asserts `DoesNotThrow` on a
  call the comment says defers via `setTimeout`; `AiOrchestratorBuiltInRolesPlayModeHarness.cs:69-79`
  `continue`s past every per-role assertion when a role emits nothing.
- **Four non-test EditorWindows live inside the `CoreAI.Tests` assembly** and compile into every run.

## Architecture violations (all three packages)

- **§2 / §4.1 — static service locators and `static Instance` singletons**, which §4.1 bans outright
  ("they allow one; we allow none"): `CoreAISettings.Instance` + ~30 mutable static overrides,
  `Log.Instance`, the `CoreAiBackend.*` static facade, `CoreAiRoutingUi.Controller`.
- **§1 / §4.5 — no layering.** `CoreAI.Core` is a single ~38k-line assembly holding domain
  (`BpeEncoder`, `ConversationHistoryPartition`), application (`AiOrchestrator`) and infrastructure
  (`HttpClientOpenAiTransport`, raw `File.ReadAllText`) together. `CoreAI.Hub.UI` likewise has no
  Domain split. §1/§4.5/§6 require a Domain layer *or a written justification in a per-feature README*;
  no feature README exists in either package.
- **§4.4 / §5 — no architecture-fitness test** for `CoreAI.Core`, `CoreAI.Hub.UI` or `CoreAiUnity`
  (both `CoreAIMcp` and `CoreAIMods` ship theirs). Nothing prevents a `UnityEngine` or `Lua` reference
  being added.
- **§2 / §4.1 — `CoreAi.cs:803` is a textbook static service locator**
  (`FindAnyObjectByType<CoreAILifetimeScope>` → `Container.Resolve`, cached in statics), and
  `CoreAILifetimeScope.Configure` is not declarative: it writes two process-wide singletons
  (`:148-151`), discovers components via `GetComponentInChildren` (`:246`) and mutates serialized state
  from a property getter (`:114-121`).
- **§3 — `System.Threading.Tasks.Task` throughout**; `UniTask` appears zero times, including in
  newly-added async seams (`IAsyncConversationContextManager`, `IAgentMemoryStore`,
  `FileConversationSummaryStore`). Several async methods take no `CancellationToken` at all.

## Convention violations

- **Clean:** no IMGUI anywhere in either package; no `Roblox` C# identifiers; the generated
  `Resources/AgentSkills/{RbxApi,LuaModding,FullLua}.txt` are byte-identical to their `.cs` consts
  (13760 / 9604 / 2000 chars) — no drift.
- **Change-narrative comments referencing internal issue ids — 24 sites**, several inside XML
  `<summary>` where they become public API docs (`QueuedAiOrchestrator.cs:20, 28, 335, 403, 446, 474,
  522, 590, 646, 703`, and others). CONTRIBUTING bans these; they document what a past commit did.
- **Corrupted comment blocks from a botched cleanup pass**: 69 lines in `MeaiOpenAiChatClient.cs` where
  `///` was mangled to `// /` (so the XML docs no longer register as docs), 21 summaries in
  `CoreAISettings.cs` ending `///.`, and several sentences truncated into meaninglessness
  (`QueuedAiOrchestrator.cs:824-825`, `SmartToolCallingChatClient.cs:153, 171-173`).
- **Step-numbering narration** `ToolExecutionPolicy.cs:947, 1010, 1072, 1089`; **banner separators**
  `CoreAiHubWindow.cs:275, 406, 460, 575, 770`; **`WHY:` that only restates the code**
  (`CoreAiHubWindow.cs:88, 476, 499, 548, 555`) — §6 says delete these.
- **641 unprefixed `//` comment lines across 58 files** in `CoreAI`; many carry real rationale and want
  re-tagging as `// WHY:`, the rest want deleting.

## Dead code

- **`CircuitBreakerLlmClientDecorator` (383 lines) is never wired** — `LlmPipelineInstaller` composes
  Retrying/ClientLimited/Timeout but not the breaker. It is kept alive solely by its own unit test, and
  it carries two of the bugs above. Wire it or delete it.
- **~120 lines of duplicated stream-accumulation logic** between `AiOrchestrator.RunStreamingAsync` and
  `CompleteForTaskAsync` — already divergent in whether they rethrow OCE, so every fix must be applied
  twice.
- Duplicate subsumed catch clause `AiOrchestrator.cs:358-369`; unreachable branch
  `RetryingStreamingLlmClientDecorator.cs:208-211`; `ResolveSelectedEndpointId` (Hub) has zero call
  sites; `MakeGgufModelDropdown`/`MakeEndpointGgufDropdown` are byte-identical twins.
- `HubSettingsPage.SetPlaceholder` installs a 200 ms polling timer **per text field** (~15 always-on
  timers) to compensate for `SetValueWithoutNotify` not raising events.

## Highest-value missing tests

- **`BpeEncoder` (403 lines) — zero tests.** A wrong merge silently mis-counts tokens, which silently
  mis-sizes every context budget.
- **`ConversationHistoryPartition` (576 lines) — zero tests.** Its own comments describe two ways it can
  "refold the whole prefix every turn".
- **`SkillSetToolResolver` (358 lines) — zero tests**, and it is the surface the model drives directly.
- **A decorator-completeness fitness test.** One reflection test asserting every `ILlmClient` decorator
  overrides every virtual interface member would have caught the circuit-breaker gap and prevents the
  next one — this is the single highest-leverage test in the list.
- Hub: `HubSubTabView`/`HubSubTabPage` (no tests at all), the mode/vision round-trips, and the
  remove-confirm state machine (which needs extracting to a pure helper to be testable).
