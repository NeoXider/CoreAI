# CoreAI Hub — extensible page system (UI Toolkit) — plan

> Status: DRAFT 2026-07-07. Parallel workstream, INDEPENDENT of the Lua VM swap (touches CoreAI core + a new
> UI module, not the mod runtime). All code/docs/commits in English. Commit at green checkpoints.

## Goal
One CoreAI "Hub" window with pages/tabs (Chat, Settings, Statistics, **Mods**, **Mod Editor**, …), where C#
modules AND Lua mods can register their own page at runtime. Convenient, polished, and future-proof for Unity
6.5 world-space UI (PanelRenderer). Built on **UI Toolkit (UITK)** — our chat panel is already UXML/USS.

## Architecture — 3 layers (optionality by package presence)

### 1. CoreAI core — the page CONTRACT + registry (no UI dependency)
Lives in `CoreAI.Core` / `CoreAI.Source` so anyone can register pages and build their OWN UI (canvas via API)
without pulling the UITK window. Mirrors the existing `SkillSet`/`HubPageRegistry`-style event pattern.
- `IHubPage` — `string PageId`, `string DisplayName`, `Texture2D/Icon`, `int Order`, `VisualElement CreatePageContent()`,
  hooks `OnActivated()/OnDeactivated()/OnDestroyed()`. (VisualElement is UnityEngine.UIElements — available in
  core without a UI package; if we want core UI-framework-free, use a thin factory delegate instead — decide in
  Phase H1.)
- `HubPageRegistry` — thread-safe `Dictionary<pageId, Func<IHubPage>>`, last-writer-wins by id, events
  `PageRegistered`/`PageUnregistered`. Registration optional-priority via `Order`. No reflection.
- Purpose: a dev who doesn't want our window queries the registry and renders pages on their own uGUI Canvas
  through the API. The registry is the stable seam.

### 2. New module `com.neoxider.coreaihub` (UITK) — the actual window (OPTIONAL)
Separate package so it's opt-in and can evolve to PanelRenderer/world-space (Unity 6.5) without touching core.
- `CoreAiHubWindow` (UIDocument) — tab bar + content container; subscribes to registry events, rebuilds tabs,
  lazy-creates a page's content on first activation; semi-transparent styling.
- Built-in pages: **Chat** (wraps the existing `CoreAiChatPanel` — reuse as-is), **Settings** (backend config),
  **Statistics** (OrchestrationDashboard metrics + token budget). Registered via DI `RegisterBuildCallback`.
- Future: swap the UIDocument host to `PanelRenderer` for world-space in 6.5 — isolated to this module.
- Rationale (owner): "maybe pages as a separate module too, so we can later move to PanelRenderer in 6.5; and
  some devs won't need it and will do canvas via API in code." → core = API, module = UITK window.

### 3. CoreAIMods — Mods page + Mod Editor page (registered into the core registry)
- **Mods page:** category (folder) tree + search; per-mod Add / Paste (`systemCopyBuffer`) / Copy / Enable /
  Disable / Delete / Import / Export / Update; refreshes on mod store events.
- **Mod Editor page:** code editor (Paste/Copy/Save/Close), per-mod log view (from the Phase-5 `IModLogSink`),
  run/validate. Lets the AI or the player author/edit a mod live.
- Optional Lua binding `coreai_ui_register_page(id, spec)` — a MOD adds its own page via a **declarative widget
  schema** (`render()` returns a Lua table of widgets `{type="label"/"button"/"slider", ...}`; C# builds+diffs
  the VisualElement; callbacks dispatched by string id). Safe (untrusted Lua never touches the UI thread),
  cheap (rebuild on state change), and **serializable → replicable** for future MP. Gated by capability tier.

## Phases (parallel to the VM swap)
- **H1 — Core contract:** `IHubPage` + `HubPageRegistry` + events in core; decide VisualElement-vs-factory seam;
  unit test register/unregister/last-writer-wins. (simple → Sonnet5 / Codex)
- **H2 — Hub module scaffold:** `com.neoxider.coreaihub` package + asmdef + `CoreAiHubWindow` UIDocument + tab
  bar + lazy content; wire to registry. (complex UITK → Opus)
- **H3 — Built-in pages:** Chat (wrap existing panel), Settings, Statistics; DI registration. (medium)
- **H4 — Mods page + Mod Editor** in CoreAIMods (needs Phase 3 mod store + Phase 5 logs). (medium)
- **H5 — Lua declarative page binding** `coreai_ui_register_page` (needs VM swap done). (later)

## Dependencies / ordering
- H1/H2/H3 are INDEPENDENT of the Lua VM swap → build now in parallel.
- H4 depends on the mod store (Phase 3) + per-mod logs (Phase 5).
- H5 depends on the VM swap (Phase 2) being green.

## Verification
Registry: a C# page and (later) a Lua page appear as tabs; removing CoreAIMods leaves Chat/Settings/Statistics
with no errors. FPS: UITK retained-mode must not grow frame time with page/mod count (old IMGUI hit 167 ms at 9
mods). Windows build renders the Hub; pages lazy-create.

## Delegation (owner: Opus for complex, Sonnet5 / Codex-spark for simple; parallel)
- Opus: H2 (UITK window), H4 editor.  · Sonnet5/Codex: H1 (contract+tests), H3 built-ins, mechanical wiring.
- Non-overlapping files per agent; orchestrator compiles + commits at green.
