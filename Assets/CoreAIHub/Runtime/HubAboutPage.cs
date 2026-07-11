using System;
using CoreAI.Hub;
using UnityEngine;
using UnityEngine.UIElements;

namespace CoreAI.Hub.UI
{
    /// <summary>
    /// Minimal built-in <see cref="IHubPage"/> used as a self-test / demo tab. Returns a simple
    /// UI Toolkit panel with a title and body label so a freshly wired
    /// <see cref="CoreAiHubWindow"/> always shows at least one page.
    /// </summary>
    public sealed class HubAboutPage : HubPageBase
    {
        /// <summary>Default registry id for the built-in About page.</summary>
        public const string DefaultPageId = "coreai.hub.about";

        private readonly string _body;

        /// <summary>Creates the About page with an optional custom body message.</summary>
        public HubAboutPage(string pageId = DefaultPageId, string displayName = "About", int order = 1000,
            string body = null)
            : base(
                string.IsNullOrWhiteSpace(pageId) ? DefaultPageId : pageId,
                string.IsNullOrWhiteSpace(displayName) ? "About" : displayName,
                order)
        {
            _body = string.IsNullOrEmpty(body)
                ? "This is the CoreAI Hub — an optional UI Toolkit window that renders pages " +
                  "registered into the HubPageRegistry. C# modules and (later) Lua mods can add their own tabs."
                : body;
        }

        /// <inheritdoc />
        public override Func<object> CreatePageContent => BuildContent;

        private object BuildContent()
        {
            VisualElement panel = new() { name = "coreai-hub-about" };
            panel.AddToClassList("coreai-hub-about");
            panel.style.flexGrow = 1f;

            Label title = new(DisplayName) { name = "coreai-hub-about-title" };
            title.AddToClassList("coreai-hub-about-title");
            title.style.color = new Color(0.302f, 0.816f, 0.882f, 1f);
            title.style.fontSize = 22f;
            title.style.unityFontStyleAndWeight = FontStyle.Bold;
            title.style.marginBottom = 12f;
            panel.Add(title);

            Label body = new(_body) { name = "coreai-hub-about-body" };
            body.AddToClassList("coreai-hub-about-body");
            body.style.color = new Color(0.863f, 0.91f, 0.941f, 1f);
            body.style.fontSize = 16f;
            body.style.whiteSpace = WhiteSpace.Normal;
            panel.Add(body);

            return panel;
        }
    }
}