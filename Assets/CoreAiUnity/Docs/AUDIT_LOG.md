# Audit Log — Append-Only, Hash-Chained

## Overview

The audit log records every significant interaction in a single append-only JSONL file:

- **LLM requests and responses** (start/finish, model, prompt hash, routing profile, streaming flag)
- **Tool calls** (tool name, arguments, actor, result or error, duration)
- **World mutations** that pass through `ICoreAiWorldCommandExecutor`: the `world_command` tool and every `ApplyAiGameCommand` the `AiGameCommandRouter` routes (command type, payload, actor, success/failure)

The audit log is wired by default: `CoreServicesInstaller.RegisterCore` calls `AuditLogInstaller.RegisterAuditLog()`, so nothing needs to be set up.

All entries are written to one file (`persistentDataPath/CoreAI/Audit/audit.jsonl`) with a **SHA-256 hash chain**. The chain detects **accidental corruption, partial writes and interior reordering/deletion** (a clean trailing truncation is caught by `Seq`/`ChainReset`/`VerifyChainedSet`, not single-file `Verify`) — it is an integrity checksum, **not** proof against a determined tamperer who owns the file (see [Threat model](#threat-model-what-the-chain-does-and-does-not-prove)).

## Architecture

```
Core           ┌─────────────────────┐
Portable       │ IAuditLog           │ ← NullAuditLog (no-op)
               │ AuditEntry          │ ← struct with AuditEntryKind discriminator
               │ AuditHash           │ ← SHA-256 (+ chain helper)
               │ AuditContext        │ ← traceId-keyed promptHash/model cache
               │ AuditLogVerifier    │ ← ReadAll() / Verify() — re-chains from genesis
               └─────────────────────┘

CoreAiUnity    ┌──────────────────────┐
(Runtime)      │ AuditLogWriter       │ ← background loop, rotation, chain
               │ LlmAuditInterceptor  │ ← subscribes LlmRequestStarted/Completed
               │ ToolCallAuditInterceptor│← subscribes CoreAi.OnToolCallCompleted/Failed
               │ AuditedWorldCommandExecutor│← decorates ICoreAiWorldCommandExecutor
               │ AuditLogInstaller    │ ← VContainer registration
               └──────────────────────┘
```

## Entry Kind

`AuditEntryKind` values:

| Kind | Published by | Captures |
|---|---|---|
| `LlmRequest` | `LlmAuditInterceptor` (on `LlmRequestStarted`) | traceId, actor, model, promptHash; `Args` holds `routingProfile` and `streaming` |
| `LlmResponse` | `LlmAuditInterceptor` (on `LlmRequestCompleted`) | traceId, actor, model, promptHash, success/error |
| `ToolCall` | `ToolCallAuditInterceptor` (on `CoreAi.OnToolCallCompleted` / `OnToolCallFailed`) | toolName, arguments JSON, result JSON or error, durationMs |
| `WorldMutation` | `AuditedWorldCommandExecutor` | commandTypeId, payload, actor, success, sourceTag |
| `PolicyDecision` | nothing yet | reserved; no code writes this kind |
| `ChainReset` | `AuditLogWriter.ResumeChain` | audits that the hash chain was restarted from genesis (corrupt tail line or I/O failure on resume) |
| `RotationMarker` | `AuditLogWriter.RotateNow` | last entry of a file about to be rotated away; its hash is what the next file's anchor embeds |
| `RotationAnchor` | `AuditLogWriter.RotateNow` | first entry of a file created by rotation; its own `prevHash` is the previous file's final hash, not `""` |
| `QueueDropped` | `AuditLogWriter.FlushBatch` | audits that the bounded in-memory queue dropped older entries under sustained backpressure |

## File Format

JSONL (one JSON object per line). The keys are the `AuditEntry` property names as Newtonsoft writes
them (PascalCase), except the two chain fields:

| Field | Description |
|---|---|
| `Seq` | Sequence number, monotonic within a chain segment; it restarts after a rotation anchor (`0`) or a chain reset |
| `Ts` | UTC ISO-8601 timestamp (set when the writer flushes the entry) |
| `Kind` | Entry kind as its `AuditEntryKind` integer value (`0` = `LlmRequest` … `8` = `QueueDropped`) |
| `TraceId` | Correlation id for the full LLM round-trip |
| `Actor` | Actor id (`local` for the default single-player identity); `system` for the writer's own entries |
| `Role` | Agent role id. The interceptors set it, but `AuditLogWriter` does not copy it into the stored line, so it is currently written as `""` |
| `Model` | LLM client type (from `LlmBackendSelected`) |
| `PromptHash` | SHA-256 of the assembled prompt (system prompt, user payload, chat history) |
| `ToolName` | Tool or command type name |
| `Args` | JSON: tool arguments or command payload |
| `PolicyDecision` | `allowed` / `denied` (tool calls), `allowed` / `failed` (world mutations), `started` / `completed` / `failed` (LLM), `reset` / `rotated` / `anchored` / `backpressure` (writer entries) |
| `Result` | `ok` / `error` / `pending` / `dropped` |
| `ResultDetail` | Result JSON or error string |
| `DurationMs` | Execution duration |
| `WorldDiff` | Reserved; currently always `""` |
| `RollbackHandle` | Reserved; currently always `""` |
| `SourceTag` | Origin tag for world mutations (e.g. `lua:world_command`, `lua:component_command`) |
| `prevHash` | SHA-256 of the previous line — chain root is `""` |
| `hash` | `SHA-256(prevHash + preimage)`, see below |

## Chain Integrity

Every line includes `prevHash` and `hash`. **Canonical preimage:** the preimage of a line's hash is
that exact same line with the `hash` field set to `""` (every other field — including `Ts` and
`prevHash` — stays exactly as stored):

```
preimage_N = jsonLine_N with "hash" set to ""
hash_1 = SHA256("" + preimage_1)
hash_2 = SHA256(hash_1 + preimage_2)
hash_3 = SHA256(hash_2 + preimage_3)
```

`AuditLogWriter.FlushBatch` builds each entry exactly once (one `Seq`, one `Ts`, `prevHash` = the
current chain head) before ever serializing it, so the bytes that get hashed are the same bytes
that (modulo the `hash` field) end up on disk — there is nothing hidden from the hash.

To verify: use `AuditLogVerifier.Verify(filePath)`. It re-chains from genesis (`prevHash = ""`) by,
for each line: parsing it, checking the stored `prevHash` equals the running chain head, blanking
the `hash` field, recomputing `SHA256(runningPrevHash + preimage)`, and comparing against the
stored `hash`. It returns `{ Ok, FirstBrokenSeq, LineCount, ChainResetCount, Error }` — `Ok` is
false at the first line whose `prevHash` or `hash` doesn't match, or that fails to parse at all
(e.g. a truncated tail line), and `FirstBrokenSeq` identifies it. A legitimate `ChainReset` entry
is accepted as the start of a new chain segment instead of failing verification, but every
mid-file reset is counted in `ChainResetCount` and reported with a warning — a self-hashed
`ChainReset` line could otherwise hide tail truncation, so resets are operator-visible, never
silent. `AuditLogVerifier.ReadAll(filePath)` returns the
parsed `AuditEntry` list for inspection without verifying the chain.

Any modification to any entry (including its `Ts`) breaks the chain for that entry and all
subsequent ones **unless** the editor also recomputes every downstream `hash`.

### Threat model: what the chain does and does not prove

The default chain is an **unkeyed** `SHA-256`. Its preimage is fully known, so anyone who can read
and rewrite the file can also recompute a valid chain over doctored contents — the recomputation is
the same one `AuditLogVerifier` runs. Therefore the plain chain proves:

- **Accidental corruption** — a flipped bit, a partial write, or a partial/unparsable truncated tail
  line (a half-written final record).
- **Interior reordering / deletion** — lines moved, dropped, or spliced *from within the chain*, or
  from another file, break `prevHash` linkage.
- **Naive edits** — any change that does not also re-chain every following line.

It does **not** prove:

- **Detection of a clean trailing truncation by `Verify` alone.** Removing whole trailing lines leaves
  the remaining prefix internally consistent, so `AuditLogVerifier.Verify` on that file alone returns
  `Ok`. A dropped tail is surfaced instead by the per-entry `Seq` gap, the writer's `ChainReset`
  resume marker, or a cross-file `VerifyChainedSet` — not by single-file chain verification.
- **Tamper-resistance against the party that owns the file.** On a local, client-side log the
  client owns the bytes and the algorithm, so no purely local scheme is tamper-proof. Do not treat
  the plain chain as signed evidence or as anti-cheat proof against that party.

### Keyed (HMAC) chains for real tamper-evidence

When tamper-evidence against the file owner is actually required, use the **opt-in** keyed path:
`AuditHash.HmacChain(key, prevHash, jsonLine)` (HMAC-SHA256) and verify with
`AuditLogVerifier.Verify(path, hmacKey)` / `VerifyChainedSet(paths, hmacKey)`. This is tamper-evident
**only** to the extent the key is withheld from whoever owns the file — e.g. a host- or
server-held session key in the host-authoritative pattern (see `DETERMINISM_AND_REPLAY.md`), where
the untrusted client never sees the key. The default `AuditLogWriter` writes the **unkeyed** chain;
wiring a keyed writer plus key custody is left to the integrator and is out of scope for the local
client log. A wrong key (or verifying a keyed file as unkeyed, or vice versa) fails verification.

### Resuming after corruption

If `AuditLogWriter` cannot resume the chain on startup — the tail line fails to parse, or the file
can't be read — it does **not** silently reset the chain. It logs an error and appends a
`ChainReset` entry (`AuditEntry.ForChainReset`) as the first entry of the new chain segment, so the
reset itself is an audited, chain-linked event rather than a hole that looks like a fresh log.

### Rotation: linked, independently-verifiable files

At 50 MB, `AuditLogWriter` rotates the active file instead of growing it forever. Rotation writes
two administrative entries so each file verifies **standalone** while the pair still proves the
**set** is linked:

1. A `RotationMarker` entry is appended as the **last** line of the file being rotated away,
   chained normally from the current head. Its resulting hash is the cross-file link.
2. The old file is renamed aside (`audit_0001.jsonl`, `audit_0002.jsonl`, ...).
3. A `RotationAnchor` entry becomes the **first** line of the new active file. Unlike every other
   entry, its own stored `prevHash` is the previous file's final hash — not `""`.

`AuditLogVerifier.Verify(path)` treats a leading `RotationAnchor`'s stored `prevHash` as the seed
for that file's chain (instead of requiring `""`), so the new file verifies on its own without
reading the old one — the cross-file link lives in the bytes, not in verifier state.
`AuditLogVerifier.VerifyChainedSet(pathsInOrder)` additionally checks that each file's anchor
`prevHash` equals the previous file's final line hash, proving the whole rotated set is one chain.

If the writer crashes between appending the marker and renaming the file, the next rotation attempt
detects the existing trailing `RotationMarker` and reuses its hash instead of appending a duplicate.

## Performance

- `IAuditLog.Record()` only **enqueues** to a `ConcurrentQueue` — ~microseconds.
- `AuditLogWriter` flushes every 500 ms, **draining the queue until empty** each tick so a burst
  cannot outrun the writer indefinitely. Outside WebGL this runs on a dedicated background thread
  (`CoreAI-AuditLogWriter`); WebGL has no threads, so there it is a main-thread `UniTask` loop.
  Serialization, SHA-256 and the batched file append are each small (sub-millisecond for typical
  entries).
- The queue is bounded (10,000 entries). If producers still outrun the flush loop, the **oldest**
  entries are dropped and the cumulative drop count is itself audited as a periodic `QueueDropped`
  marker — so sustained backpressure shows up in the log instead of silently growing without bound.
- `_seq`/`_prevHash` (the chain head) are only advanced **after** the file append succeeds. On a
  write failure the batch is put back at the front of the queue and retried on the next flush tick,
  so the chain can never reference a record that isn't actually on disk.
- `Dispose()` drains the entire queue (not just one batch), bounded by a ~2s deadline so a stuck
  disk can't hang shutdown forever.
- The writer's lifetime is **scope-owned**: it is created and disposed with its DI lifetime scope,
  so scene reloads cannot leak background flush loops writing the same file concurrently, and the
  tail is flushed on scope teardown.
- All flush entry points (the 500ms timer, `Dispose()`, and the test-only `FlushForTesting()`) share
  one lock so they can never interleave and corrupt the chain head.
- Rotation at 50 MB → `audit_0001.jsonl`, `audit_0002.jsonl`, etc. (see above).
- On WebGL, a successful flush also checks `CoreAiWebGlPersistence.Sync()` — the shared helper that
  reports whether the engine's automatic `persistentDataPath` persistence is armed for this page, the
  same mechanism `FileAgentMemoryStore` and friends use. It requires
  `config.autoSyncPersistentDataPath = true` in the web template.
- No gameplay thread blocking outside WebGL.

## Multiplayer

Each peer records its own audit log locally; nothing sends entries to a host or merges them. A game
that needs a central log replaces or wraps `IAuditLog` in DI.

## Integration Points

| Hook | Location |
|---|---|
| Prompt hash | `AiOrchestrator.BuildRequestAsync` (`AuditContext.SetPromptHash`; recomputed for a structured retry) |
| Model + request | `LlmBackendSelected` + `LlmRequestStarted` / `LlmRequestCompleted` (MessagePipe) |
| Tool call result | `CoreAi.OnToolCallCompleted` / `CoreAi.OnToolCallFailed` |
| World mutation | `AuditedWorldCommandExecutor` wraps `CoreAiWorldCommandExecutor` (`WorldCommandsInstaller`, whenever an `IAuditLog` is registered) |
| VContainer | `AuditLogInstaller.RegisterAuditLog()` (called from `CoreServicesInstaller.RegisterCore`) |

Rbx API changes made from Lua (`Instance.new`, property writes) do not pass through
`ICoreAiWorldCommandExecutor`, so they produce no `WorldMutation` entry; the `execute_lua` /
`manage_mods` call that ran the code is recorded as a `ToolCall`.
