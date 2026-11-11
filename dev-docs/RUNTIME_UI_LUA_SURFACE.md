# Runtime UI - the Lua surface (design note for the second half of P18)

Status: **implemented in code on 2026-11-11, not yet verified in Unity** (`UxmlService`, `UiScreen`,
`UiElement`; code under `Assets/CoreAIMods/Runtime/RbxApi/Instances/RuntimeUi/` and
`Assets/CoreAIMods/Runtime/Scripting/LuaCs/RuntimeUi/`). The note was written as a proposal with the
first half of P18 (the engine-free UXML/USS interpreter, the Unity factory, applier and host); read it
as the design rationale. Where it and the code differ, the code and the "From Lua" chapter of
`Assets/CoreAiUnity/Docs/RUNTIME_UI.md` are right. Proposed here and not built: `PushScreen` /
`PopScreen`, the template registry (`RegisterTemplate`, `UI_BUDGET_TEMPLATES`), the pointer and focus
signals on elements, `QueryAll` on an element, and the export lint. Known limits of what was built:
a mod's screens are removed on the frame after it unloads, not inside the unload call; after a failed
hot reload a screen the failed run replaced keeps its new texts; a header that lists capabilities must
list `Ui` to get any UI.
Roadmap: `Docs/CoreAIMods/ROBLOX_API_ROADMAP.md` §P18. User doc of the C# layer:
`Assets/CoreAiUnity/Docs/RUNTIME_UI.md`.

## 1. Why this surface is CoreAI-only

Roblox has no markup. Its interface is a tree of instances (`ScreenGui`, `Frame`, `TextLabel`, ...)
built in Studio or with `Instance.new`, styled by properties and by `StyleSheet`/`StyleRule` instances
(offline mirror: `D:\Git\RobloxDocs\creator-docs\content\en-us\reference\engine\classes\`). UXML/USS from
Lua therefore has no Roblox shape (plan decision D14):

- it is a **DEV entry** (a conscious deviation, next free number in the roadmap's DEV table), and
- every use is **flagged by the MVP13 export lint** as a CoreAI-only construct (`game:GetService("UxmlService")`
  and any member of the two userdata types below), so an exported place says up front what will not
  run on Roblox.

The Roblox GUI classes (MVP15 and the next task) are the other entrance over the same factory (§7);
a Roblox script uses those and exports cleanly.

## 2. Service name

**`UxmlService`**, reached with `game:GetService("UxmlService")`.

Checked against the mirror: Roblox ships `GuiService`, `StarterGui`, `CoreGui`, `PlayerGui`,
`TextService`, `UserInputService` and the `Style*` and `UI*` classes, but no `UxmlService`, `UiService`
or `MarkupService`. `UxmlService` names exactly what it takes and can never collide with a future
Roblox GUI service. (Rejected: `UiService` - too close to `GuiService`, and a Roblox service of that
shape is plausible; `RuntimeUiService` - describes our implementation, not the contract.)

## 3. Members

Types are Luau annotations. Errors are raised in the stable stub-error format with the codes of §6;
parser and cascade problems are never raised - they are returned, because the AI repairs from them.

### `UxmlService`

| Member | Maps to |
|---|---|
| `CreateScreen(id: string, uxml: string, uss: string?, options: {SortOrder: number?, Visible: boolean?}?): UiScreen` | `IRuntimeUiHost.CreateScreen` with `Owner = modId` and the host id `modId .. "/" .. id` |
| `GetScreen(id: string): UiScreen?` | `TryGetScreen` (own screens only) |
| `GetScreens(): {UiScreen}` | the mod's live screens |
| `PushScreen(screen: UiScreen, hidePrevious: boolean?)`, `PopScreen(): UiScreen?` | the host stack (own screens only) |
| `RegisterTemplate(name: string, uxml: string)`, `UnregisterTemplate(name: string)` | `UxmlTemplateRegistry`, namespaced per mod |
| `SetToken(name: string, value: string)`, `GetToken(name: string): string?` | `RuntimeUiTheme` (writing needs `Ui` plus the theme grant, §5) |
| `TextInputFocused: boolean` (read-only), `TextInputFocusChanged: RBXScriptSignal` | `IRuntimeUiTextInputFocus` |
| `PointerOverUi: boolean` (read-only) | `IRuntimeUiHost.IsPointerOverUi` |

### `UiScreen` (userdata)

| Member | Maps to |
|---|---|
| `Id: string`, `Visible: boolean`, `SortOrder: number` | screen properties (`Visible`/`SortOrder` writable) |
| `SetUxml(text: string): {Diagnostic}` | `ReplaceUxml` |
| `SetUss(text: string): {Diagnostic}` | `ReplaceUss` |
| `Diagnostics: {Diagnostic}` | the last replace plus API reports; `Diagnostic = {Code, Severity, Source, Line, Column, Message}` |
| `Query(selector: string): UiElement?`, `QueryAll(selector: string): {UiElement}`, `GetElement(name: string): UiElement?` | queries over authored elements |
| `CreateElement(kind: string, parent: UiElement?, name: string?): UiElement` | `CreateElement`, `kind` = a supported UXML type name |
| `Destroy()` | `Destroy` |
| `Destroying: RBXScriptSignal` | `RuntimeUiScreen.Destroyed` |

### `UiElement` (userdata)

| Member | Maps to |
|---|---|
| `Name`, `Kind` (read-only), `Parent: UiElement?` | handle properties |
| `Text: string`, `Value: boolean | number | string`, `Visible: boolean`, `Enabled: boolean`, `ZOrder: number` | `SetText`, `SetValue`/`SetBool`/`SetNumber`, `Visible`, `SetEnabled`, `SetZOrder` |
| `AddClass`, `RemoveClass`, `ToggleClass`, `HasClass`, `GetClasses` | classes |
| `SetStyle(property: string, value: string)`, `ClearStyle(property: string)`, `GetStyle(property: string): string?` | `SetStyle`, `ClearStyle`, `TryGetResolvedStyle` |
| `SetAttribute(name: string, value: string)`, `SetItems(items: {string})` | attributes, list rows |
| `Focus()`, `Blur()`, `Query(selector)`, `CreateChild(kind, name?)`, `Remove()` | the rest |
| `Clicked`, `ValueChanged`, `Submitted`, `PointerEntered`, `PointerExited`, `FocusGained`, `FocusLost`: `RBXScriptSignal` | the handle events |

A detached element (removed, or its name vanished in a UXML replace) raises `UI_ELEMENT_DETACHED` on
every write and returns `nil` on reads; `IsAlive: boolean` lets a script check first.

Signals are ordinary MVP2 signals: `Connect` returns an `RBXScriptConnection`, handlers run deferred
through the mod scheduler under the mod's execution guard (budgets and quarantine apply), and a
handler error is reported against the mod like any other script error.

## 4. Lifecycle

- **Ownership.** Every screen is created with `Owner = modId`; host ids are namespaced
  (`modId/id`), so two mods can both own a `"hud"`. A mod sees and changes only its own screens and
  templates.
- **Unload** (mod disabled, removed or quarantined): the binding disconnects the mod's connections,
  then calls `DestroyScreensOwnedBy(modId)` and drops its templates. The PL test asserts zero live
  elements (`ElementCount`), zero state callbacks (`LiveCallbackCount`), zero subscribers
  (`LiveSubscriberCount`) and zero UI Toolkit registrations afterwards - the counters already exist.
- **Hot reload** (same mod re-executed): the old script instance's connections are disconnected; its
  screens are **kept** for the duration of the reload. `CreateScreen` with an id the same mod already
  owns becomes a replace (`Replace(uxml, uss)`) instead of `UI_SCREEN_EXISTS`, so element state (the
  selected slot, typed text) survives the reload exactly as it survives a UXML replace. Screens the
  reloaded script did not re-create by the end of its first run are destroyed.
- **World reset / scope dispose**: the host is disposed with its scope; nothing outlives it.

## 5. Capability and budgets

- A new **`Ui`** tier in `LuaCapabilities`. Without it, every `UxmlService` member raises
  `UI_CAPABILITY_REQUIRED` (twin: granted, the screen renders). Proposal: granted together with
  `Gameplay` by default, as the [R4] specification says, so a fresh world's mods can draw a HUD without
  configuration; a host game can withhold it. (Roblox's own member metadata carries a `UI` script
  capability on the GUI members, which this mirrors in name.)
- Writing theme tokens changes every screen in the world, so `SetToken` additionally needs the
  elevated tier or an explicit `UiTheme` grant (open question 3).
- Per-mod budgets (the binding enforces them; the host's own limits are only the hard ceiling):

| Budget | Default | Error |
|---|---|---|
| live screens | 32 (proposed as 8) | `UI_BUDGET_SCREENS` |
| authored elements, all screens of the mod | 4,096 (proposed as 1,000) | `UI_BUDGET_ELEMENTS` |
| UXML or USS text per call | 64 KB | `UI_TEXT_TOO_LARGE` |
| templates | 32, 16 KB each | `UI_BUDGET_TEMPLATES` |
| element writes per frame (text, value, style, class, attribute) | 8,192 (proposed as 2,000) | `UI_BUDGET_WRITES` |

Handler run time is the existing per-slice instruction and time budget (DEV-3); no UI-specific cap.

## 6. Error codes

Raised (stable stub-error format): `UI_CAPABILITY_REQUIRED`, `UI_DISABLED`, `UI_SCREEN_EXISTS` (another
mod's id is impossible after namespacing, so only on a malformed id), `UI_SCREEN_NOT_FOUND`,
`UI_SCREEN_DESTROYED`, `UI_ELEMENT_DETACHED`, `UI_INVALID_SELECTOR`, `UI_UNSUPPORTED_OPERATION` (a value
on a Label), `UI_INVALID_ARGUMENT`, `UI_TEXT_TOO_LARGE`, `UI_BUDGET_SCREENS`, `UI_BUDGET_ELEMENTS`,
`UI_BUDGET_TEMPLATES`, `UI_BUDGET_WRITES`.

Returned, never raised: every `UXML_*` and `USS_*` report (see the user doc). A UXML or USS text that
is entirely broken still produces a screen (possibly empty) plus reports, so the AI's next turn can
repair it.

## 7. The Roblox GUI classes on the same factory

The next task implements `ScreenGui`, `Frame`, `TextLabel`, `TextButton`, `ImageLabel`, `TextBox`,
`UICorner` and `UIListLayout` as instances whose backing is a markup-free screen. They use only the
typed C# API (`CreateElement`, `SetStyle(UssPropertyId, UssValue)`, `SetZOrder`, the handle events), so
both entrances share one factory, one applier and one host. Per-frame property writes (tweens) use the
typed path, which parses no text.

| Roblox | On the factory |
|---|---|
| `ScreenGui` | one programmatic screen; `DisplayOrder` -> `SortOrder`; `Enabled` -> `Show`/`Hide`; `ResetOnSpawn` -> the binding destroys and rebuilds on respawn; `IgnoreGuiInset` -> a top inset on the screen root (0 on desktop) |
| `Frame` / `TextLabel` / `TextButton` / `ImageLabel` / `TextBox` | `VisualElement` / `Label` / `Button` / `Image` / `TextField` |
| `Position: UDim2` | `position: absolute`; per axis, scale only -> `left`/`top` in `%`; offset only -> `px`; both -> `left: scale%` plus `margin-left: offset px` (Yoga adds margins to absolute offsets) |
| `Size: UDim2` | scale only -> `%`, offset only -> `px`; both cannot be one USS length (no `calc()`), so the layer computes pixels from the parent's resolved size on the parent's `GeometryChangedEvent` - event-driven, never polled |
| `AnchorPoint: Vector2` | `translate: -X*100% -Y*100%` (UI Toolkit percentages are of the element's own size) |
| `ZIndex` with `ZIndexBehavior.Sibling` | `SetZOrder` (stable among siblings); `Global` needs a flattened order and starts as a documented deviation |
| `Visible`, `Rotation`, `ClipsDescendants` | `Visible`, `rotate`, `overflow: hidden` |
| `BackgroundColor3` + `BackgroundTransparency` | `background-color` with alpha `1 - transparency` |
| `BorderSizePixel`, `BorderColor3` | `border-width`, `border-color` |
| `TextColor3`, `TextSize`, `TextXAlignment` + `TextYAlignment`, `TextWrapped`, `TextTransparency` | `color`, `font-size`, `-unity-text-align` (9 combinations), `white-space`, colour alpha |
| `Text` | `SetText`; `TextBox.Text` changes raise `ValueChanged` |
| `UICorner.CornerRadius: UDim` | `border-radius` (`px`, or `%` for scale) on the parent element |
| `UIListLayout` | on the parent: `flex-direction` from `FillDirection`, `justify-content`/`align-items` from the alignments (axes swap with the direction), `Padding` as a margin on every child but the last (USS has no `gap`), `SortOrder = LayoutOrder` as a z/sibling order by `LayoutOrder`; children under a layout ignore `Position`, as in Roblox |
| `UIPadding`, `UIStroke` | `padding`; `border-*` on frames, `-unity-text-outline` on text |
| `GuiButton.Activated` | `Clicked` (pointer, keyboard and gamepad submit - the same as Roblox's definition) |
| `GuiButton.MouseButton1Click` | a pointer-only click: a `ClickEvent` with button 0 (the handle needs a pointer-only variant of `Clicked`) |
| `GuiObject.MouseEnter` / `MouseLeave` | `PointerEntered` / `PointerExited` |
| `TextBox.Focused` / `FocusLost(enterPressed, inputThatCausedFocusLoss)` | `FocusGained` / `FocusLost`; `enterPressed` is true when `Submitted` fired for the same focus |

Not mapped at first: `TextScaled` (needs text measurement), `Font`/`FontFace` (font assets), `UIGradient`,
`UIAspectRatioConstraint`, `UISizeConstraint`, `UIGridLayout`, `UIPageLayout`, `StyleSheet`/`StyleRule`
instances (their selectors could later compile onto the same cascade).

## 8. Open questions

1. **Hot reload keep-or-rebuild.** The proposal keeps screens across a reload and turns the repeated
   `CreateScreen` into a replace. The DoD says "hot reload rebuilds it"; a rebuild through `Replace`
   satisfies both readings, but the owner should confirm that state is meant to survive.
2. **Default grant of `Ui`.** With `Gameplay` (the [R4] text) or opt-in per mod?
3. **Who may change theme tokens?** Any mod with `Ui`, only the elevated tier, or a separate grant.
4. **Shared screens.** Can one mod query or restyle another mod's screen (a theme mod restyling every
   HUD)? The proposal says no; tokens are the sanctioned cross-mod channel.
5. **Event payloads.** Signals pass the element only; a Roblox-style `(inputObject, clickCount)` payload
   would need the P1 input objects.
6. **Clicks through to the world.** `PointerOverUi` exists; should the binding swallow world clicks
   automatically while it is true, or leave that to the controller?
7. **Persistence.** AI-authored screens persist in M2 with P16 ([R4] `FileUiSourceStore`); until then a
   mod re-creates its screens on start.
