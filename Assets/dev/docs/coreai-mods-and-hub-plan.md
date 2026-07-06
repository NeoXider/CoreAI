# CoreAIMods extraction + CoreAI Hub (pages) — implementation plan

Status: active. This document is the repo-side plan for (1) extracting the whole Lua layer into a
separate `com.neoxider.coreaimods` package, (2) the extensible **CoreAI Hub** page system, and
(3) mod features (bundled/updatable mods, categories). Companion spec: `Docs/coreai-mod-system.md`.

## Goals

1. **Dependency inversion.** All Lua (sandbox, `execute_lua`/formulas, mod runtime, stores, bindings,
   Lua tools, Lua Modding skill) moves into a new package `com.neoxider.coreaimods` that DEPENDS ON
   CoreAI. `CoreAI.Core`/`CoreAI.Source` end up with **zero MoonSharp / zero Lua-execution** references.
   CoreAI keeps the generic tool/agent/skill framework and a thin MoonSharp-free Lua-semantic surface
   (version store, prompt-composer repair, `EnableLuaOnWebGl`).
2. **Optionality = package presence.** No `COREAI_LUA`/`COREAI_NO_LUA` flag. No package → no Lua code
   → clean compile. Inside CoreAIMods the `#if COREAI_HAS_MOONSHARP && !COREAI_NO_LUA` guards are
   dropped (the package only exists with MoonSharp).
3. **Extensible CoreAI Hub (pages).** One window with pages (Chat, Settings, Statistics, Mods, …) plus a
   public **page-registry API** so C# modules AND Lua mods can add a page at runtime.
4. **Bundled, updatable mods** installed on first run (some active by default), categories as a tree,
   authored by both the AI and the player.
5. **Multiplayer-ready (host-authoritative)** seams added now, network layer built later.

## Package boundary

New package `Assets/CoreAIMods/` — assemblies `CoreAI.Mods` (runtime), `CoreAI.Mods.Editor`,
`CoreAI.Mods.Tests`. `CoreAI.Mods` references CoreAI.Core, CoreAI.Source, MoonSharp, VContainer,
MessagePipe(.VContainer), UniTask, undream.llmunity.Runtime, TextMeshPro, UnityEngine.UI, InputSystem.

**Moves into CoreAIMods:**
- `Assets/CoreAI/Runtime/Core/Sandbox/*` (SecureLuaEnvironment, LuaApiRegistry, guards).
- `Assets/CoreAI/Runtime/Core/Features/LuaExecution/*` (LuaModRuntime, LuaModsLlmTool, LuaLlmTool,
  LuaModManifest, LuaModHeader, IGameLuaRuntimeBindings, ILuaModSourceStore/Store, LuaCapabilities,
  LuaAiEnvelopeProcessor, LuaGenerationRateLimiter, LuaLogicSlots, LuaModAutoRepairPolicy, …).
- `Assets/CoreAiUnity/Runtime/Source/Features/Lua/**` EXCEPT the shared version stores (see keep-list).
- World Lua bindings `Features/World/Infrastructure/*Lua*.cs`, `CoreAiWorldQueryLuaBindings`.
- Lua tool + skill registrations from `WorldCommandsInstaller`/`CorePortableInstaller`/
  `CoreServicesInstaller` → new `CoreAiModsLifetimeScope`. Lua Modding skill text + `Resources/AgentSkills/LuaModding`.
- `LuaScriptedImporter` → CoreAI.Mods.Editor. All `*Lua*` tests → CoreAI.Mods.Tests. `link.xml` (MoonSharp
  IL2CPP/WebGL preserve) → CoreAIMods.

**Stays in CoreAI (MoonSharp-free):**
- `ILuaScriptVersionStore` + `LuaScriptVersionRecord` + `FileLuaScriptVersionStore` +
  `FileDataOverlayVersionStore` (shared with skills authoring). Carve these OUT of the `Features/Lua/**`
  bulk move.
- `IAiGameCommandSink` and the world-command system (only the Lua bindings on top move).
- Generic framework: `ILlmTool`, `SkillSet`, `AgentBuilder`, `AgentMemoryPolicy`, orchestrator.
- Lua-semantic-but-MoonSharp-free: `AiPromptComposer` Lua-repair, `AiOrchestrator` repair fields,
  `EnableLuaOnWebGl`.
- Delete the no-op `#else` fallbacks (`CoreDefaultLuaRuntimeBindings` no-op, WorldCommandsInstaller `#else`)
  — package absence replaces them.

## DI inversion — child LifetimeScope

CoreAIMods ships `CoreAiModsLifetimeScope : LifetimeScope`; the user makes it a **child** of
`CoreAILifetimeScope` (hierarchy or `parentReference`; Mods→CoreAI reference is allowed). The child
resolves parent singletons (`IAiGameCommandSink`, `AgentMemoryPolicy`, `HubPageRegistry`,
`ICoreAISettings`, `ILuaScriptVersionStore`, logger), registers all Lua/mod services, and in its build
callback reaches UP to add `execute_lua`/`manage_mods` tools, the Lua Modding skill, and the Mods page.
A `CoreAI → Setup → Add Mods` helper + docs cover wiring. **Timing risk:** ensure Lua tools are
registered before the first agent turn (parent-invoked installer, or lazy per-turn tool re-query, or a
"policy sealed" barrier) — add an EditMode test asserting the Programmer role has `execute_lua`/
`manage_mods`/`Lua Modding` after both scopes build.

## CoreAI Hub — pages (UI Toolkit) + registry

In **CoreAI**:
- `IHubPage` — `PageId`, `DisplayName`, `Icon`, `VisualElement CreatePageContent()`, lifecycle hooks.
- `HubPageRegistry` — thread-safe `Dictionary<pageId, Func<IHubPage>>` + `PageRegistered/Unregistered`
  events (last-writer-wins by id; no priority ordering — keep it simple).
- `CoreAiHubWindow` (UIDocument) — tab bar + page container; rebuilds tabs on registry events; lazy page
  creation. Built-in pages: **Chat** (reuse existing `CoreAiChatPanel`, UI Toolkit), **Settings**
  (backend config), **Statistics** (orchestration metrics + token budget). Semi-transparent page style;
  drag not required.
- Note: two chat UIs exist — `CoreAiChatPanel` (UI Toolkit, `CoreAiChat.uxml`) and `InGameChatPanel`
  (uGUI/TMP). Hub uses UI Toolkit reusing `CoreAiChatPanel`; verify which chat the target scenes use and
  that embedding is clean before scoping Phase 3 as cheap.

In **CoreAIMods**:
- **Mods page** — an `IHubPage` registered into the parent `HubPageRegistry`: category tree; buttons
  Add / Paste (`GUIUtility.systemCopyBuffer`) / per-mod Copy/Edit/Enable-Disable/Delete/Import/Export/
  Update; editor sub-panel. List rebuilds only on `ModSourceLoaded/Unloaded` + user actions.
- **Lua page binding** `coreai_ui_register_page(id, spec)` via `CoreAiUiLuaRuntimeBindings`. **Declarative
  widget schema, NOT VisualElement-from-Lua:** `render()` returns a Lua table of widgets
  (`{type="label"/"button"/"slider", …, on_click="id"}`); C# builds/diffs the VisualElement; callbacks
  dispatch by string id. Safe (untrusted Lua never touches the UI thread), cheap (rebuild on state
  change), and serializable → replicable for MP. Closure-render only behind Full/host-only tier, if ever.

## Mod features (inside CoreAIMods)

- `LuaModHeader.Parse` (`@coreai` frontmatter) + tests.
- `IBundledModSource` + `ResourcesBundledModSource`; `BundledModSeeder` (install/update/skip by
  version + FNV-1a hash) + matrix tests; seeder runs before `RehydrateFromStore`.
- **Load order in the seeder** (numeric prefix or hard-dep edges) — bundled mods with `mods_call`/
  `mods_get` deps must not flake on arbitrary `List()` order. Not a later "extension".
- **Version model fix:** semver (header/`SeededVersion`) is authoritative for updates; the runtime
  revision COUNT is an internal audit number — do not conflate them in `Version`.
- **Hot-reload state contract:** `ReloadMod` drops Lua locals; only `store_*` survives. Rule: durable
  state lives in `store_*` (document for AI/player authors), or add `on_reload(old_state)`.
- Categories via `Category` in the manifest → tree; `manage_mods` honors category.
- Bundled defaults `Resources/CoreAIMods/*.lua` with `@coreai` headers (`first_person` off + a few on).

## Multiplayer-ready seams (add NOW, cheap; expensive later)

- **`IModClock` / fixed-step tick** — timers off an integer tick index at a fixed rate (not
  `Time.deltaTime`); purge `DateTime.UtcNow` from state-affecting paths. Today's tick uses wall-clock
  per-client deltas → non-deterministic.
- **`store_set` via a replicable command DTO** (host-authoritative); SP short-circuits locally. Today it
  writes directly to a local store, bypassing `IAiGameCommandSink` → would desync in MP.
- **Host owns the active-mod list** (`BundledModSeeder`/`RehydrateFromStore` host-driven in MP). Hub pages
  flagged local/cosmetic (Settings/Statistics — not replicated) vs mod-derived (host-authoritative).
- Mods emit world mutations as **commands** via `IAiGameCommandSink` (stays in CoreAI, replicated), never
  mutate the client directly. **Full-tier `unity_*`** is normatively host/singleplayer-only.
- **native/Lua boundary rule:** Lua declares/reacts to discrete events; C# owns per-frame hot loops. Mods
  express "rotate at N°/s" as a command/declaration, not a `hooks_every` spinning a transform. Keep
  `IGameLuaRuntimeBindings`/`ILuaExecutor` VM-agnostic as the VM-swap seam.
- Known determinism gap: round-robin event dispatch + drop-oldest — do not claim "deterministic" yet.

## VM choice (MoonSharp vs Lua-CSharp) — next stage, not now

Do NOT swap the VM during the extraction. After the move, in a **separate folder**: download
nuskey8/Lua-CSharp, run an Editor correctness + performance benchmark, then a WebGL check, and compare
both sandboxes (MoonSharp vs Lua-CSharp) on **security of untrusted code** (decisive), performance, and
IL2CPP/WebGL compatibility. Keep `IGameLuaRuntimeBindings`/`ILuaExecutor` VM-agnostic so the swap, if
chosen, is internal to CoreAIMods.

## Phases

0. **Optimization (independent, partly done):** F9 panel cache-by-events; version-store mtime cache (done).
1. **Extraction (now):** create CoreAIMods skeleton; move Lua files preserving `.meta` GUIDs (same
   namespaces → no reference rewrites); delete no-op `#else` stubs; strip `#if` guards inside CoreAIMods;
   move Lua DI into `CoreAiModsLifetimeScope`; update asmdef graph (Demos/tests +`CoreAI.Mods`, remove
   MoonSharp from core); move `link.xml`; verify `.meta` renamed not recreated; compile + green tests +
   MoonSharp-free core build. Separate PR.
2. **Lua-VM comparison (separate folder):** Lua-CSharp Editor + WebGL benchmark vs MoonSharp.
3. **Mod features in CoreAIMods:** header/seeder/sources/categories/load-order/version-fix/hot-reload.
4. **Hub + page registry (UI Toolkit):** built-in pages + Mods page + Lua declarative page binding.
5. **PR2/extensions:** StreamingAssets/Addressables (deferred until needed), world-event contract.
6. **Close-out:** three-package lockstep version bump, English docs, full EditMode + targeted PlayMode.

## Migration hazards (checklist)

- RoslynRefactorNeo does NOT edit asmdefs, scene/prefab YAML, or `.meta`. Since namespaces are unchanged,
  a same-namespace file move needs only asmdef reference updates — `git mv` (file + `.meta`) preserves
  GUIDs natively, so `git mv` is the primary mover; RoslynRefactor is optional.
- **Verify every `.meta` is renamed/moved, not deleted+recreated** (a new GUID breaks scenes/prefabs) —
  the single highest-value check. Runtime Lua MonoBehaviours (`LuaCoroutineRunner`, `CoreAiLuaModAutoRepair`)
  are spawned at runtime, not scene-placed, so YAML risk is low; demo controllers stay in Demos.
- `internal` members are not visible across the new assembly boundary even with identical namespaces —
  grep for cross-assembly `internal` access.
- Remove MoonSharp ref + `COREAI_HAS_MOONSHARP` versionDefine from `CoreAI.Core`/`CoreAI.Source` and the
  7 test asmdefs; add `CoreAI.Mods` to `CoreAI.Demos` and test asmdefs.
- Hidden "kept" files with MoonSharp: `MeaiLlmClient` (`LuaLlmTool` alias — check live/dead),
  `CoreAiChatExternalDriver.RunLuaDiag()` (direct MoonSharp) → move/delete.
- Add a WebGL/IL2CPP smoke WITH CoreAIMods present to close-out.

## Verification

Compile clean (incl. MoonSharp-free core build); `*Lua*` tests green after the move; `scan-identifiers`/
grep confirms zero MoonSharp in core; page registry works for a C# and a Lua page; seeding install/update
matrix; mod-UI frame time flat vs mod count; download overlay unchanged.
