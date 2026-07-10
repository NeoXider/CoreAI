using System;
using CoreAI.Infrastructure.Lua;
using CoreAI.Sandbox;
using MoonSharp.Interpreter;
using UnityEngine;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode
{
    /// <summary>
    /// EditMode coverage for Lua sandbox hardening via <see cref="SecureLuaEnvironment.StripRiskyGlobals"/>,
    /// <see cref="InstructionLimitDebugger"/> (steps / timeout), <see cref="LuaExecutionGuard"/>.
    /// </summary>
    [TestFixture]
    public sealed class SecureLuaSandboxEditModeTests
    {
        // ===================== StripRiskyGlobals (каждый глобал проверяется отдельно) =====================

        [Test]
        public void StripRiskyGlobals_IoRemoved()
        {
            SecureLuaEnvironment env = new();
            Script script = env.CreateScript(new LuaApiRegistry());

            DynValue val = script.DoString("return io");
            Assert.AreEqual(DataType.Nil, val.Type, "io должен быть вырезан из глобалов");
        }

        [Test]
        public void StripRiskyGlobals_OsRemoved()
        {
            SecureLuaEnvironment env = new();
            Script script = env.CreateScript(new LuaApiRegistry());

            DynValue val = script.DoString("return os");
            Assert.AreEqual(DataType.Nil, val.Type, "os должен быть вырезан (иначе os.exit / os.execute)");
        }

        [Test]
        public void StripRiskyGlobals_DebugRemoved()
        {
            SecureLuaEnvironment env = new();
            Script script = env.CreateScript(new LuaApiRegistry());

            DynValue val = script.DoString("return debug");
            Assert.AreEqual(DataType.Nil, val.Type, "debug должен быть вырезан (getinfo/traceback бьют изоляцию)");
        }

        [Test]
        public void StripRiskyGlobals_LoadRemoved()
        {
            SecureLuaEnvironment env = new();
            Script script = env.CreateScript(new LuaApiRegistry());

            DynValue val = script.DoString("return load");
            Assert.AreEqual(DataType.Nil, val.Type, "load позволяет eval произвольного кода");
        }

        [Test]
        public void StripRiskyGlobals_LoadfileRemoved()
        {
            SecureLuaEnvironment env = new();
            Script script = env.CreateScript(new LuaApiRegistry());

            DynValue val = script.DoString("return loadfile");
            Assert.AreEqual(DataType.Nil, val.Type, "loadfile даёт доступ к файловой системе");
        }

        [Test]
        public void StripRiskyGlobals_DofileRemoved()
        {
            SecureLuaEnvironment env = new();
            Script script = env.CreateScript(new LuaApiRegistry());

            DynValue val = script.DoString("return dofile");
            Assert.AreEqual(DataType.Nil, val.Type, "dofile = loadfile + exec, недопустим");
        }

        [Test]
        public void StripRiskyGlobals_RequireThrows()
        {
            // require в HardSandbox выбрасывает ScriptRuntimeException (поведение MoonSharp)
            SecureLuaEnvironment env = new();
            Script script = env.CreateScript(new LuaApiRegistry());

            Assert.Throws<ScriptRuntimeException>(() => script.DoString("return require('x')"));
        }

        // ===================== HardSandbox: вызов print/os.exit через метатаблицы =====================

        [Test]
        public void StripRiskyGlobals_GlobalTable_DoesNotExposeRiskyModules()
        {
            // Параноидальная проверка: даже через прямой доступ к _G (если он есть)
            // ни io, ни os, ни debug не должны быть доступны.
            SecureLuaEnvironment env = new();
            Script script = env.CreateScript(new LuaApiRegistry());

            DynValue v = script.DoString(
                "local leaks = {}\n" +
                "if io ~= nil then leaks[#leaks+1] = 'io' end\n" +
                "if os ~= nil then leaks[#leaks+1] = 'os' end\n" +
                "if debug ~= nil then leaks[#leaks+1] = 'debug' end\n" +
                "if load ~= nil then leaks[#leaks+1] = 'load' end\n" +
                "if loadfile ~= nil then leaks[#leaks+1] = 'loadfile' end\n" +
                "if dofile ~= nil then leaks[#leaks+1] = 'dofile' end\n" +
                "return table.concat(leaks, ',')");
            Assert.AreEqual(string.Empty, v.String,
                $"Обнаружена утечка рискованных глобалов: {v.String}");
        }

        // ===================== Escape vectors (string.dump / coroutine.close / collectgarbage / _G) =====================

        /// <summary>Returns true when the expression is unreachable: evaluates to nil or raises a Lua error.</summary>
        private static bool VectorIsBlocked(Script script, string luaExpression)
        {
            try
            {
                DynValue v = script.DoString("return " + luaExpression);
                return v.IsNil();
            }
            catch (ScriptRuntimeException)
            {
                return true;
            }
            catch (SyntaxErrorException)
            {
                return true;
            }
        }

        [Test]
        public void EscapeVector_StringDump_Blocked()
        {
            SecureLuaEnvironment env = new();
            Script script = env.CreateScript(new LuaApiRegistry());

            Assert.IsTrue(VectorIsBlocked(script, "string.dump"),
                "string.dump должен быть вырезан (утечка байткода)");
            Assert.IsTrue(VectorIsBlocked(script, "string.dump(function() end)"),
                "вызов string.dump должен падать");
        }

        [Test]
        public void EscapeVector_StringDump_NotReachableViaStringMetatable()
        {
            SecureLuaEnvironment env = new();
            Script script = env.CreateScript(new LuaApiRegistry());

            Assert.IsTrue(VectorIsBlocked(script, "('x').dump"),
                "string.dump не должен быть доступен через метатаблицу строк");
        }

        [Test]
        public void EscapeVector_CoroutineClose_AbsentOrHarmless()
        {
            SecureLuaEnvironment env = new();
            Script script = env.CreateScript(new LuaApiRegistry());

            DynValue close = script.DoString("return coroutine.close");
            if (close.IsNil())
            {
                Assert.Pass("coroutine.close отсутствует в MoonSharp — вектор закрыт");
            }

            // Если реализован — закрытие корутины не должно ронять хост или давать доступ к окружению.
            try
            {
                DynValue v = script.DoString(
                    "local co = coroutine.create(function() coroutine.yield() end)\n" +
                    "return coroutine.close(co)");
                Assert.AreNotEqual(DataType.Table, v.Type,
                    "coroutine.close не должен возвращать таблицы (потенциальная утечка окружения)");
            }
            catch (ScriptRuntimeException)
            {
                // Ошибка Lua — допустимое (безопасное) поведение.
            }
        }

        [Test]
        public void EscapeVector_CollectGarbage_Blocked()
        {
            SecureLuaEnvironment env = new();
            Script script = env.CreateScript(new LuaApiRegistry());

            Assert.IsTrue(VectorIsBlocked(script, "collectgarbage"),
                "collectgarbage должен быть вырезан (timing/heap oracle)");
            Assert.IsTrue(VectorIsBlocked(script, "collectgarbage('count')"),
                "вызов collectgarbage должен падать");
        }

        [Test]
        public void EscapeVector_GetMetatableOfString_Blocked()
        {
            SecureLuaEnvironment env = new();
            Script script = env.CreateScript(new LuaApiRegistry());

            Assert.IsTrue(VectorIsBlocked(script, "getmetatable('')"),
                "getmetatable('') не должен отдавать метатаблицу строк (доступ к __index)");
            Assert.IsTrue(VectorIsBlocked(script, "getmetatable('') and getmetatable('').__index"),
                "__index метатаблицы строк недоступен");
        }

        [Test]
        public void EscapeVector_RawgetTricks_Blocked()
        {
            SecureLuaEnvironment env = new();
            Script script = env.CreateScript(new LuaApiRegistry());

            Assert.IsTrue(VectorIsBlocked(script, "rawget(_G or {}, 'os')"),
                "rawget(_G, 'os') не должен возвращать os");
            Assert.IsTrue(VectorIsBlocked(script, "rawget(_G or {}, 'io')"),
                "rawget(_G, 'io') не должен возвращать io");
            Assert.IsTrue(VectorIsBlocked(script, "rawget(_G or {}, 'load')"),
                "rawget(_G, 'load') не должен возвращать load");
        }

        [Test]
        public void EscapeVector_GlobalsViaUnderscoreG_Blocked()
        {
            SecureLuaEnvironment env = new();
            Script script = env.CreateScript(new LuaApiRegistry());

            DynValue v = script.DoString(
                "local g = _G or _ENV\n" +
                "if g == nil then return '' end\n" +
                "local leaks = {}\n" +
                "for _, name in ipairs({'os','io','debug','load','loadfile','dofile','collectgarbage'}) do\n" +
                "    if g[name] ~= nil then leaks[#leaks+1] = name end\n" +
                "    local sd = g['string']\n" +
                "    if sd ~= nil and sd.dump ~= nil then leaks[#leaks+1] = 'string.dump' end\n" +
                "end\n" +
                "return table.concat(leaks, ',')");
            Assert.AreEqual(string.Empty, v.IsNil() ? string.Empty : v.String,
                $"Доступ через _G/_ENV не должен возвращать рискованные глобалы: {v}");
        }

        // ===================== Coroutine sandbox =====================

        [Test]
        public void CreateCoroutine_BasicExecution_Works()
        {
            SecureLuaEnvironment env = new();
            LuaApiRegistry reg = new();
            int reported = 0;
            reg.Register("report", new Action<double>(x => reported = (int)x));

            LuaCoroutineHandle handle = env.CreateCoroutine(reg, @"
                for i = 1, 3 do
                    report(i)
                    coroutine.yield()
                end
            ");

            Assert.IsTrue(handle.IsAlive);
            handle.Resume();
            Assert.AreEqual(1, reported);

            handle.Resume();
            Assert.AreEqual(2, reported);

            handle.Resume();
            Assert.AreEqual(3, reported);

            handle.Resume();
            Assert.IsFalse(handle.IsAlive, "После завершения тела корутины IsAlive == false");
        }

        [Test]
        public void CreateCoroutine_BudgetPerResume_Enforced()
        {
            SecureLuaEnvironment env = new();
            LuaApiRegistry reg = new();

            // Бюджет 100 инструкций на resume — цикл 1..10000 должен превысить лимит.
            LuaCoroutineHandle handle = env.CreateCoroutine(reg,
                "for i = 1, 10000 do local x = i * 2 end",
                100);

            Assert.Throws<ScriptRuntimeException>(() => handle.Resume(),
                "Resume с маленьким бюджетом инструкций должен вылетать по лимиту");
        }

        [Test]
        public void Kill_MarksHandleDisposed()
        {
            SecureLuaEnvironment env = new();
            LuaApiRegistry reg = new();

            LuaCoroutineHandle handle = env.CreateCoroutine(reg,
                "while true do coroutine.yield() end");

            handle.Resume();
            Assert.IsTrue(handle.IsAlive);

            handle.Kill();
            Assert.IsFalse(handle.IsAlive, "После Kill() IsAlive должен быть false");
            Assert.Throws<ObjectDisposedException>(() => handle.Resume(),
                "Resume после Kill бросает ObjectDisposedException");
        }

        // ===================== LuaExecutionGuard timeout =====================

        [Test]
        public void LuaExecutionGuard_TightTimeout_ThrowsOnInfiniteLoop()
        {
            SecureLuaEnvironment env = new();
            Script script = env.CreateScript(new LuaApiRegistry());
            LuaExecutionGuard guard = new(50, 1_000_000);

            ScriptRuntimeException ex = Assert.Throws<ScriptRuntimeException>(() =>
                env.RunChunk(script, "while true do end", guard));

            // Ожидаем либо timeout, либо (реже) лимит шагов — оба валидны.
            Assert.IsTrue(
                ex.Message.Contains("Lua exceeded") ||
                ex.Message.Contains("EXCEEDED_HARD_LIMIT_STEPS"),
                $"Ожидается срабатывание защиты по timeout/steps, получено: {ex.Message}");
        }

        [Test]
        public void LuaExecutionGuard_MaxSteps_Enforced()
        {
            SecureLuaEnvironment env = new();
            Script script = env.CreateScript(new LuaApiRegistry());
            LuaExecutionGuard guard = new(60_000, 100);

            Assert.Throws<ScriptRuntimeException>(() =>
                env.RunChunk(script, "for i = 1, 100000 do end", guard));
        }

        [Test]
        public void LuaExecutionGuard_FastCode_CompletesSuccessfully()
        {
            SecureLuaEnvironment env = new();
            LuaApiRegistry reg = new();
            reg.Register("mul", new Func<double, double, double>((a, b) => a * b));
            Script script = env.CreateScript(reg);
            LuaExecutionGuard guard = new(2000, 500_000);

            DynValue result = env.RunChunk(script, "return mul(6, 7)", guard);
            Assert.AreEqual(42, (int)result.Number);
        }

        [Test]
        public void LuaExecutionGuard_ThrowsIfNotFunction()
        {
            LuaExecutionGuard guard = new();
            Script script = new();

            // LoadString даёт chunk (функцию), а DynValue.NewNumber — не функцию.
            Assert.Throws<ArgumentException>(() => guard.Execute(script, DynValue.NewNumber(1)));
        }

        [Test]
        public void StripRiskyGlobals_PackageNil()
        {
            SecureLuaEnvironment env = new();
            Script script = env.CreateScript(new LuaApiRegistry());

            DynValue val = script.DoString("return package");
            Assert.AreEqual(DataType.Nil, val.Type, "package must be removed from the sandbox globals.");
        }

        [Test]
        public void EscapeVector_StringRep_StringCapAndMethodForm_WithSeparatorWorks()
        {
            SecureLuaEnvironment env = new();
            Script script = env.CreateScript(new LuaApiRegistry());

            // The cap throws before allocating: both rejections must be immediate script errors.
            Assert.Throws<ScriptRuntimeException>(() => script.DoString("return string.rep('a', 2000000)"));
            Assert.Throws<ScriptRuntimeException>(() => script.DoString("return string.rep('abc', 10000000)"));

            DynValue value = script.DoString("return string.rep('ab', 3)");
            Assert.AreEqual("ababab", value.String);

            value = script.DoString("return ('ab'):rep(3)");
            Assert.AreEqual("ababab", value.String);

            value = script.DoString("return ('ab'):rep(3, '-')");
            Assert.AreEqual("ab-ab-ab", value.String);
        }

        [Test]
        public void StripRiskyGlobals_PcallAbsent_AndCallLoopStillHitsInstructionLimit()
        {
            SecureLuaEnvironment env = new();
            Script script = env.CreateScript(new LuaApiRegistry());

            // Preset_HardSandbox excludes the ErrorHandling module, so pcall cannot be used to
            // swallow guard exceptions in the first place.
            DynValue pcall = script.DoString("return pcall");
            Assert.AreEqual(DataType.Nil, pcall.Type, "pcall must be absent from the hard sandbox.");

            LuaExecutionGuard guard = new(50, 100);
            ScriptRuntimeException ex = Assert.Throws<ScriptRuntimeException>(() =>
                env.RunChunk(script, "while true do (function() end)() end", guard));
            Assert.IsTrue(
                ex.Message.Contains("Lua exceeded") ||
                ex.Message.Contains("EXCEEDED_HARD_LIMIT_STEPS"),
                $"Expected hard limit message, got: {ex.Message}");
        }

        [Test]
        public void LuaExecutionGuard_DeepRecursion_ThrowsScriptError()
        {
            SecureLuaEnvironment env = new();
            Script script = env.CreateScript(new LuaApiRegistry());

            Assert.Throws<ScriptRuntimeException>(() =>
                env.RunChunk(script, "local function f(n) return f(n + 1) end\nreturn f(0)"));
        }

        [Test]
        public void CreateCoroutine_LifetimeBudget_EnforcedAfterResumes()
        {
            SecureLuaEnvironment env = new();
            LuaApiRegistry reg = new();

            LuaCoroutineHandle handle = env.CreateCoroutine(
                reg,
                "while true do coroutine.yield() end",
                10000,
                2);

            int resumes = 0;
            while (handle.IsAlive && resumes < 10)
            {
                handle.Resume();
                resumes++;
            }

            Assert.IsFalse(handle.IsAlive, "Coroutine should be auto-killed once lifetime budget is exhausted.");
            Assert.GreaterOrEqual(resumes, 1);
            Assert.Less(resumes, 10);
        }

        [Test]
        public void LuaTimeBindings_TimeSetScale_NaNThrows()
        {
            SecureLuaEnvironment env = new();
            LuaApiRegistry reg = new();
            new LuaTimeBindings().RegisterTimeApis(reg);
            Script script = env.CreateScript(reg);

            Assert.Throws<ScriptRuntimeException>(() => env.RunChunk(script, "time_set_scale(0/0)"));
        }

        // ===================== Allocation-bomb regression (F-08) =====================

        [Test]
        public void AllocationBomb_ConcatDoubling_ThrowsMemoryBudgetError()
        {
            SecureLuaEnvironment env = new();
            Script script = env.CreateScript(new LuaApiRegistry());

            // string.rep is capped at MaxStringRepLength (1MB), so the seed string itself is allowed.
            // Doubling it via plain concatenation (no library call site to intercept) must still be
            // caught by the per-instruction GC allocation budget before it reaches hundreds of MB.
            ScriptRuntimeException ex = Assert.Throws<ScriptRuntimeException>(() =>
                script.DoString(
                    "local s = string.rep('x', 1000000)\n" +
                    "for i = 1, 30 do s = s .. s end\n" +
                    "return s"));

            Assert.IsTrue(ex.Message.Contains("EXCEEDED_MEMORY_BUDGET"),
                $"Expected the allocation-bomb backstop to fire, got: {ex.Message}");
        }

        [Test]
        public void AllocationBomb_TableConcat_CapEnforced()
        {
            SecureLuaEnvironment env = new();
            Script script = env.CreateScript(new LuaApiRegistry());

            ScriptRuntimeException ex = Assert.Throws<ScriptRuntimeException>(() =>
                script.DoString(
                    "local t = {}\n" +
                    "local chunk = string.rep('x', 1000000)\n" +
                    "for i = 1, 5 do t[i] = chunk end\n" +
                    "return table.concat(t)"));

            Assert.IsTrue(ex.Message.Contains("table.concat"),
                $"Expected the table.concat cap to fire, got: {ex.Message}");
        }

        [Test]
        public void AllocationBomb_NormalHundredKbString_StillPasses()
        {
            SecureLuaEnvironment env = new();
            Script script = env.CreateScript(new LuaApiRegistry());

            DynValue result = script.DoString(
                "local s = string.rep('x', 100000)\n" +
                "s = s .. s\n" +
                "return #s");

            Assert.AreEqual(200000, (int)result.Number,
                "A normal, non-adversarial 100KB-class string script must not be blocked by the budget.");
        }

        [Test]
        public void LuaTimeBindings_TimeSetScale_ClampsToMax()
        {
            float originalScale = Time.timeScale;
            SecureLuaEnvironment env = new();
            LuaApiRegistry reg = new();
            new LuaTimeBindings().RegisterTimeApis(reg);
            Script script = env.CreateScript(reg);

            try
            {
                env.RunChunk(script, "time_set_scale(9999)");
                Assert.AreEqual(10f, Time.timeScale);
            }
            finally
            {
                Time.timeScale = originalScale;
            }
        }
    }
}
