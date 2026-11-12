using System;
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
        private readonly List<int> _previousKeys = new();
        private readonly HashSet<int> _currentKeys = new();
        private readonly List<int> _pollBuffer = new();
        private readonly bool[] _previousMouseButtons = new bool[3];

        // WHY: interned so StepMouseButtons never concatenates a string per button change, and the
        // gameProcessedEvent flag is a cached box so a per-frame fire never boxes the bool.
        private static readonly string[] MouseButtonTypeNames =
            { "MouseButton1", "MouseButton2", "MouseButton3" };

        private static readonly object GameNotProcessed = false;
        private static readonly object GameProcessed = true;

        /// <summary>Largest <see cref="MouseDeltaSensitivity"/> the mirror allows.</summary>
        public const double MaximumMouseDeltaSensitivity = 10d;

        private RbxEnumRegistry _enums;
        private RbxEnumItem _mouseBehavior;
        private RbxVector2 _previousMouseLocation;
        private bool _hasPreviousMouseLocation;
        private IInputFocusState _focusState;
        private IRbxCursorPort _cursorPort;
        private double _mouseDeltaSensitivity = 1d;
        private object _processedThisStep = GameNotProcessed;
        private object _pointerProcessedThisStep = GameNotProcessed;
        private readonly RbxVector2[] _thumbsticks = new RbxVector2[2];
        private bool _wasCaptured;
        private Func<RbxInstance> _focusedTextBoxSource;
        private RbxInstance _wasFocusedTextBox;

        private static readonly string[] ThumbstickKeyCodeNames = { "Thumbstick1", "Thumbstick2" };

        internal RbxUserInputService(ClassDescriptor descriptor)
            : base(descriptor)
        {
            Name = "UserInputService";
        }

        /// <summary>Fires (InputObject, gameProcessedEvent) when a key/button/touch begins.</summary>
        public RbxScriptSignal InputBegan { get; } =
            new("UserInputService.InputBegan");

        /// <summary>Fires (InputObject, gameProcessedEvent) when a key/button/touch ends.</summary>
        public RbxScriptSignal InputEnded { get; } =
            new("UserInputService.InputEnded");

        /// <summary>Fires (InputObject, gameProcessedEvent) when an input changes (mouse movement).</summary>
        public RbxScriptSignal InputChanged { get; } =
            new("UserInputService.InputChanged");

        /// <summary>
        /// Mirror <c>UserInputService.TextBoxFocused(textBoxThatFocused)</c>: fires with the GUI <c>TextBox</c> that
        /// took the keyboard, or with nil when one of CoreAI's own fields (the chat, the Hub) took it.
        /// </summary>
        public RbxScriptSignal TextBoxFocused { get; } =
            new("UserInputService.TextBoxFocused");

        /// <summary>
        /// Mirror <c>UserInputService.TextBoxFocusReleased(textboxReleased)</c>: fires with the GUI <c>TextBox</c>
        /// that gave the keyboard back (nil once it is destroyed), or with nil for one of CoreAI's own fields.
        /// </summary>
        public RbxScriptSignal TextBoxFocusReleased { get; } =
            new("UserInputService.TextBoxFocusReleased");

        /// <summary>Device state seam; headless in-memory by default, the composition attaches the
        /// engine-backed source once (like the camera rig).</summary>
        public IInputSource InputSource { get; private set; } = new InMemoryInputSource();

        /// <summary>Enum.MouseBehavior item (Default until assigned). A change goes to the host
        /// <see cref="CursorPort"/>, which locks or frees the pointer, and fires
        /// <c>Changed("MouseBehavior")</c> and its property signal; assigning the item it already
        /// holds fires nothing.</summary>
        public RbxEnumItem MouseBehavior
        {
            get => _mouseBehavior;
            set
            {
                if (ReferenceEquals(_mouseBehavior, value))
                {
                    return;
                }

                _mouseBehavior = value;
                ApplyMouseBehaviorToCursor();
                NotifyPropertyChanged(nameof(MouseBehavior));
            }
        }

        /// <summary>The requested lock, as the engine-free enum the cursor port takes.</summary>
        public RbxMouseLock RequestedMouseLock =>
            _mouseBehavior == null ? RbxMouseLock.Default : (RbxMouseLock)_mouseBehavior.Value;

        /// <summary>
        /// Mirror <c>UserInputService.MouseDeltaSensitivity</c>: scales every reported mouse delta,
        /// clamped to [0, 10], default 1. A change fires <c>Changed("MouseDeltaSensitivity")</c>.
        /// </summary>
        public double MouseDeltaSensitivity
        {
            get => _mouseDeltaSensitivity;
            set
            {
                double clamped = double.IsNaN(value)
                    ? _mouseDeltaSensitivity
                    : Math.Max(0d, Math.Min(MaximumMouseDeltaSensitivity, value));
                if (clamped.Equals(_mouseDeltaSensitivity))
                {
                    return;
                }

                _mouseDeltaSensitivity = clamped;
                NotifyPropertyChanged(nameof(MouseDeltaSensitivity));
            }
        }

        /// <summary>The focus state in effect: the attached one, else the input source's own.</summary>
        public IInputFocusState FocusState => _focusState ?? InputSource.FocusState;

        /// <summary>The cursor port in effect: the attached one, else the input source's own.</summary>
        public IRbxCursorPort CursorPort => _cursorPort ?? InputSource.CursorPort;

        /// <summary>True while a host surface owns the input, so events carry gameProcessedEvent = true.</summary>
        public bool IsGameInputCaptured => FocusState?.IsGameInputCaptured ?? false;

        /// <summary>
        /// True while mouse input belongs to host UI: a surface owns the input or the pointer is over a
        /// panel. Mouse events then carry gameProcessedEvent = true, and the default camera neither zooms
        /// nor starts a right-drag.
        /// </summary>
        public bool IsPointerInputProcessed =>
            IsGameInputCaptured || (FocusState?.IsPointerOverUi ?? false);

        /// <summary>The left (1) or right (2) thumbstick as read by the last <see cref="Step"/>, each axis in [-1, 1], y up.</summary>
        public RbxVector2 GetThumbstick(int stick)
        {
            return stick == 1 ? _thumbsticks[0] : stick == 2 ? _thumbsticks[1] : RbxVector2.Zero;
        }

        /// <summary>
        /// Mirror <c>UserInputService:GetFocusedTextBox()</c>: the GUI <c>TextBox</c> holding the keyboard, else
        /// nil. CoreAI's own text fields are not script-visible TextBoxes, so it is nil while one of them has the
        /// keyboard; read <see cref="IsGameInputCaptured"/> from C#, or <c>gameProcessedEvent</c> from a script.
        /// </summary>
        public RbxInstance GetFocusedTextBox()
        {
            ThrowIfDestroyed("GetFocusedTextBox");
            return ReadFocusedTextBox();
        }

        /// <summary>
        /// Attaches the source of the focused GUI TextBox (the Roblox GUI presenter of this world); null detaches it.
        /// </summary>
        public void AttachFocusedTextBoxSource(Func<RbxInstance> source)
        {
            _focusedTextBoxSource = source;
        }

        private RbxInstance ReadFocusedTextBox()
        {
            RbxInstance box = _focusedTextBoxSource?.Invoke();
            return box != null && !box.IsDestroyed ? box : null;
        }

        /// <summary>
        /// True while the pointer is locked: a lock was requested and the host cursor granted it (a
        /// headless world with no cursor port grants every request).
        /// </summary>
        public bool IsPointerLocked =>
            RequestedMouseLock != RbxMouseLock.Default && (CursorPort?.IsPointerLocked ?? true);

        /// <summary>Unscaled device mouse delta of the last <see cref="Step"/>, pixels, y down.</summary>
        public RbxVector2 LastFrameMouseDelta { get; private set; }

        /// <summary>Wheel movement of the last <see cref="Step"/>: positive forward, 0 none.</summary>
        public float LastFrameWheel { get; private set; }

        /// <summary>
        /// Mirror <c>UserInputService:GetMouseDelta()</c>: the last frame's mouse movement in pixels,
        /// scaled by <see cref="MouseDeltaSensitivity"/>, and zero unless the pointer is locked.
        /// </summary>
        public RbxVector2 GetMouseDelta()
        {
            ThrowIfDestroyed("GetMouseDelta");
            return IsPointerLocked ? Scale(LastFrameMouseDelta) : RbxVector2.Zero;
        }

        /// <summary>Attaches the engine-backed input source; resolved once at composition.</summary>
        public void AttachInputSource(IInputSource source)
        {
            if (source != null)
            {
                InputSource = source;
                ApplyMouseBehaviorToCursor();
            }
        }

        /// <summary>Attaches a focus state that takes precedence over the input source's own.</summary>
        public void AttachFocusState(IInputFocusState focusState)
        {
            _focusState = focusState;
        }

        /// <summary>Attaches a cursor port that takes precedence over the input source's own, and
        /// applies the current <see cref="MouseBehavior"/> to it.</summary>
        public void AttachCursorPort(IRbxCursorPort cursorPort)
        {
            _cursorPort = cursorPort;
            ApplyMouseBehaviorToCursor();
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
            // WHY: own buffer, not the shared _pollBuffer — a Lua handler may call GetKeysPressed()
            // from inside an InputBegan/InputEnded handler while StepKeys is mid-foreach over it,
            // which would throw "collection modified"; this path already allocates, so it's free.
            List<RbxInputObject> pressed = new();
            List<int> buffer = new();
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
        /// calls this once per frame; handlers run at the scheduler's deferred signal drain.
        /// </summary>
        public void Step()
        {
            if (IsDestroyed)
            {
                return;
            }

            bool captured = IsGameInputCaptured;
            _processedThisStep = captured ? GameProcessed : GameNotProcessed;
            _pointerProcessedThisStep = IsPointerInputProcessed ? GameProcessed : GameNotProcessed;
            // WHY: a TextBox that lost focus since the last step was released by a key (Enter, Escape) the box
            // took, which this step reports as newly pressed; it must stay processed, or an "open the chat on
            // Enter" handler reopens the box that Enter has just closed.
            bool textBoxReleased = _wasFocusedTextBox != null && ReadFocusedTextBox() == null;
            StepTextFocus(captured);
            StepKeys(textBoxReleased ? GameProcessed : _processedThisStep);
            StepMouseButtons();
            StepMouseMovement();
            StepMouseWheel();
            StepThumbsticks();
        }

        private void StepTextFocus(bool captured)
        {
            // WHY a focused GUI TextBox is told apart from the host fields: it captures the keyboard too, and
            // scripts get it as the argument; the nil events belong to CoreAI's chat and Hub fields alone.
            RbxInstance box = ReadFocusedTextBox();
            if (!ReferenceEquals(box, _wasFocusedTextBox))
            {
                RbxInstance released = _wasFocusedTextBox;
                _wasFocusedTextBox = box;
                if (released != null && TextBoxFocusReleased.HasConnections)
                {
                    TextBoxFocusReleased.Fire(released.IsDestroyed ? null : released);
                }

                if (box != null && TextBoxFocused.HasConnections)
                {
                    TextBoxFocused.Fire(box);
                }
            }

            bool hostCaptured = captured && box == null;
            if (hostCaptured == _wasCaptured)
            {
                return;
            }

            _wasCaptured = hostCaptured;
            RbxScriptSignal signal = hostCaptured ? TextBoxFocused : TextBoxFocusReleased;
            if (signal.HasConnections)
            {
                signal.Fire((object)null);
            }
        }

        private void StepThumbsticks()
        {
            for (int stick = 0; stick < _thumbsticks.Length; stick++)
            {
                RbxVector2 position = InputSource.TryReadThumbstick(stick + 1, out RbxVector2 read)
                    ? ClampStick(read)
                    : RbxVector2.Zero;
                RbxVector2 previous = _thumbsticks[stick];
                if (position == previous)
                {
                    continue;
                }

                _thumbsticks[stick] = position;
                if (!InputChanged.HasConnections)
                {
                    continue;
                }

                RbxInputObject input = new(
                    FindItemByName("KeyCode", ThumbstickKeyCodeNames[stick]),
                    FindItemByName("UserInputType", "Gamepad1"),
                    FindItemByName("UserInputState", "Change"),
                    new RbxVector3(position.X, position.Y, 0f),
                    new RbxVector3(position.X - previous.X, position.Y - previous.Y, 0f));
                InputChanged.Fire(input, _processedThisStep);
            }
        }

        private static RbxVector2 ClampStick(RbxVector2 value)
        {
            float x = float.IsNaN(value.X) ? 0f : Math.Max(-1f, Math.Min(1f, value.X));
            float y = float.IsNaN(value.Y) ? 0f : Math.Max(-1f, Math.Min(1f, value.Y));
            return new RbxVector2(x, y);
        }

        private void StepKeys(object processed)
        {
            _pollBuffer.Clear();
            InputSource.CollectPressedKeyCodes(_pollBuffer);
            _currentKeys.Clear();
            foreach (int keyCode in _pollBuffer)
            {
                _currentKeys.Add(keyCode);
            }

            // WHY: fire Began in source (device) order; the HashSet is only the O(1) membership test.
            // HasConnections gate skips the InputObject allocation entirely when nothing listens.
            if (InputBegan.HasConnections)
            {
                foreach (int keyCode in _pollBuffer)
                {
                    if (!_previousKeys.Contains(keyCode))
                    {
                        InputBegan.Fire(MakeKeyInput(keyCode, "Begin"), processed);
                    }
                }
            }

            if (InputEnded.HasConnections)
            {
                foreach (int keyCode in _previousKeys)
                {
                    if (!_currentKeys.Contains(keyCode))
                    {
                        InputEnded.Fire(MakeKeyInput(keyCode, "End"), processed);
                    }
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
                RbxScriptSignal signal = down ? InputBegan : InputEnded;
                if (!signal.HasConnections)
                {
                    continue;
                }

                RbxVector2 location = InputSource.GetMouseLocation();
                RbxInputObject input = new(
                    FindItemByName("KeyCode", "Unknown"),
                    FindItemByName("UserInputType", MouseButtonTypeNames[button]),
                    FindItemByName("UserInputState", down ? "Begin" : "End"),
                    new RbxVector3(location.X, location.Y, 0f),
                    RbxVector3.Zero);
                signal.Fire(input, _pointerProcessedThisStep);
            }
        }

        private void StepMouseMovement()
        {
            RbxVector2 location = InputSource.GetMouseLocation();
            RbxVector2 delta;
            if (!InputSource.TryReadMouseDelta(out delta))
            {
                delta = _hasPreviousMouseLocation
                    ? new RbxVector2(
                        location.X - _previousMouseLocation.X,
                        location.Y - _previousMouseLocation.Y)
                    : RbxVector2.Zero;
            }

            LastFrameMouseDelta = delta;
            _previousMouseLocation = location;
            _hasPreviousMouseLocation = true;
            // WHY: HasConnections gate FIRST — a mouse-move frame with no InputChanged listener must
            // allocate nothing (this runs every frame the mouse moves; the common case has 0 listeners).
            if (!InputChanged.HasConnections || (delta.X == 0f && delta.Y == 0f))
            {
                return;
            }

            RbxVector2 scaled = Scale(delta);
            RbxInputObject input = new(
                FindItemByName("KeyCode", "Unknown"),
                FindItemByName("UserInputType", "MouseMovement"),
                FindItemByName("UserInputState", "Change"),
                new RbxVector3(location.X, location.Y, 0f),
                new RbxVector3(scaled.X, scaled.Y, 0f));
            InputChanged.Fire(input, _pointerProcessedThisStep);
        }

        private void StepMouseWheel()
        {
            float wheel = InputSource.ReadMouseWheel();
            LastFrameWheel = float.IsNaN(wheel) ? 0f : wheel;
            if (LastFrameWheel == 0f || !InputChanged.HasConnections)
            {
                return;
            }

            // WHY the sign and not the raw amount: devices and browsers report one notch as 1, 3 or 120;
            // the mirror only promises the direction (InputObject.Position.Z, positive forward).
            RbxVector2 location = InputSource.GetMouseLocation();
            RbxInputObject input = new(
                FindItemByName("KeyCode", "Unknown"),
                FindItemByName("UserInputType", "MouseWheel"),
                FindItemByName("UserInputState", "Change"),
                new RbxVector3(location.X, location.Y, LastFrameWheel > 0f ? 1f : -1f),
                RbxVector3.Zero);
            InputChanged.Fire(input, _pointerProcessedThisStep);
        }

        private RbxVector2 Scale(RbxVector2 delta)
        {
            float factor = (float)_mouseDeltaSensitivity;
            return new RbxVector2(delta.X * factor, delta.Y * factor);
        }

        private void ApplyMouseBehaviorToCursor()
        {
            CursorPort?.ApplyMouseBehavior(RequestedMouseLock);
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
