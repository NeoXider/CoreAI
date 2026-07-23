using System.Collections.Generic;
using CoreAI.Mods.Rbx.Datatypes;

namespace CoreAI.Mods.Rbx.Instances
{
    /// <summary>
    /// Roblox UserInputService (MVP1 input slice, pulled forward from MVP10 for mini-game
    /// controls): InputBegan/InputEnded/InputChanged signals plus the poll surface
    /// (IsKeyDown/GetKeysPressed/GetMouseLocation/MouseBehavior). State comes from the
    /// engine-free <see cref="IInputSource"/> seam; <see cref="Step"/> diffs successive polls and
    /// fires the signals, called once per frame by the host composition.
    /// </summary>
    public sealed class RbxUserInputService : RbxInstance
    {
        // WHY: gamepad buttons live in Enum.KeyCode at value 1000+; keyboard keys sit below it.
        private const int GamepadKeyCodeBase = 1000;

        // WHY: Roblox delivers input events in device order, so both the current keys (source order)
        // and the previous frame's keys are kept as ordered lists — iterating a HashSet would make
        // the per-frame Began/Ended order (and any test over it) nondeterministic.
        private readonly List<int> _previousKeys = new List<int>();
        private readonly HashSet<int> _currentKeys = new HashSet<int>();
        private readonly List<int> _pollBuffer = new List<int>();
        private readonly bool[] _previousMouseButtons = new bool[3];

        // WHY: interned so StepMouseButtons never concatenates a string per button change, and the
        // gameProcessedEvent flag is a cached box so a per-frame fire never boxes the bool.
        private static readonly string[] MouseButtonTypeNames =
            { "MouseButton1", "MouseButton2", "MouseButton3" };
        private static readonly object GameNotProcessed = false;

        private RbxEnumRegistry _enums;
        private RbxVector2 _previousMouseLocation;
        private bool _hasPreviousMouseLocation;

        internal RbxUserInputService(ClassDescriptor descriptor)
            : base(descriptor)
        {
            Name = "UserInputService";
        }

        /// <summary>Fires (InputObject, gameProcessedEvent) when a key/button/touch begins.</summary>
        public RbxScriptSignal InputBegan { get; } =
            new RbxScriptSignal("UserInputService.InputBegan", supportsDispatch: true);

        /// <summary>Fires (InputObject, gameProcessedEvent) when a key/button/touch ends.</summary>
        public RbxScriptSignal InputEnded { get; } =
            new RbxScriptSignal("UserInputService.InputEnded", supportsDispatch: true);

        /// <summary>Fires (InputObject, gameProcessedEvent) when an input changes (mouse movement).</summary>
        public RbxScriptSignal InputChanged { get; } =
            new RbxScriptSignal("UserInputService.InputChanged", supportsDispatch: true);

        /// <summary>Device state seam; headless in-memory by default, the composition attaches the
        /// engine-backed source once (like the camera rig).</summary>
        public IInputSource InputSource { get; private set; } = new InMemoryInputSource();

        /// <summary>Enum.MouseBehavior item; state-only in MVP1 (Default until assigned).
        /// TODO: route LockCenter/LockCurrentPosition to the host cursor when the pointer-lock
        /// slice lands.</summary>
        public RbxEnumItem MouseBehavior { get; set; }

        /// <summary>Attaches the engine-backed input source; resolved once at composition.</summary>
        public void AttachInputSource(IInputSource source)
        {
            if (source != null)
            {
                InputSource = source;
            }
        }

        /// <summary>Binds the world's enum registry so fired InputObjects carry interned
        /// Enum.KeyCode/UserInputType/UserInputState items (Lua == works like Roblox).</summary>
        public void AttachEnums(RbxEnumRegistry enums)
        {
            _enums = enums;
            if (MouseBehavior == null)
            {
                MouseBehavior = FindItemByName("MouseBehavior", "Default");
            }
        }

        /// <summary>UserInputService:IsKeyDown(keyCode) over the source's current state.</summary>
        public bool IsKeyDown(int keyCodeValue)
        {
            ThrowIfDestroyed("IsKeyDown");
            return InputSource.IsKeyCodeDown(keyCodeValue);
        }

        /// <summary>UserInputService:GetKeysPressed() — one InputObject per held key.</summary>
        public IReadOnlyList<RbxInputObject> GetKeysPressed()
        {
            ThrowIfDestroyed("GetKeysPressed");
            // WHY: own buffer, NOT the shared _pollBuffer — a Lua handler may call GetKeysPressed()
            // from inside an InputBegan/InputEnded handler while StepKeys is mid-foreach over
            // _pollBuffer; clearing/refilling the shared list there throws "collection modified" out
            // of the pump. GetKeysPressed already allocates its result list, so a local int buffer is
            // no extra hot-path cost (this is a mod-chosen poll, not the per-frame pump).
            var pressed = new List<RbxInputObject>();
            var buffer = new List<int>();
            InputSource.CollectPressedKeyCodes(buffer);
            foreach (int keyCode in buffer)
            {
                // WHY: Roblox GetKeysPressed() returns keyboard keys only; gamepad buttons are
                // excluded (they surface via GetGamepadState in later MVPs).
                if (keyCode < GamepadKeyCodeBase)
                {
                    pressed.Add(MakeKeyInput(keyCode, "Begin"));
                }
            }

            return pressed;
        }

        /// <summary>UserInputService:GetMouseLocation() — pixels, top-left origin.</summary>
        public RbxVector2 GetMouseLocation()
        {
            ThrowIfDestroyed("GetMouseLocation");
            return InputSource.GetMouseLocation();
        }

        /// <summary>
        /// Per-frame poll→diff→fire pump: newly held keys/buttons fire InputBegan, released ones
        /// fire InputEnded, mouse movement fires InputChanged with the position delta. The host
        /// calls this once per frame before mod dispatch so handlers see this frame's events.
        /// </summary>
        // TODO: MVP2 — the general signal scheduler replaces this pump with deferred dispatch.
        public void Step()
        {
            if (IsDestroyed)
            {
                return;
            }

            StepKeys();
            StepMouseButtons();
            StepMouseMovement();
        }

        private void StepKeys()
        {
            _pollBuffer.Clear();
            InputSource.CollectPressedKeyCodes(_pollBuffer);
            _currentKeys.Clear();
            foreach (int keyCode in _pollBuffer)
            {
                _currentKeys.Add(keyCode);
            }

            // WHY: fire Began in source (device) order; the HashSet is only the O(1) membership test.
            foreach (int keyCode in _pollBuffer)
            {
                if (!_previousKeys.Contains(keyCode))
                {
                    InputBegan.Fire(MakeKeyInput(keyCode, "Begin"), GameNotProcessed);
                }
            }

            foreach (int keyCode in _previousKeys)
            {
                if (!_currentKeys.Contains(keyCode))
                {
                    InputEnded.Fire(MakeKeyInput(keyCode, "End"), GameNotProcessed);
                }
            }

            _previousKeys.Clear();
            _previousKeys.AddRange(_pollBuffer);
        }

        private void StepMouseButtons()
        {
            for (int button = 0; button < _previousMouseButtons.Length; button++)
            {
                bool down = InputSource.IsMouseButtonDown(button);
                if (down == _previousMouseButtons[button])
                {
                    continue;
                }

                _previousMouseButtons[button] = down;
                RbxVector2 location = InputSource.GetMouseLocation();
                var input = new RbxInputObject(
                    FindItemByName("KeyCode", "Unknown"),
                    FindItemByName("UserInputType", MouseButtonTypeNames[button]),
                    FindItemByName("UserInputState", down ? "Begin" : "End"),
                    new RbxVector3(location.X, location.Y, 0f),
                    RbxVector3.Zero);
                (down ? InputBegan : InputEnded).Fire(input, GameNotProcessed);
            }
        }

        private void StepMouseMovement()
        {
            RbxVector2 location = InputSource.GetMouseLocation();
            if (_hasPreviousMouseLocation
                && (location.X != _previousMouseLocation.X || location.Y != _previousMouseLocation.Y))
            {
                var input = new RbxInputObject(
                    FindItemByName("KeyCode", "Unknown"),
                    FindItemByName("UserInputType", "MouseMovement"),
                    FindItemByName("UserInputState", "Change"),
                    new RbxVector3(location.X, location.Y, 0f),
                    new RbxVector3(
                        location.X - _previousMouseLocation.X,
                        location.Y - _previousMouseLocation.Y, 0f));
                InputChanged.Fire(input, GameNotProcessed);
            }

            _previousMouseLocation = location;
            _hasPreviousMouseLocation = true;
        }

        private RbxInputObject MakeKeyInput(int keyCodeValue, string state)
        {
            // WHY: gamepad buttons live in the same Enum.KeyCode space at value 1000+, so the
            // source reports one flat key set and the UserInputType is derived from the value.
            RbxEnumItem inputType = keyCodeValue >= GamepadKeyCodeBase
                ? FindItemByName("UserInputType", "Gamepad1")
                : FindItemByName("UserInputType", "Keyboard");
            return new RbxInputObject(
                FindItemByValue("KeyCode", keyCodeValue),
                inputType,
                FindItemByName("UserInputState", state),
                RbxVector3.Zero,
                RbxVector3.Zero);
        }

        private RbxEnumItem FindItemByName(string enumName, string itemName)
        {
            return _enums != null
                   && _enums.TryGet(enumName, out RbxEnum rbxEnum)
                   && rbxEnum.TryGetItem(itemName, out RbxEnumItem item)
                ? item
                : null;
        }

        private RbxEnumItem FindItemByValue(string enumName, int value)
        {
            return _enums != null
                   && _enums.TryGet(enumName, out RbxEnum rbxEnum)
                   && rbxEnum.TryGetItemByValue(value, out RbxEnumItem item)
                ? item
                : null;
        }
    }
}
