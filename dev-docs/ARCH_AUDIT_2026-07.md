# CoreAI — Clean Architecture Audit (2026-07)

READ-ONLY audit. No code was changed. Scope: package layout & asmdef dependency
direction; Chat/Vision/World/Llm features (`Assets/CoreAiUnity/Runtime/Source/Features`);
mods runtime + Rbx binding (`Assets/CoreAIMods/Runtime`); composition installers; Hub UI.

Bar used: the project's own "RedoSchool-level Clean Architecture" checklist — dependencies
point inward, vertical feature slices with ports/adapters, DI at the edges, no
service-locator/static leakage into logic, consumer-owned interfaces, SRP/no god-objects,
real testable seams, typed errors (no string-sniffing), and Rbx-1:1 Lua surface (no NEW
bespoke CoreAI Lua API beyond the known stopgap).

Verdict: **the architecture is genuinely good and the inner-layer discipline is real, not
cosmetic.** The findings are concentrated in a few outer-layer god-classes, a duplicated
scene-locator pattern at the API edge, and convention/doc drift. Nothing rots the core.

---

## What is DONE WELL (verified, not assumed)

- **Core is provably engine-free.** `CoreAI.Core.asmdef` has `noEngineReferences: true` and
  an empty `references` array; the only `UnityEngine` tokens under
  `Assets/CoreAI/Runtime/Core` are 5 XML/`// WHY:` comments (e.g.
  `CoreAIAgent.cs:70`, `CoreAiEvents.cs:128`). No `using UnityEngine;` and no
  `using VContainer;` anywhere in Core. Dependency inversion is enforced by the compiler,
  not by convention.
- **Rbx datatype layering is clean and inward-pointing.**
  `CoreAI.RbxApi.Datatypes` (empty refs, `noEngineReferences: true`) ← `RbxApi.Instances`
  (engine-free) ← `RbxApi.Unity` (engine-bound spatial) ← `RbxApi.Binding`. Pure data at the
  bottom, engine adapters only at the top. No cycles.
- **Ports are consumer-owned.** `ILlmClient` is defined in
  `Assets/CoreAI/Runtime/Core/Features/Orchestration/ILlmClient.cs` (inner layer) and
  implemented by the outer adapter `MeaiLlmClient` in `.../Source/Features/Llm/Infrastructure`.
  Classic dependency-inversion: the core owns the interface, infrastructure supplies the impl.
- **DI lives strictly at the edges.** All `LifetimeScope`/`IContainerBuilder` registration is
  in `Source/Composition/*` and `CoreAIMods/Runtime/Composition/*`; none in Core. Installers
  are cohesively split by concern (`LlmPipelineInstaller` 494, `WorldCommandsInstaller` 226,
  `AgentPromptsInstaller`, `CoreServicesInstaller`, `GlobalMessagePipeMinimalBootstrap`),
  not one mega-installer.
- **Hub is a truly optional layer.** `CoreAI.Mods.Hub.asmdef` is gated behind
  `defineConstraints: ["COREAI_HAS_HUB"]` fed by a `versionDefines` on
  `com.neoxider.coreaihub`, so the mods runtime has no hard compile dependency on the Hub UI.
- **Errors are modeled as types, not sniffed strings.** A repo-wide search for
  `.Message.Contains("error")` / `StartsWith("Error")` / `IndexOf("error")` across Core,
  Source, and Mods returned **zero** hits. Failure states use typed carriers instead —
  `RbxError`, `RobloxApiStubException`, and the `LuaModTeardownReason` enum
  (`Unload`/`Reload`/`Quarantine`).
- **Test seams are real.** ~96 test files define `Fake*`/`Stub*`/`InMemory*` doubles or
  implement `ILlmClient`/`IAiOrchestrationService` directly — the interfaces exist to be
  substituted, not incidentally. The Null-Object pattern is used consistently for safe
  defaults (`NullAuditLog`, `NullLog`, `PassthroughContentFilter`, `NullToolExecutionNotifier`,
  `NullInstanceBackingBinder`, `NullLuaModSourceStore`).
- **Decorator seams where they matter.** `QueuedAiOrchestrator` wraps `AiOrchestrator`
  (queueing split out of the pipeline) and `LoggingLlmClientDecorator` wraps `ILlmClient` —
  cross-cutting concerns kept out of the core classes.
- **`ToolExecutionPolicy` is NOT a god-class** despite its size (1892 LOC): it has a single
  cohesive responsibility (duplicate detection + consecutive-error tracking + notifier
  wrapping) deliberately shared by the streaming and non-streaming tool-calling clients.
- **No Rbx-surface regression.** `require` is *explicitly removed*
  (`LuaCsSecureEnvironment.cs:241 RemoveGlobal(state, "require")`), and no NEW bespoke Lua
  globals were found beyond the already-documented stopgap set.

---

## Findings (ranked by severity)

### HIGH

**H1 — `CoreAiChatPanel` is a 3636-line god-view (SRP).**
`Assets/CoreAiUnity/Runtime/Source/Features/Chat/CoreAiChatPanel.cs` is a single,
non-`partial` `MonoBehaviour` with ~51 fields and one class body. It concurrently owns at
least a dozen responsibilities: UIDocument/PanelRenderer lifecycle, *embedded-host* mode
(`CreateEmbedded`/`BuildEmbeddedChatTree`), agent switching (`TryBuildAgentDropdown`), API
profile switching (`TryBuildApiProfileControls`/`OnApiProfileChanged`), example prompts,
responsive/mobile sizing, WebGL keyboard config, streaming render + render-cap, think-block
filtering (`_thinkFilter`), cursor-gating of input, per-role transcript caching
(`_roleTranscriptCache`), and collapse/FAB state. This is the worst SRP offender in the
codebase.
- *Violates:* single responsibility / no god-objects; also weakens testability (logic is
  trapped inside a `MonoBehaviour`).
- *Minimal refactor (do not implement):* extract collaborators that need no `MonoBehaviour`
  base and unit-test them — a `ChatTranscriptRenderer` (bubbles/streaming/render-cap +
  `ThinkBlockStreamFilter`), a `ChatRoutingUiController` (agent + API-profile dropdowns; an
  `ICoreAiRoutingUiController` seam already exists), and a `ChatInputGate` (cursor/hotkey
  rules — `IsChatInputAllowed` is already `static internal` and testable). Leave the panel as
  a thin view that wires these. Splitting into `partial` files is a stopgap that improves
  readability but not SRP/testability.

### MEDIUM

**M1 — Service-locator (scene walk + manual `Container.Resolve`) has leaked past the
composition edge.**
The pattern `UnityEngine.Object.FindAnyObjectByType<CoreAILifetimeScope>(...)` followed by
`scope.Container.Resolve(typeof(...))` is duplicated across the API facade **and** inside
feature/application code:
`Api/CoreAi.cs` (lines ~199, 240, 285, 672, 722, 803, 820, 829),
`Api/CoreAiBackend.cs` (~578–669),
`Features/Chat/CoreAiChatService.cs:52 TryCreateFromScene()`,
`Features/Diagnostics/TokenBudgetRuntimeSource.cs:75`.
An ambient facade (`CoreAi.cs`) doing this at the very edge for un-DI'd user MonoBehaviours is
a defensible convenience; the same scene-walk re-implemented inside `CoreAiChatService` and
`TokenBudgetRuntimeSource` is service-locator leaking into logic, with a copy-pasted
try/Resolve/catch ladder in each.
- *Violates:* "DI at the edges; no service-locator in logic"; DRY.
- *Minimal refactor:* introduce one seam (e.g. `ICoreAiSceneScope`/`CoreAiSceneScopeLocator`)
  that performs the `FindAnyObjectByType` + resolve-with-fallback once; have
  `CoreAiChatService`/`TokenBudgetRuntimeSource` receive resolved dependencies via ctor (they
  already accept them as optional args) and let only the facade call the locator.

**M2 — Two oversized infrastructure adapters trend toward god-classes.**
- `MeaiLlmClient` — 2766 LOC, ~49 methods
  (`.../Source/Features/Llm/Infrastructure/MeaiLlmClient.cs`). One `ILlmClient` impl, but it
  bundles: static HTTP transport construction (`CreateHttp`), the streaming fan-out loop
  (`LiveUiStreamMaxCharsPerChunk`), hybrid tool-call JSON accumulation/extraction
  (`HybridToolJsonHeldTailMaxChars`), MEAI wrapping, and role-id state.
- `LuaCsModRuntime` — 2112 LOC (`.../CoreAIMods/Runtime/LuaExecution/LuaCsModRuntime.cs`). One
  `ILuaModRuntime` impl carrying lifecycle, per-frame tick, hook dispatch, timers, event
  queue, cross-mod export bridge (`mods_export/get/call`), per-mod store, quarantine policy,
  source-store persistence, and version history.
- *Violates:* SRP (many reasons to change per class).
- *Minimal refactor:* for `MeaiLlmClient`, push transport creation into the existing
  `LlmEndpointClientFactory`/`FetchSseOpenAiTransport` and move hybrid-JSON parsing behind the
  existing `LlmToolCallTextExtractor`, leaving a thinner streaming adapter. For
  `LuaCsModRuntime`, extract a `ModPersistenceCoordinator` (source-store + version history)
  and a `CrossModExportBridge`; keep the runtime as tick/dispatch only. Both are adapters, so
  this is lower-risk than H1.

**M3 — The bespoke Lua surface remains the known stopgap (tracked debt, not a regression).**
`LuaCsModRuntime.cs` registers `hooks_on`/`hooks_every`, `mods_export`/`mods_get`/`mods_call`/
`mods_list_exports`, `store_set`/`store_get`, `events_emit` (see `RegisterVarArgs` at
lines ~1301–1479). This is exactly the documented pre-MVP substitute for
`require(ModuleScript)` + `BindableEvent`, which are not yet implemented (`require` is removed
in `LuaCsSecureEnvironment.cs:241`). No *new* non-Roblox globals were found beyond the
documented set, so this is correctly-scoped debt — flagged only so it stays on the MVP
burn-down and does not accrete further.

### LOW

**L1 — Naming-convention drift: `Roblox*` identifiers inside the `Rbx*` assemblies.**
The stated rule is "identifiers use Rbx not Roblox," but ~29 references to `Roblox`-prefixed
types remain in `Assets/CoreAIMods/Runtime/RbxApi/**`: `RobloxSpace`, `RobloxWorldHost`,
`IRobloxCameraRig`, `RobloxCameraFollower`, `RobloxApiStubException`, `RobloxJson`. The
assemblies themselves were already renamed to `CoreAI.RbxApi.*`, so this is an incomplete
identifier rename. *Refactor:* finish the `Roblox*`→`Rbx*` rename (or explicitly whitelist
`RobloxSpace`/`RobloxWorldHost` if "Roblox coordinate space" is intended domain vocabulary).

**L2 — Stale docs claim a dual-VM setup that no longer exists.**
`LuaCsModRuntime.cs` (class summary, ~lines 29–33) and `LuaCsAiEnvelopeProcessor.cs:16`
describe the Lua-CSharp runtime as the "ADDITIVE counterpart" that "coexists" with a MoonSharp
`LuaModRuntime` where "both VMs coexist." That MoonSharp runtime is gone — there is no
`LuaModRuntime.cs`, no `using MoonSharp`, and every remaining `MoonSharp` token is a comment.
*Violates:* keep-docs-current. *Refactor:* rewrite those summaries to describe the single
current VM and drop the migration framing.

**L3 — Ambient mutable statics used as fallbacks.**
`Log.Instance` (`Core/Features/Logging/ILog.cs`, `volatile` with a public setter),
`CoreAISettings.Instance`, `CoreAISettingsAsset.Instance`, and `GameLoggerUnscopedFallback.Instance`
are process-wide mutable/ambient state. Most `static ... Instance` occurrences are benign
Null-Object defaults, but these four are real ambient globals that can diverge from the
DI-registered instances (e.g. `CoreAiBackend.cs:591` falls back to `Log.Instance`). Low impact
today; worth constraining so nothing in logic reads them instead of an injected dependency.

**L4 — Copy-paste XML-doc bug.** `MeaiLlmClient.cs:37–41`: the `const int
LiveUiStreamMaxCharsPerChunk` field carries a `<summary>` reading "Initializes a new instance
of the current component." — a mis-pasted ctor doc. Comment-quality only.

**L5 — Stale generated project files in repo root.** Both `CoreAI.RbxApi.*.csproj` and the
older `CoreAI.RobloxApi.*.csproj` exist at the root while only the `RbxApi` asmdefs remain.
The `RobloxApi` csprojs are leftover generated artifacts from the rename (no matching
`.asmdef`); harmless but confusing — should be regenerated/gitignored.

---

## Summary table

| ID | Severity | Area | Principle |
|----|----------|------|-----------|
| H1 | HIGH | `CoreAiChatPanel` 3636-LOC view | SRP / testable seams |
| M1 | MEDIUM | Scene-locator in `CoreAiChatService`, `TokenBudgetRuntimeSource`, API facade | DI at edges / no service-locator |
| M2 | MEDIUM | `MeaiLlmClient`, `LuaCsModRuntime` adapters | SRP |
| M3 | MEDIUM | Bespoke Lua globals (stopgap) | Rbx-1:1 surface (tracked debt) |
| L1 | LOW | `Roblox*` identifiers in `Rbx*` asmdefs | Naming convention |
| L2 | LOW | Stale dual-VM docs | Keep-docs-current |
| L3 | LOW | Ambient mutable statics | No static leakage into logic |
| L4 | LOW | Copy-paste XML doc | Comment quality |
| L5 | LOW | Stale `RobloxApi.*.csproj` | Repo hygiene |

*Auditor's note:* the load-bearing Clean-Architecture invariants (engine-free core, inward
dependencies, consumer-owned ports, DI at edges, typed errors, real test doubles) all hold
under inspection. The debt is real but peripheral and mostly mechanical to pay down; H1 is the
only finding that touches a lot of live logic.
