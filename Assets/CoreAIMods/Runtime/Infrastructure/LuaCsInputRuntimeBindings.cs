using System;
using System.Collections.Generic;
using CoreAI.Sandbox.LuaCs;
using CoreAI.Scripting;
using UnityEngine;

namespace CoreAI.Ai.LuaCs
{
    /// <summary>
    /// Lua-CSharp counterpart of <see cref="CoreAI.Infrastructure.Lua.CoreAiInputLuaRuntimeBindings"/>.
    /// </summary>
    public sealed class LuaCsInputRuntimeBindings
    {
        private static readonly Dictionary<string, KeyCode> KeyCache = new(StringComparer.OrdinalIgnoreCase)
        {
            ["left"] = KeyCode.LeftArrow,
            ["right"] = KeyCode.RightArrow,
            ["up"] = KeyCode.UpArrow,
            ["down"] = KeyCode.DownArrow,
            ["0"] = KeyCode.Alpha0,
            ["1"] = KeyCode.Alpha1,
            ["2"] = KeyCode.Alpha2,
            ["3"] = KeyCode.Alpha3,
            ["4"] = KeyCode.Alpha4,
            ["5"] = KeyCode.Alpha5,
            ["6"] = KeyCode.Alpha6,
            ["7"] = KeyCode.Alpha7,
            ["8"] = KeyCode.Alpha8,
            ["9"] = KeyCode.Alpha9
        };

        public void Register(IScriptFunctionRegistry registry, LuaCapabilities capabilities)
        {
            if ((capabilities & LuaCapabilities.Gameplay) == 0)
            {
                return;
            }

            RegisterGameplayApis(registry);
        }

        public void RegisterGameplayApis(IScriptFunctionRegistry registry)
        {
            registry.Register("input_key", new Func<string, bool>(name => Input.GetKey(ParseKey(name))));
            registry.Register("input_key_down", new Func<string, bool>(name => Input.GetKeyDown(ParseKey(name))));
            registry.Register("input_key_up", new Func<string, bool>(name => Input.GetKeyUp(ParseKey(name))));
            registry.Register("input_mouse_button", new Func<int, bool>(ValidMouseButton(Input.GetMouseButton)));
            registry.Register("input_mouse_down", new Func<int, bool>(ValidMouseButton(Input.GetMouseButtonDown)));
            registry.Register("input_mouse_x", new Func<double>(() => Input.mousePosition.x));
            registry.Register("input_mouse_y", new Func<double>(() => Input.mousePosition.y));
            registry.Register("input_axis", new Func<string, double>(GetAxisSafe));
        }

        private static Func<int, bool> ValidMouseButton(Func<int, bool> query)
        {
            return button => button is >= 0 and <= 2 && query(button);
        }

        private static KeyCode ParseKey(string name)
        {
            string key = (name ?? "").Trim();
            if (key.Length == 0)
            {
                return KeyCode.None;
            }

            lock (KeyCache)
            {
                if (KeyCache.TryGetValue(key, out KeyCode cached))
                {
                    return cached;
                }

                KeyCode parsed = Enum.TryParse(key, true, out KeyCode result) ? result : KeyCode.None;
                KeyCache[key] = parsed;
                return parsed;
            }
        }

        private static double GetAxisSafe(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return 0d;
            }

            try
            {
                return Input.GetAxis(name.Trim());
            }
            catch (ArgumentException)
            {
                return 0d;
            }
        }
    }
}
