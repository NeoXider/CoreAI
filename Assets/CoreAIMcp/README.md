# CoreAI MCP Server (`com.neoxider.coreaimcp`)

An optional [Model Context Protocol](https://modelcontextprotocol.io) server that runs **inside a live
CoreAI game session** — Editor play mode or a shipped build — so an external agent drives the *running*
game over a standard protocol instead of editing files and restarting.

It is the in-game Command Bar surfaced over MCP: `execute_lua`, `get_mod_logs`, `read_skill`,
`world_command`, `screenshot` and, with explicit host-admin configuration, `manage_mods`. Each tool is
registered only when the current composition provides its backing service (see [Tools](#tools)).

## Who this is for

- **AI-in-the-loop testing** — point a Claude Code / Codex / opencode session at the running game and
  let it spawn objects, load a mod, read the logs, and look at the result.
- **Live repair and iteration** — change behaviour without leaving play mode.
- **CI and reproduction** — script a session against a build over loopback HTTP.

## Quick start

Install `com.neoxider.coreaimcp` (it depends on `com.neoxider.coreaimods` and
`com.neoxider.coreaiunity`), then either add a **CoreAI MCP Server** component to a scene that also has
a `CoreAILifetimeScope` and tick **Start On Enable** (or call `StartListening()` on it), or start it
from code. The component alone starts nothing — **Start On Enable** is off by default:

```csharp
using CoreAI.Mcp.Server;

CoreAiMcpServer server = CoreAiMcpServer.StartServer(port: 8590); // the running server, or a new DontDestroyOnLoad host
string url = server.Url;          // http://127.0.0.1:8590/mcp
string token = server.AuthToken;  // null when auth is disabled or the server did not start
```

```bash
claude mcp add --transport http coreai http://127.0.0.1:8590/mcp \
  --header "Authorization: Bearer $COREAI_MCP_TOKEN"
```

`StartServer` collects tools from the CoreAI composition, so it needs a built `LifetimeScope` in the
scene (normally `CoreAILifetimeScope`). The server uses the scope assigned to its **Scope** field, else
the scene scope that resolves the Lua executor, else any built scope. Without one it logs
`No built CoreAI LifetimeScope; server not started.` and does nothing.
To run with **your own** catalog and no CoreAI composition at all:

```csharp
using CoreAI.Mcp.Server;
using CoreAI.Mcp.Tools;

CoreAiMcpServer server = new GameObject("Mcp").AddComponent<CoreAiMcpServer>();
McpToolRegistry registry = new(new IMcpTool[] { myTool });   // or new McpToolRegistry(null) to start empty
server.StartListening(registry, listenPort: 8590);
```

> **Before you leave this port open:** an MCP port is full control of the game. Read
> [Security model](#security-model) — loopback alone is not a boundary, and the bearer token is the
> layer that stops another local process.

## Scope and boundaries — read this before designing around it

- **This package is a server, not a client.** There is no outbound MCP client in it: no stdio spawn, no
  remote-server connector. CoreAI agents inside the game reach *their* capabilities through `ILlmTool`
  and skills, not through MCP. If you want the in-game agent to consume an *external* MCP server, you
  write that bridge yourself — this package will not do it for you.
- **HTTP only, loopback only.** One `HttpListener` bound to `127.0.0.1`. No stdio and no WebSocket
  transport; stdio-only clients go through an external bridge such as `npx mcp-remote`.
- **Not available in a WebGL player.** There `StartListening` logs a warning and does not start the
  listener. The typed live catalog (`McpToolRegistry`) itself needs neither Unity nor sockets.
- **The catalog is per-composition.** A tool appears only when its backing service resolves, so
  `tools/list` describes *this* game, not the package. `manage_mods` additionally requires explicit
  host-admin configuration (below) and is **absent by default**.
- **MCP surface is tools-only.** `initialize`, `notifications/initialized`, `tools/list`, `tools/call`,
  `ping`, and the server-sent `notifications/tools/list_changed`. No `resources/*`, `prompts/*`,
  `logging/*` or `completion/*`.

## Security model

An open MCP port is **full control of the game**: `execute_lua` runs arbitrary sandboxed Lua,
`manage_mods load` writes a mod that survives a restart, and `screenshot` returns the player's screen.

**Binding `127.0.0.1` is not, by itself, a security boundary.** Two attacks go straight through it:

- **CSRF from a web page.** A `fetch('http://127.0.0.1:8590/mcp', {method:'POST', mode:'no-cors', …})`
  with `Content-Type: text/plain` is a "simple" CORS request — no preflight — so *any* page open in the
  user's browser could fire it and the game would execute the call.
- **DNS rebinding.** Once `attacker.example` resolves to `127.0.0.1`, the page's requests are
  *same-origin*, so it can also **read** the responses — including screenshots.

So the server enforces all of the following, in this order, on every request:

| Layer | Rule | Stops |
|-------|------|-------|
| **Off by default** | nothing starts until **Start On Enable** is ticked or `StartListening()` / `StartServer()` is called | everything, until you opt in |
| **Loopback bind + `IsLocal`** | listener bound to `127.0.0.1`; a non-local request → `403` | off-box callers |
| **`Host` check** | must be `127.0.0.1`/`localhost`/`[::1]` on the server's port → else `403` | DNS rebinding |
| **`Origin` check** | absent (a real MCP client) or loopback-on-this-port → else `403` | browser CSRF |
| **Bearer token** | `Authorization: Bearer <token>` → else `401` | **any other local process** |
| **Content type** (POST) | absent or a JSON media type → else `415` | the preflight-free `text/plain` POST |
| **Body cap** (POST) | 4 MB (`McpHttpServer.MaxRequestBodyBytes`) → else `413` | a local memory-exhaustion poke |

The token is the layer that matters against a malicious *local* process, which no header check can see.
Keep it on. Still never bind to `0.0.0.0` and never forward the port through a tunnel or reverse proxy.

### The auth token — where it comes from and how to pass it

The token is resolved at start, in this order:

1. the **Auth Token** field on the `CoreAiMcpServer` component, if set;
2. the **`COREAI_MCP_TOKEN`** environment variable;
3. otherwise a fresh random token, generated per start.

However it was obtained, it is printed to the game console together with a ready-to-paste command:

```
[CoreAI MCP] Auth token: 7Qk3…
  claude mcp add --transport http coreai http://127.0.0.1:8590/mcp --header "Authorization: Bearer 7Qk3…"
```

It is also readable from code as the instance property `AuthToken` (or the static
`CoreAiMcpServer.ActiveAuthToken` of the active server), which is what an in-editor automation script
should use.

> **Set `COREAI_MCP_TOKEN` (or the inspector field) if you want a stable client config.** With the
> random default, the token changes on every play session and the client must be re-pointed each time.

Unticking **Require Auth Token** disables token auth (the `Origin`/`Host`/loopback checks stay). Only do
that on a machine you fully trust — the server logs a warning at every start when you do.

## Install / enable

1. Ensure `com.neoxider.coreaimods` (and its `com.neoxider.coreaiunity` dependency) are in the project —
   the MCP tools wrap those services.
2. Add a **CoreAI MCP Server** component to any GameObject in a scene that also has a
   `CoreAILifetimeScope` (and, for the Lua tools, a `CoreAiModsLifetimeScope`).
3. Set the port (default **8590**) and tick **Start On Enable** — required unless you start the server
   from code (`StartListening()` on the component, or `CoreAiMcpServer.StartServer()`). Set
   **Auth Token** (or `COREAI_MCP_TOKEN`) if you want the same token every run.
4. Enter play mode. With **Start On Enable** ticked, the console logs
   `CoreAI MCP server listening on http://127.0.0.1:8590/mcp` followed by the auth token line.

Or start it from code with `CoreAiMcpServer.StartServer()` as in [Quick start](#quick-start) and stop it
with `CoreAiMcpServer.StopServer()`.

Call the start/stop lifecycle APIs on the Unity main thread. `StartServer` reuses the active server;
when it creates a persistent host, the framework owns that host. `StopServer` stops its listener and
fails queued work. The stopped automatic host stays available for restart while already-running
bodies drain, preserving their admission slots. Once it is stopped and drained, `Update` destroys it;
an unsuccessful start is cleaned up the same way. A later start creates a new host, so check `IsRunning`
after starting and reacquire the instance through `StartServer` after retirement. Stopping a component
on a scene-owned GameObject leaves that GameObject under the host game's ownership.

The component must stay **enabled on an active GameObject**: `Update()` is what drains the tool queue on
the Unity main thread, and disabling the component stops the server. If `Update()` does not run — the
server was started from code on a disabled component or an inactive GameObject, or the game is paused —
a `tools/call` fails after **Main Thread Timeout Seconds** (default 30; `0` disables the timeout) with a
JSON-RPC error naming the cause, instead of hanging the client forever.

On Windows, an access-denied startup diagnostic includes the URL reservation matching the actual
listener prefix, for example `netsh http add urlacl url=http://127.0.0.1:8590/ user=%USERNAME%` from an
administrator command prompt. The reservation is for the root prefix with its trailing slash;
clients still connect to `http://127.0.0.1:8590/mcp`.

## Protocol

- Endpoint: **`POST http://127.0.0.1:<port>/mcp`**, JSON-RPC 2.0, MCP streamable HTTP.
- Methods: `initialize`, `notifications/initialized` (no-op), `tools/list`, `tools/call`, `ping`.
- **Response framing is negotiated by the `Accept` header:** clients that ask for `text/event-stream`
  get the single JSON-RPC response as one SSE `message` event; everyone else gets plain
  `application/json`. Both carry the same payload.
- For POST the session id is optional. `initialize` issues `Mcp-Session-Id`; it is required
  for the GET tool-change subscription, but it does not replace the bearer token. Session expiry and
  protocol-version negotiation are described in
  [Protocol version and expired sessions](#protocol-version-and-expired-sessions).
- Errors: unknown method → `-32601`, unknown/absent tool name → `-32602`, malformed JSON → `-32700`,
  non-object JSON or an invalid request shape → `-32600`, a stalled main thread → `-32603` (message
  names the cause). A tool that throws returns a normal result with `isError: true`.
- Request parsing preserves JSON strings exactly, including timestamp-shaped request ids, tool names,
  and arguments, including nested broker `arguments_json` objects. It does not infer dates or change
  their formatting; non-object arguments, excess nesting and trailing content are rejected.
- Transport rejections: `401` — token, `403` — foreign Origin/Host or non-local address,
  `405` — a method other than GET or POST,
  `413` — body size exceeded in **UTF-8 bytes**, including chunked requests, `415` — not JSON,
  `408` — body not received in time, `503` — all 64 request-processing slots busy.
  The body is read for at most 10 seconds (`McpHttpServer.BodyReadTimeout`); a small rejected request
  with a known length is drained at most 64 KB and 250 ms. An incomplete body never blocks the rejection
  indefinitely.
- **`serverInfo.version` is the package version.** It comes from `McpServerInfo.Version`, which
  `tools/bump_version.py` rewrites together with every `package.json`; `McpPackageVersionEditModeTests`
  fails the build if the two ever drift apart.
- `GET /mcp` with `Accept: text/event-stream` and a valid `Mcp-Session-Id` opens an SSE subscription
  for `notifications/tools/list_changed`. Once the HTTP server has started, `initialize` advertises
  `capabilities.tools.listChanged: true`. Without that `Accept` the server returns `406`, without a
  session `400`, with an unknown or expired session `404`, and `409` when the stream limit is reached.

### Live catalog: switching stages without a restart

`McpToolRegistry` needs neither Unity nor sockets. In a running game use `CoreAiMcpServer.Registry`
(the live catalog while the server is listening, `null` otherwise):

```csharp
McpToolRegistry catalog = server.Registry;
catalog.AddOrReplace(briefingTool);
catalog.Remove("previous_stage");
catalog.Replace(new IMcpTool[] { sharedTool, nextStageTool });
```

`AddOrReplace` adds a tool or replaces the binding and schema of one name, `Remove` withdraws a name
(it returns `false` when the name is absent), and `Replace` publishes a complete new set in one
operation. You can start from an empty catalog and add tools later:
`server.StartListening(new McpToolRegistry(null), listenPort: 8590)`. The parameterless
`StartListening()` collects the available tools from the CoreAI composition.

Without Unity at all, assemble the same stack by hand — this is what the engine-free EditMode tests do:

```csharp
McpToolRegistry registry = new(new IMcpTool[] { myTool });
McpRpcDispatcher dispatcher = new(registry, new McpSessionStore(), new InlineMainThreadDispatcher());
McpHttpServer http = new(port, dispatcher, log, logError, authToken);
http.Start();
```

An `IMcpTool` is `Name` / `Description` / `InputSchemaJson` (an object schema; blank means
`{"type":"object"}`) plus
`Task<McpToolResult> InvokeAsync(JObject arguments, CancellationToken cancellationToken)`; build results
with `McpToolResult.Text(...)` or `McpToolResult.Failure(...)`.

Each publication is atomic. Registration freezes a tool's name, description and schema; changing the
passed object's properties afterwards does not change what is published. An invalid schema, an empty
or whitespace-padded name, or a duplicate name in `Replace` rejects the whole update and leaves the
previous set published. To replace one tool, call `AddOrReplace`. The name `coreai_tools` is reserved
for the broker the registry manages.

`AddOrReplace(tool, McpToolResidency.Dynamic)` sets the residency of one tool; without the argument the
registry's policy applies (see [Tool residency](#tool-residency-what-the-model-pays-for)). A removed tool
is unavailable both by direct name and through the broker. The broker reads the current catalog and
refuses to call itself.

When a `tools/call` arrives, the registry captures one call plan from the current revision: the selected
binding, a copy of the arguments and, for a `coreai_tools` `call`, the target's binding with
`arguments_json` parsed once. When the call reaches the game thread, the registry re-checks that these
bindings are still current. If a name was removed or replaced in between (including remove-then-add), the call fails with `-32602` and a
message asking the client to re-read `tools/list`. After admission no name is looked up again, so a
later replacement cannot route the captured arguments to a new handler. A call that has started runs to
completion; a catalog change does not undo what it already did.

Every publication triggers `notifications/tools/list_changed` on each open GET stream, with the catalog
revision (monotonically increasing) in `params._meta["coreai/catalogRevision"]`; clients then re-read
`tools/list`. A new GET stream sends the current revision immediately, and a new stream for the same
session replaces the previous one. Only the latest revision is re-sent; tool calls are never replayed.

Each session has at most one stream, and the server holds at most 16 streams
(`McpHttpServer.MaxNotificationStreams`, settable from 1 to 32). A GET for another session beyond that
limit gets `409` while POST requests keep being served. At most 64 HTTP requests are processed at once. Rapid changes
are coalesced into the latest revision, so notification history does not grow. An idle stream receives
a keepalive comment every 15 seconds, and each write to a slow client is limited to 5 seconds
(`McpHttpServer.WriteTimeout`). `McpHttpServer.Stop()` cancels the transport and closes connections;
await `McpHttpServer.Completion` to know when its handlers have finished, without blocking the Unity
main thread.

### Game-thread queue and frame budget

HTTP requests arrive off the game thread. `CoreAiMcpServer` starts tool bodies from `Update`, while
the network handler waits asynchronously for the result. One host admits at most
`MainThreadCallCapacity` (64) calls, counting both queued calls and bodies still running;
`AdmittedMainThreadCalls` shows how many slots are taken. A call beyond that capacity fails at once with
an `isError` tool result.

Each `PumpMainThreadQueue` call works on a snapshot of the queue taken on entry; calls queued while it
runs wait for the next frame. The time budget is checked between starts:
**Main Thread Pump Budget Milliseconds** in the inspector, default 2. A host with its own loop can call
`PumpMainThreadQueue()` (inspector budget) or `PumpMainThreadQueue(TimeSpan budget)`; a zero budget still
starts one call, so the queue keeps moving. The game thread never blocks waiting on a task.

**Main Thread Timeout Seconds** limits only the wait until a call starts. A call that times out is
removed from the queue, frees its slot and fails with `-32603`; a later frame does not run it. Once a
call has started, the timeout no longer applies: the result arrives when the body actually finishes.
`StopListening` fails the queued calls and cancels the network requests' lifetime token. A body that has
already started receives the cancellation but keeps its slot until it actually finishes, even across a
server restart. Other calls can use the free slots, and asynchronous bodies may run concurrently.
This also applies to `StopServer` / `StartServer` on the automatic host. A body which ignores
cancellation delays that host's retirement until actual completion. Stop clears the previous queue
before cancellation callbacks run, so a callback can restart the host without losing its new listener
or newly queued calls.

The budget is checked **between** calls and cannot interrupt synchronous code. A host tool should keep
its synchronous part short, honour cancellation and use asynchronous I/O; split heavy work or move it
off the game thread, keeping Unity API access on the game thread. The queue does not make a handler
safe if it blocks the thread or ignores cancellation.

### Protocol version and expired sessions

An `Mcp-Session-Id` that is unknown, evicted or expired gets HTTP `404` on any request, including POST,
and no tool body runs. The client then sends a new `initialize` **without the old header**. Sessions
are bounded: the server keeps at most 64 (evicting the oldest first), each for one hour
(`McpSessionStore` defaults). POST without a session id keeps working for stateless clients.

The server supports protocol versions `2025-06-18` and `2025-03-26`. `initialize` always answers with
`2025-06-18`, whatever version the client requested; the client decides whether it can work with it.
Any other `MCP-Protocol-Version` header value gets HTTP `400` before the request is routed. When the
header is absent, a request with a session is treated as `2025-06-18` and one without a session as
`2025-03-26`.

## Tools

Tools are registered **only when their backing service resolves** in the current composition, so
`tools/list` reflects what this particular game exposes.

| Tool | Present when | What it does |
|------|--------------|--------------|
| `execute_lua` | the Lua mod stack is installed (an `ILuaExecutor` resolves) | Runs a one-off `code` snippet in the sandboxed Lua 5.2 VM and returns the executor result as JSON: `Success`, `Output` (the chunk's returned value) and `Error`. |
| `manage_mods` | the mod runtime resolves **and** host-admin authority is configured (see below) — **absent by default** | list / get_source / load / reload / unload / export / import / forget / versions / revert / diagnostics on persistent mods; `reload` takes an optional `keep_objects` (default `false`: the objects the previous run's main chunk built are removed first). |
| `get_mod_logs` | an `ILuaLogService` resolves | Reads mod `print`/`warn`/`error`/runtime-error output, independent of the Unity console. Optional `mod_id`, `level` (minimum severity), `since_sequence` and `max_entries` (default 50). |
| `read_skill` | the Programmer role has skills | Returns a registered skill by `name` (e.g. `Lua Modding`, `Rbx API`) — the same reference the on-board agent reads. A multi-document skill returns its entry document and a section index; `section` reads one document, `all: true` reads all of them. |
| `world_command` | a world-command executor resolves | Spawn / move / edit scene objects (meters; Euler degrees). |
| `screenshot` | always | Captures the main camera to a PNG (base64), downscaled to `max_resolution` (default 1024, `0` = no cap). Missing camera / capture failure is reported per call, with the real reason. |
| `coreai_tools` | always (see [Tool residency](#tool-residency-what-the-model-pays-for)) | Broker over Dynamic tools: `list`, `describe`, `call`. |

Each tool ships a real JSON Schema in `tools/list` so clients validate arguments before calling.

`get_mod_logs` and `world_command` keep their backing tool's JSON text unchanged and map its boolean
`success` or `Success` field to MCP `isError`. Invalid log levels, missing world parameters, unknown
actions and executor refusals therefore return `isError: true`, directly and through `coreai_tools`.
An `OperationCanceledException` reaching the dispatcher remains a protocol error (`-32603`) without
a tool-result payload; ordinary tool failures keep their normal content.

### `manage_mods` and host-admin authority

`manage_mods` writes mods that survive a restart, so it is not published to an anonymous caller.
`CoreAiMcpToolProvider` registers it only when it receives a host identity, and `CoreAiMcpServer`
supplies one (once the mod runtime resolves) only when **all** of the following hold; it logs a
distinct warning naming the reason whenever one does not:

1. **Enable Host Admin Mod Management** is ticked (or `ConfigureHostAdminModManagement(...)` was called
   before the server started — it throws while `IsRunning`);
2. **Host Admin Actor Id** is non-blank;
3. an `IActorIdentityProvider` is available — the one passed to
   `ConfigureHostAdminModManagement(IActorIdentityProvider)`, otherwise the host composition's;
4. that provider's `Programmer` actor is `IsTrusted` **and** `Grants.IsUnrestricted`;
5. its `ActorId` matches the configured **Host Admin Actor Id** exactly.

```csharp
server.ConfigureHostAdminModManagement("studio-host");            // explicit id
server.ConfigureHostAdminModManagement(actorIdentityProvider);    // id taken from the composed actor; throws unless it is trusted and unrestricted
```

Turning it on logs a warning at start, and the warning is the accurate description of the risk:
**every client holding the MCP bearer token receives this authority.**

### Tool residency: what the model pays for

`McpToolResidency` decides where a tool is *advertised*, not whether it works. `Native` tools appear in
`tools/list`; `Dynamic` tools are reached through the `coreai_tools` broker (`list` / `describe` /
`call`). Both stay callable by direct name while they are in the catalog. The catalog the component
builds from the CoreAI composition always lists the broker; a registry you build yourself lists it only
while at least one tool is Dynamic.

The component's composition uses `McpToolResidencyPolicies.Default` (everything Native).
`McpToolResidencyPolicies.LeanDefault` keeps only the discovery tools
(`McpToolResidencyPolicies.DiscoveryToolNames`: `read_skill` and `memory`) resident and routes the rest
through the broker. This package registers no `memory` tool; that name matters only to a host that adds
one. The source records the measurement behind that choice on the real six-tool composition:
**9,324 B all-Native versus 768 B lean**. `McpToolResidencyPolicies.Lean(...)` keeps extra names
resident. A host applies a policy with `new McpToolRegistry(tools, policy)` (or the `residencyPolicy`
argument of `CoreAiMcpToolProvider.Build`) and starts the server with `StartListening(registry)`.

Operators can move individual tools without recompiling through the environment variables
`COREAI_MCP_DYNAMIC` and `COREAI_MCP_NATIVE` (comma-separated, case-sensitive tool names, read when
`CoreAiMcpToolProvider.Build` composes the catalog). Precedence: `COREAI_MCP_NATIVE`, then
`COREAI_MCP_DYNAMIC`, then the host policy, then Native. An unknown name is ignored with one warning.

## How external agents learn the API

There is **no separate skill file** for this server — the protocol is self-describing, so the knowledge
lives *in* the server:

1. Every `tools/list` description is written to be genuinely instructive (survival-minimum globals,
   action semantics, coordinate units).
2. `read_skill` returns the **same** skill documents the in-game Programmer agent uses (for example
   `Lua Modding` and `Rbx API`) — one source of truth, no duplicated docs. An external agent calls
   `read_skill` with `name: "Rbx API"` and gets the exact datatypes/instances API the running game
   supports.

## Connecting from clients

Every snippet below carries the bearer token. Replace `$COREAI_MCP_TOKEN` with the token from the console
line — or set the environment variable `COREAI_MCP_TOKEN` **before both** the game and the client, so the
same value is used on both ends and the config never has to change.

### Claude Code (native streamable HTTP)

```bash
claude mcp add --transport http coreai http://127.0.0.1:8590/mcp \
  --header "Authorization: Bearer $COREAI_MCP_TOKEN"
```

### Codex CLI (`~/.codex/config.toml`)

Codex speaks stdio; bridge to our HTTP endpoint with `mcp-remote`:

```toml
[mcp_servers.coreai]
command = "npx"
args = ["-y", "mcp-remote", "http://127.0.0.1:8590/mcp",
        "--header", "Authorization: Bearer ${COREAI_MCP_TOKEN}"]
```

Newer Codex builds that accept a streamable-HTTP `url` directly can point at
`http://127.0.0.1:8590/mcp` with a `headers` table instead of the bridge; check your Codex version's docs.

### opencode (`opencode.json`)

```json
{
  "mcp": {
    "coreai": {
      "type": "remote",
      "url": "http://127.0.0.1:8590/mcp",
      "headers": { "Authorization": "Bearer {env:COREAI_MCP_TOKEN}" },
      "enabled": true
    }
  }
}
```

### LM Studio (`mcp.json`)

```json
{
  "mcpServers": {
    "coreai": {
      "url": "http://127.0.0.1:8590/mcp",
      "headers": { "Authorization": "Bearer PASTE_TOKEN_HERE" }
    }
  }
}
```

### Any stdio-only client

```bash
npx -y mcp-remote http://127.0.0.1:8590/mcp --header "Authorization: Bearer $COREAI_MCP_TOKEN"
```

### curl (smoke test)

```bash
curl -s http://127.0.0.1:8590/mcp \
  -H "Authorization: Bearer $COREAI_MCP_TOKEN" \
  -H "Content-Type: application/json" \
  -d '{"jsonrpc":"2.0","id":1,"method":"tools/list"}'
```

A `401` means the token is wrong or missing; a `403` means the request carried a foreign `Origin`/`Host`
(you are going through a proxy or a browser — connect to `127.0.0.1` directly).

The Claude Code, Codex, opencode, and LM Studio **config snippets** follow each tool's published
configuration format and target this server's standard streamable-HTTP endpoint; the header syntax in
particular varies between client versions — check yours if the connection returns `401`.

**Checks.** The main claims in this README are covered by these tests:

| Claim | Test |
|---|---|
| The catalog changes on a running server over one live session | `CoreAiMcpServerResidencyPlayModeTests.LiveCatalog_AddRemoveReplace_OverOneRunningHttpServerAndSession` (real `CoreAiMcpServer`, real loopback `HttpClient`, one session id throughout) |
| `list_changed` reaches the same session and a reconnect sees the current revision | `McpHttpServerIntegrationEditModeTests.LiveCatalogChanges_NotifySameSession_AndReconnectGetsCurrentRevision` |
| Publication is atomic; an invalid schema or a duplicate name rejects the whole update | `McpToolRegistryEditModeTests.InvalidSchemaUpdate_IsRejectedAtomically`, `Registry_DuplicateNamesAreRejected_WithoutChangingPublishedSet` |
| A catalog change between admission and execution cannot redirect old arguments | `McpToolRegistryEditModeTests.AdmittedInvocation_AfterReplacement_UsesOnlyCapturedBodyAndArguments`, `InvocationChangedBeforeAdmission_DoesNotAdmitOldOrReplacementBody`, `McpRpcDispatcherEditModeTests.QueuedCall_RejectsRemovedOrReplacedBinding` |
| A tool removed before its frame does not run | `CoreAiMcpServerResidencyPlayModeTests.QueuedCall_RemovedBeforeGameFrame_DoesNotRunOldOrReplacementBody` |
| Transport rejections (`401`/`403`/`413`/`415`), Host/Origin checks, UTF-8 byte cap, incomplete body | `McpHttpServerSecurityEditModeTests`, `McpRequestGuardEditModeTests` |
| An expired or evicted session gets `404` before any tool runs; an unsupported `MCP-Protocol-Version` gets `400` | `McpHttpServerIntegrationEditModeTests.Post_WithExpiredOrEvictedSession_RejectsBodyAndAllowsFreshInitialize`, `UnsupportedProtocolHeader_RejectsBeforeToolExecution` |
| `manage_mods` needs explicit, unrestricted host-admin admission | `CoreAiMcpServerMainThreadEditModeTests.BuildRegistry_ManageModsRequiresExplicitUnrestrictedHostAdminAdmission` |
| Residency, the broker and the environment-variable override | `McpDynamicToolsEditModeTests` |
| The main-thread queue honours its budget, capacity and timeout | `CoreAiMcpServerMainThreadEditModeTests` |
| Direct and broker calls preserve nested timestamp strings and reject invalid argument objects | `McpDynamicToolsEditModeTests.RawCalls_DirectAndBroker_PreserveNestedTimestampStrings`, `Broker_InvalidNestedArguments_RejectBeforeCallingTarget`, `Broker_ExcessiveNestedDepth_RejectsBeforeCallingTarget` |
| World/log failures set `isError` while retaining content and cancellation semantics | `McpRpcDispatcherEditModeTests.LogAdapter_DirectAndBroker_MapInnerFailureWithoutChangingContent`, `WorldAdapter_DirectAndBroker_MapInnerFailureWithoutChangingContent`, `LogAdapter_QueryCancellationAndFailure_KeepDistinctProtocolMeaning`, `WorldAdapter_CancelledBeforeAdmission_DoesNotInvokeExecutor` |
| Static restart retains running admission; stopped automatic hosts retire; reentrant restart and disposal failure preserve ownership | `CoreAiMcpServerResidencyPlayModeTests.StaticRestart_RetainsCancellationIgnoringBodyAndRecoversCapacityExactlyOnce`, `StaticIdleCycles_RetireAutomaticHostsAndPreserveSceneOwnedHost`, `StaticStop_ReentrantCancellationRestartPreservesNewGenerationAndQueue`, `StaticStop_DisposeFailureStillClearsStoppedGenerationAndRetainsRunningLease` (in-memory transport, no sockets or models) |
| Failed automatic starts are cleaned up; an obsolete start failure cannot dispose a replacement listener or change its queue | `CoreAiMcpServerResidencyPlayModeTests.StaticStart_FailedAttemptsReuseThenRetireAutomaticHostAndDisposeTransports`, `StaticStart_ObsoleteFailureCannotDisposeRestartedGenerationOrChangeItsQueue` |
| Windows startup guidance reserves the canonical listener prefix | `CoreAiMcpServerMainThreadEditModeTests.BindDenied_GuidanceReservesExactCanonicalListeningPrefix_AndDisposesTransport` (fake bind denial, no URLACL mutation) |
| `serverInfo.version` never drifts from `package.json` | `McpPackageVersionEditModeTests` |
| The protocol core stays engine-free | `McpArchitectureFitnessEditModeTests` |

## Worked example session

```
# 1. Connect (Claude Code)
$ claude mcp add --transport http coreai http://127.0.0.1:8590/mcp \
    --header "Authorization: Bearer $COREAI_MCP_TOKEN"

# 2. The agent lists tools (manage_mods only with host-admin authority configured)
-> tools/list
<- execute_lua, manage_mods, get_mod_logs, world_command, read_skill, screenshot, coreai_tools

# 3. The agent learns the world API before touching the game
-> tools/call read_skill { "name": "Rbx API" }
<- { "success": true, "skill": "Rbx API", "instructions": "<full Roblox-style API reference...>", "tools": [...], "usage": "..." }

# 4. It spawns a part with Lua, using globals from the reference
-> tools/call execute_lua { "code": "local p = Instance.new('Part') p.Name = 'Box' p.Position = Vector3.new(0, 1, 0) p.Parent = workspace return 'spawned Box'" }
<- { "Success": true, "Output": "spawned Box", "Error": null }

# 5. It reads what the loaded mods logged (one line per entry: [sequence] LEVEL modId - message)
-> tools/call get_mod_logs { "max_entries": 20 }
<- { "success": true, "count": 1, "logs": "[7] PRINT spawner - spawner ready" }

# 6. And sees the result
-> tools/call screenshot { "max_resolution": 768 }
<- image/png (base64)
```

## Architecture note (ARCHITECTURE_RULES §1 deviation)

This package is a **thin protocol adapter**, so it does not split into Domain / Application / Unity
assemblies; this section is the recorded justification that
[ARCHITECTURE_RULES §1](../../Docs/ARCHITECTURE_RULES.md#1-layering-assemblies-not-folders) requires
for skipping a layer. Instead the protocol + routing core (`Protocol/*`, `McpToolRegistry`,
`McpArguments`, `McpRpcDispatcher`, `McpSessionStore`, `McpRequestGuard`, `IMainThreadDispatcher`, and
the `IMcpTool` / `IScreenshotSource` interfaces) is kept **engine-free** and unit-tested
without Unity; only the adapters (`McpHttpServer`, `MainCameraScreenshotSource`, `CoreAiMcpServer`)
touch `UnityEngine` / `HttpListener`. `McpArchitectureFitnessEditModeTests` enforces that split by
checking that none of those core files references `UnityEngine` (grep-based, per
[§5](../../Docs/ARCHITECTURE_RULES.md#5-testing-rules)). Coupling to the
world/screenshot services is soft: those tools register only when their services resolve at runtime
(`CoreAiMcpToolProvider`).
