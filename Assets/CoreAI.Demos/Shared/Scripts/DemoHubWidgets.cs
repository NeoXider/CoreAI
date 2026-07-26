using System;
using UnityEngine;
using UnityEngine.UIElements;

namespace CoreAI.Demos
{
    /// <summary>
    /// Shared UI Toolkit builders for the demo Hub pages, so every demo renders with the same look as the
    /// built-in Hub pages (title / section / key-value row / note) plus the interactive controls demos need
    /// (buttons, primary buttons, toggles). Colors are kept byte-identical to the internal
    /// <c>CoreAI.Hub.UI.HubPageWidgets</c> (which demos can't reference across the asmdef boundary) so the
    /// demo pages and the built-in Settings/Mods/Statistics pages look like one cohesive Hub.
    /// </summary>
    public static class DemoHubWidgets
    {
        /// <summary>Cyan accent used for titles, section headers, and primary buttons.</summary>
        public static readonly Color Accent = new(0.302f, 0.816f, 0.882f, 1f);

        /// <summary>Primary body text color.</summary>
        public static readonly Color Text = new(0.863f, 0.91f, 0.941f, 1f);

        /// <summary>Muted secondary text color.</summary>
        public static readonly Color Muted = new(0.77f, 0.86f, 0.91f, 0.7f);

        private static readonly Color ButtonBg = new(0.16f, 0.2f, 0.24f, 1f);
        private static readonly Color ButtonBgHover = new(0.22f, 0.28f, 0.34f, 1f);

        /// <summary>Creates a scrollable page root with a title and standard padding.</summary>
        public static ScrollView CreatePage(string title, out VisualElement body)
        {
            ScrollView scroll = new(ScrollViewMode.Vertical) { name = "coreai-demo-page-scroll" };
            scroll.style.flexGrow = 1f;

            body = scroll.contentContainer;
            body.style.flexGrow = 1f;
            body.style.paddingLeft = 4f;
            body.style.paddingRight = 4f;

            if (!string.IsNullOrEmpty(title))
            {
                body.Add(MakeTitle(title));
            }

            return scroll;
        }

        public static Label MakeTitle(string text)
        {
            Label title = new(text)
            {
                style =
                {
                    color = Accent,
                    fontSize = 22f,
                    unityFontStyleAndWeight = FontStyle.Bold,
                    marginBottom = 12f,
                    whiteSpace = WhiteSpace.Normal
                }
            };
            return title;
        }

        public static Label MakeSection(string text)
        {
            Label section = new(text)
            {
                style =
                {
                    color = Accent,
                    fontSize = 16f,
                    unityFontStyleAndWeight = FontStyle.Bold,
                    marginTop = 14f,
                    marginBottom = 6f,
                    whiteSpace = WhiteSpace.Normal
                }
            };
            return section;
        }

        /// <summary>A wrapping body paragraph.</summary>
        public static Label MakeBody(string text)
        {
            Label label = new(text)
            {
                style =
                {
                    color = Text,
                    fontSize = 14f,
                    marginBottom = 4f,
                    whiteSpace = WhiteSpace.Normal
                }
            };
            return label;
        }

        /// <summary>A "label : value" row; the value label is returned for live updates.</summary>
        public static VisualElement MakeRow(string label, string value, out Label valueLabel)
        {
            VisualElement row = new();
            row.style.flexDirection = FlexDirection.Row;
            row.style.justifyContent = Justify.SpaceBetween;
            row.style.marginBottom = 3f;

            Label key = new(label)
            {
                style =
                {
                    color = Muted, fontSize = 14f, flexShrink = 1f, whiteSpace = WhiteSpace.Normal
                }
            };

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

        public static Label MakeNote(string text)
        {
            Label note = new(text)
            {
                style =
                {
                    color = Muted,
                    fontSize = 12f,
                    marginTop = 12f,
                    whiteSpace = WhiteSpace.Normal
                }
            };
            return note;
        }

        /// <summary>A standard action button wired to <paramref name="onClick"/>.</summary>
        public static Button MakeButton(string text, Action onClick)
        {
            Button button = new(onClick) { text = text };
            StyleButton(button, false);
            return button;
        }

        /// <summary>An accented primary button (the demo's main call to action).</summary>
        public static Button MakePrimaryButton(string text, Action onClick)
        {
            Button button = new(onClick) { text = text };
            StyleButton(button, true);
            return button;
        }

        /// <summary>A labelled toggle wired to <paramref name="onChanged"/>.</summary>
        public static Toggle MakeToggle(string label, bool value, EventCallback<ChangeEvent<bool>> onChanged)
        {
            Toggle toggle = new(label) { value = value };
            toggle.style.color = Text;
            toggle.style.marginTop = 4f;
            toggle.style.marginBottom = 4f;
            toggle.RegisterValueChangedCallback(onChanged);
            return toggle;
        }

        /// <summary>A horizontal container for laying buttons out in a row (wraps on narrow panels).</summary>
        public static VisualElement MakeButtonRow()
        {
            VisualElement row = new();
            row.style.flexDirection = FlexDirection.Row;
            row.style.flexWrap = Wrap.Wrap;
            row.style.marginTop = 6f;
            row.style.marginBottom = 6f;
            return row;
        }

        private static void StyleButton(Button button, bool primary)
        {
            button.style.marginRight = 6f;
            button.style.marginTop = 3f;
            button.style.marginBottom = 3f;
            button.style.paddingLeft = 12f;
            button.style.paddingRight = 12f;
            button.style.paddingTop = 6f;
            button.style.paddingBottom = 6f;
            button.style.borderTopLeftRadius = 6f;
            button.style.borderTopRightRadius = 6f;
            button.style.borderBottomLeftRadius = 6f;
            button.style.borderBottomRightRadius = 6f;
            button.style.borderTopWidth = 0f;
            button.style.borderRightWidth = 0f;
            button.style.borderBottomWidth = 0f;
            button.style.borderLeftWidth = 0f;
            button.style.unityFontStyleAndWeight = FontStyle.Bold;

            if (primary)
            {
                button.style.backgroundColor = Accent;
                button.style.color = new Color(0.05f, 0.09f, 0.11f, 1f);
            }
            else
            {
                button.style.backgroundColor = ButtonBg;
                button.style.color = Text;
                button.RegisterCallback<MouseEnterEvent>(_ => button.style.backgroundColor = ButtonBgHover);
                button.RegisterCallback<MouseLeaveEvent>(_ => button.style.backgroundColor = ButtonBg);
            }
        }
    }
}
