# MCP independent PR review

Status: complete static PR review; header was written before source review.

Repository: `D:/Git/CoreAI`; branch: `demo`.
Baseline: `69d5bcc37502cd4bb464a3bf9ac0c53b1624b804`.
Review head: `bc3fd74f0a889bfe1b1e7babc6bc02ed48a7c05e`.
Primary scope: exactly group `10` in `coverage.json` (13 paths).
Authority read: `AGENTS.md` and `Docs/ARCHITECTURE_RULES.md`.
Method: pinned read-only source/diff inspection; adjacent contracts only when needed.
No builds, tests, Unity/MCP/provider calls, Git mutations, or further agents.
Only this report is writable. Historical reports are reviewed as claims, not proof.

## Primary coverage

All 13 primary paths and every changed hunk were read. Counts below use `git diff --unified=0` between the pinned commits (52 hunks total); review used surrounding source as well. Runtime files were read completely. Test review covered every addition, its assertions/fakes, fixture setup/cleanup and relevant existing lifecycle helpers; unrelated unchanged test cases were sampled.

| Path | Hunks | Review focus / outcome |
| --- | ---: | --- |
| `Assets/CoreAIMcp/README.md` | 6 | Ownership/retirement, callback restart, parsing, errors, URLACL and evidence claims. Callback guarantee affected by PR-MCP-001. |
| `Assets/CoreAIMcp/Runtime/Protocol/McpMethods.cs` | 1 | Version matches the head MCP manifest at `7.54.0`. |
| `Assets/CoreAIMcp/Runtime/Server/CoreAiMcpServer.cs` | 20 | Admission, retirement race, stop/restart ownership, transport identity checks, failed startup and unchanged public signatures. PR-MCP-001. |
| `Assets/CoreAIMcp/Runtime/Server/McpHttpServer.cs` | 4 | Interface/prefix extraction plus real start, cancellation, listener close, handlers and notification teardown. PR-MCP-001 crosses its stop ordering. |
| `Assets/CoreAIMcp/Runtime/Tools/CoreAiToolsBrokerMcpTool.cs` | 2 | Shared parser, object/depth/trailing-content failures, exact strings and captured target/arguments. No additional defect established. |
| `Assets/CoreAIMcp/Runtime/Tools/GetModLogsMcpTool.cs` | 1 | Inner lowercase boolean, unchanged content, failure and cancellation propagation. No additional defect established. |
| `Assets/CoreAIMcp/Runtime/Tools/WorldCommandMcpTool.cs` | 1 | Inner PascalCase boolean, parameter rejection, executor refusal and cancellation path. No additional defect established. |
| `Assets/CoreAIMcp/Tests/EditMode/CoreAiMcpServerMainThreadEditModeTests.cs` | 2 | Fake bind denial, exact prefix, disposal, admission/deadline context. Assertions do not establish actual OS binding behavior. |
| `Assets/CoreAIMcp/Tests/EditMode/McpDynamicToolsEditModeTests.cs` | 1 | Direct/broker/direct-invocation timestamp cases, strict string fake and invalid/deep JSON zero-invocation assertions. |
| `Assets/CoreAIMcp/Tests/EditMode/McpRpcDispatcherEditModeTests.cs` | 6 | Both routes, content equality, query/executor counts, ordinary failures versus cancellation, helper/fake behavior. |
| `Assets/CoreAIMcp/Tests/PlayMode/CoreAiMcpServerResidencyPlayModeTests.cs` | 6 | All six added lifecycle tests, real existing HTTP helpers, host isolation/retirement and release assertions. In-memory restart misses PR-MCP-001. |
| `dev-docs/audits/2026-10-23/09-mcp.md` | 1 | Entire historical report; its four findings, hypothesis and old execution claims are historical context, not proof at the review head. |
| `dev-docs/fix-wave-2026-10-24/09-mcp.md` | 1 | Entire repair handoff; source/test claims checked against the pinned changes. Callback restart claim exceeds the real transport repair. |

## Findings

One actionable finding: P2. No P1 or P3 finding established.

### PR-MCP-001 [P2] Reentrant restart binds before the stopped HTTP listener releases its prefix

- Changed anchor: `Assets/CoreAIMcp/Runtime/Server/CoreAiMcpServer.cs:415` (head above; preceding state detachment at lines 405-413).
- Valid trigger: with a built CoreAI composition, start the automatic host with the real HTTP transport, register/start a cooperative custom tool, register a synchronous callback on its supplied cancellation token that calls `CoreAiMcpServer.StartServer(originalPort)`, then call `StopServer()` on the Unity main thread. This is the callback restart explicitly promised by the new README at line 280.
- Expected: the callback installs a usable replacement listener and its queued calls survive the outer stop. Actual source path: stop detaches `_server`/`_active`, then calls old `Dispose`; `McpHttpServer.Stop()` invokes `_cts.Cancel()` at line 161 **before** stopping/closing the old listener at lines 170-171. The callback therefore attempts to register the same HTTP prefix while the old listener owns it. The replacement's bind fails, `StartListening` clears its state, and the outer stop subsequently closes the old listener, leaving no running server.
- Causal repair defect: clearing the host generation before disposal enables the nested start, but disposal still publishes cancellation before releasing the actual transport binding. The new test at `Assets/CoreAIMcp/Tests/PlayMode/CoreAiMcpServerResidencyPlayModeTests.cs:424` uses `InMemoryTransport` (lines 617-648), whose restart has no binding conflict, so a green result there would not establish the advertised real-transport behavior. Retain the state-before-callback protection while making the real endpoint available before admitting the replacement.
- Minimal regression plan for root: use an isolated real loopback port and the automatic host with an explicit registry via the existing internal seam and a real `McpHttpServer`; execute a controlled tool through an actual HTTP request, restart synchronously from its lifetime-token callback on that same port, and assert `IsRunning`, a fresh authenticated `initialize`/tool call, preserved new queue, and old transport completion after outer stop. Do not substitute a port change or a nonbinding fake for this case.
- Uncertainty: source-derived; no listener or test was run. Exact bind exception wording depends on Unity/OS. The important condition is overlapping ownership of the identical prefix.

The other three historical repair areas have no additional established defect from this review: broker date preservation, world/log result flags and canonical URLACL text agree with their immediate contracts. The main ownership repair also retains running admission and distinguishes automatic from scene-owned objects at source level; PR-MCP-001 concerns its callback restart path.

## Contextual sampling

Read only to resolve changed-code contracts:

- `Assets/CoreAIMcp/Runtime/Protocol/JsonRpc.cs`, `McpContent.cs`: full parser/result contracts.
- `Assets/CoreAIMcp/Runtime/Server/McpRpcDispatcher.cs`, `IMainThreadDispatcher.cs`: full capture/queue/result/cancellation path and inline fake semantics.
- `Assets/CoreAIMcp/Runtime/Tools/McpToolRegistry.cs:71-176`: publication, frozen arguments, broker preparation, admission and one-shot invocation.
- `Assets/CoreAIMods/Runtime/Logging/GetModLogsLlmTool.cs`: inner result format, level validation, query and cancellation behavior.
- `Assets/CoreAiUnity/Runtime/Source/Features/World/WorldLlmTool.cs`: selected command validation/dispatch, cancellation catches and `WorldResult` serialization, especially lines 321-446 and 896-914. No general world executor audit.
- `Assets/CoreAIMcp/package.json`: version comparison only. A targeted serializer-default search found no global `JsonConvert.DefaultSettings` assignment in the four relevant package trees; the one camel-case resolver match was formatter-local, not evidence of a defect here.

No broader provider, authority, Mods sandbox, external-client or host-game policy audit is claimed. Repeated roles, authentication/account transitions, matchmaking and view lifetimes are not treated as MCP defects. No ignored supplied context was established in these changes.

## Unconfirmed issues and execution limits

Static review only. Runtime behavior and tests have not been executed.

- PR-MCP-001 needs root's real-transport regression proof; the existing in-memory lifecycle suite cannot provide it. OS bind/URLACL behavior, actual pressure, graphics, device/IL2CPP and WebGL execution remain unvalidated.
- The new world cancellation test cancels before admission; it does not exercise an executor throwing cancellation after admission. The head inner tool separately rethrows `OperationCanceledException` at lines 435-437; no contrary introduced defect was established.
- Historical MCP-H001 (an overload response fault potentially ending the accept loop) concerns unchanged code and remains unconfirmed here. It is not counted as a PR finding. No new unsupported hypothesis is promoted to a defect.
- Historical reports' aggregate passes, probes and execution claims were not rerun or adopted. No source fixes or test weakening were made.

## Handoff

- CHANGED: only `dev-docs/pr-review-2026-10-25/10-mcp.md`; incremental header, complete primary coverage and one finding.
- VERIFY OUTPUT: `git rev-parse HEAD` returned `bc3fd74f0a889bfe1b1e7babc6bc02ed48a7c05e`; pinned zero-context diff inventory returned 13 paths / 52 hunks; scoped `git status --short` reported `?? dev-docs/pr-review-2026-10-25/10-mcp.md`. These are read-only inspection results, not test passes.
- NOT DONE: no builds/tests, Unity/MCP/provider/listener calls, implementation, Git staging/commits/push, or release changes. Root owns adjudication, proof, fixes and publishing.
- CONCERNS: callback restart is documented and tested with a transport fake but still overlaps real listener ownership. Source confidence is high; runtime proof remains pending.
- QUESTIONS: none.
