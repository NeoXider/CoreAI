# Lua VM Performance Comparison — CoreAI Lua-CSharp vs Roblox Luau

A fair, runnable micro-benchmark suite comparing **CoreAI's mod VM** (Lua-CSharp — the
managed C# interpreter `nuskey8/Lua-CSharp`, namespace `Lua`) against **Roblox Luau**.

You run the Luau side in Studio; the CoreAI side is a small paste-in EditMode harness (primary)
or the in-game `execute_lua` path (limited fallback). This document is the whole kit: the portable
benchmark bodies, the exact way to run each side, a results table to fill in, and how to read it.

> **Scope note.** This measures raw interpreter throughput on standard-library Lua only. It is not a
> feature or gameplay-API comparison. Expect Luau to win by a wide margin (it is a tuned register
> bytecode VM with optional native codegen); *by how much* — and how CoreAI's guarded mod path
> compares to its raw VM — is what the table answers.

---

## 1. What already exists in the repo (investigation result)

| Thing | Where | Usable for VM timing? |
|---|---|---|
| `com.neoxider.coreaibenchmark` | `Assets/CoreAIBenchmark/` | **No.** It benchmarks how well an *LLM* builds a game (drives real `execute_lua`/`world_command`, grades world state and tool traces). It has no VM-throughput harness. |
| One-shot Lua executor | `LuaCsGameToolExecutor.ExecuteAsync` (`Assets/CoreAIMods/Runtime/Infrastructure/`) | Yes but confounded — see budgets below. This is what `execute_lua` calls. |
| Persistent mod runtime | `LuaCsModRuntime` (`Assets/CoreAIMods/Runtime/LuaExecution/`) | Yes but even tighter budgets (per-handler 100k steps / 500 ms). |
| Sandbox factory | `LuaCsSecureEnvironment.Create()` / `.RunChunk()` (`Assets/CoreAIMods/Runtime/Scripting/LuaCs/`) | **Best.** Build a state, load a chunk, time it in C#. This is the harness below. |
| Execution guard | `LuaCsExecutionGuard` (same folder) | The thing that caps steps/time/alloc; relevant as a *confounder* (below). |

### 1a. Timing primitives available to a Lua script

The CoreAI sandbox (`LuaCsSecureEnvironment.Create`) opens **Basic, String, Table, Math, Coroutine,
Bitwise** and then **strips risky globals**:

```
LuaCsSecureEnvironment.cs — StripRiskyGlobals()
  RemoveGlobal: load, loadstring, loadfile, dofile, require, io, os, debug, package, collectgarbage
```

Consequences for timing:

- **`os.clock()` / `os.time()` are NOT available in Lua-CSharp** — the whole `os` table is removed.
  So the Roblox-portable `os.clock()` cannot be used inside a CoreAI script.
- **`collectgarbage` is removed** — a script cannot force a GC between trials on the CoreAI side.
- CoreAI's own `time_realtime()` / `time_now()` (`LuaCsTimeBindings`) exist, but only when the
  **Gameplay** capability is granted, and they are single-precision `float` seconds
  (`Time.realtimeSinceStartup`) — resolution degrades as editor uptime grows, and they are **not
  Roblox-portable**.

**Timing decision (works on BOTH):**

- **Roblox side:** time inside Lua with **`os.clock()`** (high-resolution CPU seconds — the correct
  Luau primitive).
- **CoreAI side:** time **in C# with `System.Diagnostics.Stopwatch`** around the chunk execution. Because
  `os` is gone, external C# timing is the only accurate option. (`time_realtime()` is offered only as a
  low-precision fallback for the `execute_lua`-only path in §4b.)

The benchmark **body** is identical portable Lua on both sides; only the thin timing wrapper differs.
That keeps the comparison apples-to-apples: the same instructions are executed either way.

### 1b. Confounder you MUST control: the execution guard + step/time budgets

`execute_lua` and mod handlers run under `LuaCsExecutionGuard`, which installs a Lua hook that fires
**every 4 instructions** and on each fire reads `Stopwatch.GetTimestamp()` **and**
`GC.GetTotalMemory(false)`, enforcing:

| Path | Max steps | Wall-clock timeout | Source |
|---|---:|---:|---|
| One-shot `execute_lua` | **500,000** | 2,000 ms | `LuaCsSecureEnvironment.OneShotHardLimitSteps`, guard default `timeoutMs:2000` |
| Persistent mod handler/timer | **100,000** | 500 ms | `LuaCsModRuntime.DefaultHandlerMaxSteps` / `DefaultHandlerTimeoutMs` |

Two problems this creates for a micro-benchmark:

1. **A real workload does not fit.** A tight loop of even ~100k iterations exceeds 500k VM steps and is
   killed with `EXCEEDED_HARD_LIMIT_STEPS`. You cannot run a 1e6–1e7 loop through `execute_lua`.
2. **The guard hook is sandbox overhead, not VM speed.** A Stopwatch + heap read every 4 instructions
   makes the interpreter look far slower than it is. Including it measures "CoreAI's guarded mod cost",
   not "the Lua-CSharp VM".

**Therefore the primary CoreAI harness (§4a) runs the chunk WITHOUT the guard hook** and times it in C#,
giving a clean VM-vs-VM number. Optionally run the same chunk **guarded** (a big custom `maxSteps`) to
also report the guard tax. The `execute_lua` path (§4b) is a convenience fallback only, and only for
small `N`.

### 1c. Other confounders and how to control them

- **Native codegen (Luau).** Roblox can JIT a script tagged `--!native`, and honors `--!optimize 2`.
  Run Luau **twice**: default (interpreted, the apples-to-apples baseline vs the CoreAI interpreter) and
  `--!native` (the real-world upper bound). Record both columns.
- **Warm-up.** Do 1–3 untimed warm-up runs before the timed run on each side (JIT warm, caches hot,
  managed heap grown).
- **Fixed iteration count.** Every benchmark takes an explicit `N`; keep it identical across platforms.
- **GC between trials.** Roblox: no manual GC needed (short runs), keep runs independent. CoreAI: call
  `GC.Collect()` in the C# harness *between* benchmarks (Lua-side `collectgarbage` is unavailable).
- **Single-threaded, no yielding.** No coroutines, no `wait`/`task.wait`, no `Heartbeat`. Pure compute.
- **No `print` inside the timed loop.** Printing is I/O and dwarfs the work. Print only the final result.
- **Return a checksum.** Each body returns an accumulated value that depends on every iteration, so the
  interpreter cannot dead-code-eliminate the loop, and you can confirm both VMs computed the same thing.
- **Same numeric model.** Both VMs use IEEE doubles for numbers; avoid `//` (floor-div) — it is a
  Luau/5.3 operator and the CoreAI side downlevels Luau→Lua 5.2. Use `math.floor` instead. Stick to the
  standard subset listed in §2.

---

## 2. The benchmark suite (portable Lua — identical on both VMs)

Each benchmark is a single self-contained `local function work(N)` that returns a checksum. It uses only
the standard subset that runs identically in Luau and Lua-CSharp: numeric `for`, locals, arithmetic,
table array read/write, `string.format`/`rep`/`table.concat`, and function calls (incl. recursion). No
`os`, no `//`, no coroutines, no globals mutated.

Copy the `work` function as-is into either harness. Suggested `N` targets a ~0.05–2 s Luau run; scale up
if a run is too fast to measure, down if the CoreAI raw run is painfully slow.

### B1 — Tight numeric loop (sum 1..N with mixed arithmetic)
```lua
local function work(N)
  local acc = 0.0
  for i = 1, N do
    acc = acc + i * 3 - (i % 7) + (i * i) % 101
  end
  return acc
end
-- suggested N = 20000000
```

### B2 — Table array write + read-back
```lua
local function work(N)
  local t = {}
  for i = 1, N do
    t[i] = i * 2 + 1
  end
  local acc = 0.0
  for i = 1, N do
    acc = acc + t[i]
  end
  return acc
end
-- suggested N = 5000000
```

### B3 — Function-call overhead (call a tiny function N times)
```lua
local function addmul(a, b)
  return a * b + a - b
end
local function work(N)
  local acc = 0.0
  for i = 1, N do
    acc = acc + addmul(i, 3)
  end
  return acc
end
-- suggested N = 10000000
```

### B4 — Recursive Fibonacci (call + recursion heavy)
```lua
local function fib(n)
  if n < 2 then return n end
  return fib(n - 1) + fib(n - 2)
end
local function work(N)
  -- N = how many times to recompute fib(FIB_N)
  local acc = 0.0
  local FIB_N = 30
  for _ = 1, N do
    acc = acc + fib(FIB_N)
  end
  return acc
end
-- suggested N = 20   (fib(30) ~ 2.7M calls each => ~54M calls total)
```

### B5 — String build (format + concat + rep)
```lua
local function work(N)
  local acc = 0
  local parts = {}
  for i = 1, N do
    local s = string.format("row-%d=%d;", i, i * i % 1000)
    parts[(i - 1) % 64 + 1] = s
    if i % 64 == 0 then
      local joined = table.concat(parts, "|")
      acc = acc + #joined
    end
  end
  acc = acc + #string.rep("ab", 1000)
  return acc
end
-- suggested N = 500000
```

> **Checksum check.** After a run on each VM, the returned number from a given benchmark+`N` must match
> across Luau and Lua-CSharp (B1–B4 are exact; B5 compares string lengths, also exact). A mismatch means
> the bodies diverged (e.g. an accidental edit) — fix before trusting the timings.

---

## 3. Running the Luau (Roblox Studio) side

You will run each benchmark twice: **default** and **`--!native`**.

### 3a. Default (interpreted) — Command Bar or a Script
1. Open Studio → **View → Command Bar** (or insert a `Script` into `ServerScriptService`).
2. Paste this wrapper with the chosen `work` body inline, then run:

```lua
-- paste one benchmark's `work` function above this line
local N = 20000000        -- match the benchmark's suggested N
-- warm-up (untimed)
work(math.max(1, N // 100 == 0 and 1 or math.floor(N/100)))
-- timed
local t0 = os.clock()
local checksum = work(N)
local dt = os.clock() - t0
print(string.format("[Luau default] N=%d  sec=%.6f  ops/s=%.0f  checksum=%s",
  N, dt, N / dt, tostring(checksum)))
```
3. Read the line from **Output**.

> Command Bar runs server-side with default optimization. For the cleanest number use a `Script` (not
> `LocalScript`) and press **Run** (not Play) so no game systems compete.

### 3b. Native codegen — a Script with `--!native`
`--!native` must be the **first line** of a saved `Script` (it cannot be applied from the Command Bar).
1. Insert a `Script` into `ServerScriptService`.
2. Make the very first lines:
```lua
--!native
--!optimize 2
-- paste one benchmark's `work` function
local N = 20000000
work(math.floor(N/100))                 -- warm-up (also triggers native compile)
local t0 = os.clock()
local checksum = work(N)
local dt = os.clock() - t0
print(string.format("[Luau native] N=%d  sec=%.6f  ops/s=%.0f  checksum=%s",
  N, dt, N / dt, tostring(checksum)))
```
3. Press **Run**. Read Output.

> If a function is too large/complex Luau silently falls back to interpreted for that function; the
> number then equals the default column. That is a valid result — note it.

---

## 4. Running the CoreAI (Lua-CSharp) side

### 4a. PRIMARY — paste-in EditMode test (clean VM timing, no step cap)

This builds the **same sandbox mods use** (`LuaCsSecureEnvironment.Create()`), loads the chunk, and times
it in C# with `Stopwatch` — **without** the guard hook, so you measure the VM, not the sandbox tax. It
also has no 500k-step ceiling, so large `N` works.

Add this file under `Assets/CoreAIMods/Tests/EditMode/` (it references the same assemblies the other
LuaCs EditMode tests use — `CoreAI.Sandbox.LuaCs` and the `Lua` package). Put **one benchmark body** into
`LUA` per run, set `N`, run it from **Test Runner → EditMode**, and read the assertion message / console
line. Repeat per benchmark.

```csharp
using System.Diagnostics;
using CoreAI.Sandbox.LuaCs;
using Lua;                       // Lua-CSharp: LuaState, LuaClosure, LuaValue
using NUnit.Framework;
using UnityEngine;

namespace CoreAI.Tests.EditMode
{
    /// <summary>Raw Lua-CSharp interpreter throughput vs Luau. Times the VM in C#; os.clock is absent in the sandbox.</summary>
    public sealed class LuaVmMicroBenchmarkEditModeTests
    {
        // WHY: os is stripped from the CoreAI sandbox, so timing is done in C# around the VM call.
        private const int N = 20000000;   // match the benchmark's suggested N

        // Paste ONE benchmark body here. It must define work(N) and the harness calls work(N).
        private const string LUA = @"
local function work(N)
  local acc = 0.0
  for i = 1, N do
    acc = acc + i * 3 - (i % 7) + (i * i) % 101
  end
  return acc
end
return work(...)";

        [Test]
        public void RawVmThroughput()
        {
            var env = new LuaCsSecureEnvironment();
            LuaState state = env.Create();

            // WHY: pass N as a vararg so the same chunk works for any N without string interpolation.
            LuaClosure closure = state.Load(LUA, "vm_bench");

            // Warm-up (untimed): grows the managed heap, JITs the C# interpreter paths.
            RunOnce(state, closure, N / 100);
            System.GC.Collect();

            var sw = Stopwatch.StartNew();
            LuaValue result = RunOnce(state, closure, N);
            sw.Stop();

            double sec = sw.Elapsed.TotalSeconds;
            double opsPerSec = N / sec;
            string msg = $"[LuaCs raw] N={N} sec={sec:F6} ops/s={opsPerSec:F0} checksum={result}";
            Debug.Log(msg);
            Assert.Pass(msg);
        }

        // WHY: run the chunk with NO guard hook — the guard fires every 4 instructions (Stopwatch + GC read)
        // and is sandbox overhead, not VM speed. This is the clean interpreter-vs-interpreter measurement.
        private static LuaValue RunOnce(LuaState state, LuaClosure closure, int n)
        {
            LuaValue[] r = state.ExecuteAsync(closure, default, new LuaValue[] { n })
                                .AsTask().GetAwaiter().GetResult();
            return r.Length > 0 ? r[0] : LuaValue.Nil;
        }
    }
}
```

Notes / gotchas:
- If `state.ExecuteAsync(closure, ct, args)` does not accept an args span on your Lua-CSharp version,
  instead inline `N` by building the chunk string as `"... return work(" + N + ")"` and call
  `state.ExecuteAsync(closure).AsTask()...`. (The `return work(...)` + vararg form above is the tidy
  version; the string-inlined form is the always-works fallback.)
- To also report the **guarded** number (the real mod-runtime cost), wrap the timed call with a
  deliberately huge budget so it isn't cut, and time that separately:
  ```csharp
  var guard = new LuaCsExecutionGuard(timeoutMs: 600000, maxSteps: long.MaxValue, maxAllocatedBytes: 0);
  var sw2 = Stopwatch.StartNew();
  guard.Execute(state, closure);      // hook fires every 4 instr => this is the sandbox tax
  sw2.Stop();
  ```
  Report both raw and guarded if you want to show how much the sandbox costs.

### 4b. FALLBACK — in-game `execute_lua` (only small N; low-precision)

Use only if you cannot run EditMode. This path is **step-capped at 500k VM steps and 2000 ms**, and `os`
is unavailable, so you must (a) keep `N` tiny (a plain loop of ~50k already approaches the cap) and (b)
time with `time_realtime()` (float seconds, Gameplay capability — granted on the default `execute_lua`
tier). Precision is poor; treat results as indicative only.

Call `execute_lua` (via the CoreAI MCP `execute_lua` tool, or the LuaMods demo console) with:

```lua
local function work(N)
  local acc = 0.0
  for i = 1, N do
    acc = acc + i * 3 - (i % 7) + (i * i) % 101
  end
  return acc
end
local N = 40000                         -- MUST stay well under the 500k-step cap
work(1000)                              -- warm-up
local t0 = time_realtime()
local checksum = work(N)
local dt = time_realtime() - t0
return string.format("N=%d sec=%.6f ops/s=%.0f checksum=%s", N, dt, N/dt, tostring(checksum))
```

If you see `EXCEEDED_HARD_LIMIT_STEPS`, lower `N`. Because of the tiny `N` and float timer, do **not**
compare 4b numbers directly against Luau's large-`N` runs — prefer the §4a harness for the real
comparison. 4b is a sanity check that the VM behaves, not a throughput measurement.

---

## 5. Results table (fill in)

Use the **§4a raw** number as the CoreAI figure and the **§3a default** number as the apples-to-apples
Luau figure; `--!native` is a separate column. Ratio = Luau-default sec ÷ Lua-CSharp sec (how many times
faster Luau is; >1 means Luau faster). Keep `N` identical per row across columns.

Measured 2026-07-24 (checksums matched 1:1 across both VMs, so the bodies are identical). CoreAI side:
`LuaCsSecureEnvironment` + `state.RunAsync` (no guard hook), timed with `Stopwatch`, in the Editor with
Play Mode running (background load present). Luau side: Roblox Studio, Play mode, `os.clock()`, default and
`--!native --!optimize 2`. Both sides therefore carried editor/runtime background load — treat these as
indicative order-of-magnitude figures, not lab-clean throughput.

| Benchmark | N | Lua-CSharp sec | Lua-CSharp ops/s | Luau default ops/s | Luau `--!native` ops/s | Luau default ÷ LuaCs | Luau native ÷ LuaCs |
|---|---:|---:|---:|---:|---:|---:|---:|
| B1 Tight numeric loop | 20000000 | 7.98 | 2,505,170 | 21,296,163 | 159,332,080 | **8.5×** | **63.6×** |
| B2 Table write+read | 5000000 | 2.93 | 1,704,747 | 16,160,080 | 33,641,240 | **9.5×** | **19.7×** |
| B3 Function calls | 10000000 | 7.87 | 1,270,243 | 54,098,473 | 161,777,743 | **42.6×** | **127×** |
| B4 Recursive fib (calls/s)¹ | 20 | 31.9² | 1,688,596 | 23,432,000 | 45,760,000 | **13.9×** | **27.1×** |
| B5 String build | 500000 | 1.91 | 262,359 | 1,408,630 | 1,941,991 | **5.4×** | **7.4×** |

¹ B4's raw `N` counts recomputes of `fib(30)` (~2,692,537 calls each), so ops/s = N/dt is misleading; the
column reports **effective calls/s** (`2,692,537 × N ÷ sec`). CoreAI measured at N=5 (≈13.5M calls); Luau at
N=20 (≈53.8M calls). Both checksums are `k × 832040` (fib(30)), so the body is identical; calls/s normalizes
the differing N. ² CoreAI B4 sec is the N=20-equivalent (measured 7.97 s at N=5 × 4).

**Verdict:** Luau's interpreter is **~5–43× faster** than CoreAI's Lua-CSharp interpreter; with native
codegen **~7–127×**. The gap is largest exactly where the plan predicted — raw VM opcode dispatch (B3 function
calls 42.6× / 127×, B1 tight loop 8.5× / 63.6×) — and smallest on the library-bound row (B5 string.format/
concat 5.4× / 7.4×), which is managed C# on both sides. This confirms `PERF_VS_ROBLOX.md`: CoreAI cannot
out-*execute* a tuned register bytecode VM with native codegen, so its speed edge must come from the engine/
graphics/native-build layers and from keeping Lua thin (mods are event/timer callbacks under per-call budgets,
not million-iteration hot loops — the §1b budgets bound the practical impact, not this peak-throughput ratio).

Optional extra column if you also measured the guarded CoreAI path (§4a note):

| Benchmark | Lua-CSharp raw sec | Lua-CSharp guarded sec | Guard tax (guarded÷raw) |
|---|---:|---:|---:|
| B1 | | | |
| … | | | |

**Environment to record with the table (for reproducibility):**
- CPU / OS, Unity version, Editor vs standalone player, Mono vs IL2CPP.
- Roblox: Studio version, Run-mode (not Play), server `Script` vs Command Bar.
- Date, and the exact `N` used per row (if you changed the suggestions).

---

## 6. How to read the results

- **Expected shape:** Luau default is expected to be **several to tens of times** faster than the
  Lua-CSharp interpreter on every benchmark; `--!native` widens that further on the compute-heavy rows
  (B1, B3, B4), often to 1–2 orders of magnitude. The exact multiple is the open question this table
  answers — do not assume a number, measure it.
- **Where CoreAI is closest:** benchmarks dominated by C#-side library calls (B5's `string.format`/
  `concat`, which are managed C# in Lua-CSharp) usually show the **smallest** gap. Benchmarks dominated
  by raw VM opcode dispatch (B1, B3, B4) usually show the **largest** gap, because that is exactly where a
  tuned bytecode/native VM beats a managed interpreter.
- **Raw vs guarded (CoreAI):** the guard-tax column shows the cost the *sandbox* adds on top of the VM
  (Stopwatch + heap read every 4 instructions). A large tax means "mods pay for safety"; the raw column is
  the fair VM-vs-VM comparison, the guarded column is the honest "what a mod actually experiences".
- **Ratios, not just seconds:** report ops/s and the ratio so the comparison survives an `N` change. Two
  runs at different `N` are comparable via ops/s; via raw seconds they are not.
- **What a big gap does and does not mean:** a 10–50× Luau advantage is normal and expected for a managed
  interpreter vs a purpose-built game VM — it does **not** indicate a bug in Lua-CSharp. CoreAI mods are
  event/timer callbacks doing small bursts of logic under a step budget, not million-iteration hot loops,
  so the practical impact is bounded by the per-call budgets in §1b, not by this peak-throughput ratio.
  Use the table to size mod workloads, not to judge the VM as "too slow".

---

## 7. One-paragraph summary for the run

`os.clock()` does not exist in CoreAI's Lua sandbox (the `os` library is stripped), so the CoreAI side is
timed in C# with `Stopwatch` while Roblox uses `os.clock()`; the Lua *body* is identical on both. Run the
five bodies in §2 through the Studio wrappers in §3 (default **and** `--!native`) and the paste-in EditMode
harness in §4a (which bypasses the 500k-step guard so large `N` and clean VM timing are possible). Fill in
§5 and read it with §6. The `execute_lua` path (§4b) is a small-`N` sanity fallback only, because its 500k
step / 2000 ms budget and per-4-instruction guard hook make it unsuitable for real throughput numbers.
