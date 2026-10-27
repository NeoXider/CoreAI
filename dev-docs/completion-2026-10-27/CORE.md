# Core portable runtime adversarial audit — 2026-10-27

Status: source review complete; root execution and release acceptance remain pending. Reviewer: native GPT-6.1 Sol, max reasoning; independent of implementation. Baseline: `demo`, `bc3fd74f0a889bfe1b1e7babc6bc02ed48a7c05e` (published 7.54.0). Review includes tracked changes and untracked source under `Assets/CoreAI/Runtime`.

Goal: verify request identity and authentication, portable/server boundaries, ownership and failure behavior, source-edit contracts, persistence receipts, and bounded queue behavior. Review building-block framework contracts rather than one game's policies.

Scope: source read only; no source mutation, Git mutation, builds, tests, Unity, network, or model calls. The owner explicitly requested retained audit Markdown, overriding the repository's historical report-deletion convention. Evidence and acceptance gaps will be recorded separately.

## Findings

No additional confirmed Core runtime P1/P2/P3 finding in the reviewed source. This is a source-review result, not a full release pass.

One Lua editor integration defect was discovered across the portable utility and its Mods caller: the outer Lua context accepts `local n = #f`, while `AutoClosePair` performs a second Python-only context scan and declines to add the closing parenthesis. The editor reviewer accepted ownership of this P3 finding and its regression proposal. Its authoritative entry is in [EDITOR_REVIEW.md](EDITOR_REVIEW.md); it is not counted again here. The defect is in language composition rather than the Python scanner's own contract.

## Review decisions and source evidence

### Request identity, credentials and portable boundaries

- Read every new file in `Core/Features/Llm/Gateway`, the two changes in `MeaiOpenAiChatClient`, and corresponding existing transport/header seams. The gateway assembly path is guarded by `COREAI_LLM`; it depends on portable contracts and MEAI rather than Unity composition. The default side-service transport is excluded from the WebGL construction path, which explicitly requires a host transport.
- `GatewayAuthorizationSettings` rejects a configured provider API key, keeps gateway bearer lookup dynamic, and snapshots custom headers, logical request/idempotency IDs and the selected model per invocation (`GatewayAuthorizationSettings.cs:22-59,88-113`). Reserved auth and correlation names cannot be injected through additional headers. Explicit scoped null identity suppresses the legacy global provider in `MeaiOpenAiChatClient`, rather than silently consulting a different account.
- `GatewayChatClient.Context` copies request/profile context, then `BindInvocation` replaces policy/telemetry correlation with the same IDs used on the wire and pins policy model selection to the configured wire model (`GatewayChatClient.cs:217-236`). Host messages and policies remain caller-owned contracts; this client does not authenticate a multiplayer actor or replace server authorization.
- Read the buffered and streaming auth refresh paths (`GatewayChatClient.cs:64-195`). Refresh is limited to one raw request replay. Streaming replay is refused once text or another visible/tool content commits, and retry-suppression state is honored. Tool execution stays outside this raw-model middleware; the gateway cannot replay an already executed tool loop on its own.
- Valid entitlement denials remain denials, including unknown/empty reason codes. Unavailable/invalid/non-success side-service responses follow the explicitly configured availability policy. These optional client checks are not the billing or server admission boundary. This distinction matches the actual `GatewayServiceClient` v1 protocol rather than assuming every HTTP error body contains a valid entitlement result.
- `GatewayChatClient` admits at most the configured concurrent optional usage reports, observes late faults, uses an independent reporting deadline and preserves the primary outcome when telemetry fails (`GatewayChatClient.cs:305-373`). An uncooperative injected sink can retain its admitted slot, but it cannot cause an unbounded number of pending reports. Owned service resources and the inner client are retired on disposal; caller transports remain borrowed.
- Read all of `HttpClientGatewayTransport`: per-call cancellation/timeout, response-byte cap, strict UTF-8 decoding, response/stream disposal and redirect refusal are present. Injected HttpClients are explicitly required to disable redirects; the post-response URL check is not treated as a substitute for that host obligation.

### Scope capture, public API and skill bindings

- Reviewed every memory/scope and orchestrator diff, including known-provider discovery, trusted actor checks, bound Empty scope, explicit nonempty scope, unknown provider behavior, enqueue capture and unstarted-turn persistence context. Unknown custom composition retains the documented weaker legacy behavior instead of acquiring a fake authoritative Empty snapshot.
- Read the restored legacy constructor plus required full constructor and `CreateWithMemoryScope` convenience in `AiOrchestrator`. The exact legacy API repair is an explicit root design decision in `18-actor-repair.md`; candidate-only shorthand migration is not reported as an accidental ABI defect. Compile/delegate acceptance still belongs to root's actual checks.
- `RoleBinding` is invocation-owned; `read_skill` and `call_skill_tool` share the same bound descriptor snapshot and memory function. Rebinding does not mutate the catalog or widen the existing allowlist. Non-memory targets preserve their original descriptor/function identity, and invocation performs structural preflight against the bound function.
- The live skill-authoring path constructs replacement SkillSet objects before `MutableSkillCatalog.AddOrReplace`; the new per-invocation descriptor cache therefore does not hide that production replacement path. Arbitrary host mutation of an existing SkillSet without publishing catalog version changes was not promoted to a new regression.
- Equal role IDs across different actors are valid framework composition. Host-selected actor/session policy is not inferred from a role name.

### Migration receipts and resource lifetime

- Read the entire changed `FileConversationSummaryStore` and the `AgentMemoryFileKey` migration helper. Migration records canonical-copy/legacy-rename effects even when a later operation fails. Summary paths record a pending generation before releasing their file gate and settle the confirmation outside it, preserving the primary failure if confirmation also fails.
- Async read/save/clear/transaction paths retain shared path ownership while confirmation is awaited; confirmation uses no caller cancellation after an owned mutation. Synchronous calls reject async-only confirmation before migration effects and fail promptly when file work is busy. Same-file recursive durability callbacks are refused explicitly.
- Generation checks prevent an older callback from acknowledging a newer mutation. Transaction snapshot creation rereads after confirming a pending generation. Missing storage, corrupt storage, identity mismatch, failed confirmation and absent-file delete retries remain distinct paths.
- These are local mutation and configured-confirmation contracts. Neither a filesystem move nor the WebGL arming answer is described as a browser flush receipt.

### Portable editor contracts and queue bounds

- Read all seven new CodeEditing files, including the full text utility and highlighter. The document/request size caps, independent language drafts/revisions/selections, result identity fence, copied/bounded diagnostics, bounded combined undo/redo history and bounded completion scan are present.
- The Python interpreter stub has no runtime/store dependency, reports `Unsupported`, and returns `Executed=false`/`Persisted=false`. Formatting and highlighting do not execute Lua or Python. A custom language profile remains a host extension and is not assumed hostile code.
- Read the final `AsyncChunkQueue.WriteAsync` fast path and complete existing blocked writer, oversized handoff, completion, take and signal paths (`QueuedAiOrchestrator.cs:1733-1923`). Free-capacity writes perform the same capacity/byte/completion checks under the existing signal lock, then signal the reader. Cancellation and slow-consumer backpressure remain on the original cold async path. The queue is explicitly single-producer/single-reader; simultaneous independent writers are not an invented supported topology.
- Source inspection finds no new closure or async-state creation on that free-capacity path. This observation and allocation regression receipts do not establish production stream throughput or device frame time.

## Source identity and scope inventory

The read-only inventory identifies 28 changed/new C# files in this package. The source manifest is the ordinally sorted list of `repository-relative-path|lowercase-file-SHA256`, joined with LF and no terminal LF, encoded as UTF-8 without BOM. Manifest SHA-256: `6eedc79c53c6892473c5ff0a862d499d3cedcf76770961def66e17f681e2728e`.

| Area | Exact reviewed changed/new files |
| --- | --- |
| AgentMemory | `AgentMemoryFileKey.cs`, `AgentMemoryScope.cs`, `FileConversationSummaryStore.cs`, `IAgentMemoryScopeProvider.cs`, `ScopedAgentMemoryStoreDecorator.cs` |
| CodeEditing | `CodeEditorContracts.cs`, `CodeEditorHistory.cs`, `CodeEditorTextUtility.cs`, `CodeLineSelection.cs`, `CodeSyntaxPalette.cs`, `PythonSyntaxHighlighter.cs`, `SyntaxHighlightText.cs` |
| Llm/Gateway | `GatewayAuthorizationSettings.cs`, `GatewayChatClient.cs`, `GatewayClientFactory.cs`, `GatewayClientOptions.cs`, `GatewayEntitlementPolicy.cs`, `GatewayRequestContext.cs`, `GatewayServiceClient.cs`, `GatewayUsageSink.cs`, `HttpClientGatewayTransport.cs`, `IGatewayAuthRefresher.cs`, `IGatewayHttpTransport.cs` |
| Llm | `CallSkillToolLlmTool.cs`, `MeaiOpenAiChatClient.cs`, `ReadSkillLlmTool.cs` |
| Orchestration | `AiOrchestrator.cs`, `QueuedAiOrchestrator.cs` |

Representative full file hashes:

- `CodeEditorContracts.cs`: `bf90922bb54187809537b3e4d517d9e991cdb9b49df12d6c99a588d5bf7333ff`.
- `GatewayChatClient.cs`: `10a2b893d7892dc3e265090e4b7975a3c8cf72a9f59222f50caaa0d0cd54081b`.
- `FileConversationSummaryStore.cs`: `eb637f83ce3fa57e99b0fa93504263273babbf5a9931461bcdef037d3be0b2ce`.
- `QueuedAiOrchestrator.cs`: `ec53efc6cb85c49b9e61d5239ad6654fe773601c7db10306d263c8224226b8c9`.

Any later source correction requires a new hash and review of that delta; these hashes do not silently track future edits.

## Evidence and remaining gates

Read and counted actual TRX `UnitTestResult` nodes rather than trusting summary counters:

| Existing root receipt | Observed results | Meaning |
| --- | --- | --- |
| `artifacts/completion-20261027/core-final/core-final1.trx` | 2,954 Passed; 3 NotExecuted | Saved portable Core run, not a native/player gate |
| `artifacts/completion-20261027/core-perf/core-perf-green-debug1.trx` | 111 Passed | Saved focused portable Debug regression run |
| `artifacts/completion-20261027/core-perf/core-perf-green-release1.trx` | 111 Passed | Saved focused portable Release regression run |

The reviewer read the gateway, editor contract and invocation tests as coverage evidence and performed read-only diff/whitespace inspection. No reviewer build/test was executed, and these saved receipts are not cryptographically tied to the source manifest above.

NOT DONE: root's source-final native focused/full tests; actual Unity API PlayMode acceptance through OpenRouter; interactive editor/Hub input; built-player, IL2CPP, WebGL/browser, device, remote-network/load and profiling gates. A direct OpenRouter HTTP response is not Unity acceptance. The three NotExecuted results remain exclusions.

CHANGED: `CORE.md`, `MODS.md`, `PR_FINDINGS_CORE_MODS.md` only. VERIFY OUTPUT: source/diff reads, scope/hash inventory, actual saved TRX accounting. CONCERNS: editor P3 is owned by the editor review; independent execution gates remain pending. QUESTIONS: none.
