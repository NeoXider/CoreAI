namespace CoreAI.Mods.Rbx.Instances.RuntimeUi
{
    /// <summary>
    /// The style longhands the typed element API of the port sets without parsing USS text (the per-frame safe
    /// path the Roblox GUI classes draw through). The names are the USS longhands of the same name; the Unity
    /// adapter maps each to the host's property id one to one.
    /// </summary>
    public enum RbxUiStyleProperty
    {
        Display = 0,
        Position,
        Left,
        Top,
        Width,
        Height,
        Overflow,
        MarginLeft,
        MarginTop,
        MarginRight,
        MarginBottom,
        PaddingLeft,
        PaddingTop,
        PaddingRight,
        PaddingBottom,
        BorderLeftWidth,
        BorderTopWidth,
        BorderRightWidth,
        BorderBottomWidth,
        BorderLeftColor,
        BorderTopColor,
        BorderRightColor,
        BorderBottomColor,
        BorderTopLeftRadius,
        BorderTopRightRadius,
        BorderBottomRightRadius,
        BorderBottomLeftRadius,
        Color,
        FontSize,
        UnityFontStyle,
        UnityTextAlign,
        WhiteSpace,
        TextOverflow,
        UnityTextOutlineWidth,
        UnityTextOutlineColor,
        BackgroundColor,
        UnityBackgroundImageTintColor,
        Opacity,
        Rotate,
        Scale,

        /// <summary>The number of properties; not a property.</summary>
        Count
    }

    /// <summary>What kind of value a <see cref="RbxUiStyleValue"/> carries.</summary>
    public enum RbxUiStyleValueKind
    {
        /// <summary>No value.</summary>
        None = 0,

        /// <summary>A length in pixels.</summary>
        Pixels,

        /// <summary>A length in percent of the reference size.</summary>
        Percent,

        /// <summary>A plain number.</summary>
        Number,

        /// <summary>A colour, components 0..1.</summary>
        Color,

        /// <summary>A USS keyword (<c>absolute</c>, <c>hidden</c>, <c>middle-center</c>, ...).</summary>
        Keyword,

        /// <summary>An angle in degrees.</summary>
        Degrees,

        /// <summary>Two numbers (a scale's x and y).</summary>
        Pair
    }

    /// <summary>A typed style value: no text, no allocation.</summary>
    public readonly struct RbxUiStyleValue
    {
        private readonly RbxUiStyleValueKind _kind;

        private RbxUiStyleValue(RbxUiStyleValueKind kind, float a, float b, float c, float d, string keyword)
        {
            _kind = kind;
            A = a;
            B = b;
            C = c;
            D = d;
            Keyword = keyword;
        }

        /// <summary>The kind of value.</summary>
        public RbxUiStyleValueKind Kind => _kind;

        /// <summary>The number, the first of a pair, or red.</summary>
        public float A { get; }

        /// <summary>The second of a pair, or green.</summary>
        public float B { get; }

        /// <summary>Blue.</summary>
        public float C { get; }

        /// <summary>Alpha.</summary>
        public float D { get; }

        /// <summary>The keyword of a keyword value.</summary>
        public string Keyword { get; }

        public static RbxUiStyleValue Pixels(float value)
        {
            return new RbxUiStyleValue(RbxUiStyleValueKind.Pixels, value, 0f, 0f, 0f, null);
        }

        public static RbxUiStyleValue Percent(float value)
        {
            return new RbxUiStyleValue(RbxUiStyleValueKind.Percent, value, 0f, 0f, 0f, null);
        }

        public static RbxUiStyleValue Number(float value)
        {
            return new RbxUiStyleValue(RbxUiStyleValueKind.Number, value, 0f, 0f, 0f, null);
        }

        public static RbxUiStyleValue Color(float r, float g, float b, float a)
        {
            return new RbxUiStyleValue(RbxUiStyleValueKind.Color, r, g, b, a, null);
        }

        public static RbxUiStyleValue FromKeyword(string keyword)
        {
            return new RbxUiStyleValue(RbxUiStyleValueKind.Keyword, 0f, 0f, 0f, 0f, keyword);
        }

        public static RbxUiStyleValue Degrees(float value)
        {
            return new RbxUiStyleValue(RbxUiStyleValueKind.Degrees, value, 0f, 0f, 0f, null);
        }

        public static RbxUiStyleValue Pair(float x, float y)
        {
            return new RbxUiStyleValue(RbxUiStyleValueKind.Pair, x, y, 0f, 0f, null);
        }

        /// <summary>True when both carry the same value.</summary>
        public bool SameAs(in RbxUiStyleValue other)
        {
            return _kind == other._kind && A.Equals(other.A) && B.Equals(other.B) && C.Equals(other.C)
                   && D.Equals(other.D) && ReferenceEquals(Keyword, other.Keyword);
        }

        /// <inheritdoc />
        public override string ToString()
        {
            System.Globalization.CultureInfo invariant = System.Globalization.CultureInfo.InvariantCulture;
            switch (_kind)
            {
                case RbxUiStyleValueKind.Pixels:
                    return A.ToString("R", invariant) + "px";
                case RbxUiStyleValueKind.Percent:
                    return A.ToString("R", invariant) + "%";
                case RbxUiStyleValueKind.Number:
                    return A.ToString("R", invariant);
                case RbxUiStyleValueKind.Color:
                    return "rgba(" + (A * 255f).ToString("0.##", invariant) + ", " + (B * 255f).ToString("0.##", invariant)
                           + ", " + (C * 255f).ToString("0.##", invariant) + ", " + D.ToString("0.###", invariant) + ")";
                case RbxUiStyleValueKind.Keyword:
                    return Keyword;
                case RbxUiStyleValueKind.Degrees:
                    return A.ToString("R", invariant) + "deg";
                case RbxUiStyleValueKind.Pair:
                    return A.ToString("R", invariant) + " " + B.ToString("R", invariant);
                default:
                    return string.Empty;
            }
        }
    }

    /// <summary>The USS keywords the Roblox GUI classes draw with.</summary>
    public static class RbxUiKeywords
    {
        public const string Absolute = "absolute";
        public const string Flex = "flex";
        public const string None = "none";
        public const string Hidden = "hidden";
        public const string Visible = "visible";
        public const string Normal = "normal";
        public const string NoWrap = "nowrap";
        public const string Clip = "clip";
        public const string Ellipsis = "ellipsis";
        public const string Bold = "bold";
        public const string Italic = "italic";
        public const string BoldAndItalic = "bold-and-italic";
        public const string UpperLeft = "upper-left";
        public const string UpperCenter = "upper-center";
        public const string UpperRight = "upper-right";
        public const string MiddleLeft = "middle-left";
        public const string MiddleCenter = "middle-center";
        public const string MiddleRight = "middle-right";
        public const string LowerLeft = "lower-left";
        public const string LowerCenter = "lower-center";
        public const string LowerRight = "lower-right";
    }
}
