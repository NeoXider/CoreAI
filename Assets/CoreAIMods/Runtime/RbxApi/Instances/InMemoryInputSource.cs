using System.Collections.Generic;
using CoreAI.Mods.Rbx.Datatypes;

namespace CoreAI.Mods.Rbx.Instances
{
    /// <summary>
    /// Headless <see cref="IInputSource"/> for EditMode tests and engine-free worlds: tests drive
    /// the state with <see cref="PressKey"/>/<see cref="ReleaseKey"/>/<see cref="SetMouseButton"/>/
    /// <see cref="SetMouseLocation"/> and the service's per-frame diff produces the same
    /// InputBegan/InputEnded stream a real device would.
    /// </summary>
    public sealed class InMemoryInputSource : IInputSource
    {
        private readonly HashSet<int> _pressedKeys = new HashSet<int>();
        private readonly bool[] _mouseButtons = new bool[3];
        private RbxVector2 _mouseLocation;

        public void PressKey(int keyCodeValue)
        {
            _pressedKeys.Add(keyCodeValue);
        }

        public void ReleaseKey(int keyCodeValue)
        {
            _pressedKeys.Remove(keyCodeValue);
        }

        public void SetMouseButton(int button, bool down)
        {
            if (button >= 0 && button < _mouseButtons.Length)
            {
                _mouseButtons[button] = down;
            }
        }

        public void SetMouseLocation(float x, float y)
        {
            _mouseLocation = new RbxVector2(x, y);
        }

        public void CollectPressedKeyCodes(ICollection<int> buffer)
        {
            foreach (int keyCode in _pressedKeys)
            {
                buffer.Add(keyCode);
            }
        }

        public bool IsKeyCodeDown(int keyCodeValue)
        {
            return _pressedKeys.Contains(keyCodeValue);
        }

        public bool IsMouseButtonDown(int button)
        {
            return button >= 0 && button < _mouseButtons.Length && _mouseButtons[button];
        }

        public RbxVector2 GetMouseLocation()
        {
            return _mouseLocation;
        }
    }
}
