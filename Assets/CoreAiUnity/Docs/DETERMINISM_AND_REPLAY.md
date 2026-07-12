# Determinism, Replay, and the Multiplayer Contract

Status: **contract + design proposal** (2026-07-10). The audit log described here ships today;
the replayer does not yet. This page states the guarantees CoreAI can honestly make now, the
guarantees a replayer would add, and what a host-authoritative game should implement on top.

## Why this page exists

"AI logic on host, clients receive agreed outcomes" (README) is a claim that needs a contract:
LLM output is nondeterministic, so determinism in CoreAI is **not** "the model will say the same
thing twice" — it is "every effect the model had on the game is recorded, ordered, and
reproducible from the record."

## What is guaranteed today

The immutable audit log (see `AuditLogWriter`, `persistentDataPath/CoreAI/Audit/audit.jsonl`)
records a SHA-256-chained, append-only stream of:

| Entry kind | What it captures |
|---|---|
| `LlmRequest` / `LlmResponse` | Every model call and its outcome |
| `ToolCall` | Every executed tool invocation (name, args digest, result status) |
| `WorldMutation` | Every world-affecting effect that went through the command boundary |
| `PolicyDecision` | Duplicate suppression / serialization decisions by `ToolExecutionPolicy` |
| `ChainReset`, `RotationMarker`, `RotationAnchor` | Chain integrity and log rotation bookkeeping |

Combined with the serialized mutation chain (mutating tools execute strictly one at a time, in
order — see `TOOL_CALLING_BEST_PRACTICES.md`, "Policy-Enforced Mutation Ordering"), this yields
the property that matters for multiplayer:

> **Effect determinism:** the sequence of world mutations is totally ordered and recorded in a
> hash-chained log that detects accidental corruption and reordering. Two observers replaying the
> same mutation sequence reach the same world state, regardless of what the model "said".
> (The default chain is an unkeyed integrity checksum, not tamper-proof against the party that owns
> the file — see `AUDIT_LOG.md`, "Threat model".)

What is **not** guaranteed: token-level reproducibility of model output (temperature, provider
drift, GPU nondeterminism), and mutations that bypass the command boundary (direct C# your own
tools perform outside `world_command`/`execute_lua` — keep side effects inside tools if you want
them recorded).

## The host-authoritative pattern (what your game implements)

1. **Only the host runs agents.** Clients never call the LLM.
2. **Outcomes travel, prompts do not.** Replicate the *effects* (spawn, stat change, item grant) —
   the same payloads that appear as `WorldMutation`/`ToolCall` entries — through your existing
   netcode, exactly as you would replicate any server-side game logic.
3. **Seed what you randomize.** If a tool rolls dice, roll them from your game's seeded RNG and
   include the result in the tool payload, so the recorded effect is self-contained.
4. **On desync, the log wins.** The audit chain is the authority for "what actually happened";
   a client that disagrees re-syncs from replicated state as usual.

## Proposed next step: the replayer (not yet built)

A `CoreAI Audit Replayer` that consumes a verified `audit.jsonl` and re-applies the
`WorldMutation`/`ToolCall` stream against a scene, skipping `LlmRequest/Response` entries,
would unlock, in order of value:

1. **Bug reproduction** — attach a log to an issue; maintainer replays the exact session.
2. **Determinism tests** — CI replays a recorded session twice and asserts identical world hashes.
3. **Anti-cheat evidence** — the host's log records the sequence of AI-driven effects. This is only
   evidence against a client to the extent the client cannot rewrite it: keep the authoritative log
   host-side, and/or use a **keyed HMAC chain** (`AuditHash.HmacChain` +
   `AuditLogVerifier.Verify(path, hmacKey)`) with a host-held key the client never sees. The default
   unkeyed chain is an integrity checksum, not proof against the party that owns the file.
4. **Spectator/kill-cam style features** — replay as content, if a game wants it.

Prerequisites already in place: chain verification (`AuditLogVerifier`, `VerifyChainedSet`),
stable entry ordering, serialized mutations. Missing pieces: an idempotent apply-adapter per
mutation type, a world-state hash for assertions, and a CLI/editor entry point.

## Related docs

- `TOOL_CALLING_BEST_PRACTICES.md` — mutation ordering and duplicate suppression.
- `Assets/CoreAiUnity/Docs/AUDIT_LOG.md` — writer durability, rotation, verification.
- Root `README.md` — Multiplayer and Singleplayer section.
