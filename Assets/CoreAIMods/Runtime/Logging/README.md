# Lua mod logging

Captures Lua mod output (`print`/`error`, uncaught runtime errors) independent of the Unity
console, so it can be read back through a plain C# API and surfaced to an in-game LLM agent
(`GetModLogsLlmTool`) that inspects what a mod said and self-repairs it while the game runs. Built to
`Docs/ARCHITECTURE_RULES.md`.

## Layer map

- **Engine-free core** (source-level engine-free; scanned by `LoggingSeamHonestyEditModeTests`):
  - `ILuaLogService` — the contract (append / query / `EntryAppended` / clear).
  - `LuaLogService` — thread-safe ring-buffer implementation (per-mod + global buffers).
  - `LuaLogEntry`, `LuaLogLevel`, `LuaLogQuery` — the data surface.
  - `LuaLogFormatter` — compact, char-budgeted, LLM-friendly rendering.
- **Adapters (engine / framework bound)** — stay in `CoreAI.Mods`:
  - `LuaLogFileSink` — `UnityEngine` (`Application.persistentDataPath`), file IO.
  - `GetModLogsLlmTool` — MEAI `AIFunction` surface (framework-boundary `Task<string>`).
  - the Unity console mirror inside `LuaLogService` (via the injected `IGameLogger`).

## Invariants

- `Sequence` and `UtcTime` are assigned under the service lock. Stored and queried entries are copies;
  mutating a caller's entry or a query result cannot expand retained text. `EntryAppended` still receives
  the original appended entry for compatibility.
- **`EntryAppended` ordering is best-effort**: the event is raised outside the storage lock so a slow
  handler never stalls other threads' appends. Under concurrent appends two entries can arrive out of
  order — consumers order by `LuaLogEntry.Sequence`. `LuaLogFileSink` writes `Sequence` per line.
- Ring entry counts, retained mod ids and text have independent bounds. Constructor options default to
  128 mod buffers, 16,384 characters per message/script name and 4,194,304 retained UTF-16 characters (8 MiB of
  text storage, conservatively counting an entry in both views). Overlong messages and script names
  carry `...[truncated]`; exact mod ids remain queryable and count toward the retained-character budget.
  The oldest last-appended mod buffer
  is evicted first, while the global tail remains independently bounded. Text pressure can evict more
  buffers and then the oldest global entries. Querying does not change eviction order.
- `Clear()` releases the global tail and every per-mod buffer. `Clear(modId)` removes that mod buffer
  while preserving the bounded global diagnostic tail. Sequence numbering remains monotonic.
- A logging mirror/sink must never throw out of a mod's log call and break gameplay.

## Recorded deviations / notes

- The engine-free core shares the `CoreAI.Mods` assembly with the adapters; it is engine-free at
  source level only. `LuaLogService`'s constructor takes an `IGameLogger` (from `CoreAI.Source`,
  which is `UnityEngine`-coupled) for the console mirror, so the core cannot move into its own
  `noEngineReferences` assembly until that mirror becomes an `EntryAppended` subscriber adapter
  (like `LuaLogFileSink`), which would change the public constructor and its installer wiring.
- `GetModLogsLlmTool` uses `System.Threading.Tasks.Task` (banned by §3 of
  `Docs/ARCHITECTURE_RULES.md`) only at the MEAI
  `AIFunctionFactory` boundary, which requires a `Task`-returning delegate; the work underneath is
  synchronous.
