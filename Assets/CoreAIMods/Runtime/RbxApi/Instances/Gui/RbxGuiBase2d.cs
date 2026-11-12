using System;
using System.Globalization;
using CoreAI.Mods.Rbx.Datatypes;

namespace CoreAI.Mods.Rbx.Instances
{
    /// <summary>What a GUI property write changes on screen; the presenter redraws only that.</summary>
    [Flags]
    public enum RbxGuiChange
    {
        None = 0,

        /// <summary>Size, position, anchor, rotation, size constraint, layout order, padding, list and grid layouts.</summary>
        Layout = 1,

        /// <summary>Background, border, corner, stroke and clipping.</summary>
        Paint = 2,

        /// <summary>Text and the text style.</summary>
        Text = 4,

        /// <summary>Image content and image style.</summary>
        Image = 8,

        /// <summary>ZIndex.</summary>
        Order = 16,

        /// <summary>Active, Interactable: whether the element takes the pointer.</summary>
        Interaction = 32,

        /// <summary>TextBox field settings (placeholder, multiline, editable).</summary>
        Field = 64,

        /// <summary>Visible.</summary>
        Visibility = 128,

        /// <summary>ScreenGui Enabled and DisplayOrder.</summary>
        Screen = 256,

        /// <summary>ScrollingFrame canvas and scrolling settings.</summary>
        Scroll = 512
    }

    /// <summary>
    /// The presenter's hook on one rendered GUI instance: it charges the owning mod's write budget before a write
    /// and redraws after it. An instance that is not on screen has no observer and pays nothing.
    /// </summary>
    internal interface IRbxGuiObserver
    {
        /// <summary>Called before a property write takes effect; may refuse it by throwing.</summary>
        void BeforeWrite(RbxInstance source, string member);

        /// <summary>Called after a real change.</summary>
        void AfterWrite(RbxInstance source, RbxGuiChange change);

        /// <summary>Brings the layout of the instance's screen up to date (AbsoluteSize reads).</summary>
        void EnsureLayout();
    }

    /// <summary>
    /// The GUI layout of a world, asked when a GUI object nobody observes yet reads its layout (it was just
    /// parented into a shown ScreenGui and the presenter has not drawn it yet).
    /// </summary>
    internal interface IRbxGuiLayoutSource
    {
        /// <summary>Re-reads the shown trees if they changed and lays out the screen holding <paramref name="instance"/>.</summary>
        void EnsureLayout(RbxInstance instance);

        /// <summary>Moves keyboard focus into or out of a box the presenter does not draw yet (it was made this frame).</summary>
        void RequestFocus(RbxTextBox box, bool focus);
    }

    /// <summary>
    /// Roblox <c>GuiBase2d</c>: the read-only layout results <c>AbsolutePosition</c>, <c>AbsoluteSize</c> and
    /// <c>AbsoluteRotation</c>, which the presenter computes for objects shown on a player's screen (they stay
    /// (0, 0) for objects that are not), and <c>AutoLocalize</c>, which is recorded.
    /// </summary>
    public abstract class RbxGuiBase2d : RbxInstance
    {
        private RbxVector2 _absolutePosition;
        private RbxVector2 _absoluteSize;
        private float _absoluteRotation;
        private bool _autoLocalize = true;

        private protected RbxGuiBase2d(ClassDescriptor descriptor)
            : base(descriptor)
        {
        }

        internal IRbxGuiObserver Observer { get; set; }

        /// <summary>The top-left corner on screen, in pixels, after layout.</summary>
        public RbxVector2 AbsolutePosition
        {
            get
            {
                RequestLayout();
                return _absolutePosition;
            }
        }

        /// <summary>The size on screen, in pixels, after layout.</summary>
        public RbxVector2 AbsoluteSize
        {
            get
            {
                RequestLayout();
                return _absoluteSize;
            }
        }

        /// <summary>The rotation on screen, in degrees: the sum of the rotations of this object and its ancestors.</summary>
        public float AbsoluteRotation
        {
            get
            {
                RequestLayout();
                return _absoluteRotation;
            }
        }

        /// <summary>Recorded; CoreAI does not localize GUI text.</summary>
        public bool AutoLocalize => _autoLocalize;

        public void SetAutoLocalize(bool value)
        {
            if (_autoLocalize == value)
            {
                return;
            }

            BeforeWrite(nameof(AutoLocalize));
            _autoLocalize = value;
            AfterWrite(nameof(AutoLocalize), RbxGuiChange.None);
        }

        /// <summary>Brings the layout up to date before a layout result is read.</summary>
        internal void RequestLayout()
        {
            if (Observer != null)
            {
                Observer.EnsureLayout();
                return;
            }

            Registry?.GuiLayout?.EnsureLayout(this);
        }

        /// <summary>Stores a layout result; fires the property signals of what changed.</summary>
        internal void ApplyLayout(RbxVector2 position, RbxVector2 size, float rotation)
        {
            if (IsDestroyed)
            {
                return;
            }

            bool positionChanged = position != _absolutePosition;
            bool sizeChanged = size != _absoluteSize;
            bool rotationChanged = !rotation.Equals(_absoluteRotation);
            _absolutePosition = position;
            _absoluteSize = size;
            _absoluteRotation = rotation;
            if (sizeChanged)
            {
                NotifyPropertyChanged(nameof(AbsoluteSize));
            }

            if (positionChanged)
            {
                NotifyPropertyChanged(nameof(AbsolutePosition));
            }

            if (rotationChanged)
            {
                NotifyPropertyChanged(nameof(AbsoluteRotation));
            }
        }

        /// <summary>The last layout result without bringing the layout up to date (the presenter reads it).</summary>
        internal RbxVector2 LaidOutPosition => _absolutePosition;

        /// <summary>The last layout size without bringing the layout up to date.</summary>
        internal RbxVector2 LaidOutSize => _absoluteSize;

        internal void BeforeWrite(string member)
        {
            ThrowIfDestroyed(member);
            Observer?.BeforeWrite(this, member);
        }

        internal void AfterWrite(string member, RbxGuiChange change)
        {
            if (change != RbxGuiChange.None)
            {
                Observer?.AfterWrite(this, change);
            }

            NotifyPropertyChanged(member);
        }

        internal void SetField(ref bool field, bool value, string member, RbxGuiChange change)
        {
            if (field == value)
            {
                return;
            }

            BeforeWrite(member);
            field = value;
            AfterWrite(member, change);
        }

        internal void SetField(ref int field, int value, string member, RbxGuiChange change)
        {
            if (field == value)
            {
                return;
            }

            BeforeWrite(member);
            field = value;
            AfterWrite(member, change);
        }

        internal void SetField(ref float field, double value, string member, RbxGuiChange change)
        {
            float next = RbxGuiValidation.RequireFinite(value, ClassName, member);
            if (field.Equals(next))
            {
                return;
            }

            BeforeWrite(member);
            field = next;
            AfterWrite(member, change);
        }

        internal void SetField(ref string field, string value, string member, RbxGuiChange change)
        {
            string next = value ?? string.Empty;
            if (string.Equals(field, next, StringComparison.Ordinal))
            {
                return;
            }

            BeforeWrite(member);
            field = next;
            AfterWrite(member, change);
        }

        internal void SetField(ref RbxUDim2 field, RbxUDim2 value, string member, RbxGuiChange change)
        {
            RbxGuiValidation.RequireFinite(value, ClassName, member);
            if (field == value)
            {
                return;
            }

            BeforeWrite(member);
            field = value;
            AfterWrite(member, change);
        }

        internal void SetField(ref RbxUDim field, RbxUDim value, string member, RbxGuiChange change)
        {
            RbxGuiValidation.RequireFinite(value, ClassName, member);
            if (field == value)
            {
                return;
            }

            BeforeWrite(member);
            field = value;
            AfterWrite(member, change);
        }

        internal void SetField(ref RbxVector2 field, RbxVector2 value, string member, RbxGuiChange change)
        {
            RbxGuiValidation.RequireFinite(value, ClassName, member);
            if (field == value)
            {
                return;
            }

            BeforeWrite(member);
            field = value;
            AfterWrite(member, change);
        }

        internal void SetField(ref RbxColor3 field, RbxColor3 value, string member, RbxGuiChange change)
        {
            RbxGuiValidation.RequireFinite(value, ClassName, member);
            if (field == value)
            {
                return;
            }

            BeforeWrite(member);
            field = value;
            AfterWrite(member, change);
        }

        /// <inheritdoc />
        protected internal override void CopyCustomStateTo(RbxInstance copy)
        {
            ((RbxGuiBase2d)copy)._autoLocalize = _autoLocalize;
        }
    }

    /// <summary>
    /// Roblox <c>LayerCollector</c>: <c>Enabled</c> (shown while true), <c>ResetOnSpawn</c> (whether a respawn
    /// replaces the copy in <c>PlayerGui</c>) and <c>ZIndexBehavior</c> (default Sibling; Global is recorded and
    /// drawn like Sibling).
    /// </summary>
    public abstract class RbxLayerCollector : RbxGuiBase2d
    {
        private bool _enabled = true;
        private bool _resetOnSpawn = true;
        private RbxZIndexBehavior _zIndexBehavior = RbxZIndexBehavior.Sibling;

        private protected RbxLayerCollector(ClassDescriptor descriptor)
            : base(descriptor)
        {
        }

        public bool Enabled => _enabled;

        public bool ResetOnSpawn => _resetOnSpawn;

        public RbxZIndexBehavior ZIndexBehavior => _zIndexBehavior;

        public void SetEnabled(bool value)
        {
            if (_enabled == value)
            {
                return;
            }

            BeforeWrite(nameof(Enabled));
            _enabled = value;
            AfterWrite(nameof(Enabled), RbxGuiChange.Screen);
        }

        public void SetResetOnSpawn(bool value)
        {
            if (_resetOnSpawn == value)
            {
                return;
            }

            BeforeWrite(nameof(ResetOnSpawn));
            _resetOnSpawn = value;
            AfterWrite(nameof(ResetOnSpawn), RbxGuiChange.None);
        }

        public void SetZIndexBehavior(RbxZIndexBehavior value)
        {
            RbxGuiValidation.RequireRange((int)value, 0, 1, ClassName, nameof(ZIndexBehavior));
            if (_zIndexBehavior == value)
            {
                return;
            }

            BeforeWrite(nameof(ZIndexBehavior));
            _zIndexBehavior = value;
            AfterWrite(nameof(ZIndexBehavior), RbxGuiChange.Order);
        }

        /// <inheritdoc />
        protected internal override void CopyCustomStateTo(RbxInstance copy)
        {
            base.CopyCustomStateTo(copy);
            RbxLayerCollector layer = (RbxLayerCollector)copy;
            layer._enabled = _enabled;
            layer._resetOnSpawn = _resetOnSpawn;
            layer._zIndexBehavior = _zIndexBehavior;
        }
    }

    /// <summary>
    /// Roblox <c>ScreenGui</c>: one runtime UI screen of the host while it sits in the local player's
    /// <c>PlayerGui</c>. <c>DisplayOrder</c> is the screen's sort order, on the same scale as a
    /// <c>UxmlService</c> screen's <c>SortOrder</c>. <c>IgnoreGuiInset</c>, <c>ClipToDeviceSafeArea</c>,
    /// <c>SafeAreaCompatibility</c> and <c>ScreenInsets</c> are recorded: CoreAI draws no top bar, so the inset is
    /// always zero.
    /// </summary>
    public sealed class RbxScreenGui : RbxLayerCollector
    {
        private int _displayOrder;
        private bool _ignoreGuiInset;
        private bool _clipToDeviceSafeArea = true;
        private int _safeAreaCompatibility = 1;
        private int _screenInsets = 2;

        internal RbxScreenGui(ClassDescriptor descriptor)
            : base(descriptor)
        {
        }

        public int DisplayOrder => _displayOrder;

        public bool IgnoreGuiInset => _ignoreGuiInset;

        public bool ClipToDeviceSafeArea => _clipToDeviceSafeArea;

        /// <summary><c>Enum.SafeAreaCompatibility</c> value (default FullscreenExtension).</summary>
        public int SafeAreaCompatibility => _safeAreaCompatibility;

        /// <summary><c>Enum.ScreenInsets</c> value (default CoreUISafeInsets).</summary>
        public int ScreenInsets => _screenInsets;

        public void SetDisplayOrder(int value)
        {
            if (_displayOrder == value)
            {
                return;
            }

            BeforeWrite(nameof(DisplayOrder));
            _displayOrder = value;
            AfterWrite(nameof(DisplayOrder), RbxGuiChange.Screen);
        }

        public void SetIgnoreGuiInset(bool value)
        {
            if (_ignoreGuiInset == value)
            {
                return;
            }

            BeforeWrite(nameof(IgnoreGuiInset));
            _ignoreGuiInset = value;
            AfterWrite(nameof(IgnoreGuiInset), RbxGuiChange.None);
        }

        public void SetClipToDeviceSafeArea(bool value)
        {
            if (_clipToDeviceSafeArea == value)
            {
                return;
            }

            BeforeWrite(nameof(ClipToDeviceSafeArea));
            _clipToDeviceSafeArea = value;
            AfterWrite(nameof(ClipToDeviceSafeArea), RbxGuiChange.None);
        }

        public void SetSafeAreaCompatibility(int value)
        {
            if (_safeAreaCompatibility == value)
            {
                return;
            }

            BeforeWrite(nameof(SafeAreaCompatibility));
            _safeAreaCompatibility = value;
            AfterWrite(nameof(SafeAreaCompatibility), RbxGuiChange.None);
        }

        public void SetScreenInsets(int value)
        {
            if (_screenInsets == value)
            {
                return;
            }

            BeforeWrite(nameof(ScreenInsets));
            _screenInsets = value;
            AfterWrite(nameof(ScreenInsets), RbxGuiChange.None);
        }

        /// <inheritdoc />
        protected internal override void CopyCustomStateTo(RbxInstance copy)
        {
            base.CopyCustomStateTo(copy);
            RbxScreenGui screen = (RbxScreenGui)copy;
            screen._displayOrder = _displayOrder;
            screen._ignoreGuiInset = _ignoreGuiInset;
            screen._clipToDeviceSafeArea = _clipToDeviceSafeArea;
            screen._safeAreaCompatibility = _safeAreaCompatibility;
            screen._screenInsets = _screenInsets;
        }
    }

    /// <summary>Value checks shared by the GUI classes: the BAD_ARGUMENT a non-finite or undefined value raises.</summary>
    internal static class RbxGuiValidation
    {
        public static float RequireFinite(double value, string owner, string member)
        {
            if (double.IsNaN(value) || double.IsInfinity(value))
            {
                throw RbxError.BadArgument(
                    owner + "." + member + " needs a finite number, got "
                    + value.ToString(CultureInfo.InvariantCulture),
                    "check the arithmetic that produced it (0/0, math.huge) before assigning");
            }

            return (float)value;
        }

        public static void RequireFinite(RbxUDim2 value, string owner, string member)
        {
            if (!IsFinite(value.X.Scale) || !IsFinite(value.Y.Scale))
            {
                throw RbxError.BadArgument(
                    owner + "." + member + " needs a UDim2 with finite scales",
                    "check the arithmetic that produced the UDim2 before assigning");
            }
        }

        public static void RequireFinite(RbxUDim value, string owner, string member)
        {
            if (!IsFinite(value.Scale))
            {
                throw RbxError.BadArgument(
                    owner + "." + member + " needs a UDim with a finite scale",
                    "check the arithmetic that produced the UDim before assigning");
            }
        }

        public static void RequireFinite(RbxVector2 value, string owner, string member)
        {
            if (!IsFinite(value.X) || !IsFinite(value.Y))
            {
                throw RbxError.BadArgument(
                    owner + "." + member + " needs a Vector2 with finite components",
                    "check the arithmetic that produced the Vector2 before assigning");
            }
        }

        public static void RequireFinite(RbxColor3 value, string owner, string member)
        {
            if (!IsFinite(value.R) || !IsFinite(value.G) || !IsFinite(value.B))
            {
                throw RbxError.BadArgument(
                    owner + "." + member + " needs a Color3 with finite components",
                    "check the arithmetic that produced the color before assigning");
            }
        }

        public static void RequireRange(int value, int min, int max, string owner, string member)
        {
            if (value < min || value > max)
            {
                throw RbxError.BadArgument(
                    owner + "." + member + " got an item outside its enum",
                    "assign an Enum item of the property's own enum");
            }
        }

        public static bool IsFinite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }
    }
}
