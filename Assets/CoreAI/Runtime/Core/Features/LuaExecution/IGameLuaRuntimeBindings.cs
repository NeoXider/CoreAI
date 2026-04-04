using System;
using CoreAI.Sandbox;

namespace CoreAI.Ai
{
    /// <summary>
    /// Регистрация игровых/отладочных API для Lua (report, add, …).
    /// </summary>
    public interface IGameLuaRuntimeBindings
    {
        /// <summary>Зарегистрировать делегаты, доступные из Lua (имена → C# callback).</summary>
        void RegisterGameplayApis(LuaApiRegistry registry);
    }

    /// <summary>Без логгера Unity — для юнит-тестов ядра.</summary>
    public sealed class CoreDefaultLuaRuntimeBindings : IGameLuaRuntimeBindings
    {
        /// <inheritdoc />
        public void RegisterGameplayApis(LuaApiRegistry registry)
        {
            registry.Register("report", new Action<string>(_ => { }));
            registry.Register("add", new Func<double, double, double>((a, b) => a + b));
        }
    }
}