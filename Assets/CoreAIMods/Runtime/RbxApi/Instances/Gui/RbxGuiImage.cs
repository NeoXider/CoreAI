using CoreAI.Mods.Rbx.Datatypes;

namespace CoreAI.Mods.Rbx.Instances
{
    /// <summary>A GUI object that draws an image: <c>ImageLabel</c> and <c>ImageButton</c>.</summary>
    public interface IRbxGuiImageObject
    {
        /// <summary>The image and its style.</summary>
        RbxGuiImage ImageState { get; }
    }

    /// <summary>
    /// The image members <c>ImageLabel</c> and <c>ImageButton</c> share: <c>Image</c>, <c>ImageColor3</c>,
    /// <c>ImageTransparency</c> and <c>ScaleType</c> are drawn; <c>ImageRectOffset</c>, <c>ImageRectSize</c>,
    /// <c>SliceScale</c>, <c>TileSize</c> and <c>ResampleMode</c> are recorded. <c>Image</c> names an image the
    /// host has registered (a key, a <c>Resources</c> path); an <c>rbxassetid://</c> id is never fetched and
    /// draws nothing.
    /// </summary>
    public sealed class RbxGuiImage
    {
        private readonly RbxGuiObject _owner;
        private string _image = string.Empty;
        private RbxColor3 _imageColor3 = new(1f, 1f, 1f);
        private float _imageTransparency;
        private int _scaleType;
        private RbxVector2 _imageRectOffset;
        private RbxVector2 _imageRectSize;
        private float _sliceScale = 1f;
        private RbxUDim2 _tileSize = new(1f, 0, 1f, 0);
        private int _resampleMode;

        internal RbxGuiImage(RbxGuiObject owner)
        {
            _owner = owner;
        }

        public string Image => _image;

        public RbxColor3 ImageColor3 => _imageColor3;

        public float ImageTransparency => _imageTransparency;

        public RbxScaleType ScaleType => (RbxScaleType)_scaleType;

        public RbxVector2 ImageRectOffset => _imageRectOffset;

        public RbxVector2 ImageRectSize => _imageRectSize;

        public float SliceScale => _sliceScale;

        public RbxUDim2 TileSize => _tileSize;

        /// <summary><c>Enum.ResamplerMode</c> value; recorded.</summary>
        public int ResampleMode => _resampleMode;

        /// <summary>
        /// True when <see cref="Image"/> names something the host can draw: not empty and not a Roblox asset
        /// id, which CoreAI never downloads.
        /// </summary>
        public bool IsDrawable => _image.Length > 0 && !IsRobloxAsset(_image);

        public void SetImage(string value)
        {
            _owner.SetField(ref _image, value, "Image", RbxGuiChange.Image);
        }

        public void SetImageColor3(RbxColor3 value)
        {
            _owner.SetField(ref _imageColor3, value, "ImageColor3", RbxGuiChange.Image);
        }

        public void SetImageTransparency(double value)
        {
            _owner.SetField(ref _imageTransparency, value, "ImageTransparency", RbxGuiChange.Image);
        }

        public void SetScaleType(RbxScaleType value)
        {
            RbxGuiValidation.RequireRange((int)value, 0, 4, _owner.ClassName, "ScaleType");
            _owner.SetField(ref _scaleType, (int)value, "ScaleType", RbxGuiChange.Image);
        }

        public void SetImageRectOffset(RbxVector2 value)
        {
            _owner.SetField(ref _imageRectOffset, value, "ImageRectOffset", RbxGuiChange.None);
        }

        public void SetImageRectSize(RbxVector2 value)
        {
            _owner.SetField(ref _imageRectSize, value, "ImageRectSize", RbxGuiChange.None);
        }

        public void SetSliceScale(double value)
        {
            _owner.SetField(ref _sliceScale, value, "SliceScale", RbxGuiChange.None);
        }

        public void SetTileSize(RbxUDim2 value)
        {
            _owner.SetField(ref _tileSize, value, "TileSize", RbxGuiChange.None);
        }

        public void SetResampleMode(int value)
        {
            _owner.SetField(ref _resampleMode, value, "ResampleMode", RbxGuiChange.None);
        }

        internal void CopyTo(RbxGuiImage copy)
        {
            copy._image = _image;
            copy._imageColor3 = _imageColor3;
            copy._imageTransparency = _imageTransparency;
            copy._scaleType = _scaleType;
            copy._imageRectOffset = _imageRectOffset;
            copy._imageRectSize = _imageRectSize;
            copy._sliceScale = _sliceScale;
            copy._tileSize = _tileSize;
            copy._resampleMode = _resampleMode;
        }

        /// <summary>True for content ids that point at Roblox's asset service.</summary>
        public static bool IsRobloxAsset(string image)
        {
            return image.StartsWith("rbxassetid://", System.StringComparison.OrdinalIgnoreCase)
                   || image.StartsWith("rbxasset://", System.StringComparison.OrdinalIgnoreCase)
                   || image.StartsWith("rbxthumb://", System.StringComparison.OrdinalIgnoreCase)
                   || image.StartsWith("http://www.roblox.com/asset", System.StringComparison.OrdinalIgnoreCase)
                   || image.StartsWith("https://www.roblox.com/asset", System.StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>Roblox <c>ImageLabel</c>: a box with an image (default 100 by 100 pixels).</summary>
    public sealed class RbxImageLabel : RbxGuiLabel, IRbxGuiImageObject
    {
        internal RbxImageLabel(ClassDescriptor descriptor)
            : base(descriptor, new RbxUDim2(0f, 100, 0f, 100))
        {
            ImageState = new RbxGuiImage(this);
        }

        /// <inheritdoc />
        public RbxGuiImage ImageState { get; }

        /// <inheritdoc />
        protected internal override void CopyCustomStateTo(RbxInstance copy)
        {
            base.CopyCustomStateTo(copy);
            ImageState.CopyTo(((RbxImageLabel)copy).ImageState);
        }
    }

    /// <summary>
    /// Roblox <c>ImageButton</c>: a button with an image (default 100 by 100 pixels). <c>HoverImage</c> and
    /// <c>PressedImage</c> are recorded.
    /// </summary>
    public sealed class RbxImageButton : RbxGuiButton, IRbxGuiImageObject
    {
        private string _hoverImage = string.Empty;
        private string _pressedImage = string.Empty;

        internal RbxImageButton(ClassDescriptor descriptor)
            : base(descriptor, new RbxUDim2(0f, 100, 0f, 100))
        {
            ImageState = new RbxGuiImage(this);
        }

        /// <inheritdoc />
        public RbxGuiImage ImageState { get; }

        public string HoverImage => _hoverImage;

        public string PressedImage => _pressedImage;

        public void SetHoverImage(string value)
        {
            SetField(ref _hoverImage, value, nameof(HoverImage), RbxGuiChange.None);
        }

        public void SetPressedImage(string value)
        {
            SetField(ref _pressedImage, value, nameof(PressedImage), RbxGuiChange.None);
        }

        /// <inheritdoc />
        protected internal override void CopyCustomStateTo(RbxInstance copy)
        {
            base.CopyCustomStateTo(copy);
            RbxImageButton button = (RbxImageButton)copy;
            ImageState.CopyTo(button.ImageState);
            button._hoverImage = _hoverImage;
            button._pressedImage = _pressedImage;
        }
    }

    /// <summary>
    /// Roblox <c>ScrollingFrame</c>: a box whose children sit on a canvas of <c>CanvasSize</c> (default
    /// {0, 0}, {2, 0}, grown to fit the children by <c>AutomaticCanvasSize</c>) clipped by its window.
    /// <c>CanvasPosition</c> is the scroll offset: a script moves it (a charged write), and the player's wheel,
    /// touch drag or scroll bar moves it too (a layout result: never charged, the property signal fires). The layout
    /// keeps it between zero and <c>AbsoluteCanvasSize - AbsoluteWindowSize</c>. <c>ScrollingEnabled</c> and
    /// <c>ScrollingDirection</c> decide which axes the player may scroll; <c>ScrollBarThickness</c>,
    /// <c>ScrollBarImageColor3</c>, <c>ScrollBarImageTransparency</c>, the two insets and
    /// <c>VerticalScrollBarPosition</c> shape the drawn bars, which are flat bars of that color (the bar images are
    /// not drawn). <c>ElasticBehavior</c> is kept for saved worlds only: the canvas always stops at its ends.
    /// </summary>
    public sealed class RbxScrollingFrame : RbxGuiObject
    {
        private RbxUDim2 _canvasSize = new(0f, 0, 2f, 0);
        private RbxVector2 _canvasPosition;
        private int _scrollBarThickness = 12;
        private int _scrollingDirection = (int)RbxScrollingDirection.XY;
        private bool _scrollingEnabled = true;
        private RbxColor3 _scrollBarImageColor3 = new(1f, 1f, 1f);
        private float _scrollBarImageTransparency;
        private int _automaticCanvasSize;
        private int _elasticBehavior;
        private int _verticalScrollBarPosition;
        private int _horizontalScrollBarInset;
        private int _verticalScrollBarInset;
        private RbxVector2 _absoluteCanvasSize;
        private RbxVector2 _absoluteWindowSize;

        internal RbxScrollingFrame(ClassDescriptor descriptor)
            : base(descriptor, new RbxUDim2(0f, 100, 0f, 100), true)
        {
        }

        public RbxUDim2 CanvasSize => _canvasSize;

        public RbxVector2 CanvasPosition => _canvasPosition;

        public int ScrollBarThickness => _scrollBarThickness;

        public RbxScrollingDirection ScrollingDirection => (RbxScrollingDirection)_scrollingDirection;

        public bool ScrollingEnabled => _scrollingEnabled;

        public RbxColor3 ScrollBarImageColor3 => _scrollBarImageColor3;

        public float ScrollBarImageTransparency => _scrollBarImageTransparency;

        /// <summary><c>Enum.AutomaticSize</c> value of the canvas.</summary>
        public RbxAutomaticSize AutomaticCanvasSize => (RbxAutomaticSize)_automaticCanvasSize;

        /// <summary>
        /// <c>Enum.ElasticBehavior</c> value; saved with the world, never drawn (no overscroll), and a loud stub for
        /// Lua.
        /// </summary>
        public int ElasticBehavior => _elasticBehavior;

        /// <summary><c>Enum.VerticalScrollBarPosition</c> value: 0 draws the vertical bar on the right, 1 on the left.</summary>
        public int VerticalScrollBarPosition => _verticalScrollBarPosition;

        /// <summary>
        /// <c>Enum.ScrollBarInset</c> value of the horizontal bar: 0 draws it over the canvas, 1 takes its strip from
        /// the window while it shows, 2 always takes it.
        /// </summary>
        public int HorizontalScrollBarInset => _horizontalScrollBarInset;

        /// <summary><c>Enum.ScrollBarInset</c> value of the vertical bar, as <see cref="HorizontalScrollBarInset"/>.</summary>
        public int VerticalScrollBarInset => _verticalScrollBarInset;

        /// <summary>The canvas size in pixels after layout.</summary>
        public RbxVector2 AbsoluteCanvasSize
        {
            get
            {
                RequestLayout();
                return _absoluteCanvasSize;
            }
        }

        /// <summary>The visible window in pixels after layout.</summary>
        public RbxVector2 AbsoluteWindowSize
        {
            get
            {
                RequestLayout();
                return _absoluteWindowSize;
            }
        }

        public void SetCanvasSize(RbxUDim2 value)
        {
            SetField(ref _canvasSize, value, nameof(CanvasSize), RbxGuiChange.Layout | RbxGuiChange.Scroll);
        }

        public void SetCanvasPosition(RbxVector2 value)
        {
            SetField(ref _canvasPosition, value, nameof(CanvasPosition), RbxGuiChange.Layout | RbxGuiChange.Scroll);
        }

        public void SetScrollBarThickness(int value)
        {
            SetField(ref _scrollBarThickness, value, nameof(ScrollBarThickness),
                RbxGuiChange.Layout | RbxGuiChange.Scroll);
        }

        public void SetScrollingDirection(RbxScrollingDirection value)
        {
            if (value != RbxScrollingDirection.X && value != RbxScrollingDirection.Y
                                                 && value != RbxScrollingDirection.XY)
            {
                throw RbxError.BadArgument(ClassName + ".ScrollingDirection got an item outside its enum",
                    "assign Enum.ScrollingDirection.X, Y or XY");
            }

            SetField(ref _scrollingDirection, (int)value, nameof(ScrollingDirection),
                RbxGuiChange.Layout | RbxGuiChange.Scroll);
        }

        public void SetScrollingEnabled(bool value)
        {
            SetField(ref _scrollingEnabled, value, nameof(ScrollingEnabled), RbxGuiChange.Layout | RbxGuiChange.Scroll);
        }

        public void SetScrollBarImageColor3(RbxColor3 value)
        {
            SetField(ref _scrollBarImageColor3, value, nameof(ScrollBarImageColor3), RbxGuiChange.Scroll);
        }

        public void SetScrollBarImageTransparency(double value)
        {
            SetField(ref _scrollBarImageTransparency, value, nameof(ScrollBarImageTransparency), RbxGuiChange.Scroll);
        }

        public void SetAutomaticCanvasSize(RbxAutomaticSize value)
        {
            RbxGuiValidation.RequireRange((int)value, 0, 3, ClassName, nameof(AutomaticCanvasSize));
            SetField(ref _automaticCanvasSize, (int)value, nameof(AutomaticCanvasSize), RbxGuiChange.Layout);
        }

        public void SetElasticBehavior(int value)
        {
            SetField(ref _elasticBehavior, value, nameof(ElasticBehavior), RbxGuiChange.None);
        }

        public void SetVerticalScrollBarPosition(int value)
        {
            SetField(ref _verticalScrollBarPosition, value, nameof(VerticalScrollBarPosition),
                RbxGuiChange.Layout | RbxGuiChange.Scroll);
        }

        public void SetHorizontalScrollBarInset(int value)
        {
            SetField(ref _horizontalScrollBarInset, value, nameof(HorizontalScrollBarInset),
                RbxGuiChange.Layout | RbxGuiChange.Scroll);
        }

        public void SetVerticalScrollBarInset(int value)
        {
            SetField(ref _verticalScrollBarInset, value, nameof(VerticalScrollBarInset),
                RbxGuiChange.Layout | RbxGuiChange.Scroll);
        }

        /// <summary>Stores the canvas the layout computed; fires the property signals of what changed.</summary>
        internal void ApplyCanvasLayout(RbxVector2 canvas, RbxVector2 window)
        {
            bool canvasChanged = canvas != _absoluteCanvasSize;
            bool windowChanged = window != _absoluteWindowSize;
            _absoluteCanvasSize = canvas;
            _absoluteWindowSize = window;
            if (canvasChanged)
            {
                NotifyPropertyChanged(nameof(AbsoluteCanvasSize));
            }

            if (windowChanged)
            {
                NotifyPropertyChanged(nameof(AbsoluteWindowSize));
            }
        }

        /// <summary>
        /// Stores a scroll offset the player produced (wheel, drag, scroll bar): no budget is charged, the property
        /// signal fires and the children are laid out again at their new <c>AbsolutePosition</c>.
        /// </summary>
        internal void ApplyScrolledPosition(RbxVector2 position)
        {
            if (position == _canvasPosition || IsDestroyed)
            {
                return;
            }

            _canvasPosition = position;
            AfterWrite(nameof(CanvasPosition), RbxGuiChange.Layout);
        }

        /// <summary>Stores the offset the layout clamped into the canvas; only the property signal fires.</summary>
        internal void ApplyClampedPosition(RbxVector2 position)
        {
            if (position == _canvasPosition || IsDestroyed)
            {
                return;
            }

            _canvasPosition = position;
            NotifyPropertyChanged(nameof(CanvasPosition));
        }

        /// <inheritdoc />
        protected internal override void CopyCustomStateTo(RbxInstance copy)
        {
            base.CopyCustomStateTo(copy);
            RbxScrollingFrame frame = (RbxScrollingFrame)copy;
            frame._canvasSize = _canvasSize;
            frame._canvasPosition = _canvasPosition;
            frame._scrollBarThickness = _scrollBarThickness;
            frame._scrollingDirection = _scrollingDirection;
            frame._scrollingEnabled = _scrollingEnabled;
            frame._scrollBarImageColor3 = _scrollBarImageColor3;
            frame._scrollBarImageTransparency = _scrollBarImageTransparency;
            frame._automaticCanvasSize = _automaticCanvasSize;
            frame._elasticBehavior = _elasticBehavior;
            frame._verticalScrollBarPosition = _verticalScrollBarPosition;
            frame._horizontalScrollBarInset = _horizontalScrollBarInset;
            frame._verticalScrollBarInset = _verticalScrollBarInset;
        }
    }
}
