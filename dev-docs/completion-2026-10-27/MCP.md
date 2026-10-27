# MCP transport PR audit — 2026-10-27

Status: source review complete; root acceptance pending. Reviewer: native gpt-6.1-sol, reasoning effort max. Baseline: `demo` at published `bc3fd74f` / 7.54.0; tracked and untracked working-tree changes are the review surface.

## Goal, invariants and scope

Review the module as a replaceable framework block, preserve unrelated authorized changes and the frozen 2026-10-23 audits, and report only actionable confirmed defects. No implementation, asset, metadata, test, build, Unity, provider, Git or service mutation was performed. The owner explicitly requested these retained per-module reports. Saved receipts were parsed, not rerun.

## Review result

No additional confirmed source defect in this module's current diff. The PR-MCP-001 repair releases the old loopback prefix before synchronous lifetime-cancellation callbacks run. `CoreAiMcpServer.StopListening` already detaches the old transport and main-thread queue generation before disposing it. A callback therefore creates a replacement transport rather than mutating the detached transport.

The change retains loopback/Host/Origin/body/auth guards, request-slot bounds, finite read/write timeouts, and nonblocking completion observation. Listener-close failures and cancellation-callback failures are logged independently; the later handler-abort pass still runs. No WebGL listener support is implied.

## Regression evidence read

`artifacts/pr-review-20261025/unity-repair-focused5.xml`: the `CoreAI.Mcp.Tests.McpHttpServerIntegrationEditModeTests` fixture has **18 Passed, 0 Failed, 0 Skipped**. The saved selection has 500 Passed overall.

- `Start_AfterStop_RebindsLoopbackPrefix_AndServes` stops and restarts one real HTTP server on the same loopback port, then initializes it.
- `Stop_ReentrantCancellationCallback_ReleasesPrefixBeforeCallback_AndReplacementServes` starts a replacement listener synchronously from a blocking tool's cancellation callback on the **same** port, waits for the old transport completion, and initializes the replacement through HTTP.
- Existing protocol/session/notification tests retain bounded stream admission, body guards, catalog notifications and normal tool dispatch coverage.

The two new cases would Ignore when the operating system refuses loopback binding, but their saved rows are Passed, not ignored.

## Acceptance limits

The new reentrant test composes `McpHttpServer` directly with an inline dispatcher. It does not itself exercise the automatic `CoreAiMcpServer` component's main-thread queue or bearer authentication on a replacement tool call. Those parts are covered only by separate source/lifecycle/guard evidence unless root runs an integrated component fixture. Keep that distinction in PR-MCP-001 acceptance. No new native/component tests were run by this reviewer.

Package `com.coplaydev.unity-mcp` is an external Unity automation dependency. Its 10.3.0 client/server update is a separate dependency integration gate; it is not this package's `CoreAI.Mcp` HTTP server version.

## Reviewed source snapshot

| Path | SHA-256 |
| --- | --- |
| `Assets/CoreAIMcp/Runtime/Server/McpHttpServer.cs` | `f9dd633bdbc233c25da651f39b8893a352c3939d579e104a99c77551c52653c4` |
| `Assets/CoreAIMcp/Runtime/Server/CoreAiMcpServer.cs` | `436d20de9e5fd68c27af7b8f3a8809cbe65a1206f8c8209309c1f521372c4793` |
| `Assets/CoreAIMcp/Tests/EditMode/McpHttpServerIntegrationEditModeTests.cs` | `c4ab387daece33d67266838f3973dd5d3910ee94c616fae48978b50e61f5ecc8` |
