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
        private readonly HashSet<int> _pressedKeys = new();
        private readonly bool[] _mouseButtons = new bool[3];
        private RbxVector2 _mouseLocation;
        private RbxVector2 _pendingDelta;
        private bool _hasPendingDelta;
        private float _pendingWheel;

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

        /// <summary>Queues a device mouse delta (pixels, y down) that the next poll consumes.</summary>
        public void SetMouseDelta(float x, float y)
        {
            _pendingDelta = new RbxVector2(x, y);
            _hasPendingDelta = true;
        }

        /// <summary>Queues wheel movement (positive forward) that the next poll consumes.</summary>
        public void ScrollWheel(float amount)
        {
            _pendingWheel += amount;
        }

        /// <summary>Holds a thumbstick (1 left, 2 right) at a position, each axis in [-1, 1], y up.</summary>
        public void SetThumbstick(int stick, float x, float y)
        {
            if (stick == 1 || stick == 2)
            {
                _thumbsticks[stick - 1] = new RbxVector2(x, y);
            }
        }

        /// <inheritdoc />
        public bool TryReadThumbstick(int stick, out RbxVector2 position)
        {
            if (stick == 1 || stick == 2)
            {
                position = _thumbsticks[stick - 1];
                return true;
            }

            position = RbxVector2.Zero;
            return false;
        }

        private readonly RbxVector2[] _thumbsticks = new RbxVector2[2];

        /// <summary>Focus state the service reads; tests assign an <see cref="InputFocusState"/>.</summary>
        public IInputFocusState FocusState { get; set; }

        /// <summary>Cursor port the service drives; tests assign an <see cref="InMemoryCursorPort"/>.</summary>
        public IRbxCursorPort CursorPort { get; set; }

        /// <inheritdoc />
        public bool TryReadMouseDelta(out RbxVector2 delta)
        {
            delta = _hasPendingDelta ? _pendingDelta : RbxVector2.Zero;
            bool had = _hasPendingDelta;
            _hasPendingDelta = false;
            _pendingDelta = RbxVector2.Zero;
            return had;
        }

        /// <inheritdoc />
        public float ReadMouseWheel()
        {
            float wheel = _pendingWheel;
            _pendingWheel = 0f;
            return wheel;
        }
    }
}
