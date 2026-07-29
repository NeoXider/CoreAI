# CoreAI MCP Server (`com.neoxider.coreaimcp`)

An optional [Model Context Protocol](https://modelcontextprotocol.io) server that runs **inside a live
CoreAI game session** (play mode in the Editor, or a shipped build) so an external agent — Claude Code,
Codex CLI, opencode, LM Studio, or any MCP client — can drive the running game over a standard protocol.
It is the in-game Command Bar surfaced over MCP: the same `execute_lua` / `manage_mods` / `get_mod_logs`
tools the on-board agent uses, plus `screenshot`, `world_command` when that service exists, and
`read_skill` to pull the exact Lua/Rbx API reference the game ships.

Use it for AI-in-the-loop testing, live repair, and CI: connect a Claude Code session to the running
game and let it spawn objects, load mods, read logs, and see the result.

## Security model — read this first

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
- **Sessions are optional.** `initialize` issues an `Mcp-Session-Id` header, but no call ever requires
  one — the server is stateless behind the scenes.
- **Version tolerant.** The server echoes the client's `protocolVersion` when present, otherwise
  advertises its latest; it never hard-fails on an unknown version.
- Errors: unknown method → `-32601`, unknown/absent tool name → `-32602`, malformed JSON → `-32700`,
  non-object JSON → `-32600`, a stalled main thread → `-32603` (message names the cause).
- Transport-level refusals never reach JSON-RPC: `401` (no/wrong token), `403` (foreign `Origin` or
  `Host`, non-local socket), `413` (body over 4 MB), `415` (non-JSON body).
- `GET /mcp` returns `405 Method Not Allowed` (this server offers no server-initiated SSE stream).
  Legacy HTTP+SSE-only clients should bridge with `npx mcp-remote` (see below).

### Main-thread semantics

HTTP requests arrive on `HttpListener` worker threads, but tool handlers touch live game state. Every
`tools/call` is therefore marshalled onto the Unity main thread: the `CoreAiMcpServer` component queues
the invocation and drains it from `Update()`, while the HTTP worker awaits a `TaskCompletionSource`.
Handlers run exactly as the in-game agent's do, one per drained frame.

If nothing drains the queue (paused game, disabled component) the call fails after
**Main Thread Timeout Seconds** with `-32603` and a message naming the likely cause; a call the client
already gave up on is never executed by a later frame. Stopping the server resolves everything still
queued instead of leaking it.

## Tools

Tools are registered **only when their backing service resolves** in the current composition, so
`tools/list` reflects what this particular game exposes.

| Tool | Present when | What it does |
|------|--------------|--------------|
| `execute_lua` | the Lua mod stack is installed | Runs a one-off snippet in the sandboxed Lua 5.2 VM. |
| `manage_mods` | the mod runtime resolves | list / get_source / load / reload / unload / export / import / forget / versions / revert / diagnostics on persistent mods. |
| `get_mod_logs` | an `ILuaLogService` resolves | Reads mod `print`/`warn`/`error`/runtime-error output, independent of the Unity console. |
| `read_skill` | the Programmer role has skills | Returns the full text of a registered skill (e.g. `Lua Modding`, `Rbx API`) — the same reference the on-board agent reads. |
| `world_command` | a world-command executor resolves | Spawn / move / edit scene objects (meters; Euler degrees). |
| `screenshot` | always | Captures the main camera to a PNG (base64), downscaled to `max_resolution` (default 1024). Missing camera / capture failure is reported per call, with the real reason. |

Each tool ships a real JSON Schema in `tools/list` so clients validate arguments before calling.

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

**Verification status:** the wire protocol (JSON-RPC framing, JSON + SSE responses, session-optional,
version echo, `405` on `GET`) and the admission rules (`401`/`403`/`413`/`415`) are covered by this
package's EditMode tests, including real loopback HTTP round trips. The Claude Code, Codex, opencode, and
LM Studio **config snippets** follow each tool's published configuration format and target this server's
standard streamable-HTTP endpoint; the header syntax in particular varies between client versions — check
yours if the connection returns `401`.

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
-> tools/call execute_lua { "code": "coreai_world_spawn({prefab='cube', name='Box', x=0, y=1, z=0}) report('spawned Box')" }
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
```
