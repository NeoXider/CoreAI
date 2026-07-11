using UnityEngine;
using UnityEngine.UIElements;

namespace CoreAI.Hub.UI
{
    /// <summary>
    /// Small shared UI Toolkit builders for the built-in Hub pages (title, section header, key/value row)
    /// so Settings and Statistics render with a consistent look without an external stylesheet.
    /// </summary>
    internal static class HubPageWidgets
    {
        // Accent/Text/Muted are duplicated (kept byte-identical) in CoreAI.Ai.Hub.HubModWidgets
        // (CoreAIMods/Runtime/Hub/HubModWidgets.cs) so the Mods tab matches these built-in pages; that
        // module can't reference this internal type across the asmdef boundary. Keep both in sync by hand.
        internal static readonly Color Accent = new(0.302f, 0.816f, 0.882f, 1f);
        internal static readonly Color Text = new(0.863f, 0.91f, 0.941f, 1f);
        internal static readonly Color Muted = new(0.77f, 0.86f, 0.91f, 0.7f);

        /// <summary>Creates a scrollable page root with a title and standard padding.</summary>
        internal static ScrollView CreatePage(string title, out VisualElement body)
        {
            ScrollView scroll = new(ScrollViewMode.Vertical) { name = "coreai-hub-page-scroll" };
            scroll.style.flexGrow = 1f;

            body = scroll.contentContainer;
            body.style.flexGrow = 1f;

            if (!string.IsNullOrEmpty(title))
            {
                body.Add(MakeTitle(title));
            }

            return scroll;
        }

        internal static Label MakeTitle(string text)
        {
            Label title = new(text);
            title.style.color = Accent;
            title.style.fontSize = 22f;
            title.style.unityFontStyleAndWeight = FontStyle.Bold;
            title.style.marginBottom = 12f;
            title.style.whiteSpace = WhiteSpace.Normal;
            return title;
        }

        internal static Label MakeSection(string text)
        {
            Label section = new(text);
            section.style.color = Accent;
            section.style.fontSize = 16f;
            section.style.unityFontStyleAndWeight = FontStyle.Bold;
            section.style.marginTop = 14f;
            section.style.marginBottom = 6f;
            section.style.whiteSpace = WhiteSpace.Normal;
            return section;
        }

        /// <summary>Creates a "label : value" row and returns it; the value label is exposed for live updates.</summary>
        internal static VisualElement MakeRow(string label, string value, out Label valueLabel)
        {
            VisualElement row = new();
            row.style.flexDirection = FlexDirection.Row;
            row.style.justifyContent = Justify.SpaceBetween;
            row.style.marginBottom = 3f;

            Label key = new(label);
            key.style.color = Muted;
            key.style.fontSize = 14f;
            key.style.flexShrink = 1f;
            key.style.whiteSpace = WhiteSpace.Normal;

            valueLabel = new Label(value)
            {
                style =
                {
                    color = Text,
                    fontSize = 14f,
                    unityTextAlign = TextAnchor.MiddleRight,
                    marginLeft = 12f,
                    flexShrink = 0f,
                    whiteSpace = WhiteSpace.Normal
                }
            };

            row.Add(key);
            row.Add(valueLabel);
            return row;
        }

        /// <summary>Convenience overload when the value label is not needed for later updates.</summary>
        internal static VisualElement MakeRow(string label, string value)
        {
            return MakeRow(label, value, out _);
        }

        internal static Label MakeNote(string text)
        {
            Label note = new(text);
            note.style.color = Muted;
            note.style.fontSize = 12f;
            note.style.marginTop = 12f;
            note.style.whiteSpace = WhiteSpace.Normal;
            return note;
        }
    }
}