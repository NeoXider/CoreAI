using System;
using System.Collections.Generic;
using System.Globalization;

namespace CoreAI.RuntimeUi
{
    /// <summary>Value type of a UXML attribute.</summary>
    public enum UxmlAttributeType : byte
    {
        /// <summary>Any text.</summary>
        Text,

        /// <summary><c>true</c> or <c>false</c>.</summary>
        Bool,

        /// <summary>A whole number.</summary>
        Int,

        /// <summary>A number.</summary>
        Float,

        /// <summary>One enum name from the attribute's keyword list (case-insensitive).</summary>
        Keyword,

        /// <summary>A colour (same syntax as USS).</summary>
        Color,

        /// <summary>An asset reference resolved at runtime (<c>resource("path")</c>, a registered name, a path).</summary>
        Asset
    }

    /// <summary>One supported UXML attribute.</summary>
    public sealed class UxmlAttributeSpec
    {
        internal UxmlAttributeSpec(string name, UxmlAttributeType type, string[] keywords)
        {
            Name = name;
            Type = type;
            Keywords = keywords ?? Array.Empty<string>();
        }

        /// <summary>Attribute name.</summary>
        public string Name { get; }

        /// <summary>Value type.</summary>
        public UxmlAttributeType Type { get; }

        /// <summary>Allowed values for <see cref="UxmlAttributeType.Keyword"/>, in canonical spelling.</summary>
        public string[] Keywords { get; }

        /// <summary>Validates a value and returns it in canonical form (keyword spelling, trimmed numbers).</summary>
        public bool TryNormalize(string value, out string normalized, out string error)
        {
            string text = (value ?? string.Empty).Trim();
            normalized = text;
            error = null;
            switch (Type)
            {
                case UxmlAttributeType.Bool:
                    if (UxmlValueParsing.TryParseBool(text, out bool flag))
                    {
                        normalized = flag ? "true" : "false";
                        return true;
                    }

                    error = "'" + value + "' is not true or false";
                    return false;
                case UxmlAttributeType.Int:
                    if (UxmlValueParsing.TryParseInt(text, out int number))
                    {
                        normalized = number.ToString(CultureInfo.InvariantCulture);
                        return true;
                    }

                    error = "'" + value + "' is not a whole number";
                    return false;
                case UxmlAttributeType.Float:
                    if (UxmlValueParsing.TryParseFloat(text, out float real))
                    {
                        normalized = real.ToString("R", CultureInfo.InvariantCulture);
                        return true;
                    }

                    error = "'" + value + "' is not a number";
                    return false;
                case UxmlAttributeType.Keyword:
                    for (int i = 0; i < Keywords.Length; i++)
                    {
                        if (string.Equals(Keywords[i], text, StringComparison.OrdinalIgnoreCase))
                        {
                            normalized = Keywords[i];
                            return true;
                        }
                    }

                    error = "'" + value + "' is not one of: " + string.Join(", ", Keywords);
                    return false;
                case UxmlAttributeType.Color:
                    if (UssValueParser.TryParseColor(text, out _))
                    {
                        return true;
                    }

                    error = "'" + value + "' is not a colour";
                    return false;
                case UxmlAttributeType.Asset:
                    if (text.Length > 0)
                    {
                        return true;
                    }

                    error = "the asset reference is empty";
                    return false;
                default:
                    normalized = value ?? string.Empty;
                    return true;
            }
        }
    }

    /// <summary>
    /// The UXML vocabulary of the interpreter: which element types it builds, which attributes it
    /// applies to each, and which element types it recognises but does not build (so the report can
    /// say which).
    /// </summary>
    public static class UxmlElementCatalog
    {
        /// <summary>The UI Toolkit runtime namespace.</summary>
        public const string EngineNamespace = "UnityEngine.UIElements";

        /// <summary>The UI Toolkit editor namespace (never available in a player).</summary>
        public const string EditorNamespace = "UnityEditor.UIElements";

        private static readonly Dictionary<string, RuntimeUiElementKind> Kinds = new(StringComparer.Ordinal)
        {
            { "VisualElement", RuntimeUiElementKind.VisualElement },
            { "Label", RuntimeUiElementKind.Label },
            { "Button", RuntimeUiElementKind.Button },
            { "Toggle", RuntimeUiElementKind.Toggle },
            { "Slider", RuntimeUiElementKind.Slider },
            { "TextField", RuntimeUiElementKind.TextField },
            { "ProgressBar", RuntimeUiElementKind.ProgressBar },
            { "Image", RuntimeUiElementKind.Image },
            { "ScrollView", RuntimeUiElementKind.ScrollView },
            { "ListView", RuntimeUiElementKind.ListView },
            { "TemplateContainer", RuntimeUiElementKind.TemplateContainer }
        };

        private static readonly HashSet<string> KnownUnbuilt = new(StringComparer.Ordinal)
        {
            "Box", "GroupBox", "TextElement", "Foldout", "DropdownField", "EnumField", "RadioButton",
            "RadioButtonGroup", "IntegerField", "LongField", "FloatField", "DoubleField", "Hash128Field",
            "Vector2Field", "Vector3Field", "Vector4Field", "RectField", "BoundsField", "Vector2IntField",
            "Vector3IntField", "RectIntField", "BoundsIntField", "UnsignedIntegerField", "UnsignedLongField",
            "SliderInt", "MinMaxSlider", "Scroller", "RepeatButton", "TreeView", "MultiColumnListView",
            "MultiColumnTreeView", "TwoPaneSplitView", "Tab", "TabView", "ToggleButtonGroup", "PopupWindow",
            "IMGUIContainer", "BindableElement"
        };

        private static readonly Dictionary<string, UxmlAttributeSpec> Common = Specs(
            Text("name"), Text("class"), Text("style"), Text("tooltip"),
            Keyword("picking-mode", "Position", "Ignore"), Bool("focusable"), Int("tabindex"), Bool("enabled"),
            Text("view-data-key"), Text("usage-hints"), Text("content-container"));

        private static readonly Dictionary<RuntimeUiElementKind, Dictionary<string, UxmlAttributeSpec>> PerKind =
            new()
            {
                { RuntimeUiElementKind.VisualElement, Specs() },
                { RuntimeUiElementKind.TemplateContainer, Specs(Text("template")) },
                { RuntimeUiElementKind.Placeholder, Specs() },
                {
                    RuntimeUiElementKind.Label,
                    Specs(Text("text"), Bool("enable-rich-text"), Bool("parse-escape-sequences"),
                        Bool("display-tooltip-when-elided"))
                },
                {
                    RuntimeUiElementKind.Button,
                    Specs(Text("text"), Bool("enable-rich-text"), Bool("parse-escape-sequences"),
                        Bool("display-tooltip-when-elided"))
                },
                {
                    RuntimeUiElementKind.Toggle,
                    Specs(Text("label"), Bool("value"), Text("text"), Bool("toggle-on-label-click"))
                },
                {
                    RuntimeUiElementKind.Slider,
                    Specs(Text("label"), Float("value"), Float("low-value"), Float("high-value"), Float("page-size"),
                        Keyword("direction", "Horizontal", "Vertical"), Bool("inverted"), Bool("show-input-field"),
                        Bool("fill"))
                },
                {
                    RuntimeUiElementKind.TextField,
                    Specs(Text("label"), Text("value"), Text("text"), Int("max-length"), Bool("multiline"),
                        Bool("password"), Text("mask-character"), Bool("readonly"), Bool("is-read-only"),
                        Bool("is-delayed"), Text("placeholder-text"), Bool("hide-placeholder-on-focus"),
                        Bool("select-all-on-focus"), Bool("select-all-on-mouse-up"))
                },
                {
                    RuntimeUiElementKind.ProgressBar,
                    Specs(Float("low-value"), Float("high-value"), Float("value"), Text("title"))
                },
                {
                    RuntimeUiElementKind.Image,
                    Specs(Asset("image"), Asset("sprite"),
                        Keyword("scale-mode", "StretchToFill", "ScaleAndCrop", "ScaleToFit"),
                        Color("tint-color"))
                },
                {
                    RuntimeUiElementKind.ScrollView,
                    Specs(Keyword("mode", "Vertical", "Horizontal", "VerticalAndHorizontal"),
                        Keyword("horizontal-scroller-visibility", "Auto", "AlwaysVisible", "Hidden"),
                        Keyword("vertical-scroller-visibility", "Auto", "AlwaysVisible", "Hidden"),
                        Float("elasticity"), Float("mouse-wheel-scroll-size"),
                        Keyword("touch-scroll-type", "Unrestricted", "Elastic", "Clamped"),
                        Float("horizontal-page-size"), Float("vertical-page-size"),
                        Float("scroll-deceleration-rate"))
                },
                {
                    RuntimeUiElementKind.ListView,
                    Specs(Float("fixed-item-height"), Keyword("virtualization-method", "FixedHeight", "DynamicHeight"),
                        Bool("show-border"), Keyword("selection-type", "None", "Single", "Multiple"),
                        Keyword("show-alternating-row-backgrounds", "None", "ContentOnly", "All"), Bool("reorderable"),
                        Text("header-title"), Bool("show-foldout-header"), Bool("show-add-remove-footer"),
                        Text("item-template"), Bool("horizontal-scrolling"))
                }
            };

        /// <summary>The element type names the interpreter builds.</summary>
        public static IEnumerable<string> SupportedTypeNames => Kinds.Keys;

        /// <summary>Maps a local UI Toolkit type name to the kind the factory builds.</summary>
        public static bool TryGetKind(string typeName, out RuntimeUiElementKind kind)
        {
            if (typeName == null)
            {
                kind = RuntimeUiElementKind.Placeholder;
                return false;
            }

            return Kinds.TryGetValue(typeName, out kind);
        }

        /// <summary>The UI Toolkit type name of a kind (<c>Placeholder</c> builds a <c>VisualElement</c>).</summary>
        public static string TypeNameOf(RuntimeUiElementKind kind)
        {
            switch (kind)
            {
                case RuntimeUiElementKind.Label: return "Label";
                case RuntimeUiElementKind.Button: return "Button";
                case RuntimeUiElementKind.Toggle: return "Toggle";
                case RuntimeUiElementKind.Slider: return "Slider";
                case RuntimeUiElementKind.TextField: return "TextField";
                case RuntimeUiElementKind.ProgressBar: return "ProgressBar";
                case RuntimeUiElementKind.Image: return "Image";
                case RuntimeUiElementKind.ScrollView: return "ScrollView";
                case RuntimeUiElementKind.ListView: return "ListView";
                case RuntimeUiElementKind.TemplateContainer: return "TemplateContainer";
                default: return "VisualElement";
            }
        }

        /// <summary>True for a real UI Toolkit runtime element the interpreter does not build yet.</summary>
        public static bool IsKnownUnbuilt(string typeName)
        {
            return typeName != null && KnownUnbuilt.Contains(typeName);
        }

        /// <summary>Looks up an attribute the factory applies to <paramref name="kind"/>.</summary>
        public static bool TryGetAttribute(RuntimeUiElementKind kind, string attributeName, out UxmlAttributeSpec spec)
        {
            spec = null;
            if (attributeName == null)
            {
                return false;
            }

            if (Common.TryGetValue(attributeName, out spec))
            {
                return true;
            }

            return PerKind.TryGetValue(kind, out Dictionary<string, UxmlAttributeSpec> specs)
                   && specs.TryGetValue(attributeName, out spec);
        }

        /// <summary>The kind-specific attribute names (common attributes excluded).</summary>
        public static IEnumerable<string> AttributeNamesOf(RuntimeUiElementKind kind)
        {
            return PerKind.TryGetValue(kind, out Dictionary<string, UxmlAttributeSpec> specs)
                ? specs.Keys
                : Array.Empty<string>();
        }

        /// <summary>The attribute names every element accepts.</summary>
        public static IEnumerable<string> CommonAttributeNames => Common.Keys;

        private static Dictionary<string, UxmlAttributeSpec> Specs(params UxmlAttributeSpec[] specs)
        {
            Dictionary<string, UxmlAttributeSpec> map = new(StringComparer.Ordinal);
            for (int i = 0; i < specs.Length; i++)
            {
                map[specs[i].Name] = specs[i];
            }

            return map;
        }

        private static UxmlAttributeSpec Text(string name)
        {
            return new UxmlAttributeSpec(name, UxmlAttributeType.Text, null);
        }

        private static UxmlAttributeSpec Bool(string name)
        {
            return new UxmlAttributeSpec(name, UxmlAttributeType.Bool, null);
        }

        private static UxmlAttributeSpec Int(string name)
        {
            return new UxmlAttributeSpec(name, UxmlAttributeType.Int, null);
        }

        private static UxmlAttributeSpec Float(string name)
        {
            return new UxmlAttributeSpec(name, UxmlAttributeType.Float, null);
        }

        private static UxmlAttributeSpec Color(string name)
        {
            return new UxmlAttributeSpec(name, UxmlAttributeType.Color, null);
        }

        private static UxmlAttributeSpec Asset(string name)
        {
            return new UxmlAttributeSpec(name, UxmlAttributeType.Asset, null);
        }

        private static UxmlAttributeSpec Keyword(string name, params string[] keywords)
        {
            return new UxmlAttributeSpec(name, UxmlAttributeType.Keyword, keywords);
        }
    }

    /// <summary>Invariant-culture parsing of UXML attribute values.</summary>
    public static class UxmlValueParsing
    {
        /// <summary>Parses <c>true</c>/<c>false</c> (case-insensitive).</summary>
        public static bool TryParseBool(string text, out bool value)
        {
            string trimmed = (text ?? string.Empty).Trim();
            if (string.Equals(trimmed, "true", StringComparison.OrdinalIgnoreCase))
            {
                value = true;
                return true;
            }

            if (string.Equals(trimmed, "false", StringComparison.OrdinalIgnoreCase))
            {
                value = false;
                return true;
            }

            value = false;
            return false;
        }

        /// <summary>Parses a whole number.</summary>
        public static bool TryParseInt(string text, out int value)
        {
            return int.TryParse((text ?? string.Empty).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture,
                out value);
        }

        /// <summary>Parses a finite number.</summary>
        public static bool TryParseFloat(string text, out float value)
        {
            if (float.TryParse((text ?? string.Empty).Trim(), NumberStyles.Float, CultureInfo.InvariantCulture,
                    out value) && !float.IsNaN(value) && !float.IsInfinity(value))
            {
                return true;
            }

            value = 0f;
            return false;
        }
    }
}
