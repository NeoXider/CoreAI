using System;

namespace CoreAI.Ai
{
    public readonly struct LuaModHeader
    {
        public readonly string Id;
        public readonly string Name;
        public readonly string Version;
        public readonly string Capabilities;
        public readonly string Category;
        public readonly string Author;
        public readonly string Description;
        public readonly string Tags;
        public readonly bool Active;

        private LuaModHeader(
            string id,
            string name,
            string version,
            string capabilities,
            string category,
            string author,
            string description,
            string tags,
            bool active)
        {
            Id = id;
            Name = name;
            Version = version;
            Capabilities = capabilities;
            Category = category;
            Author = author;
            Description = description;
            Tags = tags;
            Active = active;
        }

        public static LuaModHeader Parse(string source, string fallbackId)
        {
            Builder builder = new(fallbackId);
            if (TryParseBlockHeader(source, ref builder) || TryParseLineHeader(source, ref builder))
            {
                return builder.Build();
            }

            return builder.Build();
        }

        private static bool TryParseBlockHeader(string source, ref Builder builder)
        {
            const string marker = "--[[@coreai";
            if (string.IsNullOrEmpty(source) || !source.StartsWith(marker, StringComparison.Ordinal))
            {
                return false;
            }

            int start = marker.Length;
            int end = source.IndexOf("]]", start, StringComparison.Ordinal);
            if (end < 0)
            {
                return false;
            }

            ParseEntries(source, start, end, ref builder);
            return true;
        }

        private static bool TryParseLineHeader(string source, ref Builder builder)
        {
            if (string.IsNullOrEmpty(source))
            {
                return false;
            }

            int position = 0;
            bool parsed = false;
            while (position < source.Length)
            {
                int lineStart = position;
                int lineEnd = source.IndexOf('\n', lineStart);
                if (lineEnd < 0)
                {
                    lineEnd = source.Length;
                    position = source.Length;
                }
                else
                {
                    position = lineEnd + 1;
                }

                int contentEnd = lineEnd;
                if (contentEnd > lineStart && source[contentEnd - 1] == '\r')
                {
                    contentEnd--;
                }

                int contentStart = SkipWhitespace(source, lineStart, contentEnd);
                if (contentStart == contentEnd)
                {
                    if (parsed)
                    {
                        break;
                    }

                    continue;
                }

                if (!StartsWith(source, contentStart, contentEnd, "--"))
                {
                    break;
                }

                int markerStart = SkipWhitespace(source, contentStart + 2, contentEnd);
                if (!StartsWith(source, markerStart, contentEnd, "@coreai"))
                {
                    break;
                }

                int entryStart = SkipWhitespace(source, markerStart + "@coreai".Length, contentEnd);
                ParseEntry(source, entryStart, contentEnd, ref builder);
                parsed = true;
            }

            return parsed;
        }

        private static void ParseEntries(string source, int start, int end, ref Builder builder)
        {
            int position = start;
            while (position < end)
            {
                int lineStart = position;
                int lineEnd = source.IndexOf('\n', lineStart);
                if (lineEnd < 0 || lineEnd > end)
                {
                    lineEnd = end;
                    position = end;
                }
                else
                {
                    position = lineEnd + 1;
                }

                if (lineEnd > lineStart && source[lineEnd - 1] == '\r')
                {
                    lineEnd--;
                }

                ParseEntry(source, lineStart, lineEnd, ref builder);
            }
        }

        private static void ParseEntry(string source, int start, int end, ref Builder builder)
        {
            start = SkipWhitespace(source, start, end);
            end = TrimEnd(source, start, end);
            if (start >= end || StartsWith(source, start, end, "@coreai"))
            {
                return;
            }

            int colon = source.IndexOf(':', start, end - start);
            if (colon < 0)
            {
                return;
            }

            int keyEnd = TrimEnd(source, start, colon);
            int valueStart = SkipWhitespace(source, colon + 1, end);
            int valueEnd = TrimEnd(source, valueStart, end);
            string key = source.Substring(start, keyEnd - start);
            string value = source.Substring(valueStart, valueEnd - valueStart);

            builder.Apply(key, value);
        }

        private static int SkipWhitespace(string source, int start, int end)
        {
            while (start < end && char.IsWhiteSpace(source[start]))
            {
                start++;
            }

            return start;
        }

        private static int TrimEnd(string source, int start, int end)
        {
            while (end > start && char.IsWhiteSpace(source[end - 1]))
            {
                end--;
            }

            return end;
        }

        private static bool StartsWith(string source, int start, int end, string token)
        {
            if (start < 0 || start + token.Length > end)
            {
                return false;
            }

            for (int i = 0; i < token.Length; i++)
            {
                if (source[start + i] != token[i])
                {
                    return false;
                }
            }

            return true;
        }

        private struct Builder
        {
            private string _id;
            private string _name;
            private string _version;
            private string _capabilities;
            private string _category;
            private string _author;
            private string _description;
            private string _tags;
            private bool _active;

            public Builder(string fallbackId)
            {
                _id = fallbackId ?? "";
                _name = null;
                _version = "0.0.0";
                _capabilities = LuaCapabilities.All.ToString();
                _category = "";
                _author = "";
                _description = "";
                _tags = "";
                _active = true;
            }

            public void Apply(string key, string value)
            {
                if (key.Equals("id", StringComparison.OrdinalIgnoreCase))
                {
                    _id = value;
                }
                else if (key.Equals("name", StringComparison.OrdinalIgnoreCase))
                {
                    _name = value;
                }
                else if (key.Equals("version", StringComparison.OrdinalIgnoreCase))
                {
                    _version = value;
                }
                else if (key.Equals("capabilities", StringComparison.OrdinalIgnoreCase))
                {
                    _capabilities = NormalizeCapabilities(value);
                }
                else if (key.Equals("category", StringComparison.OrdinalIgnoreCase))
                {
                    _category = value;
                }
                else if (key.Equals("author", StringComparison.OrdinalIgnoreCase))
                {
                    _author = value;
                }
                else if (key.Equals("description", StringComparison.OrdinalIgnoreCase))
                {
                    _description = value;
                }
                else if (key.Equals("tags", StringComparison.OrdinalIgnoreCase))
                {
                    _tags = value;
                }
                else if (key.Equals("active", StringComparison.OrdinalIgnoreCase))
                {
                    _active = !value.Equals("false", StringComparison.OrdinalIgnoreCase);
                }
            }

            public LuaModHeader Build()
            {
                string id = _id ?? "";
                return new LuaModHeader(
                    id,
                    _name ?? id,
                    _version ?? "0.0.0",
                    _capabilities ?? LuaCapabilities.All.ToString(),
                    _category ?? "",
                    _author ?? "",
                    _description ?? "",
                    _tags ?? "",
                    _active);
            }

            private static string NormalizeCapabilities(string value)
            {
                if (string.IsNullOrWhiteSpace(value))
                {
                    return "";
                }

                string[] parts = value.Replace(',', ' ').Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 0)
                {
                    return "";
                }

                LuaCapabilities capabilities = LuaCapabilities.None;
                for (int i = 0; i < parts.Length; i++)
                {
                    // WHY: Tolerant parse (mirrors HubModServiceBase.ParseCaps): an unknown token in one
                    // LLM/hand-written header must not throw out of Parse and take down whole-directory
                    // loads such as ResourcesBundledModSource; unknown tokens are simply skipped.
                    if (Enum.TryParse(parts[i], true, out LuaCapabilities parsed))
                    {
                        capabilities |= parsed;
                    }
                }

                return capabilities.ToString();
            }
        }
    }
}
