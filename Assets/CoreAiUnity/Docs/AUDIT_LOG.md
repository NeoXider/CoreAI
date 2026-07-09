# Audit Log — Immutable, Append-Only, Tamper-Evident

## Overview

The audit log records every significant interaction in a single append-only JSONL file:

- **LLM requests and responses** (send/receive, model, prompt hash)
- **Tool calls** (tool name, arguments, actor, policy decision, result, duration)
- **World mutations** (command type, payload, actor, success/failure)
- **Policy decisions** (allowed/denied/repaired)

All entries are written to one file (`persistentDataPath/CoreAI/Audit/audit.jsonl`) with a **SHA-256 hash chain** for tamper evidence.

## Architecture

```
Core           ┌─────────────────────┐
Portable       │ IAuditLog           │ ← NullAuditLog (no-op)
               │ AuditEntry          │ ← struct with Kind discriminator
               │ AuditHash           │ ← SHA-256 (+ chain helper)
               │ AuditContext        │ ← traceId-keyed promptHash/model cache
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
| `WorldMutation` | `AuditedWorldCommandExecutor` | commandTypeId, payload, actor, success |
| `PolicyDecision` | _future_ | authority host decision, guard results |

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
| `prevHash` | SHA-256 of the previous line — chain root is `""` |
| `hash` | `SHA-256(prevHash + jsonLine)` |

## Tamper Evidence

Every line includes `prevHash` and `hash`:
```
hash_1 = SHA256("" + json_1)
hash_2 = SHA256(hash_1 + json_2)
hash_3 = SHA256(hash_2 + json_3)
```

To verify: recompute the chain from line 1 and confirm `hash_N` matches. Any modification in any entry breaks the chain for all subsequent entries.

## Performance

- `IAuditLog.Record()` only **enqueues** to a `ConcurrentQueue` on the main thread — ~microseconds.
- A background loop (`AuditLogWriter`) flushes every 500ms or 10 entries:
  - SHA-256 of ~1 KB: ~tenths of microseconds
  - `File.AppendAllText` for one line: sub-millisecond
  - Total background overhead: negligible
- Rotation at 50 MB → `audit_0001.jsonl`, `audit_0002.jsonl`, etc.
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
