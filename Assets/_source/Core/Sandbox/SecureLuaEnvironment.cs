using System;
using MoonSharp.Interpreter;

namespace CoreAI.Sandbox
{
    /// <summary>
    /// Фабрика песочницы MoonSharp: минимальные модули, без load/io/os (DGF_SPEC §8).
    /// </summary>
    public sealed class SecureLuaEnvironment
    {
        private static readonly CoreModules SandboxModules =
            CoreModules.Preset_HardSandbox;

        public Script CreateScript(LuaApiRegistry registry)
        {
            var script = new Script(SandboxModules);
            registry?.ApplyToGlobals(script.Globals);
            StripRiskyGlobals(script);
            return script;
        }

        private static void StripRiskyGlobals(Script script)
        {
            var g = script.Globals;
            void Remove(string name)
            {
                try
                {
                    g[name] = DynValue.Nil;
                }
                catch
                {
                    /* ignore missing */
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

        public DynValue RunChunk(Script script, string luaCode, LuaExecutionGuard guard = null)
        {
            var fn = script.LoadString(luaCode, codeFriendlyName: "sandbox_chunk");
            guard ??= new LuaExecutionGuard();
            return guard.Execute(script, fn);
        }
    }
}
