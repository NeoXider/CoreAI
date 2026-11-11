# Guarded Lua VM throughput

Two dated runs of this benchmark against the shipped `Assets/CoreAIMods/Plugins/Lua.dll` (SHA-256
`BE5ACC6819888E3EBDC2509EB7779D52E43B437C8EB1130E24C5CDA0407BAF3F`): 2026-11-11 below, 2026-08-31 after it.
Read the 2026-11-11 section first: it corrects what the "Mono" column of the older run means.

## 2026-11-11: the collector decides the guard's cost

Machine as in "Machine and method" at the end, **contended** (ten agents were building and testing in the same
working tree, so absolute rates are pessimistic and noisy; ratios inside one row hold). CoreCLR is .NET 8.0.21
x64. Mono is the Mono 6.13.0 CLI bundled with Unity 6000.6.0f1, x86, once with the Boehm collector
(`mono-bdwgc.exe`, what the Editor and Mono players embed as `mono-2.0-bdwgc.dll`) and once with SGen
(`mono.exe`, the CLI default, which Unity does not run). Median of five trials, VM instructions per second,
commit `654bb318` plus this change.

| Count-hook batch | CoreCLR | Mono Boehm | Mono SGen | Boehm vs raw |
|---:|---:|---:|---:|---:|
| No hook | 101,082,211 | 16,389,636 | 14,922,529 | 100.0% |
| 1 | 6,764,539 | 1,698,470 | 46,059 | 10.4% |
| 2 | 11,765,386 | 3,113,661 | 86,974 | 19.0% |
| **4 (production guard)** | 22,430,607 | 4,931,876 | 182,147 | 30.1% |
| 8 | 27,679,432 | 6,759,333 | 349,550 | 41.2% |
| 16 | 49,696,186 | 9,866,932 | 754,881 | 60.2% |
| 32 | 61,702,629 | 13,226,386 | 1,266,682 | 80.7% |
| 64 | 58,548,089 | 14,336,327 | 2,439,313 | 87.5% |
| 128 | 76,834,691 | 15,671,614 | 3,472,369 | 95.6% |
| 256 | 52,459,479 | 17,355,765 | 5,502,974 | 105.9% |

| Primitive (ns per call) | CoreCLR | Mono Boehm | Mono SGen |
|---|---:|---:|---:|
| `Stopwatch.GetTimestamp()` | 26.8 | 53.9 | 52.7 |
| `GC.GetTotalMemory(false)` | 34.7 | 9.1 | 6508.5 |

| Fixed cost of one resume (ns) | CoreCLR | Mono Boehm | Mono SGen |
|---|---:|---:|---:|
| raw resume, no hook | 474 | 1911 | 1502 |
| guard mirror, batch 4 (hook reset + heap baseline read) | 622 | 2007 | 20856 |

The scheduler-thread hook mirror (a clock read per fire, no heap read) was measured in a second run the same
day, with the machine about twice as loaded (compare its "no hook" row with the one above):

| Second run | CoreCLR | Mono Boehm | Mono SGen |
|---|---:|---:|---:|
| no hook, same run | 41,913,040 | 8,516,945 | 6,827,807 |
| scheduler-thread hook, count 1 (until 2026-11) | 5,000,844 | 587,011 | 823,057 |
| **scheduler-thread hook, count 4 (now)** | 14,677,336 | 1,439,083 | 2,394,871 |
| count 4 over count 1 | 2.93x | 2.45x | 2.91x |

What the table says:

- **The 158 k instructions/s "Mono" figure of 2026-08-31 is the SGen collector.** A guard hook reads
  `GC.GetTotalMemory(false)`: about 9 ns on Boehm, about 6,500 ns on SGen. On Boehm the production batch of 4
  keeps about 30% of raw throughput, not 1.26%. Unity's Editor, Mono players and IL2CPP players all use Boehm.
- On Boehm and CoreCLR the cost of a guard hook is **the VM's dispatch into the hook** (about 0.5 us on Mono,
  about 0.1 us on CoreCLR), not the hook's body: the clock and heap reads together are about 60 ns. Reading
  the heap less often therefore buys almost nothing there; only firing less often does.
- The ceiling is the raw row: with no hook at all the VM is about **3.3x** faster than the batch-4 guard on
  Boehm (4.5x on CoreCLR). The 35x of the older run does not exist on a runtime Unity uses.
- Until this change the scheduler threads' hook (`LuaCsCoroutineHandle`: every task thread, signal handler and
  mod main chunk) fired on **every** instruction: the "count 1" row of the second table. It now uses the
  guard's batch of 4: the "count 4" row.

### Production code before and after (`tools/perfbench`)

The same day, the production sources (compiled by `tools/portable/LuaTier`) before and after the change, run
alternately three times; each cell is the best of the three medians. CoreCLR bytes are exact
(`GC.GetAllocatedBytesForCurrentThread`); Mono does not count allocations. Three cases whose code did not
change (`Box`, `Unbox`, `Fire` with no connections) moved by up to 25% between the two sides in these runs, so
a time ratio under about 1.3x is not evidence on this machine.

| Case | CoreCLR time | CoreCLR bytes | Mono Boehm time |
|---|---|---|---|
| `LuaCsCoroutineHandle.Resume(numeric loop)`, per iteration of 8 instructions | 1,244 -> 411 ns (3.0x) | 0 -> 0 | 8,312 -> 2,103 ns (4.0x) |
| `LuaCsCoroutineHandle.Resume`, immediate yield | 1,837 -> 1,253 ns | 64 -> 0 | 10,542 -> 6,169 ns |
| `LuaCsExecutionGuard.Execute(numeric loop)`, per iteration (batch unchanged) | 557 -> 573 ns | 0 -> 0 | 2,993 -> 2,536 ns |
| `LuaCsExecutionGuard.Execute(empty function)` | 640 -> 610 ns | 72 -> 0 | 5,715 -> 4,669 ns |
| `IScriptExecutionGuard.Invoke(fn, 1 argument)` -> 1 result | 650 -> 721 ns | 240 -> 120 | 7,176 -> 5,836 ns |
| Heartbeat frame, 64 Lua handlers | 383 -> 317 us | 46,480 -> 35,408 | 2,845 -> 1,771 us |
| `Advance`, 200 threads resumed every frame (`task.wait()`) | 1,462 -> 1,116 us | 68,056 -> 54,424 | 6,635 -> 3,838 us |
| `Advance`, idle world | 1,267 -> 863 ns | 856 -> 24 | 13,137 -> 2,586 ns |
| `events_emit` -> `hooks_on` handler via `LuaCsModRuntime.Tick` | 2,504 -> 3,140 ns | 600 -> 144 | 15,098 -> 12,301 ns |
| `hooks_every` timer via `LuaCsModRuntime.Tick` | 1,885 -> 1,521 ns | 448 -> 144 | 14,992 -> 11,501 ns |
| scene `sample-mods`, frame p50 | 2.1 -> 1.7 ms | 26,824 -> 21,712 per frame | 2.1 -> 1.3 ms |
| scene `parts-2048-heartbeat`, frame p50 | 1.4 -> 0.8 ms | 48,816 -> 47,344 per frame | 6.4 -> 3.6 ms |

Per handler fire that is 713 -> 553 B, per `task.wait()` resume 336 -> 272 B. What is left of the idle frame
(24 B) is the render delta boxed in `LuaCsRbxApiBindings.PumpPreRender`; what is left of a legacy dispatch
(144 B) is the mutation envelope scope and the transaction frame. A scheduler thread now reports its steps per
finished window, so "Lua steps per frame" reads up to three lower per resume (1,514 -> about 1,410 in
`sample-mods`) for the same work.

## Allocation exposure of the hook window

The allocation budget is checked from the hook, so the hook window is how long a script runs unobserved. What
holds after the 2026-11 change, with **B** the allocation budget of the run and **A** the bytes the VM can
allocate in one millisecond on the machine (2-12 MB on the reference machine: a concatenation runs at 2-11
GB/s):

| Hook | Window | Heap sampled | A bomb of two-operand concatenations is cut with its largest string at most |
|---|---|---|---|
| `LuaCsExecutionGuard` (handlers, timers, exports, `execute_lua`) | 4 instructions, unchanged | at every fire | 5 B |
| raw `coroutine.resume` (`LuaCsSecureEnvironment`) | 4 instructions, unchanged | at every fire | 5 B |
| `LuaCsCoroutineHandle` (scheduler threads) | 4 instructions; 1 after a window that lasted 1 ms or longer, until 256 instructions in a row were fast; always 1 for a handle with a lifetime step cap | once per millisecond of execution | 5 B + 4 A |

Where the numbers come from. A check that does not trip leaves the live growth at no more than 1.25 B (the
budget plus the quarter-budget slack of `LuaCsAllocationBudget`). A window of four instructions holds at most
two two-operand concatenations (`MOVE, MOVE, CONCAT` is three instructions; measured: `s = s .. s` adds three
instructions, a loop iteration four), so the largest string can grow fourfold before the next fire: 5 B. A
scheduler thread samples once per millisecond, so up to A more bytes can exist before the sample: 4 x (1.25 B +
A). `LuaCsHookBatchEditModeTests` runs a loop bomb and a straight-line bomb under both hooks against these
bounds, with a twin that churns twelve budgets of garbage and must not be cut.

What changed for scheduler threads, exactly: before, the hook fired after every instruction, so an instruction
that took a millisecond or longer was followed by a sample at once. Now up to three more instructions can run
before the sample that follows such an instruction when the instructions before it were fast; after it the hook
is back to one instruction until 256 instructions in a row were fast again, so a script that keeps building large
strings is watched after every instruction, as before. The wider window is the one the guard has always had, so
no path is watched less closely than the guard was before this change.

What no hook window bounds, before or after: **one instruction**. A concatenation of many operands
(`s .. s .. s ..`) is a single `CONCAT`; measured, 150 operands cost 150 `MOVE`s and one `CONCAT`, and the
parser refuses a chain of 200 ("too many Go levels"). So one instruction can multiply a string about 199
times: 199 strings of the library cap (1,000,000 characters) are 398 MB in one instruction under any budget.
The guard trips at the next fire, after the fact. Closing that needs the VM (a cap in `CONCAT`) or the source
rewriter, not the hook.

Why the guard's own batch stayed at 4: every extra instruction in the window is one more the script runs
before the heap is looked at, and three instructions are one more doubling. A batch of 16 would run about
1.6-2x faster on Boehm and let a two-operand bomb reach 80 B instead of 5 B; a batch of 8, 1.4x and 10 B. A
wider window that narrows when a window runs slow protects strings the run itself built, but not one the
state already held when the run began, and nothing cheap tells the hook whether such a string exists. Widening
is a product decision with these numbers, not a free optimization.

## 2026-08-31 run (its "Mono" column is the SGen collector)

Measured 2026-08-31 against the shipped `Assets/CoreAIMods/Plugins/Lua.dll` (SHA-256 `BE5ACC6819888E3EBDC2509EB7779D52E43B437C8EB1130E24C5CDA0407BAF3F`). Rates below are median VM instructions per second from five trials in the confirmation run.

| Count-hook batch | CoreCLR VM instr/s | Mono VM instr/s | CoreCLR / Mono | Mono vs raw |
|---:|---:|---:|---:|---:|
| No hook | 140,737,125 | 12,567,486 | 11.20x | 100.00% |
| 1 | 7,018,465 | 40,481 | 173.38x | 0.322% |
| 2 | 13,511,659 | 79,416 | 170.14x | 0.632% |
| **4 (production `LuaCsExecutionGuard`)** | **24,430,913** | **158,240** | **154.39x** | **1.259%** |
| 8 | 37,166,852 | 313,916 | 118.40x | 2.498% |
| 16 | 54,344,408 | 610,482 | 89.02x | 4.858% |
| 32 | 69,743,531 | 1,164,439 | 59.89x | 9.265% |
| 64 | 87,019,997 | 2,099,306 | 41.45x | 16.704% |
| 128 | 93,716,160 | 3,629,303 | 25.82x | 28.879% |
| 256 | 101,624,531 | 5,555,744 | 18.29x | 44.207% |

The raw B1 control was 17.59 M loop iterations/s on CoreCLR and 1.57 M on Mono. A separate first Mono run produced 2.30 M loop iterations/s, so Mono reproduced the existing 1.27-2.51 M range. Production batch 4 reproduced at 148,374 and 158,240 VM instructions/s across the two independent runs; the conclusion is not a one-run outlier.

### Count validation and the real production value

`LuaCsExecutionGuard.HookInstructionBatch` is **4**, and `BeginGuard` passes that value to `LuaState.SetHook`. The benchmark callback mirrors the production body: step update/check, `Stopwatch.GetTimestamp()`, `GC.GetTotalMemory(false)`, allocation check, and `ctx.Return()`.

The B1 bytecode executes exactly `8 * N + 10` VM instructions. This was calibrated with a count-1 hook at `N=1000` and `N=1001`, then independently validated at `N=2000`. In every curve row:

`hook firings = floor(VM instructions / batch)`

For example, the confirmation batch-4 trial executed 105,346 instructions and fired the hook 26,336 times. Therefore the table counts **VM instructions**, not hook firings. The initially reported ~38.8k figure was real but was the batch-1 row, not the production batch-4 row.

The dominant Mono cost is real production work, not counting instrumentation. Direct medians were:

| Primitive | CoreCLR | Mono |
|---|---:|---:|
| `Stopwatch.GetTimestamp()` | 20.0 ns/call | 42.5 ns/call |
| `GC.GetTotalMemory(false)` | 27.4 ns/call | 5,434 ns/call |

The full Mono VM-to-managed callback costs more still. At batch 4, the guard discards 98.74% of raw Mono throughput.

### Fixed per-resume cost

The coroutine body was an infinite loop whose first action is `coroutine.yield()`. It executes an average of 2.99995 VM instructions per resume. Results are five-trial medians:

| Resume configuration | CoreCLR | Mono | Hook firings/resume |
|---|---:|---:|---:|
| Raw resume, no hook | 0.399 us | 2.016 us | 0 |
| Reused full guard, batch 4 | 0.514 us | 25.224 us | 0 |
| New full guard, batch 4 | 0.560 us | 25.106 us | 0 |
| Current `LuaCsCoroutineHandle` behavior | 0.824 us | 3.936 us | 3 |

The immediate-yield body is shorter than batch 4, so no batch-4 callback fires. Its guarded time isolates hook construction/reset, the initial heap baseline read, `SetHook`, `ResumeAsync`, and hook clearing. For the full batch-4 guard, fixed cost after subtracting the three raw VM instructions is 24.87 us in the confirmation run. The independent first run measured 26.67 us fixed.

### Frame-budget arithmetic

Using the confirmation batch-4 rate:

- Instruction time: `10,000 / 158,240 = 63.195 ms`.
- Fixed resume cost: `0.02487 ms`.
- Total per 10,000-instruction resume: **63.220 ms**.
- 4 ms: `4 / 63.220 = 0.06327` resumes.
- 8 ms: `8 / 63.220 = 0.12654` resumes.

Using the slower independent run as the conservative plan value:

- Instruction time: `10,000 / 148,374 = 67.397 ms`.
- Fixed resume cost: `0.02667 ms`.
- Total: **67.424 ms per 10,000-instruction resume**.
- 4 ms: **0.05933 resumes**.
- 8 ms: **0.11865 resumes**.

Thus **zero complete 10,000-instruction resumes fit in either 4 ms or 8 ms**. One such resume consumes about 16.86 four-millisecond budgets or 8.43 eight-millisecond budgets.

The precise conservative workload that fits in a 4 ms frame is:

`floor((0.004 - 0.00002667) * 148,374) = 589 VM instructions`

That is **one full-guard resume capped at 589 instructions**, not 10,000. At 8 ms, the corresponding cap is 1,183 instructions. Alternatively, a 4 ms frame fits at most 149 immediate-yield full-guard resumes; 400 immediate yields already take about 10.7 ms.

The planned burst is unequivocally impossible under the full guard:

`400 * 67.424 ms = 26.970 seconds`

That is 6,742 times a 4 ms budget and 3,371 times an 8 ms budget.

### Important production-path discrepancy

There are two different production hooks:

- `LuaCsExecutionGuard` and the secured raw-coroutine wrapper use batch **4** and sample both clock and managed heap. Those are the full-guard numbers above.
- `LuaCsCoroutineHandle.Resume` currently calls `SetHook(..., 1)` and its callback checks steps plus `Stopwatch.ElapsedMilliseconds`, but not `GC.GetTotalMemory(false)`.

The exact current handle body measured **1,810,833 VM instructions/s on Mono**, with 2.280 us fixed cost. Its 10,000-instruction resume is **5.525 ms**: 0.724 fit in 4 ms and 1.448 fit in 8 ms; one handle resume can execute 7,239 instructions in 4 ms. A 400-thread, 10,000-instruction burst still costs about 2.210 seconds. The ~40k batch-1 full-guard rate must not be attributed to `LuaCsCoroutineHandle`, because that handle does not perform the heap read.

Which number governs the MVP2 scheduler depends on which path creates its threads. Code using `LuaCsCoroutineHandle` should use the 5.525 ms figure; code using the secured full coroutine guard should use 63.2-67.4 ms. Neither makes `400 * 10,000` viable.

### Adaptive batch

Adaptive widening would materially reduce the guard tax. On Mono, batch 256 reached 5,555,744 instructions/s: **35.1x batch 4** and 44.2% of raw throughput. With the same fixed cost, a 10,000-instruction resume would be about **1.825 ms**, so about 2.19 fit in 4 ms and 4.38 in 8 ms. Batch 128 would be about 2.78 ms per 10,000 instructions. Even the raw no-hook ceiling is about 0.797 ms per 10,000 instructions, only about five such resumes in 4 ms—not 400.

This optimization cannot simply widen based on remaining step budget. The production source deliberately keeps batch 4 because a short exponential string-concatenation bomb can allocate catastrophically between heap samples. A safe adaptive design must bound allocation-growth exposure and narrow before the memory limit, not only before the instruction limit. The throughput curve proves the potential gain; it does not prove that batch 128 or 256 is safe.

### Machine and method

- Machine: ASUS TUF Gaming A15 `FA506NC_FA506NC`.
- CPU as reported by WMI: `AMD Eng Sample: 100-000000561-40_Y`, 8 cores / 16 logical processors, 3001 MHz reported maximum.
- RAM: 42,219,933,696 bytes (39.32 GiB).
- OS: Windows 11 Pro, build 26200, x64.
- Power: `Performance` plan, AC power, battery 79%.
- CoreCLR: .NET 8.0.21, x64.
- Mono: Unity 6000.3.14f1 bundled Mono 6.13.0 CLI, x86, SGen concurrent GC.
- Harness: Release-optimized `net8.0` and `netstandard2.1` executables linking the shipped `Lua.dll`; five timed trials per row, median reported, adaptive `N` targeting roughly 0.65 seconds, identical checksum each trial.
- Unity was not launched and no repository file was modified.

The available Unity-bundled command-line Mono is x86. A 64-bit Standalone Mono player configuration could not be measured without running/building Unity, which the task prohibited; no x64-Mono number is estimated here. The raw control matching the prior Unity result is reassuring, but the release acceptance run should repeat the full-guard row inside the actual 64-bit Standalone Mono player before freezing a hardware gate.
