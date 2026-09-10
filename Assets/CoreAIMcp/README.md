# CoreAI MCP Server (`com.neoxider.coreaimcp`)

An optional [Model Context Protocol](https://modelcontextprotocol.io) server that runs **inside a live
CoreAI game session** — Editor play mode or a shipped build — so an external agent drives the *running*
game over a standard protocol instead of editing files and restarting.

It is the in-game Command Bar surfaced over MCP: `execute_lua`, `get_mod_logs`, `screenshot`,
`read_skill`, plus `world_command` and `manage_mods` when the host composes them.

## Who this is for

- **AI-in-the-loop testing** — point a Claude Code / Codex / opencode session at the running game and
  let it spawn objects, load a mod, read the logs, and look at the result.
- **Live repair and iteration** — change behaviour without leaving play mode.
- **CI and reproduction** — script a session against a build over loopback HTTP.

## Quick start

Install `com.neoxider.coreaimcp` (it depends on `com.neoxider.coreaimods` and
`com.neoxider.coreaiunity`), then either add a **CoreAI MCP Server** component to a scene that also has
a `CoreAILifetimeScope`, or start it from code:

```csharp
using CoreAI.Mcp.Server;

CoreAiMcpServer server = CoreAiMcpServer.StartServer(port: 8590); // DontDestroyOnLoad host if none exists
string url = server.Url;          // http://127.0.0.1:8590/mcp
string token = server.AuthToken;  // null only when auth is disabled
```

```bash
claude mcp add --transport http coreai http://127.0.0.1:8590/mcp \
  --header "Authorization: Bearer $COREAI_MCP_TOKEN"
```

`StartServer` collects tools from the CoreAI composition, so it needs a built `CoreAILifetimeScope` in
the scene; without one it logs `No built CoreAI LifetimeScope; server not started.` and does nothing.
To run with **your own** catalog and no CoreAI composition at all:

```csharp
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
- **Not available in a WebGL player.** `CoreAiMcpServer.IsWebGlPlayer` short-circuits `StartListening`
  with a warning. The typed live catalog (`McpToolRegistry`) itself needs neither Unity nor sockets.
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
| **Off by default** | nothing starts until you add the component or call `StartServer()` | everything, until you opt in |
| **Loopback bind + `IsLocal`** | listener bound to `127.0.0.1`; remote sockets refused | off-box callers |
| **`Host` check** | must be `127.0.0.1`/`localhost`/`[::1]` on the server's port → else `403` | DNS rebinding |
| **`Origin` check** | absent (a real MCP client) or loopback-on-this-port → else `403` | browser CSRF |
| **Content type** | JSON media types only → else `415` | the preflight-free `text/plain` POST |
| **Bearer token** | `Authorization: Bearer <token>` → else `401` | **any other local process** |
| **Body cap** | 4 MB → else `413` | a local memory-exhaustion poke |

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

It is also readable from code as `CoreAiMcpServer.AuthToken` (or `CoreAiMcpServer.ActiveAuthToken`), which
is what an in-editor automation script should use.

> **Set `COREAI_MCP_TOKEN` (or the inspector field) if you want a stable client config.** With the
> random default, the token changes on every play session and the client must be re-pointed each time.

Unticking **Require Auth Token** disables token auth (the `Origin`/`Host`/loopback checks stay). Only do
that on a machine you fully trust — the server logs a warning for the whole session when you do.

## Install / enable

1. Ensure `com.neoxider.coreaimods` (and its `com.neoxider.coreaiunity` dependency) are in the project —
   the MCP tools wrap those services.
2. Add a **CoreAI MCP Server** component to any GameObject in a scene that also has a
   `CoreAILifetimeScope` (and, for the Lua tools, a `CoreAiModsLifetimeScope`).
3. Set the port (default **8590**) and, optionally, tick **Start On Enable**. Set **Auth Token** (or
   `COREAI_MCP_TOKEN`) if you want the same token every run.
4. Enter play mode. The console logs `CoreAI MCP server listening on http://127.0.0.1:8590/mcp` followed
   by the auth token line.

Or, from code:

```csharp
using CoreAI.Mcp.Server;

CoreAiMcpServer server = CoreAiMcpServer.StartServer(port: 8590); // DontDestroyOnLoad host if none exists
string token = server.AuthToken;                                  // null only when auth is disabled
// ...
CoreAiMcpServer.StopServer();
```

The component must stay **enabled on an active GameObject**: `Update()` is what drains the tool queue on
the Unity main thread. If it is disabled — or the game is paused — a `tools/call` fails after
**Main Thread Timeout Seconds** (default 30) with a JSON-RPC error naming the cause, instead of hanging
the client forever.

## Protocol

- Endpoint: **`POST http://127.0.0.1:<port>/mcp`**, JSON-RPC 2.0, MCP streamable HTTP.
- Methods: `initialize`, `notifications/initialized` (no-op), `tools/list`, `tools/call`, `ping`.
- **Response framing is negotiated by the `Accept` header:** clients that ask for `text/event-stream`
  get the single JSON-RPC response as one SSE `message` event; everyone else gets plain
  `application/json`. Both carry the same payload.
- For POST the session id is optional. `initialize` issues `Mcp-Session-Id`; it is required
  for the GET tool-change subscription, but it does not replace the bearer token.
- The server supports `2025-06-18` and returns that version during negotiation, including for an unknown
  client version. The client decides whether the supported version is compatible with its implementation.
- Errors: unknown method → `-32601`, unknown/absent tool name → `-32602`, malformed JSON → `-32700`,
  non-object JSON → `-32600`, a stalled main thread → `-32603` (message names the cause).
- Request parsing preserves JSON strings exactly, including timestamp-shaped request ids, tool names,
  and arguments. It does not infer dates or change their formatting; trailing content is rejected.
- Transport rejections: `401` — token, `403` — foreign Origin/Host or non-local address,
  `413` — body size exceeded in **UTF-8 bytes**, including chunked requests, `415` — not JSON,
  `408` — body not received in time, `503` — all 64 request-processing slots busy.
  The body is read for at most 10 seconds (`BodyReadTimeout`); a small rejected request with a known
  length is drained at most 64 KB and 250 ms. An incomplete body never blocks the rejection indefinitely.
- **`serverInfo.version` is the package version.** It comes from `McpServerInfo.Version`, which
  `tools/bump_version.py` rewrites together with every `package.json`; `McpPackageVersionEditModeTests`
  fails the build if the two ever drift apart.
- `GET /mcp` with `Accept: text/event-stream` and a valid `Mcp-Session-Id` opens an SSE subscription.
  After the HTTP server has started successfully, `initialize` advertises `capabilities.tools.listChanged: true`.
  Without Accept the server returns `406`, without a session — `400`, with an unknown/expired session — `404`.
  Sessions are limited by count and lifetime: after a `404` the client runs `initialize` again.

### Live catalog: switching stages without a restart

`McpToolRegistry` is available even without Unity. In a running game use `CoreAiMcpServer.Registry`:

```csharp
McpToolRegistry catalog = server.Registry;
catalog.AddOrReplace(briefingTool);
catalog.Remove("previous_stage");
catalog.Replace(new IMcpTool[] { sharedTool, nextStageTool });
```

`AddOrReplace` changes the binding and schema of a single name, `Remove` closes access to a name, `Replace`
publishes a complete new set in one operation. You can start from an empty catalog and add tools
later: `server.StartListening(new McpToolRegistry(null), listenPort: 8590)`. The standard
`StartListening()` still collects the available tools from the CoreAI composition.

Without Unity at all, assemble the same stack by hand — this is what the engine-free EditMode tests do:

```csharp
McpToolRegistry registry = new(new IMcpTool[] { myTool });
McpRpcDispatcher dispatcher = new(registry, new McpSessionStore(), new InlineMainThreadDispatcher());
McpHttpServer http = new(port, dispatcher, log, logError, authToken);
http.Start();
```

An `IMcpTool` is `Name` / `Description` / `InputSchemaJson` (must parse to `{"type":"object"}`) plus
`Task<McpToolResult> InvokeAsync(JObject arguments, CancellationToken cancellationToken)`; build results
with `McpToolResult.Text(...)` or `McpToolResult.Failure(...)`.

Each publication is atomic. Names, descriptions and schemas are frozen until the next registration;
changing the properties of the passed object alone does not change the declared schema. An invalid schema,
an empty name, or a duplicate name inside `Replace` rejects the whole update. For replacement use
`AddOrReplace` explicitly; the former "first duplicate wins" rule is removed. The name `coreai_tools`
is reserved for the automatically managed broker.

`Native`/`Dynamic` controls only schema exposure: Native is visible in `tools/list`, Dynamic —
via `coreai_tools` (`list`, `describe`, `call`). Both are available by direct name while they stay
in the catalog. For a targeted update you can specify `AddOrReplace(tool, McpToolResidency.Dynamic)`;
without the argument the host policy applies. A removed tool is unavailable both directly and via the broker.
The broker reads the current catalog and forbids recursively calling itself.

Before execution in the Unity queue, **the same binding** selected when the request arrived is re-checked.
Deleting, replacing, or remove/add of a single name before execution starts yields `-32602`
asking to re-read the tools. This also applies to the internal broker-mediated call tool.
An execution that has started may finish: a catalog change does not roll back actions already performed.
The catalog pins both bindings and an independent copy of the arguments in one call plan. The broker's internal JSON
is parsed once; after admission no new name lookup is performed. Therefore replacing
a tool between admission and body start cannot redirect old arguments to a new handler.

After publication, connected clients receive `notifications/tools/list_changed` and re-read
`tools/list`. `params._meta["coreai/catalogRevision"]` carries an increasing catalog revision.
On reconnect GET immediately sends the current revision; a new stream replaces the previous stream of the same
session. Only notifications about the current list are repeated, not tool calls.

There is one stream per session, 16 by default (`MaxNotificationStreams`); at most
64 HTTP requests are processed concurrently. Frequent changes are coalesced to the current revision:
notification history does not accumulate without bound. A heartbeat is sent every 15 seconds,
writes to a slow stream are limited to 5 seconds (`WriteTimeout`). Exhausting the subscription count yields
`409`, POST keeps being served in the remaining slots. `Stop()` cancels the transport and closes
connections; `Completion` can be awaited asynchronously for handlers to finish without blocking Unity.
The HTTP listener is unavailable in WebGL player; the typed live catalog does not depend on Unity or sockets.

### Game-thread queue and frame budget

HTTP accepts requests off the game thread. `CoreAiMcpServer` starts handlers from `Update`,
while the network handler asynchronously waits for the result. A single host admits at most
`MainThreadCallCapacity` calls (64), including the queue and bodies still executing;
`AdmittedMainThreadCalls` shows the occupied slots. Overflow immediately returns an error.

One `PumpMainThreadQueue` considers only the queue snapshot taken on entry: child calls wait for
the next frame. The time budget is checked between starts, 2 ms by default in the inspector.
A host with its own loop can call `PumpMainThreadQueue(TimeSpan budget)`; a zero budget starts
at most one handler so the queue keeps moving. There is no task waiting on the game thread.

**Main Thread Timeout Seconds** limits only the wait for the start. An expired item is removed
from the queue, frees its slot and receives `-32603`; a later frame does not execute it. After the start
this timer does not produce a false timeout: the result arrives after the body actually finishes.
`StopListening` removes pending calls and cancels the lifetime token of network requests. A body already started
receives cancellation but occupies its slot until it actually finishes, even after the server is restarted.
Other tools can use the free slots; asynchronous bodies may execute in parallel.

The budget is checked **between** handlers and does not interrupt arbitrary synchronous code. The host tool contract:
a short synchronous part, cooperative cancellation and asynchronous I/O; heavy computations should be
split or moved off the game thread, keeping Unity accesses on it. A handler that
permanently blocks the thread or ignores cancellation does not become safe thanks to the queue.

### Protocol version and expired sessions

A passed but unknown, evicted, or expired `Mcp-Session-Id` yields HTTP `404` for any
request, including POST: the tool body is not started. The client runs a new `initialize`
**without the old header**. POST without an identifier stays available for compatible stateless clients.
An unknown or malformed `MCP-Protocol-Version` yields HTTP `400`. `2025-06-18`
and the compatible `2025-03-26` are supported; `initialize` negotiates `2025-06-18`. If the version header is absent,
the negotiated version is used for a stored session, and `2025-03-26` compatibility without a session.

## Tools

Tools are registered **only when their backing service resolves** in the current composition, so
`tools/list` reflects what this particular game exposes.

| Tool | Present when | What it does |
|------|--------------|--------------|
| `execute_lua` | the Lua mod stack is installed | Runs a one-off snippet in the sandboxed Lua 5.2 VM. |
| `manage_mods` | the mod runtime resolves **and** host-admin authority is configured (see below) — **absent by default** | list / get_source / load / reload / unload / export / import / forget / versions / revert / diagnostics on persistent mods. |
| `get_mod_logs` | an `ILuaLogService` resolves | Reads mod `print`/`warn`/`error`/runtime-error output, independent of the Unity console. |
| `read_skill` | the Programmer role has skills | Returns the full text of a registered skill (e.g. `Lua Modding`, `Rbx API`) — the same reference the on-board agent reads. |
| `world_command` | a world-command executor resolves | Spawn / move / edit scene objects (meters; Euler degrees). |
| `screenshot` | always | Captures the main camera to a PNG (base64), downscaled to `max_resolution` (default 1024). Missing camera / capture failure is reported per call, with the real reason. |

Each tool ships a real JSON Schema in `tools/list` so clients validate arguments before calling.

### `manage_mods` and host-admin authority

`manage_mods` writes mods that survive a restart, so it is not published to an anonymous caller.
`CoreAiMcpToolProvider` registers it only when **all** of the following hold, and logs a distinct
warning naming the reason whenever one does not:

1. **Enable Host Admin Mod Management** is ticked (or `ConfigureHostAdminModManagement(...)` was called
   before the server started — it throws while `IsRunning`);
2. **Host Admin Actor Id** is non-blank;
3. host composition supplies an `IActorIdentityProvider`;
4. that provider's `Programmer` actor is `IsTrusted` **and** `Grants.IsUnrestricted`;
5. its `ActorId` matches the configured **Host Admin Actor Id** exactly.

```csharp
server.ConfigureHostAdminModManagement("studio-host");            // explicit id
server.ConfigureHostAdminModManagement(actorIdentityProvider);    // id taken from the composed actor
```

Turning it on logs a warning for the whole session, and the warning is the accurate description of the
risk: **every client holding the MCP bearer token receives this authority.**

### Tool residency: what the model pays for

`McpToolResidency` decides where a tool is *advertised*, not whether it works. `Native` tools appear in
`tools/list`; `Dynamic` tools are reached through the `coreai_tools` broker (`list` / `describe` /
`call`). Both stay callable by direct name while they are in the catalog.

The default policy is `McpToolResidencyPolicies.Default` (everything Native).
`McpToolResidencyPolicies.LeanDefault` keeps only the discovery tools resident —
`McpToolResidencyPolicies.DiscoveryToolNames` is `{ "read_skill", "memory" }` — and routes the rest
through the broker; the source records the measurement behind that choice on the real six-tool
composition: **9,324 B all-Native versus 768 B lean**. Operators can move individual tools without
recompiling through the environment variables `COREAI_MCP_DYNAMIC` and `COREAI_MCP_NATIVE`
(comma-separated tool names, read at composition time; Native wins).

## How external agents learn the API

There is **no separate skill file** for this server — the protocol is self-describing, so the knowledge
lives *in* the server:

1. Every `tools/list` description is written to be genuinely instructive (survival-minimum globals,
   action semantics, coordinate units).
2. `read_skill` returns the **same** `Lua Modding` and `Rbx API` reference documents the in-game
   Programmer agent uses — one source of truth, no duplicated docs. An external agent calls
   `read_skill('Rbx API')` and gets the exact datatypes/instances API the running game supports.

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

**Checks.** The behaviour this README claims is covered by named tests, not by assertion:

| Claim | Test |
|---|---|
| The catalog changes on a running server, over one live session, and the client is notified | `CoreAiMcpServerResidencyPlayModeTests.LiveCatalog_AddRemoveReplace_OverOneRunningHttpServerAndSession` (real `CoreAiMcpServer`, real loopback `HttpClient`, one session id throughout) |
| `list_changed` reaches the same session and a reconnect sees the current revision | `McpHttpServerIntegrationEditModeTests.LiveCatalogChanges_NotifySameSession_AndReconnectGetsCurrentRevision` |
| Publication is atomic; an invalid or duplicate schema rejects the whole update | `McpToolRegistryEditModeTests.InvalidSchemaUpdate_IsRejectedAtomically`, `Registry_DuplicateNamesAreRejected_WithoutChangingPublishedSet` |
| A catalog change between admission and execution cannot redirect old arguments | `McpToolRegistryEditModeTests.AdmittedInvocation_AfterReplacement_UsesOnlyCapturedBodyAndArguments`, `InvocationChangedBeforeAdmission_DoesNotAdmitOldOrReplacementBody`, `McpRpcDispatcherEditModeTests.QueuedCall_RejectsRemovedOrReplacedBinding` |
| A tool removed before its frame does not run | `CoreAiMcpServerResidencyPlayModeTests.QueuedCall_RemovedBeforeGameFrame_DoesNotRunOldOrReplacementBody` |
| Transport rejections (`401`/`403`/`413`/`415`), Host/Origin checks, UTF-8 byte cap, incomplete body | `McpHttpServerSecurityEditModeTests`, `McpRequestGuardEditModeTests` |
| The main-thread queue honours its budget, capacity and timeout | `CoreAiMcpServerMainThreadEditModeTests` |
| `serverInfo.version` never drifts from `package.json` | `McpPackageVersionEditModeTests` |
| The protocol core stays engine-free | `McpArchitectureFitnessEditModeTests` |

The Claude Code, Codex, opencode, and LM Studio **config snippets** follow each tool's published
configuration format and target this server's standard streamable-HTTP endpoint; the header syntax in
particular varies between client versions — check yours if the connection returns `401`.

## Worked example session

```
# 1. Connect (Claude Code)
$ claude mcp add --transport http coreai http://127.0.0.1:8590/mcp \
    --header "Authorization: Bearer $COREAI_MCP_TOKEN"

# 2. The agent lists tools
-> tools/list
<- execute_lua, manage_mods, get_mod_logs, read_skill, world_command, screenshot

# 3. The agent learns the world API before touching the game
-> tools/call read_skill { "name": "Rbx API" }
<- { "success": true, "skill": "Rbx API", "instructions": "<full Roblox-style API reference...>" }

# 4. It spawns a part with Lua, using globals from the reference
-> tools/call execute_lua { "code": "local p = Instance.new('Part') p.Name = 'Box' p.Position = Vector3.new(0, 1, 0) p.Parent = workspace report('spawned Box')" }
<- { "Success": true, "Output": "spawned Box" }

# 5. It reads back what the mod/script printed
-> tools/call get_mod_logs { "max_entries": 20 }
<- { "success": true, "count": 1, "logs": "[print] spawned Box" }

# 6. And sees the result
-> tools/call screenshot { "max_resolution": 768 }
<- image/png (base64)
```

## Architecture note (ARCHITECTURE_RULES §1 deviation)

This package is a **thin protocol adapter**, so it does not split into Domain / Application / Unity
assemblies. Instead the protocol + routing core (`Protocol/*`, `McpToolRegistry`, `McpArguments`,
`McpRpcDispatcher`, `McpSessionStore`, `McpRequestGuard`, `IMainThreadDispatcher`, the tool interfaces)
is kept **engine-free** and unit-tested
without Unity; only the adapters (`McpHttpServer`, `MainCameraScreenshotSource`, `CoreAiMcpServer`)
touch `UnityEngine` / `HttpListener`. `McpArchitectureFitnessEditModeTests` enforces that split
(grep-based, per §5). Coupling to the world/screenshot services is soft: those tools register only when
their services resolve at runtime (`CoreAiMcpToolProvider`).
