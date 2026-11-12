using System.Collections.Generic;

namespace CoreAI.RuntimeUi
{
    /// <summary>
    /// Keeps the keystroke that opened a text field out of it. A key pressed to open a chat (T, Enter, the slash)
    /// reaches the newly focused field as a key down and as a typed character, in the frame it was pressed or the
    /// next one. Armed with the keys held when focus was asked for, it swallows, until the frame after that, a key
    /// down of one of those keys and the first typed character, whatever the keyboard layout made of it (T is a
    /// Cyrillic letter on a Russian layout, the slash key a full stop). Engine-free: key codes are integers
    /// (Unity's <c>KeyCode</c> and Roblox's <c>Enum.KeyCode</c> share the values for every key a chat opens with).
    /// </summary>
    public sealed class OpeningKeystrokeFilter
    {
        private readonly List<int> _keys = new();
        private int _untilFrame = -1;
        private bool _characterPending;

        /// <summary>True while a keystroke may still be swallowed.</summary>
        public bool IsArmed => _untilFrame >= 0 && (_keys.Count > 0 || _characterPending);

        /// <summary>
        /// Arms the filter in <paramref name="frame"/> for <paramref name="heldKeyCodes"/>; an empty or null list
        /// disarms it, since nothing was being pressed.
        /// </summary>
        public void Arm(IReadOnlyList<int> heldKeyCodes, int frame)
        {
            _keys.Clear();
            if (heldKeyCodes != null)
            {
                for (int i = 0; i < heldKeyCodes.Count; i++)
                {
                    if (heldKeyCodes[i] != 0)
                    {
                        _keys.Add(heldKeyCodes[i]);
                    }
                }
            }

            _characterPending = _keys.Count > 0;
            _untilFrame = _keys.Count > 0 ? frame + 1 : -1;
        }

        /// <summary>Disarms the filter.</summary>
        public void Disarm()
        {
            _keys.Clear();
            _characterPending = false;
            _untilFrame = -1;
        }

        /// <summary>
        /// True when the key event (<paramref name="keyCode"/> 0 for a character-only event, <paramref name="character"/>
        /// '\0' for a key without one) arriving in <paramref name="frame"/> is the opening keystroke and must not
        /// reach the field.
        /// </summary>
        public bool ShouldSwallow(int keyCode, char character, int frame)
        {
            if (_untilFrame < 0)
            {
                return false;
            }

            if (frame > _untilFrame)
            {
                Disarm();
                return false;
            }

            if (keyCode != 0 && _keys.Contains(keyCode))
            {
                if (character != '\0')
                {
                    _characterPending = false;
                }

                return true;
            }

            if (character == '\0' || !_characterPending)
            {
                return false;
            }

            _characterPending = false;
            return true;
        }
    }
}
