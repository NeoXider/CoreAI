using System;
using MoonSharp.Interpreter;

namespace CoreAI.Sandbox
{
    /// <summary>
    /// Фабрика песочницы MoonSharp: минимальные модули, без load/io/os (DGF_SPEC §8).
    /// </summary>
    public sealed class SecureLuaEnvironment
    {
        // Разрешаем Debug модуль при создании, чтобы настроить sethook (счетчик инструкций).
        // Позже модуль 'debug' удаляется из глобальной области видимости.
        private static readonly CoreModules SandboxModules =
            CoreModules.Preset_HardSandbox | CoreModules.Debug;

        /// <summary>Создать MoonSharp-скрипт с whitelist API из <paramref name="registry"/> и безопасными модулями.</summary>
        public Script CreateScript(LuaApiRegistry registry)
        {
            Script script = new(SandboxModules);
            registry?.ApplyToGlobals(script.Globals);
            StripRiskyGlobals(script);
            return script;
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
                    /* ignore missing */
                }
            }

            // Добавляем пуленепробиваемую защиту от пустых бесконечных циклов
            // через встроенный счетчик инструкций Lua. Отладчик MoonSharp может
            // пропускать пустые while true do end, а sethook срабатывает всегда.
            script.DoString(@"
                local steps = 0
                local maxSteps = 500000 -- Hard limit for sandbox
                debug.sethook(function()
                    steps = steps + 1000
                    if steps >= maxSteps then error('SecureLuaEnvironment: EXCEEDED_HARD_LIMIT_STEPS') end
                end, '', 1000)
            ");

            Remove("load");
            Remove("loadfile");
            Remove("dofile");
            Remove("require");
            Remove("io");
            Remove("os");
            Remove("debug");
        }

        /// <summary>Скомпилировать строку в чанк и выполнить под <paramref name="guard"/> (лимит времени).</summary>
        public DynValue RunChunk(Script script, string luaCode, LuaExecutionGuard guard = null)
        {
            DynValue fn = script.LoadString(luaCode, codeFriendlyName: "sandbox_chunk");
            guard ??= new LuaExecutionGuard();
            return guard.Execute(script, fn);
        }
    }
}