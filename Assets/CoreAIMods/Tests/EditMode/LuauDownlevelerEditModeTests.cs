using System;
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
        static DownlevelResult ProcessOk(string luau)
        {
            DownlevelResult result = LuauDownleveler.Process(luau);
            Assert.IsFalse(result.HasErrors,
                "Expected no downlevel errors, got: " + DescribeDiagnostics(result));
            return result;
        }

        static string DescribeDiagnostics(DownlevelResult result)
        {
            var parts = new System.Text.StringBuilder();
            foreach (DownlevelDiagnostic d in result.Diagnostics)
            {
                parts.Append(d).Append("; ");
            }

            return parts.ToString();
        }

        static void AssertRewrites(string luau, string expectedLua)
        {
            DownlevelResult result = ProcessOk(luau);
            Assert.IsTrue(result.Changed, "Expected a rewrite for: " + luau);
            Assert.AreEqual(expectedLua, result.LuaSource);
        }

        static void AssertUnchanged(string source)
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
            AssertUnchanged("-- x += 1 and `tick {a}` and continue\nlocal doc = [[\ncounter += 1\ncontinue\n`interp {x}`\n]]\n--[[ y //= 2 ]]\nprint(doc)");
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
            AssertRewrites(
                "for k: string, v: number in pairs(t) do print(k, v) end",
                "for k, v in pairs(t) do print(k, v) end");
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
            AssertRewrites("x //= 2", "x = math.floor(x / ( 2))");
        }

        [Test]
        public void CompoundOnDottedPath_DuplicatesPath()
        {
            AssertRewrites("stats.points += 10", "stats.points = stats.points + ( 10)");
        }

        [Test]
        public void CompoundOnSimpleIndex_DuplicatesIndex()
        {
            AssertRewrites("t[i] += 1", "t[i] = t[i] + ( 1)");
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
        public void ContinueInRepeat_EvaluatesConditionAtContinueSite()
        {
            AssertRewrites(
                "repeat continue until done",
                "while true do local __luau_t0 = false repeat if done then __luau_t0 = true end break if done then __luau_t0 = true end until true if __luau_t0 then break end end");
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

        static int CountOccurrences(string text, string needle)
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
                "local m = (\"Hi \" .. tostring(name) .. \"!\")");
        }

        [Test]
        public void Interpolation_MultipleExpressions()
        {
            AssertRewrites(
                "local m = `{a} and {b}`",
                "local m = (\"\" .. tostring(a) .. \" and \" .. tostring(b) .. \"\")");
        }

        [Test]
        public void Interpolation_Nested()
        {
            AssertRewrites(
                "local m = `outer {`inner {x}`}!`",
                "local m = (\"outer \" .. tostring((\"inner \" .. tostring(x) .. \"\")) .. \"!\")");
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
                "local m = (\"sum: \" .. tostring(a + b(2)) .. \"\")");
        }

        [Test]
        public void Interpolation_WithNestedIfExpression()
        {
            AssertRewrites(
                "local m = `{if ok then \"yes\" else \"no\"}`",
                "local m = (\"\" .. tostring((function() if ok then return \"yes\" else return \"no\" end end)()) .. \"\")");
        }

        [Test]
        public void Interpolation_AsCallArgument()
        {
            AssertRewrites(
                "print`score {s}`",
                "print(\"score \" .. tostring(s) .. \"\")");
        }

        // ---------------------------------------------------------------- if expressions

        [Test]
        public void IfExpression_BecomesInlineClosure()
        {
            AssertRewrites(
                "local v = if c then 1 else 2",
                "local v = (function() if c then return 1 else return 2 end end)()");
        }

        [Test]
        public void IfExpression_WithElseifChain()
        {
            AssertRewrites(
                "local r = if a then 1 elseif b then 2 else 3",
                "local r = (function() if a then return 1 elseif b then return 2 else return 3 end end)()");
        }

        [Test]
        public void IfExpression_InsideCallArguments()
        {
            AssertRewrites(
                "f(if c then 1 else 2, 3)",
                "f((function() if c then return 1 else return 2 end end)(), 3)");
        }

        [Test]
        public void IfExpression_InReturnStatement()
        {
            AssertRewrites(
                "return if c then f() else g()",
                "return (function() if c then return f() else return g() end end)()");
        }

        // ---------------------------------------------------------------- floor division

        [Test]
        public void FloorDivision_BecomesMathFloor()
        {
            AssertRewrites("local h = x // 2", "local h = math.floor(x / 2)");
        }

        [Test]
        public void FloorDivision_RespectsPrecedence()
        {
            AssertRewrites("local a = 1 + 7 // 2", "local a = 1 + math.floor(7 / 2)");
        }

        [Test]
        public void FloorDivision_ChainsLeftAssociatively()
        {
            AssertRewrites("local b = 9 // 2 // 2", "local b = math.floor(math.floor(9 / 2) / 2)");
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
    }
}
