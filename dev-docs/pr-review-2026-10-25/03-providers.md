# Independent provider PR review

- Reviewer: `/root/pr_providers`, native `gpt-6.1-sol`, `max` reasoning.
- Immutable range: `69d5bcc37502cd4bb464a3bf9ac0c53b1624b804..bc3fd74f0a889bfe1b1e7babc6bc02ed48a7c05e`.
- Primary scope: all 26 paths in coverage group `03` (2,112 additions / 322 deletions).
- State: source review complete. One actionable P2 regression; uncounted gaps are separate below. Only this report is writable; source, tests, assets, and Git remain untouched.

## Goal and review decisions

The goal is to identify introduced provider, transport, streaming, endpoint-lifetime, and test defects with changed-line causality. Typed failures, usage receipts, committed tool effects, visible output, bounded WebGL work, cancellation, source compatibility, and host-owned identity are load-bearing invariants. Review reads the complete assigned diff and the surrounding implementation; adjacent callers are sampled as needed and are not claimed as a separate full-module audit.

Before accepting a finding, the review requires a concrete trigger, expected versus actual behavior, a changed-line cause, and a meaningful regression plan. Old audit claims and test receipts are context only. Build, test, Unity, browser, network, and model execution are deliberately not authorized for this worker.

## Findings

### PR-LLM-001 [P2] - Apply disabled-tool mode before binding skill proxies

Changed-line anchor: `Assets/CoreAiUnity/Runtime/Source/Features/Llm/Infrastructure/MeaiLlmClient.cs:183` (the streaming equivalent is `:552`; the new helper calls the binding API at `:3016`). Confidence: high from source; no executable reproduction has been run by this worker.

Trigger: construct `MeaiLlmClient` with an `IAgentMemoryStore`, include a normal `CallSkillToolLlmTool.Create(...)` proxy in `request.Tools`, leave the public request's default `AgentRoleId` unchanged, and set `ForcedToolMode = LlmToolChoiceMode.None`. This is a plain model request with tools disabled; no memory/tool invocation or admitted role is needed for it.

Expected: send one tool-free request and return the provider's text. Actual: both completion paths unconditionally call the new `BuildInvocationTools` before checking `ForcedToolMode.None`. `CallSkillToolLlmTool.BindForRole` rejects the default empty role at its lines 64-69, so the request throws `ArgumentException` before the provider is called. The default empty role is declared at `ILlmClient.cs:473`; the ordinary-request fixture already exercises an unset role in `MeaiLlmClientEditModeTests.cs:23-32`.

The same ordering also violates the existing disabled-factory invariant for a valid role: rebinding constructs a new `CallSkillToolProxy`, whose constructor validates the catalog and builds its tool map (`CallSkillToolLlmTool.cs:149-157`). Those paths call nested `IAIFunctionLlmTool.CreateAIFunction` / `IAIFunctionsLlmTool.CreateAIFunctions` through `SkillSetToolResolver`, even though the model is forbidden to use any tool. This work did not occur on the old no-tools branch. The existing `NoneToolMode_NeverExecutesOrBuildsBindingsAcrossChannels` test explicitly requires zero unused binding attempts (`MeaiLlmClientEditModeTests.cs:133-135`), but its request has only direct tools and its client has no memory store, so the new proxy branch is absent.

Invariant and cause: an explicit no-tool request must not depend on any tool binding, role binding, or unused factory. The added unconditional call bypasses the preexisting `ForcedToolMode.None` guard. This is a provider integration regression, not a request to invent a host login or identity policy.

Regression proof plan for root: extend `MeaiLlmClientEditModeTests` in both native/text and completion/streaming modes. Supply a fake memory store and a skill proxy containing a counting binding factory. Reset its counter after proxy construction; make a `None` request with the default empty role. Assert one provider call, the unchanged plain response, zero executed traces, and zero additional factory calls. Repeat with an explicit valid role to isolate unused-factory construction from role validation. Before the fix the empty-role case fails before the provider and the valid-role case increments the factory counter; after the fix both paths satisfy the original no-tools contract. Keep the effective invocation tool list shared for enabled-tool requests.

## Cross-owner context and uncounted gaps

- The role-bound memory metadata mismatch is independently confirmed but not duplicated here: `ReadSkillLlmTool.cs:221-233` derives `invocable` from the unbound descriptor, while `CallSkillToolLlmTool.cs:241,358-364` can make the same declarative memory target callable after provider binding. Root was notified so the memory owner can decide its canonical finding.
- The new SSE validator does not recursively validate every supported message/reasoning content shape. The parser deliberately accepts strings and arrays for some compatibility fields. Malformed nested values and nullable usage counters need focused compatibility fixtures before a new priority is assigned; no unsupported blanket restriction is proposed here.
- The JavaScript fixtures check the actual bridge source with fake browser operations. They do not execute the managed callback/acknowledgement boundary or WASM ABI. Managed/player slow-reader overflow, terminal/cancellation reentry, memory allocation, and real-network throughput remain separate gates.
- The preexisting unbounded SSE line/tool-argument buffers remain a separate budget-contract question. A bounded transport backlog does not establish a parser-line or argument-size bound; this PR does not make that claim into a newly proven runtime defect.

## Coverage

All assigned additions/deletions were read, including the complete 754-line new regression fixture and every new test assertion. The hunk column uses `git diff --unified=0`, so adjacent edits can be separate hunks. Total: 26/26 paths, 202/202 changed hunks, +2,112/-322 lines. This is diff coverage, not execution or whole-module semantic coverage.

| Primary changed path | Hunks | + / - | Review result |
| --- | ---: | ---: | --- |
| `Assets/CoreAI/Runtime/Core/Features/Llm/LoggingLlmClientDecorator.cs` | 5 | 10 / 3 | Typed failure bookkeeping and synthetic completion reviewed; no additional introduced finding. |
| `Assets/CoreAI/Runtime/Core/Features/Llm/MeaiOpenAiChatClient.cs` | 6 | 78 / 5 | JSON reader, redacted validation, extraction catches and parser callers reviewed; nested compatibility gaps remain uncounted. |
| `Assets/CoreAI/Runtime/Core/Features/LlmRouting/LlmRouteResolver.cs` | 1 | 1 / 0 | Trimming matches the matching predicate; no additional introduced finding. |
| `Assets/CoreAI/Runtime/Core/Features/Orchestration/ILlmClient.cs` | 3 | 7 / 14 | Failure/default adapter metadata, text, reasoning, usage and receipts reviewed against retry/fallback consumers. |
| `Assets/CoreAiUnity/Docs/HTTP_TRANSPORT_SPEC.md` | 2 | 22 / 0 | All new contracts read against the corresponding source; no runtime pass inferred. |
| `Assets/CoreAiUnity/Docs/STREAMING_ARCHITECTURE.md` | 2 | 14 / 0 | All new contracts read; browser/WASM limitations are correctly separate from Node mocks. |
| `Assets/CoreAiUnity/Editor/CoreAISettingsAssetEditor.cs` | 3 | 4 / 3 | GUID loading/fallback reviewed; the referenced layout GUID matches its metadata. |
| `Assets/CoreAiUnity/Editor/CoreAIWebGlStreamingAssetsGuard.cs` | 33 | 131 / 93 | Complete changed journal, exclusion, recovery, target/path and metadata branches reviewed with build hooks. |
| `Assets/CoreAiUnity/Runtime/Plugins/WebGL/CoreAiSseFetch.jslib` | 26 | 134 / 27 | Complete bridge reviewed: bounds, acknowledgements, timeout, HTTP body read, callback failure, reader/controller abort and terminal cleanup. |
| `Assets/CoreAiUnity/Runtime/Source/Features/Llm/Infrastructure/FallbackLlmClientDecorator.cs` | 4 | 9 / 2 | Complete streaming commitment/error flow reviewed; no additional introduced finding. |
| `Assets/CoreAiUnity/Runtime/Source/Features/Llm/Infrastructure/FetchSseOpenAiTransport.cs` | 43 | 86 / 85 | Complete guarded WebGL source reviewed, including ABI, callback/state, cancellation, read, acknowledgements and disposal. |
| `Assets/CoreAiUnity/Runtime/Source/Features/Llm/Infrastructure/FetchSseTransportProtocol.cs` | 3 | 94 / 0 | Complete new error/backlog helpers reviewed; partial-fragment accounting remains charged until full acknowledgement. |
| `Assets/CoreAiUnity/Runtime/Source/Features/Llm/Infrastructure/LlmClientRegistry.cs` | 20 | 67 / 45 | Every changed lease/retirement hunk read with resolution, activation, replacement, removal, deactivation and drain paths. |
| `Assets/CoreAiUnity/Runtime/Source/Features/Llm/Infrastructure/MeaiLlmClient.cs` | 11 | 24 / 8 | Every effective-tool-list use reviewed with proxy binding and no-tool mode; PR-LLM-001. |
| `Assets/CoreAiUnity/Runtime/Source/Features/Llm/Infrastructure/RefreshOnUnauthorizedDecorator.cs` | 16 | 31 / 24 | Complete completion/stream refresh and cancellation paths reviewed; original auth chunks and committed receipts retained. |
| `Assets/CoreAiUnity/Runtime/Source/Features/Llm/Infrastructure/RoutingLlmClient.cs` | 4 | 14 / 6 | Error classification read with completion/usage/health publication and early disposal; no additional introduced finding. |
| `Assets/CoreAiUnity/Tests/EditMode/CoreAIWebGlStreamingAssetsGuardEditModeTests.cs` | 1 | 99 / 0 | Every new journal fault/conflict/idempotency/target assertion reviewed with original fixture setup/teardown. |
| `Assets/CoreAiUnity/Tests/EditMode/LlmConfigurationDefectsEditModeTests.cs` | 6 | 16 / 3 | Ownership helper and all changed call sites reviewed; assertions are retained. |
| `Assets/CoreAiUnity/Tests/EditMode/MeaiOpenAiChatClientSseEditModeTests.cs` | 1 | 56 / 0 | All malformed/control/date cases reviewed with their real fake transport/stream implementation. |
| `Assets/CoreAiUnity/Tests/EditMode/MeaiOpenAiChatClientStreamHotPathEditModeTests.cs` | 3 | 9 / 1 | Removed malformed-null expectation is replaced by typed-failure coverage; no weakened success assertion. |
| `Assets/CoreAiUnity/Tests/EditMode/ProviderContractRegressionTests.cs` | 1 | 754 / 0 | Full new fixture and all fake implementations read, including concurrency, retirement, reentry, early disposal and code-only outcomes. |
| `Assets/CoreAiUnity/Tests/EditMode/ProviderContractRegressionTests.cs.meta` | 1 | 2 / 0 | Entire addition read; GUID uniqueness searched. |
| `Assets/CoreAiUnity/Tests/Node~/fetch_sse_jslib_test.js` | 4 | 127 / 3 | Every changed harness/scenario/assertion read; virtual clocks and fake-reader limits checked. No execution claimed. |
| `dev-docs/audits/2026-10-23/03-core-providers-streaming.md` | 1 | 221 / 0 | Entire prior report read as historical source context, not accepted proof that repairs work. |
| `dev-docs/fix-wave-2026-10-24/03-provider-intake.md` | 1 | 30 / 0 | Entire work record read; acceptance obligations reconciled against source. |
| `dev-docs/fix-wave-2026-10-24/03-providers.md` | 1 | 72 / 0 | Entire work record read; prior done/green claims were not used as current validation. |

Adjacent context was targeted sampling, not full ownership coverage: `CallSkillToolLlmTool`, `ReadSkillLlmTool`, `SkillSetToolResolver`, `MemoryTool`, `RetryingStreamingLlmClientDecorator`, request defaults, existing `MeaiLlmClientEditModeTests` no-tool tests, SSE read/terminal/message/usage helpers, test fake streams, registry activation/release/route helpers, and editor restore hooks. The complete implementation of orchestration, memory stores, host account lifetimes, native LLMUnity, every provider serializer, all package tests and unassigned files was not audited.

## Test assessment and regression handoff

The new provider tests use real decorators with deterministic clients and check typed outcomes/receipts rather than only method names. The new registry fixture tests admission after four retirement operations, cached-handle generation rejection, concurrent per-operation lease accounting, early disposal/cancellation, and synchronous release-hook reentry. Its drain case proves the host is not released while an operation remains admitted; it does not by itself prove a later player-loop drain callback runs. The JavaScript fixture tests outstanding byte/chunk counts and cancellation against fake browser primitives, while the managed `FetchSseBacklog` test checks only the pure helper. These are useful but distinct layers.

No changed assertions were found that simply suppress a failing expected provider behavior. The missing binding scenario in PR-LLM-001 is an unrepresented cross-product of existing contracts. The existing portable project links the new provider fixture, but does not link `MeaiLlmClientEditModeTests`; a portable filter matching no tests cannot establish this regression's fix. Root must run its new regression through the Unity/Source test lane with `COREAI_LLM`, then run the existing no-tool cases for both completion modes and both tool protocols. Other existing checks remain relevant: `ProviderContractRegressionTests`, `MeaiOpenAiChatClientSseEditModeTests`, `CoreAIWebGlStreamingAssetsGuardEditModeTests`, and the Node bridge harness.

## Verification limits

Read-only Git inspection confirmed `demo` at the immutable head. The per-path immutable diff inventory returned exactly the 26 assigned paths and the counts above. Existing unrelated changes in `ProjectSettings/QualitySettings.asset` and the ETFX metadata remain untouched. No builds, tests, Unity operations, browser/network requests, model calls, Git mutation or source/asset edits have been run by this worker. A mistaken literal-glob lookup and one nonexistent `MemoryTool` path were corrected with `rg --files`; those navigation failures are not verification results.

Direct report read check exited 0: `REPORT_READ_CHECK lines=80 coverageRows=26 trailingWhitespace=0 cyrillicLines=0`. This is document validation only. The final check still resolved the immutable head to `bc3fd74f0a889bfe1b1e7babc6bc02ed48a7c05e`.
