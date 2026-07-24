# Can CoreAI's runtime be faster / better than Roblox?

Honest engineering assessment (2026-07). Short answer: **at the engine/simulation/graphics level — yes,
realistically; at the raw Lua-VM level — no, not today.** "Faster than Roblox" is achievable for the
*overall experience* (frame time, visuals, native builds) **if** the architecture keeps scripts thin and
pushes real work into compiled code — but not by out-executing Luau inside the interpreter.

## The two stacks

| Layer | Roblox | CoreAI |
|---|---|---|
| Script VM | **Luau** — Roblox's own Lua 5.1 fork: register-based bytecode, one of the fastest interpreters in existence, **native code generation** on server, type-directed opt, **Parallel Luau** (Actors) for multithreading | **Lua-CSharp** — a *managed* C# Lua interpreter running on Mono/IL2CPP with a GC |
| Engine | Mature C++ engine, custom PGS physics, replication/streaming, years of platform tuning | **Unity** (C#, IL2CPP AOT, Burst/Jobs/DOTS available), PhysX, URP/HDRP rendering |
| Distribution | Roblox client only | Native Windows/Android/WebGL builds we control |

## Where Roblox wins today (be honest)

1. **Raw script throughput.** Luau's interpreter + native codegen is elite. A managed C# Lua interpreter
   with a GC is slower per instruction — often several ×. If a game does heavy per-frame work *in Lua*,
   Roblox wins.
2. **Parallel Luau.** Roblox can run script logic across cores (Actors). We have no equivalent yet.
3. **Maturity.** Replication, streaming, the physics solver, and a decade of profiling are ahead of us.

## Where CoreAI can win (and how)

1. **Keep Lua THIN — this is the whole game.** In BOTH engines the script is an orchestrator; the heavy
   lifting (rendering, physics, animation, particles, pathfinding) is compiled. If our mods stay thin
   and call fast C# systems, the Lua-VM gap barely shows — the frame is dominated by the engine, not the
   interpreter. Our sample mods already do this (a Heartbeat that moves a handful of parts).
2. **The bridge is already near-zero-overhead.** v6.3.3/6.3.4 made the sandbox guard hot path
   *zero-allocation* (pooled hook, sampled instruction counter, no per-call `Stopwatch`/closure). So the
   cost of *calling into* the engine from Lua is cheap — the thing that matters when Lua is thin.
3. **Unity's compiled paths beat interpreted logic.** Move per-frame systems (spawning, steering, AI,
   grids) into C# **Burst/Jobs/DOTS** and they run at native speed across cores — something Luau logic
   can't match. A CoreAI game whose simulation is C# + Burst can out-run the same game written as Luau
   scripts.
4. **Rendering can exceed Roblox out of the box.** URP/HDRP, modern lighting, post-processing, GPU
   instancing, and swappable premium materials/shaders give better visuals per frame than Roblox's fixed
   look — a "better engine" axis that isn't about VM speed at all.
5. **Native AOT builds.** IL2CPP produces ahead-of-time native binaries for Windows/Android; no
   client-download or platform gatekeeper. WebGL parity via URP.

## The realistic verdict

- **Overall frame time / visuals / native performance:** CoreAI **can** match or beat Roblox, *provided*
  games keep scripts thin and put simulation in compiled C# (ideally Burst/DOTS). The engine is Unity —
  a compiled, mature, GPU-modern engine.
- **Out-executing Luau in the interpreter:** **no**, not without a major VM investment (Lua→IL/AOT
  compilation, or a JIT). It is not worth chasing; the winning move is to not run hot logic in Lua.

## Roadmap to actually be faster than Roblox

1. **Thin-Lua discipline** (done in samples; enforce in the skill): scripts orchestrate, they don't
   number-crunch per frame.
2. **Zero-alloc bridge** (done: guard hot path) — keep it that way; audit allocations each release.
3. **Compiled simulation tier:** offer C#/Burst/Jobs systems (spawners, movers, grids, AI) mods bind to,
   so entity-heavy games run native + multicore — the answer to Parallel Luau.
4. **GPU-driven rendering + premium material catalog** — win the visuals axis by default.
5. **(Optional, long-term) Lua hot-path compilation** — only if profiling shows Lua is the bottleneck
   after 1–3; likely unnecessary if scripts stay thin.
6. **Streaming/replication** parity for large worlds (later; needed to match Roblox at scale).

Bottom line: we don't beat Roblox by writing a faster Lua interpreter — we beat it by making Lua rarely
the bottleneck (thin scripts + zero-alloc bridge) and by running the actual work on Unity's compiled,
GPU-modern, multicore-capable engine, in native AOT builds.
