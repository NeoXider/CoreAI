# MCP repair work — 2026-10-24

Baseline: `69d5bcc37502cd4bb464a3bf9ac0c53b1624b804`. Target release: `7.54.0`.
Lease: source, tests and README under `Assets/CoreAIMcp/`, excluding metadata, package manifests and changelogs; this work file only elsewhere. Root owns all builds, tests, Git and release actions. No other agents or Unity operations are used.

## Finding review and acceptance

| ID | Reproduction and guard review | Disposition | Regression acceptance |
| --- | --- | --- | --- |
| MCP-001 | Nested `arguments_json` uses date-inferencing `JObject.Parse`; direct RPC uses the date-preserving single-value reader. Binding copies preserve the coerced token. Retain object, depth and trailing-content validation. | confirmedfix | Direct and broker dispatch preserve token type and exact ISO string text, including fractional seconds and offsets; invalid nested input does not invoke a tool. |
| MCP-002 | World/log adapters discard the returned success field; dispatcher correctly serializes the resulting envelope. Required-action validation alone does not cover inner failures. Preserve original content and cancellation propagation. | confirmedfix | Direct and broker calls expose inner failures through `isError`, successes remain successful, rejected parameters avoid executor/query work, and cancellation remains cancellation. |
| MCP-003 | Static stop clears the sole reusable-host reference; a subsequent static start allocates another persistent component and running bodies retain leases on the abandoned host. Same-component restart already retains leases. Scene-owned lifecycle remains separate. | confirmedfix | Deterministic no-model PlayMode coverage bounds automatic host count across repeated cycles and retains admission for a cancellation-ignoring body across static restart until actual completion, with exact capacity recovery. |
| MCP-004 | Windows guidance reserves `/mcp/` while the listener registers the root prefix. Existing bind cleanup does not correct that diagnostic. Preserve all loopback/auth/Origin/body/queue guards. | confirmedfix | Pure formatting/adapter coverage establishes an exact canonical listening prefix match, including the trailing slash, without sockets, `netsh`, or admin mutation. |

No host authentication, account, matchmaking or game-session policy is added. MCP-H001 remains an unconfirmed hypothesis outside this four-finding repair. Wire cancellation and platform/rendering/pressure validation remain documented limits rather than new claimed fixes.

## Owned files (proposed, then implemented)

- `Assets/CoreAIMcp/Runtime/Tools/CoreAiToolsBrokerMcpTool.cs`
- `Assets/CoreAIMcp/Runtime/Tools/GetModLogsMcpTool.cs`
- `Assets/CoreAIMcp/Runtime/Tools/WorldCommandMcpTool.cs`
- `Assets/CoreAIMcp/Runtime/Server/CoreAiMcpServer.cs`
- `Assets/CoreAIMcp/Runtime/Server/McpHttpServer.cs`
- `Assets/CoreAIMcp/Tests/EditMode/McpDynamicToolsEditModeTests.cs`
- `Assets/CoreAIMcp/Tests/EditMode/McpRpcDispatcherEditModeTests.cs`
- `Assets/CoreAIMcp/Tests/EditMode/CoreAiMcpServerMainThreadEditModeTests.cs`
- `Assets/CoreAIMcp/Tests/PlayMode/CoreAiMcpServerResidencyPlayModeTests.cs`
- `Assets/CoreAIMcp/README.md`
- `dev-docs/fix-wave-2026-10-24/09-mcp.md` (this work file)

Public signatures remain unchanged. The lifecycle/test seam described below was announced before implementation and is package-internal.

## Progress

- Read repository instructions, normative architecture rules and the assigned audit. Existing owner changes and unrelated ETFX metadata are preserved.
- Source-level reproductions, existing guards and the actual source/test/README diffs reviewed. All four IDs remain `confirmedfix`; no host policy was added.
- Builds, test execution, Unity, sockets and model/provider calls: **not run**. Root must run sequential compile/test gates after handoff; commands and filters follow below.

### Source milestone

- Reused `JsonRpc.TryParse` directly for broker argument objects, preserving its date, depth and trailing-content contract without adding a second parser.
- World/log wrappers map only the inner boolean success field and retain the original JSON text. New dispatcher regressions cover both routes, rejected parameters, success, query failure and protocol-level cancellation.
- Automatic host ownership is separate from the active listener reference. Stopped hosts retain running leases; idle automatic hosts retire in `Update`, while scene-owned hosts are not destroyed. Stop detaches the old generation and fails its pending queue before cancellation callbacks can reenter and restart.
- Internal-only seams: `IMcpServerTransport`, three-argument `StartListening` and `StartServer` overloads accepting a transport factory, and canonical HTTP-prefix accessors. No public signatures or module dependencies changed. Factories replace composition/HTTP I/O in tests; production entry points still use the existing composition and `McpHttpServer`.
- Canonical listener prefix now feeds both HTTP registration and bind-failure guidance. Added a fake access-denied regression without sockets or URLACL mutations.
- Cross-owner contract: `WorldLlmTool.ExecuteAsync` catches `OperationCanceledException` under `catch (Exception)` in the Unity package. Root has notified host owner 04. This lease does not change that implementation; MCP cancellation tests preserve its own pre-admission/protocol behavior and log-query cancellation propagation.
- PlayMode regressions now cover automatic versus scene ownership, three repeated restart cycles with a cancellation-ignoring body, bounded admission and exact capacity recovery, idle retirement, repeated failed starts, cancellation-callback restart, transport-disposal failure, and an obsolete start failure after a replacement listener has started on a different port. All new lifecycle tests use an in-memory transport and refuse to touch preexisting host instances.
- Start completion/failure checks its captured transport identity before publishing or clearing listener state. Diagnostics capture the attempted port before a callback can change the component's port. Stop state/queue cleanup precedes disposal, including when disposal throws.
- README describes host ownership, cleanup, main-thread lifecycle, broker string preservation, failure envelopes and canonical URLACL guidance. No public API signatures, manifests, assembly definitions, metadata, Unity assets or other owners' files were changed.

## Source-final handoff

Status: **SOURCE FINAL; root execution pending**. No further implementation work is planned within this lease unless independent review or root checks find a defect.

### Checks actually run

- Read-only source/diff review, including the actual runtime, tests and README diffs.
- `git diff --check -- Assets/CoreAIMcp`: exit 0; no whitespace errors. Git emits line-ending normalization warnings for three existing files; no content-wide normalization was performed.
- A read-only, in-memory Python check of the scoped Git diff and work file: exit 0. Exact output: `Scoped static checks: exact ten package files; added prose, explicit-local and comment-shape checks passed; work file has no Cyrillic or trailing whitespace.` This is a narrow source check, not compile or runtime evidence.
- No builds, Unity tests, listener startup, URLACL mutation, provider/model calls, model benchmarks or Git mutations were run.

### Sequential commands for root

PowerShell compile gates (run individually under root's shared-output coordination):

```powershell
dotnet build CoreAI.Mcp.csproj --no-restore
dotnet build CoreAI.Mcp.Tests.csproj --no-restore
dotnet build CoreAI.Mcp.PlayModeTests.csproj --no-restore
```

Expected compile result: exit 0, zero errors. These commands do not execute tests. Root must select the runner and result/log destinations; do not start another Editor against a locked project.

Focused EditMode runner arguments (three fixtures, no sockets needed):

```text
-testPlatform EditMode -assemblyNames CoreAI.Mcp.Tests -testFilter "CoreAI.Mcp.Tests.McpDynamicToolsEditModeTests;CoreAI.Mcp.Tests.McpRpcDispatcherEditModeTests;CoreAI.Mcp.Tests.CoreAiMcpServerMainThreadEditModeTests"
```

Focused PlayMode runner arguments (six new socket-free, no-model regressions):

```text
-testPlatform PlayMode -assemblyNames CoreAI.Mcp.PlayModeTests -testFilter "CoreAI.Mcp.Tests.CoreAiMcpServerResidencyPlayModeTests.StaticRestart_RetainsCancellationIgnoringBodyAndRecoversCapacityExactlyOnce;CoreAI.Mcp.Tests.CoreAiMcpServerResidencyPlayModeTests.StaticIdleCycles_RetireAutomaticHostsAndPreserveSceneOwnedHost;CoreAI.Mcp.Tests.CoreAiMcpServerResidencyPlayModeTests.StaticStop_ReentrantCancellationRestartPreservesNewGenerationAndQueue;CoreAI.Mcp.Tests.CoreAiMcpServerResidencyPlayModeTests.StaticStop_DisposeFailureStillClearsStoppedGenerationAndRetainsRunningLease;CoreAI.Mcp.Tests.CoreAiMcpServerResidencyPlayModeTests.StaticStart_ObsoleteFailureCannotDisposeRestartedGenerationOrChangeItsQueue;CoreAI.Mcp.Tests.CoreAiMcpServerResidencyPlayModeTests.StaticStart_FailedAttemptsReuseThenRetireAutomaticHostAndDisposeTransports"
```

Expected test result: no failures; lifecycle skips due to existing hosts are unverified rather than passes. Root should include the package's existing request/security/catalog/residency guards and the repository English/architecture guards in its integrated verification, coordinated with host owner 04's cancellation fix. Release/version/push remain exclusively root-owned.

### Remaining concerns and questions

- Required compile/EditMode/PlayMode checks remain unexecuted by this worker. Desktop/device/WebGL/graphics/pressure checks and MCP-H001 were not validated or claimed fixed.
- The inner world-tool cancellation contract belongs to host owner 04, already notified by root. No cross-lease change is requested here.
- Questions: none. No lease extension is needed.
