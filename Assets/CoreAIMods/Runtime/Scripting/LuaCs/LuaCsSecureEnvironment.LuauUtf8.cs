using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Lua;

namespace CoreAI.Sandbox.LuaCs
{
    /// <summary>
    /// The Luau <c>utf8</c> library over this VM's strings (DEV-109). A string here is a sequence of UTF-16 code
    /// units: text typed in a script or read from the engine holds one char per code unit, while text built from
    /// byte escapes or <c>string.char</c> holds one char per UTF-8 byte. So a code point is read as: an ASCII char;
    /// a run of chars in 0x80-0xFF that forms a valid UTF-8 sequence (Luau's own decoder rules); any other char
    /// in 0x80-0xFF as itself (Latin-1); a surrogate pair; or any other char. Only an unpaired surrogate is
    /// invalid. Positions are char positions, the ones <c>string.sub</c> takes, and <c>utf8.char</c> builds the
    /// same UTF-16 text a literal would, so <c>utf8.char(233) == "é"</c>.
    /// </summary>
    public sealed partial class LuaCsSecureEnvironment
    {
        /// <summary>Luau's <c>MAXUNICODE</c>.</summary>
        public const int MaxUnicodeCodePoint = 0x10FFFF;

        /// <summary>
        /// <c>utf8.charpattern</c>: the mirror's pattern, with UTF-16 chars above 0xFF admitted as a character
        /// start and low surrogates as its continuation, so <c>s:gmatch(utf8.charpattern)</c> yields each
        /// character of typed text too.
        /// </summary>
        public static readonly string Utf8CharPattern =
            "[%z\u0001-\u007FÂ-ôĀ-\uDBFF-￿][\u0080-¿\uDC00-\uDFFF]*";

        /// <summary>Most code points one <c>utf8.codepoint</c> call returns: Luau's "string slice too long" limit.</summary>
        public const int MaxCodePointResults = MaxUnpackResults;

        private static readonly LuaFunction Utf8CodesIterator = new("utf8.codes", Utf8CodesStep);

        /// <summary>Luau's <c>limits</c>: the largest value each sequence length may not encode (overlong forms).</summary>
        private static readonly int[] Utf8OverlongLimits = { 0xFF, 0x7F, 0x7FF, 0xFFFF };

        private static void InstallLuauUtf8Library(LuaTable environment)
        {
            LuaTable utf8 = new();
            utf8["charpattern"] = Utf8CharPattern;
            utf8["char"] = new LuaFunction("utf8.char", Utf8Char);
            utf8["codes"] = new LuaFunction("utf8.codes", (ctx, ct) =>
                new ValueTask<int>(ctx.Return(Utf8CodesIterator, ReadLuauString(ctx, 0), 0d)));
            utf8["codepoint"] = new LuaFunction("utf8.codepoint", Utf8CodePoint);
            utf8["len"] = new LuaFunction("utf8.len", Utf8Len);
            utf8["offset"] = new LuaFunction("utf8.offset", Utf8Offset);
            utf8["graphemes"] = new LuaFunction("utf8.graphemes", Utf8Graphemes);
            utf8["nfcnormalize"] = new LuaFunction("utf8.nfcnormalize",
                (ctx, ct) => Utf8Normalize(ctx, NormalizationForm.FormC));
            utf8["nfdnormalize"] = new LuaFunction("utf8.nfdnormalize",
                (ctx, ct) => Utf8Normalize(ctx, NormalizationForm.FormD));
            environment["utf8"] = utf8;
        }

        /// <summary>
        /// Decodes the code point starting at char <paramref name="at"/> by the rules on this type: its value and
        /// the chars it spans, or a length of 0 for an unpaired surrogate.
        /// </summary>
        internal static int DecodeCodePoint(string text, int at, out int codePoint)
        {
            char c = text[at];
            if (c < 0x80)
            {
                codePoint = c;
                return 1;
            }

            if (c <= 0xFF)
            {
                int length = DecodeUtf8Bytes(text, at, out codePoint);
                if (length > 0)
                {
                    return length;
                }

                codePoint = c;
                return 1;
            }

            if (char.IsHighSurrogate(c))
            {
                if (at + 1 < text.Length && char.IsLowSurrogate(text[at + 1]))
                {
                    codePoint = char.ConvertToUtf32(c, text[at + 1]);
                    return 2;
                }

                codePoint = c;
                return 0;
            }

            codePoint = c;
            return char.IsLowSurrogate(c) ? 0 : 1;
        }

        /// <summary>
        /// Luau's <c>utf8_decode</c> over chars that are bytes: the sequence length, or 0 when the chars at
        /// <paramref name="at"/> are not a valid UTF-8 sequence (a missing continuation, an overlong form, a value
        /// past <see cref="MaxUnicodeCodePoint"/>, or a char above 0xFF inside it).
        /// </summary>
        private static int DecodeUtf8Bytes(string text, int at, out int codePoint)
        {
            int c = text[at];
            int result = 0;
            int count = 0;
            codePoint = 0;
            while ((c & 0x40) != 0)
            {
                int next = at + count + 1 < text.Length ? text[at + count + 1] : 0;
                if (next > 0xFF || (next & 0xC0) != 0x80)
                {
                    return 0;
                }

                count++;
                result = (result << 6) | (next & 0x3F);
                c <<= 1;
            }

            result |= (c & 0x7F) << (count * 5);
            if (count > 3 || result > MaxUnicodeCodePoint || result <= Utf8OverlongLimits[count])
            {
                return 0;
            }

            codePoint = result;
            return count + 1;
        }

        /// <summary>Luau's <c>u_posrelat</c>: a negative position counts from the end, 0 for one before the start.</summary>
        private static int RelativeCharPosition(int position, int length)
        {
            if (position >= 0)
            {
                return position;
            }

            return -(long)position > length ? 0 : length + position + 1;
        }

        private static ValueTask<int> Utf8Char(LuaFunctionExecutionContext ctx, CancellationToken ct)
        {
            StringBuilder sb = new(ctx.ArgumentCount);
            for (int i = 0; i < ctx.ArgumentCount; i++)
            {
                long code = (uint)ReadLuauInt(ctx, i);
                if (code > MaxUnicodeCodePoint)
                {
                    LuaRuntimeException.BadArgument(ctx.State, i + 1, "value out of range");
                }

                if (code > 0xFFFF)
                {
                    sb.Append(char.ConvertFromUtf32((int)code));
                }
                else
                {
                    sb.Append((char)code);
                }
            }

            return new ValueTask<int>(ctx.Return(sb.ToString()));
        }

        /// <summary>The iterator <c>utf8.codes</c> returns: the next code point's position and value.</summary>
        private static ValueTask<int> Utf8CodesStep(LuaFunctionExecutionContext ctx, CancellationToken ct)
        {
            string text = ReadLuauString(ctx, 0);
            int position = ReadLuauInt(ctx, 1) - 1;
            if (position < 0)
            {
                position = 0;
            }
            else if (position < text.Length)
            {
                int length = DecodeCodePoint(text, position, out int _);
                position += length == 0 ? 1 : length;
            }

            if (position >= text.Length)
            {
                return new ValueTask<int>(ctx.Return());
            }

            if (DecodeCodePoint(text, position, out int codePoint) == 0)
            {
                throw LibraryRefusal(ctx.State, "invalid UTF-8 code");
            }

            return new ValueTask<int>(ctx.Return(position + 1, codePoint));
        }

        private static ValueTask<int> Utf8CodePoint(LuaFunctionExecutionContext ctx, CancellationToken ct)
        {
            string text = ReadLuauString(ctx, 0);
            int first = RelativeCharPosition(ReadOptionalLuauInt(ctx, 1, 1), text.Length);
            int last = RelativeCharPosition(ReadOptionalLuauInt(ctx, 2, first), text.Length);
            if (first < 1)
            {
                LuaRuntimeException.BadArgument(ctx.State, 2, "out of range");
            }

            if (last > text.Length)
            {
                LuaRuntimeException.BadArgument(ctx.State, 3, "out of range");
            }

            if (first > last)
            {
                return new ValueTask<int>(ctx.Return());
            }

            if (last - first >= MaxCodePointResults)
            {
                throw LibraryRefusal(ctx.State, "string slice too long");
            }

            List<LuaValue> codes = new(last - first + 1);
            int at = first - 1;
            while (at < last)
            {
                int length = DecodeCodePoint(text, at, out int codePoint);
                if (length == 0)
                {
                    throw LibraryRefusal(ctx.State, "invalid UTF-8 code");
                }

                codes.Add(codePoint);
                at += length;
            }

            return new ValueTask<int>(ctx.Return(codes.ToArray().AsSpan()));
        }

        private static ValueTask<int> Utf8Len(LuaFunctionExecutionContext ctx, CancellationToken ct)
        {
            string text = ReadLuauString(ctx, 0);
            int first = RelativeCharPosition(ReadOptionalLuauInt(ctx, 1, 1), text.Length);
            int last = RelativeCharPosition(ReadOptionalLuauInt(ctx, 2, -1), text.Length);
            if (!(first >= 1 && first - 1 <= text.Length))
            {
                LuaRuntimeException.BadArgument(ctx.State, 2, "initial position out of string");
            }

            if (!(last - 1 < text.Length))
            {
                LuaRuntimeException.BadArgument(ctx.State, 3, "final position out of string");
            }

            int count = 0;
            int at = first - 1;
            while (at <= last - 1)
            {
                int length = DecodeCodePoint(text, at, out int _);
                if (length == 0)
                {
                    return new ValueTask<int>(ctx.Return(LuaValue.Nil, at + 1));
                }

                at += length;
                count++;
            }

            return new ValueTask<int>(ctx.Return(count));
        }

        /// <summary>True when char <paramref name="at"/> continues a code point that starts before it.</summary>
        private static bool IsContinuation(string text, int at)
        {
            if (at <= 0 || at >= text.Length)
            {
                return false;
            }

            if (char.IsLowSurrogate(text[at]))
            {
                return char.IsHighSurrogate(text[at - 1]);
            }

            if (text[at] < 0x80 || text[at] > 0xBF)
            {
                return false;
            }

            for (int start = at - 1; start >= 0 && start >= at - 3; start--)
            {
                char c = text[start];
                if (c < 0x80 || c > 0xFF)
                {
                    return false;
                }

                if (c >= 0xC0)
                {
                    return DecodeUtf8Bytes(text, start, out int _) > at - start;
                }
            }

            return false;
        }

        private static ValueTask<int> Utf8Offset(LuaFunctionExecutionContext ctx, CancellationToken ct)
        {
            string text = ReadLuauString(ctx, 0);
            int n = ReadLuauInt(ctx, 1);
            int position = RelativeCharPosition(ReadOptionalLuauInt(ctx, 2, n >= 0 ? 1 : text.Length + 1),
                text.Length);
            if (!(position >= 1 && position - 1 <= text.Length))
            {
                LuaRuntimeException.BadArgument(ctx.State, 3, "position out of range");
            }

            int at = position - 1;
            if (n == 0)
            {
                while (at > 0 && IsContinuation(text, at))
                {
                    at--;
                }

                return new ValueTask<int>(ctx.Return(at + 1));
            }

            if (IsContinuation(text, at))
            {
                throw LibraryRefusal(ctx.State, "initial position is a continuation byte");
            }

            if (n < 0)
            {
                while (n < 0 && at > 0)
                {
                    do
                    {
                        at--;
                    } while (at > 0 && IsContinuation(text, at));

                    n++;
                }
            }
            else
            {
                n--;
                while (n > 0 && at < text.Length)
                {
                    do
                    {
                        at++;
                    } while (IsContinuation(text, at));

                    n--;
                }
            }

            return new ValueTask<int>(n == 0 ? ctx.Return(at + 1) : ctx.Return(LuaValue.Nil));
        }

        /// <summary>
        /// Roblox <c>utf8.graphemes(s, i, j)</c>: an iterator of the first and last char position of each grapheme
        /// cluster, a base code point with the combining marks, variation selectors, emoji modifiers, zero-width
        /// joiner sequences and regional-indicator pairs that follow it, and CR LF as one (DEV-109).
        /// </summary>
        private static ValueTask<int> Utf8Graphemes(LuaFunctionExecutionContext ctx, CancellationToken ct)
        {
            string text = ReadLuauString(ctx, 0);
            int first = RelativeCharPosition(ReadOptionalLuauInt(ctx, 1, 1), text.Length);
            int last = RelativeCharPosition(ReadOptionalLuauInt(ctx, 2, -1), text.Length);
            if (first < 1)
            {
                first = 1;
            }

            if (last > text.Length)
            {
                last = text.Length;
            }

            int at = first - 1;
            int end = last;
            LuaFunction iterator = new("utf8.graphemes", (c, t) =>
            {
                if (at >= end)
                {
                    return new ValueTask<int>(c.Return());
                }

                int start = at;
                at = NextGraphemeEnd(text, at, end);
                return new ValueTask<int>(c.Return(start + 1, at));
            });
            return new ValueTask<int>(ctx.Return(iterator));
        }

        /// <summary>The char index just past the grapheme cluster that starts at <paramref name="at"/>.</summary>
        private static int NextGraphemeEnd(string text, int at, int end)
        {
            int length = Math.Max(1, DecodeCodePoint(text, at, out int codePoint));
            int next = at + length;
            if (codePoint == '\r' && next < end && text[next] == '\n')
            {
                return next + 1;
            }

            bool regional = IsRegionalIndicator(codePoint);
            bool joined = false;
            while (next < end)
            {
                int nextLength = Math.Max(1, DecodeCodePoint(text, next, out int follower));
                if (joined || IsGraphemeExtender(follower) || (regional && IsRegionalIndicator(follower)))
                {
                    joined = follower == 0x200D;
                    regional = false;
                    next += nextLength;
                    continue;
                }

                break;
            }

            return next;
        }

        private static bool IsRegionalIndicator(int codePoint)
        {
            return codePoint >= 0x1F1E6 && codePoint <= 0x1F1FF;
        }

        private static bool IsGraphemeExtender(int codePoint)
        {
            if (codePoint == 0x200D || (codePoint >= 0xFE00 && codePoint <= 0xFE0F)
                                    || (codePoint >= 0x1F3FB && codePoint <= 0x1F3FF)
                                    || (codePoint >= 0xE0020 && codePoint <= 0xE007F)
                                    || (codePoint >= 0xE0100 && codePoint <= 0xE01EF))
            {
                return true;
            }

            if (codePoint > MaxUnicodeCodePoint || (codePoint >= 0xD800 && codePoint <= 0xDFFF))
            {
                return false;
            }

            UnicodeCategory category = CharUnicodeInfo.GetUnicodeCategory(char.ConvertFromUtf32(codePoint), 0);
            return category == UnicodeCategory.NonSpacingMark || category == UnicodeCategory.SpacingCombiningMark
                                                              || category == UnicodeCategory.EnclosingMark;
        }

        /// <summary>
        /// <c>utf8.nfcnormalize</c>/<c>utf8.nfdnormalize</c>: the code points read by this type's rules, normalized
        /// by the runtime's Unicode tables and returned as UTF-16 text.
        /// </summary>
        private static ValueTask<int> Utf8Normalize(LuaFunctionExecutionContext ctx, NormalizationForm form)
        {
            string text = ReadLuauString(ctx, 0);
            StringBuilder decoded = new(text.Length);
            int at = 0;
            while (at < text.Length)
            {
                int length = DecodeCodePoint(text, at, out int codePoint);
                if (length == 0)
                {
                    decoded.Append(text[at]);
                    at++;
                    continue;
                }

                decoded.Append(codePoint > 0xFFFF ? char.ConvertFromUtf32(codePoint) : ((char)codePoint).ToString());
                at += length;
            }

            string result;
            try
            {
                result = decoded.ToString().Normalize(form);
            }
            catch (ArgumentException)
            {
                // WHY: .NET refuses an unpaired surrogate; Luau would refuse the invalid code the same way.
                throw LibraryRefusal(ctx.State, "invalid UTF-8 code");
            }

            return new ValueTask<int>(ctx.Return(result));
        }
    }
}
