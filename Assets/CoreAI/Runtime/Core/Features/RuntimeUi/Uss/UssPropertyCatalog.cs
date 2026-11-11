using System;
using System.Collections.Generic;

namespace CoreAI.RuntimeUi
{
    /// <summary>The value grammar a longhand property accepts.</summary>
    public enum UssPropertyKind : byte
    {
        /// <summary>One identifier from <see cref="UssPropertySpec.Keywords"/>.</summary>
        Keyword,

        /// <summary>A length (px, %, or a bare number read as px) or <c>auto</c>.</summary>
        LengthOrAuto,

        /// <summary>A length or <c>none</c>.</summary>
        LengthOrNone,

        /// <summary>A length.</summary>
        Length,

        /// <summary>A pixel amount stored as a float (border widths): a number or px.</summary>
        Float,

        /// <summary>A unitless number.</summary>
        Number,

        /// <summary>A whole number (a px suffix is accepted).</summary>
        Integer,

        /// <summary>A colour.</summary>
        Color,

        /// <summary><c>resource("path")</c>, <c>url("path")</c> or <c>none</c>.</summary>
        Asset,

        /// <summary>An angle (deg, rad, grad, turn) or <c>none</c>.</summary>
        Angle,

        /// <summary>One or two lengths, or <c>none</c>.</summary>
        Translate,

        /// <summary>One or two numbers, or <c>none</c>.</summary>
        Scale,

        /// <summary>One or two lengths or position keywords.</summary>
        TransformOrigin,

        /// <summary>A comma-separated list of durations.</summary>
        TimeList,

        /// <summary>A comma-separated list of property names, <c>all</c> or <c>none</c>.</summary>
        PropertyList,

        /// <summary>A comma-separated list of easing names.</summary>
        EasingList,

        /// <summary><c>x y [blur] [color]</c> or <c>none</c>.</summary>
        TextShadow
    }

    /// <summary>How a shorthand splits onto its longhands.</summary>
    public enum UssShorthandKind : byte
    {
        /// <summary>1-4 values in top, right, bottom, left order.</summary>
        Box,

        /// <summary><c>flex: none | auto | grow [shrink] [basis]</c>.</summary>
        Flex,

        /// <summary><c>transition: [property duration timing delay], ...</c>.</summary>
        Transition,

        /// <summary><c>-unity-text-outline: width color</c>, either order.</summary>
        TextOutline
    }

    /// <summary>One supported longhand property.</summary>
    public sealed class UssPropertySpec
    {
        internal UssPropertySpec(UssPropertyId id, string name, UssPropertyKind kind, bool allowNegative,
            string[] keywords)
        {
            Id = id;
            Name = name;
            Kind = kind;
            AllowNegative = allowNegative;
            Keywords = keywords ?? Array.Empty<string>();
        }

        /// <summary>Property id.</summary>
        public UssPropertyId Id { get; }

        /// <summary>USS name, lower case.</summary>
        public string Name { get; }

        /// <summary>Value grammar.</summary>
        public UssPropertyKind Kind { get; }

        /// <summary>True when negative numbers are valid.</summary>
        public bool AllowNegative { get; }

        /// <summary>Allowed identifiers for <see cref="UssPropertyKind.Keyword"/>.</summary>
        public string[] Keywords { get; }
    }

    /// <summary>One supported shorthand property.</summary>
    public sealed class UssShorthandSpec
    {
        internal UssShorthandSpec(string name, UssShorthandKind kind, UssPropertyId[] longhands)
        {
            Name = name;
            Kind = kind;
            Longhands = longhands;
        }

        /// <summary>USS name, lower case.</summary>
        public string Name { get; }

        /// <summary>How the value splits.</summary>
        public UssShorthandKind Kind { get; }

        /// <summary>Longhands in the shorthand's component order.</summary>
        public UssPropertyId[] Longhands { get; }
    }

    /// <summary>
    /// The supported USS subset: every longhand and shorthand the interpreter applies, and the list of
    /// real USS properties it knows it does not apply (so a report can say "not supported" rather than
    /// "not a property").
    /// </summary>
    public static class UssPropertyCatalog
    {
        private static readonly string[] AlignKeywords = { "auto", "flex-start", "flex-end", "center", "stretch" };

        private static readonly string[] JustifyKeywords =
            { "flex-start", "flex-end", "center", "space-between", "space-around", "space-evenly" };

        private static readonly UssPropertySpec[] ById = new UssPropertySpec[(int)UssPropertyId.Count];
        private static readonly Dictionary<string, UssPropertySpec> Longhands = new(StringComparer.Ordinal);
        private static readonly Dictionary<string, UssShorthandSpec> Shorthands = new(StringComparer.Ordinal);

        private static readonly HashSet<string> KnownUnsupported = new(StringComparer.Ordinal)
        {
            "cursor", "-unity-font", "-unity-font-definition", "background-position", "background-position-x",
            "background-position-y", "background-repeat", "background-size", "aspect-ratio", "filter",
            "backdrop-filter", "-unity-overflow-clip-box", "-unity-slice-type", "-unity-text-generator",
            "-unity-text-auto-size", "-unity-editor-text-rendering-mode", "-unity-material",
            "-unity-text-overflow-position", "animation", "animation-name", "animation-duration",
            "animation-play-state", "-unity-animation-clip", "all", "border", "border-left", "border-top",
            "border-right", "border-bottom", "font", "font-family", "font-weight", "font-style", "text-align",
            "line-height", "z-index", "box-shadow", "transform", "gap", "row-gap", "column-gap"
        };

        static UssPropertyCatalog()
        {
            Add(UssPropertyId.Display, "display", UssPropertyKind.Keyword, false, "flex", "none");
            Add(UssPropertyId.Position, "position", UssPropertyKind.Keyword, false, "relative", "absolute");
            Add(UssPropertyId.FlexDirection, "flex-direction", UssPropertyKind.Keyword, false,
                "row", "row-reverse", "column", "column-reverse");
            Add(UssPropertyId.FlexWrap, "flex-wrap", UssPropertyKind.Keyword, false, "nowrap", "wrap", "wrap-reverse");
            Add(UssPropertyId.FlexGrow, "flex-grow", UssPropertyKind.Number, false);
            Add(UssPropertyId.FlexShrink, "flex-shrink", UssPropertyKind.Number, false);
            Add(UssPropertyId.FlexBasis, "flex-basis", UssPropertyKind.LengthOrAuto, false);
            Add(UssPropertyId.AlignItems, "align-items", UssPropertyKind.Keyword, false, AlignKeywords);
            Add(UssPropertyId.AlignSelf, "align-self", UssPropertyKind.Keyword, false, AlignKeywords);
            Add(UssPropertyId.AlignContent, "align-content", UssPropertyKind.Keyword, false, AlignKeywords);
            Add(UssPropertyId.JustifyContent, "justify-content", UssPropertyKind.Keyword, false, JustifyKeywords);
            Add(UssPropertyId.Left, "left", UssPropertyKind.LengthOrAuto, true);
            Add(UssPropertyId.Top, "top", UssPropertyKind.LengthOrAuto, true);
            Add(UssPropertyId.Right, "right", UssPropertyKind.LengthOrAuto, true);
            Add(UssPropertyId.Bottom, "bottom", UssPropertyKind.LengthOrAuto, true);
            Add(UssPropertyId.Width, "width", UssPropertyKind.LengthOrAuto, false);
            Add(UssPropertyId.Height, "height", UssPropertyKind.LengthOrAuto, false);
            Add(UssPropertyId.MinWidth, "min-width", UssPropertyKind.LengthOrAuto, false);
            Add(UssPropertyId.MinHeight, "min-height", UssPropertyKind.LengthOrAuto, false);
            Add(UssPropertyId.MaxWidth, "max-width", UssPropertyKind.LengthOrNone, false);
            Add(UssPropertyId.MaxHeight, "max-height", UssPropertyKind.LengthOrNone, false);
            Add(UssPropertyId.Overflow, "overflow", UssPropertyKind.Keyword, false, "visible", "hidden");
            Add(UssPropertyId.MarginLeft, "margin-left", UssPropertyKind.LengthOrAuto, true);
            Add(UssPropertyId.MarginTop, "margin-top", UssPropertyKind.LengthOrAuto, true);
            Add(UssPropertyId.MarginRight, "margin-right", UssPropertyKind.LengthOrAuto, true);
            Add(UssPropertyId.MarginBottom, "margin-bottom", UssPropertyKind.LengthOrAuto, true);
            Add(UssPropertyId.PaddingLeft, "padding-left", UssPropertyKind.Length, false);
            Add(UssPropertyId.PaddingTop, "padding-top", UssPropertyKind.Length, false);
            Add(UssPropertyId.PaddingRight, "padding-right", UssPropertyKind.Length, false);
            Add(UssPropertyId.PaddingBottom, "padding-bottom", UssPropertyKind.Length, false);
            Add(UssPropertyId.BorderLeftWidth, "border-left-width", UssPropertyKind.Float, false);
            Add(UssPropertyId.BorderTopWidth, "border-top-width", UssPropertyKind.Float, false);
            Add(UssPropertyId.BorderRightWidth, "border-right-width", UssPropertyKind.Float, false);
            Add(UssPropertyId.BorderBottomWidth, "border-bottom-width", UssPropertyKind.Float, false);
            Add(UssPropertyId.BorderLeftColor, "border-left-color", UssPropertyKind.Color, false);
            Add(UssPropertyId.BorderTopColor, "border-top-color", UssPropertyKind.Color, false);
            Add(UssPropertyId.BorderRightColor, "border-right-color", UssPropertyKind.Color, false);
            Add(UssPropertyId.BorderBottomColor, "border-bottom-color", UssPropertyKind.Color, false);
            Add(UssPropertyId.BorderTopLeftRadius, "border-top-left-radius", UssPropertyKind.Length, false);
            Add(UssPropertyId.BorderTopRightRadius, "border-top-right-radius", UssPropertyKind.Length, false);
            Add(UssPropertyId.BorderBottomRightRadius, "border-bottom-right-radius", UssPropertyKind.Length, false);
            Add(UssPropertyId.BorderBottomLeftRadius, "border-bottom-left-radius", UssPropertyKind.Length, false);
            Add(UssPropertyId.Color, "color", UssPropertyKind.Color, false);
            Add(UssPropertyId.FontSize, "font-size", UssPropertyKind.Length, false);
            Add(UssPropertyId.UnityFontStyle, "-unity-font-style", UssPropertyKind.Keyword, false,
                "normal", "bold", "italic", "bold-and-italic");
            Add(UssPropertyId.UnityTextAlign, "-unity-text-align", UssPropertyKind.Keyword, false,
                "upper-left", "upper-center", "upper-right", "middle-left", "middle-center", "middle-right",
                "lower-left", "lower-center", "lower-right");
            Add(UssPropertyId.WhiteSpace, "white-space", UssPropertyKind.Keyword, false,
                "normal", "nowrap", "pre", "pre-wrap");
            Add(UssPropertyId.TextOverflow, "text-overflow", UssPropertyKind.Keyword, false, "clip", "ellipsis");
            Add(UssPropertyId.LetterSpacing, "letter-spacing", UssPropertyKind.Length, true);
            Add(UssPropertyId.WordSpacing, "word-spacing", UssPropertyKind.Length, true);
            Add(UssPropertyId.UnityParagraphSpacing, "-unity-paragraph-spacing", UssPropertyKind.Length, true);
            Add(UssPropertyId.UnityTextOutlineWidth, "-unity-text-outline-width", UssPropertyKind.Float, false);
            Add(UssPropertyId.UnityTextOutlineColor, "-unity-text-outline-color", UssPropertyKind.Color, false);
            Add(UssPropertyId.TextShadow, "text-shadow", UssPropertyKind.TextShadow, true);
            Add(UssPropertyId.BackgroundColor, "background-color", UssPropertyKind.Color, false);
            Add(UssPropertyId.BackgroundImage, "background-image", UssPropertyKind.Asset, false);
            Add(UssPropertyId.UnityBackgroundImageTintColor, "-unity-background-image-tint-color",
                UssPropertyKind.Color, false);
            Add(UssPropertyId.UnityBackgroundScaleMode, "-unity-background-scale-mode", UssPropertyKind.Keyword,
                false, "stretch-to-fill", "scale-and-crop", "scale-to-fit");
            Add(UssPropertyId.UnitySliceLeft, "-unity-slice-left", UssPropertyKind.Integer, false);
            Add(UssPropertyId.UnitySliceTop, "-unity-slice-top", UssPropertyKind.Integer, false);
            Add(UssPropertyId.UnitySliceRight, "-unity-slice-right", UssPropertyKind.Integer, false);
            Add(UssPropertyId.UnitySliceBottom, "-unity-slice-bottom", UssPropertyKind.Integer, false);
            Add(UssPropertyId.UnitySliceScale, "-unity-slice-scale", UssPropertyKind.Float, false);
            Add(UssPropertyId.Opacity, "opacity", UssPropertyKind.Number, false);
            Add(UssPropertyId.Visibility, "visibility", UssPropertyKind.Keyword, false, "visible", "hidden");
            Add(UssPropertyId.Translate, "translate", UssPropertyKind.Translate, true);
            Add(UssPropertyId.Scale, "scale", UssPropertyKind.Scale, true);
            Add(UssPropertyId.Rotate, "rotate", UssPropertyKind.Angle, true);
            Add(UssPropertyId.TransformOrigin, "transform-origin", UssPropertyKind.TransformOrigin, true);
            Add(UssPropertyId.TransitionProperty, "transition-property", UssPropertyKind.PropertyList, false);
            Add(UssPropertyId.TransitionDuration, "transition-duration", UssPropertyKind.TimeList, false);
            Add(UssPropertyId.TransitionTimingFunction, "transition-timing-function", UssPropertyKind.EasingList,
                false);
            Add(UssPropertyId.TransitionDelay, "transition-delay", UssPropertyKind.TimeList, true);

            AddShorthand("margin", UssShorthandKind.Box, UssPropertyId.MarginTop, UssPropertyId.MarginRight,
                UssPropertyId.MarginBottom, UssPropertyId.MarginLeft);
            AddShorthand("padding", UssShorthandKind.Box, UssPropertyId.PaddingTop, UssPropertyId.PaddingRight,
                UssPropertyId.PaddingBottom, UssPropertyId.PaddingLeft);
            AddShorthand("border-width", UssShorthandKind.Box, UssPropertyId.BorderTopWidth,
                UssPropertyId.BorderRightWidth, UssPropertyId.BorderBottomWidth, UssPropertyId.BorderLeftWidth);
            AddShorthand("border-color", UssShorthandKind.Box, UssPropertyId.BorderTopColor,
                UssPropertyId.BorderRightColor, UssPropertyId.BorderBottomColor, UssPropertyId.BorderLeftColor);
            AddShorthand("border-radius", UssShorthandKind.Box, UssPropertyId.BorderTopLeftRadius,
                UssPropertyId.BorderTopRightRadius, UssPropertyId.BorderBottomRightRadius,
                UssPropertyId.BorderBottomLeftRadius);
            AddShorthand("flex", UssShorthandKind.Flex, UssPropertyId.FlexGrow, UssPropertyId.FlexShrink,
                UssPropertyId.FlexBasis);
            AddShorthand("transition", UssShorthandKind.Transition, UssPropertyId.TransitionProperty,
                UssPropertyId.TransitionDuration, UssPropertyId.TransitionTimingFunction,
                UssPropertyId.TransitionDelay);
            AddShorthand("-unity-text-outline", UssShorthandKind.TextOutline, UssPropertyId.UnityTextOutlineWidth,
                UssPropertyId.UnityTextOutlineColor);
        }

        /// <summary>Every supported longhand, indexed by <see cref="UssPropertyId"/>.</summary>
        public static IReadOnlyList<UssPropertySpec> AllLonghands => ById;

        /// <summary>Every supported shorthand name.</summary>
        public static IEnumerable<string> ShorthandNames => Shorthands.Keys;

        /// <summary>The spec of a longhand id.</summary>
        public static UssPropertySpec Get(UssPropertyId id)
        {
            int index = (int)id;
            return index >= 0 && index < ById.Length ? ById[index] : null;
        }

        /// <summary>Looks up a longhand by its lower-case USS name.</summary>
        public static bool TryGetLonghand(string name, out UssPropertySpec spec)
        {
            if (name == null)
            {
                spec = null;
                return false;
            }

            return Longhands.TryGetValue(name, out spec);
        }

        /// <summary>Looks up a shorthand by its lower-case USS name.</summary>
        public static bool TryGetShorthand(string name, out UssShorthandSpec spec)
        {
            if (name == null)
            {
                spec = null;
                return false;
            }

            return Shorthands.TryGetValue(name, out spec);
        }

        /// <summary>True for a name the interpreter applies (longhand or shorthand).</summary>
        public static bool IsSupported(string name)
        {
            return name != null && (Longhands.ContainsKey(name) || Shorthands.ContainsKey(name));
        }

        /// <summary>True for a real USS (or common CSS) property the interpreter knowingly does not apply.</summary>
        public static bool IsKnownUnsupported(string name)
        {
            return name != null && KnownUnsupported.Contains(name);
        }

        /// <summary>True for a custom property name (<c>--token</c>).</summary>
        public static bool IsCustomProperty(string name)
        {
            return name != null && name.Length > 2 && name[0] == '-' && name[1] == '-';
        }

        private static void Add(UssPropertyId id, string name, UssPropertyKind kind, bool allowNegative,
            params string[] keywords)
        {
            UssPropertySpec spec = new(id, name, kind, allowNegative, keywords);
            ById[(int)id] = spec;
            Longhands[name] = spec;
        }

        private static void AddShorthand(string name, UssShorthandKind kind, params UssPropertyId[] longhands)
        {
            Shorthands[name] = new UssShorthandSpec(name, kind, longhands);
        }
    }
}
