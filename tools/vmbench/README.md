# Guarded Lua VM throughput benchmark

Answers one question the MVP2 frame budget depends on: **how fast does the bundled Lua-CSharp VM run
with CoreAI's per-instruction execution hook installed?**

It exists because the number was previously guessed. `dev-docs/LUA_VM_BENCHMARK_PLAN.md` records only
RAW unguarded throughput (1.27-2.51 M ops/s), and an audit correctly refused a frame budget derived by
converting raw figures into guarded ones — the hook is the dominant cost, so that conversion is invalid.

## Running

From `tools/vmbench`:

```
dotnet run -c Release --project vmbench.csproj        # CoreCLR (net8.0)
dotnet build -c Release vmbench-mono.csproj           # Mono flavour (netstandard2.1)
```

`dotnet run` cannot start the `netstandard2.1` Mono flavour. Run the built `vmbench-mono.dll` with the
Mono CLI bundled with Unity, once per collector:

```
<Unity>/Editor/Data/MonoBleedingEdge/bin/mono-bdwgc.exe vmbench-mono.dll   # Boehm: what the Editor and Mono players embed
<Unity>/Editor/Data/MonoBleedingEdge/bin/mono.exe       vmbench-mono.dll   # SGen: the CLI default, NOT what Unity runs
```

The collector decides the result: `GC.GetTotalMemory(false)`, which a guard hook reads, costs about 9 ns on
Boehm and 6,500 ns on SGen. A number measured with `mono.exe` says nothing about a Unity player (the 2026-08-31
figure of 158 k instructions/s was one). Both CLIs are x86; a 64-bit Standalone Mono player and an IL2CPP
player still have to confirm any number taken here.

It links `Assets/CoreAIMods/Plugins/Lua.dll` directly, so it measures the SHIPPED VM rather than a
NuGet copy that might drift from it. The project files find the checkout relative to themselves;
`vmbench-mono.csproj` still names the Unity 6000.3.14f1 install for one reference assembly
(`System.Runtime.CompilerServices.Unsafe.dll`).

## What it measures

- raw throughput with no hook, as a control that must reproduce the historical range;
- throughput with a mirror of the guard hook at the production batch size (`ProductionBatch = 4` in
  `Program.cs`, the value of `LuaCsExecutionGuard.HookInstructionBatch`; keep them equal);
- the same across several batch sizes, so the hook's cost curve is visible;
- a mirror of the scheduler-thread hook (`LuaCsCoroutineHandle`: a clock read per fire, no heap read) at a
  count of 1, as it was until 2026-11, and at the production batch it uses now;
- fixed per-resume cost, by resuming a coroutine that immediately yields.

The hooks here are mirrors of the production bodies. The production code itself is measured by
`tools/perfbench` (groups `guard`, `signal`, `scheduler`, `dispatch`).

`RESULTS.md` holds the measured runs, their arithmetic and the allocation exposure of the hook window.
