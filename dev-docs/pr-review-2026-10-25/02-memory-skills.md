# Independent PR review: memory, skills, and tool contracts

Status: complete static review of all group 02 changed paths and hunks; execution gates remain unrun. Reviewer: native `gpt-6.1-sol`, reasoning effort `max`, independent of the repair authors. Base: `69d5bcc37502cd4bb464a3bf9ac0c53b1624b804`. Head: `bc3fd74f0a889bfe1b1e7babc6bc02ed48a7c05e`. Checkout: `D:\Git\CoreAI`, branch `demo`.

## Review intent and decisions

- Real goal: identify introduced behavioral defects and API regressions in the assigned changed memory, summary, skill, and tool contracts, using source and callers rather than prior audit conclusions.
- Implied requirements: frozen admitted context, durable actor and tenant/user/topic/role partitions, scoped policy/history/tool bindings, atomic role preparation, conservative migration and corruption handling, bounded shared state, cancellation and disposal safety, WebGL compatibility, and source compatibility.
- Conventions: runtime-first, replaceable public seams, English prose, C# 9 explicit types in any illustrative snippets. The parent explicitly requested this report, overriding the repository's default prohibition on audit files.
- Out of scope: production/test changes, Git mutations, builds/tests, Unity/MCP/provider calls, asset edits, and additional agents. Adjacent files may be read to trace joins.
- Acceptance: every confirmed finding must identify an added/changed line, trigger, expected/actual behavior, causal path, minimal meaningful regression, and uncertainty. Unconfirmed hypotheses are separate. Coverage records all reviewed paths and changed hunk ranges, plus unread gaps.

## Findings

### PR-MEM-001 [P2] Read-triggered key migration bypasses configured persistence confirmation

- Changed anchor: `Assets/CoreAI/Runtime/Core/Features/AgentMemory/FileConversationSummaryStore.cs:288`, calling the new filesystem mutations at `Assets/CoreAI/Runtime/Core/Features/AgentMemory/AgentMemoryFileKey.cs:71-72`.
- Concrete trigger: create an unambiguous legacy `teacher.json` containing `{"Summary":"legacy fact"}`, then construct `FileConversationSummaryStore(root, afterWriteAsync: token => Task.FromResult(false))` and call `LoadSummaryAsync("teacher")`. No malformed identity, account switch, unsupported alias, or concurrent process is needed.
- Expected: because loading now copies a file and renames its source, the configured host hook must confirm that mutation before success; a refused confirmation must surface as a failure and remain pending for recovery. On WebGL, the equivalent receipt is only automatic persistence armed, never browser transaction completion.
- Actual and causal path: `ReadSummaryCoreStrict` calls `EnsureLegacyMigration`, whose shared helper copies the canonical file and moves the legacy file to `.legacy`. Neither path calls `PathState.RecordMutation`. `ReadCommittedAsync` consequently returns `state.Pending == null` at line 252, and `LoadSummaryAsync` skips `ConfirmPendingAsync` at lines 220-224 and returns the fact successfully. The supplied hook is never invoked. `OpenSummaryTransaction[Async]` has the same migration path. `FileAgentMemoryStore.TryLoadDetailedCore` also calls `EnsureLegacyFiles` at line 424 without `QueueFlush`/`ConfirmDurableAsync`, so a document-only or existing-history key migration skips its WebGL arming check as well.
- Introduced behavior: the baseline read the existing legacy filename without mutating storage; this PR adds the copy/rename during reads. Existing save/clear confirmation does not cover a read-only session after migration.
- Minimal meaningful regression: seed the legacy summary file, use a false/pending controllable confirmation hook, and assert that migration does not complete successfully before confirmation. Verify one canonical file and byte-identical backup, a retained pending generation after refusal, recovery through a fresh peer after the hook becomes true, and no manual `FS.syncfs` or blocking wait. Cover the summary transaction-open path and a Source/WebGL memory read that migrates files without embedded conversation rewriting.
- Uncertainty: source-confirmed missing hook invocation; this reviewer has not executed the reproducer or a browser. A correctly armed default WebGL host can persist these writes automatically, so this finding claims ignored configured confirmation/failure reporting rather than universal data loss.

### PR-MEM-002 [P2] The admitted memory binding leaves `read_skill` reporting the callable target as unavailable

- Changed anchor: `Assets/CoreAI/Runtime/Core/Features/Llm/CallSkillToolLlmTool.cs:65`, plus the invocation-only descriptor binding at lines 360-364.
- Concrete trigger: a skill contains the built-in declarative `MemoryLlmTool`, and the admitted request has a memory store and both skill proxies. The new production `MeaiLlmClient.BuildInvocationTools` runs `BindForRole` on this list before native expansion. `BindForRole` changes only `CallSkillToolProxy`; its early return leaves `ReadSkillProxy` unchanged.
- Expected: the same admitted request's discovery metadata and executable target agree. After role-local memory binding, `read_skill` should expose `memory` with the effective parameter schema and `invocable: true`; an unbound or denied target should remain unavailable without changing the shared catalog.
- Actual and causal path: `call_skill_tool` now replaces a declarative memory descriptor with a real `MemoryTool` function at lines 360-364 and can execute it. The paired `read_skill` still rebuilds descriptors from the unbound skill (`Assets/CoreAI/Runtime/Core/Features/Llm/ReadSkillLlmTool.cs:221`), so its JSON at line 233 states `invocable: false`. The raw declaration implements no invocation interface. This directly contradicts the read tool's instruction at lines 144-145 to call it first to learn which tools are available; the documented discovery-first workflow is given a false availability result.
- Introduced behavior: the baseline's false flag matched its unavailable memory proxy. This PR makes execution available only in the invocation proxy and creates the contradictory request metadata. The added `MemoryProxy_BindingIsInvocationLocal_AndRetainsRestriction` test verifies execution but never checks its paired read result.
- Minimal meaningful regression: use the same admitted effective tool list for `read_skill` and `call_skill_tool`, assert that the returned `memory` schema and `invocable` flag match the resolved invocation, execute an append into only the admitted role's scoped store, and repeat with a denied allowlist and two concurrent role/store bindings. Assert the original unbound catalog remains unchanged.
- Uncertainty: the contradictory boolean follows directly from source. No claim is made that every model will refuse the call; model behavior and the real provider pipeline were not executed here.

The relevant source-derived response projection below uses the same catalog, role, memory store, and allowed target. These are exact field values deduced from the branches, not captured runtime JSON; unrelated descriptions/schema fields are omitted.

| Version / effective tools | `read_skill("notes")` projection | `call_skill_tool("memory", ...)` resolution |
| --- | --- | --- |
| Baseline | `{"success":true,"skill":"notes","tools":[{"tool_name":"memory","invocable":false}]}` | False; `Tool 'memory' is unavailable for this skill call.` |
| PR with admitted role/store binding | `{"success":true,"skill":"notes","tools":[{"tool_name":"memory","invocable":false}]}` | True; descriptor has a role-bound `MemoryTool.CreateAIFunction()` |

The raw, unbound static-host catalog may correctly report false. This finding is limited to the two proxies actually exposed together in an admitted request after binding.

## Coverage and verification

- Read repository `AGENTS.md`, normative `Docs/ARCHITECTURE_RULES.md`, and architecture/memory integration guidance. Verified the actual checkout path and branch read-only.
- Reconciled group `02` of `coverage.json`: 35 of 35 assigned paths, 2733 added lines and 376 deleted lines in the immutable comparison. All 35 paths' changed hunks were read. This comprises 19 runtime source files, seven test files, six metadata companions, one user guide, and two historical developer documents.
- Read every changed hunk in the table below. New source/test files were read in full. Existing files received complete changed-hunk review plus relevant surrounding call paths; this does not claim every untouched line in those files was audited. The six metadata files contain only `fileFormatVersion` and their GUIDs; none was modified.
- `Assets/CoreAiUnity/Runtime/Source/Features/ConversationSummary/**` and `.../Skills/**` contain no changed paths in this comparison. The actual changed skill backing is `.../Features/Llm/Infrastructure/FileSkillStore.cs`, included here.
- No build, test, Unity, browser, provider, or runtime verification has been run by this reviewer.

### Exact changed-hunk ledger

Ranges are the complete `git diff --unified=0` old-to-new hunk headers. A missing count means one line; count zero denotes an insertion/deletion boundary. Every listed hunk was read, including deleted code. No assigned changed path/hunk remains unread.

| Repository-relative path | Reviewed old-to-new ranges |
| --- | --- |
| `Assets/CoreAI/Docs/AGENT_BUILDER.md` | `-345,0 -> +346,11; -359,0 -> +371,12; -811,0 -> +835,19; -882,0 -> +925,31` |
| `Assets/CoreAI/Runtime/Core/Features/AgentMemory/AgentBuilder.cs` | `-1 -> +1; -930,10 -> +929,0; -983,4 -> +973; -1000,4 -> +987; -1019,0 -> +1004,13` |
| `Assets/CoreAI/Runtime/Core/Features/AgentMemory/AgentMemoryFileKey.cs` | `-0,0 -> +1,76` |
| `Assets/CoreAI/Runtime/Core/Features/AgentMemory/AgentMemoryFileKey.cs.meta` | `-0,0 -> +1,2` |
| `Assets/CoreAI/Runtime/Core/Features/AgentMemory/AgentMemoryPolicy.cs` | `-76,0 -> +77,4; -110,0 -> +115,4; -124,0 -> +133,4` |
| `Assets/CoreAI/Runtime/Core/Features/AgentMemory/AgentMemoryScope.cs` | `-50,0 -> +51,9; -56 -> +65,2; -70 -> +80; -79 -> +89; -163,4 -> +173,4; -175 -> +185; -182 -> +192; -231 -> +241; -256,0 -> +267,13; -274 -> +297; -283 -> +306; -288,0 -> +312,5; -294,0 -> +323; -299,0 -> +329; -305 -> +335; -308,0 -> +339; -312,0 -> +344` |
| `Assets/CoreAI/Runtime/Core/Features/AgentMemory/Context/DefaultContextBudgetPolicy.cs` | `-84 -> +84,2; -92 -> +93,2; -94,9 -> +96,3` |
| `Assets/CoreAI/Runtime/Core/Features/AgentMemory/ConversationContextSnapshot.cs` | `-10 -> +10; -12,0 -> +13,4; -44,0 -> +49,10; -47,2 -> +61,2; -50,0 -> +65,4; -56 -> +74,5; -61 -> +83,2; -63,0 -> +87,3; -72 -> +98,55` |
| `Assets/CoreAI/Runtime/Core/Features/AgentMemory/ConversationSummaryTransaction.cs` | `-0,0 -> +1,101` |
| `Assets/CoreAI/Runtime/Core/Features/AgentMemory/ConversationSummaryTransaction.cs.meta` | `-0,0 -> +1,2` |
| `Assets/CoreAI/Runtime/Core/Features/AgentMemory/DeterministicConversationContextManager.cs` | `-1 -> +1; -40,3 -> +40,4; -44,2 -> +45,10; -47 -> +56,2; -152,7 -> +162,4; -160,2 -> +167,11; -163 -> +179,2` |
| `Assets/CoreAI/Runtime/Core/Features/AgentMemory/FileConversationSummaryStore.cs` | `-2 -> +1,0; -14 -> +13,2; -25 -> +25; -32,8 -> +32,6; -56,0 -> +55,2; -103,0 -> +104; -173 -> +174,2; -215,0 -> +218; -220 -> +223; -237 -> +240,2; -283,0 -> +288; -308,0 -> +314,2; -324 -> +331,2; -336 -> +344,3; -371,2 -> +381,3; -385 -> +396; -387 -> +398,2; -396 -> +408,3; -408 -> +422; -410 -> +424; -483 -> +497,2; -494,0 -> +510; -531,2 -> +547,3; -545 -> +562; -547 -> +564,2; -555,0 -> +574; -565,0 -> +585,114; -616,2 -> +749,2; -659 -> +792; -661 -> +794; -664 -> +797,3; -665,0 -> +801; -667 -> +803; -715,5 -> +851,2; -721,2 -> +854,3; -724,4 -> +858,6` |
| `Assets/CoreAI/Runtime/Core/Features/AgentMemory/InMemoryConversationSummaryStore.cs` | `-12 -> +12,2; -14,0 -> +16,3; -51,0 -> +56,2; -81,0 -> +88,2; -85,0 -> +94,35` |
| `Assets/CoreAI/Runtime/Core/Features/AgentMemory/InventoryTool.cs` | `-38,0 -> +39; -58,0 -> +60,4` |
| `Assets/CoreAI/Runtime/Core/Features/AgentMemory/LlmAssistedConversationContextManager.cs` | `-1 -> +1; -82,14 -> +82,4; -97 -> +87,12; -99,7 -> +100,9; -107,2 -> +110,2; -110,4 -> +113; -115,7 -> +115,10; -123,10 -> +126,10; -134,3 -> +137; -138,7 -> +139,9; -146,29 -> +149,29; -176,6 -> +179,6; -183 -> +186; -185,3 -> +188,3; -189,9 -> +192,9; -199,3 -> +202,5; -203 -> +208,4; -305,0 -> +314,2; -316 -> +326; -324 -> +334; -368,5 -> +378,3; -377,0 -> +386,15; -431,2 -> +454,2` |
| `Assets/CoreAI/Runtime/Core/Features/AgentMemory/MemoryTool.cs` | `-59,0 -> +60; -149,0 -> +151,4` |
| `Assets/CoreAI/Runtime/Core/Features/AgentMemory/ScopedConversationSummaryStoreDecorator.cs` | `-135,0 -> +136,8` |
| `Assets/CoreAI/Runtime/Core/Features/AgentMemory/SharedPathStateRegistry.cs` | `-0,0 -> +1,97` |
| `Assets/CoreAI/Runtime/Core/Features/AgentMemory/SharedPathStateRegistry.cs.meta` | `-0,0 -> +1,2` |
| `Assets/CoreAI/Runtime/Core/Features/Llm/CallSkillToolLlmTool.cs` | `-62,0 -> +63,9; -106,0 -> +116,2; -134 -> +145,3; -141,0 -> +155,2; -174,0 -> +190,5; -196 -> +216,2; -219,0 -> +241; -320 -> +342; -335,0 -> +358,11; -399 -> +432,2` |
| `Assets/CoreAI/Runtime/Core/Features/Llm/SkillSetToolResolver.cs` | `-155,0 -> +156; -224 -> +225; -231,0 -> +233,5` |
| `Assets/CoreAiUnity/Runtime/Source/Features/AgentMemory/Infrastructure/FileAgentMemoryStore.cs` | `-1,2 -> +1; -12,0 -> +12; -118,17 -> +118,11; -139,0 -> +134; -235,0 -> +231,10; -278 -> +283,2; -286,0 -> +293; -338,0 -> +346,2; -367,0 -> +377,2; -385,0 -> +397,2; -409,6 -> +421,0; -416,0 -> +424,3; -417,0 -> +428; -444,0 -> +456; -450,0 -> +463,33; -453,0 -> +499,7; -463 -> +515; -494 -> +546; -501 -> +553; -513 -> +565; -537 -> +589; -545,0 -> +598; -571 -> +624; -576,3 -> +629,2; -582,0 -> +635,3; -589,0 -> +645; -613 -> +669; -644 -> +700; -651 -> +707; -663 -> +719; -670 -> +726,2; -696 -> +753; -727 -> +784; -734 -> +791; -746 -> +803; -759,0 -> +817; -764,0 -> +823; -773 -> +832,2; -782 -> +842,2; -812 -> +873; -866 -> +927; -909 -> +970; -916 -> +977; -932 -> +993; -986,2 -> +1047,2; -1219 -> +1280; -1263 -> +1324; -1308 -> +1369,3; -1332 -> +1395; -1354,0 -> +1418; -1355,0 -> +1420; -1430,0 -> +1496,7; -1433,0 -> +1506; -1440 -> +1513; -1461 -> +1534; -1479 -> +1552; -1527,0 -> +1601; -1622 -> +1696,12; -1624 -> +1709,17; -1629 -> +1730; -1632,6 -> +1733; -1639,6 -> +1735,2; -1646,2 -> +1738,12; -1648,0 -> +1751,2; -1650 -> +1754,5; -1653,5 -> +1761; -1659 -> +1763,7; -1662,6 -> +1772,2; -1669,2 -> +1775; -1672 -> +1777,2; -1673,0 -> +1780; -1675,2 -> +1782,3; -1678 -> +1786,2; -1679,0 -> +1789; -1681 -> +1791,37; -1725,2 -> +1871` |
| `Assets/CoreAiUnity/Runtime/Source/Features/Llm/Infrastructure/FileSkillStore.cs` | `-1,2 -> +1; -39,2 -> +38,4; -44,0 -> +46; -57,0 -> +60,6; -75 -> +83,2; -107,0 -> +117,3; -151 -> +163; -194 -> +206; -232 -> +244; -395 -> +407,4; -397,11 -> +411,0; -409 -> +413,5; -410,0 -> +419,8; -420,0 -> +437; -428,0 -> +446; -443,0 -> +462; -640 -> +659,2; -643 -> +663,25` |
| `Assets/CoreAiUnity/Tests/EditMode/FileAgentMemoryStoreEditModeTests.cs` | `-11,0 -> +12; -18,2 -> +19,2; -52,2 -> +53,3; -357,0 -> +360; -414 -> +417; -416 -> +419,2; -420,4 -> +424,5; -618 -> +623,2; -632,0 -> +639,5; -640,0 -> +652,36` |
| `Assets/CoreAiUnity/Tests/EditMode/FileConversationSummaryStoreEditModeTests.cs` | `-1 -> +1; -144 -> +144; -218 -> +218; -257 -> +257; -311 -> +311; -534 -> +534` |
| `Assets/CoreAiUnity/Tests/EditMode/FileSkillStoreAsyncEditModeTests.cs` | `-44,0 -> +45,57` |
| `Assets/CoreAiUnity/Tests/EditMode/MemoryContractRegressionTests.cs` | `-0,0 -> +1,627` |
| `Assets/CoreAiUnity/Tests/EditMode/MemoryContractRegressionTests.cs.meta` | `-0,0 -> +1,2` |
| `Assets/CoreAiUnity/Tests/EditMode/MemoryFileStoreRegressionTests.cs` | `-0,0 -> +1,174` |
| `Assets/CoreAiUnity/Tests/EditMode/MemoryFileStoreRegressionTests.cs.meta` | `-0,0 -> +1,2` |
| `Assets/CoreAiUnity/Tests/EditMode/ScopedAgentMemoryStoreDecoratorEditModeTests.cs` | `-249 -> +249; -254 -> +254,2; -322 -> +323` |
| `Assets/CoreAiUnity/Tests/EditMode/SkillContractRegressionTests.cs` | `-0,0 -> +1,153` |
| `Assets/CoreAiUnity/Tests/EditMode/SkillContractRegressionTests.cs.meta` | `-0,0 -> +1,2` |
| `dev-docs/audits/2026-10-23/02-core-memory-tools.md` | `-0,0 -> +1,238` |
| `dev-docs/fix-wave-2026-10-24/02-memory-skills.md` | `-0,0 -> +1,118` |

### Adjacent read-only joins and boundaries

- `Assets/CoreAI/Runtime/Core/Authority/ActorContext.cs`: trust, role/scope fields, bound marker and grant narrowing; `.../Features/Orchestration/AiTaskRequest.cs`: cloning/role retargeting; `QueuedAiOrchestrator.cs:1210-1285`: effective-scope capture; `AiOrchestrator.cs:230-290`, `:1600-1715`: deferred-summary preparation/commit/disposal. Primary ownership of execution remains group 01, including its manually composed empty-scope issue.
- `Assets/CoreAiUnity/Runtime/Source/Features/Llm/Infrastructure/MeaiLlmClient.cs:183-185`, `:552-554`, `:3008-3070`: one effective invocation list, proxy binding and native function expansion. `Assets/CoreAI/Runtime/Core/Features/Llm/ReadSkillLlmTool.cs`: catalog indexing/restriction, interactive discovery, descriptor metadata and static read/payload projection branches through line 390; the final availability helper was searched. These contextual reads support PR-MEM-002 without claiming provider-domain primary coverage.
- `Assets/CoreAI/Runtime/Core/Features/AgentMemory/MemoryLlmTool.cs`, `IConversationContextManager.cs`, `ConversationContextBuildArgs.cs`: declaration-only binding and context-manager contracts. `Assets/CoreAiUnity/Runtime/Source/Infrastructure/CoreAiWebGlPersistence.cs` and `.../Composition/CoreAILifetimeScope.cs:490-491`: immediate arming semantics and configured summary hooks.
- `Assets/CoreAiUnity/Docs/ARCHITECTURE.md` memory/runtime sections; `Docs/CoreAI/MULTIPLAYER_SESSIONS.md`; the user guide's actor/history/skill sections; targeted `SkillToolAvailabilityEditModeTests`, `SkillSetEditModeTests`, and `SkillLiveAvailabilityEditModeTests` declaration/discovery searches. Those other-owner paths are contextual reads rather than exhaustive primary audits.
- Historical audit/repair documents were read after the independent source pass. Their old findings and green-test counts were not adopted as current semantic or runtime proof. The report retains their explicit portable-versus-Unity distinction.

### Acceptance self-audit

- Read the real diff and relevant callers. Checked cancellation before effects, reset-versus-deferred-summary generations, queued commit disposal, alias/UTF-8 migration guards, residency eviction/refusal, callable alias narrowing, and replacement policy cleanup. No additional introduced defect was established in those paths.
- Confirmed source guards: admitted empty scope is distinct from an unbound provider fallback; transaction leases protect generation identity without retaining a file gate over an auxiliary model await; persisted-only cache eviction preserves transient/failed-write data; custom asynchronous skill confirmation rejects synchronous mutation before prepare/write/publication.
- The new regression fixtures were inspected for what they actually assert. The inspected fixtures do not cover the two new discovery/migration joins identified above. Their green status elsewhere cannot substitute for those scenarios.
- No unrelated production, test, project-setting, scene, prefab, metadata, Git, or shared coverage file was changed. The preexisting `QualitySettings.asset` diff and unrelated ETFX metadata remain outside this report's write scope.

## Unconfirmed hypotheses

- Cross-owner role-retarget hypothesis reviewed and not promoted: a previously bound actor is intentionally reused unchanged, including an admitted empty scope. `AgentMemoryActorScope.Bind` explicitly captures scope for subsequent turns/history; role remains separately encoded in the key. The only production `CloneForRole` caller found is the session inspector. No caller proving an unintended provider-derived scope handoff across roles was found, and automatically rebinding would violate the frozen-scope contract.

## Completion accounting

- CHANGED: this report only.
- VERIFY OUTPUT: 35 assigned changed paths and all 258 listed hunks reviewed; two source-confirmed P2 findings; zero counted P1/P3 findings. Final read-only checks found 35 ledger rows, zero missing/duplicate rows, two finding headings, zero trailing-whitespace lines, zero Cyrillic characters, and a final newline. `git rev-parse HEAD` returned the pinned review head; a path-scoped comparison against that head was empty. `git diff --check -- <report>` exited 0, with the direct content check supplying coverage for this untracked report.
- NOT DONE: builds, tests, Unity EditMode/PlayMode, player/IL2CPP/WebGL/device, fault/load/performance and real-model/provider gates; the proposed regressions were not written or run. Root owns adjudication, fixes, all execution checks, Git and publication.
- CONCERNS: ignored supplied migration confirmation and contradictory role-bound skill discovery metadata, as described in PR-MEM-001/002.
- QUESTIONS: none currently block read-only progress.
