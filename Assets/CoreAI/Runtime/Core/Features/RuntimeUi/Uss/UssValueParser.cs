using System;
using System.Collections.Generic;
using System.Globalization;

namespace CoreAI.RuntimeUi
{
    /// <summary>
    /// Parses USS value text into typed <see cref="UssValue"/>s for the supported properties. Text that
    /// still contains <c>var()</c> must be substituted first (<see cref="UssVarSubstitution"/>).
    /// </summary>
    public static class UssValueParser
    {
        private static readonly HashSet<string> Easings = new(StringComparer.Ordinal)
        {
            "ease", "ease-in", "ease-out", "ease-in-out", "linear", "ease-in-sine", "ease-out-sine",
            "ease-in-out-sine", "ease-in-cubic", "ease-out-cubic", "ease-in-out-cubic", "ease-in-circ",
            "ease-out-circ", "ease-in-out-circ", "ease-in-elastic", "ease-out-elastic", "ease-in-out-elastic",
            "ease-in-back", "ease-out-back", "ease-in-out-back", "ease-in-bounce", "ease-out-bounce",
            "ease-in-out-bounce"
        };

        /// <summary>True for an easing name USS accepts in <c>transition-timing-function</c>.</summary>
        public static bool IsEasing(string name)
        {
            return name != null && Easings.Contains(name);
        }

        /// <summary>Parses the value of one longhand.</summary>
        /// <param name="spec">The property.</param>
        /// <param name="text">Value text without <c>var()</c>.</param>
        /// <param name="value">The typed value.</param>
        /// <param name="error">Why the text was refused.</param>
        public static bool TryParseLonghand(UssPropertySpec spec, string text, out UssValue value, out string error)
        {
            value = default;
            error = null;
            if (spec == null)
            {
                error = "unknown property";
                return false;
            }

            string trimmed = (text ?? string.Empty).Trim();
            if (trimmed.Length == 0)
            {
                error = "the value is empty";
                return false;
            }

            if (string.Equals(trimmed, UssKeywords.Initial, StringComparison.OrdinalIgnoreCase))
            {
                value = UssValue.Initial;
                return true;
            }

            switch (spec.Kind)
            {
                case UssPropertyKind.Keyword:
                    return TryParseKeyword(spec, trimmed, out value, out error);
                case UssPropertyKind.LengthOrAuto:
                    if (string.Equals(trimmed, UssKeywords.Auto, StringComparison.OrdinalIgnoreCase))
                    {
                        value = UssValue.FromKeyword(UssKeywords.Auto);
                        return true;
                    }

                    return TryParseLength(trimmed, spec.AllowNegative, out value, out error);
                case UssPropertyKind.LengthOrNone:
                    if (string.Equals(trimmed, UssKeywords.None, StringComparison.OrdinalIgnoreCase))
                    {
                        value = UssValue.FromKeyword(UssKeywords.None);
                        return true;
                    }

                    return TryParseLength(trimmed, spec.AllowNegative, out value, out error);
                case UssPropertyKind.Length:
                    return TryParseLength(trimmed, spec.AllowNegative, out value, out error);
                case UssPropertyKind.Float:
                    return TryParsePixelFloat(trimmed, spec.AllowNegative, out value, out error);
                case UssPropertyKind.Number:
                    return TryParseNumber(trimmed, spec.AllowNegative, out value, out error);
                case UssPropertyKind.Integer:
                    if (!TryParsePixelFloat(trimmed, spec.AllowNegative, out UssValue raw, out error))
                    {
                        return false;
                    }

                    value = UssValue.FromNumber((float)Math.Round(raw.A));
                    return true;
                case UssPropertyKind.Color:
                    if (TryParseColor(trimmed, out value))
                    {
                        return true;
                    }

                    error = "'" + trimmed + "' is not a colour (use #rrggbb, rgb(), rgba() or a colour name)";
                    return false;
                case UssPropertyKind.Asset:
                    return TryParseAsset(trimmed, out value, out error);
                case UssPropertyKind.Angle:
                    if (string.Equals(trimmed, UssKeywords.None, StringComparison.OrdinalIgnoreCase))
                    {
                        value = UssValue.FromKeyword(UssKeywords.None);
                        return true;
                    }

                    return TryParseAngle(trimmed, out value, out error);
                case UssPropertyKind.Translate:
                    return TryParseTranslate(trimmed, out value, out error);
                case UssPropertyKind.Scale:
                    return TryParseScale(trimmed, out value, out error);
                case UssPropertyKind.TransformOrigin:
                    return TryParseTransformOrigin(trimmed, out value, out error);
                case UssPropertyKind.TimeList:
                    return TryParseList(trimmed, ListKind.Time, spec.AllowNegative, out value, out error);
                case UssPropertyKind.PropertyList:
                    return TryParseList(trimmed, ListKind.Property, false, out value, out error);
                case UssPropertyKind.EasingList:
                    return TryParseList(trimmed, ListKind.Easing, false, out value, out error);
                case UssPropertyKind.TextShadow:
                    return TryParseTextShadow(trimmed, out value, out error);
                default:
                    error = "unsupported value kind";
                    return false;
            }
        }

        /// <summary>
        /// Splits a shorthand value onto its longhands. <paramref name="output"/> must have one slot per
        /// <see cref="UssShorthandSpec.Longhands"/> entry and receives values in the same order.
        /// </summary>
        public static bool TryExpandShorthand(UssShorthandSpec spec, string text, UssValue[] output, out string error)
        {
            error = null;
            if (spec == null || output == null || output.Length < spec.Longhands.Length)
            {
                error = "unknown shorthand";
                return false;
            }

            string trimmed = (text ?? string.Empty).Trim();
            if (trimmed.Length == 0)
            {
                error = "the value is empty";
                return false;
            }

            if (string.Equals(trimmed, UssKeywords.Initial, StringComparison.OrdinalIgnoreCase))
            {
                for (int i = 0; i < spec.Longhands.Length; i++)
                {
                    output[i] = UssValue.Initial;
                }

                return true;
            }

            switch (spec.Kind)
            {
                case UssShorthandKind.Box:
                    return ExpandBox(spec, trimmed, output, out error);
                case UssShorthandKind.Flex:
                    return ExpandFlex(trimmed, output, out error);
                case UssShorthandKind.Transition:
                    return ExpandTransition(trimmed, output, out error);
                case UssShorthandKind.TextOutline:
                    return ExpandTextOutline(trimmed, output, out error);
                default:
                    error = "unsupported shorthand";
                    return false;
            }
        }

        /// <summary>Parses a colour: hex (3, 4, 6 or 8 digits), <c>rgb()</c>, <c>rgba()</c> or a colour name.</summary>
        public static bool TryParseColor(string text, out UssValue value)
        {
            value = default;
            if (string.IsNullOrEmpty(text))
            {
                return false;
            }

            string trimmed = text.Trim();
            if (trimmed.Length > 1 && trimmed[0] == '#')
            {
                return TryParseHexColor(trimmed, out value);
            }

            int open = trimmed.IndexOf('(');
            if (open > 0 && trimmed[trimmed.Length - 1] == ')')
            {
                string function = trimmed.Substring(0, open).Trim().ToLowerInvariant();
                if (function != "rgb" && function != "rgba")
                {
                    return false;
                }

                string inner = trimmed.Substring(open + 1, trimmed.Length - open - 2);
                return TryParseRgbFunction(inner, out value);
            }

            if (UssNamedColors.TryGet(trimmed, out float r, out float g, out float b, out float a))
            {
                value = UssValue.FromColor(r, g, b, a);
                return true;
            }

            return false;
        }

        /// <summary>Parses a length: a number with <c>px</c>, <c>%</c>, or no unit (pixels).</summary>
        public static bool TryParseLength(string token, bool allowNegative, out UssValue value, out string error)
        {
            value = default;
            if (!TrySplitNumber(token, out float number, out string unit))
            {
                error = "'" + token + "' is not a length (use px or %)";
                return false;
            }

            if (!allowNegative && number < 0f)
            {
                error = "a negative length is not allowed here";
                return false;
            }

            if (unit.Length == 0 || unit == "px")
            {
                value = UssValue.FromLength(number, UssUnit.Pixel);
                error = null;
                return true;
            }

            if (unit == "%")
            {
                value = UssValue.FromLength(number, UssUnit.Percent);
                error = null;
                return true;
            }

            error = "the unit '" + unit + "' is not supported (USS lengths are px or %)";
            return false;
        }

        /// <summary>Splits text into whitespace-separated components, keeping parentheses and quotes intact.</summary>
        public static void SplitComponents(string text, List<string> output)
        {
            output.Clear();
            if (string.IsNullOrEmpty(text))
            {
                return;
            }

            int depth = 0;
            char quote = '\0';
            int start = -1;
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (quote != '\0')
                {
                    if (c == quote)
                    {
                        quote = '\0';
                    }

                    continue;
                }

                if (c == '"' || c == '\'')
                {
                    quote = c;
                    if (start < 0)
                    {
                        start = i;
                    }

                    continue;
                }

                if (c == '(')
                {
                    depth++;
                }
                else if (c == ')')
                {
                    depth = depth > 0 ? depth - 1 : 0;
                }

                bool separator = depth == 0 && char.IsWhiteSpace(c);
                if (separator)
                {
                    if (start >= 0)
                    {
                        output.Add(text.Substring(start, i - start));
                        start = -1;
                    }
                }
                else if (start < 0)
                {
                    start = i;
                }
            }

            if (start >= 0)
            {
                output.Add(text.Substring(start));
            }
        }

        /// <summary>Splits text on top-level commas, trimming each item.</summary>
        public static void SplitList(string text, List<string> output)
        {
            output.Clear();
            if (text == null)
            {
                return;
            }

            int depth = 0;
            char quote = '\0';
            int start = 0;
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (quote != '\0')
                {
                    if (c == quote)
                    {
                        quote = '\0';
                    }

                    continue;
                }

                if (c == '"' || c == '\'')
                {
                    quote = c;
                }
                else if (c == '(')
                {
                    depth++;
                }
                else if (c == ')')
                {
                    depth = depth > 0 ? depth - 1 : 0;
                }
                else if (c == ',' && depth == 0)
                {
                    output.Add(text.Substring(start, i - start).Trim());
                    start = i + 1;
                }
            }

            output.Add(text.Substring(start).Trim());
        }

        private enum ListKind
        {
            Time,
            Property,
            Easing
        }

        private static bool TryParseKeyword(UssPropertySpec spec, string text, out UssValue value, out string error)
        {
            string lower = text.ToLowerInvariant();
            string[] keywords = spec.Keywords;
            for (int i = 0; i < keywords.Length; i++)
            {
                if (keywords[i] == lower)
                {
                    value = UssValue.FromKeyword(keywords[i]);
                    error = null;
                    return true;
                }
            }

            value = default;
            error = "'" + text + "' is not one of: " + string.Join(", ", keywords);
            return false;
        }

        private static bool TryParsePixelFloat(string text, bool allowNegative, out UssValue value, out string error)
        {
            value = default;
            if (!TrySplitNumber(text, out float number, out string unit) || (unit.Length != 0 && unit != "px"))
            {
                error = "'" + text + "' is not a pixel amount";
                return false;
            }

            if (!allowNegative && number < 0f)
            {
                error = "a negative value is not allowed here";
                return false;
            }

            value = UssValue.FromNumber(number);
            error = null;
            return true;
        }

        private static bool TryParseNumber(string text, bool allowNegative, out UssValue value, out string error)
        {
            value = default;
            if (!TrySplitNumber(text, out float number, out string unit) || unit.Length != 0)
            {
                error = "'" + text + "' is not a number";
                return false;
            }

            if (!allowNegative && number < 0f)
            {
                error = "a negative number is not allowed here";
                return false;
            }

            value = UssValue.FromNumber(number);
            error = null;
            return true;
        }

        private static bool TryParseAngle(string text, out UssValue value, out string error)
        {
            value = default;
            if (!TrySplitNumber(text, out float number, out string unit))
            {
                error = "'" + text + "' is not an angle";
                return false;
            }

            float degrees;
            switch (unit)
            {
                case "":
                case "deg":
                    degrees = number;
                    break;
                case "rad":
                    degrees = number * (float)(180.0 / Math.PI);
                    break;
                case "grad":
                    degrees = number * 0.9f;
                    break;
                case "turn":
                    degrees = number * 360f;
                    break;
                default:
                    error = "the angle unit '" + unit + "' is not supported (deg, rad, grad, turn)";
                    return false;
            }

            value = UssValue.FromAngle(degrees);
            error = null;
            return true;
        }

        private static bool TryParseTime(string text, bool allowNegative, out UssValue value, out string error)
        {
            value = default;
            if (!TrySplitNumber(text, out float number, out string unit))
            {
                error = "'" + text + "' is not a duration";
                return false;
            }

            float seconds;
            switch (unit)
            {
                case "":
                case "s":
                    seconds = number;
                    break;
                case "ms":
                    seconds = number / 1000f;
                    break;
                default:
                    error = "the time unit '" + unit + "' is not supported (s or ms)";
                    return false;
            }

            if (!allowNegative && seconds < 0f)
            {
                error = "a negative duration is not allowed";
                return false;
            }

            value = UssValue.FromTime(seconds);
            error = null;
            return true;
        }

        private static bool TryParseAsset(string text, out UssValue value, out string error)
        {
            value = default;
            if (string.Equals(text, UssKeywords.None, StringComparison.OrdinalIgnoreCase))
            {
                value = UssValue.FromKeyword(UssKeywords.None);
                error = null;
                return true;
            }

            int open = text.IndexOf('(');
            if (open <= 0 || text[text.Length - 1] != ')')
            {
                error = "'" + text + "' is not resource(\"path\"), url(\"path\") or none";
                return false;
            }

            string function = text.Substring(0, open).Trim().ToLowerInvariant();
            bool isResource;
            if (function == "resource")
            {
                isResource = true;
            }
            else if (function == "url")
            {
                isResource = false;
            }
            else
            {
                error = "'" + function + "()' is not an asset function (resource or url)";
                return false;
            }

            string path = Unquote(text.Substring(open + 1, text.Length - open - 2).Trim());
            if (path.Length == 0)
            {
                error = "the asset path is empty";
                return false;
            }

            value = UssValue.FromAsset(path, isResource);
            error = null;
            return true;
        }

        private static bool TryParseTranslate(string text, out UssValue value, out string error)
        {
            value = default;
            if (string.Equals(text, UssKeywords.None, StringComparison.OrdinalIgnoreCase))
            {
                value = UssValue.FromKeyword(UssKeywords.None);
                error = null;
                return true;
            }

            List<string> parts = new();
            SplitComponents(text, parts);
            if (parts.Count < 1 || parts.Count > 3)
            {
                error = "translate takes one or two lengths";
                return false;
            }

            if (!TryParseLength(parts[0], true, out UssValue x, out error))
            {
                return false;
            }

            UssValue y = UssValue.FromLength(0f, UssUnit.Pixel);
            if (parts.Count > 1 && !TryParseLength(parts[1], true, out y, out error))
            {
                return false;
            }

            value = UssValue.FromPair(x.A, x.UnitA, y.A, y.UnitA);
            error = null;
            return true;
        }

        private static bool TryParseScale(string text, out UssValue value, out string error)
        {
            value = default;
            if (string.Equals(text, UssKeywords.None, StringComparison.OrdinalIgnoreCase))
            {
                value = UssValue.FromKeyword(UssKeywords.None);
                error = null;
                return true;
            }

            List<string> parts = new();
            SplitComponents(text, parts);
            if (parts.Count < 1 || parts.Count > 3)
            {
                error = "scale takes one or two numbers";
                return false;
            }

            if (!TryParseNumber(parts[0], true, out UssValue x, out error))
            {
                return false;
            }

            UssValue y = x;
            if (parts.Count > 1 && !TryParseNumber(parts[1], true, out y, out error))
            {
                return false;
            }

            value = UssValue.FromPair(x.A, UssUnit.None, y.A, UssUnit.None);
            error = null;
            return true;
        }

        private static bool TryParseTransformOrigin(string text, out UssValue value, out string error)
        {
            value = default;
            List<string> parts = new();
            SplitComponents(text, parts);
            if (parts.Count < 1 || parts.Count > 3)
            {
                error = "transform-origin takes one or two lengths or keywords";
                return false;
            }

            if (parts.Count >= 2 && IsVerticalKeyword(parts[0]) && !IsVerticalKeyword(parts[1]))
            {
                string swap = parts[0];
                parts[0] = parts[1];
                parts[1] = swap;
            }

            UssValue x = UssValue.FromLength(50f, UssUnit.Percent);
            UssValue y = UssValue.FromLength(50f, UssUnit.Percent);
            if (parts.Count == 1 && IsVerticalKeyword(parts[0]))
            {
                if (!TryParseOriginComponent(parts[0], false, out y, out error))
                {
                    return false;
                }
            }
            else
            {
                if (!TryParseOriginComponent(parts[0], true, out x, out error))
                {
                    return false;
                }

                if (parts.Count > 1 && !TryParseOriginComponent(parts[1], false, out y, out error))
                {
                    return false;
                }
            }

            value = UssValue.FromPair(x.A, x.UnitA, y.A, y.UnitA);
            error = null;
            return true;
        }

        private static bool IsVerticalKeyword(string token)
        {
            string lower = token.ToLowerInvariant();
            return lower == "top" || lower == "bottom";
        }

        private static bool TryParseOriginComponent(string token, bool horizontal, out UssValue value,
            out string error)
        {
            string lower = token.ToLowerInvariant();
            switch (lower)
            {
                case "center":
                    value = UssValue.FromLength(50f, UssUnit.Percent);
                    error = null;
                    return true;
                case "left" when horizontal:
                case "top" when !horizontal:
                    value = UssValue.FromLength(0f, UssUnit.Percent);
                    error = null;
                    return true;
                case "right" when horizontal:
                case "bottom" when !horizontal:
                    value = UssValue.FromLength(100f, UssUnit.Percent);
                    error = null;
                    return true;
            }

            return TryParseLength(token, true, out value, out error);
        }

        private static bool TryParseList(string text, ListKind kind, bool allowNegative, out UssValue value,
            out string error)
        {
            value = default;
            List<string> items = new();
            SplitList(text, items);
            UssValue[] values = new UssValue[items.Count];
            for (int i = 0; i < items.Count; i++)
            {
                string item = items[i];
                if (item.Length == 0)
                {
                    error = "an empty list item";
                    return false;
                }

                switch (kind)
                {
                    case ListKind.Time:
                        if (!TryParseTime(item, allowNegative, out values[i], out error))
                        {
                            return false;
                        }

                        break;
                    case ListKind.Easing:
                    {
                        string lower = item.ToLowerInvariant();
                        if (!IsEasing(lower))
                        {
                            error = "'" + item + "' is not an easing function";
                            return false;
                        }

                        values[i] = UssValue.FromKeyword(lower);
                        break;
                    }
                    default:
                    {
                        string lower = item.ToLowerInvariant();
                        if (lower != UssKeywords.All && lower != UssKeywords.None && lower != UssKeywords.Initial
                            && !UssPropertyCatalog.IsSupported(lower))
                        {
                            error = "'" + item + "' is not a property the interpreter can transition";
                            return false;
                        }

                        values[i] = UssValue.FromKeyword(lower);
                        break;
                    }
                }
            }

            value = UssValue.FromList(values);
            error = null;
            return true;
        }

        private static bool TryParseTextShadow(string text, out UssValue value, out string error)
        {
            value = default;
            if (string.Equals(text, UssKeywords.None, StringComparison.OrdinalIgnoreCase))
            {
                value = UssValue.FromKeyword(UssKeywords.None);
                error = null;
                return true;
            }

            List<string> parts = new();
            SplitComponents(text, parts);
            float[] lengths = new float[3];
            int lengthCount = 0;
            UssShadowColor color = null;
            for (int i = 0; i < parts.Count; i++)
            {
                if (TrySplitNumber(parts[i], out float number, out string unit) && (unit.Length == 0 || unit == "px"))
                {
                    if (lengthCount >= 3)
                    {
                        error = "text-shadow takes at most three lengths";
                        return false;
                    }

                    lengths[lengthCount++] = number;
                    continue;
                }

                if (color == null && TryParseColor(parts[i], out UssValue parsedColor))
                {
                    color = new UssShadowColor(parsedColor.A, parsedColor.B, parsedColor.C, parsedColor.D);
                    continue;
                }

                error = "'" + parts[i] + "' is not a pixel length or a colour";
                return false;
            }

            if (lengthCount < 2)
            {
                error = "text-shadow needs an x and a y offset";
                return false;
            }

            if (lengths[2] < 0f)
            {
                error = "the blur radius cannot be negative";
                return false;
            }

            value = UssValue.FromTextShadow(lengths[0], lengths[1], lengths[2], color);
            error = null;
            return true;
        }

        private static bool ExpandBox(UssShorthandSpec spec, string text, UssValue[] output, out string error)
        {
            List<string> parts = new();
            SplitComponents(text, parts);
            if (parts.Count < 1 || parts.Count > 4)
            {
                error = spec.Name + " takes one to four values";
                return false;
            }

            UssValue[] parsed = new UssValue[parts.Count];
            for (int i = 0; i < parts.Count; i++)
            {
                UssPropertySpec longhand = UssPropertyCatalog.Get(spec.Longhands[i]);
                if (!TryParseLonghand(longhand, parts[i], out parsed[i], out error))
                {
                    return false;
                }
            }

            switch (parsed.Length)
            {
                case 1:
                    output[0] = output[1] = output[2] = output[3] = parsed[0];
                    break;
                case 2:
                    output[0] = output[2] = parsed[0];
                    output[1] = output[3] = parsed[1];
                    break;
                case 3:
                    output[0] = parsed[0];
                    output[1] = output[3] = parsed[1];
                    output[2] = parsed[2];
                    break;
                default:
                    output[0] = parsed[0];
                    output[1] = parsed[1];
                    output[2] = parsed[2];
                    output[3] = parsed[3];
                    break;
            }

            error = null;
            return true;
        }

        private static bool ExpandFlex(string text, UssValue[] output, out string error)
        {
            List<string> parts = new();
            SplitComponents(text, parts);
            if (parts.Count == 1)
            {
                string lower = parts[0].ToLowerInvariant();
                if (lower == UssKeywords.None)
                {
                    output[0] = UssValue.FromNumber(0f);
                    output[1] = UssValue.FromNumber(0f);
                    output[2] = UssValue.FromKeyword(UssKeywords.Auto);
                    error = null;
                    return true;
                }

                if (lower == UssKeywords.Auto)
                {
                    output[0] = UssValue.FromNumber(1f);
                    output[1] = UssValue.FromNumber(1f);
                    output[2] = UssValue.FromKeyword(UssKeywords.Auto);
                    error = null;
                    return true;
                }
            }

            if (parts.Count < 1 || parts.Count > 3)
            {
                error = "flex takes none, auto or grow [shrink] [basis]";
                return false;
            }

            // WHY these defaults: UI Toolkit's flex shorthand starts from grow 0, shrink 1, basis 0% when any
            // component is written, so `flex: 1` is 1 1 0% exactly as the editor importer reads it.
            UssValue grow = UssValue.FromNumber(0f);
            UssValue shrink = UssValue.FromNumber(1f);
            UssValue basis = UssValue.FromLength(0f, UssUnit.Percent);
            bool growFound = false;
            bool shrinkFound = false;
            bool basisFound = false;
            for (int i = 0; i < parts.Count; i++)
            {
                string part = parts[i];
                bool isBareNumber = TrySplitNumber(part, out float number, out string unit) && unit.Length == 0;
                if (isBareNumber && !(growFound && shrinkFound))
                {
                    if (number < 0f)
                    {
                        error = "flex-grow and flex-shrink cannot be negative";
                        return false;
                    }

                    if (!growFound)
                    {
                        grow = UssValue.FromNumber(number);
                        growFound = true;
                    }
                    else
                    {
                        shrink = UssValue.FromNumber(number);
                        shrinkFound = true;
                    }

                    continue;
                }

                if (basisFound)
                {
                    error = "flex has more than one basis";
                    return false;
                }

                basisFound = true;
                if (string.Equals(part, UssKeywords.Auto, StringComparison.OrdinalIgnoreCase))
                {
                    basis = UssValue.FromKeyword(UssKeywords.Auto);
                }
                else if (!TryParseLength(part, false, out basis, out error))
                {
                    return false;
                }

                if (growFound && i != parts.Count - 1)
                {
                    error = "the flex basis must come last after grow and shrink";
                    return false;
                }
            }

            output[0] = grow;
            output[1] = shrink;
            output[2] = basis;
            error = null;
            return true;
        }

        private static bool ExpandTransition(string text, UssValue[] output, out string error)
        {
            List<string> items = new();
            SplitList(text, items);
            UssValue[] properties = new UssValue[items.Count];
            UssValue[] durations = new UssValue[items.Count];
            UssValue[] easings = new UssValue[items.Count];
            UssValue[] delays = new UssValue[items.Count];
            List<string> parts = new();
            for (int i = 0; i < items.Count; i++)
            {
                SplitComponents(items[i], parts);
                if (parts.Count == 0)
                {
                    error = "an empty transition";
                    return false;
                }

                properties[i] = UssValue.FromKeyword(UssKeywords.All);
                durations[i] = UssValue.FromTime(0f);
                easings[i] = UssValue.FromKeyword("ease");
                delays[i] = UssValue.FromTime(0f);
                bool propertyFound = false;
                bool easingFound = false;
                int timesFound = 0;
                for (int p = 0; p < parts.Count; p++)
                {
                    string part = parts[p];
                    string lower = part.ToLowerInvariant();
                    if (TryParseTime(part, timesFound > 0, out UssValue time, out _)
                        && (part.EndsWith("s", StringComparison.OrdinalIgnoreCase) || IsZero(part)))
                    {
                        if (timesFound >= 2)
                        {
                            error = "a transition has at most a duration and a delay";
                            return false;
                        }

                        if (timesFound == 0)
                        {
                            durations[i] = time;
                        }
                        else
                        {
                            delays[i] = time;
                        }

                        timesFound++;
                        continue;
                    }

                    if (!easingFound && IsEasing(lower))
                    {
                        easings[i] = UssValue.FromKeyword(lower);
                        easingFound = true;
                        continue;
                    }

                    if (!propertyFound && (lower == UssKeywords.All || lower == UssKeywords.None
                                                                   || UssPropertyCatalog.IsSupported(lower)))
                    {
                        properties[i] = UssValue.FromKeyword(lower);
                        propertyFound = true;
                        continue;
                    }

                    error = "'" + part + "' is not a transition property, duration or easing";
                    return false;
                }
            }

            output[0] = UssValue.FromList(properties);
            output[1] = UssValue.FromList(durations);
            output[2] = UssValue.FromList(easings);
            output[3] = UssValue.FromList(delays);
            error = null;
            return true;
        }

        private static bool ExpandTextOutline(string text, UssValue[] output, out string error)
        {
            List<string> parts = new();
            SplitComponents(text, parts);
            if (parts.Count < 1 || parts.Count > 2)
            {
                error = "-unity-text-outline takes a width and a colour";
                return false;
            }

            UssValue width = UssValue.FromNumber(0f);
            UssValue color = UssValue.FromColor(0f, 0f, 0f, 1f);
            bool widthFound = false;
            bool colorFound = false;
            for (int i = 0; i < parts.Count; i++)
            {
                if (!widthFound && TryParsePixelFloat(parts[i], false, out UssValue parsedWidth, out _))
                {
                    width = parsedWidth;
                    widthFound = true;
                    continue;
                }

                if (!colorFound && TryParseColor(parts[i], out UssValue parsedColor))
                {
                    color = parsedColor;
                    colorFound = true;
                    continue;
                }

                error = "'" + parts[i] + "' is not an outline width or colour";
                return false;
            }

            output[0] = width;
            output[1] = color;
            error = null;
            return true;
        }

        private static bool IsZero(string token)
        {
            return TrySplitNumber(token, out float number, out string unit) && unit.Length == 0 && number == 0f;
        }

        private static bool TryParseHexColor(string text, out UssValue value)
        {
            value = default;
            int length = text.Length - 1;
            if (length != 3 && length != 4 && length != 6 && length != 8)
            {
                return false;
            }

            int[] digits = new int[length];
            for (int i = 0; i < length; i++)
            {
                int digit = HexDigit(text[i + 1]);
                if (digit < 0)
                {
                    return false;
                }

                digits[i] = digit;
            }

            float r;
            float g;
            float b;
            float a = 1f;
            if (length <= 4)
            {
                r = digits[0] * 17 / 255f;
                g = digits[1] * 17 / 255f;
                b = digits[2] * 17 / 255f;
                if (length == 4)
                {
                    a = digits[3] * 17 / 255f;
                }
            }
            else
            {
                r = (digits[0] * 16 + digits[1]) / 255f;
                g = (digits[2] * 16 + digits[3]) / 255f;
                b = (digits[4] * 16 + digits[5]) / 255f;
                if (length == 8)
                {
                    a = (digits[6] * 16 + digits[7]) / 255f;
                }
            }

            value = UssValue.FromColor(r, g, b, a);
            return true;
        }

        private static bool TryParseRgbFunction(string inner, out UssValue value)
        {
            value = default;
            string normalized = inner.Replace('/', ',');
            List<string> parts = new();
            if (normalized.IndexOf(',') >= 0)
            {
                SplitList(normalized, parts);
            }
            else
            {
                SplitComponents(normalized, parts);
            }

            if (parts.Count != 3 && parts.Count != 4)
            {
                return false;
            }

            float[] channels = new float[4];
            channels[3] = 1f;
            for (int i = 0; i < parts.Count; i++)
            {
                if (!TrySplitNumber(parts[i].Trim(), out float number, out string unit))
                {
                    return false;
                }

                if (unit == "%")
                {
                    channels[i] = number / 100f;
                }
                else if (unit.Length == 0)
                {
                    channels[i] = i < 3 ? number / 255f : number;
                }
                else
                {
                    return false;
                }
            }

            value = UssValue.FromColor(channels[0], channels[1], channels[2], channels[3]);
            return true;
        }

        private static int HexDigit(char c)
        {
            if (c >= '0' && c <= '9')
            {
                return c - '0';
            }

            if (c >= 'a' && c <= 'f')
            {
                return c - 'a' + 10;
            }

            if (c >= 'A' && c <= 'F')
            {
                return c - 'A' + 10;
            }

            return -1;
        }

        /// <summary>Splits <c>12.5px</c> into 12.5 and <c>px</c>; the unit is lower-cased.</summary>
        internal static bool TrySplitNumber(string token, out float number, out string unit)
        {
            number = 0f;
            unit = string.Empty;
            if (string.IsNullOrEmpty(token))
            {
                return false;
            }

            int i = 0;
            if (token[0] == '+' || token[0] == '-')
            {
                i++;
            }

            int digitsStart = i;
            bool sawDigit = false;
            while (i < token.Length && char.IsDigit(token[i]))
            {
                i++;
                sawDigit = true;
            }

            if (i < token.Length && token[i] == '.')
            {
                i++;
                while (i < token.Length && char.IsDigit(token[i]))
                {
                    i++;
                    sawDigit = true;
                }
            }

            if (!sawDigit || digitsStart > token.Length)
            {
                return false;
            }

            if (i + 1 < token.Length && (token[i] == 'e' || token[i] == 'E')
                                     && (char.IsDigit(token[i + 1])
                                         || ((token[i + 1] == '-' || token[i + 1] == '+') && i + 2 < token.Length
                                             && char.IsDigit(token[i + 2]))))
            {
                i += 2;
                while (i < token.Length && char.IsDigit(token[i]))
                {
                    i++;
                }
            }

            if (!double.TryParse(token.Substring(0, i), NumberStyles.Float, CultureInfo.InvariantCulture,
                    out double parsed) || double.IsNaN(parsed) || double.IsInfinity(parsed))
            {
                return false;
            }

            number = (float)parsed;
            unit = token.Substring(i).ToLowerInvariant();
            return true;
        }

        private static string Unquote(string text)
        {
            if (text.Length >= 2 && (text[0] == '"' || text[0] == '\'') && text[text.Length - 1] == text[0])
            {
                return text.Substring(1, text.Length - 2);
            }

            return text;
        }
    }
}
