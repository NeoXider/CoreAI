# Architecture & Public API Surface Audit — 2026-07-16

Auditor scope: first-party code only (`Assets/CoreAI`, `Assets/CoreAiUnity`, `Assets/CoreAIMods`, `Assets/CoreAIHub`, `Assets/CoreAIBenchmark`, `Assets/CoreAI.Demos`, `Assets/_exampleGame`). Focus: package boundaries, public API ergonomics, optional-module seams (`COREAI_NO_LLM` / `COREAI_NO_LUA`), the new (unreleased 5.9.0) runtime multi-endpoint LLM routing (`222e6eae`, `92681445`), and DI/lifecycle.

## Scope & goal alignment

The stated product direction — "reusable API/endpoint presets usable both from the Hub UI and from code" — is structurally in place: a portable core contract set (`ILlmEndpointRegistry`, `LlmEndpointDescriptor`, `LlmRuntimeProfile`, `ILlmEndpointReadinessProbe` in `Assets/CoreAI/Runtime/Core/Features/LlmRouting/`) is implemented once in `CoreAI.Source` (`LlmClientRegistry`) and consumed by the Hub through a thin adapter (`ICoreAiRoutingUiController` / `LlmEndpointRegistryUiController` in `Assets/CoreAiUnity/Runtime/Source/Features/Chat/CoreAiRoutingUiController.cs`). Hub does **not** duplicate persistence or probing — both live below the UI, reachable by code-only users. That part of the design is sound.

The two systemic problems are: (1) the new routing feature was not integrated into the `COREAI_NO_LLM` seam and will not compile with that define set; (2) the routing layer adds a **fourth** source of truth for "which LLM endpoint do I talk to", layered on top of three pre-existing ones, with silent precedence rules that the Hub Settings page itself obscures.

---

## Confirmed problems

### C1. CRITICAL — `COREAI_NO_LLM` breaks compilation of `CoreAI.Source` (new routing code has no guard)

**Files:**
- `Assets/CoreAiUnity/Runtime/Source/Features/Llm/Infrastructure/LlmEndpointClientFactory.cs:106` (also :245, :282)
- `Assets/CoreAiUnity/Runtime/Source/Features/Llm/Infrastructure/LlmClientRegistry.cs:1417-1419`

`LlmEndpointClientFactory` has **zero** `COREAI_NO_LLM` directives (only `COREAI_HAS_LLMUNITY`/`UNITY_WEBGL`), yet it references types whose entire files are compiled out under that define:

```csharp
// LlmEndpointClientFactory.cs:106 — unconditional
Client = new OpenAiChatLlmClient(options, _settings, _logger, _memoryStore),
```

while `OpenAiChatLlmClient.cs`, `LlmUnityServerHttpSettings.cs`, `ServerManagedLlmClient.cs`, `RefreshOnUnauthorizedDecorator.cs` all begin with `#if !COREAI_NO_LLM` (verified line 1 of each). Same for `LlmUnityServerHttpSettings` at factory lines 245 and registry lines 1417–1419 (inside the `COREAI_HAS_LLMUNITY && !UNITY_WEBGL` branch, which is independent of `COREAI_NO_LLM`).

**Failure scenario:** any project that sets `COREAI_NO_LLM` (the documented way to strip the LLM module) gets CS0246 in `CoreAI.Source` — the whole package stops compiling, not just the LLM feature. The pre-existing code was careful about this (`LlmClientRegistry.BuildProfileClient` at :1347 and `LlmPipelineInstaller` at :168/:252/:269/:297 all guard); the 5.9.0 routing wave regressed the seam. The `CorePackageIsolationSmokeEditModeTests.cs` is itself `#if !COREAI_NO_LLM`, so no test catches this.

**Fix:** wrap the HTTP/LLMUnity activation paths in `LlmEndpointClientFactory.ActivateAsync`/`BuildHttp` and `LlmClientRegistry.BuildProfileClient`'s LocalModel adapter with `#if !COREAI_NO_LLM` (returning `StubLlmClient` or throwing `PlatformNotSupportedException`, matching the existing convention), and add a CI compile check with the define set.

### C2. HIGH — Four+ sources of truth for LLM endpoint config, with silent precedence; the Hub Settings page co-hosts two of them without explaining which wins

**Files:** `Assets/CoreAiUnity/Runtime/Source/Features/Llm/Infrastructure/LlmClientRegistry.cs:348-391`, `Assets/CoreAIHub/Runtime/HubSettingsPage.cs:160` ("Backend" section) vs `:239` ("API profiles" section), `Assets/CoreAiUnity/Runtime/Source/Api/CoreAiBackend.cs:132-160`.

Endpoint configuration now lives in:
1. `CoreAISettingsAsset` (ScriptableObject; ApiBaseUrl/ApiKey/Model/ExecutionMode + secondary fallback backend) — the "legacy fallback" client;
2. `LlmRoutingManifest` (ScriptableObject; per-role static profiles) applied via `ApplyManifest`;
3. the runtime endpoint registry persisted to `persistentDataPath/CoreAI/llm-endpoints.json` (`FileLlmEndpointRegistryStore`) — what the Hub "API profiles" UI edits;
4. `CoreAiBackend.Apply*` static hot-swap, which mutates (1) at runtime and swaps the legacy fallback (`RebuildAndNotify` → `registry.SetLegacyFallback`).

Resolution precedence in `ResolveClientForRole(role, explicitProfile)` is: runtime role-profile (3) → manifest profile (2) → legacy fallback (1/4). Consequence: **when a role has a runtime profile assigned, pressing "Apply" in the Hub's "Backend" section (or calling `CoreAiBackend.ApplyHttpApi`) changes nothing for that role**, yet `HubSettingsPage.Apply()` reports "Applied live: …" (:1006) and `CoreAiBackend.VerifyAsync` probes `SmartChat`'s routed client (:337), which may be a completely different endpoint than the one just "applied". Both panels sit on the same page with no indication of the override relationship.

**Fix:** surface effective routing per role in the Backend section (e.g. "SmartChat is currently overridden by profile 'X'; Backend settings apply only to roles on Automatic"), and/or converge: make the legacy settings-asset backend a synthesized read-only endpoint (`endpointId: "settings-default"`) inside the same registry so there is one resolution table.

### C3. HIGH — Registry constructor performs I/O and starts endpoint activation at DI-container build time; restored key-auth endpoints deterministically fail

**File:** `Assets/CoreAiUnity/Runtime/Source/Features/Llm/Infrastructure/LlmClientRegistry.cs:256` (`RestoreRuntimeState()` in ctor), `:1281-1287`, and `Assets/CoreAiUnity/Runtime/Source/Composition/LlmPipelineInstaller.cs:68-69`.

The constructor loads `llm-endpoints.json` and immediately calls `BeginActivationLocked` for every `Active || KeepWarm` endpoint — HTTP readiness probes and even LLMUnity **native server startup** are triggered as a side effect of object construction. Because `LlmPipelineInstaller` registers a build callback that resolves `CoreAiRoutingUiAttachment` (which resolves the registry), this happens synchronously-at-build for every `CoreAILifetimeScope`, whether or not the game will use routing.

Compounding it: session API keys are intentionally not persisted (correct), and secrets are only recoverable via `SecretReference` → environment variable (`EnvironmentSecretProvider`, :86-95). So any Hub-created HTTP endpoint saved with a session key and no secret reference will, on every subsequent launch, auto-activate, probe with an empty key, and land in `Failed` state with a probe error — repeated startup network traffic and a confusing red state the user never asked to re-check.

Also note `BeginActivationLocked` is named `*Locked` but is called from the constructor **without** holding `_gate` (:1285); harmless today only because the instance isn't published yet.

**Fix:** restore state as `Inactive` and defer activation until first resolve for a routed role (the `ActivatingEndpointClient` machinery already supports lazy await), or gate auto-activation behind an explicit `KeepWarm`-only policy; skip activation when the endpoint requires a credential that cannot be resolved.

### C4. MEDIUM — `RoutingLlmClient.SupportsNativeToolCallingForRole` ignores the request's explicit profile

**File:** `Assets/CoreAiUnity/Runtime/Source/Features/Llm/Infrastructure/RoutingLlmClient.cs:53-57`.

```csharp
public bool SupportsNativeToolCallingForRole(string agentRoleId)
{
    ILlmClient inner = _registry.ResolveClientForRole(agentRoleId);   // no profile
    ...
```

`Prepare`/`PreflightAnnotate` resolve with `request.RoutingProfileId` (:47-49, :189), but the native-tool-support decision is made against the role's *default* route. With the new `AgentBuilder.WithLlmProfile(...)` (`AgentBuilder.cs:254`), an agent pinned to an endpoint whose client differs in tool support (e.g. Offline/Stub default vs HTTP profile, or vice versa) gets the wrong tool-calling strategy — native tools disabled on a capable endpoint or attempted on a stub. The `ILlmClient` interface has no profile-aware overload for this member, so callers can't work around it.

**Fix:** add `SupportsNativeToolCallingForRole(string roleId, string explicitProfileId)` as a default interface method (matching the pattern already used in `ILlmClientRegistry`), and have the orchestration path call it with the request's profile.

### C5. MEDIUM — Mutable global statics as routing/config attachment points; last-scope-wins and Editor-lifetime hazards

**Files:** `Assets/CoreAiUnity/Runtime/Source/Composition/CoreAILifetimeScope.cs:148-151`, `Assets/CoreAiUnity/Runtime/Source/Features/Chat/CoreAiRoutingUiController.cs:130-170`, `Assets/CoreAiUnity/Runtime/Source/Api/CoreAiBackend.cs:513-517`.

Every scope's `Configure` executes:

```csharp
CoreAISettingsAsset.SetInstance(settings);
builder.RegisterInstance<ICoreAISettings, CoreAISettingsAsset>(settings);
CoreAISettings.Instance = settings;
```

plus `CoreAiRoutingUi.Controller = ...` via `CoreAiRoutingUiAttachment`. With two scopes (additive scenes, parent/child scopes, or a demo scope like `CoreAiDemoScope`), the statics hold whichever scope built last, while each container holds its own instance — `HubSettingsPage` (which falls back to `CoreAISettingsAsset.Instance` at :1129 and `CoreAiRoutingUi.Controller` at :148) can then edit a *different* registry/settings than the one serving the agent it sits next to. `CoreAiBackend` independently picks a scope by `FindAnyObjectByType<CoreAILifetimeScope>` (:515) — "any" ordering is unspecified, so with multiple scopes the hot-swap target is arbitrary. With domain reload disabled, `CoreAiBackend.OnBackendChanged` subscribers and `CoreAiRoutingUi.Controller` also survive play-mode restarts (the attachment's `Dispose` clears the controller only if it is still the current one — good — but event subscribers of destroyed pages are only removed if `OnDestroyed` ran).

**Fix:** at minimum log a warning when a second scope overwrites `CoreAISettings.Instance`/`CoreAiRoutingUi.Controller`; longer term, make `CoreAiBackend`/Hub resolve through the scope they were created under (the Hub already accepts `ICoreAiRoutingUiController` injection — make the DI path the only path and keep the static as an explicit opt-in).

### C6. MEDIUM — Busy-wait loops in the activation path

**Files:** `Assets/CoreAiUnity/Runtime/Source/Features/Llm/Infrastructure/LlmClientRegistry.cs:142-155` (`AwaitWithoutCancellingSharedActivation`), `:1129-1132` (`ReleaseOwnedHostAfterDrainAsync`), `Assets/CoreAiUnity/Runtime/Source/Features/Llm/Infrastructure/LlmEndpointClientFactory.cs:297-308` (`WaitUntilReadyAsync`).

Three separate `while (!task.IsCompleted) { await Task.Yield(); }` loops. On the Unity main-thread SynchronizationContext this is one iteration per player-loop pump (acceptable); but these awaits also run on thread-pool continuations (activation is started from `Task`-based code paths with `ConfigureAwait(false)` upstream in `AwaitActivationForCallerAsync`), where `Task.Yield` requeues immediately — a hot CPU spin for the entire native-model startup (tens of seconds). The intent (don't propagate one caller's cancellation into a shared activation) is achievable without polling.

**Fix:** replace with `Task.WhenAny(activation, cancellationTcs.Task)` — the registry already has exactly this pattern implemented correctly in `AwaitActivationForCallerAsync` (:1195-1218). Reuse it.

### C7. MEDIUM — Endpoint validation logic duplicated between Hub UI and core contract, already divergent

**Files:** `Assets/CoreAIHub/Runtime/HubSettingsPage.cs:734-754` (`ValidateEndpoint`) vs `Assets/CoreAI/Runtime/Core/Features/LlmRouting/LlmEndpointContracts.cs:71-108` (`LlmEndpointDescriptor.Validate`).

The Hub re-implements the absolute-http(s)-URL check verbatim and adds its own rules (DisplayName required — core doesn't require it), while core rules the Hub doesn't show (ContextWindow ≥ 256, port range, ParallelSlots ≥ 1) are silently *clamped* in `ReadEndpointEditor` (:722, :727-729) instead of reported. A code-only user calling `AddOrUpdateEndpointAsync` gets an `ArgumentException` for the same input the Hub silently fixes up. Slug/unique-ID derivation (`Slug`, `UniqueEndpointId`, :805-819, :898-919) exists only in the UI, so "the same preset created from code" gets different ID semantics.

**Fix:** move name-requiredness, clamping policy, and ID slug derivation into the core contracts (e.g. `LlmEndpointDescriptor.Normalize()` + richer `Validate()`), and have the Hub display `descriptor.Validate()` output instead of its own string.

### C8. MEDIUM — Cross-package `InternalsVisibleTo` couples the lockstep packages

**Files:** `Assets/CoreAI/Runtime/Core/AssemblyInfo.cs`, `Assets/CoreAiUnity/Runtime/Source/AssemblyInfo.cs`.

`CoreAI.Core` grants internals to `CoreAI.Mods` (a separately shipped package), and `CoreAI.Source` grants internals to `CoreAI.Mods` and `CoreAI.Editor`. Test-assembly grants are fine; the production `CoreAI.Mods` grant means the Lua modding package is compiled against non-public Core/Source surface — any internal refactor in Core is a potential silent break for Mods, defeating the point of "Hub depends only on public APIs" hygiene that the Hub itself observes (Hub is correctly absent from both lists).

**Fix:** inventory what internals `CoreAI.Mods` actually touches and either promote them to public API (they are de facto public) or introduce an explicit `CoreAI.Core.ModdingContracts` surface.

### C9. LOW — Readiness probes not covered by the `COREAI_NO_LLM` seam (dead code, convention drift)

**Files:** `Assets/CoreAI/Runtime/Core/Features/Llm/HttpClientOpenAiReadinessProbe.cs`, `Assets/CoreAiUnity/Runtime/Source/Features/Llm/Infrastructure/UnityWebRequestOpenAiReadinessProbe.cs`, `Assets/CoreAiUnity/Runtime/Source/Composition/LlmPipelineInstaller.cs:40-41`.

Unlike every other HTTP-facing file in `Features/Llm`, neither probe carries `#if !COREAI_NO_LLM`, and the installer registers the probe unconditionally. They happen to compile standalone (only BCL/UnityWebRequest deps), so this is not a build break — but a "no LLM" build still ships live HTTP probing code and DI registrations for it. Fold into the C1 fix.

---

## Potential problems / risks (unverified)

- **(unverified) `HubSettingsPage.HandleBackendChanged` threading** (`HubSettingsPage.cs:1105-1108`): `Task.Yield().GetAwaiter().OnCompleted(RefreshFromStatus)` schedules on whatever context raises `CoreAiBackend.OnBackendChanged`. All current raisers appear to be main-thread, but nothing enforces it; a background raiser would touch UIToolkit off the main thread.
- **(unverified) `DescriptorFingerprint` includes `Active`/`KeepWarm`** (`LlmClientRegistry.cs:1152-1170`): toggling only Active/KeepWarm through `AddOrUpdateEndpointAsync` is treated as a "different" descriptor and cancels/restarts an in-flight activation of an otherwise identical endpoint. May be intended; worth a test.
- **(unverified) Editor-time SO mutation persistence**: `CoreAiBackend.SetApiKey`/`ApplyHttpApi` mutate `CoreAISettingsAsset` fields; in the Editor these mutations can end up serialized into the asset (including API keys typed into the Hub while play-testing) if anything marks the asset dirty. The Hub labels session keys write-only for the *registry* path, but the legacy Backend path writes the key into the SO.
- **(unverified) `LlmClientRegistry.GetEndpoints()` allocation churn** (`:510-520`): clones every descriptor per call; `HubSettingsPage.RefreshEndpointManagement` calls it on every `Changed` event. Fine at Hub scale, worth watching if `Changed` fires per activation state transition during startup of many endpoints.
- **(unverified) `ILlmClientRegistry` default interface methods** (`ILlmClientRegistry.cs:15-45`): DIMs are supported on Unity's .NET Standard 2.1 profile, but IL2CPP + older Unity LTS combinations have had codegen quirks; the repo targets one Unity version so this is likely fine — flagging only because the contract is in the engine-agnostic core package that "-ish" targets other hosts.

---

## Design-debt observations

- **`LlmClientRegistry` is a god class by responsibility count** (`LlmClientRegistry.cs`, 1460 lines): it is simultaneously (a) the legacy manifest/route-table router, (b) the new runtime endpoint registry with lifecycle/generation/drain semantics, (c) the persistence orchestrator, (d) `ILlmRoutingController`, and (e) the legacy-fallback holder — four public interfaces plus `IDisposable` on one class. The runtime-endpoint half (RuntimeEndpoint, activation, leases) is separable into its own type with the router composing it.
- **`CoreAiChatPanel.cs` at 3,201 lines** (grew +221 routing lines and +~300 more since) and **`HubSettingsPage.cs` at 1,297 lines** are the two largest UI files; `HubSettingsPage` mixes two distinct configuration systems (legacy backend + endpoint registry) in one page class. The endpoint editor (fields, load/save/remove, slug/labels) is a self-contained widget begging for extraction — it is also exactly the part a game team would want to embed in their own UI.
- **`QwenDemoShared.cs`** (807 lines, `Assets/CoreAI.Demos/QwenDemo/`): a grab-bag of nine unrelated classes (metering, layout math, tool contracts, turn guards, readiness polling, main-thread pump, procedural FX). Demo-only so severity is low, but two items leak beyond demo scope: `QwenDemoReadiness.FindLlmHost`/`ProbeHttpAsync` re-implements endpoint readiness via reflection instead of consuming the new `ILlmEndpointReadinessProbe` (a missed dogfooding opportunity for the very feature shipped in the same release), and the HUD strings are Russian (`"за сколько и сколько токенов"`, `"ошибка:"`) contrary to the EN-artifacts rule. Namespace is `CoreAI.ExampleGame.QwenDemo` although the file lives in the `CoreAI.Demos` asmdef (rootNamespace `CoreAI.Demos`).
- **Facade tiering gap**: `CoreAi.AskAsync`/`StreamAsync` cannot express a routing profile; profile selection requires dropping to `AgentBuilder.WithLlmProfile` or raw `AiTaskRequest.LlmProfileId`. That is a defensible ladder, but the Hub can assign a profile to `SmartChat` (the facade's default role), so the facade *is* affected by routing it cannot see or override — document this, or add an optional `profileId` parameter.
- **`CoreAI.Demos` hard-references `CoreAI.Hub.UI`** (`CoreAI.Demos.asmdef`), while `CoreAIMods` correctly treats Hub as optional via the `COREAI_HAS_HUB` versionDefine (`CoreAI.Mods.Hub.asmdef`). Demos therefore cannot compile without the Hub package — inconsistent with the five-packages-optional story.
- **`RemoveEndpointAsync(CancelInFlight)` silently returns `false`** (`LlmClientRegistry.cs:715-718`). The contract says registries that can't prove cancellation "must reject removal instead of reporting a false success" — returning `false` is honest, but indistinguishable from "endpoint id not found" (:740). An exception or result enum would be clearer.
- The `CoreAiRoutingUiResult.Ok` semantics from `SaveEndpointAsync` conflate "saved" with "activated" (`CoreAiRoutingUiController.cs:94-102`): a saved endpoint that failed its probe returns `Ok=false` even though the descriptor was persisted; the Hub then shows the message but the state suggests the save failed.

## What is done well

- **Core purity holds.** `CoreAI.Core.asmdef` has `noEngineReferences: true` and empty `references`; the new routing contracts (`LlmEndpointContracts.cs`, `LlmEndpointReadiness.cs`, `ILlmClientRegistry.cs`) are pure BCL, and the shared status policy (`LlmEndpointReadinessPolicy`) is genuinely reused by both the .NET (`HttpClientOpenAiReadinessProbe`) and Unity (`UnityWebRequestOpenAiReadinessProbe`) adapters — a clean ports-and-adapters seam.
- **Hub consumes only public surface.** `CoreAI.Hub.UI` is absent from every `InternalsVisibleTo` list and reaches routing exclusively through `ICoreAiRoutingUiController`; there is one registry implementation, not a Hub-side copy. The "API presets from Hub and from code" goal has the right single abstraction (`ILlmEndpointRegistry`).
- **Credential hygiene is deliberate and consistent**: session keys are write-only in the UI, never returned by `ICoreAiRoutingUiController`, never serialized by `FileLlmEndpointRegistryStore` (state is `Sanitize`d and contains only `SecretReference`), and the store writes atomically (tmp + move) with WebGL FS sync.
- **Endpoint lifecycle engineering is thoughtful**: generation counters, staged replacement via `_pendingEndpoints` (zero-downtime swap of a Ready endpoint), drain-before-release with in-flight tracking (`TrackedEndpointClient`), owned-host leases with restart bookkeeping for LLMUnity (`LlmUnityOwnedHostLeases`), and explicit refusal to reconfigure an externally-owned active LLMUnity host with an actionable error message (`LlmEndpointClientFactory.cs:175-179`).
- **Backward compatibility via default interface methods** on `ILlmClientRegistry` lets legacy registries keep compiling while new profile-aware overloads exist — a low-friction way to grow a public interface.
- **Test discipline around the new feature** is real: `CoreAiRoutingUiEditModeTests` (399 lines), `LlmEndpointReadinessEditModeTests` (193), `LlmRuntimeRegistryPlayModeTests` (130) landed in the same commits as the feature.
- Structured, greppable activation logging (`LlmUnityActivationLog`) with model-path redaction in error strings is a nice operational touch.
