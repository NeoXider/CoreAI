# The 100–200 actor target was blocked by two defaults and one broken metric

Measured 2026-09-05 on `tools/ScaleHarness`, the frozen per-actor workload, host CoreCLR.
Evidence: `artifacts/scale/staircase-honest.json`, `diag-pending512*.json`, `diag-conc16-n200.json`.

## Before

Every staircase step failed, including 20 actors. `SCALE_CHARACTERIZATION.md` recorded
"heap budget fails at 20" and the MVP2.5 plan treated that as a blocker on the owner's 20-client
release promise.

## The metric was measuring the collector, not the program

`heapSlopeMegabytesPerMinute` was a linear fit over 20 samples of `GC.GetTotalMemory(false)` — live
objects **plus** whatever garbage had not been collected yet. A Gen2 landing inside the window drops
that by tens of megabytes, so the fit came out negative.

Three identical repeats, same configuration, no code changes between them:

| N | slopes (MB/min) | spread | budget |
|---:|---|---:|---:|
| 20 | 1.33, 4.36, 3.93 | 3.0 | 1 |
| 50 | 3.44, −29.05, −29.10 | 32.5 | 1 |
| 100 | −45.64, −70.42, −93.32 | **47.7** | 1 |
| 200 | 13.91, 10.15, 19.50 | 9.4 | 1 |

The noise is 3× to 47× the threshold it was gating on, and the sign is not even stable. Over the same
repeats `allocBytesPerFrame` spread by **0.0 bytes at every step**.

The gate now uses two reproducible numbers instead:

- **Retained heap delta** — both endpoints taken after `GC.Collect()` → `WaitForPendingFinalizers()` →
  `GC.Collect()`, so it measures retention rather than collector timing. Measured spread across three
  repeats at N=20: **0.13 MB**.
- **Allocation bytes per actor per frame** — spread **0.00 bytes**.

The slope is still reported, because a large positive one is a useful hint, but it decides nothing.

## Two defaults sized for small sessions

With the honest metric, memory passed everywhere and the real limiter surfaced: chat admission.

| N | pending 64 / concurrent 4 | pending 512 / concurrent 4 | pending 512 / concurrent 16 |
|---:|---|---|---|
| 100 | 600 offered, **96 refused**, FAIL | 200/200, 0 refused, **PASS** | — |
| 200 | 1200 offered, **396 refused**, FAIL | 400/400, 0 refused, p95 5234 ms vs 5000 → FAIL | 400/400, 0 refused, p95 **1346 ms**, **PASS at 16 ms** |

`AiOrchestrationQueueOptions.MaxPending = 64` and `MaxConcurrent = 4` are defaults, not limits. At 100+
actors bursting together the queue refuses the overflow before any work is attempted; at 200 the four
lanes stretch the tail past the 5 s budget.

## After

- **200 actors pass the 16 ms (60 Hz) frame budget, the memory gate and the chat gate** with the queue
  and concurrency sized for the actor count. `largest measured passing N: 200`.
- The 4 ms budget still fails at 200 (median 7.37 ms). That is a 240 Hz-equivalent target, not 60 Hz.
- Allocation work contributed: signal-handler thread records are pooled, halving per-frame allocation
  (103.1 → 51.3 KB at N=20). Necessary, but it was not what blocked the target.

## What this does NOT say

The harness drives a **scripted provider with a fixed 100 ms latency**. These numbers bound CoreAI, not
a deployed system. The G10 real-provider run measured a 17.4–38.5 s provider p95 with one lane, where
the AI backend is the constraint long before any of this. A shipping concurrency claim still needs the
frozen manifest re-run against the real backend and the lane count that implies.
