using System;
using MoonSharp.Interpreter;

namespace CoreAI.Sandbox
{
    /// <summary>
    /// Creates MoonSharp Lua runtimes with a restricted global surface and instruction guards.
    /// </summary>
    public sealed class SecureLuaEnvironment
    {
        private static readonly CoreModules SandboxModules =
            CoreModules.Preset_HardSandbox | CoreModules.Coroutine;

        /// <summary>Maximum instruction budget for one-shot Lua script execution.</summary>
        public const int OneShotHardLimitSteps = 500_000;

        /// <summary>Creates a secured MoonSharp script and registers the allowed Lua APIs.</summary>
        public Script CreateScript(LuaApiRegistry registry)
        {
            Script script = new(SandboxModules);
            registry?.ApplyToGlobals(script.Globals);

            // Attach a debugger before loading host code so instruction limits cover all execution.
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
        /// Creates a sandboxed coroutine handle for Lua code that yields across Unity frames.
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
