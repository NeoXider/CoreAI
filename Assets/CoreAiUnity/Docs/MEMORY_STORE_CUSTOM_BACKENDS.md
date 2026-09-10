# Custom memory backends (PlayerPrefs, cloud, composite)

CoreAI treats **MemoryTool** (long-term `memory` string) and **ChatHistory** (session dialogue) through one contract: [`IAgentMemoryStore`](../../CoreAI/Runtime/Core/Features/AgentMemory/IAgentMemoryStore.cs). The default Unity implementation is [`FileAgentMemoryStore`](../Runtime/Source/Features/AgentMemory/Infrastructure/FileAgentMemoryStore.cs) (JSON under `Application.persistentDataPath/CoreAI/AgentMemory/`). **Editor:** to wipe **all** CoreAI persistence under `persistentDataPath/CoreAI` at once (memory, summaries, version stores), use **CoreAI → Delete All Persistent Saves...** (not during Play Mode). **WebGL:** this store is supported — `persistentDataPath` is backed by the browser (IndexedDB / Unity virtual FS); respect storage quotas and expect weaker guarantees in private browsing mode.

You **can** replace it with:

- **PlayerPrefs** (or `EditorPrefs` in tools) — small payloads, single device, no server.
- **Cloud save** — multi-device, backups, optional server authority.
- **Composite** — e.g. local file + async upload (offline-first).

DI registration today (Unity package): [`CoreAILifetimeScope`](../Runtime/Source/Composition/CoreAILifetimeScope.cs) defaults to `FileAgentMemoryStore`, while `AgentMemoryPersistenceMode.SessionOnly` selects `InMemoryAgentMemoryStore` before build. For a custom backend, replace the selected private backing while keeping the scoped memory/transcript decorators at the public DI boundary.

---

## Contract you must honor

Implement all members of `IAgentMemoryStore`:

| Method | Meaning |
|--------|---------|
| `TryLoad` / `Save` / `Clear` | **MemoryTool** field: `AgentMemoryState.Memory`, `LastSystemPrompt`. |
| `AppendChatMessage` / `GetChatHistory` / `ClearChatHistory` | **ChatHistory**: full dialogue lines. |

**Important:** `Clear` clears **MemoryTool** state; `ClearChatHistory` clears **only** chat. On disk, `FileAgentMemoryStore` keeps these in separate JSON fields so one can be wiped without erasing the other (see EditMode tests `FileAgentMemoryStoreEditModeTests`). If your store caches chat in RAM per `roleId`, **`ClearChatHistory` must invalidate that cache** (or equivalent) so the next `GetChatHistory` reflects durable storage — the reference `FileAgentMemoryStore` resets its in-memory list and reload flag for the role when chat is cleared.

`AppendChatMessage(..., persistToDisk: false)` is used for intermediate tool traffic; only `true` must hit durable storage if you support persistence.

---

## Option A — PlayerPrefs

### When it makes sense

- Single-player, one machine, small history.
- You already centralize saves in PlayerPrefs.

### Limits

- **Size:** Unity documents ~1 MiB cap on some platforms for PlayerPrefs; chat history can grow quickly. Cap messages in UI / policy (`MaxChatHistoryMessages`) and truncate before write.
- **Types:** store **strings** only — serialize with `JsonUtility`, `System.Text.Json`, or Newtonsoft (already in project tests).

### Suggested shape

- One key per role, e.g. `CoreAI.Memory.{roleId}` → JSON blob.
- Blob mirrors `FileAgentMemoryStore`’s on-disk shape: `memory`, `lastSystemPrompt`, `chatHistoryJson` (array wrapper), so you can copy merge logic from the reference implementation.

### Threading

`PlayerPrefs` reads/writes should run on the **main thread** in Unity. If your cloud or file layer runs on a background thread, marshal results back to the main thread before calling game code that touches Unity APIs.

### Pseudocode

```csharp
public sealed class PlayerPrefsAgentMemoryStore : IAgentMemoryStore
{
    private string Key(string roleId) => $"CoreAI.AgentMemory.{Sanitize(roleId)}";

    public bool TryLoad(string roleId, out AgentMemoryState state) { /* PlayerPrefs.GetString + JsonUtility */ }
    public void Save(string roleId, AgentMemoryState state) { /* merge with existing chat blob */ }
    public void Clear(string roleId) { /* clear memory fields, keep or drop chat per product */ }
    public void ClearChatHistory(string roleId) { /* clear chat slice only */ }
    public void AppendChatMessage(string roleId, string role, string content, bool persistToDisk = true) { /* append to list; if persist */ }
    public ChatMessage[] GetChatHistory(string roleId, int maxMessages = 0) { /* */ }
}
```

---

## Option B — Cloud save (detailed)

Use a cloud backend when you need **cross-device** continuity, **server-side backup**, or **tamper resistance** (server validates or stores canonical state).

### 1. Data model

Treat each **role** as one document or blob:

- **Key:** recommended composite, e.g. `{accountId}:{saveSlot}:{roleId}` (or Steam `ulong` id + role). Avoid guessable keys for public APIs.
- **Payload:** same logical fields as local JSON:

```json
{
  "version": 17,
  "updatedAtUtc": "2026-04-27T12:00:00Z",
  "lastSystemPrompt": "...",
  "memory": "Previous crafts: ...",
  "chatHistory": [ { "role": "user", "content": "...", "timestamp": 1714214400 } ]
}
```

`version` or `updatedAtUtc` supports **conflict resolution** (see below).

### 2. When to read (download)

Typical order:

1. **Player authenticated** (Steam, Epic, custom JWT, Unity Authentication, etc.).
2. **Before first LLM call** for that role (or at app start): `GET` cloud document for each role you care about.
3. **Hydrate** `IAgentMemoryStore` in memory (or write through to your composite local layer).

If offline: use **last cached** blob from disk (your composite’s local tier) and optionally queue a sync when online.

### 3. When to write (upload)

| Trigger | Strategy |
|---------|----------|
| `Save` / `AppendChatMessage(..., persistToDisk: true)` | **Debounce** (e.g. 500 ms–2 s) and batch per role to avoid spamming REST on every token. |
| `OnApplicationPause` / `OnApplicationQuit` | **Flush** pending debounced writes so mobile kills don’t lose data. |
| User presses “Save” in game | Immediate `PUT`. |

### 4. Conflict resolution

Two devices or “play offline then online” can diverge.

- **Last-write-wins (LWW):** server stores `updatedAtUtc`; client sends `If-Match` / version; on 409, merge or prompt (game design).
- **Merge chat:** concatenate by `Timestamp` and cap length (product decision); **memory** string is harder to merge automatically — often LWW on `memory` + append-only event log on server if you need audit.

For **solo games**, LWW with server timestamp is usually enough.

### 5. Security

- **Transport:** HTTPS only; pin certs if you maintain your own API.
- **Auth:** short-lived access token in memory; refresh via secure storage (platform keychain where available), not plain PlayerPrefs for refresh tokens in shipped builds.
- **Payload:** optional **client-side AES** with a key derived from account + server salt if blobs are sensitive (remember: client-side encryption is obfuscation, not DRM).

### 6. Implementation patterns

#### 6.1 “Thin remote” (simplest)

- `IAgentMemoryStore` implementation keeps an **in-memory cache** (or delegates to `FileAgentMemoryStore` for local).
- On `Save` / `AppendChatMessage` (persist): update cache + enqueue `PUT` to your REST API.
- On startup: `GET` then populate cache.

#### 6.2 Composite: local file + cloud (offline-first)

```text
Read:  try File → if missing or stale, fetch Cloud → write File → return
Write: write File immediately → enqueue Cloud upload
```

Matches poor networks: game stays responsive; sync catches up later.

#### 6.3 Vendor examples (conceptual)

- **Custom REST:** `GET/PUT /v1/players/{id}/coreai-memory/{roleId}` with bearer token.
- **Unity Gaming Services / Cloud Save:** one saved game key per role or one JSON blob per player containing all roles.
- **Steam Remote Storage:** file per role under a fixed folder; mind **quota** (~100 MiB per user on Steam; still cap chat length).
- **PlayFab:** `UserData` / `Entity Files` — same debounce + versioning ideas.

CoreAI stays **agnostic**: no package reference required; you implement `IAgentMemoryStore` and register it.

### 7. Failure modes to handle

- **Timeout / 503:** retry with exponential backoff; keep local write successful.
- **401:** refresh token once; if still failing, stay offline with local data and surface UI.
- **Partial write:** write entire blob atomically (temp file + rename locally; server-side transactional document replace).

### 8. Testing

- **Unit tests:** fake time + in-memory HTTP handler verifying debounce and merge.
- **PlayMode:** use `InMemoryStore` for LLM tests; add separate **EditMode** tests for your serializer and conflict rules (similar to `FileAgentMemoryStoreEditModeTests`).

---

## Option C — PlayerPrefs as cache index only

Store **only** `{ cloudDocumentId, etag, lastSyncUtc }` in PlayerPrefs while the heavy JSON lives in cloud or file. Useful when blob size exceeds PlayerPrefs limits.

---

## Chat UI (`CoreAiChatPanel`) and persistence

If you use [`CoreAiChatPanel`](../Runtime/Source/Features/Chat/README_CHAT.md) with “load persisted chat on startup”, the panel reads **`IAgentMemoryStore.GetChatHistory`**. Any custom store must return the same `ChatMessage[]` shape so hydration works.

---

## Summary

| Approach | Multi-device | Size / scale | Complexity |
|----------|--------------|--------------|------------|
| `FileAgentMemoryStore` (default) | No (per device file) | Medium | Low |
| PlayerPrefs | No | Small | Low |
| Cloud + optional local composite | Yes | Large (with caps) | Medium–high |

**Yes:** both session (**ChatHistory**) and **MemoryTool** can be backed by PlayerPrefs or cloud — implement `IAgentMemoryStore` (or wrap the default store) and register it in place of `FileAgentMemoryStore`.

---

## Conversation summary stores and async context preparation

Compact dialogue summaries (`## Conversation Summary`) live separately from memory via the contract [`IConversationSummaryStore`](../../CoreAI/Runtime/Core/Features/AgentMemory/IConversationSummaryStore.cs) and its portable async companion `IAsyncConversationSummaryStore` (Load/Save/Clear with `CancellationToken`). The sync API is kept for compatibility, but failed save/clear now throws instead of returning a silent success.

Built-in implementations:

- `FileConversationSummaryStore` — JSON `<stem>.json` under the host directory, atomic writes via a unique temp file, file gates shared per process and target path (`FileAgentMemoryStore.MutationLocks` pattern, never evicted). File format and naming unchanged.
- `ScopedConversationSummaryStoreDecorator` — the same scope boundary as memory/transcripts; scope is computed synchronously before any await.
- `InMemoryConversationSummaryStore`, `NullConversationSummaryStore` — implement async directly.
- `BlockingSyncSummaryStoreAsyncAdapter` — explicit opt-in bridge for third-party sync backends (runs inline, blocks the calling thread on disk I/O).

Durability hooks (WebGL): the `FileConversationSummaryStore` constructor takes the legacy `Func<bool> afterWrite` (queue-only `CoreAiWebGlPersistence.Sync`) and `Func<CancellationToken, Task<bool>> afterWriteAsync` (confirmed `SyncAsync`). The async hook runs after a committed mutation, outside file gates, on the calling context, with `CancellationToken.None`; false or an exception reports `IOException`. The host owns its timeout; the store adds no competing timer. Sync calls fail promptly when file work is busy. If both hooks are configured, a successful sync call only queues flushing; an async read must still obtain confirmation.

Each canonical path retains an unconfirmed write generation and its host callback across store instances. A failed flush cannot become an apparently successful read merely because VFS already contains the new fold marker. A subsequent async read re-confirms that generation before returning its summary, even through a new store instance without hooks. A later successful callback cannot acknowledge a newer write it did not observe. Delete follows the same rule: an absent VFS file is not proof that deletion reached IndexedDB. A confirmation callback must not recursively access the same summary file; this fails explicitly instead of recursing or deadlocking.

Strict async reads: a corrupt/unreadable file propagates the exception and does not overwrite data; a missing file means an empty string (sync reads keep the legacy `""` fallback).

`DeterministicConversationContextManager.BuildSnapshotAsync` and `LlmAssistedConversationContextManager.BuildSnapshotAsync` await actual async loads and saves. Sync-only custom backends must be explicitly wrapped in `BlockingSyncSummaryStoreAsyncAdapter`; there is no inferred blocking fallback. The deterministic projection is shared with the synchronous API. Deferred snapshots provide an internal awaited commit: concurrent consumers serialize, successful commits write once, failed commits retain their callback for retry, and cancellation of a waiting consumer does not cancel another accepted write. A synchronous snapshot commit cannot wait behind an asynchronous one.

When using the built-in scoped summary decorator, both async managers bind its effective role/actor/user key before the first storage await. Immediate writes and deferred commits retain that binding even if the host changes its scope provider meanwhile. The compaction provider still receives the original agent role; storage partition keys do not become routing roles.

The snapshot owner must acknowledge the old-history summary before dispatching the main provider or appending messages that may evict its source. This also prevents a persistence failure after tool execution from encouraging tool replay. File names, scope keys, and JSON format remain unchanged. Cancellation before commit prevents mutation; after commit, host confirmation reports the real outcome without claiming rollback.

Both ordinary and streaming orchestrator requests await this summary preflight before opening the main provider. A failed load or a failed confirmation ends the turn before dispatch and is reported as an error; the turn still records its user message exactly once. The learner's own words are the only content in the store that cannot be reconstructed, while the summary is a retelling of messages the store still holds, so a suppressed append would trade a certain loss for a delayed one — the next turn appends and evicts anyway. Provider-failure and cancellation history behaviour is therefore the same on both sides of the preflight: no orchestration outcome ends a turn without its user message. Unity persistent composition supplies `SyncAsync`, so async summary writes wait for host confirmation instead of accepting a queued flush as durability.

Three consequences a custom backend has to plan for. **The write-once guarantee is per orchestrator invocation, not per learner message**: a host that resubmits the same request after a failed turn records that intent a second time, because nothing in the store identifies a message as the one already written. **An `AppendChatMessage` that throws during failure teardown loses that message** — the store error is logged as a warning and deliberately not allowed to replace the cancellation or provider failure the turn is already reporting. **Size the chat cap against the fold window, not against a single turn**: an append evicts the oldest message whether or not this turn managed to commit its summary, so a cap close to the message count that triggers folding can evict source nothing has retold yet. The shipped stores trim at 500 messages while folding starts around thirty, which is what makes the occasional broken turn affordable.

Cancellation during an accepted write waits for its host confirmation. Once durability succeeds, the orchestrator rechecks cancellation before opening the main provider, while retaining the normal write-once user-intent record.

Integration limit: other memory/history APIs retain synchronous compatibility paths; this section does not claim the entire orchestration pipeline is free of blocking file I/O. Desktop tests do not prove browser IndexedDB persistence; WebGL release validation must include actual host confirmation and reload.

## Async skill and revision persistence

`FileSkillStore` implements `IAsyncSkillStore`; `FileLuaScriptVersionStore` implements `IAsyncLuaScriptVersionStore`. Their constructors configure paths without reading files or creating directories. Use the async authoring coordinator for live agents. Synchronous APIs remain explicit compatibility entry points and fail promptly when an operation owns the same directory/file gate; they must not block a player loop waiting for an async flush.

Desktop async operations put private filesystem and JSON work on workers. Preparation, catalog publication, Unity persistence callbacks, and logging use the host marshaler. WebGL uses its host context and the existing `CoreAiWebGlPersistence.SyncAsync` completion/timeout, with no worker-thread assumption. Both stores accept optional host and durability callbacks for host composition and controlled failure tests; production defaults use the existing Unity marshaler and browser persistence implementation.

The shared canonical-path gate covers write, confirmed flush, and publication. Windows path aliases share that gate. Caller cancellation before the atomic replacement prevents the edit; cancellation after a committed write does not abandon confirmation/publication or pretend to roll the edit back. Skill publication failure is a `SkillStorePublicationException`: storage committed, so do not replay the edit. A version write failure propagates to the coordinator, which keeps an already committed skill and reports `RevisionRecorded=false`.

A failed browser flush leaves a shared pending confirmation generation. A new async reader retries the retained confirmation, without replaying the edit, before exposing the VFS record; a synchronous reader rejects unconfirmed data. This applies to legacy skill filename migration as well as new writes and deletion. `SkillStoreDurabilityException` identifies local commit with durability unconfirmed and catalog unpublished. Once confirmation recovers, normal rehydration can publish the stored skill. These guarantees do not promise exactly-once completion across browser/process death.

Skill async reads preserve all ordered `Sections`; editing the main document preserves reference documents. Legacy version files retain the existing `slots`/`scriptKey`/`originalLua`/`currentLua`/`history` field names and stable revision indices. Corrupt reads and failed atomic writes do not replace earlier coherent state or silently reset it to empty.

Payload bounds are explicit: **1 MiB of encoded JSON per skill record** (`FileSkillStore.MaxRecordBytes`) and **4 MiB per version-store JSON file** (`FileLuaScriptVersionStore.MaxStoreBytes`). Oversized inputs fail instead of being truncated. WebGL yields between skill records and reconstructed version slots. Directory enumeration and bounded JSON parsing/serialization still consume synchronous CPU time; these byte limits and desktop tests are **not proof of a frame-time budget**. Profile representative maximum documents in an actual WebGL player before claiming frame-time guarantees.
