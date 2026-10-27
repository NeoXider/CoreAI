using System;
using System.Text;
using CoreAI.Infrastructure.Luau;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode
{
    /// <summary>
    /// Construct-level coverage for the Luau → Lua 5.2 downlevel preprocessor: every rewritten
    /// construct alone and combined, construct-like text inside strings/comments that must NOT
    /// rewrite, contextual keywords used as identifiers, error passthrough, and line preservation.
    /// </summary>
    [TestFixture]
    public sealed class LuauDownlevelerEditModeTests
    {
        private static DownlevelResult ProcessOk(string luau)
        {
            DownlevelResult result = LuauDownleveler.Process(luau);
            Assert.IsFalse(result.HasErrors,
                "Expected no downlevel errors, got: " + DescribeDiagnostics(result));
            return result;
        }

        private static string DescribeDiagnostics(DownlevelResult result)
        {
            StringBuilder parts = new();
            foreach (DownlevelDiagnostic d in result.Diagnostics)
            {
                parts.Append(d).Append("; ");
            }

            return parts.ToString();
        }

        private static void AssertRewrites(string luau, string expectedLua)
        {
            DownlevelResult result = ProcessOk(luau);
            Assert.IsTrue(result.Changed, "Expected a rewrite for: " + luau);
            Assert.AreEqual(expectedLua, result.LuaSource);
        }

        private static void AssertUnchanged(string source)
        {
            DownlevelResult result = LuauDownleveler.Process(source);
            Assert.IsFalse(result.HasErrors, "Unexpected errors: " + DescribeDiagnostics(result));
            Assert.IsFalse(result.Changed, "Expected passthrough for: " + source);
            Assert.AreSame(source, result.LuaSource, "Passthrough must return the original string instance.");
        }

        // ---------------------------------------------------------------- passthrough

        [Test]
        public void PlainLua_MethodCallsAndTables_PassThroughUnchanged()
        {
            AssertUnchanged("local t = {}\nfunction t:add(n)\n  return 1 + n\nend\nprint(t:add(2))");
        }

        [Test]
        public void PlainLua_LabelsAndGoto_PassThroughUnchanged()
        {
            AssertUnchanged("for i = 1, 3 do\n  if i == 2 then goto skip end\n  print(i)\n  ::skip::\nend");
        }

        [TestCase("local x = 1\n::again::\nreturn x")]
        [TestCase("x = 1\n::again::\nreturn x")]
        [TestCase("repeat tick() until ready\n::again::\nreturn true")]
        public void PlainLua_LabelsAfterExpressions_RetainStringIdentity(string source)
        {
            AssertUnchanged(source);
        }

        [Test]
        public void CastOnAdjacentLine_IsStillStripped()
        {
            AssertRewrites("local x = 1\n::number\nreturn x", "local x = 1\n\nreturn x");
        }

        [Test]
        public void PlainLua_NumericLoopRetainsIdentityWhileGenericLoopIsNormalized()
        {
            AssertUnchanged("for index = 1, 3 do print(index) end");
            DownlevelResult result = ProcessOk("for key, value in pairs({}) do end");
            Assert.IsTrue(result.Changed);
            StringAssert.Contains("generalized iteration", DescribeDiagnostics(result));
        }

        [Test]
        public void GeneralizedIteration_InsertsAdapterAndPreservesLineCount()
        {
            string luau = "local values = { 1, 2 }\nfor i, value in values do\nprint(i, value)\nend";
            DownlevelResult result = ProcessOk(luau);

            Assert.IsTrue(result.Changed);
            StringAssert.Contains("for i, value in __luau_t0(values) do", result.LuaSource);
            Assert.AreEqual(CountOccurrences(luau, "\n"), CountOccurrences(result.LuaSource, "\n"));
            bool mentioned = false;
            foreach (DownlevelDiagnostic diagnostic in result.Diagnostics)
            {
                mentioned |= diagnostic.Severity == DownlevelSeverity.Info
                    && diagnostic.Message.Contains("generalized iteration");
            }

            Assert.IsTrue(mentioned);
        }

        [Test]
        public void PlainLua_EmptySource_PassesThrough()
        {
            AssertUnchanged("");
        }

        [Test]
        public void NullSource_YieldsEmptyOutputWithoutErrors()
        {
            DownlevelResult result = LuauDownleveler.Process(null);
            Assert.IsFalse(result.Changed);
            Assert.IsFalse(result.HasErrors);
            Assert.AreEqual("", result.LuaSource);
        }

        [Test]
        public void ConstructLikeTextInsideStrings_IsNotRewritten()
        {
            AssertUnchanged("local s = \"a += b // c\"\nlocal u = 'x ..= y'\nprint(s, u)");
        }

        [Test]
        public void ConstructLikeTextInsideLongStringsAndComments_IsNotRewritten()
        {
            AssertUnchanged(
                "-- x += 1 and `tick {a}` and continue\nlocal doc = [[\ncounter += 1\ncontinue\n`interp {x}`\n]]\n--[[ y //= 2 ]]\nprint(doc)");
        }

        [Test]
        public void UrlDoubleSlashInsideString_IsNotRewritten()
        {
            AssertUnchanged("local url = \"https://example.com//assets\"\nprint(url)");
        }

        [Test]
        public void TypeUsedAsFunctionCall_IsNotRewritten()
        {
            AssertUnchanged("local kind = type(3)\nprint(type(nil), kind)");
        }

        [Test]
        public void TypeUsedAsAssignmentTarget_IsNotRewritten()
        {
            AssertUnchanged("type = 5\nprint(type)");
        }

        [Test]
        public void ContinueUsedAsIdentifier_IsNotRewritten()
        {
            AssertUnchanged("local continue = 1\ncontinue = continue + 1\nprint(continue)");
        }

        [Test]
        public void ExportUsedAsIdentifier_IsNotRewritten()
        {
            AssertUnchanged("local export = {}\nexport.value = 1\nprint(export.value)");
        }

        // ---------------------------------------------------------------- type stripping

        [Test]
        public void LocalAnnotation_IsStripped()
        {
            AssertRewrites("local x: number = 5", "local x = 5");
        }

        [Test]
        public void MultipleLocalAnnotations_AreStripped()
        {
            AssertRewrites("local a: number, b: string = 1, \"s\"", "local a, b = 1, \"s\"");
        }

        [Test]
        public void FunctionSignatureAnnotationsAndGenerics_AreStripped()
        {
            AssertRewrites(
                "function add<T>(a: number, b: number): number return a + b end",
                "function add(a, b) return a + b end");
        }

        [Test]
        public void CastExpression_IsStripped()
        {
            AssertRewrites("local y = x :: number + 1", "local y = x  + 1");
        }

        [Test]
        public void TypeDeclarations_AreRemovedEntirely()
        {
            DownlevelResult result = ProcessOk(
                "type Point = { x: number, y: number }\nexport type ID = string | number\nlocal p = 1");
            Assert.IsTrue(result.Changed);
            Assert.AreEqual("\n\nlocal p = 1", result.LuaSource);
        }

        [Test]
        public void FunctionTypeAndTableTypeAnnotations_AreStripped()
        {
            DownlevelResult result = ProcessOk(
                "local cb: (Player) -> () = nil\nlocal d: {[string]: {value: number?}} = {}\nlocal e: typeof(workspace.Part) = nil\nprint(cb, d, e)");
            Assert.IsTrue(result.Changed);
            Assert.AreEqual("local cb = nil\nlocal d = {}\nlocal e = nil\nprint(cb, d, e)", result.LuaSource);
        }

        [Test]
        public void GenericAnnotationTouchingEquals_IsStripped()
        {
            AssertRewrites("local x: Foo<number>=5", "local x=5");
        }

        [Test]
        public void NestedGenericAnnotation_IsStripped()
        {
            AssertRewrites("local x: Dict<string, List<number>> = {}", "local x = {}");
        }

        [Test]
        public void ForLoopAnnotations_AreStripped()
        {
            AssertRewrites("for i: number = 1, 10 do print(i) end", "for i = 1, 10 do print(i) end");
            DownlevelResult genericResult = ProcessOk(
                "for k: string, v: number in pairs(t) do print(k, v) end");
            Assert.IsTrue(genericResult.Changed);
            StringAssert.Contains(
                "for k, v in __luau_t0(pairs(t)) do print(k, v) end",
                genericResult.LuaSource);
            StringAssert.DoesNotContain("k: string", genericResult.LuaSource);
            StringAssert.DoesNotContain("v: number", genericResult.LuaSource);
        }

        [Test]
        public void VariadicAnnotation_IsStripped()
        {
            AssertRewrites(
                "local function f(...: number): number return 0 end",
                "local function f(...) return 0 end");
        }

        [Test]
        public void OptionalTypeAnnotation_IsStripped()
        {
            AssertRewrites("local target: Team? = nil", "local target = nil");
        }

        // ---------------------------------------------------------------- compound assignment

        [Test]
        public void CompoundAdd_IsRewritten()
        {
            AssertRewrites("x += 1", "x = x + ( 1)");
        }

        [Test]
        public void CompoundSubtract_IsRewritten()
        {
            AssertRewrites("x -= 1", "x = x - ( 1)");
        }

        [Test]
        public void CompoundMultiply_IsRewritten()
        {
            AssertRewrites("x *= 2", "x = x * ( 2)");
        }

        [Test]
        public void CompoundDivide_IsRewritten()
        {
            AssertRewrites("x /= 2", "x = x / ( 2)");
        }

        [Test]
        public void CompoundModulo_IsRewritten()
        {
            AssertRewrites("x %= 2", "x = x % ( 2)");
        }

        [Test]
        public void CompoundPower_IsRewritten()
        {
            AssertRewrites("x ^= 2", "x = x ^ ( 2)");
        }

        [Test]
        public void CompoundConcat_IsRewritten()
        {
            AssertRewrites("s ..= \"a\"", "s = s .. ( \"a\")");
        }

        [Test]
        public void CompoundFloorDivide_UsesMathFloor()
        {
            AssertRewrites("x //= 2", "local __luau_t0 = _ENV; local __luau_t1 = function() return __luau_t0.math.floor end;x = __luau_t1()(x / ( 2))");
        }

        [Test]
        public void CompoundOnDottedPath_CapturesObjectTemp()
        {
            // WHY: a '.'-target reads through __index, so the object is captured once instead of the
            // access text being duplicated (which would double a side-effecting __index).
            AssertRewrites("stats.points += 10",
                "do local __luau_t0 = stats __luau_t0.points = __luau_t0.points + ( 10) end");
        }

        [Test]
        public void CompoundOnSimpleIndex_CapturesObjectAndKeyTemps()
        {
            AssertRewrites("t[i] += 1",
                "do local __luau_t0 = t local __luau_t1 = i  __luau_t0[__luau_t1] = __luau_t0[__luau_t1] + ( 1) end");
        }

        [Test]
        public void CompoundRhs_IsParenthesized_PreservingAssociativity()
        {
            AssertRewrites("a -= b - c", "a = a - ( b - c)");
        }

        [Test]
        public void CompoundWithCallInTarget_CapturesTemps()
        {
            DownlevelResult result = ProcessOk("t[key()] += 5");
            Assert.IsTrue(result.Changed);
            StringAssert.Contains("do local __luau_t0 = t", result.LuaSource);
            StringAssert.Contains("local __luau_t1 = key()", result.LuaSource);
            StringAssert.Contains("__luau_t0[__luau_t1] = __luau_t0[__luau_t1] + (", result.LuaSource);
            StringAssert.Contains(") end", result.LuaSource);
        }

        [Test]
        public void CompoundWithCallPrefix_CapturesObjectTemp()
        {
            DownlevelResult result = ProcessOk("getStats().points += 1");
            Assert.IsTrue(result.Changed);
            StringAssert.Contains("do local __luau_t0 = getStats()", result.LuaSource);
            StringAssert.Contains("__luau_t0.points = __luau_t0.points + (", result.LuaSource);
        }

        // ---------------------------------------------------------------- continue

        [Test]
        public void ContinueInWhile_BecomesRepeatUntilTrue()
        {
            AssertRewrites("while a do continue end", "while a do repeat break until true end");
        }

        [Test]
        public void ContinueInFor_PreservesLinesAndParses()
        {
            DownlevelResult result = ProcessOk(
                "for i = 1, 5 do\n  if i == 2 then continue end\n  total = total + i\nend");
            Assert.IsTrue(result.Changed);
            Assert.AreEqual(
                "for i = 1, 5 do repeat\n  if i == 2 then break end\n  total = total + i\nuntil true end",
                result.LuaSource);
        }

        [Test]
        public void ContinueAlongsideBreak_UsesFlagForm()
        {
            AssertRewrites(
                "while a do if x then break end continue end",
                "while a do local __luau_t0 = false repeat if x then __luau_t0 = true break end break until true if __luau_t0 then break end end");
        }

        [Test]
        public void ContinueInRepeat_JumpsToOriginalCondition()
        {
            AssertRewrites(
                "repeat continue until done",
                "repeat goto __luau_t0 ::__luau_t0:: until done");
        }

        [Test]
        public void ContinueInNestedLoop_BindsToInnerLoopOnly()
        {
            DownlevelResult result = ProcessOk(
                "for i = 1, 3 do\n  for j = 1, 3 do\n    if j == 2 then continue end\n  end\n  total = total + 1\nend");
            Assert.IsTrue(result.Changed);
            int repeatCount = CountOccurrences(result.LuaSource, "repeat");
            Assert.AreEqual(1, repeatCount, "Only the inner loop must be wrapped:\n" + result.LuaSource);
        }

        [Test]
        public void ContinueInsideClosureWithoutLoop_WarnsAndLeavesSource()
        {
            DownlevelResult result = LuauDownleveler.Process("local f = function() continue end");
            Assert.IsFalse(result.Changed);
            bool warned = false;
            foreach (DownlevelDiagnostic d in result.Diagnostics)
            {
                warned |= d.Severity == DownlevelSeverity.Warning && d.Message.Contains("continue");
            }

            Assert.IsTrue(warned, "Expected a warning about 'continue' outside a loop.");
        }

        private static int CountOccurrences(string text, string needle)
        {
            int count = 0;
            int index = 0;
            while ((index = text.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
            {
                count++;
                index += needle.Length;
            }

            return count;
        }

        // ---------------------------------------------------------------- string interpolation

        [Test]
        public void Interpolation_Basic_BecomesConcatWithTostring()
        {
            AssertRewrites(
                "local m = `Hi {name}!`",
                "local __luau_t0 = tostring;local m = (\"Hi \" .. __luau_t0(name) .. \"!\")");
        }

        [Test]
        public void Interpolation_MultipleExpressions()
        {
            AssertRewrites(
                "local m = `{a} and {b}`",
                "local __luau_t0 = tostring;local m = (\"\" .. __luau_t0(a) .. \" and \" .. __luau_t0(b) .. \"\")");
        }

        [Test]
        public void Interpolation_Nested()
        {
            AssertRewrites(
                "local m = `outer {`inner {x}`}!`",
                "local __luau_t0 = tostring;local m = (\"outer \" .. __luau_t0((\"inner \" .. __luau_t0(x) .. \"\")) .. \"!\")");
        }

        [Test]
        public void Interpolation_EscapesQuotesBracesAndBackticks()
        {
            AssertRewrites(
                "local s = `he said \"hi\" \\{here} and \\`tick\\``",
                "local s = (\"he said \\\"hi\\\" {here} and `tick`\")");
        }

        [Test]
        public void Interpolation_WithExpressionCall()
        {
            AssertRewrites(
                "local m = `sum: {a + b(2)}`",
                "local __luau_t0 = tostring;local m = (\"sum: \" .. __luau_t0(a + b(2)) .. \"\")");
        }

        [Test]
        public void Interpolation_WithNestedIfExpression()
        {
            AssertRewrites(
                "local m = `{if ok then \"yes\" else \"no\"}`",
                "local __luau_t0 = tostring;local m = (\"\" .. __luau_t0((function() if ok then return (\"yes\") else return (\"no\") end end)()) .. \"\")");
        }

        [Test]
        public void Interpolation_AsCallArgument()
        {
            AssertRewrites(
                "print`score {s}`",
                "local __luau_t0 = tostring;print(\"score \" .. __luau_t0(s) .. \"\")");
        }

        // ---------------------------------------------------------------- if expressions

        [Test]
        public void IfExpression_BecomesInlineClosure()
        {
            AssertRewrites(
                "local v = if c then 1 else 2",
                "local v = (function() if c then return (1) else return (2) end end)()");
        }

        [Test]
        public void IfExpression_WithElseifChain()
        {
            AssertRewrites(
                "local r = if a then 1 elseif b then 2 else 3",
                "local r = (function() if a then return (1) elseif b then return (2) else return (3) end end)()");
        }

        [Test]
        public void IfExpression_InsideCallArguments()
        {
            AssertRewrites(
                "f(if c then 1 else 2, 3)",
                "f((function() if c then return (1) else return (2) end end)(), 3)");
        }

        [Test]
        public void IfExpression_InReturnStatement()
        {
            AssertRewrites(
                "return if c then f() else g()",
                "return (function() if c then return (f()) else return (g()) end end)()");
        }

        // ---------------------------------------------------------------- floor division

        [Test]
        public void FloorDivision_BecomesMathFloor()
        {
            AssertRewrites("local h = x // 2", "local __luau_t0 = _ENV; local __luau_t1 = function() return __luau_t0.math.floor end;local h = __luau_t1()(x / 2)");
        }

        [Test]
        public void FloorDivision_RespectsPrecedence()
        {
            AssertRewrites("local a = 1 + 7 // 2", "local __luau_t0 = _ENV; local __luau_t1 = function() return __luau_t0.math.floor end;local a = 1 + __luau_t1()(7 / 2)");
        }

        [Test]
        public void FloorDivision_ChainsLeftAssociatively()
        {
            AssertRewrites("local b = 9 // 2 // 2", "local __luau_t0 = _ENV; local __luau_t1 = function() return __luau_t0.math.floor end;local b = __luau_t1()(__luau_t1()(9 / 2) / 2)");
        }

        // ---------------------------------------------------------------- number literals

        [Test]
        public void LuauNumberLiterals_AreNormalized()
        {
            AssertRewrites("local n = 1_000_000 + 0b1010", "local n = 1000000 + 10");
        }

        // ---------------------------------------------------------------- robustness

        [Test]
        public void MalformedLuauInput_NeverThrows_ReturnsOriginalWithErrorDiagnostic()
        {
            string[] samples =
            {
                "x += ",
                "t[1] //= ",
                "`unfinished interp",
                "\"unfinished string",
                "local x: = 5",
                "local v = if c then 1",
                "repeat continue",
                "local s = `{}`"
            };
            foreach (string sample in samples)
            {
                DownlevelResult result = null;
                Assert.DoesNotThrow(() => result = LuauDownleveler.Process(sample), "Threw on: " + sample);
                Assert.IsTrue(result.HasErrors, "Expected an error diagnostic for: " + sample);
                Assert.IsFalse(result.Changed, "Malformed input must not be marked changed: " + sample);
                Assert.AreSame(sample, result.LuaSource, "Malformed input must pass through verbatim: " + sample);
            }
        }

        [Test]
        public void MalformedPlainLuaWithoutLuauTriggers_PassesThroughForTheVmToReport()
        {
            string[] samples =
            {
                "local = 5",
                "if then end",
                "local x = ("
            };
            foreach (string sample in samples)
            {
                DownlevelResult result = null;
                Assert.DoesNotThrow(() => result = LuauDownleveler.Process(sample), "Threw on: " + sample);
                Assert.IsFalse(result.Changed, "Plain input must not be marked changed: " + sample);
                Assert.AreSame(sample, result.LuaSource, "Plain input must pass through verbatim: " + sample);
            }
        }

        [Test]
        public void ErrorDiagnostics_CarryOriginalLineAndColumn()
        {
            DownlevelResult result = LuauDownleveler.Process("local a = 1\nlocal b = 2\nlocal c: = 3\n");
            Assert.IsTrue(result.HasErrors);
            bool found = false;
            foreach (DownlevelDiagnostic d in result.Diagnostics)
            {
                if (d.Severity == DownlevelSeverity.Error)
                {
                    Assert.AreEqual(3, d.Line, "Error should point at line 3: " + d);
                    found = true;
                }
            }

            Assert.IsTrue(found);
        }

        [Test]
        public void RewrittenOutput_PreservesLineCount()
        {
            string luau =
                "type State = { hp: number }\n" +
                "local hp: number = 100\n" +
                "local log = \"\"\n" +
                "for wave = 1, 10 do\n" +
                "  if wave % 2 == 0 then\n" +
                "    continue\n" +
                "  end\n" +
                "  hp -= wave // 2\n" +
                "  log ..= `wave {wave} hp {hp} `\n" +
                "end\n" +
                "print(log)\n";
            DownlevelResult result = ProcessOk(luau);
            Assert.IsTrue(result.Changed);
            Assert.AreEqual(CountOccurrences(luau, "\n"), CountOccurrences(result.LuaSource, "\n"),
                "Line count must be preserved:\n" + result.LuaSource);
        }

        [Test]
        public void Process_IsDeterministic()
        {
            string luau = "local x: number = 1\nx += 2 // 2\nprint(`x = {x}`)";
            DownlevelResult first = ProcessOk(luau);
            DownlevelResult second = ProcessOk(luau);
            Assert.AreEqual(first.LuaSource, second.LuaSource);
        }

        [Test]
        public void RewriteDiagnostics_ReportConstructKinds()
        {
            DownlevelResult result = ProcessOk("x += 1");
            bool mentioned = false;
            foreach (DownlevelDiagnostic d in result.Diagnostics)
            {
                mentioned |= d.Severity == DownlevelSeverity.Info && d.Message.Contains("compound assignment");
            }

            Assert.IsTrue(mentioned, "Expected an Info diagnostic naming the rewritten construct.");
        }

        // ---------------------------------------------------------------- generics

        [Test]
        public void GenericFunction_Unannotated_StripsTypeParameters()
        {
            AssertRewrites("local function identity<T>(x) return x end",
                "local function identity(x) return x end");
        }

        [Test]
        public void GenericGlobalFunction_Unannotated_StripsTypeParameters()
        {
            AssertRewrites("function wrap<T, U>(a, b) return a end",
                "function wrap(a, b) return a end");
        }

        [Test]
        public void PlainComparisons_DoNotFalselyTrigger()
        {
            AssertUnchanged("if a < b then f() end");
            AssertUnchanged("x = y > z");
        }

        [Test]
        public void ComparisonsThatArmGenericHeuristic_PassThroughByteIdentical()
        {
            // WHY: 'a < b, c > d' arms the '<'-before-'>' heuristic, but the parse produces zero edits
            // so the original string instance is returned unchanged — a false positive is a safe no-op.
            AssertUnchanged("f(a < b, c > d)");
        }

        // ---------------------------------------------------------------- unsupported constructs

        [Test]
        public void IfExpressionWithTopLevelVararg_IsUnsupportedAndLeftUnchanged()
        {
            DownlevelResult result =
                LuauDownleveler.Process("local function f(...) return if true then ... else 0 end");
            Assert.IsFalse(result.Changed, "The if-expression must be passed through, not half-rewritten.");
            bool warned = false;
            foreach (DownlevelDiagnostic d in result.Diagnostics)
            {
                warned |= d.Severity == DownlevelSeverity.Warning && d.Message.Contains("unsupported");
            }

            Assert.IsTrue(warned, "Expected an 'unsupported' warning for the vararg if-expression.");
        }

        // ---------------------------------------------------------------- nesting depth

        private static string Repeat(string text, int count)
        {
            StringBuilder builder = new(text.Length * count);
            for (int i = 0; i < count; i++)
            {
                builder.Append(text);
            }

            return builder.ToString();
        }

        /// <summary>
        /// Luau source nested <paramref name="depth"/> levels deep in one construct. Every shape carries
        /// a compound assignment, so the rewriting parser runs, not only the lexer.
        /// </summary>
        private static string NestedSource(string shape, int depth)
        {
            const string head = "local x = 0\nx += 1\n";
            switch (shape)
            {
                case "parentheses":
                    return head + "local y = " + Repeat("(", depth) + "1" + Repeat(")", depth) + "\n";
                case "tables":
                    return head + "local y = " + Repeat("{", depth) + Repeat("}", depth) + "\n";
                case "functions":
                    return head + Repeat("local function f() ", depth) + Repeat("end ", depth) + "\n";
                case "if-expressions":
                    return head + "local y = " + Repeat("if x then (", depth) + "1" + Repeat(") else 2", depth) + "\n";
                case "interpolations":
                    return head + "local y = " + Repeat("`{", depth) + "1" + Repeat("}`", depth) + "\n";
                case "return-type arrows":
                    return head + "local f: " + Repeat("a -> ", depth) + "a = nil\n";
                default:
                    throw new ArgumentException("unknown shape " + shape, nameof(shape));
            }
        }

        private static bool RefusedAsTooDeep(DownlevelResult result)
        {
            foreach (DownlevelDiagnostic d in result.Diagnostics)
            {
                if (d.Severity == DownlevelSeverity.Error && d.Message.Contains("levels deep"))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// The rewriting parser and the lexer recursed once per nesting level with no limit, before the
        /// VM parser (which stops at 200 levels) ever saw the source, so a deeply nested chunk overflowed
        /// the .NET stack and killed the process: about 2,000 nested parentheses or 1,000 nested
        /// if-expressions on a 1 MB stack. It is refused like the VM parser refuses it.
        /// </summary>
        [TestCase("parentheses")]
        [TestCase("tables")]
        [TestCase("functions")]
        [TestCase("if-expressions")]
        [TestCase("interpolations")]
        [TestCase("return-type arrows")]
        public void DeeplyNestedSource_IsRefusedWithAnError_InsteadOfOverflowingTheStack(string shape)
        {
            // WHY two steps: the first depth is one level past the limit and costs a few hundred KB of
            // stack at most, so a parser without the limit fails this assertion instead of reaching the
            // second depth, which would crash the test host rather than fail the test.
            DownlevelResult justPastTheLimit = LuauDownleveler.Process(NestedSource(shape, 201));
            Assert.IsTrue(RefusedAsTooDeep(justPastTheLimit),
                "Source nested 201 levels deep (" + shape + ") must be refused with a nesting error, got: "
                + DescribeDiagnostics(justPastTheLimit));

            string deeplyNested = NestedSource(shape, 100000);
            LuauDownlevelOptions nestingOptions = new(maxInputCharacters: deeplyNested.Length,
                maxOutputCharacters: checked(deeplyNested.Length * 2));
            DownlevelResult farPastTheLimit = LuauDownleveler.Process(deeplyNested, "chunk", nestingOptions);
            Assert.IsTrue(RefusedAsTooDeep(farPastTheLimit),
                "Source nested 100,000 levels deep (" + shape + ") must be refused, not overflow the stack: "
                + DescribeDiagnostics(farPastTheLimit));
            Assert.AreEqual(200, LuauDownleveler.MaxNestingDepth,
                "The limit must stay the bundled Lua 5.2 parser's own, so it refuses nothing that parser compiles.");
        }

        [TestCase("parentheses")]
        [TestCase("tables")]
        [TestCase("functions")]
        [TestCase("if-expressions")]
        [TestCase("interpolations")]
        [TestCase("return-type arrows")]
        public void NestedSourceWithinTheLimit_IsStillDownleveled(string shape)
        {
            // WHY 30: the deepest nested if-expression whose rewrite the bundled VM parser still compiles is
            // 33 levels (each one becomes a closure), so 30 is legal source for every shape here.
            DownlevelResult result = ProcessOk(NestedSource(shape, 30));

            Assert.IsTrue(result.Changed, "Source nested 30 levels deep (" + shape + ") must still be rewritten.");
        }

        // ---------------------------------------------------------------- string escapes

        [Test]
        public void UnicodeEscape_RewritesToDecimalBytes()
        {
            AssertRewrites("local s = \"\\u{48}\"", "local s = \"\\072\"");
        }

        [Test]
        public void ZEscape_StripsFollowingWhitespace()
        {
            AssertRewrites("local s = \"a\\z   b\"", "local s = \"ab\"");
        }

        [TestCase("\n", "\"")]
        [TestCase("\r\n", "\"")]
        [TestCase("\n", "'")]
        [TestCase("\r\n", "'")]
        public void ZEscape_ConsumesMultilineWhitespaceAndWarns(string newline, string quote)
        {
            DownlevelResult result = ProcessOk("return " + quote + "a\\z" + newline + "  b" + quote);
            Assert.AreEqual("return " + quote + "ab" + quote, result.LuaSource);
            bool warned = false;
            foreach (DownlevelDiagnostic diagnostic in result.Diagnostics)
            {
                warned |= diagnostic.Severity == DownlevelSeverity.Warning
                    && diagnostic.Message.Contains("line numbers shift");
            }

            Assert.IsTrue(warned);
        }

        [Test]
        public void ZEscape_LaterDiagnosticRetainsOriginalPosition()
        {
            string source = "local text = \"a\\z\r\n b\"\r\n$";
            DownlevelResult result = LuauDownleveler.Process(source);
            Assert.IsTrue(result.HasErrors);
            Assert.AreSame(source, result.LuaSource);
            Assert.AreEqual(3, result.Diagnostics[0].Line);
            Assert.AreEqual(1, result.Diagnostics[0].Column);
        }

        [TestCase("\\u{100000041}")]
        [TestCase("\\u{80000000}")]
        [TestCase("\\u{110000}")]
        [TestCase("\\u{}")]
        [TestCase("\\u{41")]
        [TestCase("\\u{xyz}")]
        [TestCase("\\u41")]
        [TestCase("\\u{00000000000000041}")]
        public void MalformedUnicodeEscape_IsRefusedInOriginalCoordinates(string escape)
        {
            string[] quotes = { "\"", "`" };
            foreach (string quote in quotes)
            {
                string source = "\nreturn " + quote + escape + quote;
                DownlevelResult result = LuauDownleveler.Process(source);
                Assert.IsTrue(result.HasErrors, source);
                Assert.IsFalse(result.Changed);
                Assert.AreSame(source, result.LuaSource);
                Assert.AreEqual(2, result.Diagnostics[0].Line);
                Assert.AreEqual(9, result.Diagnostics[0].Column);
            }
        }

        [Test]
        public void UnicodeEscape_RangeAndLeadingZeroBoundariesRemainSupported()
        {
            AssertRewrites("return \"\\u{0000000000000041}\"", "return \"\\065\"");
            AssertRewrites("return \"\\u{10FFFF}\"", "return \"\\244\\143\\191\\191\"");
            AssertRewrites("return \"\\u{D800}\"", "return \"\\237\\160\\128\"");
        }

        [TestCase("input")]
        [TestCase("output")]
        [TestCase("edits")]
        [TestCase("work")]
        public void ConfiguredBudgetRefusal_ReturnsOriginalSourceAndError(string limit)
        {
            string source = "local x = 1 x += 2 x += 3 return x";
            LuauDownlevelOptions options;
            switch (limit)
            {
                case "input":
                    options = new LuauDownlevelOptions(maxInputCharacters: source.Length - 1);
                    break;
                case "output":
                    options = new LuauDownlevelOptions(maxOutputCharacters: source.Length);
                    break;
                case "edits":
                    options = new LuauDownlevelOptions(maxEdits: 1);
                    break;
                default:
                    options = new LuauDownlevelOptions(maxWorkUnits: source.Length);
                    break;
            }

            DownlevelResult refused = LuauDownleveler.Process(source, "budget", options);
            Assert.IsTrue(refused.HasErrors);
            Assert.IsFalse(refused.Changed);
            Assert.AreSame(source, refused.LuaSource);
            StringAssert.Contains("budget exceeded", DescribeDiagnostics(refused));
            DownlevelResult allowed = LuauDownleveler.Process(source, "budget", new LuauDownlevelOptions());
            Assert.IsFalse(allowed.HasErrors);
            Assert.IsTrue(allowed.Changed);
        }

        [TestCase(1)]
        [TestCase(4)]
        [TestCase(8)]
        [TestCase(16)]
        public void NestedRepeatConditionFamily_UsesLinearSpaceAndHonorsOutputBudget(int depth)
        {
            string expression = "true";
            for (int index = 0; index < depth; index++)
            {
                expression = "(function() repeat if true then continue end until " + expression
                    + " return true end)()";
            }

            string source = "return " + expression;
            DownlevelResult allowed = ProcessOk(source);
            Assert.Less(allowed.LuaSource.Length, source.Length * 4);
            DownlevelResult refused = LuauDownleveler.Process(source, "nested",
                new LuauDownlevelOptions(maxOutputCharacters: source.Length));
            Assert.IsTrue(refused.HasErrors);
            Assert.AreSame(source, refused.LuaSource);
        }

        [TestCase("resolver();error('injected')")]
        [TestCase("end")]
        [TestCase("__luau_t0")]
        [TestCase("_ENV")]
        [TestCase("")]
        public void InvalidTrustedResolverName_IsRefusedWithoutThrowing(string name)
        {
            string source = "for key, value in {} do end";
            DownlevelResult result = LuauDownleveler.Process(source, "resolver",
                new LuauDownlevelOptions(trustedIteratorResolverName: name));
            Assert.IsTrue(result.HasErrors);
            Assert.AreSame(source, result.LuaSource);
        }

        [Test]
        public void InvalidBudgetOptions_AreRefusedWithoutThrowing()
        {
            string source = "return 1";
            DownlevelResult result = LuauDownleveler.Process(source, "options",
                new LuauDownlevelOptions(maxInputCharacters: 0));
            Assert.IsTrue(result.HasErrors);
            Assert.AreSame(source, result.LuaSource);
        }
    }
}
