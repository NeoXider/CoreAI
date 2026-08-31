# Guarded Lua VM throughput

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

## Count validation and the real production value

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

## Fixed per-resume cost

The coroutine body was an infinite loop whose first action is `coroutine.yield()`. It executes an average of 2.99995 VM instructions per resume. Results are five-trial medians:

| Resume configuration | CoreCLR | Mono | Hook firings/resume |
|---|---:|---:|---:|
| Raw resume, no hook | 0.399 us | 2.016 us | 0 |
| Reused full guard, batch 4 | 0.514 us | 25.224 us | 0 |
| New full guard, batch 4 | 0.560 us | 25.106 us | 0 |
| Current `LuaCsCoroutineHandle` behavior | 0.824 us | 3.936 us | 3 |

The immediate-yield body is shorter than batch 4, so no batch-4 callback fires. Its guarded time isolates hook construction/reset, the initial heap baseline read, `SetHook`, `ResumeAsync`, and hook clearing. For the full batch-4 guard, fixed cost after subtracting the three raw VM instructions is 24.87 us in the confirmation run. The independent first run measured 26.67 us fixed.

## Frame-budget arithmetic

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

## Important production-path discrepancy

There are two different production hooks:

- `LuaCsExecutionGuard` and the secured raw-coroutine wrapper use batch **4** and sample both clock and managed heap. Those are the full-guard numbers above.
- `LuaCsCoroutineHandle.Resume` currently calls `SetHook(..., 1)` and its callback checks steps plus `Stopwatch.ElapsedMilliseconds`, but not `GC.GetTotalMemory(false)`.

The exact current handle body measured **1,810,833 VM instructions/s on Mono**, with 2.280 us fixed cost. Its 10,000-instruction resume is **5.525 ms**: 0.724 fit in 4 ms and 1.448 fit in 8 ms; one handle resume can execute 7,239 instructions in 4 ms. A 400-thread, 10,000-instruction burst still costs about 2.210 seconds. The ~40k batch-1 full-guard rate must not be attributed to `LuaCsCoroutineHandle`, because that handle does not perform the heap read.

Which number governs the MVP2 scheduler depends on which path creates its threads. Code using `LuaCsCoroutineHandle` should use the 5.525 ms figure; code using the secured full coroutine guard should use 63.2-67.4 ms. Neither makes `400 * 10,000` viable.

## Adaptive batch

Adaptive widening would materially reduce the guard tax. On Mono, batch 256 reached 5,555,744 instructions/s: **35.1x batch 4** and 44.2% of raw throughput. With the same fixed cost, a 10,000-instruction resume would be about **1.825 ms**, so about 2.19 fit in 4 ms and 4.38 in 8 ms. Batch 128 would be about 2.78 ms per 10,000 instructions. Even the raw no-hook ceiling is about 0.797 ms per 10,000 instructions, only about five such resumes in 4 ms—not 400.

This optimization cannot simply widen based on remaining step budget. The production source deliberately keeps batch 4 because a short exponential string-concatenation bomb can allocate catastrophically between heap samples. A safe adaptive design must bound allocation-growth exposure and narrow before the memory limit, not only before the instruction limit. The throughput curve proves the potential gain; it does not prove that batch 128 or 256 is safe.

## Machine and method

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
