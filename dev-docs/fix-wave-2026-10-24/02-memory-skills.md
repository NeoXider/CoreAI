# Memory, context, tools, and skills repair

Baseline: `69d5bcc37502cd4bb464a3bf9ac0c53b1624b804`. Target: 7.54.0.
Worker: `gpt-6.1-sol`; reasoning effort: `max`. Root owns Git, shared builds/tests, Unity/MCP, metadata and release files. Status: original focused portable gate and root-confirmed lifetime/CWD follow-up passed at portable Core level. Source/Unity and cross-owner joins remain open.

## Goal and invariants

Freeze caller-supplied memory context, preserve stored data, reject stale summary commits, retain narrow callable grants, propagate cancellation and report persistence outcomes honestly. Authentication/account lifecycle remains host/game owned. C# 9, explicit types, English prose and runtime/WebGL seams are preserved. No blocking storage lock spans an auxiliary LLM await.

## Per-ID dispositions

| ID | Revalidated verdict and change | Actual regression evidence / remaining join |
| --- | --- | --- |
| MEM-001 | Confirmed. CaptureActorScope binds the actor/provider scope once; frames distinguish frozen empty scope from an unbound fallback. | Portable local/named/frozen-empty cases passed. Execution owns ActorContext marker, direct/queue admission and end-to-end queue fixture. No cross-owner closure claim. |
| MEM-002 | Confirmed. Builder indexes advertised callable aliases and retains one-function narrowing. Binding identity comparison unwraps only the narrowing proxy for collision checks. | Author/create/restart/invoke with sibling denial and host-wrapper instruction edit passed. |
| MEM-003 | Confirmed. Memory/inventory preflight and separate cancellation rethrow. File effects confirm with CancellationToken.None after commit. | Portable pre-cancelled read, pending memory/inventory, policy boundary and post-commit cancellation passed. File store runtime needs Source/Unity receipt. |
| MEM-004 | Confirmed. Prior summary, framing and footer count against payload limit; impossible framing is rejected before an auxiliary request. | Long stored summary and impossible-budget cases passed alongside existing payload/fold cases. |
| MEM-005 | Confirmed host caller integration gap. Public trusted-actor Bind and provider-aware Enter are supplied. | Public seam key tests passed. Host service/facade/panel integration and real caller fixtures belong to host owner04. |
| MEM-006 | Confirmed. Optional ITransactionalConversationSummaryStore captures a generation and CAS commit. In-memory/file implementations invalidate on save/clear without retaining a file lock over model await. Prepared leases prevent generation ABA on path-state reclamation. Root follow-up adds deterministic preparation cleanup and explicit snapshot discard. | Memory/file reset and reverse-order commits plus gated auxiliary reset passed at the 236-case checkpoint. The 12 preparation exception/cancellation, discard/commit-disposal race and logical-key CWD regressions passed in the expanded root-run Core gate. Legacy third-party summary interfaces intentionally retain their documented weaker concurrency contract. |
| MEM-007 | Confirmed. Strict embedded versions/chat/transcript validation blocks replacement/migration and preserves recoverable source. Invalid outer memory does not prevent independent history append. Ordinary Save preserves unreadable memory until explicit Clear/repair. | New MemoryFileStoreRegressionTests corruption cases and changed explicit-recovery fixture require Source/Unity execution. |
| MEM-008 | Confirmed. Every logical disk key uses a full strict-UTF-8 SHA-256 key-v2 stem. Legacy identity is validated, ambiguous aliases/reserved canonical-looking stems are refused, original bytes remain in .legacy backups, conflicting copies are preserved. | Portable summary case/sanitizer alias, exact-scope/invalid-Unicode and migration conflict cases passed. File-memory equivalents require Source/Unity execution. Filesystem aliases/symlinks/multiprocess access remain unsupported host-coordination boundaries. |
| MEM-009 | Confirmed. BindForRole returns an invocation-local proxy; only declarative MemoryLlmTool targets receive a role-bound MemoryTool function, retaining source metadata and restrictions. | Portable store/role isolation, original unbound proxy and denied restriction passed. Providers owner03 must feed the same bound list to policy and native expansion. No real Meai integration claim yet. |
| MEM-010 | Confirmed. Sync FileSkillStore mutations with a custom async confirmation reject before prepare/write/publication. | Six false/pending create/update/delete cases added to FileSkillStoreAsyncEditModeTests; require Source/Unity execution. Engine-owned sync persistence remains supported. |
| MEM-011 | Confirmed. Shared leases include owners/queued waiters/prepared snapshots/pending effects. Registry admission is finite (1024 states); idle settled entries leave it. File-memory resident partitions default to 128 with configurable constructor cap, persisted-only eviction and refusal when unsaved data occupies capacity. File-skill directory leases settle admitted work on disposal. | Portable registry waiter, summary churn and observable/recoverable failed confirmation passed. File-memory cache/transient-refusal and skill-directory waiter/disposal cases require Source/Unity execution. |
| MEM-012 | Confirmed. Shared estimator expands callable name/description/schema descriptors from the already filtered tool list; no tokenizer parity claim. | Deterministic character-cost full/narrowed schema cases passed. Execution owns its duplicate reported-estimate call-site join. |
| MEM-013 | Confirmed. Operation admission rejects disposed readers, mutation leases protect the outer gate immediately, and instance resources release after admitted work settles. | Disposed chat/transcript reader and active mutator/peer waiter interleavings added; require Source/Unity execution. |
| CE-03 | Confirmed. Prepared replacement assigns/removes prompt, prefix opt-out and live catalog; stale skill proxies no longer carry into a replacement without skills. | Sync/async clearing and composed prompt cases passed; existing failed/cancelled replacement cases also passed. |

## Exact public/internal API handoffs

- Execution: internal `ActorContext AgentMemoryScopeKey.CaptureActorScope(ActorContext actor, IAgentMemoryScopeProvider provider, string roleId)` expects execution-owned `HasBoundMemoryScope` and `BindMemoryScope` preserving principal/grants. Call before direct/queue awaits.
- Host: public `ActorContext AgentMemoryActorScope.Bind(ActorContext actorContext, IAgentMemoryScopeProvider scopeProvider, string roleId)` and `IDisposable Enter(ActorContext actorContext, IAgentMemoryScopeProvider scopeProvider, string roleId)`. Reuse one bound trusted actor for operations addressing an admitted turn; this issues no new authority.
- Providers: public `ILlmTool CallSkillToolLlmTool.BindForRole(ILlmTool proxy, IAgentMemoryStore store, string roleId)`. Non-proxy tools return unchanged. Bind a single effective request list before BOTH policy and native expansion; roleId is the trusted admitted role.
- Execution estimate: public static `int DefaultContextBudgetPolicy.EstimateToolsTokens(IReadOnlyList<ILlmTool> tools, ITokenEstimator estimator)`.
- Snapshot ownership: public `ConversationContextSnapshot : IDisposable`. `Dispose()` is idempotent and nonblocking, preserves all projection properties, rejects later commits and releases owned generations only after admitted/queued commits settle. Managers transfer their generation to the snapshot or dispose it in `finally` on every failed/nontransferred preparation. Execution uses a `using` lifetime around commit/projection.
- Custom summary stores: optional public `ITransactionalConversationSummaryStore.OpenSummaryTransaction` / `OpenSummaryTransactionAsync` returning `ConversationSummaryTransaction`. Its public constructor accepts backend CAS delegates and an optional lifetime lease. Existing summary interfaces are unchanged.
- File-memory constructor adds trailing `int maxResidentHistories = 128`; public `ResidentHistoryCount`, `MaxResidentHistories`, static `SharedPathStateCount` expose retention. File summary exposes static `SharedPathStateCount` and `PendingConfirmationCount`. File skill exposes static `SharedDirectoryStateCount` and instance `HasPendingDurabilityConfirmation`.

## Verification receipts

The worker ran no build/test/Unity command. These root-run receipts were read directly:

1. `artifacts/fix-wave-20261024/execution-red-console.log`: initial integration compile stopped at 3 CS0122 binder errors before executing tests. Fixed with a nested encapsulated clone method. This is NOT a red product-test receipt.
2. `artifacts/fix-wave-20261024/memory-checkpoint-results/memory-checkpoint.trx`: 229 executed, 224 passed, 5 failed, zero skipped. Four failures were old fixture paths (`Role.json`) after versioned keys; one was a null-vs-empty initial memory assertion. Those fixture assumptions were corrected without relaxing effect/serialization assertions.
3. `artifacts/fix-wave-20261024/memory-final-results/memory-final.trx`: **236 executed, 236 passed, 0 failed, 0 skipped**, exit 0 and 922 ms in `memory-final-console.log`. This compiled portable Core and exercised both new portable fixtures plus focused compaction/summary/actor/budget/AgentBuilder/SkillSet fixtures. It did not compile Unity Source or run the Unity-only file stores.

4. `artifacts/fix-wave-20261024/core-focused-results/core-focused.trx`: root-run expanded gate **801 executed, 801 passed, 0 failed, 0 skipped**, exit 0 and approximately 3 s as reported by the lead. This is a mixed-module Core result, not 801 memory tests. Direct TRX test-definition matching confirms these relevant fixture counts, all Passed:

| Relevant fixture | Actual cases |
| --- | ---: |
| MemoryContractRegressionTests | 34 |
| SkillContractRegressionTests | 6 |
| AgentBuilderEditModeTests | 42 |
| AgentMemoryActorScopeKeyEditModeTests | 9 |
| ConversationContextCompactionEditModeTests | 80 |
| DefaultContextBudgetPolicyEditModeTests | 2 |
| FileConversationSummaryStoreEditModeTests | 18 |
| SkillSetEditModeTests | 47 |

These eight selected fixtures total 238 passed cases. The **12 lifetime/CWD follow-up cases are a subset of MemoryContractRegressionTests**, not additional to its 34. Execution's snapshot `using` integration compiled in this gate. Unity Source/file-memory/file-skill fixtures remain unexecuted by this portable receipt.

Root links `MemoryContractRegressionTests.cs` and `SkillContractRegressionTests.cs` in the portable test project. Focused filter: `FullyQualifiedName~MemoryContractRegressionTests|FullyQualifiedName~SkillContractRegressionTests`, plus existing focused fixture names selected by the lead. Source/Unity filters must include `MemoryFileStoreRegressionTests`, `FileAgentMemoryStoreEditModeTests`, `AgentMemoryConcurrencyEditModeTests`, and `FileSkillStoreAsyncEditModeTests`.

The lead authorized derived baseline fixtures at `artifacts/fix-wave-20261024/memory-red-fixtures/`. `CONVERSION.md` names physically excluded new-API cases and retained existing-API cases. Production fixtures contain no baseline-only conditionals. Root alone copies these into a fresh immutable HEAD snapshot and runs them. The root-run baseline receipt `artifacts/fix-wave-20261024/memory-red-results/memory-red.trx` was read directly: **17 executed, 16 failed, 1 passed, 0 skipped**, exit 1 and approximately 7 s as reported by the lead. The sole passing case is the committed-after-cancellation control (`MemoryMutation_CancellationAfterCommit_ReturnsCommittedSuccess`); the retained existing-API defect scenarios failed on immutable baseline `69d5bcc3`. This is the actual failing-before product receipt, separate from the earlier integration/fixture failures.

## Remaining gates and review notes

- Root must independently review the real diff and architecture changes, then execute Source compile, Unity EditMode and required optional configurations. New Core source files are included by portable globs; generated Unity csproj input may require regeneration/import. Metadata generation remains with the Unity operator.
- MEM-001/005/009/012 need their named foreign-owner joins checked before closure. Public seam tests do not establish host UI/provider end-to-end behavior.
- No Unity/player/WebGL/browser/device/performance/live-model receipt exists from this worker. A positive engine persistence answer is automatic persistence armed, not an IndexedDB completion acknowledgment.
- Direct `Save` now preserves corrupt source until explicit Clear/repair. The modified Unity regression records that recovery sequence; root owns acceptance of this deliberately stricter preservation contract.
- Summary XML now describes configured host confirmation/automatic-persistence arming, without a browser flush completion claim. Shared path lifetime prose reflects leased operations/generations/waiters/pending confirmation.

## Root adversarial lifetime/CWD follow-up

Confirmed new repair-owned gaps: transaction leases escaped cancellation/projection exceptions before ownership transfer; abandoned deferred snapshots lacked deterministic discard; in-memory generation identities accidentally depended on the filesystem working directory. The follow-up changes only those cleanup/identity contracts, their tests and documentation.

- `ConversationContextSnapshot` owns a summary lease, counts every admitted/queued commit, and defers disposal until commits settle. A failed/cancelled commit keeps its callback/generation for retry until explicit discard; readable projection properties survive disposal.
- Both deterministic preparation paths and LLM-assisted preparation use `try/catch/finally` transfer/disposal. No summary lock spans the auxiliary request.
- The shared registry accepts a key normalizer/comparer while preserving the file-path default. `InMemoryConversationSummaryStore` selects exact logical keys and ordinal comparison, so save/clear/CAS identity never resolves against the current directory.
- Twelve cases added to the already linked `MemoryContractRegressionTests`: sync/async projection exceptions; cancellation immediately after open for both managers; repeated auxiliary cancellation with immediate path-count assertions and no forced GC; auxiliary failure/fallback discard; cancelled-commit retry/discard; failed confirmation retry preserving committed bytes; disposal with active and queued commits plus an intervening reset; and isolated working-directory change cases for save/clear. The directory-changing cases are nonparallelizable and restore the original directory in `finally`.
- Root executed and this worker inspected the follow-up receipt above: MemoryContractRegressionTests has 34 Passed cases including all 12 new lifetime/CWD cases; SkillContractRegressionTests has 6 Passed cases. The earlier 236-case green result remains a historical checkpoint. No Source/Unity acceptance is inferred.

## Final source checkpoint

Runtime and fixture inventory follows (SHA-256 of exact file bytes) for the lifetime/CWD follow-up; the matching expanded portable Core receipt is recorded above. The derived baseline artifacts remain the original 17-case conversion; production fixtures contain no baseline-only macros.

```text
ac593d69f16b49f531fe31f0ebe7a8e603211c6fe7d72e31b87ca4662f2bc118  Assets/CoreAI/Runtime/Core/Features/AgentMemory/AgentBuilder.cs
a5c2034390baaf14e00e0a9cb0c25c9b1ded6337015bebd3afaff3a3881084a8  Assets/CoreAI/Runtime/Core/Features/AgentMemory/AgentMemoryFileKey.cs
f738d13da31b20bdebab554a8a37358bdfcc5c4b771b57c8bc8926490165e70f  Assets/CoreAI/Runtime/Core/Features/AgentMemory/AgentMemoryPolicy.cs
1a0ec79803b5bd14d0dccc18a046f4bb2ee361cfcbe3599f9b76bfbbbd215dfd  Assets/CoreAI/Runtime/Core/Features/AgentMemory/AgentMemoryScope.cs
b76b4dbcf0ff6cec218e79d38324f3ecdd21a50a6a62019b6e44f7e285a21175  Assets/CoreAI/Runtime/Core/Features/AgentMemory/Context/DefaultContextBudgetPolicy.cs
e8c55ff3c51eef14d28237e99dfd6609ca8aaa2cabc30b8655ac71f3ecff2d98  Assets/CoreAI/Runtime/Core/Features/AgentMemory/ConversationContextSnapshot.cs
30c01a52911e41ce38027f83ea93cb30c7f24abb9e1e3cd04c09d5a40460aa0b  Assets/CoreAI/Runtime/Core/Features/AgentMemory/ConversationSummaryTransaction.cs
dc4589f5f690528b891f6522e841f49c0557360513b6bf8662f602065c432ea9  Assets/CoreAI/Runtime/Core/Features/AgentMemory/DeterministicConversationContextManager.cs
dd61d3f0b2dc717fc1abb79ff2c5d8b3aafa92dddec7a82beeb4ab4af5a02ea2  Assets/CoreAI/Runtime/Core/Features/AgentMemory/FileConversationSummaryStore.cs
2e74c30b3d6441dec47589aeb5ced4d12d276f8907199c06cf5652debdddce86  Assets/CoreAI/Runtime/Core/Features/AgentMemory/InMemoryConversationSummaryStore.cs
9fa634276c8c0760839f7c0664d8bd3677aaf8dbc507d2b5c0728187045b23d8  Assets/CoreAI/Runtime/Core/Features/AgentMemory/InventoryTool.cs
14e1834a038cb5360d67685b413ab885bf3655bd0a3897f45dc9ebaab8419108  Assets/CoreAI/Runtime/Core/Features/AgentMemory/LlmAssistedConversationContextManager.cs
ae5964db233c6eea0bb8a86b933f2e6f51d0c8cd21fc2d4289bd809f6b89aaab  Assets/CoreAI/Runtime/Core/Features/AgentMemory/MemoryTool.cs
77858dfb900a3db0238a5f27888f9868b2971c2cc44d0dde5cb03f94da66fc0e  Assets/CoreAI/Runtime/Core/Features/AgentMemory/ScopedConversationSummaryStoreDecorator.cs
a650f43428dbbd51b65f518a8c09793781308c631dff1a133924901f48973bb3  Assets/CoreAI/Runtime/Core/Features/AgentMemory/SharedPathStateRegistry.cs
2188066b978eb4cd85868c69426cba5d13f290371c2cd80ba9f9cc75facd8ab6  Assets/CoreAI/Runtime/Core/Features/Llm/CallSkillToolLlmTool.cs
dceeb8e2ed2eb3af1fcb66c32cedeb1459d03d0bcfbc058bb819ae4816bb83f4  Assets/CoreAI/Runtime/Core/Features/Llm/SkillSetToolResolver.cs
1d0f3d107c4ded0f2408e105d54342be4b25940367f8410470bf65d0db3c5b54  Assets/CoreAiUnity/Runtime/Source/Features/AgentMemory/Infrastructure/FileAgentMemoryStore.cs
e8bd9dd8de915aef9fa5d029515cc0023b924fdbcc63ce83f983cfef90c60b61  Assets/CoreAiUnity/Runtime/Source/Features/Llm/Infrastructure/FileSkillStore.cs
e3d48d9938a55f94feaec47c39e8955454dbe4981bc19096bbdd865bc0bc5318  Assets/CoreAiUnity/Tests/EditMode/FileAgentMemoryStoreEditModeTests.cs
9844bf7b95024d7622edadf7c1ad1f398f147563b0210ae893c4b20f54f35c1f  Assets/CoreAiUnity/Tests/EditMode/FileConversationSummaryStoreEditModeTests.cs
0a3eca320a5a0ae0914f8b68499049a19d7caafa95f4dfeb7546903158db1727  Assets/CoreAiUnity/Tests/EditMode/FileSkillStoreAsyncEditModeTests.cs
6acdc71fc982f0a3f6339362cce69633ef63b5a2bb18301af8d8a6652ea80bc9  Assets/CoreAiUnity/Tests/EditMode/MemoryContractRegressionTests.cs
c874b3365beacabafc79e441d78a971e908affe5ed3ca17df82b0e4c92d150eb  Assets/CoreAiUnity/Tests/EditMode/MemoryFileStoreRegressionTests.cs
1db24504f9ea7d12990ce2eae38dcbe72c3ea3df9b7017d8087afcc68bee74db  Assets/CoreAiUnity/Tests/EditMode/SkillContractRegressionTests.cs
```

Inventory fingerprint: `345ac857ad1571080b9b7cff3f7edcef3910c149fe6450f8610a6b7aadc45a43`.

Additional authored files: `Assets/CoreAI/Docs/AGENT_BUILDER.md`, this work log, and the three derived baseline artifact files. No git, package manifest, changelog, csproj, asmdef, meta, scene, prefab or Unity/MCP mutation was performed by the worker.
