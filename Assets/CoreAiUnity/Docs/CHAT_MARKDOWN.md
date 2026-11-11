# Chat Markdown (optional renderer)

Models answer in Markdown: headings, lists, tables, fenced code. With the optional package
`com.neoxider.markdownrenderer` installed, `CoreAiChatPanel` (and so the Hub chat page) renders assistant answers as
Markdown, while they stream and when they are finished. Without the package nothing changes: the panel keeps its
plain labels, nothing extra compiles, and no message does any extra work.

The same renderer is available to any other UI through one element, `CoreAiMarkdownView`.

## Install

**CoreAI > Setup > Modules > Markdown Renderer > Install (v1.2.1)**, or add one line to `Packages/manifest.json`:

```json
"com.neoxider.markdownrenderer": "https://github.com/NeoXider/NeoxiderMarkdownRenderer.git#v1.2.1"
```

The package is separate from CoreAI and carries its own licence: Neoxider's changes are MIT, the original Markdown
UIElement code it derives from stays under the Unity Companion License, and the bundled Markdig parser is
BSD-2-Clause (`LICENSE.md` and `Third Party Notices.md` inside the package). No CoreAI package depends on it.

When the package is present, its version define `COREAI_HAS_MARKDOWN_RENDERER` compiles the small adapter assembly
`CoreAI.ChatMarkdown.Renderer`, which registers the renderer when a player or a play session starts.
**CoreAI > Setup > Modules > Report module status** shows whether it is installed.

## What you get

| Markdown | In the chat |
|---|---|
| `#`..`######` headings, `**bold**`, `*italic*`, `` `code` ``, quotes, nested lists, rules | rendered |
| fenced code | a code block; Python and C# are syntax-highlighted |
| pipe tables | a grid with a header row and column alignment (built by CoreAI; the package has no tables) |
| links | underlined, **inert by default** (see Links) |
| images and videos | shown as links; nothing is downloaded unless you allow media |
| raw HTML and rich-text tags (`<size=…>`, `<color=…>`) | shown as text, never applied |

User messages and tool-call lines always stay plain text. The in-world chat overlay is a plain one-line feed: answer
and NPC lines are shown without Markdown markers (`ChatMarkdownText.ToPlainText`), with or without the package.

### While an answer streams

A streaming bubble keeps the panel's label (hidden) as its text buffer, and a `CoreAiMarkdownView` after it shows the
text as Markdown. Complete blocks are rendered once each; only the open tail is re-rendered, at most every 80-250 ms
depending on its length, so a long answer causes no per-token re-render storm. Markers that are still open because the
answer is mid-sentence (`**bol`, a backtick, a link whose URL is still arriving, a lone `#` or `-`) are hidden until
their partner arrives, and an unterminated code fence is closed, so the layout never breaks.

When a tool round splits a turn into several bubbles, each bubble is rendered exactly once more when it ends, with
its own segment of the answer.

### Copy

Ctrl+C (Cmd+C on macOS) on a Markdown bubble with nothing selected copies the **original Markdown** of that message.
A selection inside a paragraph copies the selected visible text, as with plain bubbles. From code:
`view.CopyMarkdownToClipboard()`.

## Links

Model text is untrusted, so a link does nothing until the host decides. Give the panel a handler:

```csharp
panel.BubbleRenderer = new CoreAiChatMarkdownBubbles
{
    LinkHandler = CoreAiMarkdownLinkHandlers.OpenWebLinks // opens only absolute http/https URLs
};
```

or implement `ICoreAiMarkdownLinkHandler` (an in-game navigation, a confirmation dialog, a whitelist). The target is
passed exactly as the model wrote it.

## Markdown in other UI

```csharp
CoreAiMarkdownView view = new(mod.Description);   // renders, or shows plain words without the package
view.LinkHandler = myLinkHandler;                 // optional
container.Add(view);

// streaming text: call for every piece, then set the final text once
view.UpdateStreaming(partialText);
view.Markdown = finalText;
```

In UXML: `<CoreAI.ChatMarkdown.CoreAiMarkdownView markdown="**Hello**" />`. Edit-mode tools (UI Builder, inspectors)
show plain text, because the renderer registers when play starts; call
`NeoxiderMarkdownRendererRegistration.Register()` from your own editor code to preview Markdown there.

## Styling

The default look for the dark chat theme is
`Runtime/Source/Features/ChatMarkdown/Renderer/Resources/CoreAI/UI/CoreAiChatMarkdown.uss`. Text colour, size and
wrapping come from the bubble (`.coreai-chat-message`). That sheet is attached to each view, so a rule in a sheet higher
up (for example the panel's **Custom USS**) needs a more specific selector to win, such as
`.coreai-chat-container .coreai-markdown .codeblock`; or replace the default sheet altogether (below). The selectors:

| Selector | Element |
|---|---|
| `.coreai-markdown` | the view; `.coreai-markdown-message` marks it in a chat bubble |
| `.coreai-markdown .h1` … `.h6` | heading labels |
| `.coreai-markdown .paragraph`, `.list`, `.quote` | paragraph and list labels, quote block |
| `.coreai-markdown .codeblock`, `.codeblock Label` | code block and its lines |
| `.coreai-markdown .line` | horizontal rule |
| `.coreai-markdown__table`, `__table-row`, `__table-row--header`, `__table-cell` | tables |
| `.coreai-markdown__plain` | plain-text fallback |

Inline-code and link colours are written into rich text, so USS cannot change them; set them on the renderer:

```csharp
CoreAiMarkdownRendererRegistry.Register(new NeoxiderMarkdownRenderer
{
    InlineCodeColor = new Color32(74, 0, 221, 255), // or InlineCodeInheritsTextColor = true
    LinkInheritsTextColor = true,
    StyleSheet = myMarkdownSheet                     // replaces the default sheet; null = only your USS
});
```

Call it after the module registers, for example from a `[RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]`
method. The package's own `Styles/MarkdownRenderer.uss` is not used: it is loaded through `AssetDatabase`, so it would
look different in a player; assign it through `StyleSheet` if you want its look and ship it yourself.

## Switching it off

| Scope | How |
|---|---|
| one panel | `panel.BubbleRenderer = null;` |
| the whole project, at compile time | add the scripting define `COREAI_DISABLE_MARKDOWN_RENDERER` |
| the whole project, at run time | `CoreAiMarkdownRendererRegistry.Register(null)` from a `BeforeSceneLoad` method |
| for good | **CoreAI > Setup > Modules > Markdown Renderer > Remove package** |

A panel subclass that overrides `CreateMessageBubble` already renders its own bubbles, so it keeps doing so; set
`BubbleRenderer` to opt in.

## Your own renderer

Implement `ICoreAiMarkdownRenderer` (turn prepared Markdown into a `VisualElement`, pass link clicks to the callback)
and register it with `CoreAiMarkdownRendererRegistry.Register`. Sanitising, streaming, tables, links, copy and the
plain fallback stay in `CoreAiMarkdownView`; a panel's whole bubble rendering can also be replaced through
`ICoreAiChatBubbleRenderer`.

## Layers

| Layer | Where | What |
|---|---|---|
| Domain, engine-free | `com.neoxider.coreai`: `Features/ChatOverlay/ChatMarkdownText.cs` (+ `ChatMarkdownInline`, `ChatMarkdownSegment`) | plain text, render-safe Markdown, stable streaming prefix, pipe tables; also runs in the portable suite |
| Seam, always compiled | `com.neoxider.coreaiunity`: `Runtime/Source/Features/ChatMarkdown` (part of `CoreAI.Source`) | `CoreAiMarkdownView`, `ICoreAiMarkdownRenderer` + registry, `ICoreAiChatBubbleRenderer`, `CoreAiChatMarkdownBubbles`, link handlers |
| Adapter, optional | `Runtime/Source/Features/ChatMarkdown/Renderer` (`CoreAI.ChatMarkdown.Renderer`) | the only code that names the package; compiles only with it |

Tests: `ChatMarkdownTextEditModeTests` and `ChatMarkdownArchitectureFitnessEditModeTests` (engine-free, also portable),
`CoreAiChatMarkdownBubblesEditModeTests` (the seam with a fake backend, package not needed), and
`NeoxiderMarkdownRendererEditModeTests` in `CoreAI.ChatMarkdown.Renderer.Tests` (only with the package).

## Players and WebGL

The adapter, the package runtime assembly and its bundled Markdig compile against the WebGL player API without
editor references; Markdig uses unsafe code, which IL2CPP supports. The adapter carries `[AlwaysLinkAssembly]`, so
managed stripping keeps it. Images are never fetched by default, so no `UnityWebRequest` leaves the page because of a
model answer. Copy goes through `GUIUtility.systemCopyBuffer`, Unity's clipboard; whether it reaches the browser
clipboard in a WebGL page depends on the Unity version and browser, so check it in your build. The package references
the Video, UnityWebRequest and UnityWebRequestTexture built-in modules, so keep them enabled.

## Known limits

- Code blocks use the chat font; the package ships no monospace font.
- The package numbers every ordered list from 1, and drops a code block nested in a list item. CoreAI lifts such
  fences to the top level so no code is lost; the list items after it then count from 1 again.
- Only Python and C# are highlighted; other languages (including Lua) are shown uncoloured.
