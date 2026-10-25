# Unity host integration repair

Date: 2026-10-25. Worker: native `gpt-6.1-sol`; reasoning effort inherited from root. Root owns Git, builds, tests, Unity/MCP, metadata and independent acceptance.

## Goal and invariants

Repair the actual saved Unity EditMode failures in the leased host/provider/memory cluster. Explicit host resolvers remain authoritative; automatic scene-scope caching follows live scopes without retaining retired services. Destroy Unity objects only on the main thread, preserve per-test resource ownership, exact opaque memory identities, legacy bytes, audit disposal guarantees and disposal fences. English prose, C# 9 and explicit types apply. No example/demo production edits, assertion weakening, new source files or foreign-owner runtime edits are authorized.

Before implementation: correctness, lifetime ordering, cancellation, failure observability and isolated cleanup apply. No performance, player, WebGL, device or live-provider acceptance can be inferred from these source changes. Existing seams and fixtures cover the needed repairs; no replacement subsystem is justified.

## Intake and exact file plan

- Read `AGENTS.md`, normative architecture rules, workdocs 02/03/04/08/13 and the root-saved full Unity failures/XML/log. The receipt reports 7,775 passed, 103 failed and 10 skipped overall; this cluster is only a subset.
- Production lease: `Assets/CoreAiUnity/Runtime/Source/Api/CoreAi.cs`, `Runtime/Source/Composition/CoreServicesInstaller.cs` and `Assets/CoreAIHub/Runtime/HubSettingsPage.cs` only when the saved failure confirms a related defect.
- Fixture lease under `Assets/CoreAiUnity/Tests/EditMode/`: `CoreAiAttachmentFacadeEditModeTests.cs`, `CoreAiFacadeEditModeTests.cs`, `CoreServicesInstallerEditModeTests.cs`, `FileAgentMemoryStoreEditModeTests.cs`, `ScopedAgentMemoryStoreDecoratorEditModeTests.cs`, `HubSettingsRegressionEditModeTests.cs`, `UnityHostRegressionTests.cs`, `ProviderContractRegressionTests.cs`, `CoreAiLuaWorldModuleEditModeTests.cs`.
- `LlmClientRegistry.cs`, `CoreAiChatService.cs`, async deadlock/WebGL guards and other workers' files are read-only. No worker build/test/Unity/Git command is authorized.

## Diagnoses to revalidate

1. Explicit `SetResolver` is erased by automatic lifetime-scope cache invalidation.
2. Provider fixtures intentionally detach `SynchronizationContext` to avoid NUnit deadlock, then perform native destruction from resumed pool continuations. Move only native cleanup to fixture teardown.
3. One routing event assertion may need deterministic main-thread publication delivery, without changing the event contract or guards.
4. File-memory fixtures contain obsolete `Role.json`/`scope-v1` disk assumptions; padded and unpadded logical identities must remain distinct.
5. Additive-scene regression cannot create a regular additive scene while an untitled scene is unsaved; use isolated preview scenes with deterministic teardown where suitable.
6. Settings notifications queued before disposal can repopulate a destroyed view.
7. Audit pump cleanup may be logically detached before its next tick; verify the actual disposal guarantee and keep meaningful observable assertions.
8. FullAccess demo opening emits a third-party constructor exception; diagnose fixture isolation without changing examples or concealing unrelated framework errors.

## Source checkpoint

Before accepting: read the actual changed source, saved failure stack lines and XML case details; distinguish stale expectations from production defects; retain every original failure, data-preservation and actor-isolation assertion. No guard file, demo, third-party package, Unity asset or metadata was edited.

| Failure | Revalidated cause and source repair | Acceptance retained or added |
| --- | --- | --- |
| Facade attachment and explicit resolver cases | `RequireChatService`/`RequireOrchestrator` passed static cache fields as `out` arguments. `TryResolve` then cleared those same fields before finishing, aliasing the result and destroying the injected service. Explicit resolution now returns before automatic scope invalidation; public getters use local outputs. `SetResolver` retires previously automatic cache entries. | Existing attachment list identity, null fallback and actor composition assertions remain. Repeated public resolution and an explicit resolver overriding a real already-resolved scene scope are covered. `Invalidate` restores automatic scope lookup. |
| Provider native teardown | Detached fixture context is required by synchronous NUnit async assertions. Actual pool continuations then called `DestroyImmediate`. | Register each settings object before any await; native destruction runs in fixture `TearDown` after context restoration. Registry disposal and gate/iterator cleanup remain inside each test. No detach or assertion was removed. |
| Routing early stream abandonment | The source publishes directly from its iterator `finally`; no UI dispatch or main-thread publication pump exists here. | The fixture now explicitly awaits iterator disposal twice before asserting exactly one failed completion/usage/health record. It also asserts no premature completion immediately after the first yielded failure. A fresh root result must determine whether further runtime repair is needed. |
| File-memory migration load failures | The fixture paths were already canonical. `JsonUtility` rewrote absent legacy `roleId` as an empty string; the strict identity check then rejected the file. Root granted the existing file-store production lease. | Parse raw property presence separately from an explicit empty/wrong/null tag; validate before migration/clear effects; stamp the proven requested identity before each legacy rewrite. Existing memory, history, transcript, version and restart assertions remain. Real legacy-file migration now verifies the exact `.legacy` backup bytes. Six new canonical/legacy cases preserve bytes and refuse explicit invalid identity before mutation. |
| Opaque scope expectations | Logical scope keys remain `scope-v1`; canonical disk stems are `key-v2`. Padded and unpadded scopes are exact distinct host identities. | Update only the stale file regex and equality assumption; preserve distinct-user round trips and opaque-name assertions. |
| Audit installer disposal | `_cts` is now a readonly field holding a disposed source; nulling it is not the disposal contract. | Require intercepted subscriptions to retire, the captured token to be cancelled, the source to reject later token access, the diagnostic pump to detach immediately and its next `MoveNext` to return false. No installer production change was needed. |
| Settings queued callback | Saved failure is the earlier `first.Subscribers` assertion: three earlier global-controller fixture pages were never destroyed. Existing production code already fences both queued callbacks and destroyed subscriptions. | Every created page is now fixture-owned and receives `OnDestroyed` in teardown. Require one initial subscription and zero subscriptions both immediately after disposal and after running every queued callback. No Hub production change was needed. |
| Additive-scene setup | `NewScene(Additive)` refuses an unsaved untitled current scene. Preview scenes could exclude objects from the actual lookup under test. | Root authorized only unique temporary `Assets/__CoreAIHostRegression_<guid>/A.unity` and `B.unity` generated during root test execution. The fixture imports and opens empty saved scenes, creates scopes in their active scene, restores the original active scene, closes only owned scenes and validates its exact owned folder before deletion. No existing scene is saved or discarded. |
| FullAccess scene opening | `IngameDebugConsole.CommandInputField` field initializer dereferences a missing TMP private field while loading the existing demo. This is outside the framework lease. | Left visible; no exception suppression, demo/plugin mutation or removed case. Root must classify or assign the third-party compatibility issue separately. |

## Verification state and root handoff

No worker build, test, Unity/MCP, Git mutation or package installation was run. Read-only source inspection and static checks do not establish a passing-after result. Root's saved `unity-full-editmode.xml` confirms the exact failures above; current edits postdate that receipt.

The saved cluster has 33 failed cases: attachment facade 3, facade 3, Lua-world demo 1, core installer 1, file memory 2, settings 1, providers 19, scoped memory 2 and host 1. These are actual failures in the old receipt, not passing-after counts.

Scoped `git diff --check` returned no diagnostics for tracked leased changes. The two untracked fixtures and this report have zero trailing-whitespace lines. The touched provider/host/settings fixtures contain no implicit `var` declarations or Cyrillic prose. Existing async-deadlock and WebGL guards were read but not changed.

Root focused Unity EditMode filters:

```text
CoreAI.Tests.EditMode.CoreAiAttachmentFacadeEditModeTests
CoreAI.Tests.EditMode.CoreAiFacadeEditModeTests
CoreAI.Tests.EditMode.CoreServicesInstallerEditModeTests
CoreAI.Tests.EditMode.FileAgentMemoryStoreEditModeTests
CoreAI.Tests.EditMode.ScopedAgentMemoryStoreDecoratorEditModeTests
CoreAI.Tests.EditMode.HubSettingsRegressionEditModeTests
CoreAI.Tests.EditMode.UnityHostRegressionTests
CoreAI.Tests.EditMode.ProviderContractRegressionTests
CoreAI.Tests.EditMode.CoreAiLuaWorldModuleEditModeTests
CoreAI.Tests.EditMode.SynchronousAsyncAssertionDeadlockGuardEditModeTests
CoreAI.Tests.EditMode.WebGlUnsafeAsyncPrimitivesEditModeTests
```

Require positive case presence and raw XML outcome accounting. All named production/runtime checks, full Unity suite, independent review, player/WebGL/browser/device/performance/live-provider and publication gates remain with root. In particular, explicit routing disposal and the temporary saved-scene import need fresh Unity execution; no source-only acceptance is claimed.

## First source checkpoint inventory

At this earlier checkpoint, only the following nine source/fixture paths and this document had been written by this worker. Granted but unchanged: `CoreServicesInstaller.cs`, `HubSettingsPage.cs`, attachment facade and Lua-world fixtures. The later follow-up below supersedes hashes for files that it names. No temporary scene asset existed before root executed the fixture.

```text
990f25076c844a852cab1b4bb9e49ce529b2970f487d84a92eb8ee2ad98a4ee5  Assets/CoreAiUnity/Runtime/Source/Api/CoreAi.cs
d830b393d4f5ceb1f4f316ed1b3a996492300b49671e9b1ee6cd61688b66b6e1  Assets/CoreAiUnity/Runtime/Source/Features/AgentMemory/Infrastructure/FileAgentMemoryStore.cs
79bc235f5d7484ef9b847c1762bcc851e025de32b05fb1c97f8c5243a4637311  Assets/CoreAiUnity/Tests/EditMode/CoreAiFacadeEditModeTests.cs
f3e69ba85824df68fe366eba7ca5e2a97c5d1c230ab4b3f8e29ecf8d3aeb38f8  Assets/CoreAiUnity/Tests/EditMode/CoreServicesInstallerEditModeTests.cs
bbc6828063fbd1c132e92d5eb6ad88241c81fac2b8d0abbea97cbe81cc3d6162  Assets/CoreAiUnity/Tests/EditMode/FileAgentMemoryStoreEditModeTests.cs
213963dec87f2f5c24e7e7e2c568d520610df2a3d4d98ebbd2e8943e0712c525  Assets/CoreAiUnity/Tests/EditMode/HubSettingsRegressionEditModeTests.cs
9199f52c732145cf671bf4923410a9c2314ec7db57f2df67e8902dc7261590bb  Assets/CoreAiUnity/Tests/EditMode/ScopedAgentMemoryStoreDecoratorEditModeTests.cs
f17a3d6920fce62f2a6b0fe2f1c52a6ee73d5a6a574ae393c743652b89b39596  Assets/CoreAiUnity/Tests/EditMode/ProviderContractRegressionTests.cs
43b5298e5ec0fc8a457bae79a9969d63167edfda15dfd9b9802ee453da6f06ea  Assets/CoreAiUnity/Tests/EditMode/UnityHostRegressionTests.cs
```

## Fresh Unity receipt and fixture follow-up

Root supplied `artifacts/fix-wave-20261025/unity-editmode-final3.xml` and `.log`, written at 2026-10-25 11:27:56 UTC and 11:28:05 UTC respectively. This worker read the XML directly: **7,873 passed, 28 failed, 10 skipped, 7,911 total**. This is the complete repository run, not a count for this worker's cluster. Applicable case accounting:

| Fixture | Passed | Failed | Skipped |
| --- | ---: | ---: | ---: |
| CoreAiAttachmentFacadeEditModeTests | 5 | 0 | 0 |
| CoreServicesInstallerEditModeTests | 3 | 0 | 0 |
| FileAgentMemoryStoreEditModeTests | 57 | 0 | 0 |
| ScopedAgentMemoryStoreDecoratorEditModeTests | 22 | 0 | 0 |
| UnityHostRegressionTests | 18 | 0 | 0 |
| ProviderContractRegressionTests | 36 | 1 | 0 |

The four remaining failed cases assigned to this cluster are the first facade actor-production composition, the settings queued-controller fixture, routing early disposal and the FullAccess demo. Fresh source changes below postdate that receipt and require another root run.

Before accepting: verify actual saved stack lines rather than a summarized diagnosis. The routing failure is at `ProviderContractRegressionTests.cs:250`, **usage count zero**; the earlier completion-count, failed-completion and original error-code assertions already passed. The iterator's disposal/finally publication works. The test abandons before consuming the later usage frame; production deliberately refuses to estimate usage for failed requests. Root approved preserving that contract and correcting the fake's measured input rather than rewriting the iterator or inventing token charges.

- The existing sticky-failure fake now reports measured `TotalTokens = 9` on its failure frame, retains its later usage frame and all exactly-once failed publication/health assertions, and asserts the exact measured total. Two added drain/early-disposal controls carry no measured usage and require one failed completion/health record with zero invented usage events. `RoutingLlmClient.cs` was additionally leased but remains unchanged because no production defect was confirmed by the fresh stack.
- The facade's pool-thread `DestroyImmediate` at line 303 masked a second native setup after its first await. Both settings assets are now fixture-owned, both production containers are built and both orchestrators resolved on the main thread before awaiting. The second actor still runs later against the same long-lived memory decorator/backing store, preserving the anti-aliasing assertion. Native destruction occurs only in main-thread fixture teardown; original context detachment, cancellation and memory assertions remain.
- The settings strict initial subscription check still sees three foreign pages before building its own view. Root extended the fixture lease to `HubSettingsStatisticsPageEditModeTests.cs`, `LlmConfigurationDefectsEditModeTests.cs` and `CoreAiRoutingUiEditModeTests.cs`. All 13 `HubSettingsPage` creation sites across those owners are now tracked and destroyed in teardown even if a test assertion fails. Their controllers and settings reset only after owned pages retire. The strict initial-one/final-zero assertions remain unchanged; no external baseline was accepted or reset globally.
- FullAccess remains outside the framework repair scope: `Assets/Plugins/IngameDebugConsole/Scripts/CommandInputField.cs:60` dereferences `typeof(TMP_InputField).GetField("m_IsCompositionActive", ...)` while initializing `m_IsCompositionActiveGetter`. The fresh XML's `NullReferenceException` originates in this third-party constructor during `CoreAiLuaWorldModuleEditModeTests.cs:131` scene opening. No exception suppression, removed test, demo change or plugin edit was made.

Source read-back confirms both containers resolve before the facade's first await, all newly leased settings-page construction sites are owned, and unchanged routing source intentionally skips failed estimates. Scoped tracked-file `git diff --check` exits 0. All five follow-up fixture files have zero trailing-whitespace lines; the new fixture changes contain no implicit `var` declarations or Cyrillic prose. No worker build, test, Unity/MCP or Git mutation occurred.

Additional root filters:

```text
CoreAI.Tests.HubSettingsStatisticsPageEditModeTests
CoreAI.Tests.CoreAiRoutingUiEditModeTests
CoreAI.Tests.EditMode.LlmConfigurationDefectsEditModeTests
```

Latest hashes for the five follow-up fixtures (the first inventory's other seven hashes remain unchanged):

```text
160b4fe244dc2df2830dc79f0793dcf57220b4173f3a6b543ceb1f930de2f69d  Assets/CoreAiUnity/Tests/EditMode/CoreAiFacadeEditModeTests.cs
fad40c23690dc698b2756e80fef79a260caaf478544266ce124a9072d81e771a  Assets/CoreAiUnity/Tests/EditMode/ProviderContractRegressionTests.cs
b454161034ee7e9ca97e9c634ca79b99b941d380fc4aca7cc012a484279a5cc4  Assets/CoreAiUnity/Tests/EditMode/HubSettingsStatisticsPageEditModeTests.cs
7eecb0050c5184f2b6cada0f767e0400549de1b5425ea189cd8d9028c895d842  Assets/CoreAiUnity/Tests/EditMode/LlmConfigurationDefectsEditModeTests.cs
972f68b0052bc52ffefb902372b503a2485680851a9759f0d02b523b731c4afd  Assets/CoreAiUnity/Tests/EditMode/CoreAiRoutingUiEditModeTests.cs
```

## Root Windows acceptance - 2026-10-25

`artifacts/fix-wave-20261025/unity-editmode-final5.xml` records 7,917 Passed, zero Failed and 13 Skipped. This includes all thirteen facade and thirty-nine provider regression cases. Root corrected the old facade expectation: distinct logical cancellation groups remain independent; the host explicitly calls `CoreAi.StopAgent(RoleId, firstRequest.ActorContext)` before admitting the replacement request. All original cancellation, success and durable actor-partition assertions remain. This four-line fixture correction supersedes its earlier hash above.

The destroyed chat-panel subscription diagnosis and fixture-owned cleanup are recorded in `17-hub-final-integration.md`; its accepted related fixtures passed 81/81. The exact external FullAccess demo exclusion and unrun player/browser/network gates remain visible in the release ledger. No vendor exception suppression was added.
