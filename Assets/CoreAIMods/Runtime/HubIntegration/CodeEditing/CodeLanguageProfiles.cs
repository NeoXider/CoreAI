using System;
using System.Collections.Generic;
using System.Text;
using CoreAI.CodeEditing;
using CoreAI.LuaAssets;

namespace CoreAI.Ai.Hub.CodeEditing
{
    internal interface ICodeLanguageContext
    {
        bool IsCodeAt(string source, int caret);
        bool TryFindBracketPair(string source, int caret, out int first, out int second);
    }

    /// <summary>Lua editing policy backed by the framework's Lua tokenizer.</summary>
    public sealed class LuaCodeLanguageProfile : ICodeLanguageProfile, ICodeLanguageContext
    {
        private string _maskSource;
        private bool[] _codeMask = Array.Empty<bool>();
        public string LanguageId => "lua";
        public string DisplayName => "Lua";
        public string LineCommentToken => "--";
        public IReadOnlyList<string> CompletionWords { get; } = new[]
        {
            "and", "break", "do", "else", "elseif", "end", "false", "for", "function", "if", "in", "local",
            "nil", "not", "or", "repeat", "return", "then", "true", "until", "while", "workspace", "game",
            "Instance", "Vector3", "Color3", "Enum", "hooks_on", "hooks_every", "report", "logic_define",
            "store_get", "store_set", "events_emit", "pairs", "ipairs", "print", "math", "string", "table"
        };

        public string[] HighlightLines(string[] lines)
        {
            string source = string.Join("\n", lines ?? Array.Empty<string>());
            if (source.Length > CodeEditorLimits.MaximumAnalysisCharacters) return CodeEditorMarkup.EscapeLines(lines);
            List<LuaToken> tokens = LuaTokenizer.Tokenize(source);
            List<string> rendered = new();
            StringBuilder current = new();
            int written = 0;
            foreach (LuaToken token in tokens)
            {
                string color = ColorFor(token.Kind);
                string text = token.GetText(source);
                string[] parts = text.Split('\n');
                for (int index = 0; index < parts.Length; index++)
                {
                    string escaped = CodeEditorMarkup.Escape(parts[index]);
                    if (color == null) current.Append(escaped);
                    else current.Append("<color=#").Append(color).Append('>').Append(escaped).Append("</color>");
                    if (index + 1 < parts.Length) { written += current.Length; rendered.Add(current.ToString()); current.Clear(); }
                    if (written + current.Length > CodeEditorLimits.MaximumHighlightCharacters)
                        return CodeEditorMarkup.EscapeLines(lines);
                }
            }
            rendered.Add(current.ToString());
            return rendered.ToArray();
        }

        private bool[] CodeMask(string source)
        {
            source ??= "";
            if (source.Length > CodeEditorLimits.MaximumAnalysisCharacters) return Array.Empty<bool>();
            if (string.Equals(source, _maskSource, StringComparison.Ordinal)) return _codeMask;
            _maskSource = source;
            _codeMask = new bool[source.Length];
            foreach (LuaToken token in LuaTokenizer.Tokenize(source))
            {
                bool code = token.Kind != LuaTokenKind.Comment && token.Kind != LuaTokenKind.String
                    && token.Kind != LuaTokenKind.LongString && token.Kind != LuaTokenKind.InterpolatedString;
                if (code) for (int index = token.Start; index < token.Start + token.Length; index++) _codeMask[index] = true;
            }
            return _codeMask;
        }
        bool ICodeLanguageContext.IsCodeAt(string source, int caret)
        {
            bool[] mask = CodeMask(source);
            return caret <= 0 || (caret <= mask.Length && mask[caret - 1]);
        }
        bool ICodeLanguageContext.TryFindBracketPair(string source, int caret, out int first, out int second)
        {
            bool[] mask = CodeMask(source);
            first = second = -1;
            for (int candidate = Math.Min(caret, mask.Length - 1); candidate >= Math.Max(0, caret - 1); candidate--)
            {
                if (!mask[candidate]) continue;
                char bracket = source[candidate];
                int opening = "([{)]}".IndexOf(bracket);
                if (opening < 0) continue;
                bool forward = opening < 3;
                char match = forward ? ")]}"[opening] : "([{"[opening - 3];
                int depth = 0;
                for (int index = candidate; index >= 0 && index < mask.Length; index += forward ? 1 : -1)
                {
                    if (!mask[index]) continue;
                    if (source[index] == bracket) depth++;
                    else if (source[index] == match && --depth == 0)
                    {
                        first = candidate;
                        second = index;
                        return true;
                    }
                }
            }
            return false;
        }

        private static string ColorFor(LuaTokenKind kind)
        {
            switch (kind)
            {
                case LuaTokenKind.Keyword: return "61AFEF";
                case LuaTokenKind.String:
                case LuaTokenKind.LongString:
                case LuaTokenKind.InterpolatedString: return "98C379";
                case LuaTokenKind.Comment: return "8A929E";
                case LuaTokenKind.Number: return "D19A66";
                case LuaTokenKind.Global: return "56B6C2";
                case LuaTokenKind.FunctionCall: return "E5C07B";
                default: return null;
            }
        }

        public CodeEnterPlan PlanEnter(string text, int selectionStart, int selectionEnd)
        {
            text ??= "";
            if (text.Length > CodeEditorLimits.MaximumAnalysisCharacters) return new CodeEnterPlan("", "", 0, 0);
            int caret = Math.Max(0, Math.Min(text.Length, Math.Min(selectionStart, selectionEnd)));
            int start = caret == 0 ? 0 : text.LastIndexOf('\n', caret - 1) + 1;
            string before = text.Substring(start, caret - start);
            int lead = 0;
            while (lead < before.Length && before[lead] == ' ') lead++;
            string indent = before.Substring(0, lead);
            // WHY: Full-source tokens preserve long-literal/comment context while still ignoring an
            // inline comment after a real block opener. An isolated physical line loses that distinction.
            List<LuaToken> tokens = LuaTokenizer.Tokenize(text);
            string last = "";
            bool function = false;
            foreach (LuaToken token in tokens)
            {
                if (token.Start >= caret || token.Start + token.Length <= start) continue;
                if (token.Kind == LuaTokenKind.Comment || token.Kind == LuaTokenKind.Whitespace) continue;
                if (token.Start < start || token.Start + token.Length > caret ||
                    token.Kind == LuaTokenKind.String || token.Kind == LuaTokenKind.LongString ||
                    token.Kind == LuaTokenKind.InterpolatedString)
                {
                    last = "";
                    continue;
                }
                string word = token.GetText(text);
                if (word == "function") function = true;
                last = word;
            }
            if (last == "then" || last == "do" || last == "repeat" || last == "else" || last == "{" || (function && last == ")"))
                indent += CodeEditorTextUtility.IndentUnit;
            return new CodeEnterPlan(indent, "", 0, 0);
        }
    }

    /// <summary>Python editing policy; execution remains an explicit unsupported capability.</summary>
    public sealed class PythonCodeLanguageProfile : ICodeLanguageProfile
    {
        private readonly PythonSyntaxHighlighter _highlighter = new();
        public string LanguageId => "python";
        public string DisplayName => "Python (execution unavailable)";
        public string LineCommentToken => "#";
        public IReadOnlyList<string> CompletionWords { get; } = new[]
        { "False", "None", "True", "and", "as", "assert", "break", "class", "continue", "def", "elif", "else", "except", "for", "from", "if", "import", "in", "is", "lambda", "not", "or", "pass", "print", "range", "return", "try", "while", "with", "yield" };
        public string[] HighlightLines(string[] lines)
        {
            int length = 0;
            foreach (string line in lines ?? Array.Empty<string>()) length += line?.Length ?? 0;
            if (length > CodeEditorLimits.MaximumAnalysisCharacters) return CodeEditorMarkup.EscapeLines(lines);
            string[] rendered = _highlighter.Highlight(lines ?? Array.Empty<string>());
            int output = 0;
            foreach (string line in rendered) output += line.Length;
            return output > CodeEditorLimits.MaximumHighlightCharacters ? CodeEditorMarkup.EscapeLines(lines) : rendered;
        }
        public CodeEnterPlan PlanEnter(string text, int start, int end) =>
            (text?.Length ?? 0) > CodeEditorLimits.MaximumAnalysisCharacters
                ? new CodeEnterPlan("", "", 0, 0)
                : CodeEditorTextUtility.PlanPythonEnter(text, start, end);
    }
}
