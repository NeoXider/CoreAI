using System;
using System.Collections.Generic;
using System.Text;

namespace CoreAI.Infrastructure.Luau
{
    /// <summary>
    /// Standalone Luau → Lua 5.2 downlevel preprocessor. Rewrites the Luau-only constructs a typical
    /// Roblox gameplay script uses — type annotations/declarations/casts, compound assignments,
    /// <c>continue</c>, backtick string interpolation, if-then-else expressions, floor division and
    /// Luau number literals — into equivalents the bundled Lua-CSharp (Lua 5.2) VM parses.
    /// darklua's rule set is the reference spec. Plain Lua passes through untouched
    /// (<see cref="DownlevelResult.Changed"/> = false); malformed input is returned verbatim with an
    /// Error diagnostic — this API never throws. Not yet wired into mod loading; callers opt in.
    /// </summary>
    public static class LuauDownleveler
    {
        public static DownlevelResult Process(string luauSource)
        {
            return Process(luauSource, "chunk");
        }

        public static DownlevelResult Process(string luauSource, string chunkName)
        {
            string source = luauSource ?? "";
            List<LuauToken> tokens;
            try
            {
                tokens = new LuauLexer(source).LexAll();
            }
            catch (LuauDownlevelException ex)
            {
                return Passthrough(source, ex, chunkName, null);
            }
            catch (Exception ex)
            {
                return InternalFailure(source, ex, chunkName);
            }

            if (!NeedsDownlevel(tokens))
            {
                return new DownlevelResult(source, false, null);
            }

            var ctx = new RewriteContext(source);
            try
            {
                new LuauRewriteParser(ctx, tokens).ParseChunk();
            }
            catch (LuauDownlevelException ex)
            {
                return Passthrough(source, ex, chunkName, ctx.Diagnostics);
            }
            catch (Exception ex)
            {
                return InternalFailure(source, ex, chunkName);
            }

            if (ctx.Edits.Count == 0)
            {
                return new DownlevelResult(source, false, ctx.Diagnostics);
            }

            if (!TryApplyEdits(source, ctx.Edits, out string output))
            {
                var conflict = new List<DownlevelDiagnostic>(ctx.Diagnostics)
                {
                    new DownlevelDiagnostic(DownlevelSeverity.Error,
                        $"internal downlevel edit conflict in '{chunkName}'; source left unchanged", 1, 1)
                };
                return new DownlevelResult(source, false, conflict);
            }

            var diagnostics = new List<DownlevelDiagnostic>(ctx.Diagnostics.Count + ctx.Notes.Count);
            diagnostics.AddRange(ctx.Diagnostics);
            for (int i = 0; i < ctx.Notes.Count; i++)
            {
                RewriteContext.RewriteNote note = ctx.Notes[i];
                string plural = note.Count == 1 ? "occurrence" : "occurrences";
                diagnostics.Add(new DownlevelDiagnostic(DownlevelSeverity.Info,
                    $"downleveled {note.Count} {plural} of {note.Kind}", note.FirstLine, note.FirstColumn));
            }

            return new DownlevelResult(output, true, diagnostics);
        }

        static DownlevelResult Passthrough(string source, LuauDownlevelException ex, string chunkName,
            List<DownlevelDiagnostic> collected)
        {
            var diagnostics = collected != null
                ? new List<DownlevelDiagnostic>(collected)
                : new List<DownlevelDiagnostic>();
            diagnostics.Add(new DownlevelDiagnostic(DownlevelSeverity.Error,
                $"{chunkName}: {ex.Message}; source left unchanged", ex.Line, ex.Column));
            return new DownlevelResult(source, false, diagnostics);
        }

        static DownlevelResult InternalFailure(string source, Exception ex, string chunkName)
        {
            var diagnostics = new List<DownlevelDiagnostic>
            {
                new DownlevelDiagnostic(DownlevelSeverity.Error,
                    $"{chunkName}: internal downlevel failure ({ex.GetType().Name}: {ex.Message}); source left unchanged",
                    1, 1)
            };
            return new DownlevelResult(source, false, diagnostics);
        }

        /// <summary>
        /// Cheap trigger scan so plain Lua skips the parser entirely. False positives only cost a
        /// parse (which produces zero edits); the checks are chosen so no Luau-only construct can
        /// slip past — an if-expression is recognized by the token before <c>if</c>, every other
        /// construct by its own token.
        /// </summary>
        static bool NeedsDownlevel(List<LuauToken> tokens)
        {
            for (int i = 0; i < tokens.Count; i++)
            {
                LuauToken t = tokens[i];
                switch (t.Kind)
                {
                    case LuauTokenKind.InterpString:
                        return true;
                    case LuauTokenKind.Number:
                        if (t.NumberRewrite != null)
                        {
                            return true;
                        }

                        break;
                    case LuauTokenKind.Name:
                        if (t.Text == "continue" || t.Text == "type" || t.Text == "export")
                        {
                            return true;
                        }

                        if (t.Text == "if" && i > 0 && IsIfExpressionPosition(tokens[i - 1]))
                        {
                            return true;
                        }

                        break;
                    case LuauTokenKind.Punct:
                        switch (t.Text)
                        {
                            case "+=":
                            case "-=":
                            case "*=":
                            case "/=":
                            case "//=":
                            case "%=":
                            case "^=":
                            case "..=":
                            case "//":
                            case "::":
                            case ":":
                            case "->":
                            case "?":
                            case "|":
                            case "&":
                            case "@":
                                return true;
                        }

                        break;
                }
            }

            return false;
        }

        static bool IsIfExpressionPosition(LuauToken prev)
        {
            if (prev.Kind == LuauTokenKind.Punct)
            {
                return prev.Text != ")" && prev.Text != "]" && prev.Text != "}" && prev.Text != ";";
            }

            if (prev.Kind == LuauTokenKind.Name)
            {
                switch (prev.Text)
                {
                    case "return":
                    case "and":
                    case "or":
                    case "not":
                    case "until":
                    case "in":
                        return true;
                }
            }

            return false;
        }

        static bool TryApplyEdits(string source, List<SourceEdit> edits, out string output)
        {
            var sorted = new List<SourceEdit>(edits);
            sorted.Sort(LuauRewriteParser.CompareEdits);
            var sb = new StringBuilder(source.Length + 64);
            int pos = 0;
            for (int i = 0; i < sorted.Count; i++)
            {
                SourceEdit e = sorted[i];
                if (e.Start < pos)
                {
                    output = null;
                    return false;
                }

                sb.Append(source, pos, e.Start - pos);
                sb.Append(e.Text);
                pos = e.End;
            }

            sb.Append(source, pos, source.Length - pos);
            output = sb.ToString();
            return true;
        }
    }
}
