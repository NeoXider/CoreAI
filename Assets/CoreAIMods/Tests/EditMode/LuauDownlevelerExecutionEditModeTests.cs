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

        static LuaValue[] RunLuau(string luau)
        {
            DownlevelResult result = LuauDownleveler.Process(luau, "exec");
            var sb = new System.Text.StringBuilder();
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
            return state.DoStringAsync(result.LuaSource).GetAwaiter().GetResult();
        }

        static double RunNumber(string luau)
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
