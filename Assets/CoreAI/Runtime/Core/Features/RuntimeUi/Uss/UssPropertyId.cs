namespace CoreAI.RuntimeUi
{
    /// <summary>
    /// The longhand USS properties the interpreter applies. Shorthands (<c>margin</c>, <c>padding</c>,
    /// <c>border-width</c>, <c>border-color</c>, <c>border-radius</c>, <c>flex</c>, <c>transition</c>,
    /// <c>-unity-text-outline</c>) expand onto these. The numeric values index bit sets, so they stay
    /// dense and below 128.
    /// </summary>
    public enum UssPropertyId
    {
        /// <summary><c>display</c></summary>
        Display = 0,

        /// <summary><c>position</c></summary>
        Position,

        /// <summary><c>flex-direction</c></summary>
        FlexDirection,

        /// <summary><c>flex-wrap</c></summary>
        FlexWrap,

        /// <summary><c>flex-grow</c></summary>
        FlexGrow,

        /// <summary><c>flex-shrink</c></summary>
        FlexShrink,

        /// <summary><c>flex-basis</c></summary>
        FlexBasis,

        /// <summary><c>align-items</c></summary>
        AlignItems,

        /// <summary><c>align-self</c></summary>
        AlignSelf,

        /// <summary><c>align-content</c></summary>
        AlignContent,

        /// <summary><c>justify-content</c></summary>
        JustifyContent,

        /// <summary><c>left</c></summary>
        Left,

        /// <summary><c>top</c></summary>
        Top,

        /// <summary><c>right</c></summary>
        Right,

        /// <summary><c>bottom</c></summary>
        Bottom,

        /// <summary><c>width</c></summary>
        Width,

        /// <summary><c>height</c></summary>
        Height,

        /// <summary><c>min-width</c></summary>
        MinWidth,

        /// <summary><c>min-height</c></summary>
        MinHeight,

        /// <summary><c>max-width</c></summary>
        MaxWidth,

        /// <summary><c>max-height</c></summary>
        MaxHeight,

        /// <summary><c>overflow</c></summary>
        Overflow,

        /// <summary><c>margin-left</c></summary>
        MarginLeft,

        /// <summary><c>margin-top</c></summary>
        MarginTop,

        /// <summary><c>margin-right</c></summary>
        MarginRight,

        /// <summary><c>margin-bottom</c></summary>
        MarginBottom,

        /// <summary><c>padding-left</c></summary>
        PaddingLeft,

        /// <summary><c>padding-top</c></summary>
        PaddingTop,

        /// <summary><c>padding-right</c></summary>
        PaddingRight,

        /// <summary><c>padding-bottom</c></summary>
        PaddingBottom,

        /// <summary><c>border-left-width</c></summary>
        BorderLeftWidth,

        /// <summary><c>border-top-width</c></summary>
        BorderTopWidth,

        /// <summary><c>border-right-width</c></summary>
        BorderRightWidth,

        /// <summary><c>border-bottom-width</c></summary>
        BorderBottomWidth,

        /// <summary><c>border-left-color</c></summary>
        BorderLeftColor,

        /// <summary><c>border-top-color</c></summary>
        BorderTopColor,

        /// <summary><c>border-right-color</c></summary>
        BorderRightColor,

        /// <summary><c>border-bottom-color</c></summary>
        BorderBottomColor,

        /// <summary><c>border-top-left-radius</c></summary>
        BorderTopLeftRadius,

        /// <summary><c>border-top-right-radius</c></summary>
        BorderTopRightRadius,

        /// <summary><c>border-bottom-right-radius</c></summary>
        BorderBottomRightRadius,

        /// <summary><c>border-bottom-left-radius</c></summary>
        BorderBottomLeftRadius,

        /// <summary><c>color</c></summary>
        Color,

        /// <summary><c>font-size</c></summary>
        FontSize,

        /// <summary><c>-unity-font-style</c></summary>
        UnityFontStyle,

        /// <summary><c>-unity-text-align</c></summary>
        UnityTextAlign,

        /// <summary><c>white-space</c></summary>
        WhiteSpace,

        /// <summary><c>text-overflow</c></summary>
        TextOverflow,

        /// <summary><c>letter-spacing</c></summary>
        LetterSpacing,

        /// <summary><c>word-spacing</c></summary>
        WordSpacing,

        /// <summary><c>-unity-paragraph-spacing</c></summary>
        UnityParagraphSpacing,

        /// <summary><c>-unity-text-outline-width</c></summary>
        UnityTextOutlineWidth,

        /// <summary><c>-unity-text-outline-color</c></summary>
        UnityTextOutlineColor,

        /// <summary><c>text-shadow</c></summary>
        TextShadow,

        /// <summary><c>background-color</c></summary>
        BackgroundColor,

        /// <summary><c>background-image</c></summary>
        BackgroundImage,

        /// <summary><c>-unity-background-image-tint-color</c></summary>
        UnityBackgroundImageTintColor,

        /// <summary><c>-unity-background-scale-mode</c></summary>
        UnityBackgroundScaleMode,

        /// <summary><c>-unity-slice-left</c></summary>
        UnitySliceLeft,

        /// <summary><c>-unity-slice-top</c></summary>
        UnitySliceTop,

        /// <summary><c>-unity-slice-right</c></summary>
        UnitySliceRight,

        /// <summary><c>-unity-slice-bottom</c></summary>
        UnitySliceBottom,

        /// <summary><c>-unity-slice-scale</c></summary>
        UnitySliceScale,

        /// <summary><c>opacity</c></summary>
        Opacity,

        /// <summary><c>visibility</c></summary>
        Visibility,

        /// <summary><c>translate</c></summary>
        Translate,

        /// <summary><c>scale</c></summary>
        Scale,

        /// <summary><c>rotate</c></summary>
        Rotate,

        /// <summary><c>transform-origin</c></summary>
        TransformOrigin,

        /// <summary><c>transition-property</c></summary>
        TransitionProperty,

        /// <summary><c>transition-duration</c></summary>
        TransitionDuration,

        /// <summary><c>transition-timing-function</c></summary>
        TransitionTimingFunction,

        /// <summary><c>transition-delay</c></summary>
        TransitionDelay,

        /// <summary>Number of longhand properties; not a property.</summary>
        Count
    }
}
