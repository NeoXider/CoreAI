using System;
using System.Collections.Generic;
using System.Reflection;
using CoreAI.Chat;
using CoreAI.Infrastructure.Logging;
using UnityEngine.UIElements;

namespace CoreAI.ChatMarkdown
{
    /// <summary>
    /// Markdown bubbles for <see cref="CoreAiChatPanel"/>: the default <see cref="ICoreAiChatBubbleRenderer"/> while
    /// a Markdown backend is registered (<see cref="CoreAiMarkdownRendererRegistry"/>).
    /// <para>
    /// A finished answer becomes a <see cref="CoreAiMarkdownView"/>. A streaming bubble keeps its label, which the
    /// panel goes on writing into, hidden; a view inserted after it previews the label's text (complete blocks once
    /// each, the open tail throttled) and renders the bubble's own text once when the panel completes it. A turn split
    /// by tool rounds therefore renders each bubble once with its own segment, never the concatenated answer.
    /// </para>
    /// </summary>
    public sealed class CoreAiChatMarkdownBubbles : ICoreAiChatBubbleRenderer
    {
        /// <summary>Added to every Markdown bubble, next to the panel's <c>coreai-chat-message coreai-ai-message</c>.</summary>
        public const string BubbleUssClassName = "coreai-markdown-message";

        /// <summary>Wrapper of a finished answer's content: the hidden text label, then the Markdown view.</summary>
        public const string ContentUssClassName = "coreai-markdown-content";

        private const string BubbleFactoryMethodName = "CreateMessageBubble";
        private static readonly Type[] BubbleFactorySignature = { typeof(string), typeof(bool) };
        private static bool _failureLogged;

        private readonly Dictionary<Label, CoreAiMarkdownView> _streaming = new();
        private readonly HashSet<Label> _plainBubbles = new();
        private readonly ICoreAiMarkdownRenderer _renderer;
        private readonly Func<double> _clock;
        private readonly ICoreAiMarkdownLinkHandler _linkRouter;

        /// <summary>
        /// Bubbles rendered by <paramref name="renderer"/>, or by the backend registered at render time when it is null.
        /// </summary>
        public CoreAiChatMarkdownBubbles(ICoreAiMarkdownRenderer renderer = null) : this(renderer, null)
        {
        }

        internal CoreAiChatMarkdownBubbles(ICoreAiMarkdownRenderer renderer, Func<double> clock)
        {
            _renderer = renderer;
            _clock = clock;
            _linkRouter = new LinkRouter(this);
        }

        /// <summary>
        /// What activating a link in a bubble does; null (the default) leaves links inert. Read when a link is
        /// activated, so a change applies to bubbles already shown.
        /// </summary>
        public ICoreAiMarkdownLinkHandler LinkHandler { get; set; }

        /// <summary>Lets finished bubbles render images and videos (the backend may fetch them); false by default.</summary>
        public bool AllowMedia { get; set; }

        /// <summary>Bubbles currently streaming through a preview.</summary>
        internal int StreamingBubbleCount => _streaming.Count;

        /// <summary>
        /// The renderer a panel uses when its host chose none: Markdown bubbles while a backend is registered,
        /// otherwise null (the panel's plain labels, at no cost). A panel whose type overrides
        /// <c>CreateMessageBubble</c> renders its own bubbles, so it gets null as well and opts in by setting
        /// <see cref="CoreAiChatPanel.BubbleRenderer"/>.
        /// </summary>
        public static ICoreAiChatBubbleRenderer CreateDefaultFor(CoreAiChatPanel panel)
        {
            if (panel == null || !CoreAiMarkdownRendererRegistry.IsAvailable || OverridesBubbleFactory(panel.GetType()))
            {
                return null;
            }

            return new CoreAiChatMarkdownBubbles();
        }

        /// <summary>True when <paramref name="panelType"/> replaces the panel's bubble factory.</summary>
        internal static bool OverridesBubbleFactory(Type panelType)
        {
            if (panelType == null || panelType == typeof(CoreAiChatPanel))
            {
                return false;
            }

            MethodInfo factory = panelType.GetMethod(BubbleFactoryMethodName,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, BubbleFactorySignature,
                null);
            return factory != null && factory.DeclaringType != typeof(CoreAiChatPanel);
        }

        /// <inheritdoc />
        /// <remarks>
        /// The content is the panel's usual label, hidden, followed by the view. Every assistant bubble therefore still
        /// holds a <c>Label.coreai-ai-message</c> with its text, streamed or not, so host code and tests that find
        /// bubbles by that class keep working with Markdown on.
        /// </remarks>
        public VisualElement CreateAssistantContent(string text)
        {
            ICoreAiMarkdownRenderer renderer = _renderer ?? CoreAiMarkdownRendererRegistry.Current;
            if (renderer == null)
            {
                return null;
            }

            VisualElement content = new();
            content.AddToClassList(ContentUssClassName);
            content.style.flexShrink = 1f;
            content.style.minWidth = 0f;
            Label source = new(text ?? "");
            source.AddToClassList("coreai-chat-message");
            source.AddToClassList("coreai-ai-message");
            source.style.display = DisplayStyle.None;
            content.Add(source);
            CoreAiMarkdownView view = CreateView(renderer);
            view.Markdown = text;
            content.Add(view);
            return content;
        }

        /// <inheritdoc />
        public void OnStreamingTextChanged(Label bubble)
        {
            if (bubble == null || _plainBubbles.Contains(bubble))
            {
                return;
            }

            try
            {
                if (!_streaming.TryGetValue(bubble, out CoreAiMarkdownView view))
                {
                    ICoreAiMarkdownRenderer renderer = _renderer ?? CoreAiMarkdownRendererRegistry.Current;
                    VisualElement parent = bubble.parent;
                    if (renderer == null || parent == null)
                    {
                        return;
                    }

                    view = CreateView(renderer);
                    view.AddToClassList(CoreAiChatPanel.StreamingActiveUssClassName);
                    parent.Insert(parent.IndexOf(bubble) + 1, view);
                    bubble.style.display = DisplayStyle.None;
                    _streaming[bubble] = view;
                }

                view.UpdateStreaming(bubble.text);
            }
            catch (Exception exception)
            {
                FallBackToLabel(bubble, exception);
            }
        }

        /// <inheritdoc />
        public void OnStreamingBubbleCompleted(Label bubble)
        {
            if (bubble == null)
            {
                return;
            }

            if (_plainBubbles.Remove(bubble) || !_streaming.TryGetValue(bubble, out CoreAiMarkdownView view))
            {
                return;
            }

            _streaming.Remove(bubble);
            try
            {
                view.RemoveFromClassList(CoreAiChatPanel.StreamingActiveUssClassName);
                view.CompleteStreaming(bubble.text);
            }
            catch (Exception exception)
            {
                FallBackToLabel(bubble, exception);
                _plainBubbles.Remove(bubble);
            }
        }

        private CoreAiMarkdownView CreateView(ICoreAiMarkdownRenderer renderer)
        {
            CoreAiMarkdownView view = new(null, renderer, _clock)
            {
                LinkHandler = _linkRouter,
                AllowMedia = AllowMedia
            };
            view.AddToClassList("coreai-chat-message");
            view.AddToClassList("coreai-ai-message");
            view.AddToClassList(BubbleUssClassName);
            return view;
        }

        private sealed class LinkRouter : ICoreAiMarkdownLinkHandler
        {
            private readonly CoreAiChatMarkdownBubbles _owner;

            public LinkRouter(CoreAiChatMarkdownBubbles owner)
            {
                _owner = owner;
            }

            public void OnLinkActivated(string target)
            {
                _owner.LinkHandler?.OnLinkActivated(target);
            }
        }

        /// <summary>A bubble that could not be previewed goes back to the panel's own label for the rest of its life.</summary>
        private void FallBackToLabel(Label bubble, Exception exception)
        {
            if (_streaming.TryGetValue(bubble, out CoreAiMarkdownView view))
            {
                view.RemoveFromHierarchy();
                _streaming.Remove(bubble);
            }

            bubble.style.display = StyleKeyword.Null;
            _plainBubbles.Add(bubble);
            if (_failureLogged)
            {
                return;
            }

            _failureLogged = true;
            GameLoggerUnscopedFallback.Instance.LogWarning(GameLogFeature.Core,
                "[CoreAiChatMarkdownBubbles] A Markdown bubble failed and shows plain text; later failures are not " +
                "logged. " + exception);
        }
    }
}
