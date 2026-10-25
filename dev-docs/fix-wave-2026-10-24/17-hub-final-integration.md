# Hub final integration ownership investigation

Date: 2026-10-25. Model: `gpt-6.1-sol`; reasoning effort: `max`.
Checkout: `D:/Git/CoreAI`. Status: accepted by root final5 Windows EditMode; C# remains frozen.

## Goal and write lease

Identify and remove the actual leaked routing subscription owner while preserving the strict
`1 -> 0` subscriber lifecycle assertions and queued-controller-event destruction protection.
No arbitrary subscriber baseline, global delegate reset or production login/account policy change.

Leased fixtures under `Assets/CoreAiUnity/Tests/EditMode/`:
`HubSettingsRegressionEditModeTests.cs`, `HubSettingsStatisticsPageEditModeTests.cs`,
`LlmConfigurationDefectsEditModeTests.cs`, `CoreAiRoutingUiEditModeTests.cs`.
`Assets/CoreAIHub/Runtime/HubSettingsPage.cs` may change only for a concrete framework defect.
This receipt is the only other write. Adjacent routing/chat controls and factories are read-only;
request an exact lease before changing them. No builds, tests, Unity/MCP, Git mutation or assets.

## Established evidence

- Root receipt `artifacts/fix-wave-20261025/unity-editmode-final4.xml`: 7,911 total,
  7,896 passed, two failed, 13 skipped. The Hub failure is
  `EndpointControllerChange_QueuedBeforeDestructionCannotReattachTheView` at line 462:
  the new fake controller has four subscribers where exactly one page should own one.
- This is after the earlier owner added cleanup for the known direct page constructors.
  Do not repeat the unproven claim that all three additional owners are HubSettingsPages.
- A source-wide search finds exactly two implementations attaching to global
  `CoreAiRoutingUi.ControllerChanged`: `HubSettingsPage.HandleRoutingControllerChanged` and
  `CoreAiChatPanel.HandleRoutingControllerChanged`. The scope attachment/factory composes or
  replaces the controller but does not itself attach a global-event listener.
- The page captures its UI context during BuildContent. `DispatchToUi` ignores a missing
  captured context and otherwise executes/posts under it; page destruction removes the global
  listener, detaches the controller and fences queued callbacks. No new page defect is proven.
- Exactly three EditMode calls to `EnableApiSwitching` occur, all in the leased
  `CoreAiRoutingUiEditModeTests`: `ChatApiSelector_IsHiddenUntilExpanded_AndUsesActiveAgentProfile`,
  `ChatApiSelector_AutomaticClearsPersistedAgentOverride`, and
  `ChatApiSelector_ZeroProfilesShowsAutomaticReadableState`.
- Each constructs a plain `CoreAiChatPanel` in EditMode, invokes API switching directly, and
  only calls `DestroyImmediate` on its GameObject. The component is not ExecuteAlways;
  routing event release lives in its protected OnDestroy, while EnableApiSwitching subscribes
  immediately even before a runtime lifecycle. Ordinary EditMode object destruction is therefore
  not sufficient evidence that these manually activated subscriptions were released.
- The same repository already models this lifecycle explicitly:
  `CoreAiChatPanelResolveTimeoutMessageEditModeTests.PanelProbe.SimulateDestroy` calls OnDestroy
  because EditMode does not dispatch those runtime callbacks. Final4 XML places all three API
  selector tests at 11:48:54Z, before the Hub consumer fails at 11:50:02Z. This supports the
  candidate ownership path but still does not identify the retained delegate targets directly.

## Design and diagnostic checkpoint

- Real goal: make fixture ownership truthful and retain a regression that detects a future real
  page resurrection or foreign-owner leak. Runtime owner identities were required before a fix.
- Invariants: exact subscriber counts, queued destruction fencing, captured UI context, no event
  reset, no masking by baselines, all fixture resources cleaned on assertion failure, no changes
  to unrelated production lifetimes.
- Initial source candidate: three detached chat panels retained by the global routing event.
  The following controlled run established the exact target inventory before implementation.
- Proposed diagnostic after root releases the Unity/C# freeze: add the fake controller's actual
  invocation target types, methods and Unity destroyed status to the existing strict assertion
  message. Preserve the expected count of one. Run the routing fixture before the Hub regression
  in the same test process to establish the exact three owner identities.
- Root explicitly approved that diagnostic and requires it as the next checkpoint before any
  chat lifecycle fix. During the active PlayMode run, only source reads and this receipt changed;
  no C# edit or shared Editor action occurred.
- After the root released the write window, this worker added `DescribeSubscribers` to the
  regression fixture's fake controller and included it in the unchanged expected-one assertion.
  It lists each actual method declaring type/name, target type and, for Unity objects, destroyed
  status using only the safe Unity equality check. No owner lifecycle or production source was
  changed. The root was notified immediately that C# is frozen again for its controlled run.

## Runtime target proof and correction

Root receipt `artifacts/fix-wave-20261025/unity-adversarial-red-final.xml` records **49 cases:
30 passed, 19 failed, zero skipped**. This was a controlled ADV negative run, not an acceptance
run; its separate source/exit receipts are `adversarial-red-final-source.json` and
`unity-adversarial-red-final-exit.json`. The latter records `Restored: true`. This worker also
read back the two controlled production files and verified both current hashes match their
recorded GreenSha256 values after restoration.

The unchanged expected-one Hub assertion now reports the actual four invocation targets:

| Actual method and target | Entries | State |
| --- | ---: | --- |
| `CoreAI.Chat.CoreAiChatPanel.RefreshApiProfileControls`, target `CoreAI.Chat.CoreAiChatPanel` | 3 | `UnityDestroyed=True` for all three |
| `CoreAI.Hub.UI.HubSettingsPage.HandleRoutingChanged`, target `CoreAI.Hub.UI.HubSettingsPage` | 1 | The one current page |

These are the three manually activated API-selector fixture panels, not three live foreign
Hub pages. The static global controller event retained the destroyed managed panel wrappers;
setting a new controller made each stale wrapper subscribe its refresh callback again.
No production page or chat defect is established by this fixture-only lifecycle omission.

After root approval, only `CoreAiRoutingUiEditModeTests.cs` changed for cleanup:

- Each of the three existing chat-selector tests keeps every original UI/routing assertion and
  calls `DestroyRoutingPanel` from its existing finally block.
- The helper invokes the actual protected `CoreAiChatPanel.OnDestroy` through an asserted
  MethodInfo before native GameObject destruction. This mirrors the runtime lifecycle that
  EditMode does not dispatch for these manually enabled panels.
- The fake controller checks delegate target identity for that exact fixture-owned panel after
  lifecycle cleanup. Other owners do not affect the assertion; no subscriber baseline or global
  event reset is used. The native DestroyImmediate remains in an inner finally, so it still runs
  when lookup, lifecycle or ownership assertions fail.
- The Hub diagnostic and all strict expected-one/expected-zero assertions remain unchanged.
  No production file, other fixture, assets, metadata, dependency or Git state was changed by
  this repair. C# was frozen and the root notified as soon as the write checkpoint completed.

## Source hashes and worker checks

All SHA-256 values are from current source readback after the correction. Production rows are
read-only proof; this worker did not edit them.

| File | SHA-256 |
| --- | --- |
| `Assets/CoreAiUnity/Tests/EditMode/CoreAiRoutingUiEditModeTests.cs` | `228f82c1c5d06048bda81c2e0cb1c00eafc10078104090945f9d6876ac2a81f8` |
| `Assets/CoreAiUnity/Tests/EditMode/HubSettingsRegressionEditModeTests.cs` | `aff921cd3c6b6265c4db6e505c28beb3ffed47ed9da0789e8f197146a75b64c2` |
| `Assets/CoreAIHub/Runtime/HubSettingsPage.cs` | `4d9ee39d2aac11a89da46157c5ad67e80418f6d4d51f7933d5f4e61cb5e8b135` |
| `Assets/CoreAiUnity/Runtime/Source/Features/Chat/CoreAiChatPanel.cs` | `f7cd4dd2e1502cc1d8595c87fe94011420c759d20207f34db06e2e8c2b9fe20c` |
| `Assets/CoreAiUnity/Runtime/Source/Features/Chat/CoreAiChatService.cs` | `b148d1218bda84768a72d41e94045827245bd10c5a4a5ec361f9f3ee89c1f386` |
| `Assets/CoreAiUnity/Runtime/Source/Features/Llm/Infrastructure/LlmClientRegistry.cs` | `fc5877a9a5faecf302f4c4d4da6278cf45121e3d8a8ad4e49838ec48fa487a63` |

Worker verification: actual leased diff read back; `git diff --check` exit 0; direct English,
trailing-whitespace and added-C# explicit-type checks clean. No build, test, Unity/MCP, install
or Git mutation was run by this worker. Root positive execution is recorded below.

## Accepted root-owned Windows EditMode receipt

Direct readback of `artifacts/fix-wave-20261025/unity-editmode-final5.xml` confirms
**7,930 total: 7,917 passed, zero failed, 13 skipped**. The companion process receipt selects
`StandaloneWindows64` and EditMode; `unity-editmode-final5-exit.json` records **exit code 0**.
The raw top-level NUnit label is `Skipped:Ignored` because of the retained skips.

All four required fixture classes are positively present, with no failures or skips:

| Fixture | Passed / total |
| --- | ---: |
| `CoreAI.Tests.CoreAiRoutingUiEditModeTests` | 9 / 9 |
| `CoreAI.Tests.EditMode.HubSettingsRegressionEditModeTests` | 18 / 18 |
| `CoreAI.Tests.HubSettingsStatisticsPageEditModeTests` | 7 / 7 |
| `CoreAI.Tests.EditMode.LlmConfigurationDefectsEditModeTests` | 47 / 47 |
| Total | 81 / 81 |

All three chat-selector owner cases passed, and
`EndpointControllerChange_QueuedBeforeDestructionCannotReattachTheView` passed with its
unchanged strict subscriber assertions and concrete diagnostic still present. The same receipt
also contains both passing `StopAgent_UsesActorCancellationAcrossFacadeAndInjectedChatService`
variants and the passing facade session-cancellation regression; those are root-owned adjacent
acceptance, not changes made by this worker. Mirror is 229 / 229, including NET001 17 / 17.

## Final adversarial diff readback and remaining gates

Read both complete current fixture diffs again after the positive receipt:

- Cleanup owns only the three panels created by its tests. The real protected OnDestroy runs
  before native destruction, its missing method or exception fails visibly, and the inner finally
  still destroys the native GameObject. The local delegate check compares the exact managed
  target, so it does not mask a leak with arbitrary baselines or interfere with other page owners.
- Owned pages remain registered for fixture teardown even if an assertion or late completion
  fails. Explicit destruction plus fixture teardown is safe through the page's existing
  idempotent lifecycle. SynchronizationContext restoration and the queued-destruction fence
  remain exercised by the strict regression.
- The fake controller retains real multicast event behavior. The diagnostic only reads invocation
  targets; it never clears global events or changes subscriber counts. Every original UI assertion
  remains, with no added Ignore, Explicit, Inconclusive, failing-log suppression or global-event
  reset in either fixture diff. The production page/panel hashes still match the table above;
  no production behavior was disabled to obtain this result. No actionable defect was found in
  this bounded reread.

The root runner retains two separate environmental exclusion patterns:
`!BenchmarkLegacyReportEvidenceEditModeTests.RealReports_` and
`!CoreAiLuaWorldModuleEditModeTests.MigratedDemo_.*FullAccessDemo`.
Neither excludes any of these 81 fixture cases. Earlier final4 and controlled-red failures are
retained above; the accepted scope is the executed Windows EditMode suite with its recorded
13 skips and those two exclusions, not an unfiltered full-platform acceptance.

Root Windows fake-only PlayMode is still pending. This receipt makes no PlayMode, built-player,
device, real-network or publication claim. This final update changes only MD16 and MD17;
the worker ran no build, test, Unity/MCP or Git mutation and kept C# frozen.
