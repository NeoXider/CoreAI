# Lua mod logging

Captures Lua mod output (`print`/`warn`/`error`, uncaught runtime errors) independent of the Unity
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

- `Sequence` and `UtcTime` are assigned under the service lock; every other field is taken as-is.
- **`EntryAppended` ordering is best-effort**: the event is raised outside the storage lock so a slow
  handler never stalls other threads' appends. Under concurrent appends two entries can arrive out of
  order — consumers order by `LuaLogEntry.Sequence`. `LuaLogFileSink` writes `Sequence` per line.
- Ring buffers are bounded, so long-running sessions cannot leak memory.
- A logging mirror/sink must never throw out of a mod's log call and break gameplay.

## Recorded deviations / notes

- TODO (intended split): move the engine-free core (`ILuaLogService`, `LuaLogService`,
  `LuaLogFormatter`, `LuaLogEntry`, `LuaLogLevel`, `LuaLogQuery`) into a `CoreAI.LuaLogging`
  assembly with `noEngineReferences: true`, leaving the adapters in `CoreAI.Mods`. Blocked on a
  cheap-split boundary: `LuaLogService`'s constructor takes an `IGameLogger` (which is
  `UnityEngine`-coupled) for the console mirror, so a true split first requires extracting that mirror
  into an `EntryAppended` subscriber adapter (like `LuaLogFileSink`), which changes the public
  constructor and its installer wiring. Deferred until that refactor is scheduled.
- `GetModLogsLlmTool` uses `System.Threading.Tasks.Task` (banned by §3) only at the MEAI
  `AIFunctionFactory` boundary, which requires a `Task`-returning delegate; the work underneath is
  synchronous.
