# CoreAI MCP bridge

An MCP endpoint for a CoreAI game that cannot listen on a port, or cannot be reached where it listens: a
WebGL player (a browser page cannot open a listening socket), a phone (its loopback is not the developer's
machine), or a desktop player started with `-coreaiMcpBridge` to test this path. The game
links **out** to this process and long-polls it for MCP requests; an MCP client (Claude Code and the
like) connects to `http://127.0.0.1:<port>/mcp` here as to any streamable-HTTP MCP server. Every request
is answered by the game's own MCP dispatcher, so the tools, sessions and errors are the ones the in-game
server gives.

Python 3 standard library only (developed and tested on Python 3.14.5). Binds `127.0.0.1` only.

## Start it

From the repository root:

```powershell
python tools/McpBridge/coreai_mcp_bridge.py
```

It prints the page link and the client command:

```
CoreAI MCP bridge listening on http://127.0.0.1:8591 (loopback only)
  game page:  http://localhost:8777/?coreaiMcp=8591#coreaiMcpKey=<key>
  client:     claude mcp add --transport http coreai http://127.0.0.1:8591/mcp --header "Authorization: Bearer <token>"
```

| Flag | Default | Meaning |
| --- | --- | --- |
| `--port` | `8591` | loopback port of the bridge (the page's `coreaiMcp=` value) |
| `--page-url` | `http://localhost:8777/` | the served WebGL page, used only for the printed link |
| `--takeover` | off | a newly attaching game replaces the attached one instead of being refused |
| `--request-timeout` | `120` | seconds a client request waits for the game's answer (then `504`) |
| `--attach-wait` | `20` | seconds a client request waits for a game to attach (then `503`) |
| `--lan ADDRESS` | off | also listen on this LAN IPv4 address for the game routes only, for a phone without adb; refuses a game key under 16 characters, loopback and `0.0.0.0` |

Environment: `COREAI_MCP_TOKEN` fixes the client token, `COREAI_MCP_BRIDGE_KEY` fixes the game key (and so
the page link). Without them both are generated at every start.

## Connect a WebGL build

1. Serve the build as usual (for example `tools/webgl-launcher/serve.ps1` on `http://localhost:8777/`).
2. Start the bridge (above).
3. Open the printed page link. The game reads `coreaiMcp=<port>` from the query and the key from the
   fragment, attaches, and logs `[CoreAI MCP] Attached to the MCP bridge` in the browser console. The Hub's
   MCP tab shows the link, and its last error if the bridge refused or could not be reached.
4. Run the printed `claude mcp add` command (or the curl below).

```powershell
curl.exe -s http://127.0.0.1:8591/mcp -H "Authorization: Bearer <token>" -H "Content-Type: application/json" -d '{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/call\",\"params\":{\"name\":\"game_status\"}}'
curl.exe -s http://127.0.0.1:8591/bridge/status -H "Authorization: Bearer <token>"
```

Keep the tab in the foreground: a hidden tab runs one or two frames a second, and every request waits
for the game's next frames.

## Connect a phone

The phone links out to this bridge; the client stays on the computer's loopback.

- **Android with USB debugging:** `adb reverse tcp:8591 tcp:8591`. The phone's `127.0.0.1:8591` is then
  this loopback bridge, and nothing listens on the network. In the game's Hub MCP tab keep the address
  `127.0.0.1`, type the key and press **Connect to bridge**.
- **iOS, or Android without adb:** `python tools/McpBridge/coreai_mcp_bridge.py --lan 192.168.1.20` with
  `COREAI_MCP_BRIDGE_KEY` set to a key of at least 16 characters. A second listener on that address serves
  only `/bridge/*` and `/health` (`/mcp` and `/bridge/status` answer `403` there). Type that address, the
  port and the key in the Hub MCP tab.

Anyone on that network who holds the key can attach as the game and receive (and answer) the client's
requests; nobody can drive the game from the network, because `/mcp` is loopback only. Use `--lan` on a
network you trust, with a key made for the session, and stop the bridge when done.

The build needs **Allow downloads over HTTP** set to *Allowed in development builds* (the bridge is plain
HTTP); Android development builds request the INTERNET permission by default. Written, not run on a
device.

## Endpoints

| Route | Caller | What it does |
| --- | --- | --- |
| `POST /mcp`, `DELETE /mcp` | MCP client, `Authorization: Bearer <token>`, no `Origin` | forwarded to the game; the answer comes back with its status, `Mcp-Session-Id` and the client's framing (`text/event-stream` when `Accept` asks) |
| `GET /mcp` | MCP client | `405`: the bridge offers no SSE stream, so `notifications/tools/list_changed` is not relayed (`initialize` says `listChanged` is off; re-read `tools/list` after a scene load) |
| `POST /bridge/attach` | the game, `Authorization: Bearer <key>` | `200`; `409` naming the attached game while another one is alive; `401` for a wrong key |
| `POST /bridge/next` | the game | long poll (8 s): `200` with one request envelope, `204` when none came, `409` when this game is no longer the attached one |
| `POST /bridge/reply` | the game | the answer to one envelope |
| `POST /bridge/detach` | the game | the game leaves (the server stopped) |
| `GET /bridge/status` | `Authorization: Bearer <token>` | the attached game, queue length and counters |
| `GET /health` | anyone on loopback | `{"ok": true, "attached": bool}` |

One game at a time. A second page is refused with the name, platform and instance of the attached one
until the first is gone; a reloaded page is let in as soon as the bridge notices that its old poll was
abandoned (well under a second), or at once with `--takeover`.

## Security

See the threat model in [the MCP package README](../../Assets/CoreAIMcp/README.md#connect-to-a-webgl-build).
In short: loopback bind; the `Host` header must name this loopback port; `/mcp` refuses any request that
carries an `Origin` and needs the client token; `/bridge/*` answers CORS only for
`http://localhost:*`, `http://127.0.0.1:*` and `http://[::1]:*` pages and needs the game key, which
reaches the page in the URL fragment and is never sent to the page server.

## Tests

```powershell
python tools/McpBridge/test_coreai_mcp_bridge.py
```

17 tests with a fake game on an ephemeral port: the round trip (body text unchanged, session and protocol
header relayed), SSE framing, notifications and DELETE, a game-side rejection keeping its status, the
client token and the Origin refusal, DNS rebinding, `503` with no game, the game key, the second-game
refusal and `--takeover`, an abandoned poll freeing the bridge, CORS only for loopback pages, `504` for a
game that does not answer, the status endpoint, and the LAN listener (game routes only, the client
refused there, a short key and a loopback or wildcard address refused).
