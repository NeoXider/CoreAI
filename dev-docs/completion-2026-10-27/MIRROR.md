# Mirror session-lifetime PR audit — 2026-10-27

Status: source review complete; root acceptance pending. Reviewer: native gpt-6.1-sol, reasoning effort max. Baseline: `demo` at published `bc3fd74f` / 7.54.0; tracked and untracked working-tree changes are the review surface.

## Goal, invariants and scope

Review the module as a replaceable framework block, preserve unrelated authorized changes and the frozen 2026-10-23 audits, and report only actionable confirmed defects. No implementation, asset, metadata, test, build, Unity, provider, Git or service mutation was performed. The owner explicitly requested these retained per-module reports. Saved receipts were parsed, not rerun.

## Review result

No additional confirmed source defect in this module's current diff. The PR-NET-001 repair assigns an opaque increasing generation to every binding. Delayed peer/binding notifications carry the departing generation, and the session host compares it with the currently admitted session before removal. Reusing connection id, actor id, empty session string or all peer values cannot identify a replacement as the old binding.

Read the reentrant paths for both listener orders, world-owned unregister, replacement admission during disconnect, disposal from legacy subscribers, failed subscriber isolation and final cleanup. Capturing both subscriber lists before invoking either preserves the generation-aware companion even if a legacy subscriber disposes the bridge. A replacement's own release is not suppressed by the earlier generation's notification scope. Final unregister in session release also verifies the admitted generation.

`RoleId`, account admission, room policy and shared/dedicated world policy remain host choices. A repeated role or reused caller session string is not treated as a framework policy defect. The opaque binding generation is internal lifecycle identity, not authentication.

## Regression evidence read

`artifacts/pr-review-20261025/unity-repair-focused5.xml` contains **51 Passed** rows for `CoreAI.Net.Mirror.Tests.MirrorBridgeRulesEditModeTests` and **25 Passed** for `MirrorSessionHostEditModeTests`, with no failure or skip in those fixtures.

Relevant exact methods include:

- `PeerDisconnect_SameIdReplacement_SurvivesDelayedBindingRelease`
- `PeerDisconnect_ReusedOverrideReplacementSurvivesBothListenerOrders` (four cases)
- `WorldRelease_ClearsTheAdmittedGeneration_RegardlessOfSessionOverride` (three cases)
- `WorldRelease_LegacySubscriberDisposesBridge_StillForgetsHostSession`
- `Admission_ConnectCallbackRetiresBinding_DoesNotPublishTheRetiredSession`
- `NET001_Disconnect_IdenticalPeerReplacementKeepsItsGenerationAndRequests` (two cases)
- `NET001_Disconnect_ReentrantReplacementReleaseNotifiesBothGenerations`
- `NET001_BindingRelease_CapturedCompanionSurvivesLegacyDisposalAndFailures`

These cases assert replacement identity, generation, event/request survival and later cleanup. They are meaningful in-process regression evidence rather than a real network-capacity claim.

## Acceptance limits

The new session-host cases use recording connect/disconnect callbacks; the bridge cases use the offline transport fixture. They do not independently prove the original acceptance's complete world/player/resources/chat teardown sequence over live Mirror transport. Root still owns that integrated acceptance and final current-source rerun. No KCP/socket multiplayer session, disconnect/reconnect on a built player, device test, latency/load target or 100-player capacity was executed by this reviewer.

## Reviewed source snapshot

| Path | SHA-256 |
| --- | --- |
| `Assets/CoreAIMirror/Runtime/MirrorNetworkBridge.cs` | `dd37e04b1ce5a6fe76434666f967d00d7db947e7401a5fd17814f4a3a1b9492e` |
| `Assets/CoreAIMirror/Runtime/CoreAiMirrorSessionHost.cs` | `0fa494da2af3a56783e189219ab2756ab017e7af89bf8095cd7f445d960a8afd` |
| `Assets/CoreAIMirror/Tests/EditMode/MirrorBridgeRulesEditModeTests.cs` | `f334bbcf1af0d0c58fd0343a4bcfdc9dcf6e3dcbfe78eec553c2b024d83ccea0` |
| `Assets/CoreAIMirror/Tests/EditMode/MirrorSessionHostEditModeTests.cs` | `80e1b469134df9ebffd2028532fec6a4cd9753f26d96db9485163d70fe0a9b5a` |
