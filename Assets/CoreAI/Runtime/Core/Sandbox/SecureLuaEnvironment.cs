using System;
using MoonSharp.Interpreter;

namespace CoreAI.Sandbox
{
    /// <summary>
    /// Фабрика песочницы MoonSharp: минимальные модули, без load/io/os (DGF_SPEC §8).
    /// <para>
    /// Поддерживает два режима:
    /// <list type="bullet">
    ///   <item><see cref="CreateScript"/> + <see cref="RunChunk"/> — one-shot команды с жёстким лимитом инструкций.</item>
    ///   <item><see cref="CreateCoroutine"/> — долгоживущие скрипты с ресетируемым бюджетом на каждый кадр.</item>
    /// </list>
    /// </para>
    /// </summary>
    public sealed class SecureLuaEnvironment
    {
        private static readonly CoreModules SandboxModules =
            CoreModules.Preset_HardSandbox;

        /// <summary>Жёсткий лимит инструкций для one-shot скриптов (500K).</summary>
        public const int OneShotHardLimitSteps = 500_000;

        /// <summary>Создать MoonSharp-скрипт с whitelist API из <paramref name="registry"/> и безопасными модулями.
        /// Для one-shot выполнения через <see cref="RunChunk"/>.</summary>
        public Script CreateScript(LuaApiRegistry registry)
        {
            Script script = new(SandboxModules);
            registry?.ApplyToGlobals(script.Globals);
            
            // Назначаем отладчик для отслеживания шагов
            var debugger = new InstructionLimitDebugger(OneShotHardLimitSteps, timeoutMs: 2000);
            script.AttachDebugger(debugger);

            StripRiskyGlobals(script);
            return script;
        }

        /// <summary>Скомпилировать строку в чанк и выполнить под <paramref name="guard"/> (лимит времени).</summary>
        public DynValue RunChunk(Script script, string luaCode, LuaExecutionGuard guard = null)
        {
            DynValue fn = script.LoadString(luaCode, codeFriendlyName: "sandbox_chunk");
            guard ??= new LuaExecutionGuard();
            return guard.Execute(script, fn);
        }

        /// <summary>
        /// Создать долгоживущую корутину из Lua-кода с ресетируемым бюджетом на каждый resume.
        /// </summary>
        public LuaCoroutineHandle CreateCoroutine(
            LuaApiRegistry registry,
            string luaCode,
            int budgetPerResume = LuaCoroutineHandle.DefaultBudgetPerResume)
        {
            Script script = new(SandboxModules);
            registry?.ApplyToGlobals(script.Globals);

            var debugger = new InstructionLimitDebugger(budgetPerResume, timeoutMs: 500);
            script.AttachDebugger(debugger);

            StripRiskyGlobals(script);

            DynValue fn = script.LoadString(luaCode, codeFriendlyName: "sandbox_coroutine");
            DynValue coroutine = script.CreateCoroutine(fn);

            return new LuaCoroutineHandle(script, coroutine, debugger, budgetPerResume);
        }

        private static void StripRiskyGlobals(Script script)
        {
            Table g = script.Globals;

            void Remove(string name)
            {
                try
                {
                    g[name] = DynValue.Nil;
                }
                catch { }
            }

            Remove("load");
            Remove("loadfile");
            Remove("dofile");
            Remove("require");
            Remove("io");
            Remove("os");
            Remove("debug");
        }
    }
}