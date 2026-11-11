using System;
using System.Collections.Generic;
using CoreAI.Mods.Rbx.Datatypes;
using CoreAI.Mods.Rbx.Instances;

namespace CoreAI.Mods.Rbx.Agents
{
    /// <summary>Which input counts as the player taking control back. Plain data.</summary>
    public sealed class RbxPlayerInputOptions
    {
        /// <summary>
        /// Roblox <c>Enum.KeyCode</c> values of the keys and buttons that count: by default the bundled control mod's
        /// movement and jump keys (W, A, S, D, the arrows, Space, Left Shift, the D-pad, gamepad A). Keys that open
        /// the chat or a menu are not here, so the player can type <c>/autopilot off</c> or talk to a villager while
        /// the autopilot drives.
        /// </summary>
        public IList<int> KeyCodes { get; set; } = new List<int>
        {
            119, 97, 115, 100, 273, 274, 275, 276, 32, 304, 1002, 1012, 1013, 1014, 1015
        };

        /// <summary>Mouse buttons that count while the pointer is over the world (0 left, 1 right, 2 middle); by default the left one.</summary>
        public IList<int> MouseButtons { get; set; } = new List<int> { 0 };

        /// <summary>The left stick counts past this deflection (the control mod's dead zone).</summary>
        public double ThumbstickDeadZone { get; set; } = 0.2d;
    }

    /// <summary>
    /// Answers "is the player giving the game real input right now" from the world's input source, honouring the
    /// focus rules: while a text field holds the keyboard (the chat, the Hub) keys do not count, and while the
    /// pointer is over UI mouse buttons do not count, exactly the input <c>UserInputService</c> marks
    /// <c>gameProcessedEvent = true</c>.
    /// </summary>
    public sealed class RbxPlayerInputWatcher
    {
        private readonly RbxPlayerInputOptions _options;

        /// <summary>Creates the watcher; null options use the defaults.</summary>
        public RbxPlayerInputWatcher(RbxPlayerInputOptions options = null)
        {
            _options = options ?? new RbxPlayerInputOptions();
        }

        /// <summary>True when the player gives the world <paramref name="game"/> real input now.</summary>
        public bool HasPlayerInput(RbxDataModel game)
        {
            RbxUserInputService input = RbxCharacterQueries.UserInput(game);
            return input != null && HasPlayerInput(input.InputSource, input.FocusState);
        }

        /// <summary>True when <paramref name="source"/> reports real input that <paramref name="focus"/> leaves to the game.</summary>
        public bool HasPlayerInput(IInputSource source, IInputFocusState focus)
        {
            if (source == null)
            {
                return false;
            }

            try
            {
                bool keysToGame = focus == null || !focus.IsGameInputCaptured;
                bool pointerToGame = keysToGame && (focus == null || !focus.IsPointerOverUi);
                if (keysToGame && AnyKey(source))
                {
                    return true;
                }

                if (pointerToGame && AnyMouseButton(source))
                {
                    return true;
                }

                return keysToGame && source.TryReadThumbstick(1, out RbxVector2 stick) &&
                       stick.Magnitude >= _options.ThumbstickDeadZone;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private bool AnyKey(IInputSource source)
        {
            if (_options.KeyCodes == null)
            {
                return false;
            }

            foreach (int key in _options.KeyCodes)
            {
                if (source.IsKeyCodeDown(key))
                {
                    return true;
                }
            }

            return false;
        }

        private bool AnyMouseButton(IInputSource source)
        {
            if (_options.MouseButtons == null)
            {
                return false;
            }

            foreach (int button in _options.MouseButtons)
            {
                if (source.IsMouseButtonDown(button))
                {
                    return true;
                }
            }

            return false;
        }
    }
}
