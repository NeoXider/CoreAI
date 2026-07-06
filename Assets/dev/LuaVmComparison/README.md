# Lua VM comparison: MoonSharp vs Lua-CSharp

Goal: decide whether CoreAIMods should keep MoonSharp or switch its Lua VM to
nuskey8/Lua-CSharp. Decisive axis is **security of untrusted AI/player-authored code**,
then performance/allocations, then Editor + WebGL/IL2CPP compatibility.

This is a throwaway evaluation harness under `Assets/dev/` (not shipped). It runs an identical
corpus of Lua on both VMs and reports.

## Packages under test

- MoonSharp: already present via CoreAIMods (`org.moonsharp.moonsharp`), accessed through
  `CoreAI.Sandbox.SecureLuaEnvironment` (our hardened wrapper) and raw `MoonSharp.Interpreter.Script`.
- Lua-CSharp: `com.nuskey8.lua` (core) + `com.nuskey8.lua.unity` (Unity layer). Core namespace `Lua`,
  entry type `LuaState` (async-first). Confirm exact API from the installed package before wiring.

## Harness layout (to build once Lua-CSharp resolves)

- `LuaVmComparison.asmdef` — Editor-only test asmdef; references `MoonSharp.Interpreter`, `CoreAI.Mods`,
  the Lua-CSharp assembly, `UnityEngine.TestRunner`, `nunit`.
- `IVmRunner` — abstraction: `RunScript(string lua) -> result/error`, `Benchmark(string lua, iters)`,
  `TrySandbox(string lua) -> blocked/allowed`. Two implementations: `MoonSharpRunner`,
  `LuaCSharpRunner`.
- `LuaVmComparisonTests` — EditMode tests that run the corpus below on both runners and log a table.
- WebGL check: a tiny scene/build (separate folder `Assets/dev/LuaVmComparison/WebGL/`) that runs a
  smoke script on each VM in a WebGL player — MoonSharp already works on WebGL (see CoreAI link.xml);
  verify Lua-CSharp does too (IL2CPP/AOT, no reflection traps).

## Test corpus

### A. Correctness (results must match Lua semantics on both VMs)
1. Arithmetic + precedence: `return (2+3)*4 - 10/2`  → 15
2. String ops: `return ('a'..'b'):upper() .. tostring(#'hello')`  → "AB5"
3. Tables + ipairs: `local t={1,2,3}; local s=0; for _,v in ipairs(t) do s=s+v end; return s` → 6
4. Closures: `local function mk() local n=0; return function() n=n+1; return n end end local f=mk(); f(); return f()` → 2
5. Recursion: `local function fib(n) if n<2 then return n end return fib(n-1)+fib(n-2) end return fib(15)` → 610
6. Metatables: `local t=setmetatable({}, {__index=function() return 42 end}); return t.anything` → 42
7. Coroutines: yield/resume round-trip returning a summed sequence.
8. pcall error handling: `local ok,err = pcall(function() error('x') end); return tostring(ok)..':'..err`

### B. Performance / allocations (measure ms + GC alloc per run; N iterations)
1. Tight numeric loop: `local s=0; for i=1,1000000 do s=s+i end; return s`
2. Fibonacci recursion: `fib(30)`
3. String building: `local s=''; for i=1,10000 do s=s..'x' end; return #s`
4. Table churn: `local t={}; for i=1,100000 do t[i]=i*2 end; local s=0; for i=1,100000 do s=s+t[i] end; return s`
5. Host-call overhead: call a registered C# function `host_add(a,b)` in a loop of 100000.
Record: median ms over K runs, GC.GetTotalAllocatedBytes delta, first-run (JIT/warmup) vs steady.

### C. Sandbox / security (the decisive axis — untrusted code must be contained)
For each, assert the VM BLOCKS it (or document exactly how each VM handles it):
1. `os.execute('...')` — must be absent/blocked.
2. `io.open('secret.txt')` — filesystem access blocked.
3. `os.exit()` / `os.getenv()` — blocked.
4. `loadfile` / `dofile` / `require` — no arbitrary code/file loading.
5. `debug.*` — no debug library escape.
6. `load('return 1')()` — decide policy (dynamic code compile) per tier.
7. Instruction/time budget: `while true do end` — the VM MUST be stoppable by a host-side
   instruction/time limit (MoonSharp: our InstructionLimitDebugger; Lua-CSharp: confirm equivalent).
   Verify a runaway script is halted, not the Editor.
8. Deep recursion / stack overflow: `local function r() return r() end r()` — must surface as a
   catchable error, not crash the host.
9. Large-allocation guard: attempt to allocate a huge table in a loop — memory containment.

## Findings — source analysis of Lua-CSharp 0.5.5 (verified against the repo)

The two decisive axes (sandbox + runaway halt) are answered directly from Lua-CSharp source; they do not
need a Unity run. Only empirical perf/GC and WebGL/IL2CPP viability still require the harness.

**Distribution (important):** the core is **NuGet `LuaCSharp`** (assembly `Lua.dll`, netstandard2.1),
NOT a git UPM package. A `?path=...git` manifest entry for the core is invalid (the earlier attempt broke
package resolution). Supported Unity install = **NuGetForUnity** (core) + git UPM
`?path=src/Lua.Unity/Assets/Lua.Unity` (Unity layer). Transitive runtime deps: `LuaCSharp.Annotations`,
`Microsoft.Bcl.TimeProvider` 8.0.0, `System.Runtime.CompilerServices.Unsafe` 6.0.0
(`LuaCSharp.SourceGenerator` is analyzer-only, not a runtime dep).

- **Sandbox (STRONG).** Libraries are per-library opt-in: `OpenBasicLibrary`, `OpenStringLibrary`,
  `OpenTableLibrary`, `OpenMathLibrary`, `OpenCoroutineLibrary`, `OpenBitwiseLibrary`, plus the dangerous
  `OpenIOLibrary` / `OpenOperatingSystemLibrary` / `OpenDebugLibrary` / `OpenModuleLibrary`. To sandbox you
  simply don't open io/os/debug/package — a clean whitelist (no post-hoc nil-ing of globals). IO/OS route
  through a pluggable `LuaPlatform` (`state.GlobalState.Platform.StandardIO`), so even file/OS access is
  behind a host-controlled seam. Parity with (arguably cleaner than) MoonSharp's `CoreModules` whitelist.
- **Runaway halt (STRONG, time-based).** Every `LuaFunction` takes a `CancellationToken`, and the register
  VM (`LuaVirtualMachine.cs`) calls `context.ThrowIfCancellationRequested()` on every back-edge:
  `case OpCode.Jmp` (line ~739) and `case OpCode.ForLoop` (line ~903). So `while true do end` (a bare
  backward JMP, no host call) is interrupted by a timeout `CancellationToken` → `LuaCanceledException`.
  Equivalent guarantee to MoonSharp's instruction-count `InstructionLimitDebugger`, expressed as a wall-clock
  budget instead of an instruction budget.
- **Async-only API (the real integration cost).** No synchronous `DoString`. Entry points are
  `RunAsync` / `DoStringAsync` / `ExecuteAsync` → `ValueTask`; host functions are
  `(LuaFunctionExecutionContext ctx, CancellationToken ct) => ValueTask<int>`; even arithmetic metamethods
  are `AddAsync`/`EqualsAsync`. CoreAIMods' `ILuaExecutor`/`IGameLuaRuntimeBindings` and every host binding
  (`unity_*`, `store_*`, world queries, tick hooks) are **synchronous** today. A swap means making the whole
  Lua path async (UniTask). Long-term this fits CoreAI's tick/cooperative model better (no main-thread block,
  native cancellation), but it is the "weeks, not days" rewrite the plan flagged.
- **Perf/GC (expected win, not yet measured here).** Register VM + source-generated marshalling vs
  MoonSharp's tree-walker → Lua-CSharp's published benchmarks show materially higher throughput and much
  lower GC. Confirm with the harness (axis B) before quoting numbers for CoreAI.
- **WebGL/IL2CPP (unverified — main open risk).** netstandard2.1 DLL is AOT-friendly and SourceGenerator
  isn't needed at runtime, but `Microsoft.Bcl.TimeProvider` polyfill + `Unsafe` under IL2CPP/WebGL must be
  proven. MoonSharp already ships working on our WebGL Full tier (see link.xml). This is the axis most
  likely to decide against a swap.

## Scoring

| Axis | Weight | MoonSharp | Lua-CSharp |
|---|---|---|---|
| Untrusted-code sandbox (block A-C.1..6) | high | strong (CoreModules whitelist) | **strong** (per-library opt-in + Platform seam) |
| Runaway halt (instruction/time budget) | high | strong (instruction-count debugger) | **strong** (CancellationToken on every JMP/ForLoop) |
| Correctness (all A pass) | gate | pass (in production) | pending harness |
| Perf (B medians) + allocations | medium | tree-walker baseline | **expected win** (register VM + codegen); measure |
| WebGL/IL2CPP works | high | **proven** (shipping) | **unverified** (TimeProvider/Unsafe AOT) |
| Editor stability | medium | proven | pending |
| Async model fit (CoreAI tick) | low | sync (matches current seam) | async-first (better long-term, costly swap) |

Decision rule: MoonSharp stays unless Lua-CSharp matches its sandbox + runaway-halt guarantees AND
wins meaningfully on perf/WebGL. The VM sits behind CoreAIMods' `IGameLuaRuntimeBindings`/`ILuaExecutor`
seam, so a swap (if chosen) is internal to the package.

**Current verdict (source-grounded):** sandbox + runaway-halt are a *tie* (both strong) — so those do NOT
justify a swap by themselves. Lua-CSharp's case rests entirely on **perf/GC**, which needs empirical
confirmation, and is gated by **WebGL/IL2CPP viability** (unproven) plus a **synchronous→async rewrite** of
the whole binding seam. Recommendation: **stay on MoonSharp now**; keep the `ILuaExecutor` seam VM-agnostic;
only invest in the live Editor+WebGL harness if perf becomes a real ceiling. See "Live-run options" below for
how to actually stand up the harness if/when we do.

## Empirical results — Editor run (measured)

Ran `LuaVmBench.RunAll()` on a background thread in the Editor (Mono) via `execute_code`. Full report:
`Assets/dev/LuaVmComparison/last-run-report.md`.

- **Correctness: 8/8 tie.** arithmetic / strings / tables / closures / recursion(fib15) / metatables /
  coroutines / pcall all produce identical Lua-correct results on both VMs — Lua-CSharp is Lua-correct.
- **Runaway halt: Lua-CSharp CONFIRMED.** `while true do end` under a 500 ms `CancellationToken` budget was
  halted after **502 ms** (`LuaCanceledException`) — empirical proof of the source finding. (MoonSharp's halt
  relies on CoreAI's `InstructionLimitDebugger`, not exercised in this harness.)
- **Sandbox: both block** os / io / debug / require / load / loadfile / dofile. MoonSharp additionally leaves
  an inert `package` table present (no `require`/loaders → cannot load anything); Lua-CSharp is slightly
  cleaner (no `package`).
- **Performance** (speedup = MoonSharp ÷ Lua-CSharp; >1 = Lua-CSharp faster):

  | case | speedup |
  |---|---|
  | tight numeric loop (1e6) | **11.98×** |
  | fib(30) recursion | **5.61×** |
  | table churn (5e4) | **6.27×** |
  | host-call overhead (1e5) | **6.72×** |
  | string build `s=s..'x'` (5e3) | **0.20× (Lua-CSharp ~5× SLOWER)** |

  Lua-CSharp is **~6–12× faster** on compute / recursion / tables / host-calls, but **~5× slower on loop
  string-concatenation** — a real weakness (its string path is costly in that pattern; prefer table + concat).
  GC deltas read 0 for both — the per-thread GC counter is unreliable in this Editor/Mono background-thread
  harness, so allocations were not meaningfully measured here.

**Verdict shift:** correctness is a tie, runaway-halt is now *empirically* proven for Lua-CSharp, and perf is
a large Lua-CSharp win on everything except string-building. The remaining decider is **WebGL/IL2CPP** (below)
— still the one unproven, blocking axis.

## Live-run options (if we proceed to empirical Editor + WebGL)

The core is NuGet, so there's no zero-touch git-URL install. Three ways to get `Lua.dll` into the project,
cheapest-first:

1. **Vendored DLLs (self-contained, throwaway):** drop `Lua.dll` + `Lua.Annotations.dll` +
   `Microsoft.Bcl.TimeProvider.dll` into `Assets/dev/LuaVmComparison/Plugins/` (skip `Unsafe` unless Unity
   errors on a missing type). No manifest changes, delete-the-folder rollback. Risk: `TimeProvider`/`Unsafe`
   duplicate-assembly conflicts with Unity's runtime — resolve per compile error.
2. **NuGetForUnity (supported):** add NuGetForUnity (git UPM) + a `packages.config` pinning `LuaCSharp 0.5.5`;
   it restores all transitive deps automatically. Cleanest for correctness, heaviest project footprint.
3. **Vendored source:** copy `src/Lua/**` into an asmdef — rejected: depends on the source generator for
   parts of the stdlib and on `TimeProvider`/`Unsafe`, so it won't compile standalone without extra work.

Recommended if we run it: option 1 for the Editor perf pass, escalate to option 2 only if WebGL/IL2CPP needs
the full supported dependency graph.
