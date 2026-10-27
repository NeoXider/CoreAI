using System.Text;
using System.Threading;
using CoreAI.Infrastructure.Luau;
using Lua;
using Lua.Standard;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode
{
    /// <summary>
    /// Semantic verification on the real Lua-CSharp VM: downleveled output must not merely parse but
    /// compute the same values Luau would — compound-assignment associativity, floor-division
    /// rounding, continue flow in every loop kind, single evaluation of side-effecting targets,
    /// falsy if-expression results and interpolation formatting.
    /// </summary>
    [TestFixture]
    public sealed class LuauDownlevelerExecutionEditModeTests
    {
        private SynchronizationContext _savedContext;

        /// <summary>
        /// The Lua-CSharp VM is driven synchronously via GetAwaiter().GetResult(); detaching Unity's
        /// main-thread SynchronizationContext lets VM continuations complete on the thread pool
        /// instead of deadlocking the blocked test thread.
        /// </summary>
        [SetUp]
        public void DetachSynchronizationContext()
        {
            _savedContext = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(null);
        }

        [TearDown]
        public void RestoreSynchronizationContext()
        {
            SynchronizationContext.SetSynchronizationContext(_savedContext);
        }

        private static LuaValue[] RunLuau(string luau, LuauDownlevelOptions options = null, string hostSetup = null)
        {
            DownlevelResult result = LuauDownleveler.Process(luau, "exec", options);
            StringBuilder sb = new();
            foreach (DownlevelDiagnostic d in result.Diagnostics)
            {
                sb.Append(d).Append('\n');
            }

            Assert.IsFalse(result.HasErrors, "Downlevel errors:\n" + sb);
            LuaState state = LuaState.Create();
            state.OpenBasicLibrary();
            state.OpenMathLibrary();
            state.OpenStringLibrary();
            state.OpenTableLibrary();
            if (hostSetup != null)
            {
                state.DoStringAsync(hostSetup).GetAwaiter().GetResult();
            }

            return state.DoStringAsync(result.LuaSource).GetAwaiter().GetResult();
        }

        private static double RunNumber(string luau)
        {
            LuaValue[] result = RunLuau(luau);
            Assert.IsTrue(result.Length > 0, "Expected a return value.");
            return result[0].Read<double>();
        }

        [Test]
        public void CompoundSubtract_KeepsRightHandSideGrouped()
        {
            Assert.AreEqual(7, RunNumber("local a = 10 a -= 5 - 2 return a"));
        }

        [Test]
        public void CompoundConcat_AppendsWholeRightHandSide()
        {
            LuaValue[] result = RunLuau("local s = \"a\" s ..= \"b\" .. \"c\" return s");
            Assert.AreEqual("abc", result[0].Read<string>());
        }

        [Test]
        public void CompoundPower_Works()
        {
            Assert.AreEqual(8, RunNumber("local x = 2 x ^= 3 return x"));
        }

        [Test]
        public void FloorDivision_RoundsDownPositive()
        {
            Assert.AreEqual(3, RunNumber("return 7 // 2"));
        }

        [Test]
        public void FloorDivision_RoundsDownNegative()
        {
            Assert.AreEqual(-4, RunNumber("return -7 // 2"));
        }

        [Test]
        public void FloorDivisionAssignment_Works()
        {
            Assert.AreEqual(4, RunNumber("local x = 9 x //= 2 return x"));
        }

        [Test]
        public void FloorDivision_BindsTighterThanAddition()
        {
            Assert.AreEqual(4, RunNumber("return 1 + 7 // 2"));
        }

        [Test]
        public void ContinueInWhile_SkipsEvenNumbers()
        {
            string luau =
                "local total = 0\n" +
                "local i = 0\n" +
                "while i < 5 do\n" +
                "\ti += 1\n" +
                "\tif i % 2 == 0 then\n" +
                "\t\tcontinue\n" +
                "\tend\n" +
                "\ttotal += i\n" +
                "end\n" +
                "return total\n";
            Assert.AreEqual(9, RunNumber(luau));
        }

        [Test]
        public void ContinueInNumericFor_SkipsTail()
        {
            Assert.AreEqual(6, RunNumber(
                "local t = 0 for i = 1, 10 do if i > 3 then continue end t += i end return t"));
        }

        [Test]
        public void GeneralizedIteration_DirectTableUsesNativeKeysAndValues()
        {
            Assert.AreEqual(66, RunNumber(
                "local total = 0 for i, v in { 10, 20, 30 } do total = total + i + v end return total"));
        }

        [Test]
        public void GeneralizedIteration_IterMetamethodSuppliesCustomIterator()
        {
            string luau =
                "local values = { 2, 4, 8 }\n" +
                "setmetatable(values, { __iter = function(t)\n" +
                "    local i = #t + 1\n" +
                "    return function() i = i - 1 if i > 0 then return i, t[i] end end\n" +
                "end })\n" +
                "local encoded = 0\n" +
                "for i, v in values do encoded = encoded * 10 + i + v end\n" +
                "return encoded\n";
            Assert.AreEqual(1163, RunNumber(luau));
        }

        [Test]
        public void GeneralizedIteration_PreservesOrdinaryIteratorTriples()
        {
            Assert.AreEqual(15, RunNumber(
                "local total = 0 for _, v in ipairs({ 4, 5, 6 }) do total = total + v end return total"));
        }

        [Test]
        public void ContinueAndBreakInSameLoop_BothWork()
        {
            string luau =
                "local t = 0\n" +
                "for i = 1, 10 do\n" +
                "\tif i == 5 then break end\n" +
                "\tif i % 2 == 0 then continue end\n" +
                "\tt += i\n" +
                "end\n" +
                "return t\n";
            Assert.AreEqual(4, RunNumber(luau));
        }

        [Test]
        public void ContinueInRepeat_StillChecksCondition()
        {
            string luau =
                "local n = 0\n" +
                "local hits = 0\n" +
                "repeat\n" +
                "\tn += 1\n" +
                "\tif n % 2 == 1 then continue end\n" +
                "\thits += 1\n" +
                "until n >= 6\n" +
                "return hits\n";
            Assert.AreEqual(3, RunNumber(luau));
        }

        [Test]
        public void ContinueInRepeat_CanExitViaCondition()
        {
            string luau =
                "local n = 0\n" +
                "repeat\n" +
                "\tn += 1\n" +
                "\tif true then continue end\n" +
                "until n >= 3\n" +
                "return n\n";
            Assert.AreEqual(3, RunNumber(luau));
        }

        [Test]
        public void ContinueInRepeat_ConditionUsesOriginalBindingsOncePerIteration()
        {
            LuaValue[] result = RunLuau(
                "local n, calls = 0, 0 "
                + "local function done() calls += 1 return n >= 2 end "
                + "repeat n += 1 if n > 5 then error('finite guard') end "
                + "do local n = 99 local function done() error('shadow called') end continue end "
                + "until done() return n, calls");
            Assert.AreEqual(2, result[0].Read<double>());
            Assert.AreEqual(2, result[1].Read<double>());
        }

        [Test]
        public void ContinueInRepeat_PreservesEarlierLocalsAndSkipsLaterLocals()
        {
            Assert.AreEqual(2, RunNumber(
                "local n = 0 repeat n += 1 local value = n "
                + "if n > 5 then error('finite guard') end if true then continue end "
                + "local unused = 99 until value >= 2 return n"));
        }

        [Test]
        public void ContinueInRepeat_LaterClosuresRetainDistinctIterationCells()
        {
            LuaValue[] result = RunLuau(
                "local n = 0 local saved = {} repeat n += 1 "
                + "if n > 5 then error('finite guard') end if n == 1 then continue end "
                + "local value = n saved[n] = function() return value end "
                + "until n >= 3 return saved[2](), saved[3]()");
            Assert.AreEqual(2, result[0].Read<double>());
            Assert.AreEqual(3, result[1].Read<double>());
        }

        [Test]
        public void ContinueInRepeat_BreakSkipsCondition()
        {
            LuaValue[] result = RunLuau(
                "local n, calls = 0, 0 local function done() calls += 1 return false end "
                + "repeat n += 1 if n == 2 then break end continue until done() return n, calls");
            Assert.AreEqual(2, result[0].Read<double>());
            Assert.AreEqual(1, result[1].Read<double>());
        }

        [TestCase(1)]
        [TestCase(4)]
        [TestCase(8)]
        public void NestedRepeatConditions_RemainBoundedAndExecute(int depth)
        {
            string expression = "true";
            for (int index = 0; index < depth; index++)
            {
                expression = "(function() repeat if true then continue end until " + expression
                    + " return true end)()";
            }

            string source = "return " + expression;
            DownlevelResult transformed = LuauDownleveler.Process(source);
            Assert.IsFalse(transformed.HasErrors);
            Assert.Less(transformed.LuaSource.Length, source.Length * 4);
            Assert.IsTrue(RunLuau(source)[0].Read<bool>());
        }

        [TestCase("")]
        [TestCase("local unrelated: number = 0 ")]
        public void IfExpressions_InStatementConditionsExecuteWithoutAnotherTrigger(string prefix)
        {
            Assert.AreEqual(7, RunNumber(prefix
                + "if if true then true else false then return 7 end return 0"));
            Assert.AreEqual(8, RunNumber(prefix
                + "if false then return 0 elseif if true then true else false then return 8 end return 0"));
            Assert.AreEqual(2, RunNumber(prefix
                + "local n = 0 while if n < 2 then true else false do n = n + 1 end return n"));
        }

        [TestCase("local x = 1\n::again::\nreturn x", 1)]
        [TestCase("x = 1\n::again::\nreturn x", 1)]
        [TestCase("local n = 0 repeat n = n + 1 until n >= 2\n::again::\nreturn n", 2)]
        public void LuaLabels_AfterExpressionsExecute(string source, int expected)
        {
            Assert.AreEqual(expected, RunNumber(source));
        }

        [Test]
        public void GeneratedPrimitives_IgnoreNestedShadowingAndItsSideEffects()
        {
            LuaValue[] result = RunLuau(
                "local calls = 0 local math = {floor = function() calls += 1 return 99 end} "
                + "local tostring = function() calls += 1 return 'wrong' end "
                + "local function nested() do local math = 0 local tostring = 0 "
                + "local x = 9 x //= 2 local t = {9} t[1] //= 2 "
                + "return 7 // 2, x, t[1], `value={3}` end end "
                + "local a, b, c, text = nested() return a, b, c, text, calls");
            Assert.AreEqual(3, result[0].Read<double>());
            Assert.AreEqual(4, result[1].Read<double>());
            Assert.AreEqual(4, result[2].Read<double>());
            Assert.AreEqual("value=3", result[3].Read<string>());
            Assert.AreEqual(0, result[4].Read<double>());
        }

        [Test]
        public void FloorHelper_PrecedesGeneratedCompoundTargetAtChunkStart()
        {
            Assert.AreEqual(7, RunNumber("({9})[1] //= 2 return 7"));
        }

        [Test]
        public void TrustedIteratorResolver_IsReplaceableAndCapturedBeforeUserLocals()
        {
            LuauDownlevelOptions options = new(trustedIteratorResolverName: "trustedIterator");
            string hostSetup = "resolve_calls = 0 trustedIterator = function(...) "
                + "resolve_calls = resolve_calls + 1 if select('#', ...) ~= 1 then return ... end "
                + "return function(_, previous) if previous == nil then return 1, 99 end end, nil, nil end";
            LuaValue[] result = RunLuau(
                "local trustedIterator = 0 local total = 0 "
                + "for _, value in {4, 5} do total += value end return total, resolve_calls",
                options, hostSetup);
            Assert.AreEqual(99, result[0].Read<double>());
            Assert.AreEqual(1, result[1].Read<double>());
        }

        [Test]
        public void FloorDivision_UnusedOperationDoesNotRequireMath()
        {
            LuaValue[] result = RunLuau(
                "local function unused() return 7 // 2 end return 42",
                null, "math = nil");
            Assert.AreEqual(42, result[0].Read<double>());
        }

        [Test]
        public void FloorDivision_MissingMathErrorsAtOperation()
        {
            Assert.Catch<System.Exception>(() =>
                RunLuau("return 7 // 2", null, "math = nil"));
        }

        [Test]
        public void FloorDivision_ObtainsFunctionBeforeOperandEffects()
        {
            LuaValue[] result = RunLuau(
                "return (function() math = nil return 7 end)() // 2");
            Assert.AreEqual(3, result[0].Read<double>());
        }

        [TestCase("\n", "\"")]
        [TestCase("\r\n", "\"")]
        [TestCase("\n", "'")]
        [TestCase("\r\n", "'")]
        public void StringWhitespaceEscape_ConsumesRealNewlines(string newline, string quote)
        {
            LuaValue[] result = RunLuau("return " + quote + "a\\z" + newline + "  b" + quote);
            Assert.AreEqual("ab", result[0].Read<string>());
        }

        [Test]
        public void StringEscapedBackslash_DoesNotConsumeFollowingZ()
        {
            Assert.AreEqual("a\\z b", RunLuau("return \"a\\\\z b\"")[0].Read<string>());
        }

        [Test]
        public void StringEscapedCrLf_ProducesOneNewline()
        {
            Assert.AreEqual("a\nb", RunLuau("return \"a\\\r\nb\"")[0].Read<string>());
        }

        [TestCase("\\u{41}", new int[] { 0x41, 0x32 })]
        [TestCase("\\u{20AC}", new int[] { 0xE2, 0x82, 0xAC, 0x32 })]
        [TestCase("\\u{1F680}", new int[] { 0xF0, 0x9F, 0x9A, 0x80, 0x32 })]
        public void InterpolatedUnicodeEscape_RemainsLiteralBeforeAnExpression(string escape, int[] expected)
        {
            AssertLuaBytes("`" + escape + "{2}`", expected);
        }

        [Test]
        public void Interpolation_EscapedBackslashAndBraceRemainLiteral()
        {
            Assert.AreEqual("\\u{41}", RunLuau("return `\\\\u\\{41}`")[0].Read<string>());
            Assert.AreEqual("\\u41", RunLuau("return `\\\\u{41}`")[0].Read<string>());
        }

        [Test]
        public void UnicodeEscape_UpperSupportedCodePointExecutes()
        {
            AssertLuaBytes("\"\\u{10FFFF}\"", 0xF4, 0x8F, 0xBF, 0xBF);
            AssertLuaBytes("`\\u{10FFFF}{2}`", 0xF4, 0x8F, 0xBF, 0xBF, 0x32);
        }

        private static void AssertLuaBytes(string expression, params int[] expected)
        {
            LuaValue[] result = RunLuau("local value = " + expression
                + " return #value, string.byte(value, 1, #value)");
            Assert.AreEqual(expected.Length + 1, result.Length);
            Assert.AreEqual(expected.Length, result[0].Read<double>());
            for (int index = 0; index < expected.Length; index++)
            {
                Assert.AreEqual(expected[index], result[index + 1].Read<double>(), "Byte " + index);
            }
        }

        [Test]
        public void ContinueInGenericFor_Works()
        {
            string luau =
                "local values = {1, 2, 3, 4}\n" +
                "local sum = 0\n" +
                "for _, v in ipairs(values) do\n" +
                "\tif v == 2 then continue end\n" +
                "\tsum += v\n" +
                "end\n" +
                "return sum\n";
            Assert.AreEqual(8, RunNumber(luau));
        }

        [Test]
        public void IfExpression_CanReturnFalsyValue()
        {
            LuaValue[] result = RunLuau("return if true then false else true");
            Assert.IsFalse(result[0].Read<bool>(),
                "if-expressions must survive falsy branch values (and/or folding would break this).");
        }

        [Test]
        public void IfExpression_ElseifChainSelectsMiddleBranch()
        {
            LuaValue[] result = RunLuau(
                "local g = 12 return if g < 10 then \"low\" elseif g < 20 then \"mid\" else \"high\"");
            Assert.AreEqual("mid", result[0].Read<string>());
        }

        [Test]
        public void Interpolation_FormatsValues()
        {
            LuaValue[] result = RunLuau("local name = \"Ana\" return `Hello {name}!`");
            Assert.AreEqual("Hello Ana!", result[0].Read<string>());
        }

        [Test]
        public void Interpolation_StringifiesExpressions()
        {
            LuaValue[] result = RunLuau("return `n = {1 + 2}`");
            Assert.AreEqual("n = 3", result[0].Read<string>());
        }

        [Test]
        public void Interpolation_Nested_Works()
        {
            LuaValue[] result = RunLuau("local x = 5 return `a{`b{x}`}c`");
            Assert.AreEqual("ab5c", result[0].Read<string>());
        }

        [Test]
        public void CompoundOnCallResultIndex_EvaluatesKeyOnce()
        {
            string luau =
                "local calls = 0\n" +
                "local t = { 10 }\n" +
                "local function key()\n" +
                "\tcalls += 1\n" +
                "\treturn 1\n" +
                "end\n" +
                "t[key()] += 5\n" +
                "return calls, t[1]\n";
            LuaValue[] result = RunLuau(luau);
            Assert.AreEqual(1, result[0].Read<double>(), "Side-effecting key must run exactly once.");
            Assert.AreEqual(15, result[1].Read<double>());
        }

        [Test]
        public void StrippedTypesAndCasts_StillRun()
        {
            Assert.AreEqual(6, RunNumber("local x: number = 5 local y = x :: number return y + 1"));
        }

        [Test]
        public void LuauNumberLiterals_EvaluateCorrectly()
        {
            Assert.AreEqual(1005, RunNumber("return 1_000 + 0b101"));
        }

        [Test]
        public void GenericFunction_Unannotated_IsDownleveledAndRuns()
        {
            // WHY: no ':' annotation anywhere — only '<T>' — so the trigger scan must catch the
            // generic list, otherwise this slips through unrewritten and fails the 5.2 VM.
            Assert.AreEqual(42, RunNumber("local function identity<T>(x) return x end return identity(42)"));
        }

        [Test]
        public void IfExpression_MultiReturnInTail_TruncatesToOneValue()
        {
            string luau =
                "local function multi() return 10, 20, 30 end\n" +
                "return select(\"#\", if true then multi() else multi())\n";
            Assert.AreEqual(1, RunNumber(luau), "if-expression branches must truncate multi-returns to one value.");
        }

        [Test]
        public void IfExpression_MultiReturnInTail_KeepsFirstValue()
        {
            string luau =
                "local function multi() return 10, 20, 30 end\n" +
                "return (if true then multi() else multi())\n";
            Assert.AreEqual(10, RunNumber(luau));
        }

        [Test]
        public void CompoundOnDottedPath_EvaluatesObjectExactlyOnce()
        {
            string luau =
                "local reads = 0\n" +
                "local inner = { c = 10 }\n" +
                "local a = setmetatable({}, { __index = function(_, k) if k == \"b\" then reads = reads + 1 end return inner end })\n" +
                "a.b.c += 1\n" +
                "return reads, inner.c\n";
            LuaValue[] result = RunLuau(luau);
            Assert.AreEqual(1, result[0].Read<double>(), "'a.b' must be evaluated once, not twice.");
            Assert.AreEqual(11, result[1].Read<double>());
        }

        [Test]
        public void CompoundOnIndexedThenField_EvaluatesIndexExactlyOnce()
        {
            string luau =
                "local reads = 0\n" +
                "local inner = { v = 5 }\n" +
                "local a = setmetatable({}, { __index = function(_, key) reads = reads + 1 return inner end })\n" +
                "local k = \"anything\"\n" +
                "a[k].v += 1\n" +
                "return reads, inner.v\n";
            LuaValue[] result = RunLuau(luau);
            Assert.AreEqual(1, result[0].Read<double>(), "'a[k]' must be evaluated once, not twice.");
            Assert.AreEqual(6, result[1].Read<double>());
        }

        [Test]
        public void UnicodeEscape_AsciiCodePoint_DecodesToChar()
        {
            LuaValue[] result = RunLuau("return \"\\u{48}i\"");
            Assert.AreEqual("Hi", result[0].Read<string>());
        }

        [Test]
        public void UnicodeEscape_MultiByte_EmitsTwoUtf8Bytes()
        {
            Assert.AreEqual(2, RunNumber("return #\"\\u{E9}\""));
        }

        [Test]
        public void ZEscape_StripsItselfAndFollowingWhitespace()
        {
            LuaValue[] result = RunLuau("return \"a\\z   b\"");
            Assert.AreEqual("ab", result[0].Read<string>());
        }

        [Test]
        public void ExponentDigitSeparator_IsStripped()
        {
            Assert.AreEqual(1e10, RunNumber("return 1e1_0"));
        }

        [Test]
        public void PlainLua_RunsUnchanged()
        {
            DownlevelResult result = LuauDownleveler.Process(
                "local t = {} function t.add(n) return 1 + n end return t.add(2)");
            Assert.IsFalse(result.Changed);
            LuaState state = LuaState.Create();
            state.OpenBasicLibrary();
            LuaValue[] values = state.DoStringAsync(result.LuaSource).GetAwaiter().GetResult();
            Assert.AreEqual(3, values[0].Read<double>());
        }
    }
}
