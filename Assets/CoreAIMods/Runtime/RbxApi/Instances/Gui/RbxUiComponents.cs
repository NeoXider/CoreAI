using System;
using CoreAI.Mods.Rbx.Datatypes;

namespace CoreAI.Mods.Rbx.Instances
{
    /// <summary>
    /// Roblox <c>UIBase</c>: the abstract base of the layout and appearance modifiers (<c>UICorner</c>,
    /// <c>UIListLayout</c>, ...). A modifier draws nothing itself; it changes how its parent GUI object (or the
    /// siblings of a layout) is laid out or drawn, so its writes redraw the parent.
    /// </summary>
    public abstract class RbxUiBase : RbxInstance
    {
        private protected RbxUiBase(ClassDescriptor descriptor)
            : base(descriptor)
        {
        }

        internal IRbxGuiObserver Observer { get; set; }

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

        internal void SetField(ref RbxVector2 field, RbxVector2 value, string member, RbxGuiChange change)
        {
            if (float.IsNaN(value.X) || float.IsNaN(value.Y))
            {
                throw RbxError.BadArgument(ClassName + "." + member + " needs a Vector2 without NaN components",
                    "check the arithmetic that produced the Vector2 before assigning");
            }

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

        private protected void BeforeWrite(string member)
        {
            ThrowIfDestroyed(member);
            Observer?.BeforeWrite(this, member);
        }

        private protected void AfterWrite(string member, RbxGuiChange change)
        {
            if (change != RbxGuiChange.None)
            {
                Observer?.AfterWrite(this, change);
            }

            NotifyPropertyChanged(member);
        }
    }

    /// <summary>
    /// Roblox <c>UICorner</c>: rounds the parent's corners. <c>CornerRadius</c> (default {0, 8}) writes all four
    /// corner radii and reads the top-left one; a radius's scale is relative to the shorter side, and no radius
    /// exceeds half of it, so a large one gives a pill.
    /// </summary>
    public sealed class RbxUiCorner : RbxUiBase
    {
        private RbxUDim _topLeft = new(0f, 8);
        private RbxUDim _topRight = new(0f, 8);
        private RbxUDim _bottomRight = new(0f, 8);
        private RbxUDim _bottomLeft = new(0f, 8);

        internal RbxUiCorner(ClassDescriptor descriptor)
            : base(descriptor)
        {
        }

        public RbxUDim CornerRadius => _topLeft;

        public RbxUDim TopLeftRadius => _topLeft;

        public RbxUDim TopRightRadius => _topRight;

        public RbxUDim BottomRightRadius => _bottomRight;

        public RbxUDim BottomLeftRadius => _bottomLeft;

        public void SetCornerRadius(RbxUDim value)
        {
            RbxGuiValidation.RequireFinite(value, ClassName, nameof(CornerRadius));
            if (_topLeft == value && _topRight == value && _bottomRight == value && _bottomLeft == value)
            {
                return;
            }

            BeforeWrite(nameof(CornerRadius));
            _topLeft = value;
            _topRight = value;
            _bottomRight = value;
            _bottomLeft = value;
            AfterWrite(nameof(CornerRadius), RbxGuiChange.Paint);
            NotifyPropertyChanged(nameof(TopLeftRadius));
            NotifyPropertyChanged(nameof(TopRightRadius));
            NotifyPropertyChanged(nameof(BottomRightRadius));
            NotifyPropertyChanged(nameof(BottomLeftRadius));
        }

        public void SetTopLeftRadius(RbxUDim value)
        {
            RbxUDim before = _topLeft;
            SetField(ref _topLeft, value, nameof(TopLeftRadius), RbxGuiChange.Paint);
            if (before != _topLeft)
            {
                NotifyPropertyChanged(nameof(CornerRadius));
            }
        }

        public void SetTopRightRadius(RbxUDim value)
        {
            SetField(ref _topRight, value, nameof(TopRightRadius), RbxGuiChange.Paint);
        }

        public void SetBottomRightRadius(RbxUDim value)
        {
            SetField(ref _bottomRight, value, nameof(BottomRightRadius), RbxGuiChange.Paint);
        }

        public void SetBottomLeftRadius(RbxUDim value)
        {
            SetField(ref _bottomLeft, value, nameof(BottomLeftRadius), RbxGuiChange.Paint);
        }

        /// <summary>A radius in pixels for a box of the given size: scale of the shorter side plus offset, capped at half of it.</summary>
        public static float ResolveRadius(RbxUDim radius, float width, float height)
        {
            float shorter = Math.Max(0f, Math.Min(width, height));
            float pixels = radius.Scale * shorter + radius.Offset;
            return Math.Max(0f, Math.Min(pixels, shorter * 0.5f));
        }

        /// <inheritdoc />
        protected internal override void CopyCustomStateTo(RbxInstance copy)
        {
            RbxUiCorner corner = (RbxUiCorner)copy;
            corner._topLeft = _topLeft;
            corner._topRight = _topRight;
            corner._bottomRight = _bottomRight;
            corner._bottomLeft = _bottomLeft;
        }
    }

    /// <summary>
    /// Roblox <c>UIPadding</c>: insets the area the parent's children are laid out in; each side's scale is
    /// relative to the parent's own size along that axis.
    /// </summary>
    public sealed class RbxUiPadding : RbxUiBase
    {
        private RbxUDim _left;
        private RbxUDim _top;
        private RbxUDim _right;
        private RbxUDim _bottom;

        internal RbxUiPadding(ClassDescriptor descriptor)
            : base(descriptor)
        {
        }

        public RbxUDim PaddingLeft => _left;

        public RbxUDim PaddingTop => _top;

        public RbxUDim PaddingRight => _right;

        public RbxUDim PaddingBottom => _bottom;

        public void SetPaddingLeft(RbxUDim value)
        {
            SetField(ref _left, value, nameof(PaddingLeft), RbxGuiChange.Layout);
        }

        public void SetPaddingTop(RbxUDim value)
        {
            SetField(ref _top, value, nameof(PaddingTop), RbxGuiChange.Layout);
        }

        public void SetPaddingRight(RbxUDim value)
        {
            SetField(ref _right, value, nameof(PaddingRight), RbxGuiChange.Layout);
        }

        public void SetPaddingBottom(RbxUDim value)
        {
            SetField(ref _bottom, value, nameof(PaddingBottom), RbxGuiChange.Layout);
        }

        /// <inheritdoc />
        protected internal override void CopyCustomStateTo(RbxInstance copy)
        {
            RbxUiPadding padding = (RbxUiPadding)copy;
            padding._left = _left;
            padding._top = _top;
            padding._right = _right;
            padding._bottom = _bottom;
        }
    }

    /// <summary>
    /// Roblox <c>UIStroke</c>: an outline. On a text object in Contextual mode it outlines the text; otherwise
    /// it draws a border of <c>Thickness</c> pixels around the parent's box (outside it, as Roblox's default
    /// Outer position does), following its corners. <c>LineJoinMode</c>, <c>StrokeSizingMode</c>,
    /// <c>BorderOffset</c> and <c>ZIndex</c> are recorded.
    /// </summary>
    public sealed class RbxUiStroke : RbxUiBase
    {
        private int _applyStrokeMode;
        private RbxColor3 _color;
        private bool _enabled = true;
        private int _lineJoinMode;
        private float _thickness = 1f;
        private float _transparency;
        private int _zIndex = 1;
        private int _borderStrokePosition;
        private RbxUDim _borderOffset;
        private int _strokeSizingMode;

        internal RbxUiStroke(ClassDescriptor descriptor)
            : base(descriptor)
        {
        }

        public RbxApplyStrokeMode ApplyStrokeMode => (RbxApplyStrokeMode)_applyStrokeMode;

        public RbxColor3 Color => _color;

        public bool Enabled => _enabled;

        /// <summary><c>Enum.LineJoinMode</c> value; recorded.</summary>
        public int LineJoinMode => _lineJoinMode;

        public float Thickness => _thickness;

        public float Transparency => _transparency;

        /// <summary>Recorded.</summary>
        public int ZIndex => _zIndex;

        public RbxBorderStrokePosition BorderStrokePosition => (RbxBorderStrokePosition)_borderStrokePosition;

        /// <summary>Recorded.</summary>
        public RbxUDim BorderOffset => _borderOffset;

        /// <summary><c>Enum.StrokeSizingMode</c> value; recorded.</summary>
        public int StrokeSizingMode => _strokeSizingMode;

        public void SetApplyStrokeMode(RbxApplyStrokeMode value)
        {
            RbxGuiValidation.RequireRange((int)value, 0, 1, ClassName, nameof(ApplyStrokeMode));
            SetField(ref _applyStrokeMode, (int)value, nameof(ApplyStrokeMode), RbxGuiChange.Paint | RbxGuiChange.Text);
        }

        public void SetColor(RbxColor3 value)
        {
            SetField(ref _color, value, nameof(Color), RbxGuiChange.Paint | RbxGuiChange.Text);
        }

        public void SetEnabled(bool value)
        {
            SetField(ref _enabled, value, nameof(Enabled), RbxGuiChange.Paint | RbxGuiChange.Text);
        }

        public void SetLineJoinMode(int value)
        {
            SetField(ref _lineJoinMode, value, nameof(LineJoinMode), RbxGuiChange.None);
        }

        public void SetThickness(double value)
        {
            SetField(ref _thickness, value, nameof(Thickness), RbxGuiChange.Paint | RbxGuiChange.Text);
        }

        public void SetTransparency(double value)
        {
            SetField(ref _transparency, value, nameof(Transparency), RbxGuiChange.Paint | RbxGuiChange.Text);
        }

        public void SetZIndex(int value)
        {
            SetField(ref _zIndex, value, nameof(ZIndex), RbxGuiChange.None);
        }

        public void SetBorderStrokePosition(RbxBorderStrokePosition value)
        {
            RbxGuiValidation.RequireRange((int)value, 0, 2, ClassName, nameof(BorderStrokePosition));
            SetField(ref _borderStrokePosition, (int)value, nameof(BorderStrokePosition), RbxGuiChange.Paint);
        }

        public void SetBorderOffset(RbxUDim value)
        {
            SetField(ref _borderOffset, value, nameof(BorderOffset), RbxGuiChange.None);
        }

        public void SetStrokeSizingMode(int value)
        {
            SetField(ref _strokeSizingMode, value, nameof(StrokeSizingMode), RbxGuiChange.None);
        }

        /// <inheritdoc />
        protected internal override void CopyCustomStateTo(RbxInstance copy)
        {
            RbxUiStroke stroke = (RbxUiStroke)copy;
            stroke._applyStrokeMode = _applyStrokeMode;
            stroke._color = _color;
            stroke._enabled = _enabled;
            stroke._lineJoinMode = _lineJoinMode;
            stroke._thickness = _thickness;
            stroke._transparency = _transparency;
            stroke._zIndex = _zIndex;
            stroke._borderStrokePosition = _borderStrokePosition;
            stroke._borderOffset = _borderOffset;
            stroke._strokeSizingMode = _strokeSizingMode;
        }
    }

    /// <summary>Roblox <c>UIScale</c>: scales the parent and its descendants around the parent's centre when drawn.</summary>
    public sealed class RbxUiScale : RbxUiBase
    {
        private float _scale = 1f;

        internal RbxUiScale(ClassDescriptor descriptor)
            : base(descriptor)
        {
        }

        public float Scale => _scale;

        public void SetScale(double value)
        {
            SetField(ref _scale, value, nameof(Scale), RbxGuiChange.Paint);
        }

        /// <inheritdoc />
        protected internal override void CopyCustomStateTo(RbxInstance copy)
        {
            ((RbxUiScale)copy)._scale = _scale;
        }
    }

    /// <summary>
    /// Roblox <c>UIGridStyleLayout</c>: the shared base of <c>UIListLayout</c> and <c>UIGridLayout</c>, which
    /// place the GUI-object siblings of the layout inside their parent, ignoring their <c>Position</c> and
    /// <c>AnchorPoint</c>; invisible siblings take no room. <c>SortOrder</c> LayoutOrder (default) sorts by
    /// <c>LayoutOrder</c>, then child order; Name sorts by name.
    /// </summary>
    public abstract class RbxUiGridStyleLayout : RbxUiBase
    {
        private int _fillDirection;
        private int _horizontalAlignment = (int)RbxHorizontalAlignment.Left;
        private int _verticalAlignment = (int)RbxVerticalAlignment.Top;
        private int _sortOrder = (int)RbxGuiSortOrder.LayoutOrder;
        private RbxVector2 _absoluteContentSize;

        private protected RbxUiGridStyleLayout(ClassDescriptor descriptor, RbxFillDirection fillDirection)
            : base(descriptor)
        {
            _fillDirection = (int)fillDirection;
        }

        public RbxFillDirection FillDirection => (RbxFillDirection)_fillDirection;

        public RbxHorizontalAlignment HorizontalAlignment => (RbxHorizontalAlignment)_horizontalAlignment;

        public RbxVerticalAlignment VerticalAlignment => (RbxVerticalAlignment)_verticalAlignment;

        public RbxGuiSortOrder SortOrder => (RbxGuiSortOrder)_sortOrder;

        /// <summary>The size the arranged siblings take, after layout.</summary>
        public RbxVector2 AbsoluteContentSize
        {
            get
            {
                RequestLayout();
                return _absoluteContentSize;
            }
        }

        public void SetFillDirection(RbxFillDirection value)
        {
            RbxGuiValidation.RequireRange((int)value, 0, 1, ClassName, nameof(FillDirection));
            SetField(ref _fillDirection, (int)value, nameof(FillDirection), RbxGuiChange.Layout);
        }

        public void SetHorizontalAlignment(RbxHorizontalAlignment value)
        {
            RbxGuiValidation.RequireRange((int)value, 0, 2, ClassName, nameof(HorizontalAlignment));
            SetField(ref _horizontalAlignment, (int)value, nameof(HorizontalAlignment), RbxGuiChange.Layout);
        }

        public void SetVerticalAlignment(RbxVerticalAlignment value)
        {
            RbxGuiValidation.RequireRange((int)value, 0, 2, ClassName, nameof(VerticalAlignment));
            SetField(ref _verticalAlignment, (int)value, nameof(VerticalAlignment), RbxGuiChange.Layout);
        }

        public void SetSortOrder(RbxGuiSortOrder value)
        {
            RbxGuiValidation.RequireRange((int)value, 0, 2, ClassName, nameof(SortOrder));
            SetField(ref _sortOrder, (int)value, nameof(SortOrder), RbxGuiChange.Layout);
        }

        /// <summary>The last content size without bringing the layout up to date (the layout pass reads it).</summary>
        internal RbxVector2 LaidOutContentSize => _absoluteContentSize;

        internal void ApplyContentSize(RbxVector2 size)
        {
            if (size == _absoluteContentSize || IsDestroyed)
            {
                return;
            }

            _absoluteContentSize = size;
            NotifyPropertyChanged(nameof(AbsoluteContentSize));
        }

        /// <inheritdoc />
        protected internal override void CopyCustomStateTo(RbxInstance copy)
        {
            RbxUiGridStyleLayout layout = (RbxUiGridStyleLayout)copy;
            layout._fillDirection = _fillDirection;
            layout._horizontalAlignment = _horizontalAlignment;
            layout._verticalAlignment = _verticalAlignment;
            layout._sortOrder = _sortOrder;
        }
    }

    /// <summary>
    /// Roblox <c>UIListLayout</c>: arranges the siblings in a row or a column (default Vertical) with
    /// <c>Padding</c> between them (its scale relative to the parent's size along the fill axis), aligned by
    /// <c>HorizontalAlignment</c> and <c>VerticalAlignment</c>. <c>Wraps</c>, <c>HorizontalFlex</c>,
    /// <c>VerticalFlex</c> and <c>ItemLineAlignment</c> are recorded.
    /// </summary>
    public sealed class RbxUiListLayout : RbxUiGridStyleLayout
    {
        private RbxUDim _padding;
        private bool _wraps;
        private int _horizontalFlex;
        private int _verticalFlex;
        private int _itemLineAlignment;

        internal RbxUiListLayout(ClassDescriptor descriptor)
            : base(descriptor, RbxFillDirection.Vertical)
        {
        }

        public RbxUDim Padding => _padding;

        /// <summary>Recorded; the list does not wrap.</summary>
        public bool Wraps => _wraps;

        /// <summary><c>Enum.UIFlexAlignment</c> value; recorded.</summary>
        public int HorizontalFlex => _horizontalFlex;

        /// <summary><c>Enum.UIFlexAlignment</c> value; recorded.</summary>
        public int VerticalFlex => _verticalFlex;

        /// <summary><c>Enum.ItemLineAlignment</c> value; recorded.</summary>
        public int ItemLineAlignment => _itemLineAlignment;

        public void SetPadding(RbxUDim value)
        {
            SetField(ref _padding, value, nameof(Padding), RbxGuiChange.Layout);
        }

        public void SetWraps(bool value)
        {
            SetField(ref _wraps, value, nameof(Wraps), RbxGuiChange.None);
        }

        public void SetHorizontalFlex(int value)
        {
            SetField(ref _horizontalFlex, value, nameof(HorizontalFlex), RbxGuiChange.None);
        }

        public void SetVerticalFlex(int value)
        {
            SetField(ref _verticalFlex, value, nameof(VerticalFlex), RbxGuiChange.None);
        }

        public void SetItemLineAlignment(int value)
        {
            SetField(ref _itemLineAlignment, value, nameof(ItemLineAlignment), RbxGuiChange.None);
        }

        /// <inheritdoc />
        protected internal override void CopyCustomStateTo(RbxInstance copy)
        {
            base.CopyCustomStateTo(copy);
            RbxUiListLayout list = (RbxUiListLayout)copy;
            list._padding = _padding;
            list._wraps = _wraps;
            list._horizontalFlex = _horizontalFlex;
            list._verticalFlex = _verticalFlex;
            list._itemLineAlignment = _itemLineAlignment;
        }
    }

    /// <summary>
    /// Roblox <c>UIGridLayout</c>: arranges the siblings in cells of <c>CellSize</c> (default {0, 100}, {0, 100})
    /// with <c>CellPadding</c> (default {0, 5}, {0, 5}) between them, filling rows first by default
    /// (Horizontal); <c>FillDirectionMaxCells</c> caps the cells per line (0 fits as many as the parent holds),
    /// and <c>StartCorner</c> picks the corner the first cell sits in.
    /// </summary>
    public sealed class RbxUiGridLayout : RbxUiGridStyleLayout
    {
        private RbxUDim2 _cellSize = new(0f, 100, 0f, 100);
        private RbxUDim2 _cellPadding = new(0f, 5, 0f, 5);
        private int _fillDirectionMaxCells;
        private int _startCorner;
        private RbxVector2 _absoluteCellSize = new(100f, 100f);
        private RbxVector2 _absoluteCellCount;

        internal RbxUiGridLayout(ClassDescriptor descriptor)
            : base(descriptor, RbxFillDirection.Horizontal)
        {
        }

        public RbxUDim2 CellSize => _cellSize;

        public RbxUDim2 CellPadding => _cellPadding;

        public int FillDirectionMaxCells => _fillDirectionMaxCells;

        public RbxStartCorner StartCorner => (RbxStartCorner)_startCorner;

        /// <summary>The cell size in pixels after layout.</summary>
        public RbxVector2 AbsoluteCellSize
        {
            get
            {
                RequestLayout();
                return _absoluteCellSize;
            }
        }

        /// <summary>Columns and rows after layout.</summary>
        public RbxVector2 AbsoluteCellCount
        {
            get
            {
                RequestLayout();
                return _absoluteCellCount;
            }
        }

        public void SetCellSize(RbxUDim2 value)
        {
            SetField(ref _cellSize, value, nameof(CellSize), RbxGuiChange.Layout);
        }

        public void SetCellPadding(RbxUDim2 value)
        {
            SetField(ref _cellPadding, value, nameof(CellPadding), RbxGuiChange.Layout);
        }

        public void SetFillDirectionMaxCells(int value)
        {
            SetField(ref _fillDirectionMaxCells, value, nameof(FillDirectionMaxCells), RbxGuiChange.Layout);
        }

        public void SetStartCorner(RbxStartCorner value)
        {
            RbxGuiValidation.RequireRange((int)value, 0, 3, ClassName, nameof(StartCorner));
            SetField(ref _startCorner, (int)value, nameof(StartCorner), RbxGuiChange.Layout);
        }

        internal void ApplyCells(RbxVector2 cellSize, RbxVector2 cellCount)
        {
            if (IsDestroyed)
            {
                return;
            }

            bool sizeChanged = cellSize != _absoluteCellSize;
            bool countChanged = cellCount != _absoluteCellCount;
            _absoluteCellSize = cellSize;
            _absoluteCellCount = cellCount;
            if (sizeChanged)
            {
                NotifyPropertyChanged(nameof(AbsoluteCellSize));
            }

            if (countChanged)
            {
                NotifyPropertyChanged(nameof(AbsoluteCellCount));
            }
        }

        /// <inheritdoc />
        protected internal override void CopyCustomStateTo(RbxInstance copy)
        {
            base.CopyCustomStateTo(copy);
            RbxUiGridLayout grid = (RbxUiGridLayout)copy;
            grid._cellSize = _cellSize;
            grid._cellPadding = _cellPadding;
            grid._fillDirectionMaxCells = _fillDirectionMaxCells;
            grid._startCorner = _startCorner;
        }
    }

    /// <summary>
    /// Roblox <c>UIAspectRatioConstraint</c>: keeps the parent's width-to-height ratio at <c>AspectRatio</c>
    /// (default 1, must be above 0). FitWithinMaxSize (default) shrinks the box to the largest one of that ratio
    /// inside its own size; ScaleWithParentSize keeps the <c>DominantAxis</c> length and caps the box at the
    /// parent's size.
    /// </summary>
    public sealed class RbxUiAspectRatioConstraint : RbxUiBase
    {
        private float _aspectRatio = 1f;
        private int _aspectType;
        private int _dominantAxis;

        internal RbxUiAspectRatioConstraint(ClassDescriptor descriptor)
            : base(descriptor)
        {
        }

        public float AspectRatio => _aspectRatio;

        public RbxAspectType AspectType => (RbxAspectType)_aspectType;

        public RbxDominantAxis DominantAxis => (RbxDominantAxis)_dominantAxis;

        public void SetAspectRatio(double value)
        {
            float next = RbxGuiValidation.RequireFinite(value, ClassName, nameof(AspectRatio));
            if (next <= 0f)
            {
                throw RbxError.BadArgument(
                    "UIAspectRatioConstraint.AspectRatio must be greater than 0",
                    "pass width divided by height, e.g. 16/9; invert it for height-to-width");
            }

            SetField(ref _aspectRatio, next, nameof(AspectRatio), RbxGuiChange.Layout);
        }

        public void SetAspectType(RbxAspectType value)
        {
            RbxGuiValidation.RequireRange((int)value, 0, 1, ClassName, nameof(AspectType));
            SetField(ref _aspectType, (int)value, nameof(AspectType), RbxGuiChange.Layout);
        }

        public void SetDominantAxis(RbxDominantAxis value)
        {
            RbxGuiValidation.RequireRange((int)value, 0, 1, ClassName, nameof(DominantAxis));
            SetField(ref _dominantAxis, (int)value, nameof(DominantAxis), RbxGuiChange.Layout);
        }

        /// <inheritdoc />
        protected internal override void CopyCustomStateTo(RbxInstance copy)
        {
            RbxUiAspectRatioConstraint constraint = (RbxUiAspectRatioConstraint)copy;
            constraint._aspectRatio = _aspectRatio;
            constraint._aspectType = _aspectType;
            constraint._dominantAxis = _dominantAxis;
        }
    }

    /// <summary>Roblox <c>UISizeConstraint</c>: clamps the parent's size between <c>MinSize</c> and <c>MaxSize</c> pixels.</summary>
    public sealed class RbxUiSizeConstraint : RbxUiBase
    {
        private RbxVector2 _minSize;
        private RbxVector2 _maxSize = new(float.PositiveInfinity, float.PositiveInfinity);

        internal RbxUiSizeConstraint(ClassDescriptor descriptor)
            : base(descriptor)
        {
        }

        public RbxVector2 MinSize => _minSize;

        public RbxVector2 MaxSize => _maxSize;

        public void SetMinSize(RbxVector2 value)
        {
            SetField(ref _minSize, value, nameof(MinSize), RbxGuiChange.Layout);
        }

        public void SetMaxSize(RbxVector2 value)
        {
            SetField(ref _maxSize, value, nameof(MaxSize), RbxGuiChange.Layout);
        }

        /// <inheritdoc />
        protected internal override void CopyCustomStateTo(RbxInstance copy)
        {
            RbxUiSizeConstraint constraint = (RbxUiSizeConstraint)copy;
            constraint._minSize = _minSize;
            constraint._maxSize = _maxSize;
        }
    }
}
