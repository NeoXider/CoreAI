# Bring Your Own Subscription (Subscription Bridge)

Power CoreAI's in-game AI from a Claude Code or Codex CLI subscription instead of
paid per-token API keys — by pointing CoreAI at a local, OpenAI-compatible bridge
that translates chat requests into CLI subprocess calls.

> This is a **development / testing / single-player-tinkering** convenience, not a
> production transport. Read the [limitations](#honest-limitations) before you rely
> on it. For shipped games or latency-sensitive NPCs, use a real API key.

## What it is

CoreAI talks to any OpenAI-compatible `/v1/chat/completions` endpoint. That is the
primary, supported path — LM Studio, OpenAI, Qwen, or your own proxy all work by
setting the **Base URL** in `CoreAISettings` (or the `COREAI_TEST_BASE_URL`
environment variable for tests). See
[COREAI_SETTINGS.md](COREAI_SETTINGS.md) and
[RUNTIME_BACKEND_SWITCHING.md](RUNTIME_BACKEND_SWITCHING.md).

The **subscription bridge** is just another such endpoint. It is a small local HTTP
server (from the `neoxider-agents` skill's `agent.sh openai-server`) that exposes a
wire-compatible `/v1/chat/completions` API and, for each request, shells out to your
already-installed Claude Code or Codex CLI. The CLI runs under **your existing
subscription**, so the subscription becomes the model backend for the game — no
metered API tokens are spent.

```
Unity (CoreAI)  --HTTP /v1/chat/completions-->  agent.sh openai-server  --subprocess-->  Claude Code / Codex CLI
   Base URL: http://127.0.0.1:8801/v1                (local bridge)                          (your subscription)
```

## Why use it

- **Free iteration on your subscription.** Prototype NPC dialogue, quests, tool
  calling, and roles without accruing per-token API charges during development.
- **No key handling in dev.** Nothing changes in your CoreAI wiring beyond the Base
  URL — the game keeps speaking plain OpenAI-compatible HTTP.
- **Same models you already use in the CLI.** Pick the engine (`claude`, `codex`)
  and model the bridge exposes.

## Setup

### 1. Start the bridge

From a terminal (the `neoxider-agents` skill provides `agent.sh`):

```bash
agent.sh openai-server -e claude -m sonnet -p 8801
```

- `-e` / engine — `claude` (Claude Code) or `codex` (Codex CLI).
- `-m` / model — the CLI model to invoke (e.g. `sonnet`).
- `-p` / port — the local port to listen on (`8801` here).

Leave it running. It serves on `http://127.0.0.1:8801/v1`.

### 2. Point CoreAI at the bridge

In `CoreAISettings` (Project Settings or the settings asset), set:

| Field | Value |
|---|---|
| **Base URL** | `http://127.0.0.1:8801/v1` (no trailing `/`) |
| **API Key** | leave **empty** — the bridge ignores it |
| **Model** | the model the bridge advertises (must match `-m`, e.g. `sonnet`) |

> ⚠️ Do not park a placeholder key here. If this `CoreAISettings` asset sits under a `Resources/`
> folder, any non-empty `apiKey`/`secondaryApiKey` **aborts the player build** (`Resources` assets ship
> inside the player). If your bridge really requires a non-empty header, set it at runtime with
> `CoreAiBackend.SetApiKey` instead of storing it on the asset.

For tests, set `COREAI_TEST_BASE_URL=http://127.0.0.1:8801/v1` instead.

### 3. Pick a role

Assign an agent role as usual (see [AI_AGENT_ROLES.md](AI_AGENT_ROLES.md)). Roles,
prompts, memory, and tool definitions all work unchanged — the bridge is transparent
to them. Enter Play Mode and chat.

## Honest limitations

The bridge is a **wire-compatible shim over a CLI**, not a low-latency inference API.
Expect the following, by design:

| Aspect | Behavior | Consequence |
|---|---|---|
| **Latency** | Several seconds to first token per call (a full CLI subprocess starts each request) | Unusable for real-time / reactive NPCs |
| **Concurrency** | One bridge process serves **one conversation at a time** (a lock serializes requests) | No parallel agents / multiplayer against a single bridge |
| **Streaming** | Real on the `claude` engine; other engines may not stream | Fine for the chat panel; verify per engine |
| **Tool calling** | **Emulated via prompting** (best-effort), not native function-calling | Tool calls can be missed or malformed; test them |
| **Token counts** | **Estimates**, not provider-reported usage | Do not use for billing/quota accounting |

Because of these, the bridge is ideal for **development, testing, and single-player
tinkering**, and **not** for production multiplayer NPCs or anything latency-sensitive.

## When to use what

| Use case | Choose |
|---|---|
| Local dev / prototyping on your own subscription, single conversation | **Subscription bridge** (this doc) |
| Production, shipped builds, multiplayer, latency-sensitive NPCs, parallel agents | **A real API key** — a proper OpenAI-compatible provider ([COREAI_SETTINGS.md](COREAI_SETTINGS.md), [SHIPPING_PLAYER_MACHINES.md](SHIPPING_PLAYER_MACHINES.md)) |
| Letting an **external** Claude Code session drive a running game (`execute_lua`, `world_command`, `screenshot`) for AI-in-the-loop testing / CI | **CoreAI MCP server** ([../../CoreAIMcp/README.md](../../CoreAIMcp/README.md)) |

The subscription bridge and the MCP server are **opposite directions** and should not
be confused:

- **Subscription bridge** — feeds a model **into** the game; it is the game's *brain*
  during development.
- **MCP server** — lets an external agent **drive** the game from outside; it is a
  *control / testing channel*, not the game's brain.

## Troubleshooting

- **Bridge not reachable / connection refused.** Confirm `agent.sh openai-server` is
  still running and the port matches the Base URL (`8801` here). Base URL must end in
  `/v1` with **no** trailing slash. The bridge binds to `127.0.0.1`, so the game and
  the bridge must be on the same machine.
- **Slow first token.** Expected. Each request cold-starts a CLI subprocess; several
  seconds to first token is normal. This is not a bug — see
  [limitations](#honest-limitations).
- **Second request hangs or queues.** One bridge serves one conversation at a time;
  requests are serialized by a lock. Do not fire parallel agents at a single bridge —
  run one conversation, or start additional bridges on other ports for isolated tests.
- **Model mismatch errors.** The **Model** in `CoreAISettings` must match the model
  the bridge was started with (`-m`).
- **Tool calls not firing.** Tool calling is prompt-emulated, best-effort. Verify with
  a real API key if a tool-dependent flow must be reliable.

## See also

- [COREAI_SETTINGS.md](COREAI_SETTINGS.md) — Base URL, model, API key, timeouts.
- [RUNTIME_BACKEND_SWITCHING.md](RUNTIME_BACKEND_SWITCHING.md) — multi-endpoint registry and provider switching.
- [SHIPPING_PLAYER_MACHINES.md](SHIPPING_PLAYER_MACHINES.md) — production deployment modes.
- [../../CoreAIMcp/README.md](../../CoreAIMcp/README.md) — the MCP server (drive the game from outside).
