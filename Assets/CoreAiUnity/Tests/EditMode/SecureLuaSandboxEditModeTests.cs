using System;
using CoreAI.Sandbox;
using MoonSharp.Interpreter;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode
{
    /// <summary>
    /// EditMode-тесты Lua-sandbox защит: <see cref="SecureLuaEnvironment.StripRiskyGlobals"/>,
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
                budgetPerResume: 100);

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
            LuaExecutionGuard guard = new(timeoutMs: 50, maxSteps: 1_000_000);

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
            LuaExecutionGuard guard = new(timeoutMs: 60_000, maxSteps: 100);

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
            LuaExecutionGuard guard = new(timeoutMs: 2000, maxSteps: 500_000);

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
    }
}
