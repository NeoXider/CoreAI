using CoreAI.Mods.Rbx.Datatypes;

namespace CoreAI.Mods.Rbx.Instances
{
    /// <summary>
    /// Roblox <c>GuiObject</c>: the box every 2D GUI element is. <c>Size</c> and <c>Position</c> are
    /// scale-plus-offset against the parent, <c>AnchorPoint</c> picks the point of the box that
    /// <c>Position</c> places, boxes are absolutely positioned unless a <c>UIListLayout</c> or
    /// <c>UIGridLayout</c> sibling arranges them, <c>ZIndex</c> orders siblings, <c>ClipsDescendants</c> clips
    /// children to the box. Defaults are Roblox's <c>Instance.new</c> defaults (the legacy grey box with a
    /// one-pixel dark border); each concrete class sets its own size.
    /// </summary>
    public abstract class RbxGuiObject : RbxGuiBase2d
    {
        /// <summary><c>Instance.new</c> default background: rgb(163, 162, 165).</summary>
        public static readonly RbxColor3 DefaultBackgroundColor = new(163f / 255f, 162f / 255f, 165f / 255f);

        /// <summary><c>Instance.new</c> default border colour: rgb(27, 42, 53).</summary>
        public static readonly RbxColor3 DefaultBorderColor = new(27f / 255f, 42f / 255f, 53f / 255f);

        private bool _active;
        private RbxVector2 _anchorPoint;
        private RbxAutomaticSize _automaticSize;
        private RbxColor3 _backgroundColor3 = DefaultBackgroundColor;
        private float _backgroundTransparency;
        private RbxColor3 _borderColor3 = DefaultBorderColor;
        private RbxBorderMode _borderMode;
        private int _borderSizePixel = 1;
        private bool _clipsDescendants;
        private bool _interactable = true;
        private int _layoutOrder;
        private RbxUDim2 _position;
        private float _rotation;
        private bool _selectable;
        private RbxUDim2 _size;
        private RbxSizeConstraint _sizeConstraint;
        private bool _visible = true;
        private int _zIndex = 1;

        private protected RbxGuiObject(ClassDescriptor descriptor, RbxUDim2 defaultSize, bool active)
            : base(descriptor)
        {
            _size = defaultSize;
            _active = active;
            _selectable = active;
        }

        public bool Active => _active;

        public RbxVector2 AnchorPoint => _anchorPoint;

        public RbxAutomaticSize AutomaticSize => _automaticSize;

        public RbxColor3 BackgroundColor3 => _backgroundColor3;

        public float BackgroundTransparency => _backgroundTransparency;

        public RbxColor3 BorderColor3 => _borderColor3;

        public RbxBorderMode BorderMode => _borderMode;

        public int BorderSizePixel => _borderSizePixel;

        public bool ClipsDescendants => _clipsDescendants;

        public bool Interactable => _interactable;

        public int LayoutOrder => _layoutOrder;

        public RbxUDim2 Position => _position;

        public float Rotation => _rotation;

        public bool Selectable => _selectable;

        public RbxUDim2 Size => _size;

        public RbxSizeConstraint SizeConstraint => _sizeConstraint;

        public bool Visible => _visible;

        public int ZIndex => _zIndex;

        /// <summary>Mirror <c>GuiObject.MouseEnter(x, y)</c>.</summary>
        public RbxScriptSignal MouseEnter => GetOrCreateSignal("MouseEnter");

        /// <summary>Mirror <c>GuiObject.MouseLeave(x, y)</c>.</summary>
        public RbxScriptSignal MouseLeave => GetOrCreateSignal("MouseLeave");

        /// <summary>True while a script listens to MouseEnter or MouseLeave (the element then takes the pointer).</summary>
        internal bool ListensToPointer => HasSignalConnections("MouseEnter") || HasSignalConnections("MouseLeave");

        public void SetActive(bool value)
        {
            SetField(ref _active, value, nameof(Active), RbxGuiChange.Interaction);
        }

        public void SetAnchorPoint(RbxVector2 value)
        {
            SetField(ref _anchorPoint, value, nameof(AnchorPoint), RbxGuiChange.Layout);
        }

        public void SetAutomaticSize(RbxAutomaticSize value)
        {
            RbxGuiValidation.RequireRange((int)value, 0, 3, ClassName, nameof(AutomaticSize));
            int field = (int)_automaticSize;
            SetField(ref field, (int)value, nameof(AutomaticSize), RbxGuiChange.Layout);
            _automaticSize = (RbxAutomaticSize)field;
        }

        public void SetBackgroundColor3(RbxColor3 value)
        {
            SetField(ref _backgroundColor3, value, nameof(BackgroundColor3), RbxGuiChange.Paint);
        }

        public void SetBackgroundTransparency(double value)
        {
            SetField(ref _backgroundTransparency, value, nameof(BackgroundTransparency), RbxGuiChange.Paint);
        }

        public void SetBorderColor3(RbxColor3 value)
        {
            SetField(ref _borderColor3, value, nameof(BorderColor3), RbxGuiChange.Paint);
        }

        public void SetBorderMode(RbxBorderMode value)
        {
            RbxGuiValidation.RequireRange((int)value, 0, 2, ClassName, nameof(BorderMode));
            int field = (int)_borderMode;
            SetField(ref field, (int)value, nameof(BorderMode), RbxGuiChange.Layout | RbxGuiChange.Paint);
            _borderMode = (RbxBorderMode)field;
        }

        public void SetBorderSizePixel(int value)
        {
            SetField(ref _borderSizePixel, value, nameof(BorderSizePixel), RbxGuiChange.Layout | RbxGuiChange.Paint);
        }

        public void SetClipsDescendants(bool value)
        {
            SetField(ref _clipsDescendants, value, nameof(ClipsDescendants), RbxGuiChange.Paint);
        }

        public void SetInteractable(bool value)
        {
            SetField(ref _interactable, value, nameof(Interactable), RbxGuiChange.Interaction);
        }

        public void SetLayoutOrder(int value)
        {
            SetField(ref _layoutOrder, value, nameof(LayoutOrder), RbxGuiChange.Layout);
        }

        public void SetPosition(RbxUDim2 value)
        {
            SetField(ref _position, value, nameof(Position), RbxGuiChange.Layout);
        }

        public void SetRotation(double value)
        {
            SetField(ref _rotation, value, nameof(Rotation), RbxGuiChange.Layout);
        }

        public void SetSelectable(bool value)
        {
            SetField(ref _selectable, value, nameof(Selectable), RbxGuiChange.None);
        }

        public void SetSize(RbxUDim2 value)
        {
            SetField(ref _size, value, nameof(Size), RbxGuiChange.Layout);
        }

        public void SetSizeConstraint(RbxSizeConstraint value)
        {
            RbxGuiValidation.RequireRange((int)value, 0, 2, ClassName, nameof(SizeConstraint));
            int field = (int)_sizeConstraint;
            SetField(ref field, (int)value, nameof(SizeConstraint), RbxGuiChange.Layout);
            _sizeConstraint = (RbxSizeConstraint)field;
        }

        public void SetVisible(bool value)
        {
            SetField(ref _visible, value, nameof(Visible), RbxGuiChange.Visibility | RbxGuiChange.Layout);
        }

        public void SetZIndex(int value)
        {
            SetField(ref _zIndex, value, nameof(ZIndex), RbxGuiChange.Order);
        }

        /// <summary>A script took MouseEnter or MouseLeave: a shown object starts taking the pointer.</summary>
        internal void NotePointerInterest()
        {
            Observer?.AfterWrite(this, RbxGuiChange.Interaction);
        }

        internal void FireMouseEnter(int x, int y)
        {
            if (HasSignalConnections("MouseEnter"))
            {
                FireSignal("MouseEnter", x, y);
            }
        }

        internal void FireMouseLeave(int x, int y)
        {
            if (HasSignalConnections("MouseLeave"))
            {
                FireSignal("MouseLeave", x, y);
            }
        }

        /// <inheritdoc />
        protected internal override void CopyCustomStateTo(RbxInstance copy)
        {
            base.CopyCustomStateTo(copy);
            RbxGuiObject gui = (RbxGuiObject)copy;
            gui._active = _active;
            gui._anchorPoint = _anchorPoint;
            gui._automaticSize = _automaticSize;
            gui._backgroundColor3 = _backgroundColor3;
            gui._backgroundTransparency = _backgroundTransparency;
            gui._borderColor3 = _borderColor3;
            gui._borderMode = _borderMode;
            gui._borderSizePixel = _borderSizePixel;
            gui._clipsDescendants = _clipsDescendants;
            gui._interactable = _interactable;
            gui._layoutOrder = _layoutOrder;
            gui._position = _position;
            gui._rotation = _rotation;
            gui._selectable = _selectable;
            gui._size = _size;
            gui._sizeConstraint = _sizeConstraint;
            gui._visible = _visible;
            gui._zIndex = _zIndex;
        }
    }

    /// <summary>Roblox <c>Frame</c>: a plain box (default size 100 by 100 pixels). <c>Style</c> is recorded.</summary>
    public sealed class RbxFrame : RbxGuiObject
    {
        private int _style;

        internal RbxFrame(ClassDescriptor descriptor)
            : base(descriptor, new RbxUDim2(0f, 100, 0f, 100), false)
        {
        }

        /// <summary><c>Enum.FrameStyle</c> value; only Custom is drawn.</summary>
        public int Style => _style;

        public void SetStyle(int value)
        {
            SetField(ref _style, value, nameof(Style), RbxGuiChange.None);
        }

        /// <inheritdoc />
        protected internal override void CopyCustomStateTo(RbxInstance copy)
        {
            base.CopyCustomStateTo(copy);
            ((RbxFrame)copy)._style = _style;
        }
    }

    /// <summary>
    /// Roblox <c>GuiButton</c>: a clickable box. <c>MouseButton1Click</c> fires on every click;
    /// <c>Activated(inputObject, clickCount)</c> fires too while <c>Active</c> is true. <c>AutoButtonColor</c>
    /// darkens the background while the pointer is over the button; <c>Modal</c>, <c>Selected</c> and
    /// <c>Style</c> are recorded.
    /// </summary>
    public abstract class RbxGuiButton : RbxGuiObject
    {
        private bool _autoButtonColor = true;
        private bool _modal;
        private bool _selected;
        private int _style;

        private protected RbxGuiButton(ClassDescriptor descriptor, RbxUDim2 defaultSize)
            : base(descriptor, defaultSize, true)
        {
        }

        public bool AutoButtonColor => _autoButtonColor;

        public bool Modal => _modal;

        public bool Selected => _selected;

        /// <summary><c>Enum.ButtonStyle</c> value; only Custom is drawn.</summary>
        public int Style => _style;

        /// <summary>Mirror <c>GuiButton.MouseButton1Click()</c>.</summary>
        public RbxScriptSignal MouseButton1Click => GetOrCreateSignal("MouseButton1Click");

        /// <summary>Mirror <c>GuiButton.Activated(inputObject, clickCount)</c>.</summary>
        public RbxScriptSignal Activated => GetOrCreateSignal("Activated");

        public void SetAutoButtonColor(bool value)
        {
            SetField(ref _autoButtonColor, value, nameof(AutoButtonColor), RbxGuiChange.Paint);
        }

        public void SetModal(bool value)
        {
            SetField(ref _modal, value, nameof(Modal), RbxGuiChange.None);
        }

        public void SetSelected(bool value)
        {
            SetField(ref _selected, value, nameof(Selected), RbxGuiChange.None);
        }

        public void SetStyle(int value)
        {
            SetField(ref _style, value, nameof(Style), RbxGuiChange.None);
        }

        /// <summary>
        /// One click of the button: <c>MouseButton1Click</c>, then <c>Activated</c> with
        /// <paramref name="input"/> and a click count of one while the button is <c>Active</c>.
        /// </summary>
        internal void FireClick(object input)
        {
            if (HasSignalConnections("MouseButton1Click"))
            {
                FireSignal("MouseButton1Click");
            }

            if (Active && HasSignalConnections("Activated"))
            {
                FireSignal("Activated", input, 1);
            }
        }

        /// <inheritdoc />
        protected internal override void CopyCustomStateTo(RbxInstance copy)
        {
            base.CopyCustomStateTo(copy);
            RbxGuiButton button = (RbxGuiButton)copy;
            button._autoButtonColor = _autoButtonColor;
            button._modal = _modal;
            button._selected = _selected;
            button._style = _style;
        }
    }
}
