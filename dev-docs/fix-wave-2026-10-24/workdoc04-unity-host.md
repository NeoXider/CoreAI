# Unity host repair workdoc

Date: 2026-10-24. Owner: host worker, `gpt-6.1-sol`, reasoning effort `max`.
Baseline: `69d5bcc37502cd4bb464a3bf9ac0c53b1624b804`. Lead owns Git, Unity, builds and release.

## Goal and decisions

Repair confirmed host defects without introducing account or game policy. Preserve existing public APIs and default local-game behavior. Keep admitted actor/session and persistence-scope snapshots stable; use the actor-specific cancellation seam for user stops. Authentication, matchmaking, connection lifetimes, durable memory partition selection and presentation replacement remain host responsibilities.

The worker changes only the lead-granted whole-file lease. No Git/index, Unity/MCP, package, metadata, scene, prefab, assembly or shared aggregate document edits. Source inspection is evidence for the reproduction scenario, never a test pass.

## Dispositions and regression plan

| ID | Current disposition | Reproduction and regression contract | Chosen files |
| --- | --- | --- | --- |
| UH-01 | Withdrawn, uncounted host integration note | Document panel recreation/all-role view reset and host identity boundaries; no login manager | `Docs/CoreAI/MULTIPLAYER_SESSIONS.md`, chat only for admitted supplied-context fixes |
| UH-02 | Source repair and regression authored; lead execution pending | Round-trip reversed-row parent/child/grandchild hierarchy with rotation and nonuniform scale twice; preserve local scale and world pose | `WorldStateManager.cs`, existing world PlayMode fixture |
| UH-03 | Lead-approved API implemented; lead execution pending | Failed write/replace/delete cannot become positive confirmation; result remains per operation; legacy manager remains unconfirmed; throwing reset observers cannot hide the receipt | `IWorldStateManager.cs`, `WorldStateManager.cs`, new `WorldStateWriteResultTests.cs`; Hub owner consumes contract |
| UH-04 | Source repair and regression authored; lead execution pending | Missing spawn/change parent rejects before every effect under both parenting policies; descendant cycles reject; mixed batch preserves partial-success semantics | `CoreAiWorldCommandExecutor.cs`, new `UnityHostRegressionTests.cs` |
| UH-05 | Source repair and regression authored; lead execution pending | Reject nonfinite, fractional, negative and above-limit counts before component addition or mutation; tests use a host limit of 8 and safe sentinel 9, never a huge allocation | component catalog/executor/options, `WorldCommandsInstaller.cs`, existing catalog/installer fixtures |
| UH-06 | Assigned separately | Worker does not edit audit feature | Separate audit owner |
| UH-07 | Alias assigned separately | Worker does not edit memory file store | Memory owner |
| UH-08 | Extra lease granted; source repair and regressions authored; lead execution pending | Preserve conflicting backup/source bytes; persist intent atomically before moving; unique build generations; metadata-only and repeated recovery; target matching | `CoreAIWebGlStreamingAssetsGuard.cs` and existing fixture |
| CARRY-HOST-A1 | Source-confirmed carry-in; source repair authored | Stable GUID lookup replaces monorepo paths; current-project GUID fixture does not replace the clean consumer-UPM gate | two leased Editor UI files, host regression fixture |
| CARRY-HOST-A4 | Source-confirmed carry-in; source repair authored | Real additive test scenes, real lifetime scopes/containers/queues and deterministic inner transport; unload first scene then ask through the facade | `CoreAi.cs`, `CoreAiChatService.cs`, host regression fixture |
| CARRY-HOST-A13 | Source-confirmed carry-in; source repair authored | Invoke the actual subsystem hook for successive generations with retained subscribers/history; old callbacks disappear and fresh subscription receives once | `CoreAi.cs`, `CoreAiBackend.cs`, host regression fixture |
| CARRY-HOST-WORLD-SCOPE | Documented default/local-game limitation, not an automatic game bug | No existing supplied ownership or storage namespace contract was identified to repair. Default capture and path stay shared; independent worlds require a host replacement/omitted default block | guide |
| MEM supplied-context integration | Coordinated dependency integrated; lead execution pending | Resolve identity only; bind one frozen scope including empty at admission; scoped hydration/clear; current actor stop does not cancel another actor; host factory registration wins | leased chat/API/portable installer, new `MultiplayerSessionIntegrationTests.cs` |

## Reuse receipt

Existing actor-keyed chat service factory, `ActorContext`, optional memory-scope provider, queue cancellation extension, world-state interface and Unity transform APIs are the existing building blocks. This is a targeted repair and integration of those contracts; no new external dependency or replacement subsystem is justified.

## Verification

No builds or tests executed by this worker. The lead runs all commands serially after source freeze. Unity-specific fixtures require Unity EditMode/PlayMode. New tests and exact filters will be recorded with the final handoff. Built-player/WebGL/consumer-UPM/reload/fault/performance gates remain distinct and unverified until lead receipts exist.

## Milestones

- Intake: read current audit, repository instructions, architecture rules, repair plan and module claim map. Confirmed UH-08's actual file differs from the initial lease and requested the two required files before editing.
- Contract: lead approved `IWorldStateWriteResults` and immutable `WorldStateWriteResult`. `Status` is `Unsupported` (zero/default), `Succeeded`, `Failed` or `Cancelled`; `LocalOperationSucceeded`, `PersistenceArmed`, safe `Error` and `IsConfirmed` are read-only. `IsConfirmed` means local operation success plus automatic persistence arming, never IndexedDB completion. Void methods remain compatible.
- Repairs: implemented UH-02/03/04/05/08, admitted A1/A4/A13, scoped chat/API history/clear/cancellation, explicit provider injection into direct orchestration and preservation of host chat-factory replacement. Root also granted the world installer/fixture so registered component limits reach the actual default executor.
- Cancellation follow-up: source-confirmed `OperationCanceledException` was converted to failure in world/component wrappers and world batch dispatch. Those adapters now rethrow it; pre-cancel and executor-thrown cancellation regressions are authored.
- Source review: inspected real scoped diffs. Corrected nullable `ActorContext?` inputs after the lead's first in-progress Unity compiler receipt; the immutable context is a value struct. Corrected receipt observer isolation, actual installer options flow, stale cache validation and test cleanup. The native allocation regression uses only safe sentinel counts so a future regression cannot allocate an enormous buffer.

## Source inventory

New files:

- `Assets/CoreAiUnity/Runtime/Source/Features/World/WorldStateWriteResult.cs`
- `Assets/CoreAiUnity/Runtime/Source/Features/World/Infrastructure/CoreAiComponentCommandOptions.cs`
- `Assets/CoreAiUnity/Tests/EditMode/WorldStateWriteResultTests.cs`
- `Assets/CoreAiUnity/Tests/EditMode/UnityHostRegressionTests.cs`
- `Assets/CoreAiUnity/Tests/EditMode/MultiplayerSessionIntegrationTests.cs`
- `Docs/CoreAI/MULTIPLAYER_SESSIONS.md`
- This workdoc.

Existing source files changed: the two leased Editor UI files; `CoreAIWebGlStreamingAssetsGuard.cs`; `Api/CoreAi.cs`; `Api/CoreAiBackend.cs`; `Composition/CorePortableInstaller.cs`; additionally leased `Composition/WorldCommandsInstaller.cs`; chat service/panel/README; world manager/interface; world command executor; component catalog/executor; world/component tool wrappers.

Existing fixtures changed: `ComponentCatalogEditModeTests.cs`, `WorldCommandsInstallerEditModeTests.cs`, `CoreAIWebGlStreamingAssetsGuardEditModeTests.cs`, `CoreAiChatServiceEditModeTests.cs`, `CoreAiFacadeEditModeTests.cs`, `PlayMode/FastNoLlm/WorldStateManagerPlayModeTests.cs`.

Exact worker-authored paths, relative to `D:/Git/CoreAI`:

```text
Assets/CoreAiUnity/Editor/CoreAIChatDemoSceneCreator.cs
Assets/CoreAiUnity/Editor/CoreAISettingsAssetEditor.cs
Assets/CoreAiUnity/Editor/CoreAIWebGlStreamingAssetsGuard.cs
Assets/CoreAiUnity/Runtime/Source/Api/CoreAi.cs
Assets/CoreAiUnity/Runtime/Source/Api/CoreAiBackend.cs
Assets/CoreAiUnity/Runtime/Source/Composition/CorePortableInstaller.cs
Assets/CoreAiUnity/Runtime/Source/Composition/WorldCommandsInstaller.cs
Assets/CoreAiUnity/Runtime/Source/Features/Chat/CoreAiChatPanel.cs
Assets/CoreAiUnity/Runtime/Source/Features/Chat/CoreAiChatService.cs
Assets/CoreAiUnity/Runtime/Source/Features/Chat/README_CHAT.md
Assets/CoreAiUnity/Runtime/Source/Features/World/ComponentLlmTool.cs
Assets/CoreAiUnity/Runtime/Source/Features/World/IWorldStateManager.cs
Assets/CoreAiUnity/Runtime/Source/Features/World/WorldLlmTool.cs
Assets/CoreAiUnity/Runtime/Source/Features/World/WorldStateManager.cs
Assets/CoreAiUnity/Runtime/Source/Features/World/WorldStateWriteResult.cs
Assets/CoreAiUnity/Runtime/Source/Features/World/Infrastructure/CoreAiComponentCatalog.cs
Assets/CoreAiUnity/Runtime/Source/Features/World/Infrastructure/CoreAiComponentCommandExecutor.cs
Assets/CoreAiUnity/Runtime/Source/Features/World/Infrastructure/CoreAiComponentCommandOptions.cs
Assets/CoreAiUnity/Runtime/Source/Features/World/Infrastructure/CoreAiWorldCommandExecutor.cs
Assets/CoreAiUnity/Tests/EditMode/ComponentCatalogEditModeTests.cs
Assets/CoreAiUnity/Tests/EditMode/CoreAIWebGlStreamingAssetsGuardEditModeTests.cs
Assets/CoreAiUnity/Tests/EditMode/CoreAiChatServiceEditModeTests.cs
Assets/CoreAiUnity/Tests/EditMode/CoreAiFacadeEditModeTests.cs
Assets/CoreAiUnity/Tests/EditMode/MultiplayerSessionIntegrationTests.cs
Assets/CoreAiUnity/Tests/EditMode/UnityHostRegressionTests.cs
Assets/CoreAiUnity/Tests/EditMode/WorldCommandsInstallerEditModeTests.cs
Assets/CoreAiUnity/Tests/EditMode/WorldStateWriteResultTests.cs
Assets/CoreAiUnity/Tests/PlayMode/FastNoLlm/WorldStateManagerPlayModeTests.cs
Docs/CoreAI/MULTIPLAYER_SESSIONS.md
dev-docs/fix-wave-2026-10-24/workdoc04-unity-host.md
```

Unity generated two new source `.meta` files while the lead's Editor imported the changing tree. This worker did not write or edit metadata. The lead owns their review and handling.

## Lead verification filters

Unity EditMode (semicolon-separated fixture filter):

```text
-testPlatform EditMode -testFilter "CoreAI.Tests.EditMode.UnityHostRegressionTests;CoreAI.Tests.EditMode.MultiplayerSessionIntegrationTests;CoreAI.Tests.EditMode.WorldStateWriteResultTests;CoreAI.Tests.EditMode.ComponentCatalogEditModeTests;CoreAI.Tests.EditMode.WorldCommandsInstallerEditModeTests;CoreAI.Tests.EditMode.CoreAIWebGlStreamingAssetsGuardEditModeTests;CoreAI.Tests.EditMode.CoreAiChatServiceEditModeTests;CoreAI.Tests.EditMode.CoreAiFacadeEditModeTests;CoreAI.Tests.EditMode.WorldStateManagerResetPersistenceEditModeTests;CoreAI.Tests.EditMode.WorldStateManagerAutoSaveDisposeEditModeTests"
```

Unity PlayMode:

```text
-testPlatform PlayMode -testFilter "CoreAI.Tests.PlayMode.WorldStateManagerPlayModeTests;CoreAI.Tests.PlayMode.CoreAiWorldCommandExecutorPlayModeTests;CoreAI.Tests.PlayMode.CoreAiChatPanelStopPlayModeTests;CoreAI.Tests.PlayMode.CoreAiChatPanelLifecyclePlayModeTests"
```

Expected: positive fixture presence and every applicable case passes. Windows-native file sharing/read-only failure tests explicitly skip elsewhere. Destructive reset fixtures skip if external tracked world objects exist; the facade fixture skips if an external lifetime scope exists. Run those in the lead's isolated empty test context and report skips explicitly. No new fixture is portable-linked: these exercise Unity host types and Editor APIs.

Worker-executed verification: scoped `git diff --check` returned exit 0 with no output; source and test reads only. No test execution, failure-before runtime receipt, compile pass, live Unity action, build/export, consumer UPM smoke, browser transaction/reload, actual domain-reload-disabled play transition, device or network/load result is claimed. The lead's compiler feedback was an in-progress failure, not a product regression receipt or a final pass.

The final tracked-file whitespace check and the explicit six-new-file trailing-whitespace/hash inspection both returned exit 0. New-file SHA-256 values at source freeze:

| File | SHA-256 |
| --- | --- |
| `World/WorldStateWriteResult.cs` | `0A518604C85DB621F29F04B630D2C8EBEC1FCB5C16A219A28AEACB7FD8DEE933` |
| `World/Infrastructure/CoreAiComponentCommandOptions.cs` | `E8DF65A5DD799D41DE3AD10A54D5A711DBCB8BC15CCFEB70658DAC8449E36898` |
| `Tests/EditMode/UnityHostRegressionTests.cs` | `C18255F7823E5AEAF341303EBC2CC34695D5460044299B8D0C114178FC2A2B54` |
| `Tests/EditMode/MultiplayerSessionIntegrationTests.cs` | `DB459D5BE1352743BFFDEC6DDFC0D8A9393BE42FB2C591D018A5A76173AD8D66` |
| `Tests/EditMode/WorldStateWriteResultTests.cs` | `6D644C71FD7DF31411A2B654782B26E08A3710A643809C75199A64C629680D23` |
| `Docs/CoreAI/MULTIPLAYER_SESSIONS.md` | `2B3B63D4D01E83FF66E0D6D9038A2E060BB2663444512858F7D5ADFF8B69CE77` |

## Remaining concerns and ownership

- UH-06 and audit retention belong to the separate audit owner; UH-07 belongs to the memory owner. This worker did not edit either feature.
- Default global world capture/shared file remains an explicit local-game preset limitation, not a claim of per-player or additive-world isolation.
- Current-project stable-GUID checks do not establish a clean UPM consuming-project result. Platform/device/browser/actual play-generation gates remain open.
- Idle deadline tool-progress subscriptions still match by role. Same-role tool events from another actor can rearm that local idle budget; this was relayed to the lead for triage rather than silently adding an unscored cross-cutting redesign. Host caller tokens still provide independent cancellation.
- The lead received a source-freeze signal. No source/test/guide file will change after that signal without notifying the lead. Only this receipt was appended afterward.
