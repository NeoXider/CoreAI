# Independent adversarial review of selected audit claims

Baseline: `69d5bcc37502cd4bb464a3bf9ac0c53b1624b804`. Date: 2026-10-23. Reviewer: native gpt-6.1-sol, max reasoning effort, no delegation.

Completed independent source review: 14 selected claims; 13 ACCEPT, one REVISE, zero REJECT. All eight P1 claims present at intake were examined: seven accepted and MOD-003 revised to a narrower P2 configuration limitation. No selected report claims a P0. Source confirmation is not runtime reproduction or a security certification.

This pass wrote only this report. It did not edit the reports under review, host report04, production source, assets or git index. Root assigned this reviewer sequential recovery ownership of report10 after this review because its CLI owner failed twice. NET-001 below was independently reviewed from that owner's existing draft; any later additional NET findings authored by this reviewer in report10 are not independently reviewed by this document.

## Review contract and material read

The goal is to stop unsupported security/correctness conclusions entering the final audit. Each selected claim was checked against actual source, guards, reachable composition, required trust/capability conditions and failure/commit boundaries. ACCEPT means the stated source defect survives those checks. REVISE preserves a narrower real issue but corrects scope, severity or evidence. REJECT would require a verified counterexample/guard that defeats the stated defect.

Reports read for their findings, supporting call chains, existing safeguards and verification limitations: `03-core-providers-streaming.md`, `05-mods-runtime-sandbox.md`, `06-mods-rbx-api.md`, `07-mods-luau-downlevel.md`, `09-mcp.md` and the original partial `10-mirror-network.md`. Detailed independent source tracing covers every P1 and one P2 per report: LLM-001/002/005, MOD-001/002/003/009, RBX-001/006/008, LUAU-001/002, MCP-003 and NET-001. Other P2/P3 findings inform overlap/coverage context; they are not endorsed merely because their containing report is listed.

## P1 adjudication

### LLM-001 - ACCEPT: non-streaming auth refresh can replay completed local effects

The production server-managed factory constructs `RefreshOnUnauthorizedDecorator` at `Assets/CoreAiUnity/Runtime/Source/Composition/LlmPipelineInstaller.cs:385-392`. Its `CompleteAsync` at `Assets/CoreAiUnity/Runtime/Source/Features/Llm/Infrastructure/RefreshOnUnauthorizedDecorator.cs:70-82` checks only failed AuthExpired and refresh success before calling the inner completion again. It never consults executed receipts.

This is reachable with built-in clients, not only a replacement fake: `ServerManagedLlmClient.cs:47-52` constructs `MeaiLlmClient`; `MeaiLlmClient.cs:200-204` constructs a fresh native tool-loop client and `:240-245` converts a later provider exception while retaining its tool traces. `Assets/CoreAI/Runtime/Core/Features/Llm/SmartToolCallingChatClient.cs:350-353` creates a new `ToolExecutionPolicy` per invocation. Its duplicate-signature set is an instance field (`ToolExecutionPolicy.cs:102`), so successful-call suppression from the first invocation does not protect the replay. An HTTP idempotency header is not a local effect ledger.

Required conditions remain the report's: provider-backed server-managed mode, a non-streaming tool-enabled turn, at least one completed effect followed by AuthExpired, and an installed refresher that succeeds. A failure before effects is safely retryable; ordinary built-in streaming has additional commitment accounting and is not the confirmed trigger. P1 is supportable for duplicated purchases/appends/spawns. No duplicate effect was executed here.

### LLM-002 - ACCEPT: the default failure-to-stream conversion removes replay protection

At `Assets/CoreAI/Runtime/Core/Features/Orchestration/ILlmClient.cs:955-966`, the failed completion becomes a terminal chunk without `ExecutedToolCalls`. The success branch copies receipts at `:981`. `Assets/CoreAI/Runtime/Core/Features/Llm/RetryingStreamingLlmClientDecorator.cs:192-210` admits a transient pre-commit retry; its commitment predicate at `:344-347` recognizes text or executed traces. Therefore a real trace omitted by the adapter becomes indistinguishable from no effect to this consumer. The fallback receipt guard cannot recover metadata already discarded upstream.

The report correctly narrows this to completion-only replacement clients that use the default interface adapter. Built-in MEAI streaming overrides it, so this is a broken replacement contract, not proof every built-in stream replays. The supplied failure must actually contain a receipt for an invoked effect; a merely rejected/uninvoked tool receipt needs separate accounting. Accept P1 because a supported composition loses the safety field that retry/fallback explicitly requires.

### MOD-001 - ACCEPT: Read registers shared version mutations without a write grant

`Assets/CoreAIMods/Runtime/LuaExecution/LuaCapabilities.cs:15` and `Assets/CoreAI/Docs/LUA_ACCESS_MODES.md:17` promise no game side effects for Read. The effective capability intersection is real, but `Assets/CoreAIMods/Runtime/Infrastructure/LuaCsGameplayBindings.cs:204-213` registers the whole version surface inside that Read block. `LuaCsVersioningRuntimeBindings.cs:53-73,92-108,120-160` exposes resets, seed-overwrite and overlay apply without actor/key authorization. The apply records state and publishes its command; the class accepts no actor or owner identity through which a later check could rescue the boundary.

Preconditions: the host includes non-null shared version stores or an effective overlay sink and admits the script at Read. A Null store can make individual mutations inert; it does not make the composed shared-store path safe. Mod-local `store_set` and the universally available mod runtime APIs are a distinct documented surface and are excluded. Accept P1 as privilege/capability escalation into shared persistent state, not a VM escape or arbitrary filesystem write.

### MOD-002 - ACCEPT: overlay persistence failure is erased before success publication

`Assets/CoreAiUnity/Runtime/Source/Features/Lua/Infrastructure/FileDataOverlayVersionStore.cs:64-69` mutates `_memory` first; `:214-221` ignores `CoreAiWebGlPersistence.Sync()`'s false result and catches save exceptions without propagating failure. The caller at `Assets/CoreAIMods/Runtime/Infrastructure/LuaCsVersioningRuntimeBindings.cs:106-107` then publishes the apply command. `Assets/CoreAI/Runtime/Core/Features/RuntimeVersioning/MemoryDataOverlayVersionStore.cs:97-100` deduplicates an identical payload against the already advanced memory.

Atomic replacement preserves the previous file on disk; it does not roll back memory or prevent the false-success event. An identical retry after storage recovery remains a no-op, so the report's retry-poisoning consequence follows. The issue requires the file-backed host and a real disk failure or browser persistence refusal. Memory-only hosts that intentionally offer no durability are outside the trigger. Accept P1 for accepted edits silently lost on restart under the promised persistence path. This is distinct from the existing cross-instance overlay lost-update race and host04's world-snapshot receipt defect.

### MOD-003 - REVISE: no-Rbx startup lacks the configurable guard seam, but its documented cap is not bypassed

The reported source branch is accurate: `Assets/CoreAIMods/Runtime/LuaExecution/LuaCsModRuntime.cs:2021-2028` supplies no explicit guard when Rbx is absent. `LuaCsScriptEngine.cs:140-150,176-187` passes/substitutes a one-shot guard, and `LuaCsSecureEnvironment.cs:739-741` preserves a finite default guard. The runtime constructs the requested handler guard at `LuaCsModRuntime.cs:869-871` but does not use it on this branch.

The material counterevidence is the public options contract at `Assets/CoreAIMods/Runtime/Infrastructure/LuaCsModRuntimeFactory.cs:174-187`: both HandlerTimeoutMs and HandlerMaxSteps explicitly cover main-chunk resumes only **with RbxApi set**. The report describes those fields as a promised no-Rbx startup limit, omitting that qualifier. A 1,000-step HandlerMaxSteps setting therefore does not establish violation of a documented 1,000-step no-Rbx main-chunk budget. Default one-shot time/step/allocation protection remains present.

Recommended disposition: retain a P2 configuration/composability issue: a host that omits the optional Rbx block cannot configure the main-chunk budget through the same options, and a large one-shot default may be unsuitable. Add an explicit main-chunk budget/engine seam or define and document one consistent policy; test that policy. Do not call it unlimited execution or a demonstrated P1 security-boundary bypass unless a separate normative public contract is produced. The initial draft of this independent review accepted the source trace; discovery of the qualifier changed the final judgment, and root was notified promptly.

### RBX-001 - ACCEPT: range conversion squares the requested displacement

`Assets/CoreAIMods/Runtime/RbxApi/Binding/UnityRbxPhysicsPort.cs:89-114` multiplies `DirectionToUnity(directionStuds)` by `LengthToUnity(directionStuds.Magnitude)`, derives magnitude, then sends it as maximum distance. `Assets/CoreAIMods/Runtime/RbxApi/Unity/RbxSpace.cs:160-163` mirrors axes without normalization, so the resulting length is L squared times metres-per-stud. The normalized direction passed to Physics does not undo the erroneous distance.

The Domain checks at `Assets/CoreAIMods/Runtime/RbxApi/Instances/RbxWorldPhysics.cs:191-213` reject zero/nonfinite/over-15,000 input, but do not recheck the converted endpoint; eligible out-of-range hits are accepted afterward. Accept the correctness defect and P1 prioritization for a pervasive built-player physics adapter. This is not an actor authorization bypass. The actual collider hit and device query cost remain unexecuted; the arithmetic does not prove a performance number or crash.

### RBX-008 - ACCEPT: outbound byte limits are checked after amplified allocation

`Assets/CoreAIMods/Runtime/Scripting/LuaCs/LuaCsRbxNetworkCodec.cs:325-340` builds the graph, renders the whole JSON string and allocates its UTF-8 byte array before enforcing MaxPayloadBytes. `:1078-1086` uses an unrestricted StringWriter. String values are copied into JValues without a shared byte budget at `:469-484`. The 100,000-entry and depth/cycle guards are real, but the report's 4,096 references to one 32-KiB string satisfy them while expanding to more than 128 MiB of serialized content.

`LuaCsRbxApiBindings.cs:2123-2128` encodes before passing the payload to the bridge. `LuaCsRbxValues.cs:82-90` executes the host callback synchronously, so a VM instruction hook or later transport byte cap cannot interrupt the intermediate host allocation. Accept P1 for admitted-script resource amplification before the advertised 64-KiB quota. This is not an incoming raw-packet length bypass: the decoder's early byte cap is a separate correct guard. No oversized graph, allocation benchmark or OOM experiment was run here.

### LUAU-001 - ACCEPT: nested repeat lowering amplifies source before the VM guard

`Assets/CoreAIMods/Runtime/LuauDownlevel/LuauRewriteParser.cs:778-794` renders the already rewritten repeat condition, inserts that complete string at each continue and leaves the original condition. `:318-345` materializes the nested edits. A condition containing a rewritten function/repeat therefore appears both in the copied continue and the original condition; nesting creates multiplicative output growth.

The recursion ceiling at `:188-195` counts source parser levels, not output bytes or rewrite work. The relevant entry counters are statement parsing (`:394-405`) and expression parsing (`:1129-1140`); the small-input nested-function family can remain below the ceiling while requesting exponentially expanded text. `Assets/CoreAIMods/Runtime/LuaExecution/LuauSourceGate.cs:38-47` runs synchronous Process without an output/work budget. Mod ingestion at `LuaCsModRuntime.cs:1999` reaches it before VM execution at `:2023`; the one-shot environment also loads source before invoking its guard.

Accept P1 for script-input amplification before VM protection, conditional on the host accepting code through these supported paths. It is not an unauthenticated network exploit by itself, and no measured giant output or timing is inferred from the original auditor's arithmetic probe. A simple input-size limit alone would not bound this linear-input/exponential-output family.

## Selected P2 adjudication

| Report/claim | Decision | Independent source/guard result and preconditions |
|---|---|---|
| 03 / LLM-005 | ACCEPT | `LlmClientRegistry.cs:196,208-217,264-269` uses one release flag per reusable handle. Two overlapping admissions on that handle increment twice but only the first completion decrements. Disposing an unused handle can decrement before admission. `:1412-1433` drains while count is positive, so both delayed release and premature release follow. Requires reuse/concurrency or explicit disposal of the returned handle; ordinary fresh-per-call routing is not proof of failure. No one-shot contract is enforced by ResolveClientForRole (`:430-443`). |
| 05 / MOD-009 | ACCEPT | `LuaModsLlmTool.cs:224-255` passes cancellation only to an optional backup gate. `:286-317` directly dispatches the authorized synchronous mutation when that gate is absent. The authorization/management permission guards remain; an already cancelled token has no effect on this admitted standalone branch. Accept the cancelled-call side effect, not arbitrary preemption of a synchronous load already underway. |
| 06 / RBX-006 | ACCEPT | `RbxRemotes.cs:19,31` creates RemoteEvent signals outside Instance's owned table. `RbxInstance.cs:753,1145-1158` disconnects only its base/attribute tables. `LuaCsRbxApiBindings.cs:2545-2570` unregisters RemoteFunction callbacks/part state without repairing RemoteEvent signals. `ModConnectionRegistry.cs:206-220` retains still-Connected entries. Direct or ancestor destruction therefore does not satisfy connection teardown. The original report correctly excludes continued ordinary packet delivery after registry removal. |
| 07 / LUAU-002 | ACCEPT | `LuauRewriteParser.cs:778-786` copies the condition into the continue site's nested lexical block. An inner `local n=99` binds the inserted condition instead of the original outer n. The original repeat-until condition remains outside that inner block. The supplied outer-counter trigger therefore has different control flow. This is an independent semantic/hygiene defect in the same lowering as LUAU-001, not a duplicate allocation claim. No reference VM was executed here. |
| 09 / MCP-003 | ACCEPT | `CoreAiMcpServer.cs:602-619` creates a persistent host when `_active` is null; `:362-372,623-625` stops and clears that reference without destroying/reusing the host. Its normal OnDisable only stops again (`:193-196`); no owned-host cleanup exists. `_runningCalls` remains per old component. Requires use of the static stop/start route; same-component restart tests do not cover it. Capacity escape additionally requires an already running body that ignores cancellation. No listener or persistent Unity object was created here. |
| Original10 / NET-001 | ACCEPT | `MirrorNetworkBridge.cs:940-953` calls FailPendingFor before PeerDisconnected. `:2692` invokes a completion without containment. A throw skips the world-disconnect notification, but finally releases the connection; BindingReleased (`:2660`) causes `CoreAiMirrorSessionHost.cs:255-265` to drop identity/session without invoking the world. The latter is on the skipped path (`:226-236,288-311`). Provider containment (`CoreAiMirrorNetworkBridgeProvider.cs:677-682`) logs too late to restore it. Requires an admitted connection and a throwing composition callback; this is failure isolation, not a demonstrated remote privilege escalation. |

## Deduplication and guard boundaries

- LLM-001 and LLM-002 violate the same no-replay-after-effects invariant but have independent roots: the auth wrapper ignores receipts versus the default adapter discarding them. Fixing either leaves the other reachable. Keep separate subitems in one replay-safety workstream.
- MOD-001 is a missing capability/actor scope on legacy versioning APIs. The reported RBX-007 explicit-envelope tween target issue is a different binding path and should not be merged solely because both concern write authority. This review does not independently adjudicate RBX-007.
- MOD-002 and host04 UH-03 both erase persistence failure, but they affect separate stores/contracts. The former also poisons same-payload retry through staged memory. Existing peer-store lost updates are another root cause; do not count the same atomic-write fixture as proof of three independent fixes.
- LUAU-001, RBX-008 and revised MOD-003 concern different phases: source transformation, synchronous native serialization and VM execution configuration. A VM instruction/memory hook does not prove bounds for the first two. Early incoming byte checks do not protect outbound encoding.
- LUAU-001 and LUAU-002 share the repeat-lowering implementation, so one coherent redesign can fix both, but the acceptance checks differ: bounded expansion versus lexical equivalence. Keep both regression obligations.
- RBX-006 destroys a remote's signal lifetime; original NET-001 skips actor/world disconnection after a host callback exception. They can both leave connections/resources, but one is object teardown and the other transport/session teardown. Neither is evidence of live packet delivery to a destroyed object.
- LLM-005 and MCP-003 both concern admission lifetime accounting, but one is per-handle invocation ownership and the other abandoned Unity host ownership. Neither duplicates host04's stale facade cache or audit diagnostic queue.

## Coverage limitations and actual checks

Actual work: read the detailed report bodies for selected findings and surrounding safeguards/limits; read the source ranges and immediate callers cited above; targeted symbol/call-site searches; inspect the no-Rbx options contract which changes MOD-003's disposition; persist and reread this Markdown; run report-only whitespace/content checks. No original auditor's claimed executable probe was rerun. No Node, C#, .NET, Unity, player, browser, network/listener, model, load/fault or performance test was executed by this reviewer.

The source reads were targeted, not a fresh whole-package audit. Third-party VM/Unity/transport implementations were not reviewed. Report03's other P2s, report05's other P2s, report06's other P2s, report07's remaining P2/P3s, report09's other P2/P3s and any later report10 additions need their own acceptance/verification if promoted. Existing source tests and report arithmetic/probes do not establish an executed player gate. Root has separately reported passing central suites and Node harnesses; those receipts belong to the root handoff, not execution performed by this reviewer, and they do not exercise the new regression cases above.

One shell source-read command was delayed by the slow host and later completed. A connected file-read fallback was unavailable due a disconnected tunnel; it supplied no evidence. All final source judgments use the read-only local shell output. No service recovery or tool installation was attempted.

Final report check receipts are recorded by the handoff: `git diff --check -- dev-docs/audits/2026-10-23/15-independent-security-review.md` plus a direct trailing-whitespace/English/fence scan, because Git does not inspect untracked report contents through the ordinary diff command alone.
