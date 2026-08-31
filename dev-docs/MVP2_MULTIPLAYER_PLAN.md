# MVP2 + multiplayer foundation — plan of record (v6)

Branch `feature/mvp2-multiplayer`, forked from `v7.1.0`.
Five adversarial audit rounds produced 31 findings (21 blocking) against v1–v5. §10 records what each
version got wrong. **Every file:line below was opened and read while writing this version.**

## 1. Scope

**The goal:** 20 concurrent players minimum, 100–200 target, each with their own AI agent that chats
privately, edits and spawns Lua, and mutates the shared world, online, over a Roblox-compatible API.

**This branch:** the single-process foundation plus the MVP2 API, proven against **simulated actors**.
Real online play additionally needs MVP3 (world file), MVP8 (`Players`), MVP11 (Mirror bridge) and
MVP12 (replication).

**Identity is a PORT, not a provider choice (user decision).** MVP11 assumes an authenticator and
provides none, so rather than picking one, CoreAI does what it already does for input, camera and
script threads: it defines the seam and lets the host supply the implementation. `IActorIdentityProvider`
is Domain-defined; the shape it yields is Roblox-shaped (`UserId`, `Name`, session), so mods written
against it port 1:1. Implementations ship separately and none is privileged: a synthetic/local provider
now, a host-supplied signed token for embedders, Mirror auth when MVP11 lands. This follows the
framework principle already in the roadmap — opinions ship as configuration, never hardcoded.

**Version impact: this stays a 7.x minor.** Round 4 established that per-actor chat privacy does *not*
require changing the public `IInGameLlmChatService` contract — the restriction is the singleton DI
registration (`CorePortableInstaller.cs:119`, `Lifetime.Singleton`). An actor-keyed factory delivers
privacy without a source- or binary-breaking change, which would otherwise force 8.0.0 under the
lockstep rule (`CONTRIBUTING.md:79`).

## 2. Ceilings — every locator re-read for this version

| Ceiling | Where (verified) | Effect |
|---|---|---|
| **Shared-world mutation is not ownership-checked** | `RequireWorldEdit` tests only the coarse `CanWorldEdit` bool (`LuaCsRbxInstanceBindings.cs:94`), while ownership already exists in the data: `InstanceRecord.OwnerModId` and `OriginTag` (`mod:` / `console:` / `ai:`) (`InstanceRecord.cs:22`) | actor A can mutate and destroy actor B's objects. The data for an ACL exists; the check does not. |
| Any agent can manage any mod | `LuaModsLlmTool` takes a caller-supplied `mod_id`; `Mutate` gates only on the global `_allowModManagement` (`:37,60,185`) | same hole on the mod path |
| Chat service is a DI singleton | `CorePortableInstaller.cs:119` | one history, one gate, one limiter for everyone |
| Chat limiter fails the MINIMUM target | `InGameLlmChatService`: `SemaphoreSlim(1,1)`, 10 requests / 60 s | 20 actors offer 40/min → ≈75% rejected |
| AI queue 2 active | `AiOrchestrationQueueOptions.cs:8` | 40 req/min needs ≤3 s mean service; a synchronized burst needs ≈≤0.5 s |
| 33rd mod rejected | `LuaCsModRuntime.cs:75` | caps sessions and blocks benchmarking above 32 |
| Events broadcast to every mod | `EmitEvent` iterates `_mods.Values` under `lock (_gate)` (`:841-855`) | O(n²) fan-out plus one serialization point |
| Timers bypass the global budget | `LuaCsModRuntime.cs:891` (`// WHY:` says so) | deliberate; stops working at scale |
| No per-state memory accounting | `IScriptState` has no memory surface; the 256 MB guard is process-wide, per-call, first-growth | cumulative per-actor byte limits are not implementable today |
| **Every Lua resume installs a per-instruction hook** | `LuaCsCoroutineHandle.cs:164`. RAW (unguarded) throughput is 1.27–2.51 M ops/s (`dev-docs/LUA_VM_BENCHMARK_PLAN.md:373`); the GUARDED rate at the production batch of 4 was measured separately at **148–158 k instructions/s** — see the manifest §8 | at the guarded rate one 10 000-instruction resume costs **≈67 ms**, so a 4 ms frame holds ~589 instructions. Frame budgets must be derived from the guarded rate, never from the raw one |
| Actors cancel each other | `CoreAiChatPanel.cs:2670` uses the ROLE id as `CancellationScope` | same-role actors supersede one another |
| Completion polling per-frame linear | `ModScheduler.PromoteCompletedWaits` | 7 passes/frame; quadratic via `RemoveAt` |
| Metrics and audit keyed by role | `InMemoryAiOrchestrationMetrics`, `ToolCallAuditInterceptor` | 200 misbehaving agents are one row |
| No revision/operation id on instances | `InstanceRecord`, `RbxInstance` setters | last-write-wins, no idempotency |

## 3. Decisions

1. **`ActorContext` first**, threaded through AI → chat → tools → scheduler → mod runtime →
   persistence → mutation → audit. Synthetic here; authenticated in Track C.
2. **Per-actor chat privacy via an actor-keyed factory**, not an interface break. Keeps 7.x.
3. **Authorization covers BOTH paths** — mod management *and* every direct Lua world mutation — over
   a NEW ownership dimension, because the existing one is insufficient. Audit round 5 showed
   `OwnerModId` is *teardown* ownership and `OriginTag` is provenance; console objects get only
   `console:session-N` with no owner, host objects encode no actor, and clones inherit the source
   owner. The ACL therefore adds a durable `OwnerActorId` plus an explicit access scope —
   **owned / shared-writable / host-protected** — see §12.
4. **Per-state memory accounting is investigated BEFORE implementation starts (user decision).**
   Audit round 5 established that Lua-CSharp 0.5.6 exposes no per-state memory API, so a hard quota
   would require forking the VM — a reversal of 6.13.1, which retired a local patch precisely to stop
   carrying one. The product's premise is LLM-authored Lua, which cannot be assumed cooperative, so
   "trust the actors" is a different product rather than a downgrade. Phase 0 therefore answers, with
   evidence: is there an existing hook; what would a fork cost; what do the fork-free alternatives
   actually guarantee. **The isolation claim this release may make is decided by that answer, not
   before it.**
5. **Structural quotas + hard emergency ceiling**, gated at `N`/`N+1`.
6. **Durable identity separate from connection identity**, so reconnect cannot fork memory.
7. **Subscription-routed events**, global emit lock off the fan-out path.
8. **Provider capacity is sized, not assumed.** Fair queueing does not create throughput: the model,
   context and output caps, backend concurrency and arrival pattern are frozen in the manifest (§6),
   and provider replication is scheduled work.
9. **Mutation envelope** — actor, target, operation id, expected revision — with its own phase/tests.
10. **Engine-free ports enforced by an asmdef dependency test**, including transitive references.

## 4. Build order

0. **VM memory-accounting feasibility** (blocking; see decision 4). Outcome decides the isolation
   guarantee and whether 100-200 stays in this release.
1. **`ActorContext`** — specified in §11, not left to interpretation — plus the actor-keyed chat
   factory, per-actor metrics, audit and denial reasons.
2. **Authorization** — mod ownership *and* the shared-world ACL across every Lua mutation path.
3. **Capacity + acceptance manifest + 20-actor baseline** (§6). Capacity becomes configurable
   (production default and tested benchmark limit) so higher counts can be measured later.
4. **Quotas, ceiling, routing, admission, provider sizing, memory meter.**
5. **Mutation envelope** with duplicate / stale-revision / concurrent-ordering / reconnect / teardown
   tests.
6. **Scheduler and registry cost** — event-fed completion promotion (thread safety, deterministic
   ordering, teardown races, WebGL single-thread), teardown without rescans.
7. **MVP2 API core** — diagnostics/error formatter → general signal dispatch (removes the
   `SupportsDispatch` split; deferred drain after delayed resumptions, before Heartbeat, per
   R5.4–R5.7) → `WaitForChild` yield, `signal:Wait()`, `Destroying` order → budget/quarantine. Also
   fixes `task.wait` inside signal callbacks.
8. **Services and data** — `ServiceCatalog`; clock surface D9; shared JSON + `HttpService`, deciding
   the JSON and `os.clock()` conflicts explicitly.
9. **Loopback networking** — `INetworkBridge`, `NullNetworkBridge`, `RemoteEvent` /
   `UnreliableRemoteEvent` / `RemoteFunction` honouring delivery, ordering, reliability class, actor
   argument and rate limits as an abstract contract. MTU, real disconnect and wire limits are MVP11/12.
10. **Corpus and closure** — Tier-A ≥30% (§6.4); materials catalog **and its missing acceptance
    criterion**; Model pivot implemented rather than stubbed.

## 5. Frame budget — derived, not invented

v4's "p95 ≤ 4 ms" was arbitrary. The measured constraint is that a guarded Lua resume runs at
1.27–2.51 M ops/s with a per-instruction hook, so **the frame budget is a function of the op count the
workload actually executes**. Therefore:

- The manifest (§6) fixes deterministic Lua bodies with **known op counts per resume**.
- The budget is stated as **ops/frame and ms/frame together**, derived from the measured rate on the
  pinned machine, with the derivation shown.
- Any threshold is published *with its arithmetic*, so a reviewer can check that it is neither
  trivially loose nor impossible.

## 6. The acceptance manifest — checked in, not described

Round 4 demonstrated a concrete cheat for every prose gate: canned chat replies, denying everyone to
"prove" confidentiality, one dummy quota, invoking nobody and asserting zero touches, a constant
memory reading, zero discovered tests, six easy corpus fixtures. Prose gates are therefore replaced by
a **versioned manifest file in the repository**, and the release is judged against it:

- **Machine**: CPU, RAM, GPU, OS, Unity version, power profile, editor-batchmode vs Standalone. Fixed
  *before* measuring.
- **Workload**: per actor — mod count, deterministic Lua bodies with fixed op counts, thread counts,
  due-work distribution, timer and event cadence, subscriber/non-subscriber ratio, chat arrival
  pattern (both staggered and synchronized burst).
- **Provider**: model id, context and output caps, backend concurrency.
- **Counters that must be non-zero**: completed operations, resumes, events delivered, chat responses
  actually produced by the provider — so "did nothing" cannot pass.
- **Positive and negative pairs** for every guarantee: A-owner succeeds *and* A→B is refused; a
  subscriber is invoked *and* a non-subscriber is not; quota `N` succeeds *and* `N+1` is refused with
  actor and reason.
- **Fixed identifiers**: exact corpus fixture ids, expected test counts, zero-skip requirement.
- **Memory**: warm-up duration, RSS ceiling and managed-heap slope, all fixed in advance.
- **WebGL**: an actual browser run, not a static checklist.

## 7. Targets

**Mandatory — 20 actors.** All manifest gates pass. This is the release gate.
**Characterization — 50/100/200.** Measured after step 4 lifts the mod ceiling, published with
derivations; thresholds proposed from data and approved before step 6 optimisation, never inherited.

Every phase ships tests and docs and gets an **independent adversarial QA pass**. Justification: this
session found 22 real defects in fully-green code, and this plan needed five versions because every
audit found blockers its author had missed — including, in round 4, a cheat for every acceptance row.

## 8. The one open question for the user

**Who owns authentication?** MVP11 assumes it; no rung provides it. Track C — and therefore any real
20-player session — cannot start until the source is decided (host-supplied token, Mirror auth, or an
external identity provider). This is policy, not derivable from the repository.

## 9. Out of scope

MVP11 transport, MVP12 replication and `ClientWritePolicy`, MVP13 dedicated server, MVP3 world file,
MVP8 full `Players`. R4.10 native-thread interop stays deferred — the `IScriptEngine` /
`IScriptCoroutine` seam cannot wrap an existing native thread.

## 10. What earlier versions got wrong

**v1** promised 200 online players while excluding transport and identity; called isolation "a wiring
job"; ignored conflict and reconnect; proposed deleting the OOM ceiling; cited a false engine-free
precedent; wrote unjudgeable gates; missed the O(n²) broadcast.

**v2** put the security fix before the identity it needs; assumed MVP11 delivers authentication;
promised cumulative memory budgets with no seam; invented "0 bytes/frame" and "p95 ≤ 2 ms"; wrote an AI
gate that passes while rejecting everything; declared the conflict model without scheduling it; cited
line 891 for a broadcast at 841.

**v3** let any measured result become the standard; misapplied the 1 KB scheduler budget to the whole
frame; scheduled a baseline the 32-mod ceiling makes unrunnable; left the real chat path — which fails
at the *minimum* target — unrepaired; kept an OOM claim structural quotas cannot support.

**v4** wrote gates with a concrete cheat available for every single row; fixed 4/8 ms without deriving
it from the per-instruction hook and measured VM throughput; kept a "cooperative actors" escape hatch
that is a different product rather than a downgrade; scheduled fairness but not provider capacity;
missed that shared-world mutation is unauthorized even though `InstanceRecord` already carries the
owner; and proposed a breaking interface change that would have forced 8.0.0 when DI scoping suffices.


## 11. `ActorContext` — specified, not left to interpretation

Audit round 5 blocked v5 because "phase 1: ActorContext" was one line and two engineers would have
built different things. This is the contract.

**Shape.** Immutable value type in the engine-free Domain assembly.

| Field | Meaning |
|---|---|
| `ActorId` | **durable** identity. Keys memory, quotas, mod ownership, audit. Survives reconnect. |
| `SessionId` | **connection** identity. Keys cancellation only. A new one per connect — never part of a durable key, so reconnect cannot fork memory. |
| `RoleId` | the agent role, as today. Orthogonal to identity; two actors may share a role. |
| `WorldId` | which world the actor acts in. Reserved now, used by MVP12. |
| `Grants` | capability set already modelled by `LuaCapabilities`, narrowed per actor. |

**Invariants.**
1. `ActorId` is never derived from `RoleId`. Conflating them is the current cross-cancellation bug.
2. Only ONE trusted construction point builds an `ActorContext`; nothing downstream may forge or widen
   one. Grants may only narrow as they flow inward.
3. Every entry point that can act on behalf of an actor requires it: chat, tools, mod load/manage,
   scheduler ownership, persistence, mutation, audit.
4. Single-player and existing hosts get a default local actor, so current behaviour is unchanged
   without configuration.

**Source.** `IActorIdentityProvider` (Domain port). Implementations: local/synthetic (this branch),
host-supplied token, Mirror auth (MVP11). None privileged; the port is the contract.

**Phase-1 exit criteria.** An `ActorContext` is present and asserted at every listed entry point; a
same-role/different-actor pair does not cancel one another (extends the existing
`QueuedAiOrchestratorEditModeTests` regression); metrics and audit rows carry `ActorId` rather than
`RoleId`; a forged or widened context is rejected by a test.

## 12. The world ACL — a new dimension, because the existing one is not enough

`OwnerModId` is *teardown* ownership and `OriginTag` is provenance; console objects carry only
`console:session-N`, host objects encode no actor, and clones inherit the source owner. None of that
can authorize.

**Model.** Every instance record gains `OwnerActorId` (nullable = host) and an `AccessScope`:

| Scope | Who may mutate | Who may destroy |
|---|---|---|
| `Owned` | the owning actor | the owning actor |
| `SharedWritable` | any actor with `WorldEdit` | the owner or host only |
| `HostProtected` | any actor with `WorldEdit`, for *property writes only* | nobody but the host |

**Why `HostProtected` exists and is not a loophole.** `workspace.CurrentCamera` is host-owned, and all
three bundled samples write it (`sample_lane_racer`, `sample_tetris3d`, `sample_clicker`). A strict
owner-only rule would break them and the MVP1 acceptance gates that deliberately mutate another mod's
objects. So the rule is: host singletons stay writable, but not destroyable or reparentable.

**Rules that must be decided explicitly, not by default:** reparenting is authorized against BOTH
endpoints; a clone is attributed to the *cloning* actor, not the source's owner; recursive destroy and
`ClearAllChildren` authorize per descendant; reads and exports are authorized separately from writes.

**Migration — corrected after the 124-citation call-site audit.** The v6 sentence "existing content
loads as host-owned `SharedWritable`, preserving today's behaviour exactly" was **wrong**: under the
scope table a non-host actor may mutate but may NOT destroy host-owned `SharedWritable`, so legacy
worlds would silently lose destruction they have always had.

The correct mechanism is an **ACL version on the world**. Legacy worlds (missing the field) enter
compatibility mode and keep today's destroy behaviour untouched; only worlds explicitly ACL-enabled
use the strict table. Never silently weaken all `SharedWritable` objects to obtain compatibility.

**Attribution rules** (from the same audit): new actor objects and clone subtrees are caller-`Owned`;
host singletons are `HostProtected`; host-created ordinary content is `SharedWritable`. Reparenting is
authorized against BOTH endpoints; recursive destruction is preflighted atomically; reads stay
cross-owner under a separate grant while source, revisions, diagnostics and bundle export require
owner/host. Raw reflection, scene transform and low-level bindings stay host-only until native
`GameObject` metadata shares the ACL — production already withholds the low-level bindings.
