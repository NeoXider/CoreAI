#if COREAI_HAS_MOONSHARP && !COREAI_NO_LUA
using System;
using System.Collections.Generic;
using CoreAI.Ai;
using CoreAI.Sandbox;
using UnityEngine;

namespace CoreAI.Infrastructure.Lua
{
    /// <summary>
    /// Gameplay-tier Lua input bindings: lets mods and scripts read the keyboard and mouse so game
    /// logic (movement, piece steering, click handling) can live entirely in Lua. Read-only —
    /// nothing here can synthesize input or reach outside <see cref="UnityEngine.Input"/>.
    /// <para>
    /// API: <c>input_key(name)</c> / <c>input_key_down(name)</c> / <c>input_key_up(name)</c> (held /
    /// pressed this frame / released this frame), <c>input_mouse_button(i)</c> /
    /// <c>input_mouse_down(i)</c> (0 = left, 1 = right, 2 = middle), <c>input_mouse_x()</c> /
    /// <c>input_mouse_y()</c> (screen pixels, origin bottom-left), <c>input_axis(name)</c>
    /// (e.g. 'Horizontal'/'Vertical', 0 when the axis is undefined).
    /// </para>
    /// <para>
    /// Key names are <see cref="KeyCode"/> spellings, case-insensitive ('a', 'space', 'return',
    /// 'leftarrow'), plus aliases: 'left'/'right'/'up'/'down' for the arrows and '0'..'9' for the
    /// top-row digits. Note that frame-edge checks (<c>input_key_down</c>/<c>_up</c>) are true for
    /// one frame only; a mod timer slower than the frame rate can miss them — poll held state
    /// (<c>input_key</c>) from timers, or use a hooks_on('tick') handler for edges.
    /// </para>
    /// </summary>
    public sealed class CoreAiInputLuaRuntimeBindings : IGameLuaRuntimeBindings
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

        public void RegisterGameplayApis(LuaApiRegistry registry)
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
                // Axis not defined in the Input Manager — an undefined axis reads as centered.
                return 0d;
            }
        }
    }
}
#endif