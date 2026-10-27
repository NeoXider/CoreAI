# Actor and tool-binding delta audit

Status: complete static review. One new P2 source-compatibility regression; root owns its repair and executable acceptance. Review baseline: `bc3fd74f0a889bfe1b1e7babc6bc02ed48a7c05e`, branch `demo`, checkout `D:/Git/CoreAI`. The reviewed candidate is the current working-tree delta, not a new commit.

## Review decisions

Before planning: the real goal is to reject regressions introduced by the actor/API/tool-binding repairs while preserving the approved design in `16-actor-fix-design.md`. Applicable invariants are public source and binary signatures/defaults; explicit-provider precedence; discoverable scoped-store and inner-wrapper capabilities; Unknown versus frozen Empty; preserved trusted actor/grants; capture before queue waits and chat enumeration; cancellation attribution; exact actor plus normalized nonempty role and raw/canonical-trim trace correlation; disabled-tool bypass; paired request-local discovery/invocation descriptors; allowlist retention; and live-catalog/captured-invocation behavior. Authentication, account switching, host-game policy and unrelated repair domains are outside scope.

Before delegation: this is an exclusive leaf audit; no further agents, source fixes, builds/tests, Unity/MCP, provider/network calls or Git mutation are authorized. The sole writable path is this report. The user's explicit per-auditor report request overrides `AGENTS.md`'s default no-report rule. `AGENTS.md`, original reports 01-04, and approved design 16 were read; previous done/green statements were treated as claims, not acceptance.

Before accepting: every counted finding needs a valid caller, changed causal line, expected/actual behavior, and a meaningful regression. Every listed diff hunk and both complete new fixtures were read. Adjacent unchanged code was read only far enough to trace the affected joins; this is not an exhaustive module audit.

## Confirmed finding

### DELTA-ACTOR-001 [P2] Remove the new history overload that makes a legacy default-literal call ambiguous

**Changed target:** `Assets/CoreAiUnity/Runtime/Source/Features/Chat/CoreAiChatService.cs:696-698`; the retained legacy overload is at line 666.

**Concrete trigger:** an existing C# host uses `chat.TryGetPersistedChatHistory(role, out ChatMessage[] messages, default)` to request the default history limit. That call is valid both at original `69d5bcc3` and released `bc3fd74f`: the third argument has only the `int maxMessages` target there.

**Expected/actual:** the same consumer should compile after this compatibility repair. The new three-argument overload accepts `ActorContext?` as its third argument, so the default literal now converts to both `int` and `ActorContext?`. Neither target has an implicit conversion to the other; neither overload is better. The consumer therefore gets CS0121 before any runtime history access. Restoring the old exact signature does not prevent this source break when a competing same-arity overload is added.

**Causal path:** lines 696-698 add the competing convenience solely to allow actor-only shorthand. The full actor-aware `(string, out ChatMessage[], int, ActorContext?)` overload already exists at lines 671-672 and can coexist with the legacy method without this ambiguity. The constructor repair correctly uses distinct full arities; this finding does not apply to those constructors.

**Repair direction:** retain legacy history3 and actor history4; remove this invented history3 convenience or give it a distinct name. Migrate the current named `actorContext:`-only callsites in `MultiplayerSessionIntegrationTests.cs:82,83,121,124,160,163,184,187,188` to pass an explicit zero limit before the actor, or to the distinct convenience if chosen. The production panel and documentation compile example already use full arity (`CoreAiChatPanel.cs:1967`, `MultiplayerDocumentationCompileExamples.cs:69`). Root independently accepted this concern during the audit; no source fix was made here.

**Regression:** add the literal consumer call to the compile-visible `HOST001_LegacyHistoryReflectionSignatureStillReturnsHistory` fixture, together with a normal integer limit, the two-argument optional call, an old delegate assignment, and the full four-argument actor call. Compile the real host/test lane; current code should fail compilation specifically on the default literal, repaired code should compile and return the same saved history. Keep the exact three-argument reflected signature assertion. The present fixture only reflects the method and passes boxed integer zero, so it cannot catch overload resolution. This is a static C# finding; this worker did not run a compiler.

## Source checks that passed

- Provider resolution: `ResolveKnownProvider` discovers a direct provider before the optional provider-source capability and rejects inconsistent capability results. Core and queue select explicit providers first. Chat selects its explicit provider, then its supplied memory store's capability; an opaque supplied store deliberately blocks fallback to the inner provider. With no chat store, the inner capability is discoverable. `ScopedAgentMemoryStoreDecorator.GetScope` forwards the normalized role to its existing provider.
- Admission: `CaptureKnownActorScope` preserves a bound actor, preserves and binds a nonempty actor scope, and leaves an empty actor unbound when the provider is Unknown. It does not synthesize an Empty provider. `ActorContext.BindMemoryScope` retains actor, connection, world, role and grants; narrowing retains the binding marker. Queue captures before admission/wait, enters the captured actor/provider for execution and unstarted-turn recording, and pushes no frame for an actorless Unknown request. Chat captures before returning its streaming enumerable and uses the already selected actor for history/clear.
- Public APIs: the old 16-argument orchestrator constructor, 8-argument chat constructor, single-argument component constructor, StopAgent signatures, facade ClearContext signature/defaults and history3 signature/default are present. Their parameter order/names/default expressions match the original baseline where applicable. Extended constructors require full arity, so they do not compete with a legacy optional suffix. The distinct orchestrator factory forwards all dependencies. Current DI uses full arity; the two changed fixtures' former short named-provider calls have been migrated.
- Progress/cancellation: the chat match accepts only the admitted raw trace or that trace's precomputed trimmed alias. It compares exact actor identity and a normalized nonempty event role; it does not trim arbitrary event traces or make an empty event role default to Creator. Request trace metadata is preserved. The changed code leaves the caller/deadline token split, linkage and timeout catch intact.
- Disabled tools: both MEAI completion paths select an empty invocation list before calling role binding or native factories when mode is None. Downstream policy receives that empty list. The eight new tests represent native/text, buffered/streamed and empty/valid-role combinations and assert no extra binding or invocation.
- Paired skills: production calls `BindToolsForRole`, sharing one invocation-owned binding across read/call proxies. Its descriptor cache is keyed by immutable `SkillSet` identity; memory functions are store/role-local and cached within that binding. Read metadata and call resolution use those same descriptors. Restrictions retain the binding and intersect allowlists, while both discovery and invocation still filter denied names. Unbound static read/catalog paths remain unbound. Direct-tool fallback binds memory only after name/allowlist selection.
- Live catalogs: both proxies retain the live `MutableSkillCatalog`, refresh their map/index on version change, and read a catalog snapshot when enumerating. Add/replace publishes a new immutable skill object after validation; removal excludes it from rebuilt maps. The descriptor cache therefore does not retain a removed name in the active map, and replacing a skill uses a new cache key. `ResolvedLlmToolInvocation` still owns its selected descriptor and argument object, so later replacement/removal does not redirect an already resolved call. Cache publication and ordinary descriptor construction are serialized; no additional concrete race or deadlock was established from this delta.

## Test and execution gaps

These gaps are acceptance work, not additional counted defects:

- New manual core/chat tests exercise omitted-provider discovery from a supplied scoped store in buffered and streaming turns, using two nonempty scopes. They do not exercise an omitted-provider queue discovering the inner provider, nor chat with no store discovering it before delayed enumeration. Repeat the existing gated queue/stream scenarios with only the new capability discovery path, including admission at Empty, provider change while waiting, pending cancellation, and retained grants.
- Existing frozen-Empty, queue-wait, concurrent turn, cancellation/failure and delayed-chat tests pass providers explicitly. No scoped-store capability fake/opaque-store fake tests were found for Unknown, supplied opaque chat store precedence, or actorless Unknown's absence of a fake Empty execution frame. Those are new behavior branches and should receive targeted tests through real wrappers, not only direct helper assertions.
- Compatibility tests currently check constructor arities but not optional/default parameter metadata or the full old/new call matrix. The ClearContext case list also covers only the facade case; it does not instantiate the chat ClearContext delegate branch. Add compile-visible calls/delegates and exact optional/default checks in the same host fixture. The confirmed default-literal regression above is the concrete consequence already found.
- The new paired-discovery test runs the real buffered native loop with two independent role/store bindings and checks schemas, effects and the untouched static catalog. It does not exercise paired metadata through the streaming/text loop, live AddOrReplace/Remove while bound, restricted proxies after binding, or resolve-then-replace invocation retention. Existing live-skill tests exercise unbound proxies. Run the same live-catalog/captured-invocation cases on one shared bound tool list and include a counting factory to prove the intended per-binding cache behavior.
- The new progress tests cover raw forwarded trace with started/completed/failed events and foreign actor/role/trace rejection. Existing core-trimmed-alias and caller/deadline regressions remain necessary; add empty-role rejection, whitespace-normalized matching role, and arbitrary padded incoming trace rejection to state the repaired matching contract directly.

## Hypotheses not counted

- A supposedly changing callable function set on the same tool instance is not a demonstrated cache regression: `SkillSetToolResolver` explicitly documents fixed function sets per instance and requires replacement when a set changes. No unsupported host mutation requirement is invented here.
- Arbitrary custom-factory reentry while a descriptor cache monitor is held has not been executed, and no concrete newly failing permitted caller was established. It remains outside the confirmed findings; source locking alone is not called proof of a deadlock.
- Deferred direct-core/queue streaming capture still starts on enumeration; the approved requirement is capture before queue wait and eager chat admission. No stronger direct-core call-time contract is invented.

## Complete changed-hunk ledger

All 79 tracked delta hunks below were read, including their removed code, plus both complete untracked fixtures (632 lines). Ranges are the actual `git diff --unified=0 HEAD -- <path>` old/new headers at audit completion. There are 11 runtime paths, two tracked test paths and two new test paths. These are read-coverage counts, not passing tests or whole-file semantic coverage.

| Path | Hunks | Reviewed old/new ranges |
| --- | ---: | --- |
| `Assets/CoreAI/Runtime/Core/Features/AgentMemory/AgentMemoryScope.cs` | 1/1 | `-50,0 +51,23` |
| `Assets/CoreAI/Runtime/Core/Features/AgentMemory/IAgentMemoryScopeProvider.cs` | 1/1 | `-12,0 +13,7` |
| `Assets/CoreAI/Runtime/Core/Features/AgentMemory/ScopedAgentMemoryStoreDecorator.cs` | 2/2 | `-20 +20; -47,0 +48,7` |
| `Assets/CoreAI/Runtime/Core/Features/Orchestration/AiOrchestrator.cs` | 5/5 | `-23 +23,2; -74,2 +75,26; -95 +120,33; -332 +389; -593 +650` |
| `Assets/CoreAI/Runtime/Core/Features/Orchestration/QueuedAiOrchestrator.cs` | 13/13 | `-17 +17,2; -66 +67; -82,0 +84,7; -179 +187,2; -229 +238,2; -448,3 +458; -532,3 +540; -667,0 +674; -685,0 +693; -1248 +1256,7; -1266 +1280; -1385,3 +1399; -1400,3 +1412` |
| `Assets/CoreAI/Runtime/Core/Features/Llm/CallSkillToolLlmTool.cs` | 14/14 | `-66,4 +66,97; -116,2 +209,2; -146,2 +239; -152 +244; -155,3 +247,2; -184 +275; -190 +281; -192 +283; -217 +308; -241 +332; -358,11 +448,0; -371 +451; -414 +494,2; -417 +498,4` |
| `Assets/CoreAI/Runtime/Core/Features/Llm/ReadSkillLlmTool.cs` | 18/18 | `-30,0 +31,17; -48,0 +66; -74,9 +92,11; -84 +104,2; -94 +115,4; -130 +154; -162,5 +186,10; -190 +219; -196 +225,2; -199 +229; -204 +234,2; -212 +243; -221 +252,4; -247 +281; -263 +297; -272 +306; -423 +457,2; -436 +471,4` |
| `Assets/CoreAiUnity/Runtime/Source/Features/Chat/CoreAiChatService.cs` | 14/14 | `-44,2 +44,16; -55,2 +69,13; -274 +299,2; -310,0 +337; -312,2 +339,3; -617,0 +646,6; -620 +654; -622 +656,2; -631,2 +666,7; -641 +681,2; -644,0 +686; -653,0 +696,11; -655 +708; -670 +723,3` |
| `Assets/CoreAiUnity/Runtime/Source/Api/CoreAi.cs` | 2/2 | `-833 +833,6; -861,2 +866,13` |
| `Assets/CoreAiUnity/Runtime/Source/Features/World/Infrastructure/CoreAiComponentCommandExecutor.cs` | 1/1 | `-15 +15,6` |
| `Assets/CoreAiUnity/Runtime/Source/Features/Llm/Infrastructure/MeaiLlmClient.cs` | 3/3 | `-183 +183,3; -552 +554,3; -3011,9 +3015,2` |
| `Assets/CoreAiUnity/Tests/EditMode/ExecutionAdmissionRegressionTests.cs` | 1/1 | `-369 +369` |
| `Assets/CoreAiUnity/Tests/EditMode/MultiplayerSessionIntegrationTests.cs` | 4/4 | `-56,4 +56,4; -113,2 +113,2; -141,2 +141,2; -181,2 +181,2` |
| `Assets/CoreAiUnity/Tests/EditMode/PrReviewCompositionEditModeTests.cs` | New, full | `+1,375` |
| `Assets/CoreAiUnity/Tests/EditMode/PrReviewToolBindingEditModeTests.cs` | New, full | `+1,257` |

Supporting read-only joins: complete `ActorContext`, scoped memory decorator, execution scope leases, `MutableSkillCatalog`; relevant `SkillSet`, descriptor/resolver/factory contracts, memory declaration/implementation, core task/stream/teardown and queue admission/execution/unstarted recording; chat capture/history/clear/progress/token paths; facade forwarding; full-arity `CorePortableInstaller` construction; original baseline signatures and current history callsites. Historical report coverage is context, not extra ownership credit.

## Verification and completion accounting

- CHANGED: this report only. No runtime, tests, metadata, settings, PLAN, shared tracker or Git index was changed by this worker.
- VERIFY OUTPUT: read-only `git rev-parse HEAD` returned `bc3fd74f0a889bfe1b1e7babc6bc02ed48a7c05e`; `git diff --unified=0` returned the 79 tracked hunks recorded above; the two complete new fixtures contained 375 and 257 lines. `git show 69d5bcc3:<path>` verified the original signatures. Direct report check returned 15 coverage rows, one finding, zero trailing-whitespace lines and zero Cyrillic lines; scoped `git diff --check` exited 0 (the report is untracked, so direct content checks supply its validation). Chat source SHA-256 at completion: `B70FCB17384FA5B9F9631B8307B7F8F62F1042E15E9E041308179F9BD23F097B`. One new P2 finding; no counted P1/P3 or additional runtime finding.
- NOT DONE: compiler reproduction, tests, Unity Editor/player/WebGL/device, runtime/fault/load/performance, model/provider/network, and publication gates. Root's supplied portable Core result is not a Unity host result. The supplied 36-case release baseline was 28 failed / 8 passed; no repaired host green was supplied or inferred. No execution acceptance is claimed by this audit.
- CONCERNS: DELTA-ACTOR-001 requires the convenience overload repair and a compile-visible regression. The other listed gaps require targeted proof before root accepts their respective contracts.
- QUESTIONS: none block root's next action. Runtime remains frozen until root releases its own lease.
