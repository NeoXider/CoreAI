#if COREAI_LLM
using System;
using System.Text;

namespace CoreAI.Infrastructure.Llm
{
    /// <summary>
    /// Reads the one streamed-chunk shape that carries almost every token of a chat turn, without building a
    /// JSON tree: an OpenAI-compatible <c>chat.completion.chunk</c> whose single choice has a <c>delta</c> with
    /// <c>content</c> and/or a reasoning field and nothing else that changes the result. Anything outside that
    /// whitelist (tool calls, usage, finish reasons, errors, progress frames, unknown keys, duplicate keys,
    /// surrogate escapes, numbers that are not plain integers, any syntax the strict reader does not know) is
    /// declined, and <see cref="MeaiOpenAiChatClient"/> parses that chunk with the full Newtonsoft path, so
    /// the update a caller sees is the same whichever path produced it.
    /// </summary>
    /// <remarks>
    /// WHY: the full path allocated a <c>Substring</c> of the line, a <c>JsonTextReader</c> with its char
    /// buffer and a <c>JObject</c> tree with line-info annotations for every token, about 7 KB and 17 µs per
    /// token on CoreCLR (dev-docs/PERF_BASELINE_2026-11.md, finding M3), garbage that WebGL's non-moving
    /// collector pays for at token rate. The reader walks the line in place and allocates only the strings
    /// the update keeps.
    /// </remarks>
    internal static class SseContentDeltaReader
    {
        /// <summary>The fields of an accepted chunk that the update is built from.</summary>
        internal struct Delta
        {
            /// <summary>The chunk's <c>id</c>, or null when the chunk has none.</summary>
            public string Id;

            /// <summary>The chunk's <c>model</c>, or null when the chunk has none.</summary>
            public string Model;

            /// <summary><c>delta.content</c>, or null when absent or JSON null.</summary>
            public string Content;

            /// <summary>
            /// The first non-empty of <c>reasoning_content</c>, <c>reasoning</c>, <c>reasoningContent</c>,
            /// <c>reasoning_text</c> (the full path's order), or empty.
            /// </summary>
            public string Reasoning;
        }

        private const int ReasoningFieldCount = 4;

        /// <summary>
        /// True when <paramref name="line"/>[<paramref name="start"/>..<paramref name="end"/>) is a whitelisted
        /// content or reasoning delta that yields an update; false sends the chunk to the full parser.
        /// </summary>
        internal static bool TryRead(string line, int start, int end, out Delta delta)
        {
            delta = default;
            Cursor cursor = new(line, start, end);
            if (!ReadRoot(ref cursor, ref delta))
            {
                delta = default;
                return false;
            }

            cursor.SkipWhitespace();
            if (cursor.Position != end)
            {
                delta = default;
                return false;
            }

            if (string.IsNullOrEmpty(delta.Content) && string.IsNullOrEmpty(delta.Reasoning))
            {
                delta = default;
                return false;
            }

            return true;
        }

        private static bool ReadRoot(ref Cursor cursor, ref Delta delta)
        {
            cursor.SkipWhitespace();
            if (!cursor.Consume('{'))
            {
                return false;
            }

            bool seenId = false;
            bool seenObject = false;
            bool seenCreated = false;
            bool seenModel = false;
            bool seenFingerprint = false;
            bool seenTier = false;
            bool seenObfuscation = false;
            bool seenUsage = false;
            bool seenChoices = false;
            if (cursor.ConsumeAfterWhitespace('}'))
            {
                return false;
            }

            while (true)
            {
                if (!cursor.ReadPropertyName(out PropertyName name))
                {
                    return false;
                }

                switch (name)
                {
                    case PropertyName.Id:
                        if (!Once(ref seenId) || !cursor.ReadString(out delta.Id))
                        {
                            return false;
                        }

                        break;
                    case PropertyName.Model:
                        if (!Once(ref seenModel) || !cursor.ReadString(out delta.Model))
                        {
                            return false;
                        }

                        break;
                    case PropertyName.Object:
                        if (!Once(ref seenObject) || !cursor.SkipString())
                        {
                            return false;
                        }

                        break;
                    case PropertyName.Obfuscation:
                        if (!Once(ref seenObfuscation) || !cursor.SkipString())
                        {
                            return false;
                        }

                        break;
                    case PropertyName.Created:
                        if (!Once(ref seenCreated) || !cursor.SkipInteger())
                        {
                            return false;
                        }

                        break;
                    case PropertyName.SystemFingerprint:
                        if (!Once(ref seenFingerprint) || !cursor.SkipStringOrNull())
                        {
                            return false;
                        }

                        break;
                    case PropertyName.ServiceTier:
                        if (!Once(ref seenTier) || !cursor.SkipStringOrNull())
                        {
                            return false;
                        }

                        break;
                    case PropertyName.Usage:
                        if (!Once(ref seenUsage) || !cursor.ConsumeNull())
                        {
                            return false;
                        }

                        break;
                    case PropertyName.Choices:
                        if (!Once(ref seenChoices) || !ReadChoices(ref cursor, ref delta))
                        {
                            return false;
                        }

                        break;
                    default:
                        return false;
                }

                if (cursor.ConsumeAfterWhitespace(','))
                {
                    continue;
                }

                return seenChoices && cursor.ConsumeAfterWhitespace('}');
            }
        }

        private static bool ReadChoices(ref Cursor cursor, ref Delta delta)
        {
            if (!cursor.ConsumeAfterWhitespace('[') || !cursor.ConsumeAfterWhitespace('{'))
            {
                return false;
            }

            bool seenIndex = false;
            bool seenDelta = false;
            bool seenFinish = false;
            bool seenLogprobs = false;
            if (cursor.ConsumeAfterWhitespace('}'))
            {
                return false;
            }

            while (true)
            {
                if (!cursor.ReadPropertyName(out PropertyName name))
                {
                    return false;
                }

                switch (name)
                {
                    case PropertyName.Index:
                        if (!Once(ref seenIndex) || !cursor.SkipInteger())
                        {
                            return false;
                        }

                        break;
                    case PropertyName.Delta:
                        if (!Once(ref seenDelta) || !ReadDelta(ref cursor, ref delta))
                        {
                            return false;
                        }

                        break;
                    case PropertyName.FinishReason:
                        if (!Once(ref seenFinish) || !cursor.ConsumeNull())
                        {
                            return false;
                        }

                        break;
                    case PropertyName.Logprobs:
                        if (!Once(ref seenLogprobs) || !cursor.ConsumeNull())
                        {
                            return false;
                        }

                        break;
                    default:
                        return false;
                }

                if (cursor.ConsumeAfterWhitespace(','))
                {
                    continue;
                }

                // WHY exactly one choice: the full path reads choices[0] but validates every element, so a
                // second choice is left to it.
                return seenDelta && cursor.ConsumeAfterWhitespace('}') && cursor.ConsumeAfterWhitespace(']');
            }
        }

        private static bool ReadDelta(ref Cursor cursor, ref Delta delta)
        {
            if (!cursor.ConsumeAfterWhitespace('{'))
            {
                return false;
            }

            bool seenRole = false;
            bool seenContent = false;
            bool seenRefusal = false;
            int seenReasoning = 0;
            string[] reasoning = null;
            if (!cursor.ConsumeAfterWhitespace('}'))
            {
                while (true)
                {
                    if (!cursor.ReadPropertyName(out PropertyName name))
                    {
                        return false;
                    }

                    switch (name)
                    {
                        case PropertyName.Role:
                            if (!Once(ref seenRole) || !cursor.SkipString())
                            {
                                return false;
                            }

                            break;
                        case PropertyName.Content:
                            if (!Once(ref seenContent) || !cursor.ReadStringOrNull(out delta.Content))
                            {
                                return false;
                            }

                            break;
                        case PropertyName.Refusal:
                            if (!Once(ref seenRefusal) || !cursor.ConsumeNull())
                            {
                                return false;
                            }

                            break;
                        case PropertyName.ReasoningContent:
                        case PropertyName.Reasoning:
                        case PropertyName.ReasoningContentCamel:
                        case PropertyName.ReasoningText:
                            int slot = ReasoningSlot(name);
                            int bit = 1 << slot;
                            if ((seenReasoning & bit) != 0 || !cursor.ReadStringOrNull(out string text))
                            {
                                return false;
                            }

                            seenReasoning |= bit;
                            if (!string.IsNullOrEmpty(text))
                            {
                                reasoning ??= new string[ReasoningFieldCount];
                                reasoning[slot] = text;
                            }

                            break;
                        default:
                            return false;
                    }

                    if (cursor.ConsumeAfterWhitespace(','))
                    {
                        continue;
                    }

                    if (!cursor.ConsumeAfterWhitespace('}'))
                    {
                        return false;
                    }

                    break;
                }
            }

            delta.Reasoning = "";
            if (reasoning != null)
            {
                for (int index = 0; index < ReasoningFieldCount; index++)
                {
                    if (!string.IsNullOrEmpty(reasoning[index]))
                    {
                        delta.Reasoning = reasoning[index];
                        break;
                    }
                }
            }

            return true;
        }

        private static int ReasoningSlot(PropertyName name)
        {
            switch (name)
            {
                case PropertyName.ReasoningContent:
                    return 0;
                case PropertyName.Reasoning:
                    return 1;
                case PropertyName.ReasoningContentCamel:
                    return 2;
                default:
                    return 3;
            }
        }

        private static bool Once(ref bool seen)
        {
            if (seen)
            {
                return false;
            }

            seen = true;
            return true;
        }

        private enum PropertyName
        {
            Unknown,
            Id,
            Object,
            Created,
            Model,
            SystemFingerprint,
            ServiceTier,
            Obfuscation,
            Usage,
            Choices,
            Index,
            Delta,
            FinishReason,
            Logprobs,
            Role,
            Content,
            Refusal,
            ReasoningContent,
            Reasoning,
            ReasoningContentCamel,
            ReasoningText
        }

        private struct Cursor
        {
            private static readonly string[] Names =
            {
                "", "id", "object", "created", "model", "system_fingerprint", "service_tier", "obfuscation",
                "usage", "choices", "index", "delta", "finish_reason", "logprobs", "role", "content", "refusal",
                "reasoning_content", "reasoning", "reasoningContent", "reasoning_text"
            };

            private readonly string _text;
            private readonly int _end;
            private int _position;

            public Cursor(string text, int start, int end)
            {
                _text = text;
                _position = start;
                _end = end;
            }

            public int Position => _position;

            public void SkipWhitespace()
            {
                while (_position < _end)
                {
                    char c = _text[_position];
                    if (c != ' ' && c != '\t' && c != '\n' && c != '\r')
                    {
                        return;
                    }

                    _position++;
                }
            }

            public bool Consume(char expected)
            {
                if (_position < _end && _text[_position] == expected)
                {
                    _position++;
                    return true;
                }

                return false;
            }

            public bool ConsumeAfterWhitespace(char expected)
            {
                SkipWhitespace();
                return Consume(expected);
            }

            public bool ConsumeNull()
            {
                SkipWhitespace();
                if (_end - _position < 4 || string.CompareOrdinal(_text, _position, "null", 0, 4) != 0)
                {
                    return false;
                }

                _position += 4;
                return true;
            }

            /// <summary>Reads <c>"name"</c> and the colon after it; false for an escaped or unexpected name.</summary>
            public bool ReadPropertyName(out PropertyName name)
            {
                name = PropertyName.Unknown;
                SkipWhitespace();
                if (!Consume('"'))
                {
                    return false;
                }

                int nameStart = _position;
                while (_position < _end)
                {
                    char c = _text[_position];
                    if (c == '"')
                    {
                        break;
                    }

                    if (c == '\\' || c < ' ')
                    {
                        return false;
                    }

                    _position++;
                }

                if (_position >= _end)
                {
                    return false;
                }

                int length = _position - nameStart;
                _position++;
                for (int index = 1; index < Names.Length; index++)
                {
                    string candidate = Names[index];
                    if (candidate.Length == length &&
                        string.CompareOrdinal(_text, nameStart, candidate, 0, length) == 0)
                    {
                        name = (PropertyName)index;
                        break;
                    }
                }

                return name != PropertyName.Unknown && ConsumeAfterWhitespace(':');
            }

            public bool SkipString()
            {
                return ReadStringCore(false, out _);
            }

            public bool SkipStringOrNull()
            {
                SkipWhitespace();
                return ConsumeNull() || ReadStringCore(false, out _);
            }

            public bool ReadString(out string value)
            {
                return ReadStringCore(true, out value);
            }

            public bool ReadStringOrNull(out string value)
            {
                SkipWhitespace();
                if (ConsumeNull())
                {
                    value = null;
                    return true;
                }

                return ReadStringCore(true, out value);
            }

            /// <summary>Plain non-negative integer digits, the only number shape the whitelist knows.</summary>
            public bool SkipInteger()
            {
                SkipWhitespace();
                int digitsStart = _position;
                while (_position < _end && _text[_position] >= '0' && _text[_position] <= '9')
                {
                    _position++;
                }

                int digits = _position - digitsStart;
                // WHY a leading zero declines: Newtonsoft reads 0-prefixed digits as an octal literal and
                // throws on an invalid one, so only the full parser may decide such a chunk.
                if (digits == 0 || digits > 18 || digits > 1 && _text[digitsStart] == '0')
                {
                    return false;
                }

                if (_position < _end)
                {
                    char next = _text[_position];
                    if (next == '.' || next == 'e' || next == 'E')
                    {
                        return false;
                    }
                }

                return true;
            }

            /// <summary>
            /// Reads a JSON string with the escapes Newtonsoft decodes the same way (<c>\" \\ \/ \b \f \n \r \t</c>
            /// and a non-surrogate <c>\uXXXX</c>); a raw control character, a surrogate escape or any other escape
            /// declines, so only text whose decoding is unambiguous takes this path.
            /// </summary>
            private bool ReadStringCore(bool keep, out string value)
            {
                value = null;
                SkipWhitespace();
                if (!Consume('"'))
                {
                    return false;
                }

                int contentStart = _position;
                StringBuilder decoded = null;
                int copiedUpTo = contentStart;
                while (_position < _end)
                {
                    char c = _text[_position];
                    if (c == '"')
                    {
                        if (keep)
                        {
                            if (decoded == null)
                            {
                                value = _text.Substring(contentStart, _position - contentStart);
                            }
                            else
                            {
                                decoded.Append(_text, copiedUpTo, _position - copiedUpTo);
                                value = decoded.ToString();
                            }
                        }

                        _position++;
                        return true;
                    }

                    if (c < ' ')
                    {
                        return false;
                    }

                    if (c != '\\')
                    {
                        _position++;
                        continue;
                    }

                    if (_position + 1 >= _end)
                    {
                        return false;
                    }

                    char escape = _text[_position + 1];
                    char replacement;
                    int consumed = 2;
                    switch (escape)
                    {
                        case '"':
                            replacement = '"';
                            break;
                        case '\\':
                            replacement = '\\';
                            break;
                        case '/':
                            replacement = '/';
                            break;
                        case 'b':
                            replacement = '\b';
                            break;
                        case 'f':
                            replacement = '\f';
                            break;
                        case 'n':
                            replacement = '\n';
                            break;
                        case 'r':
                            replacement = '\r';
                            break;
                        case 't':
                            replacement = '\t';
                            break;
                        case 'u':
                            if (!TryReadHex(_position + 2, out replacement) ||
                                char.IsSurrogate(replacement))
                            {
                                return false;
                            }

                            consumed = 6;
                            break;
                        default:
                            return false;
                    }

                    if (keep)
                    {
                        decoded ??= new StringBuilder(_position - contentStart + 16);
                        decoded.Append(_text, copiedUpTo, _position - copiedUpTo);
                        decoded.Append(replacement);
                    }

                    _position += consumed;
                    copiedUpTo = _position;
                }

                return false;
            }

            private bool TryReadHex(int at, out char value)
            {
                value = '\0';
                if (at + 4 > _end)
                {
                    return false;
                }

                int code = 0;
                for (int index = 0; index < 4; index++)
                {
                    char c = _text[at + index];
                    int digit = c >= '0' && c <= '9' ? c - '0'
                        : c >= 'a' && c <= 'f' ? c - 'a' + 10
                        : c >= 'A' && c <= 'F' ? c - 'A' + 10
                        : -1;
                    if (digit < 0)
                    {
                        return false;
                    }

                    code = code * 16 + digit;
                }

                value = (char)code;
                return true;
            }
        }
    }
}
#endif
