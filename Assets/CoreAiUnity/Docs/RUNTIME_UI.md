# Runtime UI - UXML and USS while the game runs

**Goal:** build, restyle and rebuild game interfaces from UXML and USS **text** while the game runs - in the
editor, in a built player and in WebGL - with no import step. Unity imports UXML and USS only in the
editor, so CoreAI interprets the text itself: a forgiving UXML reader builds a UI Toolkit element tree,
and a USS subset is applied as inline styles by an engine-free cascade. Everything the interpreter does
not apply is reported with its line; nothing is dropped silently.

The same element factory and style applier also work without any markup (a typed C# API), which is
what a later Roblox GUI layer (`ScreenGui`, `Frame`, `TextLabel`, ...) builds on.

Mods reach the same host from Lua through `game:GetService("UxmlService")` — see [From Lua](#from-lua)
at the end of this page (design notes: `dev-docs/RUNTIME_UI_LUA_SURFACE.md`). The chapters before it
document the C# API that surface sits on.

## Quick start

`CoreAILifetimeScope` registers the host for you (`RuntimeUiOptions` on the inactive scope configures it or
switches it off). A scope of your own registers it once (after `RegisterCore`, so it can log):

```csharp
using CoreAI.RuntimeUi;

builder.RegisterRuntimeUi();
```

Then create a screen from text and talk to its elements:

```csharp
IRuntimeUiHost ui = container.Resolve<IRuntimeUiHost>();

RuntimeUiResult result = ui.CreateScreen("hud",
    "<ui:UXML xmlns:ui=\"UnityEngine.UIElements\">" +
    "  <ui:VisualElement name=\"hotbar\" class=\"cai-row\">" +
    "    <ui:Button name=\"slot0\" class=\"slot\" text=\"1\"/>" +
    "    <ui:Button name=\"slot1\" class=\"slot\" text=\"2\"/>" +
    "  </ui:VisualElement>" +
    "  <ui:Label name=\"counter\" text=\"0\"/>" +
    "</ui:UXML>",
    ".slot { width: 64px; height: 64px; border-width: 2px; border-color: var(--cai-color-border); }" +
    ".slot.is-selected { border-color: var(--cai-color-accent); }" +
    ".slot:hover { scale: 1.05; transition: scale 0.1s ease-out; }");

foreach (RuntimeUiDiagnostic report in result.Diagnostics)
{
    Debug.Log(report); // uss:1:12 Warning USS_UNSUPPORTED_PROPERTY: ...
}

RuntimeUiScreen hud = result.Screen;
hud.GetElement("slot1").AddClass("is-selected");
hud.GetElement("counter").SetText("42");
hud.Query("#hotbar > .slot").Clicked += element => Debug.Log("clicked " + element.Name);
```

A fresh world needs nothing else: on the first screen the host creates its own `UIDocument` panel
(GameObject `CoreAI Runtime UI`) with the theme stylesheet shipped in this package's `Resources`.

## Registration and replacement

| What you want | How |
|---|---|
| The default host | `builder.RegisterRuntimeUi();` |
| Configure it | `builder.RegisterRuntimeUi(new RuntimeUiHostOptions { PanelSortingOrder = 100, MaxScreens = 16 });` |
| Switch runtime UI off | `new RuntimeUiHostOptions { Enabled = false }` - every call then fails with `UI_DISABLED` |
| Your own host | register your `IRuntimeUiHost` **after** `RegisterRuntimeUi` (the later registration wins) |
| Render into your own UI | `ui.AttachTo(myRootVisualElement)` - a `UIDocument` root, a `PanelRenderer` root, a Hub page |
| Your own panel settings | `RuntimeUiHostOptions.PanelSettings` or `ThemeStyleSheet` |
| Without DI | `new RuntimeUiHost(options, logger)`; dispose it when done |

The host is one singleton registered as both `IRuntimeUiHost` and `IRuntimeUiTextInputFocus`; the scope
disposes it (all screens, the auto-created panel).

## Screens

| Call | Effect |
|---|---|
| `CreateScreen(id, uxml, uss, options)` | builds a screen; `options.SortOrder`, `Visible`, `Owner` (a mod id, for bulk removal) |
| `screen.ReplaceUss(uss)` | restyles the **same** tree; element state, classes and handles are untouched |
| `screen.ReplaceUxml(uxml)` / `Replace(uxml, uss)` | rebuilds the tree; state of elements whose **names** match carries over (below) |
| `screen.Show()` / `Hide()` | toggles the screen; hiding releases keyboard focus held inside it |
| `screen.SetSortOrder(n)` | higher draws on top; ties keep stack order, then creation order |
| `ui.PushScreen(screen, hidePrevious)` / `PopScreen()` | a screen stack; a pop re-shows what its push hid |
| `screen.Destroy()`, `ui.DestroyScreen(id)`, `ui.DestroyScreensOwnedBy(owner)` | releases elements, callbacks and subscribers - nothing stays connected |
| `screen.Width`, `screen.Height`, `screen.Resized` | the root's size in panel pixels after the panel's last layout (0 before it), and an event when the panel lays the root out at a new size; a layer that places elements in pixels itself (the Roblox GUI classes) lays out again on it |

Screen roots fill the panel and ignore picking, so a HUD does not block clicks to the game where it
draws nothing. `screen.Root` carries the class `cai-screen` and matches `:root`.

### What a UXML replace keeps

Element state is matched by **name** (and position among equal names, in document order):

- values the player changed: toggle, slider and text-field values, scroll offsets;
- everything set through an element handle: text, classes added or removed, styles, enabled state,
  attributes, list items, z-order, images;
- the handles themselves, with their subscribers (`Clicked` keeps firing on the new element).

Unnamed elements, elements created through the API since the last build, and names that are gone lose
their state; their handles detach (`IsAlive` is false, calls return false). Name the elements you care
about.

## Elements: the typed C# API

`RuntimeUiElement` is the handle on one element (`screen.Query`, `QueryAll`, `GetElement`,
`CreateElement`).

| Member | Meaning |
|---|---|
| `Text` / `SetText` | Label and Button text, Toggle text, TextField value, ProgressBar title, Slider label |
| `TryGetValue` / `SetValue(string)` | value as text: `true`/`false`, a number, the field text, the selected index |
| `TryGetNumber` / `SetNumber`, `TryGetBool` / `SetBool` | typed values; API writes do not raise `ValueChanged` |
| `Visible` | `display: flex` / `none` as an API style |
| `SetEnabled` | enables or disables the subtree (`:disabled` rules follow) |
| `AddClass`, `RemoveClass`, `ToggleClass`, `HasClass`, `GetClasses` | classes; the subtree restyles |
| `SetStyle(property, text)` | one property from USS text (longhand, shorthand or `--token`, `var()` allowed) |
| `SetStyle(UssPropertyId, UssValue)` | one longhand from a typed value, no text parsing (per-frame safe) |
| `ClearStyle`, `ClearAllStyles`, `TryGetResolvedStyle` | remove API styles; read what the cascade resolved |
| `SetAttribute(name, value)` | any UXML attribute of the kind (`low-value`, `placeholder-text`, ...) |
| `SetItems(strings)` | rows of a ListView (each row a Label, or the `item-template`) |
| `SetImage(Texture2D or Sprite)` | the image of an Image, the background of anything else |
| `SetZOrder(n)`, `BringToFront`, `SendToBack` | stacking among siblings; equal z keeps document order |
| `Focus`, `Blur`, `Query`, `CreateChild`, `Remove`, `Parent` | the rest |
| `ScrollOffset`, `SetScrollOffset(Vector2)` | how far a ScrollView is scrolled; the view clamps a write to its range, and the write raises no `ScrollChanged` (zero and `false` for any other kind) |
| events `Clicked`, `ValueChanged`, `Submitted`, `PointerEntered`, `PointerExited`, `FocusGained`, `FocusLost` | wired lazily; a throwing handler is logged and does not stop the others |
| event `ScrollChanged` | a ScrollView's offset moved by anything but `SetScrollOffset`: the wheel, a touch drag, a scroll bar, the keyboard, or the view clamping it after its content shrank |

API styles win over every rule and over the `style` attribute, and survive USS and UXML replaces.

### Building without markup

```csharp
RuntimeUiScreen gui = ui.CreateScreen("gui", null, null).Screen;
RuntimeUiElement frame = gui.CreateElement(RuntimeUiElementKind.VisualElement, null, "frame");
frame.SetStyle(UssPropertyId.Position, UssValue.FromKeyword("absolute"));
frame.SetStyle(UssPropertyId.Left, UssValue.FromLength(50f, UssUnit.Percent));
frame.SetStyle(UssPropertyId.Translate, UssValue.FromPair(-50f, UssUnit.Percent, 0f, UssUnit.Pixel));
RuntimeUiElement label = frame.CreateChild(RuntimeUiElementKind.Label, "title");
label.SetText("Hello");
label.SetZOrder(10);
```

Elements created this way are styled by the screen's USS and the theme exactly like markup elements.
`RuntimeUiElementFactory` and `RuntimeUiStyleApplier` are public too, for adapters that manage their own
elements.

## Supported UXML

Namespaces are optional: `ui:`, `engine:` and unprefixed names resolve to `UnityEngine.UIElements` even
without an `xmlns` declaration. Attribute values follow the editor importer (enum names are
case-insensitive).

| Element | Attributes (besides the common ones) |
|---|---|
| `VisualElement` | - |
| `Label` | `text`, `enable-rich-text`, `parse-escape-sequences`, `display-tooltip-when-elided` |
| `Button` | as `Label` |
| `Toggle` | `label`, `value`, `text`, `toggle-on-label-click` |
| `Slider` | `label`, `value`, `low-value`, `high-value`, `page-size`, `direction`, `inverted`, `show-input-field`, `fill` |
| `TextField` | `label`, `value` (or `text`), `max-length`, `multiline`, `password`, `mask-character`, `readonly`, `is-read-only`, `is-delayed`, `placeholder-text`, `hide-placeholder-on-focus`, `select-all-on-focus`, `select-all-on-mouse-up` |
| `ProgressBar` | `low-value`, `high-value`, `value`, `title` |
| `Image` | `image`, `sprite` (a registered key, a Resources path or `resource("path")`), `scale-mode`, `tint-color` |
| `ScrollView` | `mode`, `horizontal-scroller-visibility`, `vertical-scroller-visibility`, `elasticity`, `mouse-wheel-scroll-size`, `touch-scroll-type`, `horizontal-page-size`, `vertical-page-size`, `scroll-deceleration-rate` |
| `ListView` | `fixed-item-height`, `virtualization-method`, `show-border`, `selection-type`, `show-alternating-row-backgrounds`, `reorderable`, `header-title`, `show-foldout-header`, `show-add-remove-footer`, `item-template`, `horizontal-scrolling` |
| `Template` + `Instance` (+ `AttributeOverrides`) | templates are registered as text: `((UxmlTemplateRegistry)ui.Templates).Register("Slot", uxml)`; `src="UI/Slot.uxml"` finds `Slot` |

Common attributes: `name`, `class`, `style` (inline USS declarations), `tooltip`, `picking-mode`,
`focusable`, `tabindex`, `enabled`, `view-data-key`, `usage-hints`, `content-container`.

Forgiving by design: an unknown or unsupported element becomes a labelled red placeholder (class
`cai-ui-placeholder`) that keeps its name and children; unclosed and mismatched tags are repaired and
reported; unquoted attribute values are read with a warning; text content of a `Label` or `Button` is
used as its text (reported as Info, because the editor importer ignores it); `<Style src>` is ignored
with a report - pass the USS text instead.

## Supported USS

**Selectors**: type (`Button`, exact class name as in UI Toolkit), `*`, `.class`, `#name`, descendant
(whitespace) and child (`>`) combinators, selector lists, and the pseudo-classes `:hover`, `:active`,
`:inactive`, `:focus`, `:disabled`, `:enabled`, `:checked`, `:root` (the screen root).

**Cascade**: as UI Toolkit - theme sheets lose to the screen's sheet whatever their specificity; then
specificity (100 per `#name`, 10 per class or pseudo-class, 1 per type); then source order. The `style`
attribute beats every rule; API styles beat the `style` attribute. Selectors match the real UI Toolkit
hierarchy, so `ScrollView > Label` does not match (children sit in the content container) exactly as
with the editor importer, and internal parts of controls (`.unity-base-slider__dragger`, ...) can be
styled. Interaction states are tracked only on elements some selector tests, through UI Toolkit events;
nothing runs per frame.

**Custom properties**: `--name: value` on any rule, inherited by descendants; `var(--name)` and
`var(--name, fallback)` anywhere in a value, nested `var()` included; cycles are reported.

**Properties**:

| Area | Properties |
|---|---|
| Layout (flex) | `display` (flex, none), `position` (relative, absolute), `flex-direction`, `flex-wrap`, `flex-grow`, `flex-shrink`, `flex-basis`, `flex`, `align-items`, `align-self`, `align-content`, `justify-content`, `left`, `top`, `right`, `bottom`, `width`, `height`, `min-width`, `min-height`, `max-width`, `max-height`, `overflow` |
| Box | `margin` (+ 4 sides), `padding` (+ 4 sides), `border-width` (+ 4 sides) |
| Border | `border-color` (+ 4 sides), `border-radius` (+ 4 corners) |
| Text | `color`, `font-size`, `-unity-font-style`, `-unity-text-align`, `white-space`, `text-overflow`, `letter-spacing`, `word-spacing`, `-unity-paragraph-spacing`, `-unity-text-outline` (+ `-width`, `-color`), `text-shadow` |
| Background | `background-color`, `background-image`, `-unity-background-image-tint-color`, `-unity-background-scale-mode`, `-unity-slice-left/top/right/bottom`, `-unity-slice-scale` |
| Visual | `opacity`, `visibility` |
| Transform | `translate`, `scale`, `rotate`, `transform-origin` |
| Transition | `transition` and `transition-property`, `-duration`, `-timing-function`, `-delay` |

**Values**: lengths in `px` or `%` (a bare number is pixels), `auto` / `none` where USS allows them,
colours as `#rgb`, `#rgba`, `#rrggbb`, `#rrggbbaa`, `rgb()`, `rgba()` or a CSS colour name, times in `s`
or `ms`, angles in `deg`, `rad`, `grad` or `turn`, the 23 UI Toolkit easing names, and `initial` for any
property. Images come from `resource("path")` (a `Resources` path) or `url("key")`; register textures and
sprites with `RuntimeUiAssetRegistry.Register(key, texture)` (`ui.Assets`).

**Deliberately unsupported** (each reported once with its line): every at-rule (`@import`, `@media`,
`@keyframes`, ...), attribute selectors, sibling combinators (`+`, `~`), pseudo-elements, functional and
structural pseudo-classes (`:not()`, `:first-child`, ...), `!important` (ignored, the value still
applies), and the properties `cursor`, `-unity-font`, `-unity-font-definition`, `background-position`,
`background-repeat`, `background-size`, `aspect-ratio`, `filter`, `backdrop-filter`, `-unity-slice-type`,
`-unity-overflow-clip-box`, `-unity-text-auto-size`, the animation properties, and CSS-only names such
as `border`, `font`, `gap`, `z-index`, `box-shadow` (use `SetZOrder` for stacking).

## Theme: design tokens and semantic classes

`ui.Theme` holds design tokens (custom properties every screen root inherits) and a base sheet of
semantic classes that loses to any screen's rules:

| Tokens | Classes |
|---|---|
| `--cai-color-bg`, `-surface`, `-surface-raised`, `-surface-hover`, `-border`, `-text`, `-text-muted`, `-accent`, `-accent-hover`, `-on-accent`; `--cai-space-1..4`; `--cai-radius`; `--cai-font-size-small`, `-body`, `-h1` | `cai-panel`, `cai-row`, `cai-column`, `cai-h1`, `cai-text`, `cai-small`, `cai-muted`, `cai-btn`, `cai-btn-primary`, `is-hidden`, `is-selected`, `is-disabled` |

`ui.Theme.SetToken("--cai-color-accent", "rgb(80, 200, 255)")` restyles every screen at once; a screen
that declares the token itself keeps its own value. `SetBaseUss` replaces the semantic classes, and
`RuntimeUiHostOptions.Theme` replaces the whole theme.

## Input focus signals

In a `CoreAILifetimeScope` both signals are reported into the scope's `ICoreAiUiFocus` under
`coreai.runtime-ui` (`RuntimeUiHostFocusReporter`), so a focused field also stops the Roblox-style world's
controls (`gameProcessedEvent = true`) and the in-world chat's open keys; nothing else needs wiring.

- `IRuntimeUiTextInputFocus.IsTextInputFocused` and `TextInputFocusChanged`: a runtime-UI text field has
  keyboard focus, so a character or camera controller should ignore typing. Hiding or destroying the
  screen releases it; `RuntimeUiHost.ReleaseTextInputFocus()` drops it on demand (on Escape, say).
- `IRuntimeUiHost.IsPointerOverUi` and `PointerOverUiChanged`: the pointer is over a pickable element of
  a screen, so a click there is UI input rather than a world action. Give purely decorative elements
  `picking-mode="Ignore"` - a crosshair label at the centre of the screen would otherwise count as "over
  UI" whenever the cursor is locked to the centre.

## Reports

Every report is a `RuntimeUiDiagnostic` with a stable `Code`, a severity, its source (`uxml`, `uss`,
`style`, `api`), line and column. Parsing never throws into the game loop; limits
(`RuntimeUiParseLimits`: text size, element count, depth, template depth, rule count) stop at a report.

| Code | Meaning |
|---|---|
| `UXML_MALFORMED`, `UXML_UNCLOSED_ELEMENT`, `UXML_MISMATCHED_CLOSE_TAG` | the markup was repaired here |
| `UXML_UNKNOWN_ELEMENT`, `UXML_EDITOR_ONLY_ELEMENT` | a placeholder stands in |
| `UXML_UNSUPPORTED_ATTRIBUTE`, `UXML_INVALID_ATTRIBUTE_VALUE`, `UXML_TEXT_CONTENT`, `UXML_STYLE_REFERENCE_IGNORED` | an attribute or text was not applied as written |
| `UXML_TEMPLATE_NOT_FOUND`, `UXML_TEMPLATE_RECURSION`, `UXML_LIMIT_EXCEEDED` | templates and limits |
| `USS_SYNTAX`, `USS_UNSUPPORTED_AT_RULE`, `USS_UNSUPPORTED_SELECTOR`, `USS_UNSUPPORTED_PROPERTY`, `USS_INVALID_VALUE`, `USS_UNSUPPORTED_IMPORTANT` | a rule, selector or declaration was not applied |
| `USS_UNRESOLVED_VAR`, `USS_VAR_CYCLE`, `USS_UNRESOLVED_ASSET`, `USS_LIMIT_EXCEEDED` | resolved at cascade time |
| `UI_DISABLED`, `UI_SCREEN_EXISTS`, `UI_SCREEN_NOT_FOUND`, `UI_SCREEN_DESTROYED`, `UI_SCREEN_LIMIT`, `UI_ELEMENT_LIMIT`, `UI_TEXT_TOO_LARGE`, `UI_ELEMENT_DETACHED`, `UI_INVALID_SELECTOR`, `UI_UNSUPPORTED_OPERATION`, `UI_INVALID_ARGUMENT` | host and API failures (`RuntimeUiResult.ErrorCode`) |

## Performance

Building a screen parses, creates elements, runs the cascade once and writes inline styles; afterwards
nothing runs per frame. Styles change only on a replace, an API call, a theme change or an interaction
event (pointer enter or leave, press, focus, toggle) on an element some selector tests - and then only
that element, or its subtree when a selector tests the state on an ancestor. The P18 budget is a
200-element screen built in 10 ms or less with zero allocation per idle frame; `RuntimeUiBudgetEditModeTests`
measures both in the editor and the P19 harness in players.

## Layer map

| Layer | Assembly and folder | Contents |
|---|---|---|
| Domain (engine-free) | `CoreAI.Core`, `Assets/CoreAI/Runtime/Core/Features/RuntimeUi` | UXML reader, USS parser, value grammar, selector matching, cascade, `var()`, theme tokens, reports, the text-focus contract |
| Unity adapter | `CoreAI.Source`, `Assets/CoreAiUnity/Runtime/Source/Features/RuntimeUi` | element factory, style applier, screens and handles, host, auto-created panel, installer |
| Tests | `Assets/CoreAiUnity/Tests/EditMode/RuntimeUi` | the engine-free fixtures also run in the portable suite; the rest are EditMode, including a parity test against the editor importer |

There is no application layer between them: the host is the application service, and its contract is
the C# API above.

## From Lua

A mod (and the AI through it) builds its interface through one of two entrances, which draw on the same
host and share ownership, budgets, focus reporting and lifetime:

- `game:GetService("UxmlService")` hands UXML and USS text to the host above and gets screens and elements
  back as Lua values. The service is CoreAI-only - Roblox has no markup - so the export lint flags it.
- The Roblox GUI classes (`ScreenGui` in `PlayerGui`, `Frame`, `TextLabel`, `TextButton`, `TextBox`, ...)
  run a plain Roblox UI script unchanged (below, [The Roblox GUI entrance](#the-roblox-gui-entrance)).

### Setup and replacement

| What you want | How |
|---|---|
| Lua UI in a game | the scope registers the host (`builder.RegisterRuntimeUi()`) and the mods composition registers the Lua surface (`builder.RegisterRuntimeUiLua()`); every Rbx world the composition builds gets the service |
| No runtime UI (headless, dedicated server) | register no host: every screen call answers `UI_UNAVAILABLE` ("runtime UI is not available") |
| Runtime UI switched off | `new RuntimeUiHostOptions { Enabled = false }`: every screen call answers `UI_DISABLED` |
| Keep mods off the screen | mask the capability: `LuaCapabilities.All & ~LuaCapabilities.Ui` |
| Other budgets | `builder.RegisterRuntimeUiLua(new RbxUiBudgets { MaxScreensPerMod = 4 })`, or register an `RbxUiBudgets` |
| Your own UI host behind Lua | implement `IRbxUiBackend` (engine-free) and construct `RuntimeUiLuaSurface` with it |

`Ui` is part of `LuaCapabilities.All`, so an ordinary gameplay mod (`capabilities: All`) draws its HUD
without configuration. A mod whose header lists its tiers adds `Ui` to the list; without it every
member raises `UI_CAPABILITY_REQUIRED`.

### Ownership and lifetime

- A screen belongs to the mod that created it. Ids are private to the mod (the host id is
  `modId/id`), so two mods can both own a `"hud"`, and a mod sees and changes only its own screens
  (`UI_NOT_OWNER` otherwise). A one-off `execute_lua` chunk owns nothing and gets `UI_MOD_REQUIRED`.
- Unloading or quarantining the mod removes its screens and their handlers by the next frame.
- Hot reload: `CreateScreen` with an id the mod already owns replaces both texts of that screen, so
  values and handle state of elements whose names match survive, exactly as in a UXML replace. A
  screen the reloaded run neither re-creates nor looks up (`GetScreen`, `GetScreens`) is destroyed
  by the next frame.
- A screen lives as long as a connection of a loaded run of its mod claims it, so it follows the same
  rules as signal connections: a failed first load leaves no screen behind (a failed reload keeps the
  screens of the run it did not replace).
- Design tokens: `UxmlService:SetToken` overrides a token on the calling mod's screens only; the global
  theme belongs to the host (`ui.Theme`).

### The Lua API

`UxmlService`

| Member | Meaning |
|---|---|
| `CreateScreen(id, uxml, uss?, {SortOrder?, Visible?}?) -> UiScreen` | builds a screen, or replaces the mod's screen with that id |
| `GetScreen(id) -> UiScreen?`, `GetScreens() -> {UiScreen}` | the mod's own screens |
| `SetToken(name, value)`, `GetToken(name) -> string?` | design tokens of the mod's screens (`--cai-color-accent`, ...); `GetToken` falls back to the host theme |
| `TextInputFocused`, `PointerOverUi` (read-only), `TextInputFocusChanged(focused)` | the host's focus state |

`UiScreen`

| Member | Meaning |
|---|---|
| `Id`, `IsAlive`, `ElementCount`, `Diagnostics` | read-only |
| `Visible`, `SortOrder` | writable; `Show()`, `Hide()` too |
| `SetUxml(text) -> {Diagnostic}`, `SetUss(text) -> {Diagnostic}` | live replaces; USS restyles the same tree |
| `Query(selector)`, `QueryAll(selector)`, `GetElement(name)`, `Root` | elements |
| `CreateElement(kind, parent?, name?)` | an element without markup (`VisualElement`, `Label`, `Button`, `Toggle`, `Slider`, `TextField`, `ProgressBar`, `Image`, `ScrollView`, `ListView`) |
| `Destroy()`, `Destroying` | removal and its signal |

`UiElement`

| Member | Meaning |
|---|---|
| `Name`, `Kind`, `Parent`, `IsAlive` | read-only |
| `Text`, `Value`, `Visible`, `Enabled`, `ZOrder` | writable; `Value` is a boolean (Toggle), a number (Slider, ProgressBar, ListView selection) or text (TextField) |
| `AddClass`, `RemoveClass`, `ToggleClass`, `HasClass`, `GetClasses` | classes |
| `SetStyle(property, value) -> boolean`, `ClearStyle(property)`, `GetStyle(property) -> string?` | single style properties; `SetStyle` returns false (and reports) for an unsupported property or value |
| `SetAttribute(name, value) -> boolean`, `SetItems({string})` | UXML attributes, list rows |
| `Focus()`, `Blur()`, `Query(selector)`, `CreateChild(kind, name?)`, `Remove()` | the rest |
| `Clicked()`, `ValueChanged(value)`, `Submitted(text)` | ordinary script signals: `Connect` returns a connection, handlers run deferred in the mod scheduler under the mod's budgets, and unload disconnects them |

`Diagnostic = {Code, Severity, Source, Line, Column, Message}`. UXML and USS reports are returned, never
raised, so the AI's next turn can repair the text. A detached element (removed, or its name vanished in
a UXML replace) returns `nil` on reads and raises `UI_ELEMENT_DETACHED` on writes.

### Errors and budgets

Errors use the structured format with the UI code inside it:
`[mod:hotbar script:main.lua line:12] BUDGET_EXCEEDED: UI_BUDGET_SCREENS: ... | fix: ...`.

| Budget (per mod, `RbxUiBudgets`) | Default | Error |
|---|---|---|
| live screens | 32 | `UI_BUDGET_SCREENS` |
| authored elements over all screens | 4,096 | `UI_BUDGET_ELEMENTS` |
| UXML, USS or element text per call | 65,536 characters | `UI_TEXT_TOO_LARGE` |
| element writes per frame | 8,192 | `UI_BUDGET_WRITES` |

The budgets are per mod, sized so that one mod can carry a whole game's interface (HUD, inventory,
menus, shop, dialogue), and a host changes them through `RbxUiBudgets`. Rows of a `ListView` filled with
`SetItems` are virtualized and are not authored elements, so a long list costs one element.

The host's own limits (`MaxScreens`, `MaxElementsPerScreen`) stay the hard ceiling above these
(`UI_SCREEN_LIMIT`, `UI_ELEMENT_LIMIT`). Other codes: `UI_CAPABILITY_REQUIRED`, `UI_UNAVAILABLE`,
`UI_DISABLED`, `UI_MOD_REQUIRED`, `UI_NOT_OWNER`, `UI_SCREEN_EXISTS`, `UI_SCREEN_DESTROYED`,
`UI_ELEMENT_DETACHED`, `UI_INVALID_SELECTOR`, `UI_UNSUPPORTED_OPERATION`, `UI_INVALID_ARGUMENT`.

### Input focus

While a runtime-UI text field has keyboard focus or the pointer is over a pickable element, the surface
reports it into the shared `ICoreAiUiFocus` (surface id `coreai.runtime-ui`), which the mods composition
mirrors into the world's input focus: `UserInputService` then fires input with `gameProcessedEvent =
true`, so typing never moves the character and a click on a slot does not place a block.

### The Roblox GUI entrance

`RbxGuiPresenter` (engine-free, `Assets/CoreAIMods/Runtime/RbxApi/Instances/RuntimeUi`) shows every
`ScreenGui` in the local player's `PlayerGui` as one screen of this host (id `rbxgui/<n>`, owner = the mod
that made it, `DisplayOrder` = `SortOrder`, so a ScreenGui and a UXML screen stack on one scale) and every
GUI object as one element built by the same element factory: `Frame` is a `VisualElement`, `TextLabel` a
`Label`, `TextButton` a `Button`, `TextBox` a `TextField`, `ImageLabel`/`ImageButton` an `Image`,
`ScrollingFrame` a `ScrollView`. Elements nest like the instances. The presenter does Roblox layout itself
(scale and offset against the parent, `AnchorPoint`, `SizeConstraint`, `UIPadding`, `UIListLayout`,
`UIGridLayout`, the two constraints, `AutomaticSize`, `ScrollingFrame` canvas) and pushes absolute pixel
boxes through the typed style path (`IRbxUiElement.SetStyle(RbxUiStyleProperty, RbxUiStyleValue)`, which the
adapter maps to `RuntimeUiElement.SetStyle(UssPropertyId, UssValue)` without parsing text), so
`AbsolutePosition`/`AbsoluteSize` are exactly what is drawn: a border Roblox draws outside the box (an Outer
`UIStroke`, `BorderMode.Outline`) is the element's border, and the box is the element's area inside it. It
lays out again when `screen.Resized` fires.

- Per frame: writes only mark their object dirty; at `PreRender` the presenter lays out the screens whose
  layout changed and pushes only the style values that differ from the last push. An idle frame does
  nothing.
- Budgets and lifetime: a ScreenGui counts against the mod's screen budget, each GUI object against its
  element budget, a property write of a shown object against its write budget (`UI_BUDGET_*` above, shared
  with UxmlService). Parenting a GUI object into a shown screen over budget raises before the move. Unloading
  the mod destroys its instances and so its screens; a hot reload rebuilds them.
- Whose GUI: the `PlayerGui` of the player at this screen (`LuaCsRbxApiBindings.LocalPlayerActorId`, else the
  player the camera follows, else the only player). A mod loaded as that player's actor reaches it through
  `Players.LocalPlayer`; a mod the host loads is a server script (`LocalPlayer` nil) and reaches it through
  `Players:GetPlayers()` or `StarterGui`, which `RbxStarterGuiReplicator` copies into each `PlayerGui` when the
  character spawns (`ResetOnSpawn` honoured).
- Focus: a focused `TextBox` and the pointer over a button, a `TextBox`, an `Active` object or an object a
  script listens to `MouseEnter` on are reported like any other element of the host, so `UserInputService`
  marks that input `gameProcessed` and typing never moves the character.
- Scrolling: a `ScrollingFrame` is a `ScrollView` holding one canvas element (class `rbx-canvas`, not counted
  against the mod's element budget) sized to `AbsoluteCanvasSize` plus the strips of inset bars, so the
  view's own range is the Roblox one; the children are drawn on the canvas at their unscrolled places and the
  view's `scrollOffset` is `CanvasPosition`. A script's `CanvasPosition` is pushed with `SetScrollOffset`; the
  player's wheel, touch drag or bar comes back as `ScrollChanged` and becomes `CanvasPosition` without a write
  charge, and an axis `ScrollingEnabled`/`ScrollingDirection` closes is put straight back. `ScrollingDirection`
  is the view's `mode`; `ScrollBarThickness`, `ScrollBarImageColor3`/`ScrollBarImageTransparency` and the
  side (`rbx-scroll-left`/`rbx-scroll-right`) reach the bars through the screen USS (`--rbx-scroll-bar-size`,
  `--rbx-scroll-bar-color`), which draws them flat, over the canvas and without arrow buttons.
- What differs from Roblox (fonts, text measurement, `rbxassetid` images, scrolling, the stubbed signals) is
  listed in [RBX_API.md](../../CoreAI/Docs/RBX_API.md#roblox-gui).

### Layer map of the Lua surface

| Layer | Assembly and folder | Contents |
|---|---|---|
| Rules (engine-free) | `CoreAI.RbxApi.Instances`, `Assets/CoreAIMods/Runtime/RbxApi/Instances/RuntimeUi` | the `IRbxUiBackend` port, `RbxUiWorld` (ownership, claims, budgets, tokens), screen and element handles, `RbxUiErrors`, the `UxmlService` instance, `RbxGuiPresenter` and the Roblox layout (`RbxGuiLayout`) |
| Roblox GUI classes (engine-free) | `CoreAI.RbxApi.Instances`, `Assets/CoreAIMods/Runtime/RbxApi/Instances/Gui` | `ScreenGui`, `Frame`, the text, image and button classes, the `UI*` layout objects, `StarterGui`/`PlayerGui` and the StarterGui replicator, their enums and catalog rows |
| Focus | `CoreAI.RbxApi.Binding`, `Assets/CoreAIMods/Runtime/RbxApi/Binding/RuntimeUi` | `RuntimeUiFocusReporter` |
| Lua binding and adapter | `CoreAI.Mods`, `Assets/CoreAIMods/Runtime/Scripting/LuaCs/RuntimeUi` | the Lua userdata, `RuntimeUiLuaSurface`, `RuntimeUiHostBackend` (the adapter over `IRuntimeUiHost`), `RuntimeUiLuaInstaller` |
| Tests | `Assets/CoreAIMods/Tests/EditMode/RbxApi/RuntimeUi` | Lua-tier conformance over a fake host (also in the portable Lua suite), the same over the real host, the hotbar fixture |
| Roblox GUI tests | `Assets/CoreAIMods/Tests/EditMode/RbxApi/Gui`, `GuiLua`, `GuiUnity` | engine-free layout, presenter, StarterGui and allocation fixtures (portable suite), Lua-tier conformance (portable Lua suite), the real host |
