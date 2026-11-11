using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

namespace CoreAI.RuntimeUi
{
    /// <summary>What the factory knows about an element it built from markup or created on request.</summary>
    public sealed class RuntimeUiAuthoredElement
    {
        internal RuntimeUiAuthoredElement(RuntimeUiElementKind kind, IReadOnlyList<UssDeclaration> inlineStyle,
            UxmlNode node)
        {
            Kind = kind;
            InlineStyle = inlineStyle ?? Array.Empty<UssDeclaration>();
            Node = node;
        }

        /// <summary>The kind built.</summary>
        public RuntimeUiElementKind Kind { get; }

        /// <summary>The <c>style</c> attribute declarations.</summary>
        public IReadOnlyList<UssDeclaration> InlineStyle { get; }

        /// <summary>The UXML node it came from, or null for an element created through the API.</summary>
        public UxmlNode Node { get; }
    }

    /// <summary>
    /// Builds UI Toolkit elements: from parsed UXML nodes, or one kind at a time for C# callers (and the
    /// Roblox GUI layer) that need no markup. Attribute names are the UXML ones on both paths, so
    /// <c>SetAttribute("low-value", "0")</c> means exactly what <c>low-value="0"</c> means in markup.
    /// </summary>
    public sealed class RuntimeUiElementFactory
    {
        /// <summary>Class of every placeholder that stands in for an element type the factory does not build.</summary>
        public const string PlaceholderClass = "cai-ui-placeholder";

        /// <summary>Class of the label inside a placeholder.</summary>
        public const string PlaceholderLabelClass = "cai-ui-placeholder__label";

        private readonly IRuntimeUiAssetResolver _assets;

        /// <summary>Creates a factory.</summary>
        /// <param name="assets">Resolves <c>Image</c> sources; null refuses them.</param>
        public RuntimeUiElementFactory(IRuntimeUiAssetResolver assets)
        {
            _assets = assets;
        }

        /// <summary>Creates an empty element of a kind.</summary>
        public VisualElement Create(RuntimeUiElementKind kind)
        {
            switch (kind)
            {
                case RuntimeUiElementKind.Label:
                    return new Label();
                case RuntimeUiElementKind.Button:
                    return new Button();
                case RuntimeUiElementKind.Toggle:
                    return new Toggle();
                case RuntimeUiElementKind.Slider:
                    return new Slider();
                case RuntimeUiElementKind.TextField:
                    return new TextField();
                case RuntimeUiElementKind.ProgressBar:
                    return new ProgressBar();
                case RuntimeUiElementKind.Image:
                    return new Image();
                case RuntimeUiElementKind.ScrollView:
                    return new ScrollView();
                case RuntimeUiElementKind.ListView:
                    return new ListView();
                case RuntimeUiElementKind.TemplateContainer:
                    return new TemplateContainer();
                case RuntimeUiElementKind.Placeholder:
                    return CreatePlaceholder(null);
                default:
                    return new VisualElement();
            }
        }

        /// <summary>
        /// Builds a node and its children. Every built element is recorded in <paramref name="authored"/>
        /// (kind, inline style, node); attribute problems go to <paramref name="diagnostics"/>.
        /// </summary>
        public VisualElement Build(UxmlNode node, IDictionary<VisualElement, RuntimeUiAuthoredElement> authored,
            RuntimeUiDiagnosticList diagnostics)
        {
            VisualElement element = node.IsPlaceholder
                ? CreatePlaceholder(node.PlaceholderReason ?? "<" + node.ElementType + "> line " + node.Line)
                : Create(node.Kind);
            if (!string.IsNullOrEmpty(node.Name))
            {
                element.name = node.Name;
            }

            for (int i = 0; i < node.Classes.Count; i++)
            {
                element.AddToClassList(node.Classes[i]);
            }

            for (int i = 0; i < node.Attributes.Count; i++)
            {
                UxmlAttribute attribute = node.Attributes[i];
                if (!ApplyAttribute(element, node.Kind, attribute.Name, attribute.Value, out string error))
                {
                    diagnostics?.Add(RuntimeUiDiagnosticCodes.UxmlInvalidAttributeValue,
                        RuntimeUiDiagnosticSeverity.Warning, RuntimeUiSource.Uxml, attribute.Line, attribute.Column,
                        "'" + attribute.Name + "' on <" + node.ElementType + "> was not applied: " + error);
                }
            }

            authored[element] = new RuntimeUiAuthoredElement(node.Kind, node.InlineStyle, node);
            for (int i = 0; i < node.Children.Count; i++)
            {
                element.Add(Build(node.Children[i], authored, diagnostics));
            }

            return element;
        }

        /// <summary>
        /// Applies one UXML attribute to an element of a kind; the name and value are validated against
        /// <see cref="UxmlElementCatalog"/> first.
        /// </summary>
        /// <returns>False with an error when the attribute is unknown for the kind or its value is invalid.</returns>
        public bool ApplyAttribute(VisualElement element, RuntimeUiElementKind kind, string name, string value,
            out string error)
        {
            error = null;
            if (element == null)
            {
                error = "no element";
                return false;
            }

            if (!UxmlElementCatalog.TryGetAttribute(kind, name, out UxmlAttributeSpec spec))
            {
                error = "'" + name + "' is not an attribute of " + UxmlElementCatalog.TypeNameOf(kind);
                return false;
            }

            if (!spec.TryNormalize(value, out string text, out error))
            {
                return false;
            }

            switch (name)
            {
                case "name":
                    element.name = text;
                    return true;
                case "class":
                    string[] classes = text.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
                    for (int i = 0; i < classes.Length; i++)
                    {
                        element.AddToClassList(classes[i]);
                    }

                    return true;
                case "style":
                    error = "set inline styles through the style API, not the style attribute";
                    return false;
                case "tooltip":
                    element.tooltip = text;
                    return true;
                case "picking-mode":
                    element.pickingMode = text == "Ignore" ? PickingMode.Ignore : PickingMode.Position;
                    return true;
                case "focusable":
                    element.focusable = Bool(text);
                    return true;
                case "tabindex":
                    element.tabIndex = Int(text);
                    return true;
                case "enabled":
                    element.SetEnabled(Bool(text));
                    return true;
                case "view-data-key":
                    element.viewDataKey = text;
                    return true;
                case "usage-hints":
                case "content-container":
                case "template":
                    return true;
            }

            switch (kind)
            {
                case RuntimeUiElementKind.Label:
                case RuntimeUiElementKind.Button:
                    return ApplyTextElement((TextElement)element, name, text, out error);
                case RuntimeUiElementKind.Toggle:
                    return ApplyToggle((Toggle)element, name, text, out error);
                case RuntimeUiElementKind.Slider:
                    return ApplySlider((Slider)element, name, text, out error);
                case RuntimeUiElementKind.TextField:
                    return ApplyTextField((TextField)element, name, text, out error);
                case RuntimeUiElementKind.ProgressBar:
                    return ApplyProgressBar((ProgressBar)element, name, text, out error);
                case RuntimeUiElementKind.Image:
                    return ApplyImage((Image)element, name, text, out error);
                case RuntimeUiElementKind.ScrollView:
                    return ApplyScrollView((ScrollView)element, name, text, out error);
                case RuntimeUiElementKind.ListView:
                    return ApplyListView((ListView)element, name, text, out error);
                default:
                    error = "'" + name + "' is not handled for " + kind;
                    return false;
            }
        }

        /// <summary>The placeholder element: a dashed-looking box with a label naming what it replaces.</summary>
        public static VisualElement CreatePlaceholder(string reason)
        {
            VisualElement placeholder = new();
            placeholder.AddToClassList(PlaceholderClass);
            Label label = new(string.IsNullOrEmpty(reason) ? "unsupported element" : reason);
            label.AddToClassList(PlaceholderLabelClass);
            label.pickingMode = PickingMode.Ignore;
            placeholder.Add(label);
            return placeholder;
        }

        private static bool ApplyTextElement(TextElement element, string name, string text, out string error)
        {
            error = null;
            switch (name)
            {
                case "text":
                    element.text = text;
                    return true;
                case "enable-rich-text":
                    element.enableRichText = Bool(text);
                    return true;
                case "parse-escape-sequences":
                    element.parseEscapeSequences = Bool(text);
                    return true;
                case "display-tooltip-when-elided":
                    element.displayTooltipWhenElided = Bool(text);
                    return true;
                default:
                    error = "unhandled attribute '" + name + "'";
                    return false;
            }
        }

        private static bool ApplyToggle(Toggle toggle, string name, string text, out string error)
        {
            error = null;
            switch (name)
            {
                case "label":
                    toggle.label = text;
                    return true;
                case "value":
                    toggle.SetValueWithoutNotify(Bool(text));
                    return true;
                case "text":
                    toggle.text = text;
                    return true;
                case "toggle-on-label-click":
                    toggle.toggleOnLabelClick = Bool(text);
                    return true;
                default:
                    error = "unhandled attribute '" + name + "'";
                    return false;
            }
        }

        private static bool ApplySlider(Slider slider, string name, string text, out string error)
        {
            error = null;
            switch (name)
            {
                case "label":
                    slider.label = text;
                    return true;
                case "value":
                    slider.SetValueWithoutNotify(Float(text));
                    return true;
                case "low-value":
                    slider.lowValue = Float(text);
                    return true;
                case "high-value":
                    slider.highValue = Float(text);
                    return true;
                case "page-size":
                    slider.pageSize = Float(text);
                    return true;
                case "direction":
                    slider.direction = text == "Vertical" ? SliderDirection.Vertical : SliderDirection.Horizontal;
                    return true;
                case "inverted":
                    slider.inverted = Bool(text);
                    return true;
                case "show-input-field":
                    slider.showInputField = Bool(text);
                    return true;
                case "fill":
                    slider.fill = Bool(text);
                    return true;
                default:
                    error = "unhandled attribute '" + name + "'";
                    return false;
            }
        }

        private static bool ApplyTextField(TextField field, string name, string text, out string error)
        {
            error = null;
            switch (name)
            {
                case "label":
                    field.label = text;
                    return true;
                case "value":
                case "text":
                    field.SetValueWithoutNotify(text);
                    return true;
                case "max-length":
                    field.maxLength = Int(text);
                    return true;
                case "multiline":
                    field.multiline = Bool(text);
                    return true;
                case "password":
                    field.isPasswordField = Bool(text);
                    return true;
                case "mask-character":
                    if (text.Length > 0)
                    {
                        field.maskChar = text[0];
                    }

                    return true;
                case "readonly":
                case "is-read-only":
                    field.isReadOnly = Bool(text);
                    return true;
                case "is-delayed":
                    field.isDelayed = Bool(text);
                    return true;
                case "placeholder-text":
                    field.textEdition.placeholder = text;
                    return true;
                case "hide-placeholder-on-focus":
                    field.textEdition.hidePlaceholderOnFocus = Bool(text);
                    return true;
                case "select-all-on-focus":
                    field.selectAllOnFocus = Bool(text);
                    return true;
                case "select-all-on-mouse-up":
                    field.selectAllOnMouseUp = Bool(text);
                    return true;
                default:
                    error = "unhandled attribute '" + name + "'";
                    return false;
            }
        }

        private static bool ApplyProgressBar(ProgressBar bar, string name, string text, out string error)
        {
            error = null;
            switch (name)
            {
                case "low-value":
                    bar.lowValue = Float(text);
                    return true;
                case "high-value":
                    bar.highValue = Float(text);
                    return true;
                case "value":
                    bar.SetValueWithoutNotify(Float(text));
                    return true;
                case "title":
                    bar.title = text;
                    return true;
                default:
                    error = "unhandled attribute '" + name + "'";
                    return false;
            }
        }

        private bool ApplyImage(Image image, string name, string text, out string error)
        {
            error = null;
            switch (name)
            {
                case "image":
                case "sprite":
                    if (!TryResolveImage(text, out Background background))
                    {
                        error = "the image '" + text + "' is not registered with the runtime UI host and was not " +
                                "found in Resources";
                        return false;
                    }

                    if (background.sprite != null)
                    {
                        image.sprite = background.sprite;
                    }
                    else
                    {
                        image.image = background.texture;
                    }

                    return true;
                case "scale-mode":
                    image.scaleMode = text == "ScaleAndCrop" ? ScaleMode.ScaleAndCrop
                        : text == "ScaleToFit" ? ScaleMode.ScaleToFit : ScaleMode.StretchToFill;
                    return true;
                case "tint-color":
                    if (UssValueParser.TryParseColor(text, out UssValue color))
                    {
                        image.tintColor = new Color(color.A, color.B, color.C, color.D);
                        return true;
                    }

                    error = "'" + text + "' is not a colour";
                    return false;
                default:
                    error = "unhandled attribute '" + name + "'";
                    return false;
            }
        }

        private bool TryResolveImage(string text, out Background background)
        {
            background = default;
            if (_assets == null)
            {
                return false;
            }

            string trimmed = text.Trim();
            if (UssPropertyCatalog.TryGetLonghand("background-image", out UssPropertySpec spec)
                && trimmed.EndsWith(")", StringComparison.Ordinal)
                && UssValueParser.TryParseLonghand(spec, trimmed, out UssValue asset, out _)
                && asset.Type == UssValueType.Asset)
            {
                return _assets.TryResolveBackground(asset.Keyword, asset.A > 0.5f, out background);
            }

            return _assets.TryResolveBackground(trimmed, false, out background);
        }

        private static bool ApplyScrollView(ScrollView view, string name, string text, out string error)
        {
            error = null;
            switch (name)
            {
                case "mode":
                    view.mode = text == "Horizontal" ? ScrollViewMode.Horizontal
                        : text == "VerticalAndHorizontal" ? ScrollViewMode.VerticalAndHorizontal
                        : ScrollViewMode.Vertical;
                    return true;
                case "horizontal-scroller-visibility":
                    view.horizontalScrollerVisibility = ToScrollerVisibility(text);
                    return true;
                case "vertical-scroller-visibility":
                    view.verticalScrollerVisibility = ToScrollerVisibility(text);
                    return true;
                case "elasticity":
                    view.elasticity = Float(text);
                    return true;
                case "mouse-wheel-scroll-size":
                    view.mouseWheelScrollSize = Float(text);
                    return true;
                case "touch-scroll-type":
                    view.touchScrollBehavior = text == "Elastic" ? ScrollView.TouchScrollBehavior.Elastic
                        : text == "Clamped" ? ScrollView.TouchScrollBehavior.Clamped
                        : ScrollView.TouchScrollBehavior.Unrestricted;
                    return true;
                case "horizontal-page-size":
                    view.horizontalPageSize = Float(text);
                    return true;
                case "vertical-page-size":
                    view.verticalPageSize = Float(text);
                    return true;
                case "scroll-deceleration-rate":
                    view.scrollDecelerationRate = Float(text);
                    return true;
                default:
                    error = "unhandled attribute '" + name + "'";
                    return false;
            }
        }

        private static bool ApplyListView(ListView list, string name, string text, out string error)
        {
            error = null;
            switch (name)
            {
                case "fixed-item-height":
                    list.fixedItemHeight = Float(text);
                    return true;
                case "virtualization-method":
                    list.virtualizationMethod = text == "DynamicHeight"
                        ? CollectionVirtualizationMethod.DynamicHeight
                        : CollectionVirtualizationMethod.FixedHeight;
                    return true;
                case "show-border":
                    list.showBorder = Bool(text);
                    return true;
                case "selection-type":
                    list.selectionType = text == "None" ? SelectionType.None
                        : text == "Multiple" ? SelectionType.Multiple : SelectionType.Single;
                    return true;
                case "show-alternating-row-backgrounds":
                    list.showAlternatingRowBackgrounds = text == "All" ? AlternatingRowBackground.All
                        : text == "ContentOnly" ? AlternatingRowBackground.ContentOnly
                        : AlternatingRowBackground.None;
                    return true;
                case "reorderable":
                    list.reorderable = Bool(text);
                    return true;
                case "header-title":
                    list.headerTitle = text;
                    return true;
                case "show-foldout-header":
                    list.showFoldoutHeader = Bool(text);
                    return true;
                case "show-add-remove-footer":
                    list.showAddRemoveFooter = Bool(text);
                    return true;
                case "horizontal-scrolling":
                    list.horizontalScrollingEnabled = Bool(text);
                    return true;
                case "item-template":
                    return true;
                default:
                    error = "unhandled attribute '" + name + "'";
                    return false;
            }
        }

        private static ScrollerVisibility ToScrollerVisibility(string text)
        {
            return text == "AlwaysVisible" ? ScrollerVisibility.AlwaysVisible
                : text == "Hidden" ? ScrollerVisibility.Hidden : ScrollerVisibility.Auto;
        }

        private static bool Bool(string text)
        {
            return UxmlValueParsing.TryParseBool(text, out bool value) && value;
        }

        private static int Int(string text)
        {
            return UxmlValueParsing.TryParseInt(text, out int value) ? value : 0;
        }

        private static float Float(string text)
        {
            return UxmlValueParsing.TryParseFloat(text, out float value) ? value : 0f;
        }
    }
}
