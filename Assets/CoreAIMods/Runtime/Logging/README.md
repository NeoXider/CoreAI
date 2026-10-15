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

- `Sequence` and `UtcTime` are assigned under the service lock; every other field is taken as-is.
- **`EntryAppended` ordering is best-effort**: the event is raised outside the storage lock so a slow
  handler never stalls other threads' appends. Under concurrent appends two entries can arrive out of
  order — consumers order by `LuaLogEntry.Sequence`. `LuaLogFileSink` writes `Sequence` per line.
- Ring buffers are bounded, so long-running sessions cannot leak memory.
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
