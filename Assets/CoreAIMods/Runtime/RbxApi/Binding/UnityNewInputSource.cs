using System;
using System.Collections.Generic;
using CoreAI.Mods.Rbx.Datatypes;
using CoreAI.Mods.Rbx.Instances;
using UnityEngine;
#if COREAI_HAS_INPUT_SYSTEM
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;
#endif

namespace CoreAI.Mods.Rbx.Binding
{
    /// <summary>
    /// New-Input-System backend of <see cref="IInputSource"/>: polls
    /// <c>Keyboard.current</c>/<c>Mouse.current</c>/<c>Gamepad.current</c> and maps device state
    /// to Roblox <c>Enum.KeyCode</c> values (gamepad buttons at 1000+). Mouse coordinates are
    /// converted to the Roblox screen convention (pixels, top-left origin). WebGL: keyboard,
    /// mouse and gamepad work; touch/sensor devices are limited there, so this source polls none
    /// of them (touch arrives with a later input slice). Null device guards keep every poll safe
    /// when a device class is absent (headless, server, WebGL without a gamepad).
    /// </summary>
    // TODO: com.unity.inputsystem may be removed — keep UserInputService behind IInputSource so
    // this backend can be replaced (legacy Input or a custom poll) without touching the Lua API.
    public sealed class UnityNewInputSource : IInputSource
    {
#if COREAI_HAS_INPUT_SYSTEM
        private static readonly (Key Key, int KeyCode)[] KeyMap = BuildKeyMap();
        private static readonly Dictionary<int, Key> KeyByKeyCode = BuildKeyByKeyCode();
        private static readonly (int KeyCode, Func<Gamepad, ButtonControl> Button)[] GamepadMap =
        {
            (1000, pad => pad.buttonWest), (1001, pad => pad.buttonNorth),
            (1002, pad => pad.buttonSouth), (1003, pad => pad.buttonEast),
            (1004, pad => pad.rightShoulder), (1005, pad => pad.leftShoulder),
            (1006, pad => pad.rightTrigger), (1007, pad => pad.leftTrigger),
            (1008, pad => pad.rightStickButton), (1009, pad => pad.leftStickButton),
            (1010, pad => pad.startButton), (1011, pad => pad.selectButton),
            (1012, pad => pad.dpad.left), (1013, pad => pad.dpad.right),
            (1014, pad => pad.dpad.up), (1015, pad => pad.dpad.down)
        };

        public void CollectPressedKeyCodes(ICollection<int> buffer)
        {
            Keyboard keyboard = Keyboard.current;
            if (keyboard != null)
            {
                foreach ((Key key, int keyCode) in KeyMap)
                {
                    if (keyboard[key].isPressed)
                    {
                        buffer.Add(keyCode);
                    }
                }
            }

            Gamepad gamepad = Gamepad.current;
            if (gamepad != null)
            {
                foreach ((int keyCode, Func<Gamepad, ButtonControl> button) in GamepadMap)
                {
                    if (button(gamepad).isPressed)
                    {
                        buffer.Add(keyCode);
                    }
                }
            }
        }

        public bool IsKeyCodeDown(int keyCodeValue)
        {
            if (keyCodeValue >= 1000)
            {
                Gamepad gamepad = Gamepad.current;
                if (gamepad == null)
                {
                    return false;
                }

                foreach ((int keyCode, Func<Gamepad, ButtonControl> button) in GamepadMap)
                {
                    if (keyCode == keyCodeValue)
                    {
                        return button(gamepad).isPressed;
                    }
                }

                return false;
            }

            Keyboard keyboard = Keyboard.current;
            return keyboard != null
                   && KeyByKeyCode.TryGetValue(keyCodeValue, out Key key)
                   && keyboard[key].isPressed;
        }

        public bool IsMouseButtonDown(int button)
        {
            Mouse mouse = Mouse.current;
            if (mouse == null)
            {
                return false;
            }

            switch (button)
            {
                case 0: return mouse.leftButton.isPressed;
                case 1: return mouse.rightButton.isPressed;
                case 2: return mouse.middleButton.isPressed;
                default: return false;
            }
        }

        public RbxVector2 GetMouseLocation()
        {
            Mouse mouse = Mouse.current;
            if (mouse == null)
            {
                return RbxVector2.Zero;
            }

            Vector2 position = mouse.position.ReadValue();
            // WHY: Unity's mouse origin is bottom-left; Roblox screen space is top-left.
            return new RbxVector2(position.x, Screen.height - position.y);
        }

        private static (Key, int)[] BuildKeyMap()
        {
            var map = new List<(Key, int)>
            {
                (Key.Space, 32), (Key.Enter, 13), (Key.Tab, 9), (Key.Backquote, 96),
                (Key.Quote, 39), (Key.Semicolon, 59), (Key.Comma, 44), (Key.Period, 46),
                (Key.Slash, 47), (Key.Backslash, 92), (Key.LeftBracket, 91),
                (Key.RightBracket, 93), (Key.Minus, 45), (Key.Equals, 61),
                (Key.LeftShift, 304), (Key.RightShift, 303), (Key.LeftAlt, 308),
                (Key.RightAlt, 307), (Key.LeftCtrl, 306), (Key.RightCtrl, 305),
                (Key.LeftMeta, 310), (Key.RightMeta, 309), (Key.ContextMenu, 319),
                (Key.Escape, 27), (Key.LeftArrow, 276), (Key.RightArrow, 275),
                (Key.UpArrow, 273), (Key.DownArrow, 274), (Key.Backspace, 8),
                (Key.PageDown, 281), (Key.PageUp, 280), (Key.Home, 278), (Key.End, 279),
                (Key.Insert, 277), (Key.Delete, 127), (Key.CapsLock, 301), (Key.NumLock, 300),
                (Key.PrintScreen, 316), (Key.ScrollLock, 302), (Key.Pause, 19),
                (Key.NumpadEnter, 271), (Key.NumpadDivide, 267), (Key.NumpadMultiply, 268),
                (Key.NumpadPlus, 270), (Key.NumpadMinus, 269), (Key.NumpadPeriod, 266),
                (Key.NumpadEquals, 272)
            };
            for (int i = 0; i < 26; i++)
            {
                map.Add((Key.A + i, 97 + i));
            }

            map.Add((Key.Digit0, 48));
            for (int i = 1; i <= 9; i++)
            {
                map.Add((Key.Digit1 + (i - 1), 48 + i));
            }

            for (int i = 0; i < 10; i++)
            {
                map.Add((Key.Numpad0 + i, 256 + i));
            }

            for (int i = 0; i < 12; i++)
            {
                map.Add((Key.F1 + i, 282 + i));
            }

            return map.ToArray();
        }

        private static Dictionary<int, Key> BuildKeyByKeyCode()
        {
            var byKeyCode = new Dictionary<int, Key>();
            foreach ((Key key, int keyCode) in KeyMap)
            {
                byKeyCode[keyCode] = key;
            }

            return byKeyCode;
        }
#else
        // WHY: compile-safe fallback for a project without com.unity.inputsystem — the Lua surface
        // stays intact (loads, connects, polls) and simply reports no input.
        public void CollectPressedKeyCodes(ICollection<int> buffer)
        {
        }

        public bool IsKeyCodeDown(int keyCodeValue) => false;

        public bool IsMouseButtonDown(int button) => false;

        public RbxVector2 GetMouseLocation() => RbxVector2.Zero;
#endif
    }
}
