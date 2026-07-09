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
        internal static readonly Color Accent = new(0.42f, 0.87f, 0.95f, 1f);
        internal static readonly Color Text = new(0.93f, 0.96f, 0.98f, 1f);
        internal static readonly Color Muted = new(0.80f, 0.88f, 0.93f, 0.95f);
        internal static readonly Color Danger = new(0.98f, 0.55f, 0.52f, 1f);
        internal static readonly Color Panel = new(1f, 1f, 1f, 0.07f);
        internal static readonly Color Border = new(1f, 1f, 1f, 0.14f);

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

        /// <summary>Forces a Toggle's label to <see cref="Text"/> so it stays readable over the Hub's
        /// dark gradient (the default toggle label colour is close to invisible there).</summary>
        internal static void StyleToggleLabel(Toggle toggle)
        {
            Label label = toggle?.Q<Label>();
            if (label != null)
            {
                label.style.color = Text;
            }
        }

        /// <summary>
        /// Forces a Foldout's title text to a bright accent + bold so it stays readable over the Hub
        /// content's light-to-dark gradient (the default foldout label colour vanishes near the bottom).
        /// Also enlarges the toggle arrow's hit target.
        /// </summary>
        internal static void StyleFoldoutTitle(Foldout foldout)
        {
            if (foldout == null)
            {
                return;
            }

            Toggle toggle = foldout.Q<Toggle>();
            Label title = toggle?.Q<Label>() ?? foldout.Q<Label>();
            if (title != null)
            {
                title.style.color = Accent;
                title.style.fontSize = 14f;
                title.style.unityFontStyleAndWeight = FontStyle.Bold;
            }
        }

        /// <summary>A dark, monospace-ish multiline code field that matches the dark theme (the default
        /// TextField renders as a jarring white box in the dark Hub).</summary>
        internal static void StyleCodeField(TextField field)
        {
            if (field == null)
            {
                return;
            }

            VisualElement input = field.Q("unity-text-input") ?? field;
            input.style.backgroundColor = new Color(0.05f, 0.09f, 0.16f, 0.96f);
            input.style.color = new Color(0.86f, 0.93f, 0.86f, 1f);
            SetBorder(input, new Color(Accent.r, Accent.g, Accent.b, 0.30f));
            SetRadius(input, 6f);
            input.style.unityTextAlign = TextAnchor.UpperLeft;
            input.style.whiteSpace = WhiteSpace.Normal;
            input.style.paddingLeft = 10f;
            input.style.paddingRight = 10f;
            input.style.paddingTop = 8f;
            input.style.paddingBottom = 8f;
            field.style.fontSize = 13f;
        }
    }
}
