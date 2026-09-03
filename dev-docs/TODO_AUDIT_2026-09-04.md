# TODO audit 2026-09-04 — 131 unchecked `- [ ]` + 5 `[~]` items vs code

Method: each item checked with grep/read against `Assets/`, `CHANGELOG.md` (core + host),
`dev-docs/`. Buckets: DONE (code does it — fix TODO.md), OPEN (unfinished + next step),
DEFERRED (real but blocked/needs decision), STALE (plan/code gone — say what replaced it).

Meta-findings (not items, but affect the whole file):
- TODO.md header (TODO.md:5) says "Released: 7.3.1 (2026-09-02)". Both CHANGELOGs show
  **7.5.0 shipped 2026-09-03** (7.3.x numbers were taken by a chat-tape branch; the work is inside
  7.5.0). The header is stale.
- Six ladder/roadmap items are DONE in code but still unchecked (scheduler #4, GetService #5,
  log-service #8, highlighting #9, networking #10, corpus #11) — see rows.

## Table (Item | Bucket | Evidence file:line | Next step)

### MVP2.5 persistence release (TODO.md:60-110)
| Item | Bucket | Evidence | Next step |
|---|---|---|---|
| Rung-zero host restore envelope (:60) | OPEN | No `nvelope` match in `Assets/CoreAIMods/Runtime/Infrastructure/RbxWorldPackageSerializer.cs` or `Assets/CoreAIMods/Runtime/RbxApi/Binding/RbxWorldHost.cs` | Add host scope around restore writes + RED test restoring ACL-versioned package through production composition |
| Demo scene fixes: orphan ChatPromptButtonsController, WaveAutoBattler no-op binder (:77) | OPEN | `CoreAiTokenBudgetOverlay` still referenced in `Assets/CoreAI.Demos/LiveMechanicsMods/LiveMechanicsModsChatDemo.unity`, `MiniRpg/MiniRpgModsDemo.unity` | Open the 3 scenes in Editor, enable chat example menu or delete controller, fix no-op, save |
| IMGUI migration backlog, 13-file allowlist (:80) | OPEN | `Assets/CoreAIMods/Tests/EditMode/ImguiBanRatchetEditModeTests.cs` still populated (`:27-129`) | Migrate one file at a time, delete its entry (ratchet enforces shrink) |
| Incremental JSON/ZIP encode on WebGL (:82) | OPEN | No streaming-codec code found; budget text uncontradicted | Design chunked codec or raise/document budget; touch world-package serializer + persistence |
| Hostile-reader quotas/canonicality (:84) | OPEN | No preorder/sorted-canonicality or token-quota code found | Add token materialization quota + canonicality enforcement in package reader |
| ConfigureAwait(false) allowlist, ~19 files (:92) | OPEN | `Assets/CoreAiUnity/Tests/EditMode/WebGlUnsafeAsyncPrimitivesEditModeTests.cs:97-200` — all entries present (Inventory/Memory/GameConfig/CallSkill/Wait + memory contour + decorators/orchestrator) | Convert per entry to host-context awaits + `MeaiToolTaskBridge.Publish` for tool bodies, drop entry |
| Browser-gate harness keep-alive (:97) | OPEN | Procedural (G11 record); no visibility/frame-rate check in repo | Add visibility + frame-rate pre-check to G11 protocol |
| Weak tests: symmetric oracle, once-per-load, At1To1 (:101) | OPEN | Tests exist: `Assets/CoreAIMods/Tests/EditMode/RbxApi/Acceptance/Mvp1ConversionLintEditModeTests.cs`, `.../LuaBindings/RbxApiLuaBindingsEditModeTests.cs` | Rewrite with independent oracle / reload twin / negative twin |
| Final QA verdict umbrella (:107) | OPEN | Admission `MaxPending=64` static (`Assets/CoreAI/Runtime/Core/Features/Orchestration/AiOrchestrationQueueOptions.cs:18`); envelope + heap rows above | Tracker: closes when G10 admission, heap-slope, envelope rows close |

### MVP1/MVP2 scheduler wave (:147-246)
| Item | Bucket | Evidence | Next step |
|---|---|---|---|
| Top-level task.wait own rung (:147) | DEFERRED | Deliberate design split by text; blast radius (20 files call `LoadMod(`) uncontradicted | Unblocks with a dedicated design pass + acceptance criteria, not a bolt-on |
| Deferred drain points R5.4–R5.7 (:155) | OPEN | `PipelineStage.DrainDeferred/DrainSignals` exist (`ModScheduler.cs:35,40,310-317`); the extra inter-point drain unverified | Diff stage table vs R4.8/R5.4 spec, add drain + test |
| PlayMode live gate blocked on LLM backend (:212) | DEFERRED | External env (LM Studio model), not code | Load model, point `COREAI_TEST_MODEL` at it, re-run PlayMode |
| Play-test both samples (:245) | OPEN | Manual, no artefact | Play Lane Racer + Tetris in Play Mode after axis/collision changes |

### Verification gates (:264, :277, :306, :1122)
| Item | Bucket | Evidence | Next step |
|---|---|---|---|
| Reasoning fixtures gate (:264) | DONE | Superseded: full EditMode 3256/3247/0/9 (`g9.xml`) + browser gate 2026-09-02 | Uncheck in TODO.md |
| AgentBuilder fixture gate (:277) | DONE | Same later green full suites | Uncheck |
| UXML bubble gate (:306) | DONE | 7.0.1 shipped; later suites green | Uncheck |
| 5.7.0 full-suite gate (:1122) | DONE | Superseded by 7.x full runs (batchmode `editmode8.xml`, `g9.xml`, `g11.xml`) | Uncheck |

### Post-7.0.0 audit fix wave (:372-399)
| Item | Bucket | Evidence | Next step |
|---|---|---|---|
| Comment-convention cleanup (:372) | OPEN | Counts unrecounted; `tools/strip_non_doc_comments.py` caveat stands | Fix/replace strip script, run wave per package |
| SemaphoreSlim.Wait reentrancy (:379) | OPEN | Stores named still sync-gated; no reentrancy guard found | Decide guard vs document; touch 5 stores + `ISkillStore` |
| WebGL Lua↔C# bridge canary (:383) | OPEN | `LuaCsExecutionGuard.cs`, `LuaCsSecureEnvironment.cs`, `LuaCsCoroutineHandle.cs` sync-resume reliance uncontradicted | Add WebGL-build canary test |
| Summary/token-store persistence decision (:387) | DEFERRED | `FileConversationSummaryStore.cs:311` uses `File.Replace`; DI gating `CoreAILifetimeScope.cs:381-417` per text | Decide won't-fix (document) vs injectable flush sink |
| Editor tooling zero coverage (:391) | OPEN | Uncontradicted | Add EditMode coverage for scene creators, module manager, importers |
| Untested MCP tools + Hub UI (:394) | OPEN | `GetModLogsMcpTool` live (`GetModLogsLlmTool.cs` + installer wiring); coverage absent | Cover `GetModLogs/ManageMods/WorldCommand` tools, screenshot source, Hub pages |
| Vision wire-format live (:397) | OPEN | `VisionSelfProbe.cs`, `HubSettingsPage.cs:1381` probe paths exist; no live evidence | One live probe per backend |

### Deleted-audit residue — architecture (:411-434)
| Item | Bucket | Evidence | Next step |
|---|---|---|---|
| ChatPanel god-view (:411) | OPEN | `Assets/CoreAiUnity/Runtime/Source/Features/Chat/CoreAiChatPanel.cs` — **4541 lines** (grew past 4185) | Extract transcript renderer / routing controller / input gate |
| Service-locator leak M1 (:415) | OPEN | Pattern uncontradicted (7 sites listed) | Introduce `CoreAiSceneScopeLocator` |
| Oversized adapters M2 (:421) | OPEN | `.../Llm/Infrastructure/MeaiLlmClient.cs` still **2790 lines**; `LuaCsModRuntime.cs` **3350 lines** (grew past 2194) | Extract endpoint factory / tool-call extractor; persistence coordinator / export bridge |
| Stale MoonSharp docs L2 (:424) | OPEN | `Assets/CoreAIMods/Runtime/LuaExecution/LuaCsModRuntime.cs:34-35` still "both VMs coexist" | Rewrite to single-VM reality (+ `LuaModsLlmTool.cs:91`) |
| Ambient mutable statics L3 (:427) | OPEN | Uncontradicted | Constrain reads to injected deps |
| Copy-paste XML doc L4 (:431) | OPEN | Trivial; site named | One-line doc fix |
| Stale RobloxApi csproj L5 (:433) | OPEN | `CoreAI.RobloxApi.{Binding,Datatypes,Instances,Unity}.csproj` exist at root; asmdefs are now `CoreAI.RbxApi.*` | Delete/gitignore |

### Correctness (:438-461)
| Item | Bucket | Evidence | Next step |
|---|---|---|---|
| Connection registry prune (:438) | OPEN | `ModConnectionRegistry.cs:41-169` compacts only in `DisconnectOwnedBy`; `RbxScriptConnection`/`RbxScriptSignal` hold no registry refs | Notify registry on Disconnect/:Once or compact `!Connected` |
| Registry threading invariant (:442) | OPEN | Uncontradicted | Assert main-thread (dev) or lock; verify manage_mods marshals |
| Bare catch blocks (:446) | OPEN | `AiOrchestrator.cs` has 21 `catch` sites (e.g. `:1159`, `:2060`); bare form to re-verify per site | Distinguish cancel vs fault; short-circuit outer cancel |
| CoreAISettings lock asymmetry (:449) | OPEN | Uncontradicted | Lock readers or explicitly accept |
| IMGUI overlay in demo scenes (:452) | OPEN | Overlay refs in `LiveMechanicsModsChatDemo.unity`, `MiniRpgModsDemo.unity` (+ AutoSave copies) | Remove refs; UITK replacement exists |
| Test quality leftovers (:455) | OPEN | Uncontradicted | Fix `.Result` sites, 3-arg overload leak, mangled comments |
| Hub duplication (:459) | OPEN | Twins at `Assets/CoreAIHub/Runtime/HubSettingsPage.cs:1872,1915`; `SetPlaceholder` callers `:255-267` | Dedupe; replace polling placeholders |

### Perf (:465-489)
| Item | Bucket | Evidence | Next step |
|---|---|---|---|
| Boxing guard seam (:465) | OPEN | `LuaCsModRuntime.cs:1870,1918,1926` — still `params object[]` | Void overload + scratch array; reuse dispatch scratch buffer |
| DynamicInvoke per call (:471) | OPEN | `LuaCsApiRegistry.cs:225` `callback.DynamicInvoke(args)` | Direct-delegate / compiled dispatch |
| Per-read dispatch probes (:473) | OPEN | Uncontradicted | Class-kind bitmask at wrap time |
| Double alloc per crossing (:476) | OPEN | Uncontradicted | Generic `LuaCsRbxValueBox<T>` |
| Adaptive hook batch (:479) | DEFERRED | Needs design (budget charging, EndGuard restore) | Design pass + bomb test at several heap sizes |
| World-command JSON alloc (:484) | OPEN | `JsonUtility.ToJson` in `LuaCsWorldRuntimeBindings.cs` (+ versioning/component bindings) | Document zero-alloc Rbx sink path (guardrail) |
| Guard microbenchmark (:488) | OPEN | No benchmark found; fold into F-20 | Add EditMode benchmark |

### Ladder foundation (:635-651)
| Item | Bucket | Evidence | Next step |
|---|---|---|---|
| #4 task scheduler (:635) | DONE | Shipped + wired 7.1.0 (`ModScheduler`, `LuaCsRbxSchedulerAdapter`); drains in stage table | Uncheck |
| #5 game:GetService (:637) | DONE | `ServiceCatalog.cs:100-160` whitelist + `RbxStubService(serviceName, plannedMvp, workaroundHint)` loud stubs | Uncheck |
| #7 mod system UX (:642) | OPEN | No `mod.json` handling in `FileLuaModSourceStore.cs` | Manifest + enable/disable + hot reload + C# API |
| #8 Lua log service (:644) | DONE | Registered `CoreAiModsInstaller.cs:150`, threaded `:231,623`, tool attached `:544`; `LuaCsModRuntime.cs:203,1251,1599-1688` appends | Uncheck |
| #9 syntax highlighting (:646) | DONE | `CoreAiUnity/Editor/LuaScriptedImporter.cs`, `CoreAIMods/Editor/LuauScriptedImporter.cs`, `LuaScriptViewerWindow.cs` exist | Uncheck |
| #10 networking stubs (:648) | DONE | `Networking/INetworkBridge.cs`, `NullNetworkBridge.cs`, `RbxRemotes.cs` exist | Uncheck |
| #11 test corpus (:650) | DONE | 40 fixtures in `.../RbxApi/CompatibilityCorpus/Fixtures/` (≥ ~20) + `LuauDownlevelerRbxCorpusEditModeTests.cs` | Uncheck |

### A7 accepted risks (:660-680)
| Item | Bucket | Evidence | Next step |
|---|---|---|---|
| Route pinning (:660) | DONE | `Assets/CoreAI/Runtime/Core/Features/LlmRouting/ILlmClientRegistry.cs:8` `LlmRoleRouteSnapshot`, `:79` `ResolveRouteForRole` | Uncheck |
| Generation lease (:666) | OPEN | Uncontradicted | Lease between resolve and execution |
| Disposed registry resolves (:670) | OPEN | `LlmClientRegistry.cs:55` has `_disposed` but resolution-guard unverified | Add `ObjectDisposedException` guards under gate |
| Offline + persisted assignment (:673) | OPEN | Uncontradicted | Fall through to offline/legacy client in Offline mode |

### A6 open TODOs (:717-754)
| Item | Bucket | Evidence | Next step |
|---|---|---|---|
| Coroutine guard 5.8.4 [~] (:717) | STALE | Replaced by the validated 5.8.7 line (:712: batchmode green, wrap stripped) | Delete line, keep 5.8.7 |
| guard-tight-loop-latency (:736) | OPEN | Uncontradicted | Watchdog thread or finer hook granularity |
| Alloc-guard backstop (:744) | DEFERRED | Documented Mono limitation, infeasible per text | Possible future: per-mod allocation-rate watchdog |
| LLM-API PlayMode gate (:750) | DEFERRED | Needs live endpoint (env) | Stand up spark/opencode server, run API + pipeline + behavior audit |

### R0.5 demo pass (:795-829)
| Item | Bucket | Evidence | Next step |
|---|---|---|---|
| Manual interaction drivers (:795) | OPEN | Procedural; no drivers/screenshots in repo | Per-demo drivers + screenshots |
| Demo review wave [~] (:797) | DONE | All three fixes stated as Fixed (Skills null-guard, Full-tier activation caps, G6 clean_tools) | Mark [x] |
| moddableunits-binding-seam (:802) | OPEN | Seam exists per text; threading + PlayMode validation remaining | Thread option through installer/scope, lazy forge lookup, validate, restore README |
| Demo hygiene (:809) | OPEN | `git ls-files` shows `Assets/Scenes/AutoSaves/` tracked despite `.gitignore:106`; machine-specific scene wiring per text | Untrack autosaves; strip local-LLM wiring |
| Hub-chat mod-writing per demo (:826) | OPEN | Procedural live-model work | 4B/9B/27B + Opus runs per Hub demo |

### R0.6 release engineering (:835-891)
| Item | Bucket | Evidence | Next step |
|---|---|---|---|
| F-12 CI gates [~] (:835) | OPEN | Merge-queue + package-graph done; IL2CPP builds + isolation matrix remaining | Licensed runner: Standalone/WebGL IL2CPP builds, consumer matrix |
| F-18 pin git deps (:839) | OPEN | Uncontradicted | Pin tags/commits + upgrade command |
| F-19 slim dev project (:840) | OPEN | Uncontradicted | Slim or minimal verification project; assets to Samples~ |
| F-20 perf regression suite (:842) | OPEN | Uncontradicted | Enqueue/streaming/world-query/store/audit/WebGL-cadence suite |
| F-22 package-local tests [~] (:844) | OPEN | `CoreAI.Core.Tests` pattern exists; unity/hub/benchmark remaining | Same pattern for remaining packages |
| F-21 signal-based waits (:847) | OPEN | Uncontradicted | Replace fixed `Task.Delay` waits in async tests |
| Cross-request idempotency (:850) | OPEN | Uncontradicted | Executor-level stable idempotency keys |
| Full-tier Lua queries → budgeted walker (:852) | DONE | `WorldQuerySceneWalker.cs:8` shared walker, consumed by `WorldInstanceAdapter.cs` + `LuaCsWorldQueryBindings.cs` (+ tests) | Uncheck |
| LlamaLib WebGL [~] (:854) | OPEN | Guard `CoreAiWebGlLlmUnitySceneGuard.cs` exists; no `GetPlatform` refs in Assets (upstream); verification pending per text | Upstream gate/split/fork + fresh EditMode + clean/incremental WebGL builds |
| Hub log copyable (:863) | OPEN | Uncontradicted | Text selection in log viewer |
| Hub Mods tab on WebGL (:866) | OPEN | No `HubModsPages` refs in `Assets/CoreAIHub/Runtime/` | Investigate defines/composition gating, restore tab |
| Durability (:886) | OPEN | Reset-sync DONE (`WorldStateManager.cs:551-600`, flush in `finally`); two-phase audit rotation + worker surfacing remain | Rotation recovery + runtime surfacing |
| Hub Audit Log page (:890) | OPEN | `AuditLogVerifier` exists per text; no viewer page found | Viewer + chain-integrity badge |

### Roadmap R4 runtime UI (:944-1008, 13 items) — all OPEN, flagship
Evidence for the block: no `*UiRuntime*`, `*FileUiSourceStore*`, `ui_command`/`ui_query` in Assets.
| Item | Next step |
|---|---|
| UXML factory + USS interpreter (:944) | Parse UXML→tree + USS subset in `CoreAI.Source`; roundtrip/degradation/parity tests |
| Editor materialization (:951) | After interpreter: text→`Assets/CoreAI.Generated/UI/` import |
| Theme system (:954) | Ship `CoreAiRuntimeTheme.uss` + tokens + semantic/state classes |
| CoreAiUiRuntime host (:960) | Scene component, reload-callback lifecycle, router, teardown |
| UI persistence (:964) | `FileUiSourceStore` on version-store pattern + auto-restore |
| LLM tools ui_command/ui_query (:970) | Standard tool-policy registration, honest errors |
| Mods↔UI binding + LuaCapabilities.Ui (:975) | `ui_*` Lua API through execution guard; detach on unload |
| Animations (:983) | State-class + transitions first; schedule-tween fallback |
| Hub page (:988) | Screen list, source view, revert, token editor, toggles |
| ui-builder skill (:990) | Ship via skill system (theme ref, patterns, repair checklist) |
| Small-model gate G9/G9r (:995) | 9B+skill or 27B builds + repairs HUD; benchmark scenarios |
| Verification gate (:1003) | EditMode + PlayMode + G9/G9r at small-model bar |
| Docs (:1006) | `runtime-ui.md`, Lua `ui_*` docs, INSTALL recipe, README bullet, minor bump |

### R5 compaction (:1014-1018)
| Item | Bucket | Evidence | Next step |
|---|---|---|---|
| Live compaction test (:1014) | OPEN | Only stub-covered per text | Long-conversation live PlayMode: token reduction + fact retention |
| Overflow-retry convergence (:1016) | OPEN | Only shrink factor unit-tested | Integration test: shrink loop succeeds |
| Summary token cap (:1018) | DONE | `ICoreAISettings.cs:34` default 2048 (not 0); wired `AiOrchestrator.cs:215` | Uncheck |

### R6 resilience (:1026-1030)
| Item | Bucket | Evidence | Next step |
|---|---|---|---|
| Circuit-breaker wiring (:1026) | OPEN | Decorator + tests only (`CircuitBreakerLlmClientDecorator.cs`, two test fixtures); no composition-root use | Wire threshold/cooldown/scope into production root |
| Fallback chain (:1028) | OPEN | 1-secondary only per text | Ordered list; wrap secondary in retry/logging |
| Per-provider rate limiting (:1030) | OPEN | Uncontradicted | Token/request buckets distinct from Lua limiter |

### R7 structured output (:1042) | DEFERRED | No `response_format`/`json_schema` refs in `Assets/CoreAI/Runtime/`; text says "decide whether to build" | Owner decision first
### R7.5 per-agent providers (:1054-1060)
| Item | Bucket | Evidence | Next step |
|---|---|---|---|
| Per-profile fallback+limits (:1054) | OPEN | Uncontradicted | Per-profile story for decorator + timeout/retry |
| Hot-swap consistency (:1056) | OPEN | Uncontradicted | Re-ApplyManifest/ApplyRouteTable on key/URL change |
| Per-agent docs (:1060) | OPEN | Uncontradicted | Inspector-only recipe |
### R8 vision (:1065) | OPEN | Send path + gate shipped; no live round-trip test | `[Explicit]` capture→model→assert PlayMode test
### R9 sub-agents (:1072-1075, 4 items) | OPEN | No `SubAgentDefinition`/`ExecuteSubAgentAsync` refs | Definition → registry/orchestrator → tool+DI → tests/docs/CHANGELOG

### Audit cleanup (:1079-1171)
| Item | Bucket | Evidence | Next step |
|---|---|---|---|
| Audit retention (:1079) | OPEN | No retention/quarantine for rotated logs | Retention policy + corrupt-tail quarantine |
| Lost-update races (:1082) | OPEN | Uncontradicted | Per-role lock, path-keyed locks, reload-on-change, atomic summary |
| Test flake (:1086) | OPEN | Uncontradicted | Fix dispose race in `QueuedAiOrchestrator` |
| Cheap items (:1089) | OPEN | Timeout-mapping sub-item fixed per text; collisions/wipe/ghosts/zombie/interval remain | Fix remaining five |
| Per-request statics (:1096) | OPEN | Uncontradicted | Per-call context when concurrency arrives |
| ChainReset limits (:1102) | DEFERRED | Accepted design per text | Keyed HMAC chain if tamper-evidence required |
| Truncation semantics (:1106) | DEFERRED | Note; no wire violation | `ShouldPartition`-style hysteresis for coherence |
| Pending-parent ownership (:1110) | OPEN | Uncontradicted | Per-manager routing via DI |
| Fold-marker over-cap (:1116) | OPEN | Uncontradicted (self-healing; external readers see over-cap) | Reserve marker headroom in limiter |
| ConfigureChatHistory guard (:1120) | OPEN | Uncontradicted; trivial null/whitespace guard | One-line fix + test |
| DelegateLlmTool IL2CPP (:1129) | OPEN | Uncontradicted | Verify in built player (RUNTIME-first) |
| TryLoad edge (:1134) | OPEN | Uncontradicted | Align `_unresolvedObjects`/pending-parent lifetimes |
| Benchmark zero-result green (:1137) | OPEN | Uncontradicted | `Assert.Inconclusive` for CI |
| Timeout chunk mutation (:1139) | OPEN | Uncontradicted | Copy-on-write for third-party clients |
| Router clear theft (:1142) | OPEN | Uncontradicted | Generation token alongside refcount |
| G8 divergence (:1146) | OPEN | Cosmetic; uncontradicted | Re-evaluate migration per launch |
| LastRoundtrip coverage (:1149) | OPEN | Uncontradicted | Set field on new clients' terminal paths |
| Multi-scope audit writer (:1153) | DEFERRED | Needs design decision | Per-scope files or shared singleton |
| Extractor vs lax models (:1157) | DEFERRED | Deliberate tradeoff, watch item | Trailing-lone-cited-block exception if G-stalls appear |
| File.Replace on WebGL (:1162) | OPEN | `FileConversationSummaryStore.cs:311` depends on it; support unverified | WebGL smoke check or `File.Move` fallback |
| Threading harden
...[truncated 2085 chars]