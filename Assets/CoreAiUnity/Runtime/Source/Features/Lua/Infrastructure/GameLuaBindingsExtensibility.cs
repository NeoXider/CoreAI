using System.Collections.Generic;
using CoreAI.Ai;
using CoreAI.Sandbox;

namespace CoreAI.Infrastructure.Lua
{
    /// <summary>
    /// Game Lua Bindings Extensibility component used by CoreAI.
    /// </summary>
    public static class GameLuaBindingsExtensibility
    {
        private static readonly List<IGameLuaRuntimeBindings> Additional = new();

        /// <summary>Registers a new value or callback with the target runtime registry.</summary>
        public static void Register(IGameLuaRuntimeBindings bindings)
        {
            if (bindings == null)
            {
                return;
            }

            lock (Additional)
            {
                if (!Additional.Contains(bindings))
                {
                    Additional.Add(bindings);
                }
            }
        }

        public static void Unregister(IGameLuaRuntimeBindings bindings)
        {
            if (bindings == null)
            {
                return;
            }

            lock (Additional)
            {
                Additional.Remove(bindings);
            }
        }

        internal static void RegisterAll(LuaApiRegistry registry)
        {
            lock (Additional)
            {
                for (int i = 0; i < Additional.Count; i++)
                {
                    Additional[i]?.RegisterGameplayApis(registry);
                }
            }
        }
    }
}
