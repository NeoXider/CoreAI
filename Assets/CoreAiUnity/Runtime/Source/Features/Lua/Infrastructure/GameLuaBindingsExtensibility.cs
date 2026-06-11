#if COREAI_HAS_MOONSHARP && !COREAI_NO_LUA
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
        private sealed class Entry
        {
            public IGameLuaRuntimeBindings Bindings;
            public LuaCapabilities RequiredCapabilities;
        }

        private static readonly List<Entry> Additional = new();

        /// <summary>
        /// Registers an extension binding set. <paramref name="requiredCapabilities"/> gates
        /// exposure: the extension is only registered into scripts whose granted tier includes
        /// every required flag (default <see cref="LuaCapabilities.All"/> — full-trust extension,
        /// hidden from restricted scripts).
        /// </summary>
        public static void Register(
            IGameLuaRuntimeBindings bindings,
            LuaCapabilities requiredCapabilities = LuaCapabilities.All)
        {
            if (bindings == null)
            {
                return;
            }

            lock (Additional)
            {
                for (int i = 0; i < Additional.Count; i++)
                {
                    if (ReferenceEquals(Additional[i].Bindings, bindings))
                    {
                        Additional[i].RequiredCapabilities = requiredCapabilities;
                        return;
                    }
                }

                Additional.Add(new Entry
                {
                    Bindings = bindings,
                    RequiredCapabilities = requiredCapabilities
                });
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
                for (int i = Additional.Count - 1; i >= 0; i--)
                {
                    if (ReferenceEquals(Additional[i].Bindings, bindings))
                    {
                        Additional.RemoveAt(i);
                    }
                }
            }
        }

        internal static void RegisterAll(LuaApiRegistry registry, LuaCapabilities grantedCapabilities)
        {
            lock (Additional)
            {
                for (int i = 0; i < Additional.Count; i++)
                {
                    Entry entry = Additional[i];
                    if (entry?.Bindings == null)
                    {
                        continue;
                    }

                    if ((grantedCapabilities & entry.RequiredCapabilities) == entry.RequiredCapabilities)
                    {
                        entry.Bindings.RegisterGameplayApis(registry);
                    }
                }
            }
        }
    }
}
#endif