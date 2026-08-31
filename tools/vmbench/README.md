# Guarded Lua VM throughput benchmark

Answers one question the MVP2 frame budget depends on: **how fast does the bundled Lua-CSharp VM run
with CoreAI's per-instruction execution hook installed?**

It exists because the number was previously guessed. `dev-docs/LUA_VM_BENCHMARK_PLAN.md` records only
RAW unguarded throughput (1.27-2.51 M ops/s), and an audit correctly refused a frame budget derived by
converting raw figures into guarded ones — the hook is the dominant cost, so that conversion is invalid.

## Running

```
dotnet run -c Release --project vmbench.csproj        # CoreCLR
dotnet run -c Release --project vmbench-mono.csproj   # Mono, the runtime Unity actually uses
```

It links `Assets/CoreAIMods/Plugins/Lua.dll` directly, so it measures the SHIPPED VM rather than a
NuGet copy that might drift from it.

## What it measures

- raw throughput with no hook, as a control that must reproduce the historical range;
- throughput with the hook at the production batch size (read from `LuaCsExecutionGuard`, not assumed);
- the same across several batch sizes, so the hook's cost curve is visible;
- fixed per-resume cost, by resuming a coroutine that immediately yields.

`RESULTS.md` holds the measured run and its arithmetic. Re-measure before freezing any threshold: the
recorded run used Unity's bundled x86 Mono CLI, and the gate must be confirmed in the real 64-bit
Standalone player.
