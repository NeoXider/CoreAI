# Determinism, Replay, and the Multiplayer Contract

This page states the guarantees CoreAI makes about recording what AI agents did, and what a
host-authoritative game should implement on top. The audit log described here ships today; CoreAI has
no replayer: the log is for inspection and verification.

## Why this page exists

"AI logic on the host, clients receive agreed outcomes" needs a contract: LLM output is
nondeterministic, so determinism in CoreAI is **not** "the model will say the same thing twice" — it is
"every effect the model had on the game that went through a recorded boundary is recorded, in order."

## What is guaranteed today

The immutable audit log (see [AUDIT_LOG.md](AUDIT_LOG.md); `AuditLogWriter`,
`persistentDataPath/CoreAI/Audit/audit.jsonl`) records a SHA-256-chained, append-only stream of:

| Entry kind | What it captures |
|---|---|
| `LlmRequest` / `LlmResponse` | Every model call and its outcome |
| `ToolCall` | Every executed tool invocation (name, arguments JSON, result or error, duration) |
| `WorldMutation` | Every command that went through `ICoreAiWorldCommandExecutor`: the `world_command` tool and every `ApplyAiGameCommand` routed by `AiGameCommandRouter` (for example the Lua `coreai_world_*` bindings) |
| `PolicyDecision` | Reserved — the entry kind exists, but nothing writes it yet |
| `ChainReset`, `RotationMarker`, `RotationAnchor`, `QueueDropped` | Chain integrity, log rotation and backpressure bookkeeping |

Combined with the serialized mutation chain (mutating tools of one turn execute strictly one at a time,
in call order — see [TOOL_CALLING_BEST_PRACTICES.md](../../CoreAI/Docs/TOOL_CALLING_BEST_PRACTICES.md#policy-enforced-mutation-ordering)),
this yields the property that matters for multiplayer:

> **Effect ordering:** the recorded world mutations and tool calls are totally ordered in a
> hash-chained log that detects accidental corruption and reordering, regardless of what the model
> "said". (The default chain is an unkeyed integrity checksum, not tamper-proof against the party that
> owns the file — see [AUDIT_LOG.md, "Threat model"](AUDIT_LOG.md#threat-model-what-the-chain-does-and-does-not-prove).)

What is **not** guaranteed:

- Token-level reproducibility of model output (temperature, provider drift, GPU nondeterminism).
- Effects that bypass the command boundary. Rbx API changes made from Lua (`Instance.new`, property
  writes) are not `WorldMutation` entries; only the `execute_lua` / `manage_mods` call that ran the
  code is recorded, as a `ToolCall` with its arguments. The same holds for direct C# your own tools
  perform: keep side effects inside tools if you want them recorded.
- Entries dropped under sustained backpressure: the writer's queue is bounded (10,000 entries) and
  records each drop as a `QueueDropped` entry.

## The host-authoritative pattern (what your game implements)

1. **Only the host runs agents.** Set **Ai Network Execution Policy** to `HostOnly` on
   `CoreAILifetimeScope` and supply a `CoreAiNetworkPeerBehaviour`; clients then never call the LLM
   ([MULTIPLAYER_AI.md](MULTIPLAYER_AI.md)).
2. **Outcomes travel, prompts do not.** Replicate the *effects* (spawn, stat change, item grant) —
   the same payloads that appear as `WorldMutation`/`ToolCall` entries — through your existing
   netcode, exactly as you would replicate any server-side game logic.
3. **Seed what you randomize.** If a tool rolls dice, roll them from your game's seeded RNG and
   include the result in the tool payload, so the recorded effect is self-contained.
4. **On desync, the host wins.** The host's state and its audit chain are the record of "what actually
   happened"; a client that disagrees re-syncs from replicated state as usual.
5. **Evidence against a client** only holds to the extent the client cannot rewrite it: keep the
   authoritative log host-side, and/or use a **keyed HMAC chain** (`AuditHash.HmacChain` +
   `AuditLogVerifier.Verify(path, hmacKey)`) with a host-held key the client never sees.

Tools for checking a log: `AuditLogVerifier.Verify` (one file), `AuditLogVerifier.VerifyChainedSet`
(a rotated set), `AuditLogVerifier.ReadAll` (parsed entries without verification).

## Related docs

- [TOOL_CALLING_BEST_PRACTICES.md](../../CoreAI/Docs/TOOL_CALLING_BEST_PRACTICES.md) — mutation ordering and duplicate suppression.
- [AUDIT_LOG.md](AUDIT_LOG.md) — writer durability, rotation, verification.
- [MULTIPLAYER_AI.md](MULTIPLAYER_AI.md) — where AI runs in a networked game.
