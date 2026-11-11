using System;
using UnityEngine;
#if ENABLE_INPUT_SYSTEM && (COREAI_HAS_INPUT_SYSTEM || UNITY_6000_7_OR_NEWER)
using UnityEngine.InputSystem;
#endif

namespace CoreAI.ChatOverlay
{
    /// <summary>Polled keyboard of the in-world chat; a test passes a fake.</summary>
    public interface IChatOverlayKeyboard
    {
        /// <summary>True in the frame <paramref name="key"/> went down.</summary>
        bool WasPressedThisFrame(KeyCode key);

        /// <summary>True while Ctrl, Alt or Command is held; chat keys are ignored then.</summary>
        bool IsModifierHeld { get; }
    }

    /// <summary>
    /// Decides whether the input may open right now. The default allows it; a host registers one that says no
    /// while another text field (a Hub page, the chat panel) has keyboard focus.
    /// </summary>
    public interface IChatOverlayOpenGate
    {
        /// <summary>False keeps the open keys from opening the input this frame.</summary>
        bool IsOpenAllowed();
    }

    /// <summary>What an open key asks for.</summary>
    public enum ChatOverlayOpenRequest
    {
        /// <summary>No open key went down.</summary>
        None = 0,

        /// <summary>Open the input empty.</summary>
        Chat = 1,

        /// <summary>Open the input with the command prefix typed.</summary>
        Command = 2
    }

    /// <summary>Key decisions of the overlay, kept apart from the polling so they are testable.</summary>
    public static class ChatOverlayHotkeys
    {
        /// <summary>
        /// The open request for this frame: a command key wins over a chat key; nothing while a modifier is held.
        /// <paramref name="pressedKey"/> is the key that matched.
        /// </summary>
        public static ChatOverlayOpenRequest Resolve(IChatOverlayKeyboard keyboard, CoreAiChatOverlaySettings settings,
            out KeyCode pressedKey)
        {
            pressedKey = KeyCode.None;
            if (keyboard == null || settings == null || keyboard.IsModifierHeld)
            {
                return ChatOverlayOpenRequest.None;
            }

            if (TryFindPressed(keyboard, settings.OpenCommandKeys, out pressedKey))
            {
                return ChatOverlayOpenRequest.Command;
            }

            if (TryFindPressed(keyboard, settings.OpenChatKeys, out pressedKey))
            {
                return ChatOverlayOpenRequest.Chat;
            }

            return ChatOverlayOpenRequest.None;
        }

        /// <summary>The character a key types, for swallowing the opening keystroke; <c>'\0'</c> when none.</summary>
        public static char CharacterFor(KeyCode key)
        {
            if (key >= KeyCode.A && key <= KeyCode.Z)
            {
                return (char)('a' + (key - KeyCode.A));
            }

            if (key >= KeyCode.Alpha0 && key <= KeyCode.Alpha9)
            {
                return (char)('0' + (key - KeyCode.Alpha0));
            }

            if (key >= KeyCode.Keypad0 && key <= KeyCode.Keypad9)
            {
                return (char)('0' + (key - KeyCode.Keypad0));
            }

            switch (key)
            {
                case KeyCode.Return:
                case KeyCode.KeypadEnter:
                    return '\n';
                case KeyCode.Slash:
                case KeyCode.KeypadDivide:
                    return '/';
                case KeyCode.Backslash:
                    return '\\';
                case KeyCode.BackQuote:
                    return '`';
                case KeyCode.Period:
                case KeyCode.KeypadPeriod:
                    return '.';
                case KeyCode.Comma:
                    return ',';
                case KeyCode.Semicolon:
                    return ';';
                case KeyCode.Quote:
                    return '\'';
                case KeyCode.Minus:
                case KeyCode.KeypadMinus:
                    return '-';
                case KeyCode.Equals:
                    return '=';
                case KeyCode.Space:
                    return ' ';
                default:
                    return '\0';
            }
        }

#if ENABLE_INPUT_SYSTEM && (COREAI_HAS_INPUT_SYSTEM || UNITY_6000_7_OR_NEWER)
        /// <summary>The Input System key for a legacy <see cref="KeyCode"/>; false when there is no equivalent.</summary>
        public static bool TryMapToInputSystemKey(KeyCode keyCode, out Key key)
        {
            key = Key.None;
            if (keyCode >= KeyCode.Alpha0 && keyCode <= KeyCode.Alpha9)
            {
                key = keyCode == KeyCode.Alpha0 ? Key.Digit0 : Key.Digit1 + (keyCode - KeyCode.Alpha1);
                return true;
            }

            if (keyCode >= KeyCode.Keypad0 && keyCode <= KeyCode.Keypad9)
            {
                key = Key.Numpad0 + (keyCode - KeyCode.Keypad0);
                return true;
            }

            switch (keyCode)
            {
                case KeyCode.None:
                    return false;
                case KeyCode.Return:
                    key = Key.Enter;
                    return true;
                case KeyCode.KeypadEnter:
                    key = Key.NumpadEnter;
                    return true;
                case KeyCode.KeypadDivide:
                    key = Key.NumpadDivide;
                    return true;
                case KeyCode.KeypadMultiply:
                    key = Key.NumpadMultiply;
                    return true;
                case KeyCode.KeypadMinus:
                    key = Key.NumpadMinus;
                    return true;
                case KeyCode.KeypadPlus:
                    key = Key.NumpadPlus;
                    return true;
                case KeyCode.KeypadPeriod:
                    key = Key.NumpadPeriod;
                    return true;
                case KeyCode.LeftControl:
                    key = Key.LeftCtrl;
                    return true;
                case KeyCode.RightControl:
                    key = Key.RightCtrl;
                    return true;
                case KeyCode.LeftCommand:
                    key = Key.LeftMeta;
                    return true;
                case KeyCode.RightCommand:
                    key = Key.RightMeta;
                    return true;
            }

            return Enum.TryParse(keyCode.ToString(), true, out key) && key != Key.None;
        }
#endif

        private static bool TryFindPressed(IChatOverlayKeyboard keyboard, KeyCode[] keys, out KeyCode pressed)
        {
            pressed = KeyCode.None;
            if (keys == null)
            {
                return false;
            }

            foreach (KeyCode key in keys)
            {
                if (key != KeyCode.None && keyboard.WasPressedThisFrame(key))
                {
                    pressed = key;
                    return true;
                }
            }

            return false;
        }
    }

    /// <summary>
    /// Default <see cref="IChatOverlayKeyboard"/>: the Input System keyboard when the project uses it, the legacy
    /// input manager otherwise (both are polled from the player loop, so it works in a WebGL player).
    /// </summary>
    public sealed class UnityChatOverlayKeyboard : IChatOverlayKeyboard
    {
        /// <inheritdoc />
        public bool IsModifierHeld
        {
            get
            {
#if ENABLE_INPUT_SYSTEM && (COREAI_HAS_INPUT_SYSTEM || UNITY_6000_7_OR_NEWER)
                Keyboard keyboard = Keyboard.current;
                if (keyboard != null)
                {
                    return keyboard.ctrlKey.isPressed || keyboard.altKey.isPressed ||
                           keyboard.leftMetaKey.isPressed || keyboard.rightMetaKey.isPressed;
                }
#endif
#if ENABLE_LEGACY_INPUT_MANAGER
                try
                {
                    return Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl) ||
                           Input.GetKey(KeyCode.LeftAlt) || Input.GetKey(KeyCode.RightAlt) ||
                           Input.GetKey(KeyCode.LeftCommand) || Input.GetKey(KeyCode.RightCommand);
                }
                catch (InvalidOperationException)
                {
                    return false;
                }
#else
                return false;
#endif
            }
        }

        /// <inheritdoc />
        public bool WasPressedThisFrame(KeyCode key)
        {
            if (key == KeyCode.None)
            {
                return false;
            }

#if ENABLE_INPUT_SYSTEM && (COREAI_HAS_INPUT_SYSTEM || UNITY_6000_7_OR_NEWER)
            Keyboard keyboard = Keyboard.current;
            if (keyboard != null && ChatOverlayHotkeys.TryMapToInputSystemKey(key, out Key mapped))
            {
                return keyboard[mapped].wasPressedThisFrame;
            }
#endif
#if ENABLE_LEGACY_INPUT_MANAGER
            try
            {
                return Input.GetKeyDown(key);
            }
            catch (InvalidOperationException)
            {
                // WHY: the legacy manager throws while the project is switched to the Input System only.
                return false;
            }
#else
            return false;
#endif
        }
    }
}
