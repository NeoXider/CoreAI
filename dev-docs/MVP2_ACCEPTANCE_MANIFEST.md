# MVP2 acceptance manifest

The release is judged against **this file**, not against prose. Audit round 4 demonstrated a concrete
cheat for every prose gate (canned chat replies, denying everyone to "prove" confidentiality, one dummy
quota, invoking nobody and asserting zero touches, a constant memory reading, zero discovered tests,
six easy corpus fixtures). Everything below is therefore fixed **before** measuring, and every gate has
a negative twin so that "did nothing" cannot pass.

## 1. Reference machine — fixed before any measurement

| Field | Value |
|---|---|
| CPU / RAM / GPU | **NOT MEASURED** — the Phase 3 reference measurement machine has not been frozen |
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

The real-provider configuration is frozen in `tools/G10Harness/g10.real-provider.local.json`. The
classification column is normative: it prevents facts observed from LM Studio from being blurred with
parameters deliberately chosen for this run.

| Field | Frozen value | Classification |
|---|---|---|
| Provider mode | `RealProvider` | chosen harness mode |
| Endpoint | `http://127.0.0.1:1234/v1` | observed LM Studio service fact |
| API key value | `lm-studio` | chosen local compatibility placeholder, not a measurement |
| Model id | `ling-3.0-tiny` | observed LM Studio service fact |
| Context cap | 131072 tokens | observed LM Studio service fact |
| Backend concurrency | 1 | observed LM Studio service fact (`lms ps`: parallel 1) |
| Output cap | 512 tokens | chosen run parameter, **not a measurement** |
| Orchestrator concurrency | 4 | chosen run parameter, **not a measurement** |
| Request timeout | 120 s | chosen run parameter, **not a measurement** |
| Temperature / extra body | 0.0 / empty | chosen run parameters |
| Deterministic backend | scripted stub, recorded separately in §5.1 | queue/latency control, not provider capacity evidence |

## 3b. Production-path rule (added after the phase-1 QA)

A guarantee counts as delivered only when a test drives it **through the production path**. QA found
that all 18 phase-1 tests passed while the shipped code never routed through the new components — the
types existed and behaved, and production ignored them. So for every gate below, the test must reach
the component the way the running system does, not by constructing it directly. See
`MVP2_PHASE1_CORRECTION.md`.

## 4. Counters that must be non-zero

A run reporting zero for any of these **fails**, regardless of its timings:

- completed Lua operations (sum of guard step counts)
- thread resumes
- events delivered to subscribers
- chat responses actually produced by the provider (not stubbed) in the provider-backed run
- discovered tests: STUB-MODE harness **6 discovered, 0 skipped**; real-provider-backed run
  discovery is **NOT MEASURED**

## 5. Gates — every one with a negative twin

| # | Positive | Negative (must be refused/absent) |
|---|---|---|
| G1 authorization, mods | actor A manages its own mod | A→B `unload`/`reload`/`revert`/`forget` refused, naming actor + reason |
| G2 authorization, world | in an ACL-versioned world, A mutates its own instance | in an ACL-versioned world, A→B mutate/destroy refused |
| G3 host-protected | mod writes `workspace.CurrentCamera` (samples rely on it) | A cannot destroy or reparent host-owned singletons |
| G4 chat privacy | A reads A's history | A cannot read, clear or observe B's history or rate state |
| G5 quotas | at quota `N` the actor succeeds | at `N+1` refused with actor + reason; **a build with quotas absent fails G5** |
| G6 event routing | the subscriber's handler runs | the 19 non-subscribers' handlers never run; touched-entry count asserted == subscribers |
| G7 no global fan-out lock | — | structural test: emit path holds no process-wide lock |
| G8 reconnect | same durable actor resumes the same memory | duplicate operation ids are idempotent; no memory fork |
| G9 engine-free ports | ports resolve | asmdef test: the engine-free assembly references no Unity assembly, transitively |
| G10 chat throughput | ≥95% of offered load served, p95 ≤ 5 s | 0 cross-actor cancellations; no actor starved > 60 s |
| G11 WebGL | §6.5 checklist in a real browser run | a static checklist alone does not satisfy G11 |
| G12 corpus | exact frozen fixture ids listed below, ≥30% unmodified; catalog measured 2026-09-01: **17/20 = 85%** (0 modified, 3 failing) | — |

### 5.1 G10 STUB-MODE measurement record — not a G10 result

The scripted stub harness ran end-to-end through production composition. These measurements establish
queue/scheduler behavior under deterministic stub latency; they do **not** establish real-provider
capacity or pass G10.

Evidence: `Temp/G10/g10-stub-manifest.json` and `Temp/G10/TestResults/g10-tests.trx`.

| Arrival pattern | Served | Served fraction | p95 service wait | Cross-actor cancellations | Starvation | Per-actor maximum waits |
|---|---:|---:|---:|---:|---|---|
| Staggered | 40/40 | 1.0 | 109.9214 ms | 0 | none beyond 60 s | 0.1066–0.1144 s |
| Synchronized burst | 40/40 | 1.0 | 1075.1702 ms | 0 | none beyond 60 s | 0.1069–1.0942 s |

Each world run recorded **3,284,440 guarded steps**, **5,280 completed Lua operations**, **420 thread
resumes**, and **1,800 events delivered**. Test discovery recorded **6 discovered, 0 skipped**. Independent
G6 evidence recorded **1,200 intended subscriber writes** and **0 non-subscriber invocations across
22,800 checks**.

The calibrated Lua body is **580 guarded instructions**, derived from observed guard samples of
**576–580**. It is nine instructions below the manifest's measured **589-instruction** frame capacity.

**STUB-MODE status:** this record does not satisfy G10. The real-provider result is recorded below.

### 5.2 G10 REAL-PROVIDER measurement record — failed

Measured 2026-09-01 with 20 actors, a 30 s warm-up, and a 60 s measurement window. Evidence:
`artifacts/g10-real.json`, `artifacts/g10-real.err`, and the frozen configuration in §3. The run recorded
**50 chat responses actually produced by the provider** across both warm-ups and measurement phases.
Test discovery and skip counts were not measured by this process.

| Arrival pattern | Offered | Served | Fraction | p95 end-to-end | p95 queue | p95 provider | Reported provider failures | Same-actor cancellations | Harness deadline cancellations | Cross-actor cancellations | Admission failures |
|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| Staggered | 40 | 10 | 0.25 | 72,009.8 ms | 46,153.1 ms | 38,465.5 ms | 23 | 0 | 11 | 0 | 0 |
| Synchronized burst | 40 | 23 | 0.575 | 52,358.9 ms | 45,093.4 ms | 17,445.0 ms | 9 | 17 | 3 | 0 | 0 |

**Gate verdict: FAILED.** Both patterns fail served fraction, p95 end-to-end latency, and the 60 s
starvation requirement. This is not a pass. With backend parallelism 1 and p95 provider latency of
17.445–38.466 s, forty requests cannot be served inside a 60 s window. The binding capacity constraint
in this run is the AI backend, not CoreAI. Zero cross-actor cancellations and zero admission failures
show that the queue admitted and isolated actors correctly; it had no faster provider to dispatch to.

**The staggered `providerFailures = 23` count is a CoreAI defect, not an LM Studio failure.** LM Studio's
server log for 12:55:27–12:57:56 accepted 53 staggered requests and logged no provider error, HTTP 429
or other concurrency rejection, context overrun, or malformed response. Successful predictions used
643 prompt tokens, far below the 131072-token context cap. No request reached the chosen 120 s request
timeout. Instead, the server recorded 19 client disconnects as the second measurement wave replaced
still-active requests for the same actors, then 4 more active disconnects at the harness starvation
deadline: **19 + 4 = 23**. Seven additional requests were still pending at that deadline, so **4 + 7 =
11** harness-deadline cancellations.

The classification is lost at the CoreAI client/probe boundary. `MeaiLlmClient.FromException` converts
`OperationCanceledException` into an unsuccessful `LlmCompletionResult` with
`ErrorCode = Cancelled`; `G10MeasuredLlmClient` treats every returned unsuccessful result as a
non-cancelled provider failure and only recognizes cancellation when an exception escapes. Thus the
same-actor and deadline cancellations above were serialized as provider failures. That measurement
bug is CoreAI's fault and must be fixed before the failure counters are used again. It does not change
the capacity verdict: the correctly measured served fractions and latencies still fail G10 because of
the single-lane backend.

#### Defensible 20-actor claim: numerical requirements

The offered rate is 40/60 = 0.667 requests/s, and a 95% pass requires 38/60 = 0.633 completions/s.
These are measurement-derived sizing thresholds before transport and scheduling overhead. The lane
estimate assumes the observed p95 is representative per lane and remains stable under parallel load;
a claimed configuration needs additional headroom and a new measurement.

- At the observed 17.445–38.466 s provider p95, throughput alone requires
  `ceil(38 × latency / 60)` = **12–25 backend lanes**, with orchestrator concurrency raised from the
  chosen 4 to at least the lane count. This still cannot pass the 5 s p95 gate because one provider
  response already takes longer than 5 s.
- Keeping four-way orchestration requires a real backend parallelism of 4 and provider p95 **≤1.0 s**
  to put the 19th request of each 20-request synchronized burst through within 5 s. At provider p95
  2.5 s the burst needs at least **10 lanes**; at 5.0 s it needs at least **19 lanes**. With one backend
  lane, the same burst requires provider p95 **≤5/19 = 0.263 s**.
- A workload-only claim on this one-lane model cannot retain G10. The observed latency supports only
  `floor(60 / latency)` = **1–3 total requests/minute**, rather than 40; shared fairly across 20 actors,
  that is roughly one request per actor every **7–20 minutes**, and the latency SLO would have to be
  relaxed to at least the observed 17.445–38.466 s. To retain the 5 s gate, the workload must instead
  reduce per-request generation enough to meet the latency/parallelism bounds above and be remeasured.

**G2 scope.** G2's cross-actor refusal security claim applies only to worlds with an explicit ACL
version. Legacy worlds whose ACL version is missing or `null` remain in compatibility mode: they do
**not** receive cross-actor mutation/destruction refusal and are **not covered by the G2 security
claim**. The strict ACL-versioned-world positive/negative test remains mandatory.

**G12 frozen fixture ids:**

- `TAC-001-instance-parent-last`
- `TAC-002-part-properties`
- `TAC-003-attributes-change-signal`
- `TAC-004-signal-connect-disconnect`
- `TAC-005-signal-once`
- `TAC-006-signal-wait`
- `TAC-007-task-scheduling`
- `TAC-008-runservice-heartbeat-loop`
- `TAC-009-userinput-began`
- `TAC-010-vector3-math`
- `TAC-011-cframe-math`
- `TAC-012-color3-math`
- `TAC-013-getservice-identity`
- `TAC-014-destroy-pcall-cleanup`
- `TAC-015-script-parent-property-signal`
- `TAC-016-generic-for-descendants`
- `TAC-017-waitforchild-yield`
- `TAC-018-contextaction-bind`
- `TAC-019-tween-create`
- `TAC-020-players-localplayer`

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
| Absolute RSS ceiling | **NOT MEASURED** — the 20-actor baseline run has not happened |
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


## 9. The 4 ms gate is arithmetically impossible at batch 4 — measured 2026-08-31

The staircase benchmark could not be built honestly, and that refusal is itself the finding.

**Why no number was produced.** The production path exposes no counters: `LuaCsExecutionGuard._steps`
is private and the scheduler's script thread has no resume telemetry, and its factory cannot be
decorated through production composition. Reading them by reflection would bypass the very path §3b
requires the measurement to travel, so a reflected number would be worthless. **An observability seam
is prerequisite work, not an optimisation.**

**What the arithmetic already settles.** At the measured guarded rate (§8), a 4 ms frame contains
**589 guarded instructions in total**. Split across 20 simultaneous resumes that is **fewer than 30
instructions per actor**, before any scheduler, timer or event overhead. No realistic Lua body fits.
So the 4 ms p95 at 20 actors is not a demanding target — it is an impossible one at the current guard
batch, and any implementation that appeared to pass it would be measuring something else.

**Therefore the guard batch decision is forced by measurement, not preference.** Batch 256 was measured
at 35.1x, which turns 589 instructions per 4 ms frame into roughly 20 600 — about 1 000 per actor at 20
actors, which is workable. The counter-argument for batch 4 is allocation-bomb latency, and the VM-fork
research established that this protection is a process-wide first-growth backstop a determined actor
evades regardless. So batch 4 buys little and costs 35x.

**Not changed yet, deliberately.** The measurement used Unity's bundled x86 Mono CLI. Widening the
production batch on that basis would repeat the invented-number mistake this document exists to
prevent. Sequence: build the observability seam, re-measure on the 64-bit Standalone player, then set
the batch and the frame gate together from that data.

**Reference machine for the run above:** Windows 11 Pro build 26200, AMD engineering sample, 16 logical
processors, 39.3 GiB RAM, RTX 3050 Laptop, Unity 6000.3.14f1, Performance power plan.
