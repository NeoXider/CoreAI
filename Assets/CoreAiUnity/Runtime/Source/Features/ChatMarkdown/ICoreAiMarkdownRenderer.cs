using System;
using UnityEngine.UIElements;

namespace CoreAI.ChatMarkdown
{
    /// <summary>
    /// A Markdown-to-UI Toolkit backend. CoreAI ships no backend itself: the optional
    /// <c>CoreAI.ChatMarkdown.Renderer</c> assembly compiles only when <c>com.neoxider.markdownrenderer</c> is
    /// installed and registers one in <see cref="CoreAiMarkdownRendererRegistry"/>. A host may register its own.
    /// </summary>
    /// <remarks>
    /// The backend only turns Markdown into elements. Everything else (sanitising model text, streaming in stable
    /// blocks, tables, link policy, copy, plain-text fallback) is done by <see cref="CoreAiMarkdownView"/>, so a
    /// backend receives Markdown that <c>ChatMarkdownText.PrepareForRender</c> already made safe.
    /// </remarks>
    public interface ICoreAiMarkdownRenderer
    {
        /// <summary>Style sheet the rendered elements expect; null for none. Added once to each view.</summary>
        StyleSheet StyleSheet { get; }

        /// <summary>
        /// Renders <paramref name="markdown"/> into a new element. Called on the main thread. The backend must never
        /// open a link itself: every activated link target goes to <paramref name="linkActivated"/>, and the view
        /// decides what happens.
        /// </summary>
        VisualElement Render(string markdown, Action<string> linkActivated);
    }
}
