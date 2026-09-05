# MVP2.5 online play plan

## 1. Entry condition: MVP1 and MVP2 are verified first

**MVP2.5 does not begin until MVP1 and MVP2 are stable and fully verified.** A green fix-specific
test is not enough, and an earlier pass is invalidated by later production-path changes.

| Gate | Required evidence | Concrete failure |
|---|---|---|
| P1 — full EditMode suite | Every expected test is discovered; failures, skips, and ignored tests are zero; results come from the complete suite, not a filter. | The discovered count differs from the frozen expected count, any test does not pass, or only selected assemblies/fixtures ran. |
| P2 — independent QA | Every independent QA round listed for MVP1/MVP2 is complete, its findings are closed, and a final round ran after the last fix. Reviewers must judge production behavior rather than the implementation author's unit fixtures. | A planned round is missing, the final round predates a fix, a blocker remains open, or the review only repeats the author's test path. |
| P3 — MVP1 acceptance re-check | All five audited defects are now fixed, but the claims are not restored until the original MVP1 acceptance gate is re-run: the four rows previously **PARTIAL** and the one **FALSELY CLAIMED** row each need their success case and original adverse case. | Any of the five is inferred from “fixed,” exercised only through a helper, lacks its adverse case, or is not a full PASS through the running composition. |
| P4 — MVP2 manifest | The complete `MVP2_ACCEPTANCE_MANIFEST.md` passes with its frozen workload, non-zero work counters, production-path rule, and negative twins. | Any prose-only substitute, zero-work pass, direct construction of a component production does not use, changed workload after seeing results, or failed negative twin. |
| P5 — mutation envelope | Verify the MVP2 step-5 envelope through production: server-selected actor, target, operation id, expected revision, accepted mutation, duplicate idempotency, stale-revision refusal, and reconnect behavior. | A duplicate applies twice, a stale write wins, the caller can forge the actor, reconnect forks the operation ledger, or tests bypass production composition. |

P5 is **MVP2 work already in progress**, not MVP2.5 scope. If any entry gate fails, work returns to
MVP1/MVP2 stabilization; the online plan does not absorb the fix.

## 2. Motivation

MVP2 shipped useful single-process foundations: the bridge contract, remotes, loopback, actor quotas,
`ActorContext`, and ACL checks on Lua bindings. It did not ship online play. Actor ids are synthetic,
there is no real identity provider or credential/connection input, disconnect does not drive
`PlayerRemoving`, and current ACL enforcement does not protect entry paths outside the Lua binding
boundary.

MVP3 has not quietly shipped underneath that foundation: the existing code is a partial tree DTO
mapper, not a place package, runtime restart contract, or durable world format.

MVP3 is not an online-only dependency. Without a world file, a scene does not survive a restart and
every game—single-player included—must rebuild its world by script on every run. A runtime place
package is therefore required for **any persistent game**, independently raising MVP3 ahead of
transport work. The same serializer later becomes the late-join snapshot; networking must not create a
second world format.

Real clients also change the threat model. The verified replication semantics are server-owned shared
state with permanent filtering of containers and properties. `LocalPlayer` is nil on the server.
Network ownership is a separate hostile physics channel, while replication focus is
streaming/interest management. The roadmap's current `Open` direct-write forwarding contradicts those
rules and cannot pass as Roblox parity.

Mirror is not installed in CoreAI. NeoxiderTools' optional Mirror adapters are useful references, not
the missing implementation: reflection property sync is reference-only, and the context relay is not
authenticated `RemoteFunction` request/response.

## 3. Dependency order and release split

```text
verified MVP1 + verified MVP2 ─┬─> MVP3 world package ───────────────┐
                              └─> full MVP8 Players/gameplay ─>     │
                                  authenticated MVP11 transport ───> MVP12 replication
```

Authentication is not a hidden fifth rung. It is an admission gate inside the MVP11 release and must
enter through `IActorIdentityProvider`; no connection may create a `Player` or `ActorContext` before
that gate succeeds.

Four rungs in one release is unrealistic. MVP3 is medium scope, each later rung is large, identity is
missing, Mirror is not installed, and MVP12 opens a new security boundary. Calling all of that one
release would repeat the earlier plans' mistake: confidence without judgeable evidence.

The split to ship is one rung per release:

1. **Persistence release — MVP3.** Runtime place package and backups. Valuable to solo games on its
   own.
2. **Gameplay identity model — full MVP8.** Not a `Players`-only shortcut; MVP11 depends on the full
   rung.
3. **Authenticated transport — MVP11.** Real host/client Mirror traffic, identity admission, player
   lifecycle, and remotes. No shared-state replication claim.
4. **Authoritative replication — MVP12.** Filtered state replication and late join using MVP3.

Each release gets its own frozen acceptance manifest and independent adversarial QA. A later rung does
not delay shipping an earlier rung that has passed.

## 4. Build order and acceptance gates

Every row below is a paired gate. The positive path must do non-zero work through production
composition; the negative twin must be observed as rejection, absence, or unchanged canonical state.
A gate fails if its negative case is not executed or if a deliberately invalid fixture would still
pass.

### 4.1 MVP3 — runtime world package

Build one canonical world serializer for runtime save/load and the future join snapshot; package
`world.json`, mod sources, settings, ownership/origin metadata, and versioned manifest; add manual and
pre-mutation automatic backups; flush WebGL persistence.

| Gate | Must succeed | Negative twin / concrete failure |
|---|---|---|
| W3.1 restart round-trip | Save a non-trivial world, terminate its runtime state, load it, and match the golden tree, stable ids, properties, attributes, settings, owners, origins, and mod sources without a rebuild script. | A corrupt or unsupported package must be rejected atomically. The gate fails on partial load, regenerated ids, omitted fields, or any need to reconstruct the scene by script. |
| W3.2 one serializer | Disk load and the exported snapshot entry point decode to the same canonical world payload for a fixture containing every supported field. | A field present through only one path, or a second independent tree mapper, fails the gate. |
| W3.3 clean mod restart | Saved mod sources restart exactly once after the tree is restored. | Coroutines, event connections, or other ephemeral VM state surviving the load—or duplicate mod startup—fails. |
| W3.4 backup safety | A manual slot remains byte-stable; every covered AI mutation creates a timestamped, trigger-labelled autosave before mutation; the ring rotates by its frozen policy. | AI overwrite/delete of a manual slot must be refused with bytes unchanged. An injected backup failure must prevent the mutation; mutation-first ordering fails. |
| W3.5 WebGL durability | In a real browser build, save, call `CoreAiWebGlPersistence.Sync()`, reload the page, and restore the same world. | Reporting success before sync, omitting sync, or losing the world after reload fails. A static checklist does not pass. |

### 4.2 MVP8 — full Players and gameplay services

Ship the complete rung: `Players`/`Player`, values and `leaderstats`, Humanoid basics, fly policy,
touch/raycast/per-body gravity, Debris, TweenService, and CollectionService. A partial `Players` DTO is
not an MVP8 dependency.

| Gate | Must succeed | Negative twin / concrete failure |
|---|---|---|
| P8.1 player contexts and lifecycle | Solo exposes its one synthetic client player; server context returns `LocalPlayer == nil`; add/remove through the production lifecycle seam fires exactly once and all lookup methods agree. | Non-nil server `LocalPlayer`, duplicate events, an unknown removed player remaining discoverable, or a lifecycle test that invokes the signal directly fails. |
| P8.2 Player/Humanoid behavior | Character, health/death, movement fields, damage, `MoveTo`, values, and `leaderstats` run through the real controller adapter at 0.28 m/stud and the 1:1 smoke profile. | Unsupported states must raise their documented loud stub. Silent success, wrong unit conversion, repeated `Died`, or host-controller mutation outside the adapter fails. |
| P8.3 physics services | Real contact drives `Touched`/`TouchEnded`; raycast results convert correctly; mod bodies receive Roblox-space per-body gravity at both scales. | A non-contacting body must not fire touch events, and the host scene's global gravity must remain unchanged. Either violation fails. |
| P8.4 Debris/Tween/Collection | Scheduled destruction, supported-property tweening, completion state, and tag queries/signals all perform non-zero work in production. | An unsupported tween type must fail loudly, an untagged object must remain absent, and canceled/destroyed work must not later report successful completion. |
| P8.5 corpus | The frozen kill-brick, touch-pickup-with-leaderstats, and door-tween fixtures pass, and the fixed Tier-A+B fixture set reaches the roadmap's 60% gate. | Corrupted twins for the named fixtures must fail with the expected diagnostic. Fewer discovered fixtures, substituted easier fixtures, or a corrupted fixture passing fails the gate. |

### 4.3 MVP11 — authenticated Mirror transport

Before opening a socket to gameplay, route connection/credential material through a real
`IActorIdentityProvider` implementation. Then build the CoreAI-specific Mirror bridge for host and
client, wire real player connect/disconnect, implement RemoteEvent channels and correlated
RemoteFunction request/response, and retain the Mirror-free loopback path. NeoxiderTools reflection
sync or context relay does not satisfy this rung.

| Gate | Must succeed | Negative twin / concrete failure |
|---|---|---|
| N11.1 admission | A valid authenticated connection maps to one durable actor, session, `Player`, and server-created `ActorContext` before gameplay access. | Missing, provider-invalid, expired, or replayed credentials must be refused before player creation, chat, mods, remotes, or world access. Anonymous fallback fails. |
| N11.2 identity integrity | Reconnect resumes the same durable actor state according to policy. | Client-supplied actor id, `UserId`, name, role, owner, or capability cannot select authority or access another actor's chat/mod/world state. Acceptance of any forged claim fails. |
| N11.3 real RemoteEvent traffic | Host↔client, targeted, broadcast, and unreliable fixtures move real packets; delivery/order counters are non-zero. | Wrong-context calls, malformed/oversize payloads, and over-rate traffic are refused. Zero packets, loopback delivery, wrong-recipient delivery, or silent truncation fails. |
| N11.4 RemoteFunction correlation | Concurrent requests return to the correct caller and timeout produces the documented Lua error. | A forged, replayed, late, or other-connection response must not complete a request. Cross-correlation or an unbounded wait fails. |
| N11.5 player teardown | Connect fires `PlayerAdded` once; graceful and abrupt disconnect each fire `PlayerRemoving` once, cancel actor work, release quotas/remotes, and leave no ghost player; reconnect then follows the durable policy. | Missing/duplicate removal, surviving private state on another connection, leaked work, or synthetic ids standing in for authenticated identity fails. |
| N11.6 contexts and clock | Server-only APIs reject client context, `LocalPlayer` is nil server-side, and synchronized server time stays within the frozen measured tolerance. | A context violation succeeding, client time masquerading as server time, or a tolerance chosen after the run fails. |
| N11.7 optional transport boundary | With Mirror present, all N11 gates use the Mirror path; with Mirror absent, the complete solo manifest still passes through `NullNetworkBridge`. | A hard Mirror dependency in solo, Mirror types leaking into engine-free assemblies, or an online pass with Mirror packet counters at zero fails. |

### 4.4 MVP12 — filtered, server-authoritative replication

Replicate whitelisted instance containers and properties from the server, bind server-assigned ids to
transport ids, batch dirty state, and reuse MVP3 for late join. All inbound client mutation intents
cross one server-side authentication, rate, ACL, revision, and mutation boundary. The current
Lua-binding-only ACL is insufficient.

| Gate | Must succeed | Negative twin / concrete failure |
|---|---|---|
| R12.1 filtering | A server-created whitelisted tree reaches the intended client with identical stable ids and supported properties. | Filtering is never disabled: filtered containers/properties remain absent while the authoritative filter excludes them, and later deltas must not bypass that filter. Wrong-recipient or filtered data appearing fails. |
| R12.2 canonical authority | Server mutations converge on all intended clients. Under `RobloxParity`, a local client write stays local and is overwritten by server sync; under `Strict`, it is rejected with `NOT_AUTHORITY`. | A direct client write changing canonical server/shared state, surviving reconciliation, or reaching another client fails. `Open` direct-write forwarding cannot pass this gate. |
| R12.3 mutation intents | A permitted client intent is rebound to the authenticated actor, passes central ACL/rate checks, applies once at the expected revision, and emits the authoritative result. | Forged actor/owner/role, unauthorized target/property, stale revision, duplicate operation id, malformed payload, or over-rate intent must leave canonical state unchanged. Any bypass fails. |
| R12.4 late join | A late client loads an MVP3 snapshot, then ordered revisions, and converges while the server continues mutating. Snapshot and disk payloads use the same canonical serializer. | Duplicate/out-of-order/missing deltas must trigger deterministic ignore or resync, never silent divergence. A second snapshot mapper or different ids fails. |
| R12.5 physics boundary | Shared physics remains server-owned for this rung and authoritative transforms replicate outward. | Client transform, velocity, or ownership claims must not change server physics state. Treating property replication as network-ownership authority fails. |
| R12.6 churn and scale | The frozen 100-instance churn fixture and concurrency staircase run with non-zero packets, mutations, filtered items, snapshots, and reconciliations; every published limit passes its pre-frozen CPU, memory, bandwidth, and latency budgets. | Zero-work results, changed workload/budgets after measurement, cross-client leakage, or a claimed client count that did not pass every gate fails. |

## 5. Security boundary once real clients connect

The client sends **intent**, never truth. These values must never be client-authoritative:

- authentication result; durable actor id; `UserId`; session; role; capabilities; ACL owner/scope;
- player roster and connect/disconnect state;
- shared instance existence, ids, revisions, properties, destruction, and operation ledger;
- mod source/version, load state, grants, quotas, scheduler state, and server game logic;
- chat history, memory, rate state, cancellation scope, audit attribution, and another actor's errors;
- world package, backups, autosave ordering, and persistent shared state;
- server time, remote caller identity, response correlation, rate counters, and filtering decisions;
- shared physics state and any later network-ownership grant.

Clients may own local input, camera/UI, and explicitly local-only effects. A client may request a
server mutation, but the server derives its actor from the authenticated connection, revalidates
context/ACL/quota/revision, applies the mutation, and publishes the result. Transport code must not
write the registry or Unity objects around that boundary. Denials name the authenticated actor and
reason without leaking another actor's private state.

## 6. Measure; do not assume

The only current guarded-VM facts are:

- production guard batch 4 measured **148,374–158,240 guarded VM instructions/s**;
- that permits only **589 instructions in a 4 ms frame across all actors**, fewer than 30 each at 20
  simultaneous actors before scheduler, serialization, or networking work;
- batch 256 measured **35.1× faster**;
- those numbers came from Unity's bundled x86 Mono CLI and must be confirmed in the real 64-bit
  Standalone Mono player before changing the guard or freezing an online frame budget.

Therefore no online plan may inherit “4 ms,” “20 players,” or “100–200 players” as a proven capacity.
The batch/memory-safety decision and frame budget are frozen together from measured production-player
data. A release may claim 20 clients only after 20 passes every fixed gate; otherwise it reports the
last passing count and is not called the 20-player milestone.

Each rung's manifest freezes hardware, build, transport, payload mix, actor/mod/thread counts, event
and chat arrival patterns, duration, repeat count, expected tests, and budgets **before** the acceptance
run. Measure at least:

- main-thread CPU per phase and guarded instructions completed—not just frame average;
- managed allocations, heap/RSS slope, per-connection state, and disconnect cleanup;
- real packet count, bytes/client and bytes/server, reliable/unreliable loss and ordering, MTU failure,
  serialization cost, remote round-trip distribution, and rate-limit behavior;
- authentication latency/failure/replay behavior and reconnect recovery;
- snapshot bytes/time, delta backlog, join-during-churn convergence, filtered bytes avoided, and resyncs;
- chat/provider queue capacity under staggered and synchronized arrival; fairness does not create
  provider throughput;
- WebGL in a real browser as a pure client, including persistence sync where applicable.

Publish medians, worst runs, raw counter totals, the exact workload, and the arithmetic deriving every
budget. A result with zero relevant work is a failure, not a fast pass.

## 7. Decisions required from the owner

> **DECIDED 2026-09-04 by the owner.** All five are settled. Build against this block; the numbered
> recommendations below it are kept as the reasoning that produced these answers.
>
> 1. **Authentication** — host-supplied provider plus a Mirror admission adapter. **No anonymous
>    online fallback.** CoreAI owns the admission seam only; it never owns accounts.
> 2. **Mirror packaging** — a **separate optional package**. The solo build keeps no hard Mirror
>    dependency, and NeoxiderTools reflection sync / context relay is not the bridge.
>    **Mirror over NGO, reconfirmed 2026-09-04** after the build plan found Unity Netcode for
>    GameObjects 2.11 already installed and used by the example game. The reason is decision 3, not
>    taste: NGO's route to scale is distributed authority — handing authority to clients — while this
>    project just committed to a server-authoritative model, so NGO's scaling story works against the
>    design at every step. Mirror is also transport-agnostic (Telepathy / KCP / a relay can be swapped
>    under load) and pulls in no paid Unity service, which is what "universal" has to mean here. NGO
>    stays where it is, in the example game; it is not removed and not used by the framework.
>    **The transport is not the binding constraint today** and should not be treated as one: the heap
>    budget already fails at 20 actors in solo, with no network at all, so CoreAI runs out before any
>    stack does.
> 3. **`Open` write policy** — **removed as it stands**, replaced by an explicit host-granted,
>    server-mediated write authority. The owner's intent is that a host can grant a *named* client the
>    right to change the world — co-building, an authoring client, a client driving mods through its
>    own model. That intent is preserved in full; what goes away is the unchecked path. The client
>    sends a request, the server checks the grant, the server applies and replicates: the host decides
>    who may write, the server is still the one that writes. **The host itself holds every right by
>    default** and needs no grant — it *is* the authority; grants exist only to extend that authority
>    to a named client. Today the grant is not checked at all,
>    which is both the exploit surface and the parity break — in Roblox a direct client write simply
>    does not replicate, so a script written against the current forwarding behaves differently there.
>    Direct client-authoritative forwarding is never to be called Roblox parity.
> 4. **Release promise** — hold the **20-client bar** before the scale manifest is frozen. No
>    concurrency number is claimed publicly until the staircase measurement proves it.
> 5. **Physics and streaming scope** — server-owned physics in MVP12; hostile physics authority and
>    streaming / interest management deferred to measured later work.

1. **Authentication owner and credential.** Choose the concrete provider: host-signed token, Mirror
   authenticator, or external identity provider, including reconnect and simultaneous-session policy.
   Recommendation: host-supplied provider plus a Mirror admission adapter, with no anonymous online
   fallback.
2. **Mirror packaging.** Decide whether the dedicated CoreAI Mirror adapter is an optional package or
   an optional dependency/define inside the existing host package. Recommendation: keep solo free of a
   hard Mirror dependency; do not treat NeoxiderTools reflection sync/context relay as the bridge.
3. **`Open` write policy.** Decide whether to remove it or replace it with a separately named,
   server-mediated collaborative-build intent policy. Recommendation: remove direct forwarding from
   MVP12 and schedule validated co-building later; never call direct forwarding Roblox parity.
4. **Release promise.** Decide whether the product promise is “first authenticated host/client” or a
   hard 20-client milestone before the scale manifest is frozen. Recommendation: ship the functional
   rungs separately and make no concurrency claim that the staircase has not proved.
5. **Physics and streaming scope.** Decide whether network ownership or replication focus is required
   now. Recommendation: keep server-owned physics in MVP12 and defer both hostile physics authority
   and streaming/interest management to measured later work.

## 8. MVP2.5 will not deliver

- one monolithic release containing all four rungs;
- the MVP2 mutation envelope—it is an entry precondition;
- dedicated/headless server topology (MVP13), matchmaking, lobby, relay/NAT, or cross-server state;
- RBXL import/export (MVP4) or DataStoreService (MVP9);
- a universal built-in account system or identity provider before decision 1;
- direct client-authoritative shared writes or the current `Open` forwarding semantics;
- per-player/per-property partial authority, collaborative undo, or conflict-free multi-writer editing;
- client network ownership, prediction/reconciliation of hostile physics, or trust in client transforms;
- replication focus, world streaming, or an unlimited-world scale claim;
- automatic reflection replication or reuse of the NeoxiderTools context relay as RemoteFunction/auth;
- a 100–200 player claim, or even a 20-player claim, without the fixed production measurement gate;
- WebGL hosting; WebGL remains solo or a pure client.
