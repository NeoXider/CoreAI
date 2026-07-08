# CoreAI Lua-Mods + Hub — Optimization / Redundancy Review

Review-only audit (no code changed) of the recently-built Lua-CSharp mod runtime, bindings, installer,
Hub window/pages, auto-repair, and the mods demo controllers. Every finding below was verified against
source; file:line citations are real. "Safe now" vs "after MoonSharp purge" is called out per item.

Scope audited:
`Assets/CoreAIMods/Runtime/**`, `Assets/CoreAIHub/Runtime/**`,
`Assets/CoreAI.Demos/LiveMechanicsMods/Scripts/**`, `Assets/CoreAI.Demos/MiniRpg/**`.

Context: there are two Lua backends — MoonSharp (`Lua*` classes) and Lua-CSharp (`LuaCs*` classes).
**Production wiring is 100% Lua-CSharp** (`CoreAiModsInstaller.RegisterCoreAiMods` references zero MoonSharp
runtime types). The MoonSharp side compiles today (asmdef references `MoonSharp.Interpreter` unguarded,
`CoreAI.Mods.asmdef:8`) but is reached only by tests + `#if COREAI_HAS_MOONSHARP`-guarded demos. Its deletion
is already planned ("Commit 2 purge"); this report notes what the purge cleans up vs. what is separately
removable now.

---

## Ranked findings (highest impact first)

| # | Category | Finding | Safe? |
|---|----------|---------|-------|
| 1 | Perf | Demo `OnGUI` calls `ILuaModRuntime.ListMods()` + allocates a fresh `GUIStyle` per label every frame | now |
| 2 | Dead | `LuaModRuntimeTicker` + `LuaModEventEmitted` (+ 2 broker registrations) — a fully dead publish chain, zero subscribers | now |
| 3 | Perf | Full-tier `Resolve(instanceId)` does `Resources.FindObjectsOfTypeAll<GameObject>()` + O(n) scan on **every** `unity_*` call (both VMs) | now |
| 4 | Redundancy | `HubModsDemoBinder` duplicates the shipped `CoreAiModsHubBinder`; three overlapping Hub binders total | now |
| 5 | Dead | `LoggingLuaExecutionObserver` never instantiated; `ILuaExecutionObserver` registration hook unused; `DataOverlayValidator` option never assigned | now |
| 6 | Awkward API | ~7 demo controllers (+ auto-repair) hand-`Find`+`Resolve` the mods scope — ~70-90 lines of copy-paste; injection exists but is unused | now |
| 7 | Perf | `HubModsPage.RefreshTree` rebuilds the whole tree **and re-parses every mod header** on every keystroke; `HubStatisticsPage` rebuilds role rows every second | now |
| 8 | Awkward API | `ChatPromptButtonsController` reflects into `CoreAiChatPanel`'s private `InputField` — no public "set input text" API | now |
| 9 | Over-abstraction | `HubModServiceBase` (418 lines) + two ~90-line adapters; one dead pre-purge, collapses to a single class after | mixed |
| 10 | Dead | Demo `Configure(...)` public methods (2) never called; several Hub `HubModRecord` fields written-never-read | now |
| 11 | Simplification | `IFullLuaAccessBlacklistPolicy` / `IDataOverlayPayloadValidator` — single no-op impls with unreachable wiring | now |
| 12 | Redundancy | `HubPageWidgets` vs `HubModWidgets` duplicate the same colors/helpers | now |
| 13 | Awkward API | `IHubPage.CreatePageContent` is a `Func<object>` property; every page repeats 3 empty lifecycle methods | now |
| 14 | Minor | `OneOffCapabilities==Capabilities`, `SetPlaceholder` misnamed, `RichLabel()` re-declared 3×, per-spawn `Material` alloc, stale doc-comments | now |

---

## 1. Dead / unneeded code

### 1.1 `LuaModRuntimeTicker` + `LuaModEventEmitted` — a fully dead publish chain *(safe now)*
- `Assets/CoreAIMods/Runtime/Infrastructure/LuaModRuntimeTicker.cs:14` — `LuaModRuntimeTicker : ITickable`.
- The installer wires **only** the MonoBehaviour driver:
  `Assets/CoreAIMods/Runtime/Composition/CoreAiModsInstaller.cs:108` —
  `tickerGo.AddComponent<LuaModRuntimeTickDriver>().Initialize(runtime);`. There is **no** `ITickable` /
  `RegisterEntryPoint<ITickable>` registration anywhere in the Mods assembly. `LuaModRuntimeTickDriver.cs:8-12`
  even documents *why* the old ITickable path was abandoned ("verified live: ITickable unresolved … timers frozen").
- The Ticker is referenced only by itself + two EditMode tests, and it binds the **MoonSharp** `LuaModRuntime`
  concrete type, so it is doubly obsolete.
- The Ticker is also the **only** publisher of `LuaModEventEmitted`
  (`LuaModRuntimeTicker.cs:47` → `_eventPublisher?.Publish(...)`). That message struct
  (`Assets/CoreAIMods/Runtime/Messaging/LuaModEventEmitted.cs:6`) has **zero subscribers** anywhere
  (`grep ISubscriber<LuaModEventEmitted>` is empty), yet is registered as a MessagePipe broker in two core
  installers: `CoreServicesInstaller.cs:40` and `GlobalMessagePipeMinimalBootstrap.cs:35`. Demos consume mod
  events through the direct `ILuaModRuntime.ModEventEmitted` C# event instead (e.g.
  `WaveAutoBattlerModsDemoController.cs:85`).
- **Recommend:** delete `LuaModRuntimeTicker.cs`, `LuaModEventEmitted.cs`, and the two `RegisterMessageBroker<LuaModEventEmitted>`
  lines. The `LuaModRuntimeTickDriver` already ticks the runtime; nothing is lost except a mod-report log line
  no one reads (see 1.2). **Safe now** — only a MoonSharp EditMode test references the Ticker.

### 1.2 `LoggingLuaExecutionObserver` never instantiated; observer seam always null-object *(safe now)*
- `Assets/CoreAIMods/Runtime/Infrastructure/LoggingLuaExecutionObserver.cs:7` — `new LoggingLuaExecutionObserver`
  appears **nowhere**. Nothing registers `ILuaExecutionObserver` (`As<ILuaExecutionObserver>()` / `Register(...ILuaExecutionObserver)`
  = 0 hits), so the installer's `ExecutionObserver = c.ResolveOrDefault<ILuaExecutionObserver>()`
  (`CoreAiModsInstaller.cs:72`) always resolves null and the factory falls back to `NullLuaExecutionObserver`
  (`LuaCsModRuntimeFactory.cs:170`).
- **Recommend:** delete `LoggingLuaExecutionObserver`. Keep `ILuaExecutionObserver` + `NullLuaExecutionObserver`
  only if a host is expected to wire logging later; otherwise the whole observer seam is inert and could be
  dropped. **Safe now** (VM-agnostic, purge-independent).

### 1.3 `LuaCsModStackOptions.DataOverlayValidator` — option declared, passed, never assigned *(safe now)*
- `Assets/CoreAIMods/Runtime/Infrastructure/LuaCsModRuntimeFactory.cs:42` declares the field and `:150`
  passes it into the bindings, but there is no `DataOverlayValidator =` assignment anywhere — it is always null
  and the bindings use their internal `DefaultDataOverlayPayloadValidator`.
- **Recommend:** drop the unused option field (keep the interface + default). **Safe now.**

### 1.4 Dead demo `Configure(...)` methods *(safe now)*
- `ChatPromptButtonsController.cs:59` `Configure(string, Rect, PromptButton[])` and
  `LiveMechanicsModsChatPersistenceController.cs:104` `Configure(string, string, Rect, bool)` — `grep '\.Configure('`
  over `Assets/CoreAI.Demos` returns **zero callers**. All config is via serialized fields.
- **Recommend:** delete both. **Safe now.**

### 1.5 Hub `HubModRecord` fields written but never read *(safe now)*
- `Assets/CoreAIMods/Runtime/Hub/HubModServiceBase.cs:88` `StoredActive = manifest.Active` — field declared at
  `IHubModService.cs:46`, never read.
- `HubModServiceBase.cs:86` `Origin = manifest.Origin` — the record field is never read (the `:264`
  `manifest.Origin = existing.Origin` operates on the manifest, not the record).
- `HubModServiceBase.cs:319-321` enriches `record.Author`, but the editor reads `header.Author` directly
  (`HubModEditorPage.cs:194`), never `record.Author`.
- **Recommend:** either surface these in the row (author badge / stored-disabled marker) or drop the fields.
  **Safe now** — `HubModRecord` is used only inside the Hub folder.

---

## 2. Redundancy

### 2.1 `HubModsDemoBinder` duplicates the shipped `CoreAiModsHubBinder` *(safe now)*
- Package component `Assets/CoreAIMods/Runtime/Hub/CoreAiModsHubBinder.cs:37-53` and demo component
  `Assets/CoreAI.Demos/LiveMechanicsMods/Scripts/HubModsDemoBinder.cs:42-64` do the same job: find
  `CoreAiModsLifetimeScope`, `container.Resolve<LuaCsModRuntime>()`, `ResolveOrDefault<ILuaModSourceStore>()`,
  build the `LuaCapabilities` grant, `HubModsPages.Register(...)`. The package one is the better design —
  additive (`window.Registry ?? new HubPageRegistry()`), the demo one overwrites `window.Registry` and
  re-registers the built-in About/Settings/Statistics pages by hand.
- More broadly, three components build a registry and assign `window.Registry`: `CoreAiHubDemo`, `HubModsDemoBinder`,
  `CoreAiModsHubBinder`.
- **Recommend:** let the Hub's own bootstrap register About/Settings/Statistics, keep `CoreAiModsHubBinder` as the
  single "light up Mods" component, and delete `HubModsDemoBinder` (confirm the Hub bootstrap runs in the demo
  scenes first). **Safe now.**

### 2.2 `LuaModRuntimeHubService` (MoonSharp adapter) is dead *before* the purge *(safe now)*
- `Assets/CoreAIMods/Runtime/Hub/LuaModRuntimeHubService.cs:15` is constructed only by the
  `HubModsPages.Register(HubPageRegistry, LuaModRuntime, …)` overload (`HubModsPages.cs:43`), and **that overload
  has no callers** — both binders resolve `LuaCsModRuntime` and hit the LuaCs overload (`HubModsPages.cs:60`).
- **Recommend:** delete `LuaModRuntimeHubService.cs` and the MoonSharp `HubModsPages.Register` overload; drop the
  "either runtime" claim from the `HubModsPages` doc. **Safe now** (unreferenced), and forced by the purge anyway.

### 2.3 `HubPageWidgets` vs `HubModWidgets` — duplicated colors/helpers *(safe now, cosmetic)*
- `Assets/CoreAIHub/Runtime/HubPageWidgets.cs:12-14` and `Assets/CoreAIMods/Runtime/Hub/HubModWidgets.cs:13-15`
  declare byte-identical `Accent`/`Text`/`Muted` colors, plus duplicate `MakeTitle`/`MakeNote`. The duplication is
  deliberate (`HubModWidgets.cs:7-10` notes it avoids depending on the internal Hub helper) but the two already drift.
- **Recommend:** make `HubPageWidgets` public in `CoreAI.Hub.UI` (which `CoreAI.Mods` already references,
  `CoreAI.Mods.asmdef:7`) and delete the duplicated color/title/note code. **Safe now.**

### 2.4 Standalone chat is **not** redundant with the Hub chat tab *(no action — clarification)*
- `HubChatPage` does not reimplement chat: `HubChatPage.cs:100` calls `CoreAiChatPanel.CreateEmbedded(...)`,
  reusing the same `CoreAiChatPanel` the standalone demos use. The demo scenes deliberately omit the Hub chat
  tab (`HubModsDemoBinder.cs:53` — "the scene's standalone chat drives the tasks"). The chat UI is shared; only
  the *mod-manager* CRUD overlaps (see 2.5). No change needed for chat.

### 2.5 Two mod-manager UIs: `HubModsPage` (UITK) vs the demo IMGUI panel *(after purge)*
- `HubModsPage.cs:10` states it "replaces the old IMGUI mod panel", yet
  `LiveMechanicsModsChatPersistenceController` still ships a full OnGUI mod manager (list/edit/activate/deactivate/
  forget + source editor; `OnGUI` at `:321`). They overlap on CRUD, but the IMGUI one uniquely owns scene
  persistence + autoload (`AutoloadSavedMods`, `:171`).
- **Recommend:** keep the persistence/autoload policy, drop the IMGUI CRUD in favor of the Hub Mods tab. The
  controller is `#if COREAI_HAS_MOONSHARP && !COREAI_NO_LUA`-guarded and carries autoload logic that must move
  first — **after the purge / not a pure delete.**

---

## 3. Awkward / inconvenient APIs

### 3.1 Every consumer hand-`Find`s the scope and `Resolve`s services — injection exists but is unused *(safe now)*
- The same ~13-line block (`if scope==null → FindFirstObjectByType`; null-guard `enabled=false`;
  `FindFirstObjectByType<CoreAiModsLifetimeScope>()`; `luaContainer` ternary; `container.Resolve<T>()`) is
  copy-pasted across **7 demo controllers** — e.g. `WaveAutoBattlerModsDemoController.cs:63-81`,
  `LiveMechanicsModsChatPersistenceController.cs:130-148`, `LiveMechanicsDemoController.cs:56-76`, plus
  `FullModeModDemoController`, `LuaPlatformExampleController`, `LuaModsDemoController`, `ModdableUnitsDemoController`
  — and again in `Presentation/CoreAiLuaModAutoRepair.cs:74-109`. ~70-90 lines of duplication.
- The scene already sets `autoInjectGameObjects: []` on both scopes (`MiniRpgModsDemo.unity:383`), so the VContainer
  injection channel exists and is simply empty.
- **Recommend:** register these controllers in `autoInjectGameObjects` and use
  `[Inject] void Construct(ILuaModRuntime, LuaCsLogicSlots, …)`, or add one shared helper
  (`CoreAiModsDemoScope.TryResolve(...)` / a `CoreAiModsDemoBehaviour` base exposing `LuaContainer`). Deletes both
  the `Find` and the `Resolve`. **Safe now** (helper must carry the callers' `COREAI_HAS_MOONSHARP && !COREAI_NO_LUA` guard).

### 3.2 `ChatPromptButtonsController` reflects into a private `CoreAiChatPanel` field *(safe now, needs a framework method)*
- `ChatPromptButtonsController.cs:140-153` — `typeof(CoreAiChatPanel).GetField("InputField",
  BindingFlags.Instance | BindingFlags.NonPublic)` then `input.value = text`. Target is
  `CoreAiChatPanel.cs:74 protected TextField InputField`. There is a public `SubmitMessageFromExternalAsync`
  (used as fallback at `:133`) but no public "set input text without submitting" API, so the demo string-reflects
  into another assembly's internals — a rename silently breaks it.
- **Recommend:** add `public bool TrySetInputText(string)` to `CoreAiChatPanel` and delete the reflection. This is a
  legitimate framework feature, not demo glue. **Safe now.**

### 3.3 `IHubPage.CreatePageContent` is a `Func<object>` property + per-page lifecycle boilerplate *(safe now)*
- `IHubPage.cs:22` — `Func<object> CreatePageContent { get; }`. Every page implements it as `=> BuildMethod`
  (`HubChatPage.cs:62` etc.) and the host does runtime type-checking to recover the `VisualElement`
  (`CoreAiHubWindow.cs:407-413`). The `object` return (UI-free core) is justified, but wrapping it in a `Func<>`
  *property* buys nothing over a plain method.
- Each page also repeats identical empty `OnActivated`/`OnDeactivated`/`OnDestroyed` + `PageId`/`DisplayName`/`Order`
  plumbing (`HubChatPage.cs:65-72`, `HubSettingsPage.cs:49-61`, `HubStatisticsPage.cs:60-72`, `HubAboutPage.cs:46-58`).
- **Recommend:** change the contract to a method `object CreatePageContent();`, and add an optional `HubPageBase`
  with virtual no-op lifecycle + stored id/name/order so a page overrides only the content builder. **Safe now**
  (core interface change touching all 6 pages).

### 3.4 Persistence controller abuses the version store as a KV flag *(safe now, larger change)*
- `LiveMechanicsModsChatPersistenceController.cs:303-315` persists an "active" boolean by writing `"1"`/`"0"` as
  fake Lua source into `ILuaScriptVersionStore` under a magic `__active__.` key. Mod source, active flag, and the
  "forget" tombstone all share one keyspace, disambiguated by string prefixes.
- **Recommend:** offer a small `IModPersistencePolicy`/enablement seam so the demo isn't inventing autoload
  semantics on top of an undo store. Lower priority.

### 3.5 `parentReference` wiring is a stringly-typed, scene-set link + a one-frame ordering band-aid *(acceptable; note)*
- `CoreAiModsLifetimeScope.cs:31-35` documents that parenting is via VContainer `parentReference` set in the scene,
  and that overriding `FindParent` would NRE. In `MiniRpgModsDemo.unity:382-383` the child serializes only
  `parentReference: { TypeName: CoreAI.Composition.CoreAILifetimeScope }` — a type-name string, no object reference,
  so it resolves the wrong scope if two CoreAI scopes ever coexist. Slot-declaring controllers rely on a
  `yield return null` in `Start` (`LiveMechanicsModsChatPersistenceController.cs:124-126`) to run first.
- **Recommend:** an explicit serialized parent-scope reference + an ordered `RegisterBuildCallback` for slot
  declaration would harden it. Not urgent — the current form is idiomatic VContainer. No negative-instanceID hack
  exists in C# (the only `GetInstanceID`-adjacent code is the Unity-6.5 `GetEntityId().GetHashCode()` id in the
  Full bindings, see 4.2).

---

## 4. Performance

### 4.1 Demo `OnGUI` allocates per label and calls `ListMods()` every frame *(safe now — highest ROI)*
- `OnGUI` runs ≥2×/frame (Layout+Repaint) plus per input event, multiplying every allocation.
- `WaveAutoBattlerModsDemoController.cs:438` `RichLabel()` returns `new GUIStyle(GUI.skin.label){...}` and is called
  at `:373,374,384,387,390,400,416` = **7 `GUIStyle` allocations per OnGUI pass** (~14/frame), plus more in the slot
  loop. Same pattern in `LiveMechanicsDemoController.cs:261` (~11/pass) and `ChatPromptButtonsController.cs:155` (2/pass).
- `WaveAutoBattlerModsDemoController.cs:401` calls `_mods.ListMods()` (a fresh list each call) inside OnGUI and
  interpolates a `$"* {mod.Id} caps=…"` string per mod every pass; same at `LiveMechanicsDemoController.cs:232`.
  `WaveAutoBattlerModsDemoController.cs:146` also allocates `_mods.EmitEvent("battle_tick", $"{_wave}:{...}")` every
  `Update` frame regardless of listeners.
- The project's own rule forbids this: `LiveMechanicsModsChatPersistenceController.cs:55-57` caches on
  `ModSourceLoaded/Unloaded` events and calls `EnsureStyles()` once (`:454-474`); Wave/LiveMechanics never adopted it.
- **Recommend (cheap, high-value):** cache one `GUIStyle` in a field; cache the mod list on events instead of calling
  `ListMods()` in OnGUI. Best long-term: migrate these panels (stat row, slot table, mod list, battle log — all
  event-driven data) to a retained UITK `UIDocument` and drop OnGUI entirely. This is the ~167ms-at-9-mods class of
  cost. **Safe now.**

### 4.2 Full-tier `Resolve(instanceId)` scans every loaded object on every `unity_*` call *(safe now)*
- `Assets/CoreAIMods/Runtime/Infrastructure/LuaCsFullUnityRuntimeBindings.cs:562-579` (and the byte-equivalent
  MoonSharp copy `CoreAiFullUnityLuaRuntimeBindings.cs:519-534`): `Resolve(int instanceId)` calls
  `Resources.FindObjectsOfTypeAll<GameObject>()` (allocates an array of **all** loaded GameObjects, including
  inactive/prefab/editor objects) then linearly compares `GetObjectId(...)`. This runs on **every** Full binding
  call — `unity_set_position`, `unity_get_member`, `unity_call`, etc. A mod that touches N objects per tick triggers
  N full-scene scans + allocations per tick. `ResolveUnityObject` (`:581`) has the same shape.
- **Recommend:** maintain a short-lived `Dictionary<int, GameObject>` cache per binding call-batch (or per tick,
  invalidated on scene change), or accept an object handle rather than re-resolving by id each call. Fix both VMs
  (the MoonSharp copy dies with the purge; fix the LuaCs one now). **Safe now.**

### 4.3 `HubModsPage.RefreshTree` rebuilds the tree + re-parses every mod header on each keystroke *(safe now)*
- `HubModsPage.cs:151-214` — `RefreshTree()` does `_treeScroll.Clear()` then re-allocates every `Foldout` + row.
  It fires on `ModsChanged` (`:400`), every toggle (`:314`), delete (`:329`), editor close (`:379`), **and every
  search keystroke** (`:119-123`). Each rebuild calls `_service.ListMods()`, which re-parses every mod's `@coreai`
  header via `EnrichFromHeader` (`HubModServiceBase.cs:117,286-333`) — so one keystroke = re-parse all mods +
  re-alloc all elements + lose foldout/scroll state.
- `HubStatisticsPage.cs:141-167` similarly `Clear()`s + rebuilds per-role rows every `RefreshIntervalMs=1000`
  even when unchanged (contrast the top stats at `:125-136`, which correctly mutate cached labels in place).
- **Recommend:** debounce search; keep a `Dictionary<id, row>` and mutate labels/toggles in place; cache the parsed
  listing between keystrokes. **Safe now** (event-driven, so lower absolute cost than 4.1, but wasteful).

### 4.4 Per-spawn `Material(Shader.Find("Standard"))` in the auto-battler *(safe now — verify visually)*
- `WaveAutoBattlerModsDemoController.cs:434` `renderer.sharedMaterial = new Material(Shader.Find("Standard")){color=color};`
  runs right after `CoreAiPrimitiveFactory.EnsureRenderPipelineCompatibleMaterial(...)` (`:129,:312`), overwriting the
  URP-safe material with a built-in Standard material (a Material allocation per enemy per wave, and likely magenta
  under URP).
- **Recommend:** tint via `MaterialPropertyBlock` on the factory material instead of allocating a new one.

---

## 5. Over-engineering / simplifications

### 5.1 `HubModServiceBase` is a 1:1 abstraction once MoonSharp is gone *(after purge)*
- `HubModServiceBase.cs` is 418 lines and owns all logic (list-merge, save/enable/disable/delete, persist,
  header-enrich, capability masking). The two derived adapters are ~90 lines each and identical apart from the
  runtime field type (`LuaModRuntimeHubService.cs:53-89` vs `LuaCsModRuntimeHubService.cs:54-91`). After the purge
  there is exactly one subclass.
- **Recommend:** after the purge, collapse `HubModServiceBase` + `LuaCsModRuntimeHubService` into one sealed class
  (inline the 6 `protected abstract` primitives). Keep the `IHubModService` seam (real — used by the page/editor).
  **After the purge** (while both adapters exist the base legitimately dedupes them).

### 5.2 `IFullLuaAccessBlacklistPolicy` — single no-op impl with unreachable wiring *(safe now)*
- Sole impl is the allow-all `AllowAllFullLuaAccessBlacklistPolicy` (`IFullLuaAccessBlacklistPolicy.cs:21`), used as
  the default in both Full bindings (`LuaCsFullUnityRuntimeBindings.cs:41`, `CoreAiFullUnityLuaRuntimeBindings.cs:41`).
  The installer's `fullLuaBlacklistPolicy` param (`CoreAiModsInstaller.cs:46`) is never supplied by the only
  production caller (`CoreAiModsLifetimeScope.cs:44-47` omits it) — the scene path *cannot* inject a real blacklist.
  Similarly `enableFullLuaPrivateAccess` has no guard that Full access is actually on.
- **Recommend:** keep the interface (real security seam consumed by the bindings) but prune the dead
  `fullLuaBlacklistPolicy`/`FullBlacklistPolicy` plumbing until a host uses it, or expose it on the LifetimeScope so
  it's reachable. **Safe now.**

### 5.3 `OneOffCapabilities` always equals `Capabilities` in production *(safe now, minor)*
- `CoreAiModsInstaller.cs:73-74` sets both to `scriptCapabilities`; only a test differs
  (`LuaCsModRuntimeEditModeTests.cs:126`). The separate one-off ceiling is an unused flexibility seam.
- **Recommend:** collapse to one capability grant unless a host needs a lower one-off tier.

### 5.4 `MoonSharp` runtime is fully shadowed — purge is clean except one example-game file *(after purge)*
- Every `Lua*` class (`LuaModRuntime`, `LuaLogicSlots`, `LuaApiRegistry`, `SecureLuaEnvironment`,
  `LuaCoroutineRunner/Handle`, `LuaExecutionGuard`, `LuaAiEnvelopeProcessor`, `LuaTimeBindings`, all
  `CoreAi*LuaRuntimeBindings`, `GameLuaToolExecutor`, `GameLuaBindingsExtensibility`, `IGameLuaRuntimeBindings`) is
  referenced only by other MoonSharp files, tests, or `#if COREAI_HAS_MOONSHARP`-guarded demos. `LuaModRuntime.Tick`
  duplicates `LuaCsModRuntime.Tick` nearly byte-for-byte (`LuaModRuntime.cs:648-717` vs `LuaCsModRuntime.cs:637-716`)
  — do **not** extract a shared base; the dup resolves itself at deletion.
- **Blocker:** `Assets/_exampleGame/RogueliteArena/Features/ArenaProgression/Infrastructure/ArenaProgressionLuaBindings.cs:12,43`
  implements the MoonSharp `IGameLuaRuntimeBindings` / `RegisterGameplayApis(LuaApiRegistry)` **unguarded**, and its
  asmdef hard-references `MoonSharp.Interpreter` with empty `defineConstraints`. Port it to the LuaCs
  `ILuaCsGameRuntimeBindings` (or add the guard) before the purge can compile. (Out of this review's scope, flagged
  for the purge task.)

### 5.5 Minor cleanups *(safe now)*
- `HubModsPage.SetPlaceholder` (`:426-430`) is misnamed — it only sets a tooltip, no placeholder text is shown.
- `HubModsPage.RefreshList` (`:141-149`) is a near-redundant wrapper over `RefreshTree` (both guard-then-call).
- `HubModsPage.Build()` calls `Subscribe(); RefreshList();` (`:89-90`) and `OnActivated` does it again
  (`:59-60`) — `Subscribe` is idempotent, but the list is built twice on first open; drop it from `Build()`.
- `RichLabel()` is re-declared identically in three controllers (`WaveAutoBattler…:438`, `LiveMechanics…:261`,
  `ChatPromptButtons…:155`) — one shared cached style (fixes 4.1 too).
- `ChatPromptButtonsController.cs:38,41` use `_`-prefixed SerializeFields while siblings do not — inconsistent inspector labels.
- `Presentation/CoreAiLuaModAutoRepair.cs:11-18` doc-comment still claims it is MoonSharp-gated
  (`LuaModRuntime.ModHandlerErrored`, "inert when MoonSharp is unavailable") but the code is VM-agnostic
  (`ILuaModRuntime`, `:96`). Update the doc during the purge.

---

## Suggested order of work
1. **Now, cheap:** 4.1 (cache GUIStyle + stop `ListMods()` in OnGUI), 1.1 (delete Ticker + `LuaModEventEmitted`),
   1.2/1.3/1.4 (delete dead observer/option/`Configure`), 5.5 minor cleanups.
2. **Now, medium:** 4.2 (Full-bindings resolve cache), 2.1 (delete `HubModsDemoBinder`), 3.1 (shared resolver /
   injection), 3.2 (add `TrySetInputText`), 4.3 (in-place Hub refresh), 5.2 (prune blacklist plumbing).
3. **After the MoonSharp purge:** 2.2 (delete `LuaModRuntimeHubService` + overload), 5.1 (collapse
   `HubModServiceBase`), 2.5 (retire the IMGUI mod manager), 5.4 (port `ArenaProgressionLuaBindings`), 3.3 doc fixes.
