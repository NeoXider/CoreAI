# CoreAI Audit Wave — 2026-07-16 — Combined Summary

Seven parallel audits of the project at the unreleased **5.9.0** state (post-5.8.10 commits `222e6eae` runtime multi-endpoint routing, `92681445` portable readiness probes, `fa37a523` Qwen spell hardening), judged against the project goals: production-grade LLM agents in Unity, local-first, five packages in lockstep, and the flagship product intent — **an agent can switch API/endpoint at runtime keeping its history, with endpoint presets usable both from the Hub UI and from code**.

| Report | File |
|---|---|
| Dynamic API / multi-endpoint routing | [dynamic-api-routing.md](dynamic-api-routing.md) |
| Architecture & public API surface | [architecture-api.md](architecture-api.md) |
| Security | [security.md](security.md) |
| Runtime robustness & concurrency | [runtime-robustness.md](runtime-robustness.md) |
| Tests & CI | [tests-ci.md](tests-ci.md) |
| Docs & release hygiene | [docs-release.md](docs-release.md) |
| QwenDemo runtime correctness | [qwendemo-runtime.md](qwendemo-runtime.md) |

## Verdict

The core promise **works**: conversation history, memory, and tool registrations are role-keyed and genuinely survive an endpoint/provider switch; Hub and code share one registry abstraction (`ILlmEndpointRegistry`) with no duplicated persistence; credential hygiene is deliberate (session keys never persisted, no keys committed to git, prompt-injection endpoint-hijack channel closed). But the switch is **not yet behavior-preserving or release-ready**: the orchestrator still makes tool-strategy and context-budget decisions against the *old* route, retries break on the routing sentinel, `COREAI_NO_LLM` builds no longer compile, and the flagship scenario has zero test coverage and zero documentation.

## Release blockers for 5.9.0 (fix before shipping)

1. **[CRITICAL] `COREAI_NO_LLM` breaks compilation of `CoreAI.Source`.** The new `LlmEndpointClientFactory` references types compiled out under the define (`LlmEndpointClientFactory.cs:106/245`, `LlmClientRegistry.cs:1417-1419`); no CI config catches it because the isolation smoke test is itself `#if !COREAI_NO_LLM`. → architecture-api.md C1, C9.
2. **[HIGH] Retries break under default routing.** `RoutingLlmClient.Prepare` writes the `"fallback"` sentinel into `request.RoutingProfileId`; retry decorators re-enter with it as an explicit profile and get `RoutingUnavailableClient` instead of a retry (`RoutingLlmClient.cs:188-191`, `LlmClientRegistry.cs:496`). → dynamic-api-routing.md.
3. **[HIGH] Tool-calling strategy ignores the routed endpoint.** Native-vs-text resolution is role-only (`AiOrchestrator.cs:1410`, `RoutingLlmClient.cs:53-57`); after a switch (or with `WithLlmProfile`) tools can silently stop working or use the wrong contract. → dynamic-api-routing.md; architecture-api.md C4.
4. **[HIGH] Prompt/history budgeting ignores the routed endpoint's context window** (`AiOrchestrator.cs:122-124`); descriptor `ContextWindowTokens` never constrains the prompt — a 128K→8K switch overflows every turn. → dynamic-api-routing.md.
5. **[HIGH] Native-host leak on Hub "Save".** Re-saving a Ready LLMUnity endpoint with `Active=false` via `AddOrUpdateEndpointAsync` replaces the runtime entry without releasing the owned llama.cpp host — server/VRAM/GameObject leak (`LlmClientRegistry.cs:599-644`; `SetEndpointActiveAsync` handles the same case correctly). → runtime-robustness.md.
6. **[HIGH] Registry constructor does I/O and starts activation at DI build time**, and restored key-auth endpoints deterministically re-activate with an empty key and land `Failed` on every launch (`LlmClientRegistry.cs:256, 1281-1287`). → architecture-api.md C3.
7. **[HIGH] Flagship scenario untested.** No test anywhere exercises endpoint/profile switch mid-conversation with history preservation (the only switch test checks routing of the *next* request). → tests-ci.md H1.
8. **[HIGH] CoreAiUnity CHANGELOG is missing all released sections 5.8.2–5.8.10** (jumps [Unreleased] → 5.8.1) despite lockstep releases. → docs-release.md.

## High-value problems (not strictly blocking, fix soon)

- **Config precedence maze (HIGH):** four sources of truth for "which endpoint" (settings asset, routing manifest, runtime registry, `CoreAiBackend` hot-swap) with silent precedence; the Hub Settings page hosts two of them and reports "Applied live" even when a runtime role-profile makes the change a no-op (`LlmClientRegistry.cs:348-391`, `HubSettingsPage.cs:160/239/1006`). → architecture-api.md C2.
- **Behavior drift after switch (MEDIUM):** runtime HTTP endpoints drop `ReasoningMode`/`ThinkingBudget`/`MaxTokens`/`ExtraBodyJson` that the legacy backend honors (`LlmEndpointClientFactory.BuildHttp`). → dynamic-api-routing.md.
- **No health demotion (MEDIUM):** `Ready` is sticky; 401/404 mid-conversation never triggers failover (`FallbackProfileIds` engage only for non-Ready endpoints) and Hub keeps showing Ready. → dynamic-api-routing.md.
- **`AssignRoleProfile("npc.*", …)` silently never matches** — matcher supports exact + `"*"` only, despite pattern-named API and docs (`LlmClientRegistry.cs:883-899`). → dynamic-api-routing.md.
- **Agents pinned via `WithLlmProfile` to a removed/typo profile fail permanently** with no fallback or diagnostic; Hub's "returns to Automatic" claim is false for builder-pinned agents. → dynamic-api-routing.md.
- **Busy-wait loops hot-spin a thread-pool thread at 100% CPU** for the whole model activation / stream drain when entered from `ConfigureAwait(false)` continuations (`LlmClientRegistry.cs:142-155, 1129-1132`, `LlmEndpointClientFactory.cs:297-308`); the correct `Task.WhenAny` pattern already exists at `:1195-1218`. → runtime-robustness.md; architecture-api.md C6.
- **`Changed` event fires synchronously under `_gate`** into UI Toolkit subscribers on sync-completing activations; and `RoutingLlmClient.Prepare` resolves client/profile/context/mode in four separate lock acquisitions — a concurrent switch yields torn state (client from endpoint A, context window from endpoint B). → runtime-robustness.md.
- **Mutable global statics (`CoreAISettings.Instance`, `CoreAiRoutingUi.Controller`, `CoreAiBackend` scope pick via `FindAnyObjectByType`)**: last-scope-wins with multiple scopes; `CoreAiRoutingUi` statics not cleared in `ResetForSubsystemRegistration` (domain-reload-disabled hazard). → architecture-api.md C5; runtime-robustness.md.
- **Endpoint validation duplicated and already divergent** between `HubSettingsPage.ValidateEndpoint` and `LlmEndpointDescriptor.Validate`; Hub silently clamps what code-only callers get exceptions for; slug/unique-ID derivation exists only in the UI. → architecture-api.md C7.
- **Test coverage gaps around the registry:** `CancelInFlight` (implementation unconditionally returns `false`, indistinguishable from "not found"), `KeepWarm` lifecycle, and the `Changed` event on the real registry are all untested. → tests-ci.md M1-M3.
- **CI silently skips EditMode/PlayMode jobs when `UNITY_LICENSE` is missing** on push/PR (green run + notice); only the merge queue hard-fails — risky with direct-to-main commits and a static README badge. → tests-ci.md M4.
- **`Assert.ThrowsAsync`/`CatchAsync` reintroduced in new EditMode tests** — the documented interactive-runner freeze hazard. → tests-ci.md M5.

## Gaps vs the stated product intent

- **Hub/code parity:** Hub has no profile editor — fallback chains are code-only; conversely slug/ID derivation and name-requiredness are UI-only. The Chat page "API" dropdown persistently rewrites *global* role routing (surprising side effect). The facade `CoreAi.AskAsync` cannot express a routing profile even though the Hub can re-route its default role out from under it.
- **Documentation:** the history-preserving switch guarantee is documented nowhere; `RUNTIME_BACKEND_SWITCHING.md` is unreachable from any index; root README never advertises the routing feature; TODO.md §R7.5 contradicts shipped code. → docs-release.md.
- **WebGL:** the default env-var secret provider is dead on WebGL (no environment variables), so key-auth endpoints can't restore there.
- **Dogfooding:** `QwenDemoReadiness` re-implements endpoint readiness via reflection instead of consuming the new `ILlmEndpointReadinessProbe` shipped in the same release.

## Lower-severity notes

- Security (all Low): persisted `llm-endpoints.json` auto-activates every Active/KeepWarm endpoint on restart, sending env-resolved keys to whatever host the locally-writable descriptor names (no host allowlist/re-confirmation); registry-load failure logs raw Newtonsoft `ex.Message`. Everything else verified clean — no committed keys, redaction intact, sandbox unchanged.
- QwenDemo: LLM turns hardcode `CancellationToken.None` (uncancellable, outlive the scene, leak into edit mode with Reload Domain disabled); `RunDeterminism` is `async void`; one wiring fix (`_lifetimeCancellation.Token` through `LlmMeter.RunAsync`) closes most of it. Demo HUD strings are Russian, contrary to the EN-artifacts rule; Russian sections also remain in both changelogs (5.6.2, 4.10.2) plus mojibake in CONTEXT_MANAGEMENT_ROADMAP.md.
- Design debt: `LlmClientRegistry` (1,460 lines, five interfaces) and `HubSettingsPage`/`CoreAiChatPanel` god-files; `InternalsVisibleTo` from Core/Source to the separately-shipped `CoreAI.Mods`; `CoreAI.Demos` hard-references the Hub package (inconsistent with the optional-packages story); `SaveEndpointAsync` conflates "saved" with "activated".

## What is done well

- History/memory/tools genuinely survive endpoint switches; staged zero-downtime endpoint replacement with generation tracking and drain-before-release; owned-host leases for LLMUnity with an actionable refusal message.
- Clean ports-and-adapters seam: pure-BCL routing contracts in Core (`noEngineReferences`), shared readiness policy reused by both .NET and UnityWebRequest probes; Hub consumes only public surface through one controller.
- Credential hygiene end-to-end: write-only masked keys, `SecretReference`-only persistence, atomic store writes, build guard against keys in Resources, redaction intact on the changed transport; endpoint create/switch is human-UI-only — Lua mods and LLM tools cannot touch the registry.
- New routing tests that do exist are strong (TCS-gated concurrency/hot-swap/generation tests, real loopback HTTP servers for probes, secrets asserted absent from persisted JSON); FastNoLlm stays LLM-free; CI has anti-hollow minimum-count guards across three define configs.
- Docs fundamentals: all five package.json at 5.9.0 with matching pins; Unreleased changelog sections truthfully cover the three unreleased commits; LLM_ROUTING.md API names match real code.

## Suggested order of work

1. Fix the eight release blockers (items 1–8 above); add a `COREAI_NO_LLM` compile config to CI as part of #1.
2. Add the missing flagship test: switch endpoint mid-conversation, assert history + tool strategy + context budget follow the new endpoint (pins fixes #2-#4).
3. Converge endpoint config: surface effective routing in the Hub Backend section, or fold the legacy settings backend into the registry as a synthesized endpoint.
4. Document the switch guarantee (LLM_ROUTING.md), link RUNTIME_BACKEND_SWITCHING.md from the indexes, advertise routing in README, refresh TODO §R7.5.
5. Sweep the mediums: parameter parity for runtime HTTP endpoints, health demotion, role-pattern matching (or rename the API), busy-waits → `Task.WhenAny`, validation/slug logic into core contracts.
