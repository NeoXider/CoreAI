using System;
using System.Collections.Generic;
using System.Text;

namespace CoreAI.Infrastructure.Luau
{
    /// <summary>
    /// Standalone Luau → Lua 5.2 downlevel preprocessor. Rewrites the Luau-only constructs a typical
    /// Roblox gameplay script uses — type annotations/declarations/casts, generic type-parameter
    /// lists, compound assignments, <c>continue</c>, backtick string interpolation, if-then-else
    /// expressions, floor division, Luau number literals and the <c>\u{XXXX}</c>/<c>\z</c> string
    /// escapes — into equivalents the bundled Lua-CSharp (Lua 5.2) VM parses.
    /// darklua's rule set is the reference spec. Plain Lua passes through untouched
    /// (<see cref="DownlevelResult.Changed"/> = false); malformed input is returned verbatim with an
    /// Error diagnostic — this API never throws. Wired into every runtime Lua compile path via
    /// <c>LuauSourceGate</c> (mod load/reload, <c>execute_lua</c>, the AI envelope).
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

            RewriteContext ctx = new(source);
            ctx.ChunkInsertionPosition = tokens.Count > 0 ? tokens[0].Start : 0;
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
                List<DownlevelDiagnostic> conflict = new(ctx.Diagnostics)
                {
                    new DownlevelDiagnostic(DownlevelSeverity.Error,
                        $"internal downlevel edit conflict in '{chunkName}'; source left unchanged", 1, 1)
                };
                return new DownlevelResult(source, false, conflict);
            }

            List<DownlevelDiagnostic> diagnostics = new(ctx.Diagnostics.Count + ctx.Notes.Count);
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

        private static DownlevelResult Passthrough(string source, LuauDownlevelException ex, string chunkName,
            List<DownlevelDiagnostic> collected)
        {
            List<DownlevelDiagnostic> diagnostics = collected != null
                ? new List<DownlevelDiagnostic>(collected)
                : new List<DownlevelDiagnostic>();
            diagnostics.Add(new DownlevelDiagnostic(DownlevelSeverity.Error,
                $"{chunkName}: {ex.Message}; source left unchanged", ex.Line, ex.Column));
            return new DownlevelResult(source, false, diagnostics);
        }

        private static DownlevelResult InternalFailure(string source, Exception ex, string chunkName)
        {
            List<DownlevelDiagnostic> diagnostics = new()
            {
                new DownlevelDiagnostic(DownlevelSeverity.Error,
                    $"{chunkName}: internal downlevel failure ({ex.GetType().Name}: {ex.Message}); source left unchanged",
                    1, 1)
            };
            return new DownlevelResult(source, false, diagnostics);
        }

        /// <summary>
        /// Cheap trigger scan so plain Lua skips the parser entirely. False positives only cost a
        /// parse (which produces zero edits and re-emits the source byte-identically); the checks are
        /// chosen so no Luau-only construct can slip past — an if-expression is recognized by the token
        /// before <c>if</c>, a generic type-parameter list by an identifier directly followed by
        /// <c>&lt;</c> that closes with <c>&gt;</c> before the next <c>(</c>, string escapes and Luau
        /// number literals by a token rewrite the lexer already computed, every other construct by its
        /// own token.
        /// </summary>
        private static bool NeedsDownlevel(List<LuauToken> tokens)
        {
            for (int i = 0; i < tokens.Count; i++)
            {
                LuauToken t = tokens[i];
                switch (t.Kind)
                {
                    case LuauTokenKind.InterpString:
                        return true;
                    case LuauTokenKind.String:
                        if (t.StringRewrite != null)
                        {
                            return true;
                        }

                        break;
                    case LuauTokenKind.Number:
                        if (t.NumberRewrite != null)
                        {
                            return true;
                        }

                        break;
                    case LuauTokenKind.Name:
                        if (t.Text == "continue" || t.Text == "type" || t.Text == "export"
                            || t.Text == "for")
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
                            case "<":
                                if (i > 0 && tokens[i - 1].Kind == LuauTokenKind.Name &&
                                    LooksLikeGenericList(tokens, i))
                                {
                                    return true;
                                }

                                break;
                        }

                        break;
                }
            }

            return false;
        }

        /// <summary>
        /// Heuristic for a generic type-parameter list opening at <paramref name="open"/> (a
        /// <c>&lt;</c> directly after an identifier): true when a matching <c>&gt;</c> appears before
        /// the next call <c>(</c> or a token that cannot occur inside a type-parameter list. Only used
        /// to arm the parser — a false positive triggers a no-op parse (byte-identical passthrough), so
        /// this errs toward triggering; an unannotated generic function (<c>function f&lt;T&gt;()</c>)
        /// must never slip past. Plain comparisons (<c>a &lt; b then</c>) stop at the keyword and do
        /// not trigger.
        /// </summary>
        private static bool LooksLikeGenericList(List<LuauToken> tokens, int open)
        {
            for (int i = open + 1; i < tokens.Count; i++)
            {
                LuauToken t = tokens[i];
                if (t.Kind == LuauTokenKind.Punct)
                {
                    switch (t.Text)
                    {
                        case ">":
                        case ">=":
                            return true;
                        case ",":
                        case ".":
                        case "<":
                        case "...":
                            continue;
                        default:
                            return false;
                    }
                }

                if (t.Kind == LuauTokenKind.Name && !IsTypeListStopWord(t.Text))
                {
                    continue;
                }

                return false;
            }

            return false;
        }

        private static bool IsTypeListStopWord(string text)
        {
            switch (text)
            {
                // WHY: keywords that end a statement/expression cannot appear inside a type-parameter
                // list, so hitting one means the '<' was a comparison operator, not a generic opener.
                case "then":
                case "do":
                case "end":
                case "return":
                case "if":
                case "else":
                case "elseif":
                case "while":
                case "for":
                case "repeat":
                case "until":
                case "local":
                case "function":
                case "in":
                case "and":
                case "or":
                case "not":
                    return true;
                default:
                    return false;
            }
        }

        private static bool IsIfExpressionPosition(LuauToken prev)
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

        private static bool TryApplyEdits(string source, List<SourceEdit> edits, out string output)
        {
            List<SourceEdit> sorted = new(edits);
            sorted.Sort(LuauRewriteParser.CompareEdits);
            StringBuilder sb = new(source.Length + 64);
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
