# CoreAI Hub (UI Toolkit)

An **optional** runtime UI Toolkit window for CoreAI: a tabbed Hub that renders pages registered
into CoreAI's `HubPageRegistry` (Chat, Settings, Statistics, an optional About page, World state, and
any C#/Lua-authored page). Content is built lazily per tab, the tab bar rebuilds live as pages
register/unregister, and the panel is a semi-transparent runtime overlay.

| Package | Depends on | Status |
|---------|-----------|--------|
| `com.neoxider.coreaihub` — [`package.json`](package.json) | `com.neoxider.coreai`, `com.neoxider.coreaiunity` | Optional |

> **You do not need this package to use the registry.** `HubPageRegistry`, `IHubPage`, and the page
> contracts live in the **core** package (`CoreAI.Hub`), so a game can register pages and render them on
> its own uGUI/UITK canvas via the API. Install this package only when you want the ready-made Hub window.

---

## What the Hub is

`CoreAiHubWindow` is a `MonoBehaviour` that (with a sibling `UIDocument`) turns a `HubPageRegistry`
into a tab bar plus a lazily populated content area:

- It clones a UXML shell (`CoreAiHub.uxml`: root / tab bar / content / collapse button) into the
  document's `rootVisualElement` rather than building the tree in C#. Every bind goes through an
  idempotent rebuild so the Hub survives a UIDocument re-init (pre-6.5, no `PanelRenderer`).
- It subscribes to `HubPageRegistry.PageRegistered` / `PageUnregistered` and rebuilds the tab bar
  live — features "light up" as their packages/pages appear.
- Each page's content is created on **first activation** and cached; `IHubPage.OnActivated` /
  `OnDeactivated` fire as tabs change, and `OnDestroyed` fires on teardown.

`CoreAiHubDemo` is a tiny drop-in controller (not a DI point): it creates a registry, registers the
built-in pages, and assigns it to the window so the Hub shows real tabs the moment the GameObject is
enabled. Real integrations build their own registry with live DI-resolved sources.

## How pages register

The registry lives in the core package:

```csharp
// CoreAI.Hub.HubPageRegistry
void Register(string pageId, Func<IHubPage> factory, int order = 0);  // last writer wins per id
bool Unregister(string pageId);
bool TryGet(string pageId, out Func<IHubPage> factory);
IReadOnlyList<(string pageId, int order)> List();                    // sorted by order, then id
```

Pages are registered as **lazy factories**: the window peeks a factory to read metadata
(`DisplayName`) and only builds content when the tab is first activated. A page implements
`IHubPage`:

```csharp
public interface IHubPage
{
    string PageId { get; }
    string DisplayName { get; }          // tab label
    int Order { get; }                   // lower sorts first
    Func<object> CreatePageContent { get; } // UITK hosts return a VisualElement
    void OnActivated();
    void OnDeactivated();
    void OnDestroyed();
}
```

Two optional markers refine behavior:

- **`IHubFullBleedPage`** — the window drops its content padding while the page is active, so the page
  reaches all four edges (the embedded chat uses this, since it brings its own padding).
- **`IHubEscapeHandler`** — `bool TryHandleEscape()`; the active page gets first refusal on the Escape
  key before the Hub reacts (see below).

`HubBuiltInPages.RegisterAll(registry, chatTemplate, chatStyleSheet, chatConfig, settings, metrics,
chatStopGenerationOnEscape)` registers the Chat, Settings, and Statistics pages in one call. Every
argument is optional and null-tolerant: a page with no data source renders a short setup note instead
of throwing.

## Built-in pages

| Page | Id | Display | Order | Notes |
|---|---|---|---:|---|
| Chat | `coreai.hub.chat` | `Chat` | 0 | Hosts the real `CoreAiChatPanel` via `CreateEmbedded` — streaming, tools, history, and hotkeys behave exactly like the standalone chat. Full-bleed; implements `IHubEscapeHandler`. |
| Settings | `coreai.hub.settings` | `AI Settings` | 100 | Runtime backend switching (Auto / LLMUnity / HTTP API / Offline) with the same surface as `CoreAiBackend`, endpoint/profile management, and an inline health probe. **Fetch models** lists a server's advertised model ids in a dropdown, a **Vision** override (Auto / On / Off) forces the multimodal gate, and the endpoint editor has a mode-aware form with an Advanced foldout and a per-row Remove — see [Runtime Backend Switching §3](../CoreAiUnity/Docs/RUNTIME_BACKEND_SWITCHING.md). API keys are write-only in the UI. |
| Statistics | `coreai.hub.statistics` | `Statistics` | 200 | Orchestration metrics (completions, tool calls, …) from an optional `InMemoryAiOrchestrationMetrics`. |
| About | `coreai.hub.about` | `About` | 1000 | Opt-in (off by default in `CoreAiHubDemo`) so the tab bar stays focused on functional pages. |
| World state | `coreai.hub.worldstate` | `World` | — | Saved-state status plus Reset World / Save Now controls (see [WORLD_COMMANDS.md](../CoreAiUnity/Docs/WORLD_COMMANDS.md) §7). The page class ships here, but it is registered on demand by `WorldStateHubBinder` in the optional mods package, not by `RegisterAll`. |
| World Loads | `coreai.hub.world-loads` | `World Loads` | 250 | Player confirmation surface for AI-requested world restores. `load_world` never applies a package: it returns `player_confirmation_required` plus a one-use request id, and this page renders the pending request metadata and calls `ConfirmManualLoadAsync(requestId, true|false)`. It never receives package bytes and exposes no direct-load action. Registered by the mods package when `IRbxWorldRuntimeService` is available — see [WORLD_PACKAGE.md](../../Docs/CoreAIMods/WORLD_PACKAGE.md). |

The **Chat / Settings / Statistics** trio comes from `HubBuiltInPages.RegisterAll`; **About** is
opt-in via `CoreAiHubDemo`. The **Mods** and **World** tabs are not registered by this package: when
`com.neoxider.coreaimods` is installed, the setup menu adds its `CoreAiModsHubBinder` (resolved by name
from the `CoreAI.Mods.Hub` assembly) and its binders register the Mods, World state, and World Loads pages into the same
registry — features light up when their packages appear.

## Collapse and Escape

The window can collapse to just its toggle button:

- **Collapse button / `ToggleCollapsed()` / `SetCollapsed(bool)`** hide the tabs + content, leaving the
  toggle (`–` when expanded, `+` when collapsed).
- **Escape** (`escapeCollapses`, default **on**): while expanded, Escape first offers the active page a
  chance to consume it via `IHubEscapeHandler.TryHandleEscape()`, then collapses the Hub to the toggle
  button. Set `escapeCollapses` off to disable Hub Escape handling entirely.
- **Escape does not stop generation by default.** Collapsing the Hub leaves any in-flight AI turn
  running in the background; the answer still lands normally once the Hub is expanded again.
  Stop-on-Escape for the chat page is **opt-in** via `HubChatPage.StopGenerationOnEscape`
  (forwarded through `HubBuiltInPages.RegisterAll(..., chatStopGenerationOnEscape: true)`) — when on,
  Escape stops the in-flight turn and consumes the key-press once instead of collapsing.
- **`toggleHotkey`** (default `KeyCode.None`) optionally expands/collapses the Hub when no UITK element
  holds keyboard focus.
- **`requireVisibleCursor`** (default **on**) gates Escape and the toggle hotkey behind a visible,
  unlocked cursor, mirroring `CoreAiChatOptions.ChatRequiresVisibleCursor`, so a locked-cursor
  (first-person) game keeps its input.

## Setup

Menu: **CoreAI ▸ Setup ▸ Add Hub**. On first use it authors the reusable module prefab at
`Assets/CoreAIHub/Runtime/CoreAiHub.prefab` (its own `PanelSettings` so it never contends with the
embedded chat's `UIDocument`) and instances it into the open scene; later invocations just instance
it. If `com.neoxider.coreaimods` is present, the Mods tab is lit up automatically. The scene needs a
`CoreAILifetimeScope` (and, for the Mods tab, a `CoreAiModsLifetimeScope` child).
