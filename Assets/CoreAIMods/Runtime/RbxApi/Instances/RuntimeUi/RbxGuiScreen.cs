using System;
using System.Globalization;
using CoreAI.Mods.Rbx.Datatypes;

namespace CoreAI.Mods.Rbx.Instances.RuntimeUi
{
    /// <summary>
    /// One shown <c>ScreenGui</c>: the host screen it is drawn on, the node tree of its GUI objects, and the
    /// translation of their properties into typed styles. It is the observer of every instance it shows, so a
    /// property write charges the owning mod's write budget and marks only that node dirty.
    /// </summary>
    internal sealed class RbxGuiScreen : IRbxGuiObserver
    {
        private const float HoverShade = 0.8f;
        private const float MaxTextSize = 100f;
        private const string ScrollModeHorizontal = "Horizontal";
        private const string ScrollModeVertical = "Vertical";
        private const string ScrollModeBoth = "VerticalAndHorizontal";
        private const string ScrollerAuto = "Auto";
        private const string ScrollerHidden = "Hidden";

        /// <summary>The class that puts a ScrollingFrame's vertical bar on its left side.</summary>
        internal const string ScrollLeftClass = "rbx-scroll-left";

        /// <summary>The class that puts a ScrollingFrame's vertical bar on its right side.</summary>
        internal const string ScrollRightClass = "rbx-scroll-right";

        private readonly Action _onResized;
        private float _viewportW = -1f;
        private float _viewportH = -1f;
        private bool _boxesStale = true;

        public RbxGuiScreen(RbxGuiPresenter presenter, RbxScreenGui screenGui, string ownerModId, IRbxUiScreen port,
            RbxGuiNode root)
        {
            Presenter = presenter;
            ScreenGui = screenGui;
            OwnerModId = ownerModId;
            Port = port;
            Root = root;
            root.Dirty = RbxGuiChange.Screen;
            _onResized = OnResized;
            port.Resized += _onResized;
            LayoutDirty = true;
            HasDirty = true;
        }

        public RbxGuiPresenter Presenter { get; }

        public RbxScreenGui ScreenGui { get; }

        public string OwnerModId { get; }

        public IRbxUiScreen Port { get; }

        public RbxGuiNode Root { get; }

        public int ElementCount { get; set; }

        public bool LayoutDirty { get; set; }

        public bool HasDirty { get; set; }

        public bool IsClosed { get; private set; }

        public void BeforeWrite(RbxInstance source, string member)
        {
            if (!IsClosed)
            {
                Presenter.World.ChargeExternalWrite(OwnerModId, source.ClassName, member);
            }
        }

        public void AfterWrite(RbxInstance source, RbxGuiChange change)
        {
            if (!IsClosed)
            {
                Presenter.MarkDirty(source, change);
            }
        }

        public void EnsureLayout()
        {
            if (!IsClosed)
            {
                Presenter.EnsureLayout(this);
            }
        }

        public void Close()
        {
            if (IsClosed)
            {
                return;
            }

            IsClosed = true;
            Port.Resized -= _onResized;
        }

        /// <summary>Lays out the screen when its layout or viewport changed; then pushes what differs when asked.</summary>
        public void Update(bool push)
        {
            if (IsClosed || Port.IsDestroyed)
            {
                return;
            }

            float width = Port.Width;
            float height = Port.Height;
            if (!width.Equals(_viewportW) || !height.Equals(_viewportH))
            {
                _viewportW = width;
                _viewportH = height;
                LayoutDirty = true;
            }

            if (LayoutDirty)
            {
                LayoutDirty = false;
                RbxGuiLayout.LayoutScreen(Root, width, height);
                _boxesStale = true;
                HasDirty = true;
            }

            if (!push || !HasDirty)
            {
                return;
            }

            HasDirty = false;
            bool boxes = _boxesStale;
            _boxesStale = false;
            if ((Root.Dirty & RbxGuiChange.Screen) != 0)
            {
                Root.Dirty = RbxGuiChange.None;
                Port.SetVisible(ScreenGui.Enabled);
                Port.SetSortOrder(ScreenGui.DisplayOrder);
            }

            for (int i = 0; i < Root.Children.Count; i++)
            {
                PushTree(Root.Children[i], 0f, 0f, boxes);
            }
        }

        private void PushTree(RbxGuiNode node, float parentInnerX, float parentInnerY, bool boxes)
        {
            if (node.Element == null)
            {
                return;
            }

            if (boxes || node.Dirty != RbxGuiChange.None)
            {
                Push(node, parentInnerX, parentInnerY);
            }

            float innerX = node.X - node.Outline + node.Border;
            float innerY = node.Y - node.Outline + node.Border;
            if (node.Gui is RbxScrollingFrame)
            {
                // WHY add the offset back: the layout moved the children by it (their AbsolutePosition), while the
                // host moves the canvas they are drawn on, so each child is placed on the canvas unscrolled.
                innerX -= node.ScrollX;
                innerY -= node.ScrollY;
            }

            for (int i = 0; i < node.Children.Count; i++)
            {
                PushTree(node.Children[i], innerX, innerY, boxes);
            }
        }

        private void Push(RbxGuiNode node, float parentInnerX, float parentInnerY)
        {
            RbxGuiChange change = node.Dirty;
            node.Dirty = RbxGuiChange.None;
            IRbxUiElement element = node.Element;
            RbxGuiObject gui = node.Object;
            if (!element.IsAlive || gui == null)
            {
                return;
            }

            IRbxGuiTextObject text = gui as IRbxGuiTextObject;
            ResolveBorder(node, text != null, out RbxColor3 borderColor, out float borderAlpha);
            float left = node.X - node.Outline - parentInnerX;
            float top = node.Y - node.Outline - parentInnerY;
            float width = Math.Max(0f, node.W) + node.Outline * 2f;
            float height = Math.Max(0f, node.H) + node.Outline * 2f;
            Set(node, RbxUiStyleProperty.Left, RbxUiStyleValue.Pixels(left));
            Set(node, RbxUiStyleProperty.Top, RbxUiStyleValue.Pixels(top));
            Set(node, RbxUiStyleProperty.Width, RbxUiStyleValue.Pixels(width));
            Set(node, RbxUiStyleProperty.Height, RbxUiStyleValue.Pixels(height));
            SetUnlessDefault(node, RbxUiStyleProperty.Rotate, RbxUiStyleValue.Degrees(gui.Rotation), gui.Rotation == 0f);

            PushPaint(node, gui, text != null, borderColor, borderAlpha);
            if (!node.PushedVisibleSet || node.PushedVisible != gui.Visible)
            {
                element.SetVisible(gui.Visible);
                node.PushedVisibleSet = true;
                node.PushedVisible = gui.Visible;
            }

            if (!node.PushedOrderSet || node.PushedOrder != gui.ZIndex)
            {
                element.SetZOrder(gui.ZIndex);
                node.PushedOrderSet = true;
                node.PushedOrder = gui.ZIndex;
            }

            string picking = IsPickable(gui) ? "Position" : "Ignore";
            if (!ReferenceEquals(picking, node.PushedPicking))
            {
                element.SetAttribute("picking-mode", picking);
                node.PushedPicking = picking;
            }

            if (text != null)
            {
                PushText(node, gui, text.TextState);
            }

            if (gui is IRbxGuiImageObject image && (change & RbxGuiChange.Image) != 0)
            {
                PushImage(node, image.ImageState);
            }

            if (gui is RbxTextBox box && (change & RbxGuiChange.Field) != 0)
            {
                PushField(node, box);
            }

            if (gui is RbxScrollingFrame scrolling)
            {
                PushScroll(node, scrolling);
            }
        }

        /// <summary>
        /// Pushes a ScrollingFrame's scroll state: the canvas size (the host's scroll range), the bars (which axes,
        /// thickness, side, color) and the offset the layout kept in range. Only what differs from the last push is
        /// written, and none of it is charged: the script already paid for the property writes behind it.
        /// </summary>
        private static void PushScroll(RbxGuiNode node, RbxScrollingFrame frame)
        {
            IRbxUiElement element = node.Element;
            IRbxUiElement canvas = node.Canvas;
            float canvasW = node.CanvasW + node.InsetV;
            float canvasH = node.CanvasH + node.InsetH;
            if (canvas != null && canvas.IsAlive && (!canvasW.Equals(node.PushedCanvasW) || !canvasH.Equals(node.PushedCanvasH)))
            {
                canvas.SetStyle(RbxUiStyleProperty.Width, RbxUiStyleValue.Pixels(canvasW));
                canvas.SetStyle(RbxUiStyleProperty.Height, RbxUiStyleValue.Pixels(canvasH));
                node.PushedCanvasW = canvasW;
                node.PushedCanvasH = canvasH;
            }

            RbxScrollingDirection direction = frame.ScrollingDirection;
            string mode = direction == RbxScrollingDirection.X ? ScrollModeHorizontal
                : direction == RbxScrollingDirection.Y ? ScrollModeVertical : ScrollModeBoth;
            if (!ReferenceEquals(mode, node.PushedMode))
            {
                element.SetAttribute("mode", mode);
                node.PushedMode = mode;
            }

            int thickness = Math.Max(0, frame.ScrollBarThickness);
            bool drawn = frame.ScrollingEnabled && thickness > 0;
            string barX = drawn && direction != RbxScrollingDirection.Y ? ScrollerAuto : ScrollerHidden;
            string barY = drawn && direction != RbxScrollingDirection.X ? ScrollerAuto : ScrollerHidden;
            if (!ReferenceEquals(barX, node.PushedBarX))
            {
                element.SetAttribute("horizontal-scroller-visibility", barX);
                node.PushedBarX = barX;
            }

            if (!ReferenceEquals(barY, node.PushedBarY))
            {
                element.SetAttribute("vertical-scroller-visibility", barY);
                node.PushedBarY = barY;
            }

            if (thickness != node.PushedBarSize)
            {
                element.SetStyle("--rbx-scroll-bar-size", thickness.ToString(CultureInfo.InvariantCulture) + "px");
                node.PushedBarSize = thickness;
            }

            RbxColor3 tint = frame.ScrollBarImageColor3;
            float alpha = Clamp01(1f - frame.ScrollBarImageTransparency);
            if (!node.PushedBarTintSet || tint != node.PushedBarTint || !alpha.Equals(node.PushedBarAlpha))
            {
                element.SetStyle("--rbx-scroll-bar-color", RbxUiStyleValue.Color(tint.R, tint.G, tint.B, alpha).ToString());
                node.PushedBarTintSet = true;
                node.PushedBarTint = tint;
                node.PushedBarAlpha = alpha;
            }

            string side = frame.VerticalScrollBarPosition == 1 ? ScrollLeftClass : ScrollRightClass;
            if (!ReferenceEquals(side, node.PushedBarSide))
            {
                if (node.PushedBarSide != null)
                {
                    element.RemoveClass(node.PushedBarSide);
                }

                element.AddClass(side);
                node.PushedBarSide = side;
            }

            if (!node.PushedScrollSet || !node.ScrollX.Equals(node.PushedScrollX) || !node.ScrollY.Equals(node.PushedScrollY))
            {
                element.SetScrollOffset(node.ScrollX, node.ScrollY);
                node.PushedScrollSet = true;
                node.PushedScrollX = node.ScrollX;
                node.PushedScrollY = node.ScrollY;
            }
        }

        private static void ResolveBorder(RbxGuiNode node, bool isText, out RbxColor3 color, out float alpha)
        {
            RbxGuiObject gui = node.Object;
            RbxUiStroke stroke = node.Stroke != null && node.Stroke.Enabled ? node.Stroke : null;
            if (stroke != null && (stroke.ApplyStrokeMode == RbxApplyStrokeMode.Border || !isText))
            {
                float thickness = Math.Max(0f, stroke.Thickness);
                node.Border = thickness;
                node.Outline = stroke.BorderStrokePosition == RbxBorderStrokePosition.Outer ? thickness
                    : stroke.BorderStrokePosition == RbxBorderStrokePosition.Center ? thickness * 0.5f : 0f;
                color = stroke.Color;
                alpha = Clamp01(1f - stroke.Transparency);
                return;
            }

            float size = Math.Max(0, gui.BorderSizePixel);
            node.Border = size;
            node.Outline = gui.BorderMode == RbxBorderMode.Outline ? size
                : gui.BorderMode == RbxBorderMode.Middle ? size * 0.5f : 0f;
            color = gui.BorderColor3;
            alpha = Clamp01(1f - gui.BackgroundTransparency);
        }

        private static void PushPaint(RbxGuiNode node, RbxGuiObject gui, bool isText, RbxColor3 borderColor,
            float borderAlpha)
        {
            RbxColor3 background = gui.BackgroundColor3;
            float shade = node.Hovered && gui is RbxGuiButton button && button.AutoButtonColor ? HoverShade : 1f;
            Set(node, RbxUiStyleProperty.BackgroundColor, RbxUiStyleValue.Color(background.R * shade,
                background.G * shade, background.B * shade, Clamp01(1f - gui.BackgroundTransparency)));
            RbxUiStyleValue borderWidth = RbxUiStyleValue.Pixels(node.Border);
            bool noBorder = node.Border <= 0f;
            SetUnlessDefault(node, RbxUiStyleProperty.BorderLeftWidth, borderWidth, noBorder);
            SetUnlessDefault(node, RbxUiStyleProperty.BorderTopWidth, borderWidth, noBorder);
            SetUnlessDefault(node, RbxUiStyleProperty.BorderRightWidth, borderWidth, noBorder);
            SetUnlessDefault(node, RbxUiStyleProperty.BorderBottomWidth, borderWidth, noBorder);
            if (!noBorder || node.Pushed[(int)RbxUiStyleProperty.BorderLeftColor].Kind != RbxUiStyleValueKind.None)
            {
                RbxUiStyleValue color = RbxUiStyleValue.Color(borderColor.R, borderColor.G, borderColor.B, borderAlpha);
                Set(node, RbxUiStyleProperty.BorderLeftColor, color);
                Set(node, RbxUiStyleProperty.BorderTopColor, color);
                Set(node, RbxUiStyleProperty.BorderRightColor, color);
                Set(node, RbxUiStyleProperty.BorderBottomColor, color);
            }

            RbxUiCorner corner = node.Corner;
            if (corner != null || node.Pushed[(int)RbxUiStyleProperty.BorderTopLeftRadius].Kind != RbxUiStyleValueKind.None)
            {
                float extra = node.Outline;
                Set(node, RbxUiStyleProperty.BorderTopLeftRadius, Radius(corner?.TopLeftRadius, node, extra));
                Set(node, RbxUiStyleProperty.BorderTopRightRadius, Radius(corner?.TopRightRadius, node, extra));
                Set(node, RbxUiStyleProperty.BorderBottomRightRadius, Radius(corner?.BottomRightRadius, node, extra));
                Set(node, RbxUiStyleProperty.BorderBottomLeftRadius, Radius(corner?.BottomLeftRadius, node, extra));
            }

            bool truncates = isText && ((IRbxGuiTextObject)gui).TextState.TextTruncate != RbxTextTruncate.None;
            bool clips = gui.ClipsDescendants || truncates;
            SetUnlessDefault(node, RbxUiStyleProperty.Overflow,
                RbxUiStyleValue.FromKeyword(clips ? RbxUiKeywords.Hidden : RbxUiKeywords.Visible), !clips);
            float scale = node.Scale != null ? node.Scale.Scale : 1f;
            SetUnlessDefault(node, RbxUiStyleProperty.Scale, RbxUiStyleValue.Pair(scale, scale), scale == 1f);
        }

        private static RbxUiStyleValue Radius(RbxUDim? radius, RbxGuiNode node, float extra)
        {
            if (!radius.HasValue)
            {
                return RbxUiStyleValue.Pixels(0f);
            }

            float pixels = RbxUiCorner.ResolveRadius(radius.Value, node.W, node.H);
            return RbxUiStyleValue.Pixels(pixels > 0f ? pixels + extra : 0f);
        }

        private static void PushText(RbxGuiNode node, RbxGuiObject gui, RbxGuiText state)
        {
            IRbxUiElement element = node.Element;
            string shown = state.Text;
            if (!string.Equals(shown, node.PushedText, StringComparison.Ordinal))
            {
                element.SetText(shown);
                node.PushedText = shown;
            }

            string rich = state.RichText ? "true" : "false";
            if (!(gui is RbxTextBox) && !ReferenceEquals(rich, node.PushedRichText))
            {
                element.SetAttribute("enable-rich-text", rich);
                node.PushedRichText = rich;
            }

            RbxColor3 color = state.TextColor3;
            float alpha = Clamp01(1f - state.TextTransparency);
            bool fieldStyleChanged = Set(node, RbxUiStyleProperty.Color,
                RbxUiStyleValue.Color(color.R, color.G, color.B, alpha));
            float size = state.TextScaled
                ? RbxGuiTextMetrics.FitTextSize(state.ContentText, node.W, node.H, state.TextWrapped, state.LineHeight)
                : Math.Max(0f, Math.Min(MaxTextSize, state.TextSize));
            fieldStyleChanged |= Set(node, RbxUiStyleProperty.FontSize, RbxUiStyleValue.Pixels(size));
            string fontStyle = state.IsBold ? (state.IsItalic ? RbxUiKeywords.BoldAndItalic : RbxUiKeywords.Bold)
                : state.IsItalic ? RbxUiKeywords.Italic : RbxUiKeywords.Normal;
            Set(node, RbxUiStyleProperty.UnityFontStyle, RbxUiStyleValue.FromKeyword(fontStyle));
            string align = AlignKeyword(state.TextXAlignment, state.TextYAlignment);
            fieldStyleChanged |= Set(node, RbxUiStyleProperty.UnityTextAlign, RbxUiStyleValue.FromKeyword(align));
            Set(node, RbxUiStyleProperty.WhiteSpace,
                RbxUiStyleValue.FromKeyword(state.TextWrapped ? RbxUiKeywords.Normal : RbxUiKeywords.NoWrap));
            bool truncate = state.TextTruncate != RbxTextTruncate.None;
            SetUnlessDefault(node, RbxUiStyleProperty.TextOverflow,
                RbxUiStyleValue.FromKeyword(truncate ? RbxUiKeywords.Ellipsis : RbxUiKeywords.Clip), !truncate);
            RbxUiStroke stroke = node.Stroke != null && node.Stroke.Enabled
                                                     && node.Stroke.ApplyStrokeMode == RbxApplyStrokeMode.Contextual
                ? node.Stroke
                : null;
            float outlineWidth = 0f;
            RbxColor3 outlineColor = state.TextStrokeColor3;
            float outlineAlpha = Clamp01(1f - state.TextStrokeTransparency);
            if (stroke != null)
            {
                outlineWidth = Math.Max(0f, stroke.Thickness);
                outlineColor = stroke.Color;
                outlineAlpha = Clamp01(1f - stroke.Transparency);
            }
            else if (state.TextStrokeTransparency < 1f)
            {
                outlineWidth = 1f;
            }

            SetUnlessDefault(node, RbxUiStyleProperty.UnityTextOutlineWidth, RbxUiStyleValue.Pixels(outlineWidth),
                outlineWidth <= 0f);
            if (outlineWidth > 0f
                || node.Pushed[(int)RbxUiStyleProperty.UnityTextOutlineColor].Kind != RbxUiStyleValueKind.None)
            {
                Set(node, RbxUiStyleProperty.UnityTextOutlineColor,
                    RbxUiStyleValue.Color(outlineColor.R, outlineColor.G, outlineColor.B, outlineAlpha));
            }

            if (gui is RbxTextBox && fieldStyleChanged)
            {
                PushFieldTextStyle(node, color, alpha, size, align);
            }
        }

        private static void PushFieldTextStyle(RbxGuiNode node, RbxColor3 color, float alpha, float size,
            string align)
        {
            string colorText = RbxUiStyleValue.Color(color.R, color.G, color.B, alpha).ToString();
            if (!string.Equals(colorText, node.PushedFieldColor, StringComparison.Ordinal))
            {
                node.Element.SetStyle("--rbx-text-color", colorText);
                node.PushedFieldColor = colorText;
            }

            string sizeText = size.ToString("R", CultureInfo.InvariantCulture) + "px";
            if (!string.Equals(sizeText, node.PushedFieldSize, StringComparison.Ordinal))
            {
                node.Element.SetStyle("--rbx-font-size", sizeText);
                node.PushedFieldSize = sizeText;
            }

            if (!ReferenceEquals(align, node.PushedFieldAlign))
            {
                node.Element.SetStyle("--rbx-text-align", align);
                node.PushedFieldAlign = align;
            }
        }

        private static void PushImage(RbxGuiNode node, RbxGuiImage image)
        {
            IRbxUiElement element = node.Element;
            string source = image.IsDrawable ? image.Image : string.Empty;
            if (!string.Equals(source, node.PushedImage ?? string.Empty, StringComparison.Ordinal))
            {
                if (source.Length > 0)
                {
                    element.SetAttribute("image", source);
                }

                node.PushedImage = source;
            }

            RbxColor3 tint = image.ImageColor3;
            string tintText = HexColor(tint.R, tint.G, tint.B, Clamp01(1f - image.ImageTransparency));
            if (!string.Equals(tintText, node.PushedTint, StringComparison.Ordinal))
            {
                element.SetAttribute("tint-color", tintText);
                node.PushedTint = tintText;
            }

            string scaleMode = image.ScaleType == RbxScaleType.Fit ? "ScaleToFit"
                : image.ScaleType == RbxScaleType.Crop ? "ScaleAndCrop" : "StretchToFill";
            if (!ReferenceEquals(scaleMode, node.PushedScaleMode))
            {
                element.SetAttribute("scale-mode", scaleMode);
                node.PushedScaleMode = scaleMode;
            }
        }

        private static void PushField(RbxGuiNode node, RbxTextBox box)
        {
            IRbxUiElement element = node.Element;
            if (!string.Equals(box.PlaceholderText, node.PushedPlaceholder ?? string.Empty, StringComparison.Ordinal))
            {
                element.SetAttribute("placeholder-text", box.PlaceholderText);
                node.PushedPlaceholder = box.PlaceholderText;
            }

            string multiline = box.MultiLine ? "true" : "false";
            if (!ReferenceEquals(multiline, node.PushedMultiline))
            {
                element.SetAttribute("multiline", multiline);
                node.PushedMultiline = multiline;
            }

            string readOnly = box.TextEditable ? "false" : "true";
            if (!ReferenceEquals(readOnly, node.PushedReadOnly))
            {
                element.SetAttribute("readonly", readOnly);
                node.PushedReadOnly = readOnly;
            }
        }

        private static bool IsPickable(RbxGuiObject gui)
        {
            return gui.Interactable && (gui.Active || gui is RbxGuiButton || gui is RbxTextBox
                                        || gui is RbxScrollingFrame || gui.ListensToPointer);
        }

        private static bool Set(RbxGuiNode node, RbxUiStyleProperty property, RbxUiStyleValue value)
        {
            int index = (int)property;
            if (node.Pushed[index].SameAs(value))
            {
                return false;
            }

            node.Pushed[index] = value;
            node.Element.SetStyle(property, value);
            return true;
        }

        /// <summary>Pushes a value, except a default one nothing was pushed for yet (a fresh element already has it).</summary>
        private static void SetUnlessDefault(RbxGuiNode node, RbxUiStyleProperty property, RbxUiStyleValue value,
            bool isDefault)
        {
            if (isDefault && node.Pushed[(int)property].Kind == RbxUiStyleValueKind.None)
            {
                return;
            }

            Set(node, property, value);
        }

        private static string AlignKeyword(RbxTextXAlignment x, RbxTextYAlignment y)
        {
            switch (y)
            {
                case RbxTextYAlignment.Top:
                    return x == RbxTextXAlignment.Left ? RbxUiKeywords.UpperLeft
                        : x == RbxTextXAlignment.Right ? RbxUiKeywords.UpperRight : RbxUiKeywords.UpperCenter;
                case RbxTextYAlignment.Bottom:
                    return x == RbxTextXAlignment.Left ? RbxUiKeywords.LowerLeft
                        : x == RbxTextXAlignment.Right ? RbxUiKeywords.LowerRight : RbxUiKeywords.LowerCenter;
                default:
                    return x == RbxTextXAlignment.Left ? RbxUiKeywords.MiddleLeft
                        : x == RbxTextXAlignment.Right ? RbxUiKeywords.MiddleRight : RbxUiKeywords.MiddleCenter;
            }
        }

        private static string HexColor(float r, float g, float b, float a)
        {
            return "#" + Byte(r).ToString("X2", CultureInfo.InvariantCulture)
                       + Byte(g).ToString("X2", CultureInfo.InvariantCulture)
                       + Byte(b).ToString("X2", CultureInfo.InvariantCulture)
                       + Byte(a).ToString("X2", CultureInfo.InvariantCulture);
        }

        private static int Byte(float value)
        {
            return (int)Math.Round(Clamp01(value) * 255f);
        }

        private static float Clamp01(float value)
        {
            return value < 0f ? 0f : value > 1f ? 1f : value;
        }

        private void OnResized()
        {
            LayoutDirty = true;
            HasDirty = true;
        }
    }

    /// <summary>
    /// The text-size estimate behind <c>TextScaled</c>: the host measures no text for the engine-free layer, so
    /// the largest size that fits is estimated from the box and the character count (an average glyph is about
    /// 0.55 of the size wide, a line about 1.15 of it tall), capped at 100 like Roblox.
    /// </summary>
    internal static class RbxGuiTextMetrics
    {
        private const float GlyphWidth = 0.55f;
        private const float LineFactor = 1.15f;

        public static float FitTextSize(string text, float width, float height, bool wrapped, float lineHeight)
        {
            float line = LineFactor * Math.Max(0.5f, lineHeight);
            float byHeight = Math.Max(0f, height) / line;
            int characters = string.IsNullOrEmpty(text) ? 0 : text.Length;
            float size;
            if (characters == 0)
            {
                size = byHeight;
            }
            else if (!wrapped)
            {
                size = Math.Min(byHeight, Math.Max(0f, width) / (GlyphWidth * characters));
            }
            else
            {
                float area = Math.Max(0f, width) * Math.Max(0f, height);
                size = Math.Min(byHeight, (float)Math.Sqrt(area / (GlyphWidth * characters * line)));
            }

            return (float)Math.Floor(Math.Max(1f, Math.Min(100f, size)));
        }
    }
}
