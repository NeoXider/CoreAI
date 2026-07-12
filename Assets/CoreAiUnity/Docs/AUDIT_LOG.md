# Audit Log — Append-Only, Hash-Chained

## Overview

The audit log records every significant interaction in a single append-only JSONL file:

- **LLM requests and responses** (send/receive, model, prompt hash)
- **Tool calls** (tool name, arguments, actor, policy decision, result, duration)
- **World mutations** (command type, payload, actor, success/failure)
- **Policy decisions** (allowed/denied/repaired)

All entries are written to one file (`persistentDataPath/CoreAI/Audit/audit.jsonl`) with a **SHA-256 hash chain**. The chain detects **accidental corruption, truncation and reordering** — it is an integrity checksum, **not** proof against a determined tamperer who owns the file (see [Threat model](#threat-model-what-the-chain-does-and-does-not-prove)).

## Architecture

```
Core           ┌─────────────────────┐
Portable       │ IAuditLog           │ ← NullAuditLog (no-op)
               │ AuditEntry          │ ← struct with Kind discriminator
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

| Kind | Published by | Captures |
|---|---|---|
| `LlmRequest` | `LlmAuditInterceptor` | traceId, actor, model, promptHash, routing profile |
| `LlmResponse` | `LlmAuditInterceptor` | traceId, actor, success/error |
| `ToolCall` | `ToolCallAuditInterceptor` | toolName, args, policyDecision, result, durationMs |
| `WorldMutation` | `AuditedWorldCommandExecutor` | commandTypeId, payload, actor, success, sourceTag |
| `PolicyDecision` | _future_ | authority host decision, guard results |
| `ChainReset` | `AuditLogWriter.ResumeChain` | audits that the hash chain was restarted from genesis (corrupt tail line or I/O failure on resume) |
| `RotationMarker` | `AuditLogWriter.RotateNow` | last entry of a file about to be rotated away; its hash is what the next file's anchor embeds |
| `RotationAnchor` | `AuditLogWriter.RotateNow` | first entry of a file created by rotation; its own `prevHash` is the previous file's final hash, not `""` |
| `QueueDropped` | `AuditLogWriter.FlushBatch` | audits that the bounded in-memory queue dropped older entries under sustained backpressure |

## File Format

JSONL (one JSON object per line). Each entry includes:

| Field | Description |
|---|---|
| `seq` | Monotonic sequence number |
| `ts` | UTC ISO-8601 timestamp |
| `kind` | Entry kind (see above) |
| `traceId` | Correlation id for the full LLM round-trip |
| `actor` | `roleId` / `lua:...` / `demo:...` / `player` / `system` |
| `model` | LLM client type (from `LlmBackendSelected`) |
| `promptHash` | SHA-256 of the assembled prompt |
| `toolName` | Tool or command type name |
| `args` | JSON: tool arguments or command payload |
| `policyDecision` | `allowed` / `denied` / `repaired` / `started` / `completed` / `failed` |
| `result` | `ok` / `error` / `pending` / `timeout` |
| `resultDetail` | Result JSON or error string |
| `durationMs` | Execution duration |
| `worldDiff` | JSON: command-scoped diff (future) |
| `rollbackHandle` | JSON: reverse action descriptor (future) |
| `sourceTag` | Origin tag for world mutations (e.g. `lua:world_command`, `demo:...`) |
| `prevHash` | SHA-256 of the previous line — chain root is `""` |
| `hash` | `SHA-256(prevHash + jsonLine)` |

## Chain Integrity

Every line includes `prevHash` and `hash`. **Canonical preimage:** the preimage of a line's hash is
that exact same line with the `hash` field set to `""` (every other field — including `ts` and
`prevHash` — stays exactly as stored):

```
preimage_N = jsonLine_N with "hash" set to ""
hash_1 = SHA256("" + preimage_1)
hash_2 = SHA256(hash_1 + preimage_2)
hash_3 = SHA256(hash_2 + preimage_3)
```

`AuditLogWriter.FlushBatch` builds each entry exactly once (one `seq`, one `ts`, `prevHash` = the
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

Any modification to any entry (including its `ts`) breaks the chain for that entry and all
subsequent ones **unless** the editor also recomputes every downstream `hash`.

### Threat model: what the chain does and does not prove

The default chain is an **unkeyed** `SHA-256`. Its preimage is fully known, so anyone who can read
and rewrite the file can also recompute a valid chain over doctored contents — the recomputation is
the same one `AuditLogVerifier` runs. Therefore the plain chain proves:

- **Accidental corruption** — a flipped bit, a partial write, a truncated tail line.
- **Reordering / deletion** — lines moved, dropped, or spliced from another file break `prevHash`
  linkage.
- **Naive edits** — any change that does not also re-chain every following line.

It does **not** prove:

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

- `IAuditLog.Record()` only **enqueues** to a `ConcurrentQueue` on the main thread — ~microseconds.
- A background loop (`AuditLogWriter`) flushes every 500ms, **draining the queue until empty** each
  tick (no longer capped at 10 entries per flush) so a burst cannot outrun the writer indefinitely:
  - SHA-256 of ~1 KB: ~tenths of microseconds
  - `File.AppendAllText` for one line: sub-millisecond
  - Total background overhead: negligible
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
- On WebGL, a successful flush also calls `CoreAi_PersistFsSync` (via the shared
  `CoreAiWebGlPersistence` helper) so the write reaches IndexedDB without waiting for
  `Application.Quit` — the same mechanism `FileAgentMemoryStore` and friends use.
- No gameplay thread blocking.

## Multiplayer Future

All audit entries are recorded locally per peer. The `IAuditLog.Record()` API is designed so that a future host mirror subscriber (`AuditEntryProduced` via MessagePipe) can receive entries for centralised storage without changing the recording strategy.

## Integration Points

| Hook | Location |
|---|---|
| Prompt hash | `AiOrchestrator.BuildRequestAsync` |
| Model + request | `LlmBackendSelected` + `LlmRequestStarted` (MessagePipe) |
| Tool call result | `CoreAi.OnToolCallCompleted` / `CoreAi.OnToolCallFailed` |
| World mutation | `AuditedWorldCommandExecutor` wraps `CoreAiWorldCommandExecutor` |
| VContainer | `AuditLogInstaller.RegisterAuditLog()` (called from `CoreServicesInstaller`) |
