using UnityEngine;
using UnityEngine.UIElements;

namespace CoreAI.Ai.Hub
{
    /// <summary>
    /// Small shared UI Toolkit builders for the Mods page + editor so they render with the same
    /// semi-transparent, accent-on-dark look as the built-in Hub pages without depending on the
    /// (internal) <c>CoreAI.Hub.UI.HubPageWidgets</c> in the separate Hub module.
    /// </summary>
    internal static class HubModWidgets
    {
        internal static readonly Color Accent = new(0.302f, 0.816f, 0.882f, 1f);
        internal static readonly Color Text = new(0.863f, 0.91f, 0.941f, 1f);
        internal static readonly Color Muted = new(0.77f, 0.86f, 0.91f, 0.7f);
        internal static readonly Color Danger = new(0.92f, 0.45f, 0.42f, 1f);
        internal static readonly Color Panel = new(1f, 1f, 1f, 0.04f);
        internal static readonly Color Border = new(1f, 1f, 1f, 0.10f);

        internal static Label MakeTitle(string text)
        {
            Label title = new(text);
            title.style.color = Accent;
            title.style.fontSize = 22f;
            title.style.unityFontStyleAndWeight = FontStyle.Bold;
            title.style.marginBottom = 10f;
            title.style.whiteSpace = WhiteSpace.Normal;
            return title;
        }

        internal static Label MakeNote(string text)
        {
            Label note = new(text) { name = "coreai-hub-mods-note" };
            note.style.color = Muted;
            note.style.fontSize = 12f;
            note.style.marginTop = 6f;
            note.style.marginBottom = 6f;
            note.style.whiteSpace = WhiteSpace.Normal;
            return note;
        }

        internal static Label MakeStatus()
        {
            Label status = new(string.Empty) { name = "coreai-hub-mods-status" };
            status.style.color = Muted;
            status.style.fontSize = 12f;
            status.style.marginTop = 4f;
            status.style.marginBottom = 4f;
            status.style.whiteSpace = WhiteSpace.Normal;
            return status;
        }

        internal static Button MakeButton(string text, System.Action onClick)
        {
            Button button = new(onClick) { text = text };
            button.style.marginLeft = 0f;
            button.style.marginRight = 6f;
            button.style.marginTop = 2f;
            button.style.marginBottom = 2f;
            button.style.paddingLeft = 12f;
            button.style.paddingRight = 12f;
            button.style.paddingTop = 4f;
            button.style.paddingBottom = 4f;
            button.style.height = 26f;
            button.style.color = Text;
            button.style.backgroundColor = new Color(Accent.r, Accent.g, Accent.b, 0.18f);
            button.style.unityFontStyleAndWeight = FontStyle.Bold;
            SetBorder(button, new Color(Accent.r, Accent.g, Accent.b, 0.45f));
            SetRadius(button, 6f);
            return button;
        }

        internal static Button MakeDangerButton(string text, System.Action onClick)
        {
            Button button = MakeButton(text, onClick);
            button.style.color = Danger;
            button.style.backgroundColor = new Color(Danger.r, Danger.g, Danger.b, 0.14f);
            SetBorder(button, new Color(Danger.r, Danger.g, Danger.b, 0.45f));
            return button;
        }

        private static void SetBorder(VisualElement el, Color color)
        {
            el.style.borderTopWidth = 1f;
            el.style.borderBottomWidth = 1f;
            el.style.borderLeftWidth = 1f;
            el.style.borderRightWidth = 1f;
            el.style.borderTopColor = color;
            el.style.borderBottomColor = color;
            el.style.borderLeftColor = color;
            el.style.borderRightColor = color;
        }

        private static void SetRadius(VisualElement el, float r)
        {
            el.style.borderTopLeftRadius = r;
            el.style.borderTopRightRadius = r;
            el.style.borderBottomLeftRadius = r;
            el.style.borderBottomRightRadius = r;
        }

        /// <summary>A padded, bordered container used for rows and boxes.</summary>
        internal static VisualElement MakePanel()
        {
            VisualElement panel = new();
            panel.style.backgroundColor = Panel;
            panel.style.borderTopWidth = 1f;
            panel.style.borderBottomWidth = 1f;
            panel.style.borderLeftWidth = 1f;
            panel.style.borderRightWidth = 1f;
            panel.style.borderTopColor = Border;
            panel.style.borderBottomColor = Border;
            panel.style.borderLeftColor = Border;
            panel.style.borderRightColor = Border;
            panel.style.borderTopLeftRadius = 4f;
            panel.style.borderTopRightRadius = 4f;
            panel.style.borderBottomLeftRadius = 4f;
            panel.style.borderBottomRightRadius = 4f;
            panel.style.paddingTop = 6f;
            panel.style.paddingBottom = 6f;
            panel.style.paddingLeft = 8f;
            panel.style.paddingRight = 8f;
            panel.style.marginBottom = 4f;
            return panel;
        }

        internal static Label MakeFieldLabel(string text)
        {
            Label label = new(text);
            label.style.color = Text;
            label.style.fontSize = 13f;
            label.style.whiteSpace = WhiteSpace.Normal;
            return label;
        }

        internal static Label MakeMutedLabel(string text)
        {
            Label label = new(text);
            label.style.color = Muted;
            label.style.fontSize = 12f;
            label.style.whiteSpace = WhiteSpace.Normal;
            return label;
        }
    }
}
