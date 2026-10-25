# CoreAI Hub (UI Toolkit)

An **optional** runtime UI Toolkit window for CoreAI: a tabbed Hub that renders pages registered
into CoreAI's `HubPageRegistry` (Chat, Settings, Statistics, an optional About page, World state, and
any page your C# code registers). Content is built lazily per tab, the tab bar rebuilds live as pages
register/unregister, and the panel is a semi-transparent runtime overlay. There is no Lua API for
registering Hub pages; Lua mods appear in the Hub through the Mods tab of `com.neoxider.coreaimods`.

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
  `OnDeactivated` fire as tabs change. Disabling the window deactivates its visible page and preserves
  cached pages/content, including drafts, for re-enable. Final window destruction, registry replacement,
  or replacement/removal of a page factory destroys the affected pages.

`CoreAiHubDemo` is a tiny drop-in controller (not a DI point): it creates a registry, registers the
built-in pages, and assigns it to the window so the Hub shows real tabs the moment the GameObject is
enabled. Real integrations build their own registry with live DI-resolved sources.

## How pages register

The registry lives in the core package:

```csharp
// CoreAI.Hub.HubPageRegistry
void Register(string pageId, Func<IHubPage> factory, int order = 0);  // last writer wins per id
void RegisterOwned(string pageId, Func<IHubPage> factory, IDisposable lifetime, int order = 0);
bool Unregister(string pageId);
bool Unregister(string pageId, Func<IHubPage> expectedFactory);
bool TryGet(string pageId, out Func<IHubPage> factory);
IReadOnlyList<(string pageId, int order)> List();                    // sorted by order, then id
```

Pages are registered as **lazy factories**: the window calls a factory once to read the tab label
(`DisplayName`), keeps that page instance, and only builds its content when the tab is first activated.
Changing the window's registry creates pages from the new registry even when IDs match. Queued
notifications from the old view cannot destroy replacement pages. `RegisterOwned` transfers a
registration lifetime that is disposed on replacement/removal, outside the registry lock; the
factory-token `Unregister` overload prevents a stale binder from removing a newer registration.
Factories should create a fresh page for each window lifetime. Caller-supplied services remain
caller-owned; the Mods runtime-registration overload owns and releases its runtime adapter.
A page implements `IHubPage`:

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

`HubPageBase` (also in `CoreAI.Hub`) stores the id, label and order from its constructor and gives
no-op lifecycle hooks, so a page only overrides what it needs:

```csharp
using System;
using CoreAI.Hub;
using UnityEngine.UIElements;

public sealed class ScorePage : HubPageBase
{
    public ScorePage() : base("mygame.score", "Score", 50) { }

    public override Func<object> CreatePageContent => () => new Label("Score: 0");
}

// registry.Register("mygame.score", () => new ScorePage(), 50);
```

To group several pages under one tab, register one `HubSubTabPage(pageId, displayName, order, children...)`
from this package; the Mods tab uses it for its Mods / Logs sub-tabs.

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
| Settings | `coreai.hub.settings` | `AI Settings` | 100 | Runtime backend switching (Auto / LLMUnity / HTTP API / Offline) with the same surface as `CoreAiBackend`, endpoint/profile management, and an inline health probe. **Fetch models** lists a server's advertised model ids in a dropdown, a **Vision** override (Auto / On / Off) forces the multimodal gate, and the endpoint editor has a mode-aware form with an Advanced foldout and a per-row Remove — see [Runtime Backend Switching §2](../CoreAiUnity/Docs/RUNTIME_BACKEND_SWITCHING.md#2-hub-ai-settings-editor). API keys are write-only in the UI. |
| Statistics | `coreai.hub.statistics` | `Statistics` | 200 | Orchestration metrics (completions, tool calls, …) from an optional `InMemoryAiOrchestrationMetrics`. |
| About | `coreai.hub.about` | `About` | 1000 | Opt-in (off by default in `CoreAiHubDemo`) so the tab bar stays focused on functional pages. |
| World state | `coreai.hub.worldstate` | `World` | 300 | Saved-state status plus Reset World / Save Now controls (see [WORLD_COMMANDS.md](../CoreAiUnity/Docs/WORLD_COMMANDS.md) §7). The page class ships here, but it is registered on demand by `WorldStateHubBinder` in the optional mods package, not by `RegisterAll`. |
| World Loads | `coreai.hub.world-loads` | `World Loads` | 250 | Player confirmation surface for AI-requested world restores. `load_world` never applies a package: it returns `player_confirmation_required` plus a one-use request id, and this page renders the pending request metadata and calls `ConfirmManualLoadAsync(requestId, true|false)`. It never receives package bytes and exposes no direct-load action. A blank or malformed slot or autosave name never reaches this page: `load_world` / `load_autosave` refuse it themselves with a JSON result (`success: false`, `status: "invalid_argument"`) and create no request. Registered by the mods package when `IRbxWorldRuntimeService` is available — see [WORLD_PACKAGE.md](../../Docs/CoreAIMods/WORLD_PACKAGE.md). The page also lists the autosave ring (file name, trigger such as `execute_lua` or `manage_mods-load`, local time, size) and its `Load...` action enters the same confirmation pool as manual slots; the runtime writes a `load_world-pre` safety autosave before any confirmed load. The load tools return every other failure as a JSON status too (`not_found`, `invalid_package`, `read_failed`, `network_sessions_active`) and create no request for it. A world the player confirms here also reopens on the next start: when the service implements `IRbxWorldStartupSelection` (the production controller does) the page opens with a **Next start** section — `Opens on start: <world>` (or `default world`) and a **Start with the default world next time** button, which calls `ClearStartupSelectionAsync` and leaves the live world and the saves alone — and the **Confirm load** tooltip and the outcome line say whether the confirmed world will reopen, together with later changes made to it by the AI or on the Mods page, unless it holds an active Full-capability mod. The load tools also refuse a package the confirmation would refuse (`invalid_package`) and answer `session_unavailable` once the world session is shut down. |

The **Chat / Settings / Statistics** trio comes from `HubBuiltInPages.RegisterAll`; **About** is
opt-in via `CoreAiHubDemo`. The **Mods** and **World** tabs are not registered by this package: when
`com.neoxider.coreaimods` is installed, the setup menu adds its `CoreAiModsHubBinder` (resolved by name
from the `CoreAI.Mods.Hub` assembly), and the mods package's binders (`CoreAiModsHubBinder`,
`WorldStateHubBinder`) register the Mods (order 300, with Mods / Logs sub-tabs), World state, and World Loads
pages into the same registry — features light up when their packages appear. `CoreAiModsHubBinder` also
re-registers Settings and Statistics with the live `ICoreAISettings` and `InMemoryAiOrchestrationMetrics`
from the container, replacing the source-less pages `CoreAiHubDemo` registered.

The `CoreAI.Mods.Hub` assembly compiles only when `COREAI_HAS_HUB` is defined (`defineConstraints`). Its
version define sets `COREAI_HAS_HUB` when `com.neoxider.coreaihub` is installed as a package; this
repository keeps the packages under `Assets/`, so it sets the symbol in Player Settings (Standalone,
Android, WebGL). Without the define the Mods tab is silently absent.

On the Mods tab, the editor's **Save & run** reloads a
loaded mod and first removes what its previous run built at startup, unless **Keep objects on Save & run** is on
(one Hub-wide setting, PlayerPrefs key `CoreAI.Hub.Mods.KeepObjectsOnSave`); the status line says what was cleaned,
**Add** opens a template that matches the composition (`RunService.Heartbeat` and `task.wait` with the Rbx API),
a mod suspended after repeated budget trips is marked on its row, and the Mods and Logs tabs rebuild at most once
per panel update, on the main thread — see [mod-system.md](../../Docs/CoreAIMods/mod-system.md) §4.2 and §5c.

The mod editor compares its opening source with the current service source before saving. A changed
source needs an explicit overwrite of the version shown by the warning. Back preserves a dirty draft
until Save, Copy, or an explicit Discard choice; window disable preserves it as well. Destroying the
window or replacing its registry ends the view lifetime, so a host that needs drafts across that
boundary must capture `HubModEditorPage.DraftSource` before teardown. This comparison protects
interleaved UI operations; it is not storage compare-and-swap for concurrent background writers.

`HubModSaveResult.PersistenceStatus` distinguishes live execution from source persistence. The built-in
service reports `Saved` only when optional `ILuaModSourceStrictWriter.SaveStrict` returns successfully,
which acknowledges local source/manifest installation and automatic persistence arming. A
`LuaModSourcePersistenceException` becomes `Failed`; `LocalCommitCompleted` says whether local
installation preceded an arming failure. A custom legacy `ILuaModSourceStore` is still called but
returns `Unconfirmed`, even when its readback matches. The original four-argument result constructor
continues to declare `Saved` for compatibility: a custom service using it must already have that
acknowledgment, or use the explicit-status overload. Existing enum values remain unchanged, with
`Unconfirmed` appended. Neither success nor local installation confirms an IndexedDB transaction.

Failed/unconfirmed saves keep the draft dirty, including an unchanged buffer. Retrying the same source
while its accepted live version remains loaded retries storage without executing the mod again. A
successful persistence receipt clears that retry state, so the next Save & run is a new execution.
Enable/Disable similarly require strict acknowledgment from configured storage and report failure
after any live mutation; retrying Enable does not reload an already live mod. Cancellation remains
cancellation. The mode-less save API throws when configured storage fails or remains unconfirmed.
Deletion uses optional `ILuaModSourceStrictRemover.DeleteStrict`; a refused arm request remains
retryable after local files disappear. Legacy configured stores return an unconfirmed/false deletion
result even after a successful local readback. Strict removal failure preserves its typed local-commit
detail and cancellation propagates separately.

Endpoint saves preserve inputs, including the entered key, on `NotSaved`. Changing endpoint/controller
or editing the draft fences old completions and protects newer busy state. World save/reset consumes
`IWorldStateWriteResults` for that operation. Legacy managers remain unconfirmed; a separate global
arming check cannot turn a failed write into a success. World-load decisions, failures, and cancellation
remain in the outcome line while the pending inventory refreshes.

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
- **Keyboard navigation**: Tab focuses the top-level tabs and collapse/expand button. Left/Right and
  Home/End select tabs; directional UI navigation supports Left/Right as well. Escape collapse focuses
  the labeled expand button; submit reopens the Hub and restores the active tab's focus. Pointer
  selection releases navigation-button focus so gameplay Space does not activate the last clicked tab.
- **`requireVisibleCursor`** (default **on**) gates Escape and the toggle hotkey behind a visible,
  unlocked cursor, mirroring `CoreAiChatOptions.ChatRequiresVisibleCursor`, so a locked-cursor
  (first-person) game keeps its input.

## Setup

Menu: **CoreAI ▸ Setup ▸ Add Hub**. On first use it authors the reusable module prefab at
`Assets/CoreAIGenerated/Hub/CoreAiHub.prefab` (its own `PanelSettings` so it never contends with the
embedded chat's `UIDocument`) and instances it into the open scene; later invocations just instance
it. Required Hub/chat resources resolve from their installed UPM package paths or the source layout
under Assets; generated output always stays in the writable project folder. Missing resources or a
refused prefab save stop setup with an error before prefab instantiation. If `com.neoxider.coreaimods`
is present, the Mods tab is lit up automatically. The shipped prefab
also carries the mods package's `WorldStateHubBinder` and `WorldStateAutoSaveHook`. The scene needs a
`CoreAILifetimeScope` (and, for the Mods tab, a `CoreAiModsLifetimeScope` child). Without the menu, add
`CoreAiHubWindow` (it requires a `UIDocument`) and assign `CoreAiHubWindow.Registry` from your own code, or
add `CoreAiHubDemo` next to it.

## Layer map

The engine-free page/registry contracts live in CoreAI's `Core/Features/Hub` block. This optional
package is the Unity UI adapter, with a separate Editor setup assembly; it needs no additional Domain
assembly because it adds view composition and presentation rather than game rules. Mods integration
stays in the optional `CoreAI.Mods.Hub` assembly and does not make the Hub depend on the Mods package.
