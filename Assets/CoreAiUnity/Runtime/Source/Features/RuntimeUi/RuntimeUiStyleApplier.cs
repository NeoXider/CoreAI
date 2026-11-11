using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

namespace CoreAI.RuntimeUi
{
    /// <summary>
    /// Writes cascade results onto UI Toolkit inline styles. It owns every inline property it sets: a
    /// property set on the previous pass and absent from this one is reset to <c>StyleKeyword.Null</c>,
    /// so a rule that stops matching (a pseudo-state ending, a class removed, a USS swap) leaves nothing
    /// behind. Usable without any markup: the typed C# API and a Roblox GUI layer call
    /// <see cref="Apply(IStyle, UssPropertyId, UssValue, out string)"/> directly.
    /// </summary>
    public sealed class RuntimeUiStyleApplier
    {
        private readonly IRuntimeUiAssetResolver _assets;
        private readonly Dictionary<UssValue[], List<StylePropertyName>> _propertyLists = new();
        private readonly Dictionary<UssValue[], List<TimeValue>> _timeLists = new();
        private readonly Dictionary<UssValue[], List<EasingFunction>> _easingLists = new();

        /// <summary>Creates an applier.</summary>
        /// <param name="assets">Resolves <c>background-image</c>; null refuses every image.</param>
        public RuntimeUiStyleApplier(IRuntimeUiAssetResolver assets)
        {
            _assets = assets;
        }

        /// <summary>
        /// Applies a resolved style to an element and resets the properties applied last time but not now.
        /// </summary>
        /// <param name="element">Target.</param>
        /// <param name="resolved">The cascade result.</param>
        /// <param name="applied">The set applied on the previous pass; updated to this pass.</param>
        /// <param name="failures">Receives a report per value that could not be applied; may be null.</param>
        /// <returns>True when the result sets <c>display: none</c>.</returns>
        public bool Apply(VisualElement element, UssResolvedStyle resolved, ref RuntimeUiPropertyMask applied,
            List<string> failures)
        {
            IStyle style = element.style;
            RuntimeUiPropertyMask now = default;
            bool displayNone = false;
            IReadOnlyList<UssPropertyId> set = resolved.SetProperties;
            for (int i = 0; i < set.Count; i++)
            {
                UssPropertyId id = set[i];
                UssValue value = resolved.Get(id);
                if (Apply(style, id, value, out string error))
                {
                    now.Add(id);
                    if (id == UssPropertyId.Display && value.IsKeyword("none"))
                    {
                        displayNone = true;
                    }
                }
                else if (error != null)
                {
                    failures?.Add(error);
                }
            }

            if (!applied.IsEmpty)
            {
                for (int i = 0; i < (int)UssPropertyId.Count; i++)
                {
                    UssPropertyId id = (UssPropertyId)i;
                    if (applied.Has(id) && !now.Has(id))
                    {
                        Reset(style, id);
                    }
                }
            }

            applied = now;
            return displayNone;
        }

        /// <summary>Resets every property in <paramref name="applied"/> and clears it.</summary>
        public static void ResetAll(VisualElement element, ref RuntimeUiPropertyMask applied)
        {
            if (applied.IsEmpty)
            {
                return;
            }

            IStyle style = element.style;
            for (int i = 0; i < (int)UssPropertyId.Count; i++)
            {
                UssPropertyId id = (UssPropertyId)i;
                if (applied.Has(id))
                {
                    Reset(style, id);
                }
            }

            applied.Clear();
        }

        /// <summary>Writes one typed value onto an inline style.</summary>
        /// <returns>False with an error when the value does not fit the property (or an image is unknown).</returns>
        public bool Apply(IStyle style, UssPropertyId id, UssValue value, out string error)
        {
            error = null;
            if (value.IsKeyword(UssKeywords.Initial))
            {
                SetKeyword(style, id, StyleKeyword.Initial);
                return true;
            }

            switch (id)
            {
                case UssPropertyId.Display:
                    style.display = value.IsKeyword("none") ? DisplayStyle.None : DisplayStyle.Flex;
                    return true;
                case UssPropertyId.Position:
                    style.position = value.IsKeyword("absolute") ? UnityEngine.UIElements.Position.Absolute
                        : UnityEngine.UIElements.Position.Relative;
                    return true;
                case UssPropertyId.FlexDirection:
                    style.flexDirection = ToFlexDirection(value.Keyword);
                    return true;
                case UssPropertyId.FlexWrap:
                    style.flexWrap = value.Keyword == "wrap" ? Wrap.Wrap
                        : value.Keyword == "wrap-reverse" ? Wrap.WrapReverse : Wrap.NoWrap;
                    return true;
                case UssPropertyId.FlexGrow:
                    style.flexGrow = value.A;
                    return true;
                case UssPropertyId.FlexShrink:
                    style.flexShrink = value.A;
                    return true;
                case UssPropertyId.FlexBasis:
                    style.flexBasis = ToStyleLength(value);
                    return true;
                case UssPropertyId.AlignItems:
                    style.alignItems = ToAlign(value.Keyword);
                    return true;
                case UssPropertyId.AlignSelf:
                    style.alignSelf = ToAlign(value.Keyword);
                    return true;
                case UssPropertyId.AlignContent:
                    style.alignContent = ToAlign(value.Keyword);
                    return true;
                case UssPropertyId.JustifyContent:
                    style.justifyContent = ToJustify(value.Keyword);
                    return true;
                case UssPropertyId.Left:
                    style.left = ToStyleLength(value);
                    return true;
                case UssPropertyId.Top:
                    style.top = ToStyleLength(value);
                    return true;
                case UssPropertyId.Right:
                    style.right = ToStyleLength(value);
                    return true;
                case UssPropertyId.Bottom:
                    style.bottom = ToStyleLength(value);
                    return true;
                case UssPropertyId.Width:
                    style.width = ToStyleLength(value);
                    return true;
                case UssPropertyId.Height:
                    style.height = ToStyleLength(value);
                    return true;
                case UssPropertyId.MinWidth:
                    style.minWidth = ToStyleLength(value);
                    return true;
                case UssPropertyId.MinHeight:
                    style.minHeight = ToStyleLength(value);
                    return true;
                case UssPropertyId.MaxWidth:
                    style.maxWidth = ToStyleLength(value);
                    return true;
                case UssPropertyId.MaxHeight:
                    style.maxHeight = ToStyleLength(value);
                    return true;
                case UssPropertyId.Overflow:
                    style.overflow = value.IsKeyword("hidden") ? Overflow.Hidden : Overflow.Visible;
                    return true;
                case UssPropertyId.MarginLeft:
                    style.marginLeft = ToStyleLength(value);
                    return true;
                case UssPropertyId.MarginTop:
                    style.marginTop = ToStyleLength(value);
                    return true;
                case UssPropertyId.MarginRight:
                    style.marginRight = ToStyleLength(value);
                    return true;
                case UssPropertyId.MarginBottom:
                    style.marginBottom = ToStyleLength(value);
                    return true;
                case UssPropertyId.PaddingLeft:
                    style.paddingLeft = ToStyleLength(value);
                    return true;
                case UssPropertyId.PaddingTop:
                    style.paddingTop = ToStyleLength(value);
                    return true;
                case UssPropertyId.PaddingRight:
                    style.paddingRight = ToStyleLength(value);
                    return true;
                case UssPropertyId.PaddingBottom:
                    style.paddingBottom = ToStyleLength(value);
                    return true;
                case UssPropertyId.BorderLeftWidth:
                    style.borderLeftWidth = value.A;
                    return true;
                case UssPropertyId.BorderTopWidth:
                    style.borderTopWidth = value.A;
                    return true;
                case UssPropertyId.BorderRightWidth:
                    style.borderRightWidth = value.A;
                    return true;
                case UssPropertyId.BorderBottomWidth:
                    style.borderBottomWidth = value.A;
                    return true;
                case UssPropertyId.BorderLeftColor:
                    style.borderLeftColor = ToColor(value);
                    return true;
                case UssPropertyId.BorderTopColor:
                    style.borderTopColor = ToColor(value);
                    return true;
                case UssPropertyId.BorderRightColor:
                    style.borderRightColor = ToColor(value);
                    return true;
                case UssPropertyId.BorderBottomColor:
                    style.borderBottomColor = ToColor(value);
                    return true;
                case UssPropertyId.BorderTopLeftRadius:
                    style.borderTopLeftRadius = ToStyleLength(value);
                    return true;
                case UssPropertyId.BorderTopRightRadius:
                    style.borderTopRightRadius = ToStyleLength(value);
                    return true;
                case UssPropertyId.BorderBottomRightRadius:
                    style.borderBottomRightRadius = ToStyleLength(value);
                    return true;
                case UssPropertyId.BorderBottomLeftRadius:
                    style.borderBottomLeftRadius = ToStyleLength(value);
                    return true;
                case UssPropertyId.Color:
                    style.color = ToColor(value);
                    return true;
                case UssPropertyId.FontSize:
                    style.fontSize = ToStyleLength(value);
                    return true;
                case UssPropertyId.UnityFontStyle:
                    style.unityFontStyleAndWeight = ToFontStyle(value.Keyword);
                    return true;
                case UssPropertyId.UnityTextAlign:
                    style.unityTextAlign = ToTextAnchor(value.Keyword);
                    return true;
                case UssPropertyId.WhiteSpace:
                    style.whiteSpace = ToWhiteSpace(value.Keyword);
                    return true;
                case UssPropertyId.TextOverflow:
                    style.textOverflow = value.IsKeyword("ellipsis") ? TextOverflow.Ellipsis : TextOverflow.Clip;
                    return true;
                case UssPropertyId.LetterSpacing:
                    style.letterSpacing = ToStyleLength(value);
                    return true;
                case UssPropertyId.WordSpacing:
                    style.wordSpacing = ToStyleLength(value);
                    return true;
                case UssPropertyId.UnityParagraphSpacing:
                    style.unityParagraphSpacing = ToStyleLength(value);
                    return true;
                case UssPropertyId.UnityTextOutlineWidth:
                    style.unityTextOutlineWidth = value.A;
                    return true;
                case UssPropertyId.UnityTextOutlineColor:
                    style.unityTextOutlineColor = ToColor(value);
                    return true;
                case UssPropertyId.TextShadow:
                    if (value.IsKeyword("none"))
                    {
                        style.textShadow = StyleKeyword.None;
                        return true;
                    }

                    UssShadowColor shadowColor = value.Payload as UssShadowColor;
                    style.textShadow = new TextShadow
                    {
                        offset = new Vector2(value.A, value.B),
                        blurRadius = value.C,
                        color = shadowColor == null
                            ? Color.black
                            : new Color(shadowColor.R, shadowColor.G, shadowColor.B, shadowColor.A)
                    };
                    return true;
                case UssPropertyId.BackgroundColor:
                    style.backgroundColor = ToColor(value);
                    return true;
                case UssPropertyId.BackgroundImage:
                    return ApplyBackgroundImage(style, value, out error);
                case UssPropertyId.UnityBackgroundImageTintColor:
                    style.unityBackgroundImageTintColor = ToColor(value);
                    return true;
                case UssPropertyId.UnityBackgroundScaleMode:
                    ApplyScaleMode(style, value.Keyword);
                    return true;
                case UssPropertyId.UnitySliceLeft:
                    style.unitySliceLeft = Mathf.RoundToInt(value.A);
                    return true;
                case UssPropertyId.UnitySliceTop:
                    style.unitySliceTop = Mathf.RoundToInt(value.A);
                    return true;
                case UssPropertyId.UnitySliceRight:
                    style.unitySliceRight = Mathf.RoundToInt(value.A);
                    return true;
                case UssPropertyId.UnitySliceBottom:
                    style.unitySliceBottom = Mathf.RoundToInt(value.A);
                    return true;
                case UssPropertyId.UnitySliceScale:
                    style.unitySliceScale = value.A;
                    return true;
                case UssPropertyId.Opacity:
                    style.opacity = Mathf.Clamp01(value.A);
                    return true;
                case UssPropertyId.Visibility:
                    style.visibility = value.IsKeyword("hidden") ? Visibility.Hidden : Visibility.Visible;
                    return true;
                case UssPropertyId.Translate:
                    if (value.IsKeyword("none"))
                    {
                        style.translate = StyleKeyword.None;
                        return true;
                    }

                    style.translate = new Translate(ToLength(value.A, value.UnitA), ToLength(value.B, value.UnitB));
                    return true;
                case UssPropertyId.Scale:
                    if (value.IsKeyword("none"))
                    {
                        style.scale = StyleKeyword.None;
                        return true;
                    }

                    style.scale = new Scale(new Vector2(value.A, value.B));
                    return true;
                case UssPropertyId.Rotate:
                    if (value.IsKeyword("none"))
                    {
                        style.rotate = StyleKeyword.None;
                        return true;
                    }

                    style.rotate = new Rotate(new Angle(value.A, AngleUnit.Degree));
                    return true;
                case UssPropertyId.TransformOrigin:
                    style.transformOrigin = new TransformOrigin(ToLength(value.A, value.UnitA),
                        ToLength(value.B, value.UnitB));
                    return true;
                case UssPropertyId.TransitionProperty:
                    style.transitionProperty = PropertyList(value);
                    return true;
                case UssPropertyId.TransitionDuration:
                    style.transitionDuration = TimeList(value);
                    return true;
                case UssPropertyId.TransitionTimingFunction:
                    style.transitionTimingFunction = EasingList(value);
                    return true;
                case UssPropertyId.TransitionDelay:
                    style.transitionDelay = TimeList(value);
                    return true;
                default:
                    error = "the property " + id + " has no UI Toolkit mapping";
                    return false;
            }
        }

        /// <summary>Resets one inline property to "not set inline".</summary>
        public static void Reset(IStyle style, UssPropertyId id)
        {
            SetKeyword(style, id, StyleKeyword.Null);
        }

        private bool ApplyBackgroundImage(IStyle style, UssValue value, out string error)
        {
            error = null;
            if (value.IsKeyword("none"))
            {
                style.backgroundImage = StyleKeyword.None;
                return true;
            }

            bool isResource = value.A > 0.5f;
            if (_assets != null && _assets.TryResolveBackground(value.Keyword, isResource, out Background background))
            {
                style.backgroundImage = new StyleBackground(background);
                return true;
            }

            error = "the image " + value.ToUssText() +
                    " is not registered with the runtime UI host and was not found in Resources";
            return false;
        }

        private static void ApplyScaleMode(IStyle style, string keyword)
        {
#pragma warning disable CS0618
            // WHY: -unity-background-scale-mode is deprecated in favour of background-size/-repeat/-position,
            // but it is the one-line form authors (and models) write, and UI Toolkit still honours it.
            style.unityBackgroundScaleMode = keyword == "scale-and-crop" ? ScaleMode.ScaleAndCrop
                : keyword == "scale-to-fit" ? ScaleMode.ScaleToFit : ScaleMode.StretchToFill;
#pragma warning restore CS0618
        }

        private StyleList<StylePropertyName> PropertyList(UssValue value)
        {
            UssValue[] items = value.ListItems;
            if (items.Length == 1 && items[0].IsKeyword("none"))
            {
                return StyleKeyword.None;
            }

            if (!_propertyLists.TryGetValue(items, out List<StylePropertyName> list))
            {
                list = new List<StylePropertyName>(items.Length);
                for (int i = 0; i < items.Length; i++)
                {
                    list.Add(new StylePropertyName(items[i].Keyword));
                }

                _propertyLists[items] = list;
            }

            return new StyleList<StylePropertyName>(list);
        }

        private StyleList<TimeValue> TimeList(UssValue value)
        {
            UssValue[] items = value.ListItems;
            if (!_timeLists.TryGetValue(items, out List<TimeValue> list))
            {
                list = new List<TimeValue>(items.Length);
                for (int i = 0; i < items.Length; i++)
                {
                    list.Add(new TimeValue(items[i].A, TimeUnit.Second));
                }

                _timeLists[items] = list;
            }

            return new StyleList<TimeValue>(list);
        }

        private StyleList<EasingFunction> EasingList(UssValue value)
        {
            UssValue[] items = value.ListItems;
            if (!_easingLists.TryGetValue(items, out List<EasingFunction> list))
            {
                list = new List<EasingFunction>(items.Length);
                for (int i = 0; i < items.Length; i++)
                {
                    list.Add(new EasingFunction(ToEasing(items[i].Keyword)));
                }

                _easingLists[items] = list;
            }

            return new StyleList<EasingFunction>(list);
        }

        private static StyleLength ToStyleLength(UssValue value)
        {
            if (value.Type == UssValueType.Keyword)
            {
                if (value.IsKeyword(UssKeywords.Auto))
                {
                    return StyleKeyword.Auto;
                }

                if (value.IsKeyword(UssKeywords.None))
                {
                    return StyleKeyword.None;
                }

                return StyleKeyword.Initial;
            }

            if (value.Type == UssValueType.Number)
            {
                return new Length(value.A, LengthUnit.Pixel);
            }

            return ToLength(value.A, value.UnitA);
        }

        private static Length ToLength(float amount, UssUnit unit)
        {
            return new Length(amount, unit == UssUnit.Percent ? LengthUnit.Percent : LengthUnit.Pixel);
        }

        private static StyleColor ToColor(UssValue value)
        {
            return new Color(value.A, value.B, value.C, value.D);
        }

        private static FlexDirection ToFlexDirection(string keyword)
        {
            switch (keyword)
            {
                case "row": return FlexDirection.Row;
                case "row-reverse": return FlexDirection.RowReverse;
                case "column-reverse": return FlexDirection.ColumnReverse;
                default: return FlexDirection.Column;
            }
        }

        private static Align ToAlign(string keyword)
        {
            switch (keyword)
            {
                case "flex-start": return Align.FlexStart;
                case "flex-end": return Align.FlexEnd;
                case "center": return Align.Center;
                case "stretch": return Align.Stretch;
                default: return Align.Auto;
            }
        }

        private static Justify ToJustify(string keyword)
        {
            switch (keyword)
            {
                case "flex-end": return Justify.FlexEnd;
                case "center": return Justify.Center;
                case "space-between": return Justify.SpaceBetween;
                case "space-around": return Justify.SpaceAround;
                case "space-evenly": return Justify.SpaceEvenly;
                default: return Justify.FlexStart;
            }
        }

        private static FontStyle ToFontStyle(string keyword)
        {
            switch (keyword)
            {
                case "bold": return FontStyle.Bold;
                case "italic": return FontStyle.Italic;
                case "bold-and-italic": return FontStyle.BoldAndItalic;
                default: return FontStyle.Normal;
            }
        }

        private static TextAnchor ToTextAnchor(string keyword)
        {
            switch (keyword)
            {
                case "upper-center": return TextAnchor.UpperCenter;
                case "upper-right": return TextAnchor.UpperRight;
                case "middle-left": return TextAnchor.MiddleLeft;
                case "middle-center": return TextAnchor.MiddleCenter;
                case "middle-right": return TextAnchor.MiddleRight;
                case "lower-left": return TextAnchor.LowerLeft;
                case "lower-center": return TextAnchor.LowerCenter;
                case "lower-right": return TextAnchor.LowerRight;
                default: return TextAnchor.UpperLeft;
            }
        }

        private static WhiteSpace ToWhiteSpace(string keyword)
        {
            switch (keyword)
            {
                case "nowrap": return WhiteSpace.NoWrap;
                case "pre": return WhiteSpace.Pre;
                case "pre-wrap": return WhiteSpace.PreWrap;
                default: return WhiteSpace.Normal;
            }
        }

        private static EasingMode ToEasing(string keyword)
        {
            switch (keyword)
            {
                case "ease-in": return EasingMode.EaseIn;
                case "ease-out": return EasingMode.EaseOut;
                case "ease-in-out": return EasingMode.EaseInOut;
                case "linear": return EasingMode.Linear;
                case "ease-in-sine": return EasingMode.EaseInSine;
                case "ease-out-sine": return EasingMode.EaseOutSine;
                case "ease-in-out-sine": return EasingMode.EaseInOutSine;
                case "ease-in-cubic": return EasingMode.EaseInCubic;
                case "ease-out-cubic": return EasingMode.EaseOutCubic;
                case "ease-in-out-cubic": return EasingMode.EaseInOutCubic;
                case "ease-in-circ": return EasingMode.EaseInCirc;
                case "ease-out-circ": return EasingMode.EaseOutCirc;
                case "ease-in-out-circ": return EasingMode.EaseInOutCirc;
                case "ease-in-elastic": return EasingMode.EaseInElastic;
                case "ease-out-elastic": return EasingMode.EaseOutElastic;
                case "ease-in-out-elastic": return EasingMode.EaseInOutElastic;
                case "ease-in-back": return EasingMode.EaseInBack;
                case "ease-out-back": return EasingMode.EaseOutBack;
                case "ease-in-out-back": return EasingMode.EaseInOutBack;
                case "ease-in-bounce": return EasingMode.EaseInBounce;
                case "ease-out-bounce": return EasingMode.EaseOutBounce;
                case "ease-in-out-bounce": return EasingMode.EaseInOutBounce;
                default: return EasingMode.Ease;
            }
        }

        private static void SetKeyword(IStyle style, UssPropertyId id, StyleKeyword keyword)
        {
            switch (id)
            {
                case UssPropertyId.Display: style.display = keyword; break;
                case UssPropertyId.Position: style.position = keyword; break;
                case UssPropertyId.FlexDirection: style.flexDirection = keyword; break;
                case UssPropertyId.FlexWrap: style.flexWrap = keyword; break;
                case UssPropertyId.FlexGrow: style.flexGrow = keyword; break;
                case UssPropertyId.FlexShrink: style.flexShrink = keyword; break;
                case UssPropertyId.FlexBasis: style.flexBasis = keyword; break;
                case UssPropertyId.AlignItems: style.alignItems = keyword; break;
                case UssPropertyId.AlignSelf: style.alignSelf = keyword; break;
                case UssPropertyId.AlignContent: style.alignContent = keyword; break;
                case UssPropertyId.JustifyContent: style.justifyContent = keyword; break;
                case UssPropertyId.Left: style.left = keyword; break;
                case UssPropertyId.Top: style.top = keyword; break;
                case UssPropertyId.Right: style.right = keyword; break;
                case UssPropertyId.Bottom: style.bottom = keyword; break;
                case UssPropertyId.Width: style.width = keyword; break;
                case UssPropertyId.Height: style.height = keyword; break;
                case UssPropertyId.MinWidth: style.minWidth = keyword; break;
                case UssPropertyId.MinHeight: style.minHeight = keyword; break;
                case UssPropertyId.MaxWidth: style.maxWidth = keyword; break;
                case UssPropertyId.MaxHeight: style.maxHeight = keyword; break;
                case UssPropertyId.Overflow: style.overflow = keyword; break;
                case UssPropertyId.MarginLeft: style.marginLeft = keyword; break;
                case UssPropertyId.MarginTop: style.marginTop = keyword; break;
                case UssPropertyId.MarginRight: style.marginRight = keyword; break;
                case UssPropertyId.MarginBottom: style.marginBottom = keyword; break;
                case UssPropertyId.PaddingLeft: style.paddingLeft = keyword; break;
                case UssPropertyId.PaddingTop: style.paddingTop = keyword; break;
                case UssPropertyId.PaddingRight: style.paddingRight = keyword; break;
                case UssPropertyId.PaddingBottom: style.paddingBottom = keyword; break;
                case UssPropertyId.BorderLeftWidth: style.borderLeftWidth = keyword; break;
                case UssPropertyId.BorderTopWidth: style.borderTopWidth = keyword; break;
                case UssPropertyId.BorderRightWidth: style.borderRightWidth = keyword; break;
                case UssPropertyId.BorderBottomWidth: style.borderBottomWidth = keyword; break;
                case UssPropertyId.BorderLeftColor: style.borderLeftColor = keyword; break;
                case UssPropertyId.BorderTopColor: style.borderTopColor = keyword; break;
                case UssPropertyId.BorderRightColor: style.borderRightColor = keyword; break;
                case UssPropertyId.BorderBottomColor: style.borderBottomColor = keyword; break;
                case UssPropertyId.BorderTopLeftRadius: style.borderTopLeftRadius = keyword; break;
                case UssPropertyId.BorderTopRightRadius: style.borderTopRightRadius = keyword; break;
                case UssPropertyId.BorderBottomRightRadius: style.borderBottomRightRadius = keyword; break;
                case UssPropertyId.BorderBottomLeftRadius: style.borderBottomLeftRadius = keyword; break;
                case UssPropertyId.Color: style.color = keyword; break;
                case UssPropertyId.FontSize: style.fontSize = keyword; break;
                case UssPropertyId.UnityFontStyle: style.unityFontStyleAndWeight = keyword; break;
                case UssPropertyId.UnityTextAlign: style.unityTextAlign = keyword; break;
                case UssPropertyId.WhiteSpace: style.whiteSpace = keyword; break;
                case UssPropertyId.TextOverflow: style.textOverflow = keyword; break;
                case UssPropertyId.LetterSpacing: style.letterSpacing = keyword; break;
                case UssPropertyId.WordSpacing: style.wordSpacing = keyword; break;
                case UssPropertyId.UnityParagraphSpacing: style.unityParagraphSpacing = keyword; break;
                case UssPropertyId.UnityTextOutlineWidth: style.unityTextOutlineWidth = keyword; break;
                case UssPropertyId.UnityTextOutlineColor: style.unityTextOutlineColor = keyword; break;
                case UssPropertyId.TextShadow: style.textShadow = keyword; break;
                case UssPropertyId.BackgroundColor: style.backgroundColor = keyword; break;
                case UssPropertyId.BackgroundImage: style.backgroundImage = keyword; break;
                case UssPropertyId.UnityBackgroundImageTintColor: style.unityBackgroundImageTintColor = keyword; break;
                case UssPropertyId.UnityBackgroundScaleMode:
#pragma warning disable CS0618
                    style.unityBackgroundScaleMode = keyword;
#pragma warning restore CS0618
                    break;
                case UssPropertyId.UnitySliceLeft: style.unitySliceLeft = keyword; break;
                case UssPropertyId.UnitySliceTop: style.unitySliceTop = keyword; break;
                case UssPropertyId.UnitySliceRight: style.unitySliceRight = keyword; break;
                case UssPropertyId.UnitySliceBottom: style.unitySliceBottom = keyword; break;
                case UssPropertyId.UnitySliceScale: style.unitySliceScale = keyword; break;
                case UssPropertyId.Opacity: style.opacity = keyword; break;
                case UssPropertyId.Visibility: style.visibility = keyword; break;
                case UssPropertyId.Translate: style.translate = keyword; break;
                case UssPropertyId.Scale: style.scale = keyword; break;
                case UssPropertyId.Rotate: style.rotate = keyword; break;
                case UssPropertyId.TransformOrigin: style.transformOrigin = keyword; break;
                case UssPropertyId.TransitionProperty: style.transitionProperty = keyword; break;
                case UssPropertyId.TransitionDuration: style.transitionDuration = keyword; break;
                case UssPropertyId.TransitionTimingFunction: style.transitionTimingFunction = keyword; break;
                case UssPropertyId.TransitionDelay: style.transitionDelay = keyword; break;
            }
        }
    }
}
