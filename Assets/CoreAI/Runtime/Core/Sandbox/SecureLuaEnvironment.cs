using System;
using MoonSharp.Interpreter;

namespace CoreAI.Sandbox
{
    /// <summary>
    /// Provides secure lua environment functionality.
    /// </summary>
    public sealed class SecureLuaEnvironment
    {
        private static readonly CoreModules SandboxModules =
            CoreModules.Preset_HardSandbox | CoreModules.Coroutine;

        /// <summary>One shot hard limit steps.</summary>
        public const int OneShotHardLimitSteps = 500_000;

        /// <summary>Creates a secured MoonSharp script and registers the allowed Lua APIs.</summary>
        /// Provides API usage information.
        public Script CreateScript(LuaApiRegistry registry)
        {
            Script script = new(SandboxModules);
            registry?.ApplyToGlobals(script.Globals);

            /* Implementation note in English. */
            InstructionLimitDebugger debugger = new(OneShotHardLimitSteps, 2000);
            script.AttachDebugger(debugger);

            StripRiskyGlobals(script);
            return script;
        }

        /// <summary>Runs Lua code inside a secured script with the optional execution guard.</summary>
        public DynValue RunChunk(Script script, string luaCode, LuaExecutionGuard guard = null)
        {
            DynValue fn = script.LoadString(luaCode, codeFriendlyName: "sandbox_chunk");
            guard ??= new LuaExecutionGuard();
            return guard.Execute(script, fn);
        }

        /// <summary>
/// Executes CreateCoroutine API operation.
        /// </summary>
        public LuaCoroutineHandle CreateCoroutine(
            LuaApiRegistry registry,
            string luaCode,
            int budgetPerResume = LuaCoroutineHandle.DefaultBudgetPerResume)
        {
            Script script = new(SandboxModules);
            registry?.ApplyToGlobals(script.Globals);

            InstructionLimitDebugger debugger = new(budgetPerResume, 500);
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
                catch
                {
                }
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
