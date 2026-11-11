using System;
using System.Globalization;
using System.Text;

namespace CoreAI.RuntimeUi
{
    /// <summary>What a <see cref="UssValue"/> holds.</summary>
    public enum UssValueType : byte
    {
        /// <summary>No value (an unset slot).</summary>
        None = 0,

        /// <summary>An identifier such as <c>auto</c>, <c>none</c>, <c>initial</c> or <c>flex-start</c>.</summary>
        Keyword,

        /// <summary>A length in pixels or percent (<see cref="UssValue.A"/>, <see cref="UssValue.UnitA"/>).</summary>
        Length,

        /// <summary>A unitless number (<see cref="UssValue.A"/>).</summary>
        Number,

        /// <summary>A colour, components 0..1 in <see cref="UssValue.A"/>..<see cref="UssValue.D"/> (r, g, b, a).</summary>
        Color,

        /// <summary>An angle in degrees (<see cref="UssValue.A"/>).</summary>
        Angle,

        /// <summary>A duration in seconds (<see cref="UssValue.A"/>).</summary>
        Time,

        /// <summary>An asset reference: path in <see cref="UssValue.Keyword"/>, <see cref="UssValue.A"/> is 1 for <c>resource()</c>.</summary>
        Asset,

        /// <summary>Two components (translate, scale, transform-origin) in A/B with units in UnitA/UnitB.</summary>
        Pair,

        /// <summary>A comma-separated list; the items are in <see cref="UssValue.Payload"/> as a <see cref="UssValue"/> array.</summary>
        List,

        /// <summary>A text shadow: offsets in A/B, blur in C, colour in <see cref="UssValue.Payload"/>.</summary>
        TextShadow
    }

    /// <summary>Unit of a numeric component.</summary>
    public enum UssUnit : byte
    {
        /// <summary>No unit (numbers, scale factors).</summary>
        None = 0,

        /// <summary>Pixels.</summary>
        Pixel,

        /// <summary>Percent of the reference size.</summary>
        Percent
    }

    /// <summary>Colour carried by a text-shadow value.</summary>
    public sealed class UssShadowColor
    {
        /// <summary>Creates the colour.</summary>
        public UssShadowColor(float r, float g, float b, float a)
        {
            R = r;
            G = g;
            B = b;
            A = a;
        }

        /// <summary>Red, 0..1.</summary>
        public float R { get; }

        /// <summary>Green, 0..1.</summary>
        public float G { get; }

        /// <summary>Blue, 0..1.</summary>
        public float B { get; }

        /// <summary>Alpha, 0..1.</summary>
        public float A { get; }
    }

    /// <summary>
    /// A parsed, typed USS value, engine-free. Values from a style sheet are parsed once and reused; the
    /// typed factory methods let C# callers (and a Roblox GUI layer) set single style properties without
    /// building or parsing text.
    /// </summary>
    public readonly struct UssValue : IEquatable<UssValue>
    {
        private UssValue(
            UssValueType type,
            float a,
            float b,
            float c,
            float d,
            UssUnit unitA,
            UssUnit unitB,
            string keyword,
            object payload)
        {
            Type = type;
            A = a;
            B = b;
            C = c;
            D = d;
            UnitA = unitA;
            UnitB = unitB;
            Keyword = keyword;
            Payload = payload;
        }

        /// <summary>The CSS-wide <c>initial</c> keyword: reset the property to its default.</summary>
        public static UssValue Initial => FromKeyword(UssKeywords.Initial);

        /// <summary>What this value holds.</summary>
        public UssValueType Type { get; }

        /// <summary>First numeric component.</summary>
        public float A { get; }

        /// <summary>Second numeric component.</summary>
        public float B { get; }

        /// <summary>Third numeric component.</summary>
        public float C { get; }

        /// <summary>Fourth numeric component.</summary>
        public float D { get; }

        /// <summary>Unit of <see cref="A"/>.</summary>
        public UssUnit UnitA { get; }

        /// <summary>Unit of <see cref="B"/>.</summary>
        public UssUnit UnitB { get; }

        /// <summary>Keyword text, or the asset path of an <see cref="UssValueType.Asset"/>.</summary>
        public string Keyword { get; }

        /// <summary>List items or text-shadow colour.</summary>
        public object Payload { get; }

        /// <summary>True when the value holds anything.</summary>
        public bool HasValue => Type != UssValueType.None;

        /// <summary>A keyword value.</summary>
        public static UssValue FromKeyword(string keyword)
        {
            return new UssValue(UssValueType.Keyword, 0f, 0f, 0f, 0f, UssUnit.None, UssUnit.None,
                keyword ?? string.Empty, null);
        }

        /// <summary>A length; <paramref name="unit"/> is <see cref="UssUnit.Pixel"/> or <see cref="UssUnit.Percent"/>.</summary>
        public static UssValue FromLength(float value, UssUnit unit = UssUnit.Pixel)
        {
            return new UssValue(UssValueType.Length, value, 0f, 0f, 0f,
                unit == UssUnit.Percent ? UssUnit.Percent : UssUnit.Pixel, UssUnit.None, null, null);
        }

        /// <summary>A unitless number.</summary>
        public static UssValue FromNumber(float value)
        {
            return new UssValue(UssValueType.Number, value, 0f, 0f, 0f, UssUnit.None, UssUnit.None, null, null);
        }

        /// <summary>A colour with components 0..1.</summary>
        public static UssValue FromColor(float r, float g, float b, float a = 1f)
        {
            return new UssValue(UssValueType.Color, Clamp01(r), Clamp01(g), Clamp01(b), Clamp01(a),
                UssUnit.None, UssUnit.None, null, null);
        }

        /// <summary>An angle in degrees.</summary>
        public static UssValue FromAngle(float degrees)
        {
            return new UssValue(UssValueType.Angle, degrees, 0f, 0f, 0f, UssUnit.None, UssUnit.None, null, null);
        }

        /// <summary>A duration in seconds.</summary>
        public static UssValue FromTime(float seconds)
        {
            return new UssValue(UssValueType.Time, seconds, 0f, 0f, 0f, UssUnit.None, UssUnit.None, null, null);
        }

        /// <summary>An asset reference; <paramref name="isResource"/> selects <c>resource()</c> over <c>url()</c>.</summary>
        public static UssValue FromAsset(string path, bool isResource)
        {
            return new UssValue(UssValueType.Asset, isResource ? 1f : 0f, 0f, 0f, 0f, UssUnit.None, UssUnit.None,
                path ?? string.Empty, null);
        }

        /// <summary>A two-component value (translate, scale, transform-origin).</summary>
        public static UssValue FromPair(float a, UssUnit unitA, float b, UssUnit unitB)
        {
            return new UssValue(UssValueType.Pair, a, b, 0f, 0f, unitA, unitB, null, null);
        }

        /// <summary>A comma-separated list value; the array is kept, not copied.</summary>
        public static UssValue FromList(UssValue[] items)
        {
            return new UssValue(UssValueType.List, items?.Length ?? 0, 0f, 0f, 0f, UssUnit.None, UssUnit.None, null,
                items ?? Array.Empty<UssValue>());
        }

        /// <summary>A text shadow; offsets and blur in pixels.</summary>
        public static UssValue FromTextShadow(float offsetX, float offsetY, float blur, UssShadowColor color)
        {
            return new UssValue(UssValueType.TextShadow, offsetX, offsetY, blur, 0f, UssUnit.Pixel, UssUnit.Pixel,
                null, color);
        }

        /// <summary>True when this is the given keyword (ordinal, case-insensitive).</summary>
        public bool IsKeyword(string keyword)
        {
            return Type == UssValueType.Keyword && string.Equals(Keyword, keyword, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>The list items, or an empty array when this is not a list.</summary>
        public UssValue[] ListItems => Payload as UssValue[] ?? Array.Empty<UssValue>();

        /// <inheritdoc />
        public bool Equals(UssValue other)
        {
            if (Type != other.Type || UnitA != other.UnitA || UnitB != other.UnitB
                || !A.Equals(other.A) || !B.Equals(other.B) || !C.Equals(other.C) || !D.Equals(other.D)
                || !string.Equals(Keyword, other.Keyword, StringComparison.Ordinal))
            {
                return false;
            }

            if (Type == UssValueType.List)
            {
                UssValue[] mine = ListItems;
                UssValue[] theirs = other.ListItems;
                if (mine.Length != theirs.Length)
                {
                    return false;
                }

                for (int i = 0; i < mine.Length; i++)
                {
                    if (!mine[i].Equals(theirs[i]))
                    {
                        return false;
                    }
                }

                return true;
            }

            if (Type == UssValueType.TextShadow)
            {
                UssShadowColor mine = Payload as UssShadowColor;
                UssShadowColor theirs = other.Payload as UssShadowColor;
                if (mine == null || theirs == null)
                {
                    return mine == theirs;
                }

                return mine.R.Equals(theirs.R) && mine.G.Equals(theirs.G) && mine.B.Equals(theirs.B)
                       && mine.A.Equals(theirs.A);
            }

            return true;
        }

        /// <inheritdoc />
        public override bool Equals(object obj)
        {
            return obj is UssValue other && Equals(other);
        }

        /// <inheritdoc />
        public override int GetHashCode()
        {
            unchecked
            {
                int hash = (int)Type;
                hash = (hash * 397) ^ A.GetHashCode();
                hash = (hash * 397) ^ B.GetHashCode();
                hash = (hash * 397) ^ (Keyword != null ? Keyword.GetHashCode() : 0);
                return hash;
            }
        }

        /// <summary>Formats the value back as USS text (for reports and API reads).</summary>
        public string ToUssText()
        {
            switch (Type)
            {
                case UssValueType.Keyword:
                    return Keyword;
                case UssValueType.Length:
                    return Format(A) + (UnitA == UssUnit.Percent ? "%" : "px");
                case UssValueType.Number:
                    return Format(A);
                case UssValueType.Color:
                    return "rgba(" + Format(Round255(A)) + ", " + Format(Round255(B)) + ", " + Format(Round255(C)) +
                           ", " + Format(D) + ")";
                case UssValueType.Angle:
                    return Format(A) + "deg";
                case UssValueType.Time:
                    return Format(A) + "s";
                case UssValueType.Asset:
                    return (A > 0.5f ? "resource(\"" : "url(\"") + Keyword + "\")";
                case UssValueType.Pair:
                    return FormatComponent(A, UnitA) + " " + FormatComponent(B, UnitB);
                case UssValueType.List:
                {
                    StringBuilder builder = new();
                    UssValue[] items = ListItems;
                    for (int i = 0; i < items.Length; i++)
                    {
                        if (i > 0)
                        {
                            builder.Append(", ");
                        }

                        builder.Append(items[i].ToUssText());
                    }

                    return builder.ToString();
                }
                case UssValueType.TextShadow:
                {
                    string text = Format(A) + "px " + Format(B) + "px " + Format(C) + "px";
                    if (Payload is UssShadowColor color)
                    {
                        text += " " + FromColor(color.R, color.G, color.B, color.A).ToUssText();
                    }

                    return text;
                }
                default:
                    return string.Empty;
            }
        }

        /// <inheritdoc />
        public override string ToString()
        {
            return ToUssText();
        }

        private static string FormatComponent(float value, UssUnit unit)
        {
            return unit switch
            {
                UssUnit.Pixel => Format(value) + "px",
                UssUnit.Percent => Format(value) + "%",
                _ => Format(value)
            };
        }

        private static string Format(float value)
        {
            return value.ToString("0.####", CultureInfo.InvariantCulture);
        }

        private static float Round255(float component)
        {
            return (float)Math.Round(component * 255f);
        }

        private static float Clamp01(float value)
        {
            if (value < 0f)
            {
                return 0f;
            }

            return value > 1f ? 1f : value;
        }
    }
}
