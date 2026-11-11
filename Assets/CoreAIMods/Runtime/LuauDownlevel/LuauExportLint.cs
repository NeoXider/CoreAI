using System;
using System.Collections.Generic;

namespace CoreAI.Infrastructure.Luau
{
    /// <summary>How bad an export lint finding is.</summary>
    public enum LuauExportSeverity
    {
        /// <summary>Roblox refuses the construct or does not have it: the script fails there.</summary>
        Error,

        /// <summary>Roblox runs the script, but this part means something else there or nothing at all.</summary>
        Warning
    }

    /// <summary>One construct of a script that Roblox does not accept or does not have, at the author's position.</summary>
    public readonly struct LuauExportFinding
    {
        public LuauExportFinding(string code, LuauExportSeverity severity, int line, int column, string subject,
            string message)
        {
            Code = code;
            Severity = severity;
            Line = line;
            Column = column;
            Subject = subject;
            Message = message;
        }

        /// <summary>The stable code (<see cref="LuauExportLint"/> lists them), for tools to match on.</summary>
        public string Code { get; }

        public LuauExportSeverity Severity { get; }

        /// <summary>1-based line of the construct in the author's source.</summary>
        public int Line { get; }

        /// <summary>1-based column of the construct in the author's source.</summary>
        public int Column { get; }

        /// <summary>The name the finding is about (<c>goto</c>, <c>hooks_every</c>, <c>AIService</c>, ...).</summary>
        public string Subject { get; }

        public string Message { get; }

        /// <inheritdoc />
        public override string ToString()
        {
            return Code + " " + Line + ":" + Column + " " + Message;
        }
    }

    /// <summary>
    /// The export lint (MVP13, RT6 and RT7): reads a script as written and reports, with line and column, every
    /// construct Roblox does not accept or does not have. It parses with the downlevel's own parser, so it reads
    /// exactly the Luau CoreAI runs, and resolves names by scope, so a local or a global the script defines itself
    /// is never mistaken for a CoreAI global. Text inside strings and comments is never code. It changes nothing
    /// and never throws. Codes:
    /// <list type="bullet">
    /// <item><c>LX001</c> the script does not parse.</item>
    /// <item><c>LX101</c> <c>goto</c>, <c>LX102</c> a <c>::label::</c>, <c>LX103</c> <c>_ENV</c>, <c>LX104</c> a
    /// hexadecimal float literal: Lua 5.2 syntax Luau does not have.</item>
    /// <item><c>LX201</c> a CoreAI global (<c>hooks_every</c>, <c>report</c>, <c>coreai_world_spawn</c>,
    /// <c>unity_find</c>, ...); <c>LX202</c> (warning) a global neither Roblox nor the script defines.</item>
    /// <item><c>LX203</c> a member of a Luau library that Luau does not have (<c>task._waitForChildBridge</c>, or a
    /// field a script adds to a library, which Roblox's read-only libraries refuse).</item>
    /// <item><c>LX301</c> a CoreAI service (<c>GetService("AIService")</c>, <c>GetService("UxmlService")</c>);
    /// <c>LX302</c> (warning) a service name Roblox does not have; <c>LX303</c> a member only Roblox plugins may
    /// call, which CoreAI's elevated tier may (DEV-14).</item>
    /// <item><c>LX401</c> (warning) a key of the CoreAI mod header (<c>-- @coreai capabilities: WorldEdit</c>),
    /// which Roblox reads as a comment.</item>
    /// </list>
    /// </summary>
    public static class LuauExportLint
    {
        private static readonly char[] HexFloatMarks = { '.', 'p', 'P' };

        /// <summary>Checks <paramref name="source"/> against <see cref="LuauExportLintSurface.Default"/>.</summary>
        public static IReadOnlyList<LuauExportFinding> Check(string source)
        {
            return Check(source, LuauExportLintSurface.Default);
        }

        /// <summary>Checks <paramref name="source"/> against <paramref name="surface"/>; findings in source order.</summary>
        public static IReadOnlyList<LuauExportFinding> Check(string source, LuauExportLintSurface surface)
        {
            string text = source ?? "";
            LuauExportLintSurface lintSurface = surface ?? LuauExportLintSurface.Default;
            List<LuauExportFinding> findings = new();
            CheckHeader(text, findings);
            try
            {
                DownlevelBudget budget = new(text, new LuauDownlevelOptions());
                List<LuauToken> tokens = new LuauLexer(text, budget).LexAll();
                CheckNumbers(tokens, findings);
                ScopeCollector collector = new();
                RewriteContext context = new(text, budget);
                new LuauRewriteParser(context, tokens, collector).ParseChunk();
                Classify(collector, lintSurface, findings);
            }
            catch (LuauDownlevelException ex)
            {
                findings.Add(new LuauExportFinding("LX001", LuauExportSeverity.Error, ex.Line, ex.Column, "syntax",
                    "the script does not parse: " + ex.Message));
            }
            catch (Exception ex)
            {
                findings.Add(new LuauExportFinding("LX001", LuauExportSeverity.Error, 1, 1, "syntax",
                    "the script could not be read: " + ex.Message));
            }

            findings.Sort(CompareFindings);
            return findings;
        }

        private static int CompareFindings(LuauExportFinding a, LuauExportFinding b)
        {
            int byLine = a.Line.CompareTo(b.Line);
            if (byLine != 0)
            {
                return byLine;
            }

            int byColumn = a.Column.CompareTo(b.Column);
            return byColumn != 0 ? byColumn : string.CompareOrdinal(a.Code, b.Code);
        }

        private static void CheckNumbers(List<LuauToken> tokens, List<LuauExportFinding> findings)
        {
            for (int i = 0; i < tokens.Count; i++)
            {
                LuauToken token = tokens[i];
                if (token.Kind == LuauTokenKind.InterpString && token.InterpParts != null)
                {
                    foreach (LuauInterpPart part in token.InterpParts)
                    {
                        if (part.IsExpr && part.Tokens != null)
                        {
                            CheckNumbers(part.Tokens, findings);
                        }
                    }

                    continue;
                }

                if (token.Kind != LuauTokenKind.Number || token.Text.Length < 2 || token.Text[0] != '0'
                    || (token.Text[1] != 'x' && token.Text[1] != 'X'))
                {
                    continue;
                }

                if (token.Text.IndexOfAny(HexFloatMarks) >= 0)
                {
                    findings.Add(new LuauExportFinding("LX104", LuauExportSeverity.Error, token.Line, token.Column,
                        token.Text, "hexadecimal float literal " + token.Text
                                    + " is Lua 5.2 syntax; Luau reads only integer hexadecimal literals"));
                }
            }
        }

        private static void Classify(ScopeCollector collector, LuauExportLintSurface surface,
            List<LuauExportFinding> findings)
        {
            foreach (LuauToken keyword in collector.Gotos)
            {
                findings.Add(new LuauExportFinding("LX101", LuauExportSeverity.Error, keyword.Line, keyword.Column,
                    "goto", "goto is Lua 5.2 syntax; Luau has no goto (use a loop with break or continue)"));
            }

            foreach (LuauToken open in collector.Labels)
            {
                findings.Add(new LuauExportFinding("LX102", LuauExportSeverity.Error, open.Line, open.Column,
                    "label", "a ::label:: is Lua 5.2 syntax; Luau has no labels"));
            }

            foreach (LuauToken name in collector.EnvironmentNames)
            {
                findings.Add(new LuauExportFinding("LX103", LuauExportSeverity.Error, name.Line, name.Column, "_ENV",
                    "_ENV is Lua 5.2's environment; in Luau it is an ordinary name (use getfenv or pass a table)"));
            }

            foreach (LuauToken name in collector.FreeNames)
            {
                if (name.Text == "_ENV" || collector.AssignedGlobals.Contains(name.Text))
                {
                    continue;
                }

                if (surface.IsCoreAiGlobal(name.Text))
                {
                    findings.Add(new LuauExportFinding("LX201", LuauExportSeverity.Error, name.Line, name.Column,
                        name.Text, name.Text + " is a CoreAI global; Roblox has no such global"));
                }
                else if (!LuauExportLintSurface.IsRobloxGlobal(name.Text))
                {
                    findings.Add(new LuauExportFinding("LX202", LuauExportSeverity.Warning, name.Line, name.Column,
                        name.Text, name.Text + " is not a Roblox global and the script does not define it; it is "
                                   + "nil in Roblox"));
                }
            }

            foreach (MemberUse use in collector.Members)
            {
                if (collector.AssignedGlobals.Contains(use.Base.Text))
                {
                    continue;
                }

                if (LuauExportLintSurface.TryGetLibrary(use.Base.Text, out IReadOnlyCollection<string> members)
                    && !Contains(members, use.Member.Text))
                {
                    findings.Add(new LuauExportFinding("LX203", LuauExportSeverity.Error, use.Member.Line,
                        use.Member.Column, use.Base.Text + "." + use.Member.Text,
                        use.Base.Text + "." + use.Member.Text + " is not part of Luau's " + use.Base.Text
                        + " library (Roblox's libraries are read-only, so a script cannot add it either)"));
                }

                if ((use.Base.Text == "game" || use.Base.Text == "workspace") && !use.Method
                                                                                && surface.IsCoreAiService(use.Member.Text))
                {
                    AddService(findings, use.Member, use.Member.Text);
                }
            }

            foreach (MethodCall call in collector.Methods)
            {
                if ((call.Method.Text == "GetService" || call.Method.Text == "FindService") && call.Argument != null)
                {
                    string service = StringValue(call.Argument.Text);
                    if (service != null && surface.IsCoreAiService(service))
                    {
                        AddService(findings, call.Argument, service);
                    }
                    else if (service != null && !LuauExportLintSurface.IsRobloxService(service))
                    {
                        findings.Add(new LuauExportFinding("LX302", LuauExportSeverity.Warning, call.Argument.Line,
                            call.Argument.Column, service, service + " is not a Roblox service; GetService raises "
                                                           + "\"" + service + " is not a valid Service name\" there"));
                    }
                }

                if (surface.IsElevatedMethod(call.Method.Text, out string member))
                {
                    findings.Add(new LuauExportFinding("LX303", LuauExportSeverity.Error, call.Method.Line,
                        call.Method.Column, member, member + " is PluginSecurity in Roblox: a game script cannot call "
                                                    + "it there; CoreAI lets its elevated tier call it (DEV-14)"));
                }
            }
        }

        private static void AddService(List<LuauExportFinding> findings, LuauToken at, string service)
        {
            findings.Add(new LuauExportFinding("LX301", LuauExportSeverity.Error, at.Line, at.Column, service,
                service + " is a CoreAI service; Roblox has no such service, so nothing obtained from it exists there"));
        }

        private static bool Contains(IReadOnlyCollection<string> members, string name)
        {
            foreach (string member in members)
            {
                if (member == name)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>The value of a plain string literal (quotes or long brackets, no escapes inside); null otherwise.</summary>
        private static string StringValue(string literal)
        {
            if (literal.Length >= 2 && (literal[0] == '"' || literal[0] == '\'') && literal[literal.Length - 1] == literal[0])
            {
                string inner = literal.Substring(1, literal.Length - 2);
                return inner.IndexOf('\\') >= 0 ? null : inner;
            }

            if (literal.StartsWith("[", StringComparison.Ordinal))
            {
                int open = literal.IndexOf('[', 1);
                int close = literal.IndexOf(']');
                if (open > 0 && close > open)
                {
                    return literal.Substring(open + 1, close - open - 1);
                }
            }

            return null;
        }

        // ---- the mod header ------------------------------------------------------------------------------

        /// <summary>
        /// Reports each key of a CoreAI mod header, read by the same rules as <c>LuaModHeader</c>: a block
        /// <c>--[[@coreai ... ]]</c> at the very start, or the leading <c>-- @coreai key: value</c> lines.
        /// </summary>
        private static void CheckHeader(string source, List<LuauExportFinding> findings)
        {
            string marker = LuauExportLintSurface.HeaderMarker;
            if (source.StartsWith("--[[" + marker, StringComparison.Ordinal))
            {
                int end = source.IndexOf("]]", 4 + marker.Length, StringComparison.Ordinal);
                if (end < 0)
                {
                    return;
                }

                int lineStart = 4 + marker.Length;
                while (lineStart < end)
                {
                    int lineEnd = source.IndexOf('\n', lineStart);
                    if (lineEnd < 0 || lineEnd > end)
                    {
                        lineEnd = end;
                    }

                    ReportHeaderEntry(source, lineStart, lineEnd, findings);
                    lineStart = lineEnd + 1;
                }

                return;
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
                }

                position = lineEnd + 1;
                int contentStart = SkipSpaces(source, lineStart, lineEnd);
                if (contentStart >= lineEnd || (contentStart == lineEnd - 1 && source[contentStart] == '\r'))
                {
                    if (parsed)
                    {
                        return;
                    }

                    continue;
                }

                if (string.CompareOrdinal(source, contentStart, "--", 0, 2) != 0)
                {
                    return;
                }

                int markerStart = SkipSpaces(source, contentStart + 2, lineEnd);
                if (string.CompareOrdinal(source, markerStart, marker, 0, marker.Length) != 0)
                {
                    return;
                }

                ReportHeaderEntry(source, markerStart + marker.Length, lineEnd, findings);
                parsed = true;
            }
        }

        private static void ReportHeaderEntry(string source, int start, int end, List<LuauExportFinding> findings)
        {
            int keyStart = SkipSpaces(source, start, end);
            int colon = source.IndexOf(':', keyStart, Math.Max(0, end - keyStart));
            if (colon < 0)
            {
                return;
            }

            string key = source.Substring(keyStart, colon - keyStart).Trim();
            if (key.Length == 0 || key.StartsWith(LuauExportLintSurface.HeaderMarker, StringComparison.Ordinal))
            {
                return;
            }

            (int line, int column) = Position(source, keyStart);
            findings.Add(new LuauExportFinding("LX401", LuauExportSeverity.Warning, line, column, key,
                "the CoreAI mod header key '" + key + "' is a comment to Roblox, which applies none of it"));
        }

        private static int SkipSpaces(string source, int start, int end)
        {
            while (start < end && (source[start] == ' ' || source[start] == '\t'))
            {
                start++;
            }

            return start;
        }

        private static (int Line, int Column) Position(string source, int offset)
        {
            int line = 1;
            int lineStart = 0;
            for (int i = 0; i < offset && i < source.Length; i++)
            {
                if (source[i] == '\n')
                {
                    line++;
                    lineStart = i + 1;
                }
            }

            return (line, offset - lineStart + 1);
        }

        // ---- scope collection -----------------------------------------------------------------------------

        private readonly struct MemberUse
        {
            public MemberUse(LuauToken baseName, LuauToken member, bool method)
            {
                Base = baseName;
                Member = member;
                Method = method;
            }

            public LuauToken Base { get; }

            public LuauToken Member { get; }

            public bool Method { get; }
        }

        private readonly struct MethodCall
        {
            public MethodCall(LuauToken method, LuauToken argument)
            {
                Method = method;
                Argument = argument;
            }

            public LuauToken Method { get; }

            public LuauToken Argument { get; }
        }

        /// <summary>Resolves names by Lua's scoping as the parser walks the chunk.</summary>
        private sealed class ScopeCollector : ILuauSyntaxObserver
        {
            private readonly List<HashSet<string>> _scopes = new() { new HashSet<string>(StringComparer.Ordinal) };

            public readonly List<LuauToken> FreeNames = new();
            public readonly HashSet<string> AssignedGlobals = new(StringComparer.Ordinal);
            public readonly List<MemberUse> Members = new();
            public readonly List<MethodCall> Methods = new();
            public readonly List<LuauToken> Gotos = new();
            public readonly List<LuauToken> Labels = new();
            public readonly List<LuauToken> EnvironmentNames = new();

            public void OpenScope()
            {
                _scopes.Add(new HashSet<string>(StringComparer.Ordinal));
            }

            public void CloseScope()
            {
                if (_scopes.Count > 1)
                {
                    _scopes.RemoveAt(_scopes.Count - 1);
                }
            }

            public void Declare(LuauToken name)
            {
                _scopes[_scopes.Count - 1].Add(name.Text);
                if (name.Text == "_ENV")
                {
                    EnvironmentNames.Add(name);
                }
            }

            public void DeclareImplicit(string name)
            {
                _scopes[_scopes.Count - 1].Add(name);
            }

            public void UseName(LuauToken name)
            {
                if (name.Text == "_ENV")
                {
                    EnvironmentNames.Add(name);
                }

                if (!IsLocal(name.Text))
                {
                    FreeNames.Add(name);
                }
            }

            public void AssignName(LuauToken name)
            {
                if (!IsLocal(name.Text))
                {
                    AssignedGlobals.Add(name.Text);
                }
            }

            public void UseMember(LuauToken baseName, LuauToken member, bool method)
            {
                if (!IsLocal(baseName.Text))
                {
                    Members.Add(new MemberUse(baseName, member, method));
                }
            }

            public void CallMethod(LuauToken method, LuauToken firstStringArgument)
            {
                Methods.Add(new MethodCall(method, firstStringArgument));
            }

            public void Goto(LuauToken keyword, LuauToken label)
            {
                Gotos.Add(keyword);
            }

            public void Label(LuauToken open, LuauToken label)
            {
                Labels.Add(open);
            }

            private bool IsLocal(string name)
            {
                for (int i = _scopes.Count - 1; i >= 0; i--)
                {
                    if (_scopes[i].Contains(name))
                    {
                        return true;
                    }
                }

                return false;
            }
        }
    }
}
