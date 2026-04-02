using System;
using CoreAI.Sandbox;

namespace CoreAI.Ai
{
    /// <summary>
    /// Регистрация игровых/отладочных API для Lua (report, add, …).
    /// </summary>
    public interface IGameLuaRuntimeBindings
    {
        void RegisterGameplayApis(LuaApiRegistry registry);
    }

    /// <summary>Без логгера Unity — для юнит-тестов ядра.</summary>
    public sealed class CoreDefaultLuaRuntimeBindings : IGameLuaRuntimeBindings
    {
        public void RegisterGameplayApis(LuaApiRegistry registry)
        {
            registry.Register("report", new Action<string>(_ => { }));
            registry.Register("add", new Func<double, double, double>((a, b) => a + b));
        }
    }
}
