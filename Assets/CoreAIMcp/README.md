# CoreAI MCP Server (`com.neoxider.coreaimcp`)

An optional [Model Context Protocol](https://modelcontextprotocol.io) server that runs **inside a live
CoreAI game session** (play mode in the Editor, or a shipped build) so an external agent — Claude Code,
Codex CLI, opencode, LM Studio, or any MCP client — can drive the running game over a standard protocol.
It is the in-game Command Bar surfaced over MCP: the same `execute_lua` / `manage_mods` / `get_mod_logs`
tools the on-board agent uses, plus `world_command` and `screenshot` when those services exist, and
`read_skill` to pull the exact Lua/Rbx API reference the game ships.

Use it for AI-in-the-loop testing, live repair, and CI: connect a Claude Code session to the running
game and let it spawn objects, load mods, read logs, and see the result.

## Security model — read this first

- **Off by default.** Nothing starts until you add a `CoreAiMcpServer` component to a scene (or call
  `CoreAiMcpServer.StartServer()`). There is no auto-start.
- **Localhost only.** The HTTP listener binds strictly to `127.0.0.1`.
- **No authentication.** Any local process can call it. That is acceptable **only** on loopback.
  Never bind it to `0.0.0.0`, never forward the port through a tunnel or reverse proxy, and never expose
  it beyond the machine without adding auth first. Treat an open MCP port as full control of the game.

## Install / enable

1. Ensure `com.neoxider.coreaimods` (and its `com.neoxider.coreaiunity` dependency) are in the project —
   the MCP tools wrap those services.
2. Add a **CoreAI MCP Server** component to any GameObject in a scene that also has a
   `CoreAILifetimeScope` (and, for the Lua tools, a `CoreAiModsLifetimeScope`).
3. Set the port (default **8590**) and, optionally, tick **Start On Enable**.
4. Enter play mode. The console logs `CoreAI MCP server listening on http://127.0.0.1:8590/mcp`.

Or, from code:

```csharp
using CoreAI.Mcp.Server;

CoreAiMcpServer.StartServer(port: 8590); // creates a DontDestroyOnLoad host if none exists
// ...
CoreAiMcpServer.StopServer();
```

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
  non-object JSON → `-32600`.
- `GET /mcp` returns `405 Method Not Allowed` (this server offers no server-initiated SSE stream).
  Legacy HTTP+SSE-only clients should bridge with `npx mcp-remote` (see below).

### Main-thread semantics

HTTP requests arrive on `HttpListener` worker threads, but tool handlers touch live game state. Every
`tools/call` is therefore marshalled onto the Unity main thread: the `CoreAiMcpServer` component queues
the invocation and drains it from `Update()`, while the HTTP worker awaits a `TaskCompletionSource`.
Handlers run exactly as the in-game agent's do, one per drained frame.

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
| `screenshot` | a camera exists | Captures the main camera to a PNG (base64), downscaled to `max_resolution` (default 1024). |

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

### Claude Code (native streamable HTTP)

```bash
claude mcp add --transport http coreai http://127.0.0.1:8590/mcp
```

### Codex CLI (`~/.codex/config.toml`)

Codex speaks stdio; bridge to our HTTP endpoint with `mcp-remote`:

```toml
[mcp_servers.coreai]
command = "npx"
args = ["-y", "mcp-remote", "http://127.0.0.1:8590/mcp"]
```

Newer Codex builds that accept a streamable-HTTP `url` directly can point at
`http://127.0.0.1:8590/mcp` without the bridge; check your Codex version's docs.

### opencode (`opencode.json`)

```json
{
  "mcp": {
    "coreai": {
      "type": "remote",
      "url": "http://127.0.0.1:8590/mcp",
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
      "url": "http://127.0.0.1:8590/mcp"
    }
  }
}
```

### Any stdio-only client

```bash
npx -y mcp-remote http://127.0.0.1:8590/mcp
```

**Verification status:** the wire protocol (JSON-RPC framing, JSON + SSE responses, session-optional,
version echo, `405` on `GET`) is covered by this package's EditMode tests, including a real loopback
HTTP round trip. The Claude Code, Codex, opencode, and LM Studio **config snippets** follow each tool's
published configuration format and target this server's standard streamable-HTTP endpoint; adapt paths
to your installed client version.

## Worked example session

```
# 1. Connect (Claude Code)
$ claude mcp add --transport http coreai http://127.0.0.1:8590/mcp

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
assemblies. Instead the protocol + routing core (`Protocol/*`, `McpToolRegistry`, `McpRpcDispatcher`,
`McpSessionStore`, `IMainThreadDispatcher`, the tool interfaces) is kept **engine-free** and unit-tested
without Unity; only the adapters (`McpHttpServer`, `MainCameraScreenshotSource`, `CoreAiMcpServer`)
touch `UnityEngine` / `HttpListener`. `McpArchitectureFitnessEditModeTests` enforces that split
(grep-based, per §5). Coupling to the world/screenshot services is soft: those tools register only when
their services resolve at runtime (`CoreAiMcpToolProvider`).
```
