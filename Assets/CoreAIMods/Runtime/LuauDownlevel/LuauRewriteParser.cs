using System.Collections.Generic;
using System.Text;

namespace CoreAI.Infrastructure.Luau
{
    /// <summary>One span replacement against the original source; an insertion when Start == End.</summary>
    internal struct SourceEdit
    {
        public int Start;
        public int End;
        public int Seq;
        public string Text;
    }

    /// <summary>
    /// State shared between the root parser and interpolation sub-parsers: the source text, the
    /// accumulated edit list, diagnostics, aggregated rewrite notes, and the temp-name counter.
    /// </summary>
    internal sealed class RewriteContext
    {
        public RewriteContext(string source)
        {
            Source = source;
        }

        public readonly string Source;
        public readonly List<SourceEdit> Edits = new List<SourceEdit>();
        public readonly List<DownlevelDiagnostic> Diagnostics = new List<DownlevelDiagnostic>();
        public readonly List<RewriteNote> Notes = new List<RewriteNote>();
        public int TempCounter;

        public sealed class RewriteNote
        {
            public string Kind;
            public int Count;
            public int FirstLine;
            public int FirstColumn;
        }

        public void Note(string kind, LuauToken at)
        {
            for (int i = 0; i < Notes.Count; i++)
            {
                if (Notes[i].Kind == kind)
                {
                    Notes[i].Count++;
                    return;
                }
            }

            Notes.Add(new RewriteNote { Kind = kind, Count = 1, FirstLine = at.Line, FirstColumn = at.Column });
        }
    }

    /// <summary>
    /// Recursive-descent parser over the full Luau statement/expression grammar that records source
    /// edits instead of building a tree: type annotations/declarations are deleted, compound
    /// assignments, <c>continue</c>, string interpolation, if-expressions, floor division and
    /// Luau-only number literals are rewritten to Lua 5.2 equivalents. Deletions re-emit the newlines
    /// they cover and insertions never add lines, so line numbers are preserved (the one exception —
    /// a multi-line <c>repeat</c>-<c>until</c> condition duplicated for <c>continue</c> — emits a
    /// Warning diagnostic).
    /// </summary>
    internal sealed class LuauRewriteParser
    {
        static readonly HashSet<string> Keywords = new HashSet<string>
        {
            "and", "break", "do", "else", "elseif", "end", "false", "for", "function", "if", "in",
            "local", "nil", "not", "or", "repeat", "return", "then", "true", "until", "while"
        };

        static readonly HashSet<string> CompoundOps = new HashSet<string>
        {
            "+=", "-=", "*=", "/=", "//=", "%=", "^=", "..="
        };

        readonly RewriteContext _ctx;
        readonly List<LuauToken> _toks;
        int _i;
        int _funcDepth;
        int _pendingEqPos = -1;
        readonly List<LoopFrame> _loops = new List<LoopFrame>();
        readonly List<int> _ifExprFuncDepths = new List<int>();
        readonly List<bool> _ifExprUnsupported = new List<bool>();

        sealed class LoopFrame
        {
            public bool Barrier;
            public bool IsRepeat;
            public readonly List<LuauToken> Continues = new List<LuauToken>();
            public readonly List<LuauToken> Breaks = new List<LuauToken>();
        }

        enum SuffixKind
        {
            Name,
            Dot,
            Index,
            Call,
            Paren
        }

        struct SuffixedInfo
        {
            public int Start;
            public int End;
            public SuffixKind LastKind;
            public bool HasCall;
            public LuauToken DotTok;
            public LuauToken DotName;
            public LuauToken OpenBracket;
            public LuauToken CloseBracket;
        }

        public LuauRewriteParser(RewriteContext ctx, List<LuauToken> tokens)
        {
            _ctx = ctx;
            _toks = tokens;
        }

        public void ParseChunk()
        {
            ParseBlock();
            if (Cur.Kind != LuauTokenKind.Eof)
            {
                Throw($"unexpected '{Cur.Text}'");
            }
        }

        void ParseInterpolationExpression()
        {
            ParseExpr();
            if (Cur.Kind != LuauTokenKind.Eof)
            {
                Throw($"unexpected '{Cur.Text}' inside string interpolation");
            }
        }

        LuauToken Cur => _toks[_i];

        LuauToken Peek(int offset)
        {
            int i = _i + offset;
            return i < _toks.Count ? _toks[i] : _toks[_toks.Count - 1];
        }

        void Advance()
        {
            if (_i < _toks.Count - 1)
            {
                _i++;
            }
        }

        static bool Is(LuauToken t, LuauTokenKind kind, string text)
        {
            return t.Kind == kind && t.Text == text;
        }

        static bool IsPunct(LuauToken t, string text)
        {
            return t.Kind == LuauTokenKind.Punct && t.Text == text;
        }

        static bool IsName(LuauToken t, string text)
        {
            return t.Kind == LuauTokenKind.Name && t.Text == text;
        }

        void Throw(string message)
        {
            throw new LuauDownlevelException(message, Cur.Line, Cur.Column);
        }

        LuauToken ExpectPunct(string text)
        {
            if (!IsPunct(Cur, text))
            {
                Throw($"expected '{text}' near '{Cur.Text}'");
            }

            LuauToken t = Cur;
            Advance();
            return t;
        }

        LuauToken ExpectKeyword(string text)
        {
            if (!IsName(Cur, text))
            {
                Throw($"expected '{text}' near '{Cur.Text}'");
            }

            LuauToken t = Cur;
            Advance();
            return t;
        }

        LuauToken ExpectIdentifier()
        {
            if (Cur.Kind != LuauTokenKind.Name || Keywords.Contains(Cur.Text))
            {
                Throw($"expected identifier near '{Cur.Text}'");
            }

            LuauToken t = Cur;
            Advance();
            return t;
        }

        void ConsumeEquals()
        {
            if (_pendingEqPos >= 0)
            {
                _pendingEqPos = -1;
                return;
            }

            ExpectPunct("=");
        }

        bool AtEquals()
        {
            return _pendingEqPos >= 0 || IsPunct(Cur, "=");
        }

        void AddEdit(int start, int end, string text)
        {
            _ctx.Edits.Add(new SourceEdit { Start = start, End = end, Seq = _ctx.Edits.Count, Text = text });
        }

        void ReplaceToken(LuauToken t, string text)
        {
            AddEdit(t.Start, t.End, text);
        }

        void Insert(int pos, string text)
        {
            AddEdit(pos, pos, text);
        }

        /// <summary>Deletes a span while re-emitting its newlines so following lines keep their numbers.</summary>
        void DeleteKeepNewlines(int start, int end)
        {
            int newlines = 0;
            for (int i = start; i < end; i++)
            {
                if (_ctx.Source[i] == '\n')
                {
                    newlines++;
                }
            }

            AddEdit(start, end, newlines == 0 ? "" : new string('\n', newlines));
        }

        bool SpanHasChar(int start, int end, char c)
        {
            for (int i = start; i < end; i++)
            {
                if (_ctx.Source[i] == c)
                {
                    return true;
                }
            }

            return false;
        }

        // WHY: temp names use the '__luau_t' prefix on the assumption that user code never declares an
        // identifier with it; the counter is shared across the whole chunk so every generated temp is
        // unique. A colliding user identifier would shadow a temp and is treated as out of contract.
        string NextTemp()
        {
            return "__luau_t" + _ctx.TempCounter++;
        }

        void EmitStringRewrite(LuauToken t)
        {
            if (t.StringRewrite != null)
            {
                ReplaceToken(t, t.StringRewrite);
                _ctx.Note("string escape", t);
            }
        }

        void Warn(string message, LuauToken at)
        {
            _ctx.Diagnostics.Add(new DownlevelDiagnostic(DownlevelSeverity.Warning, message, at.Line, at.Column));
        }

        /// <summary>
        /// Renders a source span with all edits recorded so far applied — used when a rewrite has to
        /// duplicate an already-rewritten fragment (compound-assign targets, repeat-until conditions).
        /// Insertions sitting exactly at <paramref name="start"/> are excluded: they belong to the
        /// construct preceding the span.
        /// </summary>
        string GetRewrittenText(int start, int end)
        {
            var slice = new List<SourceEdit>();
            for (int i = 0; i < _ctx.Edits.Count; i++)
            {
                SourceEdit e = _ctx.Edits[i];
                if (e.Start >= start && e.End <= end && !(e.Start == start && e.End == start))
                {
                    slice.Add(e);
                }
            }

            slice.Sort(CompareEdits);
            var sb = new StringBuilder();
            int pos = start;
            for (int i = 0; i < slice.Count; i++)
            {
                SourceEdit e = slice[i];
                if (e.Start < pos)
                {
                    continue;
                }

                sb.Append(_ctx.Source, pos, e.Start - pos);
                sb.Append(e.Text);
                pos = e.End;
            }

            sb.Append(_ctx.Source, pos, end - pos);
            return sb.ToString();
        }

        internal static int CompareEdits(SourceEdit a, SourceEdit b)
        {
            if (a.Start != b.Start)
            {
                return a.Start.CompareTo(b.Start);
            }

            // WHY: an insertion at position P belongs to the construct ending at P and must land
            // before any replacement that starts at P.
            bool aIns = a.Start == a.End;
            bool bIns = b.Start == b.End;
            if (aIns != bIns)
            {
                return aIns ? -1 : 1;
            }

            return a.Seq.CompareTo(b.Seq);
        }

        // ---------------------------------------------------------------- statements

        void ParseBlock()
        {
            while (!IsBlockEnd())
            {
                ParseStatement();
            }
        }

        bool IsBlockEnd()
        {
            LuauToken t = Cur;
            if (t.Kind == LuauTokenKind.Eof)
            {
                return true;
            }

            return t.Kind == LuauTokenKind.Name &&
                   (t.Text == "end" || t.Text == "until" || t.Text == "else" || t.Text == "elseif");
        }

        void ParseStatement()
        {
            LuauToken t = Cur;
            if (IsPunct(t, ";"))
            {
                Advance();
                return;
            }

            if (IsPunct(t, "@") && Peek(1).Kind == LuauTokenKind.Name)
            {
                DeleteKeepNewlines(t.Start, Peek(1).End);
                _ctx.Note("attribute", t);
                Advance();
                Advance();
                return;
            }

            if (IsPunct(t, "::"))
            {
                Advance();
                ExpectIdentifier();
                ExpectPunct("::");
                return;
            }

            if (t.Kind == LuauTokenKind.Name)
            {
                switch (t.Text)
                {
                    case "if":
                        ParseIfStatement();
                        return;
                    case "while":
                        ParseWhileStatement();
                        return;
                    case "do":
                        Advance();
                        ParseBlock();
                        ExpectKeyword("end");
                        return;
                    case "for":
                        ParseForStatement();
                        return;
                    case "repeat":
                        ParseRepeatStatement();
                        return;
                    case "function":
                        Advance();
                        ParseFuncName();
                        ParseFuncBody();
                        return;
                    case "local":
                        ParseLocalStatement();
                        return;
                    case "return":
                        ParseReturnStatement();
                        return;
                    case "break":
                        HandleBreak(t);
                        Advance();
                        return;
                    case "goto":
                        if (Peek(1).Kind == LuauTokenKind.Name && !Keywords.Contains(Peek(1).Text))
                        {
                            Advance();
                            Advance();
                            return;
                        }

                        break;
                    case "continue":
                        if (!CanExtendExpression(Peek(1)))
                        {
                            if (HasEnclosingLoop())
                            {
                                HandleContinue(t);
                            }
                            else
                            {
                                Warn("'continue' outside of a loop was left unchanged", t);
                            }

                            Advance();
                            return;
                        }

                        break;
                    case "type":
                        if (IsTypeStatementAhead(0))
                        {
                            ParseTypeStatement(t, false);
                            return;
                        }

                        break;
                    case "export":
                        if (IsName(Peek(1), "type") && IsTypeStatementAhead(1))
                        {
                            ParseTypeStatement(t, true);
                            return;
                        }

                        break;
                    default:
                        if (Keywords.Contains(t.Text))
                        {
                            Throw($"unexpected '{t.Text}'");
                        }

                        break;
                }
            }

            ParseExprStatement();
        }

        bool IsTypeStatementAhead(int offset)
        {
            LuauToken name = Peek(offset + 1);
            if (name.Kind != LuauTokenKind.Name || Keywords.Contains(name.Text))
            {
                return false;
            }

            LuauToken after = Peek(offset + 2);
            return IsPunct(after, "=") || IsPunct(after, "<");
        }

        void ParseTypeStatement(LuauToken startTok, bool isExport)
        {
            if (isExport)
            {
                Advance();
            }

            Advance();
            Advance();
            if (IsPunct(Cur, "<"))
            {
                SkipBalancedAngles();
            }

            ConsumeEquals();
            int end = SkipType();
            DeleteKeepNewlines(startTok.Start, end);
            _ctx.Note("type declaration", startTok);
        }

        static bool CanExtendExpression(LuauToken t)
        {
            if (t.Kind == LuauTokenKind.String || t.Kind == LuauTokenKind.InterpString)
            {
                return true;
            }

            if (t.Kind != LuauTokenKind.Punct)
            {
                return false;
            }

            switch (t.Text)
            {
                case "(":
                case "[":
                case ".":
                case ":":
                case "{":
                case "=":
                case ",":
                case "::":
                    return true;
                default:
                    return CompoundOps.Contains(t.Text);
            }
        }

        LoopFrame EnclosingLoop()
        {
            if (_loops.Count == 0)
            {
                return null;
            }

            LoopFrame top = _loops[_loops.Count - 1];
            return top.Barrier ? null : top;
        }

        bool HasEnclosingLoop()
        {
            return EnclosingLoop() != null;
        }

        void HandleBreak(LuauToken t)
        {
            EnclosingLoop()?.Breaks.Add(t);
        }

        void HandleContinue(LuauToken t)
        {
            EnclosingLoop()?.Continues.Add(t);
        }

        void ParseIfStatement()
        {
            Advance();
            ParseExpr();
            ExpectKeyword("then");
            ParseBlock();
            while (IsName(Cur, "elseif"))
            {
                Advance();
                ParseExpr();
                ExpectKeyword("then");
                ParseBlock();
            }

            if (IsName(Cur, "else"))
            {
                Advance();
                ParseBlock();
            }

            ExpectKeyword("end");
        }

        void ParseWhileStatement()
        {
            Advance();
            ParseExpr();
            LuauToken doTok = ExpectKeyword("do");
            LoopFrame frame = PushLoop(false);
            ParseBlock();
            LuauToken endTok = ExpectKeyword("end");
            PopLoop();
            FinalizeDoLoop(frame, doTok, endTok);
        }

        void ParseForStatement()
        {
            Advance();
            ExpectIdentifier();
            MaybeStripAnnotation();
            if (AtEquals())
            {
                ConsumeEquals();
                ParseExpr();
                ExpectPunct(",");
                ParseExpr();
                if (IsPunct(Cur, ","))
                {
                    Advance();
                    ParseExpr();
                }
            }
            else
            {
                while (IsPunct(Cur, ","))
                {
                    Advance();
                    ExpectIdentifier();
                    MaybeStripAnnotation();
                }

                ExpectKeyword("in");
                ParseExprList();
            }

            LuauToken doTok = ExpectKeyword("do");
            LoopFrame frame = PushLoop(false);
            ParseBlock();
            LuauToken endTok = ExpectKeyword("end");
            PopLoop();
            FinalizeDoLoop(frame, doTok, endTok);
        }

        void ParseRepeatStatement()
        {
            LuauToken repeatTok = Cur;
            Advance();
            LoopFrame frame = PushLoop(true);
            ParseBlock();
            LuauToken untilTok = ExpectKeyword("until");
            PopLoop();
            int condStart = Cur.Start;
            int condEnd = ParseExpr();
            FinalizeRepeatLoop(frame, repeatTok, untilTok, condStart, condEnd);
        }

        LoopFrame PushLoop(bool isRepeat)
        {
            var frame = new LoopFrame { IsRepeat = isRepeat };
            _loops.Add(frame);
            return frame;
        }

        void PopLoop()
        {
            _loops.RemoveAt(_loops.Count - 1);
        }

        void FinalizeDoLoop(LoopFrame frame, LuauToken doTok, LuauToken endTok)
        {
            if (frame.Continues.Count == 0)
            {
                return;
            }

            if (frame.Breaks.Count > 0)
            {
                string flag = NextTemp();
                Insert(doTok.End, " local " + flag + " = false repeat");
                for (int i = 0; i < frame.Breaks.Count; i++)
                {
                    ReplaceToken(frame.Breaks[i], flag + " = true break");
                }

                for (int i = 0; i < frame.Continues.Count; i++)
                {
                    ReplaceToken(frame.Continues[i], "break");
                }

                Insert(endTok.Start, "until true if " + flag + " then break end ");
            }
            else
            {
                Insert(doTok.End, " repeat");
                for (int i = 0; i < frame.Continues.Count; i++)
                {
                    ReplaceToken(frame.Continues[i], "break");
                }

                Insert(endTok.Start, "until true ");
            }

            _ctx.Note("continue", frame.Continues[0]);
        }

        void FinalizeRepeatLoop(LoopFrame frame, LuauToken repeatTok, LuauToken untilTok, int condStart, int condEnd)
        {
            if (frame.Continues.Count == 0)
            {
                return;
            }

            string flag = NextTemp();
            string cond = GetRewrittenText(condStart, condEnd);

            // WHY: 'continue' in repeat-until must evaluate the condition at the continue site (Luau
            // forbids the condition referencing locals declared after a continue, so the scope there is
            // sufficient); the loop is rebuilt as while-true so the condition never runs twice per pass.
            ReplaceToken(repeatTok, "while true do local " + flag + " = false repeat");
            for (int i = 0; i < frame.Continues.Count; i++)
            {
                ReplaceToken(frame.Continues[i], "if " + cond + " then " + flag + " = true end break");
            }

            for (int i = 0; i < frame.Breaks.Count; i++)
            {
                ReplaceToken(frame.Breaks[i], flag + " = true break");
            }

            ReplaceToken(untilTok, "if");
            Insert(condEnd, " then " + flag + " = true end until true if " + flag + " then break end end");
            if (cond.IndexOf('\n') >= 0)
            {
                Warn("multi-line repeat-until condition duplicated for 'continue'; line numbers shift inside this loop",
                    frame.Continues[0]);
            }

            _ctx.Note("continue", frame.Continues[0]);
        }

        void ParseFuncName()
        {
            ExpectIdentifier();
            while (IsPunct(Cur, "."))
            {
                Advance();
                ExpectIdentifier();
            }

            if (IsPunct(Cur, ":"))
            {
                Advance();
                ExpectIdentifier();
            }
        }

        int ParseFuncBody()
        {
            _funcDepth++;
            _loops.Add(new LoopFrame { Barrier = true });
            if (IsPunct(Cur, "<"))
            {
                LuauToken open = Cur;
                int genericsEnd = SkipBalancedAngles();
                DeleteKeepNewlines(open.Start, genericsEnd);
                _ctx.Note("type annotation", open);
            }

            ExpectPunct("(");
            if (!IsPunct(Cur, ")"))
            {
                while (true)
                {
                    if (IsPunct(Cur, "..."))
                    {
                        Advance();
                        MaybeStripAnnotation();
                    }
                    else
                    {
                        ExpectIdentifier();
                        MaybeStripAnnotation();
                    }

                    if (IsPunct(Cur, ","))
                    {
                        Advance();
                        continue;
                    }

                    break;
                }
            }

            ExpectPunct(")");
            if (IsPunct(Cur, ":"))
            {
                LuauToken colon = Cur;
                Advance();
                int end = SkipType();
                DeleteKeepNewlines(colon.Start, end);
                _ctx.Note("type annotation", colon);
            }

            ParseBlock();
            LuauToken endTok = ExpectKeyword("end");
            _loops.RemoveAt(_loops.Count - 1);
            _funcDepth--;
            return endTok.End;
        }

        void ParseLocalStatement()
        {
            Advance();
            if (IsName(Cur, "function"))
            {
                Advance();
                ExpectIdentifier();
                ParseFuncBody();
                return;
            }

            while (true)
            {
                ExpectIdentifier();
                MaybeStripAnnotation();
                if (IsPunct(Cur, ","))
                {
                    Advance();
                    continue;
                }

                break;
            }

            if (AtEquals())
            {
                ConsumeEquals();
                ParseExprList();
            }
        }

        void ParseReturnStatement()
        {
            Advance();
            if (!IsBlockEnd() && !IsPunct(Cur, ";"))
            {
                ParseExprList();
            }

            if (IsPunct(Cur, ";"))
            {
                Advance();
            }
        }

        void MaybeStripAnnotation()
        {
            if (!IsPunct(Cur, ":"))
            {
                return;
            }

            LuauToken colon = Cur;
            Advance();
            int end = SkipType();
            DeleteKeepNewlines(colon.Start, end);
            _ctx.Note("type annotation", colon);
        }

        void ParseExprStatement()
        {
            SuffixedInfo info = ParseSuffixedExpr();
            LuauToken t = Cur;
            if (t.Kind == LuauTokenKind.Punct && CompoundOps.Contains(t.Text))
            {
                ParseCompoundAssignment(info, t);
                return;
            }

            if (IsPunct(t, "="))
            {
                RequireAssignable(info);
                Advance();
                ParseExprList();
                return;
            }

            if (IsPunct(t, ","))
            {
                RequireAssignable(info);
                while (IsPunct(Cur, ","))
                {
                    Advance();
                    SuffixedInfo more = ParseSuffixedExpr();
                    RequireAssignable(more);
                }

                ExpectPunct("=");
                ParseExprList();
                return;
            }

            if (info.LastKind != SuffixKind.Call)
            {
                Throw($"unexpected '{t.Text}' — expression is not a statement");
            }
        }

        void RequireAssignable(SuffixedInfo info)
        {
            if (info.LastKind == SuffixKind.Call || info.LastKind == SuffixKind.Paren)
            {
                Throw("cannot assign to this expression");
            }
        }

        void ParseCompoundAssignment(SuffixedInfo target, LuauToken opTok)
        {
            RequireAssignable(target);
            Advance();
            string binOp = opTok.Text == "..=" ? ".." : opTok.Text.Substring(0, opTok.Text.Length - 1);

            // WHY: only a bare identifier is safe to duplicate. Any '.'/'[' means the target reads
            // through '__index' (and writes through '__newindex'), and any '(' is a call — evaluating
            // the access text twice would double those side effects, so capture the object/key in
            // temps instead. LastKind == Name is exactly a suffix-free identifier.
            bool needTemp = target.LastKind != SuffixKind.Name;
            if (!needTemp)
            {
                string lhs = GetRewrittenText(target.Start, target.End);
                if (binOp == "//")
                {
                    ReplaceToken(opTok, "= math.floor(" + lhs + " / (");
                    int end = ParseExpr();
                    Insert(end, "))");
                }
                else
                {
                    ReplaceToken(opTok, "= " + lhs + " " + binOp + " (");
                    int end = ParseExpr();
                    Insert(end, ")");
                }
            }
            else
            {
                // WHY: a call inside the target could have side effects; duplicating the text would
                // evaluate it twice, so the object (and key) are captured in temps first.
                string obj = NextTemp();
                string access;
                if (target.LastKind == SuffixKind.Dot)
                {
                    access = obj + "." + target.DotName.Text;
                    Insert(target.Start, "do local " + obj + " = ");
                    DeleteKeepNewlines(target.DotTok.Start, target.DotName.End);
                }
                else if (target.LastKind == SuffixKind.Index)
                {
                    string key = NextTemp();
                    access = obj + "[" + key + "]";
                    Insert(target.Start, "do local " + obj + " = ");
                    ReplaceToken(target.OpenBracket, " local " + key + " = ");
                    ReplaceToken(target.CloseBracket, " ");
                }
                else
                {
                    Throw("cannot compound-assign to this expression");
                    return;
                }

                if (binOp == "//")
                {
                    ReplaceToken(opTok, access + " = math.floor(" + access + " / (");
                    int end = ParseExpr();
                    Insert(end, ")) end");
                }
                else
                {
                    ReplaceToken(opTok, access + " = " + access + " " + binOp + " (");
                    int end = ParseExpr();
                    Insert(end, ") end");
                }
            }

            _ctx.Note("compound assignment", opTok);
        }

        // ---------------------------------------------------------------- expressions

        int ParseExprList()
        {
            int end = ParseExpr();
            while (IsPunct(Cur, ","))
            {
                Advance();
                end = ParseExpr();
            }

            return end;
        }

        int ParseExpr()
        {
            return ParseBinExpr(0).End;
        }

        static int BinaryPrecedence(LuauToken t)
        {
            if (t.Kind == LuauTokenKind.Name)
            {
                if (t.Text == "or")
                {
                    return 1;
                }

                if (t.Text == "and")
                {
                    return 2;
                }

                return 0;
            }

            if (t.Kind != LuauTokenKind.Punct)
            {
                return 0;
            }

            switch (t.Text)
            {
                case "<":
                case ">":
                case "<=":
                case ">=":
                case "~=":
                case "==":
                    return 3;
                case "..":
                    return 4;
                case "+":
                case "-":
                    return 5;
                case "*":
                case "/":
                case "//":
                case "%":
                    return 6;
                case "^":
                    return 8;
                default:
                    return 0;
            }
        }

        static bool IsRightAssociative(LuauToken t)
        {
            return IsPunct(t, "..") || IsPunct(t, "^");
        }

        bool IsUnaryOp(LuauToken t)
        {
            return IsName(t, "not") || IsPunct(t, "-") || IsPunct(t, "#");
        }

        (int Start, int End) ParseBinExpr(int limit)
        {
            const int UnaryPrecedence = 7;
            int start = Cur.Start;
            int end;
            if (IsUnaryOp(Cur))
            {
                Advance();
                end = ParseBinExpr(UnaryPrecedence).End;
            }
            else
            {
                end = ParseSimpleExpr();
            }

            while (true)
            {
                LuauToken op = Cur;
                int prec = BinaryPrecedence(op);
                if (prec == 0 || prec <= limit)
                {
                    break;
                }

                Advance();
                (int _, int rhsEnd) = ParseBinExpr(IsRightAssociative(op) ? prec - 1 : prec);
                if (op.Text == "//")
                {
                    Insert(start, "math.floor(");
                    ReplaceToken(op, "/");
                    Insert(rhsEnd, ")");
                    _ctx.Note("floor division", op);
                }

                end = rhsEnd;
            }

            return (start, end);
        }

        int ParseSimpleExpr()
        {
            LuauToken t = Cur;
            int end;
            if (t.Kind == LuauTokenKind.Number)
            {
                if (t.NumberRewrite != null)
                {
                    ReplaceToken(t, t.NumberRewrite);
                    _ctx.Note("number literal", t);
                }

                Advance();
                end = t.End;
            }
            else if (t.Kind == LuauTokenKind.String)
            {
                EmitStringRewrite(t);
                Advance();
                end = t.End;
            }
            else if (t.Kind == LuauTokenKind.InterpString)
            {
                RewriteInterpolatedString(t);
                Advance();
                end = t.End;
            }
            else if (IsName(t, "nil") || IsName(t, "true") || IsName(t, "false"))
            {
                Advance();
                end = t.End;
            }
            else if (IsPunct(t, "..."))
            {
                MarkVarargUnsupportedIfInsideIfExpression(t);
                Advance();
                end = t.End;
            }
            else if (IsName(t, "function"))
            {
                Advance();
                end = ParseFuncBody();
            }
            else if (IsPunct(t, "{"))
            {
                end = ParseTableConstructor();
            }
            else if (IsName(t, "if"))
            {
                end = ParseIfExpression(t);
            }
            else
            {
                end = ParseSuffixedExpr().End;
            }

            while (IsPunct(Cur, "::"))
            {
                LuauToken castTok = Cur;
                Advance();
                int typeEnd = SkipType();
                DeleteKeepNewlines(castTok.Start, typeEnd);
                _ctx.Note("type cast", castTok);
                end = typeEnd;
            }

            return end;
        }

        /// <summary>
        /// A top-level <c>...</c> in an if-expression branch would become a parse error inside the
        /// generated closure (the closure has no varargs), so the enclosing if-expression is marked
        /// unsupported and left unrewritten rather than emitting broken code.
        /// </summary>
        void MarkVarargUnsupportedIfInsideIfExpression(LuauToken t)
        {
            for (int i = _ifExprFuncDepths.Count - 1; i >= 0; i--)
            {
                if (_ifExprFuncDepths[i] == _funcDepth)
                {
                    _ifExprUnsupported[i] = true;
                    return;
                }
            }
        }

        int ParseIfExpression(LuauToken ifTok)
        {
            int editMark = _ctx.Edits.Count;
            ReplaceToken(ifTok, "(function() if");
            Advance();
            _ifExprFuncDepths.Add(_funcDepth);
            _ifExprUnsupported.Add(false);
            ParseExpr();
            WrapIfExpressionBranch();
            while (IsName(Cur, "elseif"))
            {
                Advance();
                ParseExpr();
                WrapIfExpressionBranch();
            }

            LuauToken elseTok = ExpectKeyword("else");
            ReplaceToken(elseTok, "else return");
            int branchStart = Cur.Start;
            int end = ParseExpr();
            Insert(branchStart, "(");
            Insert(end, ")");
            Insert(end, " end end)()");

            bool unsupported = _ifExprUnsupported[_ifExprUnsupported.Count - 1];
            _ifExprUnsupported.RemoveAt(_ifExprUnsupported.Count - 1);
            _ifExprFuncDepths.RemoveAt(_ifExprFuncDepths.Count - 1);
            if (unsupported)
            {
                // WHY: a top-level vararg in a branch cannot be lowered into the closure; drop every
                // edit this if-expression produced and pass the original construct through with a
                // diagnostic rather than emitting code that fails to parse under Lua 5.2.
                _ctx.Edits.RemoveRange(editMark, _ctx.Edits.Count - editMark);
                Warn("'...' (varargs) inside an if-then-else expression is unsupported; the construct was left unchanged", ifTok);
                return end;
            }

            _ctx.Note("if expression", ifTok);
            return end;
        }

        /// <summary>
        /// Rewrites <c>then EXPR</c> into <c>then return (EXPR)</c>. The parentheses truncate a
        /// multi-value tail call or <c>...</c> to a single result, matching Luau's if-expression
        /// semantics (darklua expands all results; parenthesizing is the cheap correct fix).
        /// </summary>
        void WrapIfExpressionBranch()
        {
            ReplaceToken(ExpectKeyword("then"), "then return");
            int branchStart = Cur.Start;
            int end = ParseExpr();
            Insert(branchStart, "(");
            Insert(end, ")");
        }

        int ParseTableConstructor()
        {
            ExpectPunct("{");
            while (!IsPunct(Cur, "}"))
            {
                if (IsPunct(Cur, "["))
                {
                    Advance();
                    ParseExpr();
                    ExpectPunct("]");
                    ExpectPunct("=");
                    ParseExpr();
                }
                else if (Cur.Kind == LuauTokenKind.Name && !Keywords.Contains(Cur.Text) && IsPunct(Peek(1), "="))
                {
                    Advance();
                    Advance();
                    ParseExpr();
                }
                else
                {
                    ParseExpr();
                }

                if (IsPunct(Cur, ",") || IsPunct(Cur, ";"))
                {
                    Advance();
                    continue;
                }

                break;
            }

            LuauToken close = ExpectPunct("}");
            return close.End;
        }

        SuffixedInfo ParseSuffixedExpr()
        {
            var info = new SuffixedInfo();
            LuauToken t = Cur;
            info.Start = t.Start;
            if (t.Kind == LuauTokenKind.Name && !Keywords.Contains(t.Text))
            {
                Advance();
                info.LastKind = SuffixKind.Name;
                info.End = t.End;
            }
            else if (IsPunct(t, "("))
            {
                Advance();
                ParseExpr();
                LuauToken close = ExpectPunct(")");
                info.LastKind = SuffixKind.Paren;
                info.End = close.End;
            }
            else
            {
                Throw($"expected expression near '{t.Text}'");
            }

            while (true)
            {
                LuauToken c = Cur;
                if (IsPunct(c, "."))
                {
                    Advance();
                    LuauToken name = ExpectIdentifier();
                    info.LastKind = SuffixKind.Dot;
                    info.DotTok = c;
                    info.DotName = name;
                    info.End = name.End;
                }
                else if (IsPunct(c, "["))
                {
                    Advance();
                    ParseExpr();
                    LuauToken close = ExpectPunct("]");
                    info.LastKind = SuffixKind.Index;
                    info.OpenBracket = c;
                    info.CloseBracket = close;
                    info.End = close.End;
                }
                else if (IsPunct(c, ":") && Peek(1).Kind == LuauTokenKind.Name && IsCallArgsStart(Peek(2)))
                {
                    Advance();
                    Advance();
                    info.End = ParseCallArgs();
                    info.LastKind = SuffixKind.Call;
                    info.HasCall = true;
                }
                else if (IsCallArgsStart(c))
                {
                    info.End = ParseCallArgs();
                    info.LastKind = SuffixKind.Call;
                    info.HasCall = true;
                }
                else
                {
                    break;
                }
            }

            return info;
        }

        static bool IsCallArgsStart(LuauToken t)
        {
            return IsPunct(t, "(") || IsPunct(t, "{") ||
                   t.Kind == LuauTokenKind.String || t.Kind == LuauTokenKind.InterpString;
        }

        int ParseCallArgs()
        {
            LuauToken t = Cur;
            if (IsPunct(t, "("))
            {
                Advance();
                if (!IsPunct(Cur, ")"))
                {
                    ParseExprList();
                }

                LuauToken close = ExpectPunct(")");
                return close.End;
            }

            if (t.Kind == LuauTokenKind.String)
            {
                EmitStringRewrite(t);
                Advance();
                return t.End;
            }

            if (t.Kind == LuauTokenKind.InterpString)
            {
                RewriteInterpolatedString(t);
                Advance();
                return t.End;
            }

            return ParseTableConstructor();
        }

        // ---------------------------------------------------------------- interpolation

        void RewriteInterpolatedString(LuauToken tok)
        {
            AddEdit(tok.Start, tok.Start + 1, "(\"");
            List<LuauInterpPart> parts = tok.InterpParts;
            for (int i = 0; i < parts.Count; i++)
            {
                LuauInterpPart part = parts[i];
                if (!part.IsExpr)
                {
                    string escaped = EscapeInterpText(part.Start, part.End);
                    if (escaped != null)
                    {
                        AddEdit(part.Start, part.End, escaped);
                    }
                }
                else
                {
                    AddEdit(part.Start - 1, part.Start, "\" .. tostring(");
                    AddEdit(part.End, part.End + 1, ") .. \"");
                    var sub = new LuauRewriteParser(_ctx, part.Tokens);
                    sub.ParseInterpolationExpression();
                }
            }

            AddEdit(tok.End - 1, tok.End, "\")");
            _ctx.Note("string interpolation", tok);
        }

        /// <summary>
        /// Converts interpolated-string text into double-quoted string content: <c>\`</c> and
        /// <c>\{</c> lose their Luau-only escapes, a bare <c>"</c> gains one. Returns null when the
        /// text is already valid as-is.
        /// </summary>
        string EscapeInterpText(int start, int end)
        {
            string src = _ctx.Source;
            var sb = new StringBuilder(end - start + 4);
            bool changed = false;
            int i = start;
            while (i < end)
            {
                char c = src[i];
                if (c == '\\' && i + 1 < end)
                {
                    char n = src[i + 1];
                    if (n == '`' || n == '{')
                    {
                        sb.Append(n);
                        changed = true;
                    }
                    else
                    {
                        sb.Append(c).Append(n);
                    }

                    i += 2;
                }
                else if (c == '"')
                {
                    sb.Append("\\\"");
                    changed = true;
                    i++;
                }
                else
                {
                    sb.Append(c);
                    i++;
                }
            }

            return changed ? sb.ToString() : null;
        }

        // ---------------------------------------------------------------- type skipping

        /// <summary>
        /// Consumes a Luau type expression (unions, intersections, optionals, generics, table and
        /// function types, typeof) and returns the character offset just past it. Nothing inside a
        /// type is rewritten — the whole span is deleted by the caller.
        /// </summary>
        int SkipType()
        {
            if (IsPunct(Cur, "|") || IsPunct(Cur, "&"))
            {
                Advance();
            }

            int end = SkipTypeAtom();
            while (true)
            {
                if (IsPunct(Cur, "?"))
                {
                    end = Cur.End;
                    Advance();
                }
                else if (IsPunct(Cur, "|") || IsPunct(Cur, "&"))
                {
                    Advance();
                    end = SkipTypeAtom();
                }
                else if (IsPunct(Cur, "->"))
                {
                    Advance();
                    end = SkipType();
                }
                else
                {
                    break;
                }
            }

            return end;
        }

        int SkipTypeAtom()
        {
            LuauToken t = Cur;
            if (t.Kind == LuauTokenKind.Name && !Keywords.Contains(t.Text) ||
                IsName(t, "nil") || IsName(t, "true") || IsName(t, "false"))
            {
                bool isTypeof = t.Text == "typeof";
                Advance();
                int end = t.End;
                if (isTypeof && IsPunct(Cur, "("))
                {
                    return SkipBalancedPunct("(", ")");
                }

                while (IsPunct(Cur, ".") && Peek(1).Kind == LuauTokenKind.Name)
                {
                    Advance();
                    end = Cur.End;
                    Advance();
                }

                if (IsPunct(Cur, "<"))
                {
                    end = SkipBalancedAngles();
                }

                if (IsPunct(Cur, "..."))
                {
                    end = Cur.End;
                    Advance();
                }

                return end;
            }

            if (t.Kind == LuauTokenKind.String)
            {
                Advance();
                return t.End;
            }

            if (IsPunct(t, "{"))
            {
                return SkipBalancedPunct("{", "}");
            }

            if (IsPunct(t, "("))
            {
                return SkipBalancedPunct("(", ")");
            }

            if (IsPunct(t, "<"))
            {
                SkipBalancedAngles();
                return SkipBalancedPunct("(", ")");
            }

            if (IsPunct(t, "..."))
            {
                Advance();
                if (Cur.Kind == LuauTokenKind.Name && !Keywords.Contains(Cur.Text) ||
                    Cur.Kind == LuauTokenKind.String || IsPunct(Cur, "{") || IsPunct(Cur, "("))
                {
                    return SkipTypeAtom();
                }

                return t.End;
            }

            Throw($"unexpected '{t.Text}' in type position");
            return 0;
        }

        int SkipBalancedPunct(string open, string close)
        {
            ExpectPunct(open);
            int depth = 1;
            while (true)
            {
                LuauToken t = Cur;
                if (t.Kind == LuauTokenKind.Eof)
                {
                    Throw($"unfinished type: missing '{close}'");
                }

                Advance();
                if (IsPunct(t, open))
                {
                    depth++;
                }
                else if (IsPunct(t, close))
                {
                    depth--;
                    if (depth == 0)
                    {
                        return t.End;
                    }
                }
            }
        }

        /// <summary>
        /// Skips a balanced <c>&lt;...&gt;</c> list. A closing <c>&gt;=</c> token (as in
        /// <c>Foo&lt;T&gt;=x</c>) is split: the '&gt;' closes the list and the '=' is remembered for
        /// the next <see cref="ConsumeEquals"/>.
        /// </summary>
        int SkipBalancedAngles()
        {
            ExpectPunct("<");
            int depth = 1;
            while (true)
            {
                LuauToken t = Cur;
                if (t.Kind == LuauTokenKind.Eof)
                {
                    Throw("unfinished type parameter list: missing '>'");
                }

                Advance();
                if (IsPunct(t, "<"))
                {
                    depth++;
                }
                else if (IsPunct(t, ">"))
                {
                    depth--;
                    if (depth == 0)
                    {
                        return t.End;
                    }
                }
                else if (IsPunct(t, ">="))
                {
                    depth--;
                    if (depth == 0)
                    {
                        _pendingEqPos = t.Start + 1;
                        return t.Start + 1;
                    }
                }
            }
        }
    }
}
