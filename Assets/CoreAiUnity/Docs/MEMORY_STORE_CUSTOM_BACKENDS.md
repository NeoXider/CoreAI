# Custom memory backends (PlayerPrefs, cloud, composite)

CoreAI treats **MemoryTool** (long-term `memory` string) and **ChatHistory** (session dialogue) through one contract: [`IAgentMemoryStore`](../../CoreAI/Runtime/Core/Features/AgentMemory/IAgentMemoryStore.cs). The default Unity implementation is [`FileAgentMemoryStore`](../Runtime/Source/Features/AgentMemory/Infrastructure/FileAgentMemoryStore.cs) (JSON under `Application.persistentDataPath/CoreAI/AgentMemory/`). **WebGL:** this store is supported — `persistentDataPath` is backed by the browser (IndexedDB / Unity virtual FS); respect storage quotas and expect weaker guarantees in private browsing mode.

You **can** replace it with:

- **PlayerPrefs** (or `EditorPrefs` in tools) — small payloads, single device, no server.
- **Cloud save** — multi-device, backups, optional server authority.
- **Composite** — e.g. local file + async upload (offline-first).

DI registration today (Unity package): [`CoreAILifetimeScope`](../Runtime/Source/Composition/CoreAILifetimeScope.cs) registers `FileAgentMemoryStore` as `IAgentMemoryStore`. Swap that registration (or register a decorator) with your implementation.

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
