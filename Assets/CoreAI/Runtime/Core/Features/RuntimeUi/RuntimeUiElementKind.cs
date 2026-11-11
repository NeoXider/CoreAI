namespace CoreAI.RuntimeUi
{
    /// <summary>
    /// The element kinds the runtime UI factory builds. UXML element types map onto these; the typed C#
    /// API (and a future Roblox GUI layer) creates them directly without any markup.
    /// </summary>
    public enum RuntimeUiElementKind
    {
        /// <summary>A plain container (<c>VisualElement</c>).</summary>
        VisualElement = 0,

        /// <summary>Static text (<c>Label</c>).</summary>
        Label = 1,

        /// <summary>A clickable button with text (<c>Button</c>).</summary>
        Button = 2,

        /// <summary>A boolean check box with an optional label (<c>Toggle</c>).</summary>
        Toggle = 3,

        /// <summary>A float slider (<c>Slider</c>).</summary>
        Slider = 4,

        /// <summary>A text input (<c>TextField</c>).</summary>
        TextField = 5,

        /// <summary>A read-only progress bar (<c>ProgressBar</c>).</summary>
        ProgressBar = 6,

        /// <summary>An image (<c>Image</c>).</summary>
        Image = 7,

        /// <summary>A scrolling container (<c>ScrollView</c>).</summary>
        ScrollView = 8,

        /// <summary>A virtualized list of string items (<c>ListView</c>).</summary>
        ListView = 9,

        /// <summary>The container an <c>Instance</c> of a template becomes (<c>TemplateContainer</c>).</summary>
        TemplateContainer = 10,

        /// <summary>The labelled stand-in for an element type the interpreter does not build.</summary>
        Placeholder = 11
    }
}
