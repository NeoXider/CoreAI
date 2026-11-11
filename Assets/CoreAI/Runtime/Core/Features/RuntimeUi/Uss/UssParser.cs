using System;
using System.Collections.Generic;

namespace CoreAI.RuntimeUi
{
    /// <summary>
    /// A forgiving parser for the supported USS subset. It never throws: a broken rule is reported with
    /// its line and skipped, and parsing resumes at the next rule.
    /// </summary>
    public static class UssParser
    {
        /// <summary>Parses a style sheet.</summary>
        public static UssStyleSheet Parse(string text, RuntimeUiParseLimits limits = null)
        {
            RuntimeUiParseLimits effective = limits ?? RuntimeUiParseLimits.Default;
            RuntimeUiDiagnosticList diagnostics = new(effective.MaxDiagnostics);
            string source = text ?? string.Empty;
            if (source.Length > effective.MaxTextLength)
            {
                diagnostics.Add(RuntimeUiDiagnosticCodes.UssLimitExceeded, RuntimeUiDiagnosticSeverity.Error,
                    RuntimeUiSource.Uss, 1, 1,
                    "the style sheet is " + source.Length + " characters; the limit is " + effective.MaxTextLength);
                return new UssStyleSheet(Array.Empty<UssRule>(), diagnostics.ToArray());
            }

            RuntimeUiLineMap lines = new(source);
            string clean = StripComments(source, lines, diagnostics, RuntimeUiSource.Uss);
            List<UssRule> rules = new();
            int pos = 0;
            bool limitReported = false;
            while (pos < clean.Length)
            {
                pos = SkipWhitespace(clean, pos);
                if (pos >= clean.Length)
                {
                    break;
                }

                char c = clean[pos];
                if (c == '@')
                {
                    pos = SkipAtRule(clean, pos, lines, diagnostics);
                    continue;
                }

                if (c == '}')
                {
                    lines.Locate(pos, out int strayLine, out int strayColumn);
                    diagnostics.Add(RuntimeUiDiagnosticCodes.UssSyntax, RuntimeUiDiagnosticSeverity.Error,
                        RuntimeUiSource.Uss, strayLine, strayColumn, "unexpected '}'");
                    pos++;
                    continue;
                }

                int selectorStart = pos;
                int brace = FindTopLevel(clean, pos, '{', out char stoppedAt);
                if (brace < 0)
                {
                    lines.Locate(selectorStart, out int line, out int column);
                    string what = stoppedAt == '\0' ? "the end of the text" : "'" + stoppedAt + "'";
                    diagnostics.Add(RuntimeUiDiagnosticCodes.UssSyntax, RuntimeUiDiagnosticSeverity.Error,
                        RuntimeUiSource.Uss, line, column, "expected '{' after the selector, found " + what);
                    int skip = FindTopLevel(clean, pos, ';', out _);
                    int stray = clean.IndexOf('}', pos);
                    int next = skip >= 0 ? skip : stray;
                    if (stray >= 0 && (next < 0 || stray < next))
                    {
                        next = stray;
                    }

                    pos = next < 0 ? clean.Length : next + 1;
                    continue;
                }

                int close = FindBlockEnd(clean, brace, out bool nested);
                lines.Locate(selectorStart, out int ruleLine, out int ruleColumn);
                if (nested)
                {
                    diagnostics.Add(RuntimeUiDiagnosticCodes.UssSyntax, RuntimeUiDiagnosticSeverity.Error,
                        RuntimeUiSource.Uss, ruleLine, ruleColumn,
                        "nested rules are not supported in USS; the nested block was skipped");
                }

                if (close < 0)
                {
                    diagnostics.Add(RuntimeUiDiagnosticCodes.UssSyntax, RuntimeUiDiagnosticSeverity.Error,
                        RuntimeUiSource.Uss, ruleLine, ruleColumn, "the rule's '{' is never closed");
                    close = clean.Length;
                }

                if (rules.Count >= effective.MaxRules)
                {
                    if (!limitReported)
                    {
                        diagnostics.Add(RuntimeUiDiagnosticCodes.UssLimitExceeded, RuntimeUiDiagnosticSeverity.Error,
                            RuntimeUiSource.Uss, ruleLine, ruleColumn,
                            "more than " + effective.MaxRules + " rules; the rest of the sheet was skipped");
                        limitReported = true;
                    }

                    pos = close + 1;
                    continue;
                }

                UssComplexSelector[] selectors = ParseSelectorGroup(clean, selectorStart, brace, lines, diagnostics);
                List<UssDeclaration> declarations = new();
                ParseDeclarations(clean, brace + 1, Math.Min(close, clean.Length), lines, 0,
                    RuntimeUiSource.Uss, diagnostics, declarations, nested);
                if (selectors.Length > 0)
                {
                    rules.Add(new UssRule(selectors, declarations.ToArray(), ruleLine, rules.Count));
                }

                pos = close + 1;
            }

            return new UssStyleSheet(rules.ToArray(), diagnostics.ToArray());
        }

        /// <summary>
        /// Parses a declaration list such as a UXML <c>style</c> attribute (<c>width: 10px; color: red</c>).
        /// </summary>
        /// <param name="text">Declarations.</param>
        /// <param name="baseLine">Line of the text in its document; reports add the line offset inside it.</param>
        /// <param name="source">Source reported for the declarations.</param>
        /// <param name="diagnostics">Report sink.</param>
        public static UssDeclaration[] ParseInlineDeclarations(
            string text,
            int baseLine,
            RuntimeUiSource source,
            RuntimeUiDiagnosticList diagnostics)
        {
            string value = text ?? string.Empty;
            if (value.Trim().Length == 0)
            {
                return Array.Empty<UssDeclaration>();
            }

            RuntimeUiLineMap lines = new(value);
            string clean = StripComments(value, lines, diagnostics, source);
            List<UssDeclaration> declarations = new();
            ParseDeclarations(clean, 0, clean.Length, lines, Math.Max(0, baseLine - 1), source, diagnostics,
                declarations, false);
            return declarations.ToArray();
        }

        private static UssComplexSelector[] ParseSelectorGroup(string text, int start, int end, RuntimeUiLineMap lines,
            RuntimeUiDiagnosticList diagnostics)
        {
            List<UssComplexSelector> selectors = new();
            int itemStart = start;
            for (int i = start; i <= end; i++)
            {
                if (i < end && text[i] != ',')
                {
                    continue;
                }

                string item = text.Substring(itemStart, i - itemStart);
                int offset = itemStart + (item.Length - item.TrimStart().Length);
                lines.Locate(offset, out int line, out int column);
                if (item.Trim().Length == 0)
                {
                    diagnostics.Add(RuntimeUiDiagnosticCodes.UssSyntax, RuntimeUiDiagnosticSeverity.Error,
                        RuntimeUiSource.Uss, line, column, "an empty selector in a selector list");
                }
                else if (UssComplexSelector.TryParse(item, line, column, out UssComplexSelector selector,
                             out string error))
                {
                    selectors.Add(selector);
                }
                else
                {
                    diagnostics.Add(RuntimeUiDiagnosticCodes.UssUnsupportedSelector, RuntimeUiDiagnosticSeverity.Warning,
                        RuntimeUiSource.Uss, line, column,
                        "'" + item.Trim() + "' was dropped: " + error);
                }

                itemStart = i + 1;
            }

            return selectors.ToArray();
        }

        private static void ParseDeclarations(
            string text,
            int start,
            int end,
            RuntimeUiLineMap lines,
            int lineOffset,
            RuntimeUiSource source,
            RuntimeUiDiagnosticList diagnostics,
            List<UssDeclaration> output,
            bool skipNestedBlocks)
        {
            int segmentStart = start;
            int depth = 0;
            int braceDepth = 0;
            char quote = '\0';
            for (int i = start; i <= end; i++)
            {
                bool atEnd = i >= end;
                char c = atEnd ? ';' : text[i];
                if (!atEnd)
                {
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
                        continue;
                    }

                    if (c == '(')
                    {
                        depth++;
                        continue;
                    }

                    if (c == ')')
                    {
                        depth = depth > 0 ? depth - 1 : 0;
                        continue;
                    }

                    if (c == '{')
                    {
                        braceDepth++;
                        continue;
                    }

                    if (c == '}')
                    {
                        braceDepth = braceDepth > 0 ? braceDepth - 1 : 0;
                        if (braceDepth == 0 && skipNestedBlocks)
                        {
                            segmentStart = i + 1;
                        }

                        continue;
                    }
                }

                if (c != ';' || depth != 0 || braceDepth != 0)
                {
                    continue;
                }

                AddDeclaration(text, segmentStart, i, lines, lineOffset, source, diagnostics, output);
                segmentStart = i + 1;
            }
        }

        private static void AddDeclaration(string text, int start, int end, RuntimeUiLineMap lines, int lineOffset,
            RuntimeUiSource source, RuntimeUiDiagnosticList diagnostics, List<UssDeclaration> output)
        {
            string segment = text.Substring(start, end - start);
            if (segment.Trim().Length == 0)
            {
                return;
            }

            int offset = start + (segment.Length - segment.TrimStart().Length);
            lines.Locate(offset, out int line, out int column);
            line += lineOffset;
            int colon = segment.IndexOf(':');
            if (colon < 0)
            {
                diagnostics.Add(RuntimeUiDiagnosticCodes.UssSyntax, RuntimeUiDiagnosticSeverity.Error, source, line,
                    column, "expected 'property: value' but found '" + segment.Trim() + "'");
                return;
            }

            string name = segment.Substring(0, colon).Trim();
            string value = segment.Substring(colon + 1).Trim();
            int important = value.LastIndexOf("!important", StringComparison.OrdinalIgnoreCase);
            if (important >= 0 && value.Substring(important).Trim().Length == "!important".Length)
            {
                diagnostics.Add(RuntimeUiDiagnosticCodes.UssUnsupportedImportant, RuntimeUiDiagnosticSeverity.Warning,
                    source, line, column, "'!important' does not exist in USS; '" + name + "' is applied without it");
                value = value.Substring(0, important).Trim();
            }

            UssDeclaration declaration = UssDeclaration.Create(name, value, line, column, source, diagnostics);
            if (declaration != null)
            {
                output.Add(declaration);
            }
        }

        private static int SkipAtRule(string text, int pos, RuntimeUiLineMap lines, RuntimeUiDiagnosticList diagnostics)
        {
            lines.Locate(pos, out int line, out int column);
            int nameEnd = pos + 1;
            while (nameEnd < text.Length && (char.IsLetterOrDigit(text[nameEnd]) || text[nameEnd] == '-'))
            {
                nameEnd++;
            }

            string name = text.Substring(pos, nameEnd - pos);
            diagnostics.Add(RuntimeUiDiagnosticCodes.UssUnsupportedAtRule, RuntimeUiDiagnosticSeverity.Warning,
                RuntimeUiSource.Uss, line, column,
                "the at-rule '" + name + "' is not supported and was skipped" +
                (name == "@import" ? "; pass each style sheet's text to the host instead" : string.Empty));
            int semicolon = FindTopLevel(text, nameEnd, ';', out _);
            int brace = FindTopLevel(text, nameEnd, '{', out _);
            if (brace >= 0 && (semicolon < 0 || brace < semicolon))
            {
                int close = FindBlockEnd(text, brace, out _);
                return close < 0 ? text.Length : close + 1;
            }

            return semicolon < 0 ? text.Length : semicolon + 1;
        }

        private static int SkipWhitespace(string text, int pos)
        {
            while (pos < text.Length && char.IsWhiteSpace(text[pos]))
            {
                pos++;
            }

            return pos;
        }

        /// <summary>Finds <paramref name="target"/> outside strings and parentheses; stops at a brace or semicolon.</summary>
        private static int FindTopLevel(string text, int pos, char target, out char stoppedAt)
        {
            stoppedAt = '\0';
            int depth = 0;
            char quote = '\0';
            for (int i = pos; i < text.Length; i++)
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
                    continue;
                }

                if (c == '(')
                {
                    depth++;
                    continue;
                }

                if (c == ')')
                {
                    depth = depth > 0 ? depth - 1 : 0;
                    continue;
                }

                if (depth > 0)
                {
                    continue;
                }

                if (c == target)
                {
                    return i;
                }

                if (c == '{' || c == '}' || c == ';')
                {
                    stoppedAt = c;
                    return -1;
                }
            }

            return -1;
        }

        private static int FindBlockEnd(string text, int openBrace, out bool nested)
        {
            nested = false;
            int depth = 0;
            char quote = '\0';
            for (int i = openBrace; i < text.Length; i++)
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
                else if (c == '{')
                {
                    depth++;
                    if (depth > 1)
                    {
                        nested = true;
                    }
                }
                else if (c == '}')
                {
                    depth--;
                    if (depth == 0)
                    {
                        return i;
                    }
                }
            }

            return -1;
        }

        private static string StripComments(string text, RuntimeUiLineMap lines, RuntimeUiDiagnosticList diagnostics,
            RuntimeUiSource source)
        {
            int open = text.IndexOf("/*", StringComparison.Ordinal);
            if (open < 0)
            {
                return text;
            }

            char[] chars = text.ToCharArray();
            while (open >= 0)
            {
                int close = text.IndexOf("*/", open + 2, StringComparison.Ordinal);
                int end = close < 0 ? text.Length : close + 2;
                if (close < 0)
                {
                    lines.Locate(open, out int line, out int column);
                    diagnostics.Add(RuntimeUiDiagnosticCodes.UssSyntax, RuntimeUiDiagnosticSeverity.Error, source, line,
                        column, "a comment is never closed");
                }

                for (int i = open; i < end; i++)
                {
                    if (chars[i] != '\n')
                    {
                        chars[i] = ' ';
                    }
                }

                open = close < 0 ? -1 : text.IndexOf("/*", end, StringComparison.Ordinal);
            }

            return new string(chars);
        }
    }
}
