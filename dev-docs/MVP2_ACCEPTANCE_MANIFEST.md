# MVP2 acceptance manifest

The release is judged against **this file**, not against prose. Audit round 4 demonstrated a concrete
cheat for every prose gate (canned chat replies, denying everyone to "prove" confidentiality, one dummy
quota, invoking nobody and asserting zero touches, a constant memory reading, zero discovered tests,
six easy corpus fixtures). Everything below is therefore fixed **before** measuring, and every gate has
a negative twin so that "did nothing" cannot pass.

## 1. Reference machine — fixed before any measurement

| Field | Value |
|---|---|
| CPU / RAM / GPU | *filled in at Phase 3, then frozen* |
| OS / Unity | Windows 11, Unity 6000.3.14f1 |
| Power profile | high performance, on AC |
| Runs | (a) editor batchmode, (b) Standalone Mono player |
| Repeats | 5 runs; report median and worst |

Thresholds apply to this machine. Other hardware is reported, never gated.

## 2. Workload — deterministic, per simulated actor

| Field | Value |
|---|---|
| Mods per actor | 1 |
| Lua body | fixed script `bench_actor.lua`, deterministic, **instruction count set from the MEASURED guarded rate — see §8**, asserted by the guard's step counter |
| Threads per actor | 10 deferred + 10 delayed |
| Due-work distribution | delayed deadlines spread uniformly across 10 frames, seeded RNG, seed **20260831** |
| Timer cadence | 1 timer per actor at 0.5 s |
| Event cadence | 1 emit per actor per second |
| Subscriber ratio | 1 subscriber per emit; **19 non-subscribers must not be invoked** |
| Chat arrival | two patterns, both required: (a) staggered, 1 request/30 s/actor; (b) synchronized burst, all actors at t=0 |
| Duration | 60 s measurement after 30 s warm-up; soak variant 30 min |

## 3. Provider — frozen

| Field | Value |
|---|---|
| Model id | *frozen at Phase 3 from what LM Studio actually serves* |
| Context cap / output cap | frozen with the model |
| Backend concurrency | frozen; recorded in the result |
| Deterministic backend | a scripted stub for queue/latency separation, **plus** one real provider-backed run |

## 4. Counters that must be non-zero

A run reporting zero for any of these **fails**, regardless of its timings:

- completed Lua operations (sum of guard step counts)
- thread resumes
- events delivered to subscribers
- chat responses actually produced by the provider (not stubbed) in the provider-backed run
- discovered tests: expected count fixed at Phase 3; **skips = 0**

## 5. Gates — every one with a negative twin

| # | Positive | Negative (must be refused/absent) |
|---|---|---|
| G1 authorization, mods | actor A manages its own mod | A→B `unload`/`reload`/`revert`/`forget` refused, naming actor + reason |
| G2 authorization, world | A mutates its own instance | A→B mutate/destroy refused |
| G3 host-protected | mod writes `workspace.CurrentCamera` (samples rely on it) | A cannot destroy or reparent host-owned singletons |
| G4 chat privacy | A reads A's history | A cannot read, clear or observe B's history or rate state |
| G5 quotas | at quota `N` the actor succeeds | at `N+1` refused with actor + reason; **a build with quotas absent fails G5** |
| G6 event routing | the subscriber's handler runs | the 19 non-subscribers' handlers never run; touched-entry count asserted == subscribers |
| G7 no global fan-out lock | — | structural test: emit path holds no process-wide lock |
| G8 reconnect | same durable actor resumes the same memory | duplicate operation ids are idempotent; no memory fork |
| G9 engine-free ports | ports resolve | asmdef test: the engine-free assembly references no Unity assembly, transitively |
| G10 chat throughput | ≥95% of offered load served, p95 ≤ 5 s | 0 cross-actor cancellations; no actor starved > 60 s |
| G11 WebGL | §6.5 checklist in a real browser run | a static checklist alone does not satisfy G11 |
| G12 corpus | fixed fixture ids (listed at Phase 10), ≥30% unmodified | — |

## 6. Frame budget — derived, with the arithmetic shown

Not invented. The derivation is published with the result:

```
budget_ms = (resumes_per_frame × ops_per_resume) / measured_guarded_ops_per_second × 1000
```

`measured_guarded_ops_per_second` must be measured **with the per-instruction hook installed** — the
existing benchmark records only raw unguarded throughput (1.27–2.51 M ops/s), and audit round 5
correctly noted that converting raw numbers into guarded ones is invalid. Phase 3 therefore measures
the guarded rate first, then publishes the budget and its arithmetic.

## 7. Memory

| Field | Value |
|---|---|
| Warm-up | 30 s, fixed |
| Managed heap slope | ≤ 1 MB/min after warm-up |
| Absolute RSS ceiling | fixed at Phase 3 from the 20-actor baseline, then frozen |
| Per-state byte metering | **see the open risk in the plan — not available on Lua-CSharp 0.5.6** |


## 8. Measured guarded VM throughput — the number the budget must respect

Measured 2026-08-31 with the shipped `Lua.dll` 0.5.6, harness in `scratchpad/vmbench`. This replaces
the plan's earlier assumption, which was wrong by roughly 6750x.

| Configuration | Result |
|---|---|
| Production `LuaCsExecutionGuard` batch | **4** |
| Mono, batch 4 | 148 374 - 158 240 VM instructions/s |
| One 10 000-instruction resume, batch 4 | **67.4 ms** |
| Resumes fitting a 4 ms frame, batch 4 | 0.059 — i.e. one resume capped at **589 instructions** |
| Resumes fitting an 8 ms frame, batch 4 | 0.119 — **1 183 instructions** |
| `LuaCsCoroutineHandle` as actually configured (count-1 / time-only) | **5.525 ms per 10 000 instructions** |
| Mono, batch 256 | **35.1x faster**, ≈1.825 ms per 10 000 |

**Consequences for this release.**
1. The workload in §2 cannot be 10 000 instructions per resume. It is set from this table, and the
   manifest states instructions-per-frame rather than a fictional resume size.
2. **The guard batch is the dominant cost, and what it buys is questionable.** Batch 4 exists to catch
   allocation bombs quickly, but the VM-fork research established that the allocation guard is a
   process-wide first-growth backstop that a determined actor evades regardless. So the release is
   currently paying a 35x throughput penalty for a guarantee it does not actually have. Widening the
   batch is therefore coupled to the memory decision, not independent of it.
3. Measurement caveat, stated by the harness author: the Mono CLI used was Unity's bundled x86
   runtime. The gate must be confirmed in the real 64-bit Standalone Mono player before any threshold
   is frozen.
