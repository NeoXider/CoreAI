# Independent PR review: Mirror integration

Status: complete native takeover after the CLI review ended before producing a report. All nine assigned changed paths and 72 hunks were reviewed. One source-confirmed P2 finding; execution gates remain unrun. Reviewer: native `gpt-6.1-sol`, reasoning effort `max`. Base: `69d5bcc37502cd4bb464a3bf9ac0c53b1624b804`. Review head: `bc3fd74f0a889bfe1b1e7babc6bc02ed48a7c05e`. Checkout: `D:\Git\CoreAI`, branch `demo`.

## Intent and decisions

- Real goal: identify introduced Mirror bridge and fixture defects from the full changed source/caller path.
- Implied requirements: stable callback-entry snapshots, listener error isolation, immutable admission identity under transport-id reuse, reentrant disconnect/disposal safety, exact cleanup ownership/order, trusted actor/capability APIs, and static-state cleanup on WebGL-targeted Editor runs.
- Conventions: runtime-first and replaceable seams, English prose, C# 9 explicit types in any snippets. The assigned report is an explicit exception to the repository's default audit-document rule.
- Scope: all nine group 11 changed paths and hunks. Adjacent contracts and ignored installed Mirror v96.11.3 are read-only context. Existing documented host-mode and real-socket limits are not new defects.
- Exclusions: production/test changes, Git mutations, builds/tests, Unity/MCP/network calls, assets, vendor edits, and additional agents. Reports 02/05/17 and the persistence artifact remain frozen.
- Acceptance: every confirmed finding needs a causal changed line, valid trigger, expected/actual behavior, minimal regression plan, and uncertainty. Hypotheses and unrun gates remain separate; in-memory fixtures cannot establish real-network behavior.

## Findings

### PR-NET-001 [P2] A delayed old-ID binding notification removes a replacement session

- Changed anchor: `Assets/CoreAIMirror/Runtime/MirrorNetworkBridge.cs:986`, unconditionally invoking the captured `BindingReleased` subscribers with only `connectionId` after peer listeners and pending completions finish. The deferral is introduced at lines 2716-2720.
- Valid trigger: create the normal session host first, then subscribe a `PeerDisconnected` listener that admits a trusted replacement session on the transport's reused connection ID. Let the session host's earlier listener finish removing the old player/session before that later listener admits the replacement with a new session ID. This uses a fresh host-issued context and a new transport connection object; no forged identity or unsupported host mode is needed. Installed Mirror removes the old connection from its table before invoking its disconnect event (`Assets/Mirror/Core/NetworkServer.cs:1028-1037`), so the ID is free during callback reentry.
- Expected: the old departure releases only its captured session. A newly admitted replacement keeps its world player, identity and session, and its later disconnect cleans those resources exactly once.
- Actual and causal path: the normal host listener removes the old `_actorsByConnection` entry before world teardown. Its unregister calls `ReleaseBinding`, which this PR suppresses while `_notifyingDisconnect` contains that integer ID. The later listener can then call `CoreAiMirrorSessionHost.Admit` for the replacement, storing its new context under the same integer. `SamePeer` detects the new session and protects the replacement bridge binding at lines 966-970, but the unconditional delayed notification still runs. `CoreAiMirrorSessionHost.OnBindingReleased` at lines 248-261 reads the current entry by integer ID, removes the replacement session and its identity, and intentionally makes no world-disconnect call. The new player remains with no corresponding host session; a later transport departure cannot find it for normal world teardown.
- Introduced behavior: baseline `ReleaseBinding` notified binding listeners during the old host's release, before the later peer listener admitted the replacement. Its final `SamePeer` guard then skipped replacement release. The new delayed notification carries neither the captured admission nor a generation to distinguish the replacement.
- Regression plan: extend the real world/session fixture with a later peer listener that replaces the removed transport object at the same ID and admits a fresh trusted context/session. Assert replacement identity and `LiveSessionCount` survive the old notification, the old player is removed once, replacement requests remain usable, and a later replacement disconnect removes its player/signals/chat resources exactly once. Include the same-ID/new-session case separately from the existing different-ID replacement and admission-only reuse tests.
- Uncertainty: source-confirmed bridge/session-host mismatch; no Unity, transport execution or real-network reproduction was run. The installed vendor disconnect ordering was read directly. Existing fixture `Disconnect_ReentrantDispose_PreservesReplacementAdmissionOnAReusedConnectionId` calls only the authenticator after bridge disposal and creates no replacement world session, so it does not disprove this path.

## Coverage and verification

- Group 11 inventory: nine of nine changed paths, all 72 hunks, 1184 added and 44 deleted lines. Complete changed-hunk review includes deleted code and all added test bodies/helpers; new historical developer documents were read in full. No assigned changed path/hunk remains unread. Existing untouched implementation bodies were read where needed to trace the joins below, not claimed as an exhaustive package-wide audit.
- No builds, tests, Unity, network, MCP or provider execution performed by this reviewer.

### Exact changed-hunk ledger

Ranges are `git diff --unified=0` old-to-new headers. A missing count means one line; count zero denotes an insertion/deletion boundary. Every listed hunk was read.

| Repository-relative path | Reviewed old-to-new ranges |
| --- | --- |
| `Assets/CoreAIMirror/README.md` | `-129,0 -> +130,14` |
| `Assets/CoreAIMirror/Runtime/MirrorNetworkBridge.cs` | `-2,0 -> +3; -3,0 -> +5; -312,0 -> +315; -841 -> +844; -928,0 -> +932,3; -932 -> +938,2; -937,0 -> +945,6; -940,2 -> +953,2; -945 -> +958; -947 -> +960; -949,3 -> +962; -953 -> +964,35; -955,0 -> +1001,9; -1245 -> +1299,2; -1327 -> +1382; -1346,0 -> +1402; -1357 -> +1413; -1359 -> +1415; -1360,0 -> +1417,2; -1448 -> +1506; -2636,0 -> +2695; -2656 -> +2715,4; -2658,3 -> +2720; -2663 -> +2723,2; -2666,0 -> +2728,5; -2677,0 -> +2744,10; -2680 -> +2756; -2682,0 -> +2759; -2685,3 -> +2762; -2689 -> +2764; -2690,0 -> +2766; -2692 -> +2768,14; -2693,0 -> +2783,2; -2696 -> +2787,2; -2698 -> +2790; -2703,2 -> +2795; -2706 -> +2797; -2708 -> +2799; -2710,2 -> +2801; -2713,2 -> +2803,2; -2715,0 -> +2806,2; -2716,0 -> +2809,5; -2719 -> +2816; -2721 -> +2818; -2723,2 -> +2820,2; -2728,0 -> +2826,10; -2970 -> +3077` |
| `Assets/CoreAIMirror/Tests/EditMode/MirrorBridgeAttachEditModeTests.cs` | `-3,0 -> +4; -109,0 -> +111,38` |
| `Assets/CoreAIMirror/Tests/EditMode/MirrorBridgeRulesEditModeTests.cs` | `-31,0 -> +32; -37,0 -> +39; -40 -> +42; -267,0 -> +270,258; -986,0 -> +1247,9` |
| `Assets/CoreAIMirror/Tests/EditMode/MirrorKickEditModeTests.cs` | `-1,0 -> +2; -2,0 -> +4; -9,0 -> +12; -39,0 -> +43; -50 -> +54,3; -125,0 -> +132,289; -354,0 -> +650,58` |
| `Assets/CoreAIMirror/Tests/EditMode/OfflineMirror.cs` | `-98,0 -> +99,5; -125,0 -> +131; -130,0 -> +137,2; -137,0 -> +146,2; -259 -> +269,14; -261,0 -> +285,4; -367,0 -> +395,2` |
| `dev-docs/audits/2026-10-23/10-mirror-network.md` | `-0,0 -> +1,118` |
| `dev-docs/fix-wave-2026-10-24/10-mirror.md` | `-0,0 -> +1,77` |
| `dev-docs/fix-wave-2026-10-24/16-unity-mirror-integration.md` | `-0,0 -> +1,134` |

### Adjacent read-only source coverage

- `MirrorNetworkBridge.cs`: changed disconnect/pending/subscriber methods plus pending-state declarations; bind/unregister/teardown; server request admission and timeout; disposal; connection/binding release; same-peer comparison; live client-connection reconciliation. The remaining unchanged event/clock/codec paths were not exhaustively reread.
- Full `Assets/CoreAIMirror/Runtime/CoreAiMirrorSessionHost.cs`: admitted context/session ownership, newest-session replacement, world release ordering and identity cleanup. `CoreAiMirrorNetworkBridgeProvider.cs`: world attach/startup catch-up, disposal/update/hook references, `AdmitOrDrop`, pending drops, recorded admissions and `OnServerDisconnected` catch boundary. `CoreAiMirrorAuthenticator.cs`: provider decision, trust-result binding, result lookup/forget and attempt references.
- Full portable contracts `Assets/CoreAIMods/Runtime/RbxApi/Instances/Networking/INetworkBridge.cs` and `Assets/CoreAI/Runtime/Core/Authority/IActorAdmissionProvider.cs`, plus previously read `ActorContext` trust/grant construction. Relevant `LuaCsRbxApiBindings.cs:1550-1623` explains actor/player/chat/mod/signal cleanup. No new authority factory or broadened grants were proposed.
- Installed, ignored Mirror v96.11.3 was read only, as pinned by root. Actual vendor `Assets/Mirror/Core/NetworkServer.cs` startup, static reset/handler removal and `OnTransportDisconnected` ordering were inspected. Its `Listen` branch uses `listen && !Utils.IsWebGL`, otherwise zeroes capacity; its disconnect removes the old connection before raising the event. No vendor install, modification or real-socket test occurred.
- Offline fixture changed ownership/startup/shutdown, loopback connect and teardown helper bodies were inspected against those vendor branches. The test assemblies remain optional under `MIRROR` and `UNITY_INCLUDE_TESTS`; runtime/test asmdefs were read contextually without changes.
- Historical audit/repair and Unity integration receipts were reviewed as changed documents after independent source inspection. Their previous source-final statements and Windows EditMode counts are historical evidence, not a disproof of the newly identified same-ID/new-session path. Their actual XML/process artifacts were not revalidated in this takeover.

### Acceptance self-audit

- The repair detaches old pending objects before arbitrary peer listeners and individually attempts multicast completion/lifecycle subscribers. Cleanup is protected by nested `finally` blocks. Old completion objects cannot remove newly created correlation entries merely by enumerating a live dictionary. No additional introduced defect was established in that ordinary failure-isolation path.
- Inspected exception/notification ordering, reentrant notify/unregister/disconnect/dispose cases, replacement requests, client admission loss, expiry and diagnostic containment. Existing different-ID replacement and admission-only reused-ID tests do not exercise PR-NET-001's replacement world-session ownership.
- The WebGL-targeted Editor fixture restoration is bounded to test code: it enables listen, calls the vendor's real Listen/registration path, restores offline capacity only under `UNITY_EDITOR && UNITY_WEBGL`, and restores prior listen/capacity after shutdown. It does not enable a WebGL-player server or prove a network listener. No new defect was established in its changed startup/restoration flow.
- Host mode, world replication, actual socket behavior, capacity and restart gaps already documented outside this repair were not relabeled as new findings. Trusted identity remains host-issued; this review does not prescribe host login/account policy.

## Unconfirmed hypotheses

- No additional introduced finding is counted. Integer-only teardown/notifying markers merit same-ID/new-generation regression coverage when PR-NET-001 is fixed; they must not be treated as evidence that a reused integer always denotes the departing session.
- Reusing the same admission object without a new peer/session generation is not independently counted: the peer contract says a reconnect gets a new session, and the added admission-only fixture has narrower expectations than a newly admitted world session.

## Completion accounting

- CHANGED: this report only.
- VERIFY OUTPUT: all nine assigned paths and 72 hunks reviewed; one P2, zero counted P1/P3 findings. Direct final checks found nine ledger rows, zero missing/duplicate paths, one finding heading, zero trailing-whitespace lines, zero Cyrillic characters and a final newline. `git diff --check -- <report>` exited 0; the direct content checks also cover this untracked report. HEAD still equals the pinned review head, and the nine primary paths have no worktree difference from that head.
- NOT DONE: builds/tests, Unity EditMode/PlayMode, actual in-memory reproduction of PR-NET-001, real sockets/separate processes, player/IL2CPP/WebGL/browser/device, performance/load and Git/publication. Root owns regression implementation and all executable acceptance.
- CONCERNS: a delayed connection-ID-only release removes a replacement session despite the bridge's generation-aware peer guard.
- QUESTIONS: none currently block read-only progress.
