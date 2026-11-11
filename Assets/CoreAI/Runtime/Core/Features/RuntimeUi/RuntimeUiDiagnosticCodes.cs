namespace CoreAI.RuntimeUi
{
    /// <summary>
    /// Stable report codes of the runtime UI interpreter and host. The codes are part of the contract a
    /// Lua binding and the AI repair loop key on; messages may change, codes do not.
    /// </summary>
    public static class RuntimeUiDiagnosticCodes
    {
        /// <summary>The markup is not well formed at this point (bad tag, attribute, entity or comment).</summary>
        public const string UxmlMalformed = "UXML_MALFORMED";

        /// <summary>An element was still open when its parent closed or the text ended.</summary>
        public const string UxmlUnclosedElement = "UXML_UNCLOSED_ELEMENT";

        /// <summary>A closing tag matched no open element and was ignored.</summary>
        public const string UxmlMismatchedCloseTag = "UXML_MISMATCHED_CLOSE_TAG";

        /// <summary>The element type is not built by the interpreter; a labelled placeholder stands in.</summary>
        public const string UxmlUnknownElement = "UXML_UNKNOWN_ELEMENT";

        /// <summary>The element lives in <c>UnityEditor.UIElements</c> and cannot exist in a player.</summary>
        public const string UxmlEditorOnlyElement = "UXML_EDITOR_ONLY_ELEMENT";

        /// <summary>The attribute is not applied by the interpreter for this element type.</summary>
        public const string UxmlUnsupportedAttribute = "UXML_UNSUPPORTED_ATTRIBUTE";

        /// <summary>The attribute value could not be read (not a number, boolean or allowed keyword).</summary>
        public const string UxmlInvalidAttributeValue = "UXML_INVALID_ATTRIBUTE_VALUE";

        /// <summary>Text content was used as the element's text; the editor importer would ignore it.</summary>
        public const string UxmlTextContent = "UXML_TEXT_CONTENT";

        /// <summary>A <c>Style</c> reference was ignored; runtime screens get their USS as text.</summary>
        public const string UxmlStyleReferenceIgnored = "UXML_STYLE_REFERENCE_IGNORED";

        /// <summary>An <c>Instance</c> names a template that is not declared or cannot be resolved.</summary>
        public const string UxmlTemplateNotFound = "UXML_TEMPLATE_NOT_FOUND";

        /// <summary>A template instantiates itself (directly or through others) or nests too deeply.</summary>
        public const string UxmlTemplateRecursion = "UXML_TEMPLATE_RECURSION";

        /// <summary>The markup exceeds an element, depth or size limit; the rest was not built.</summary>
        public const string UxmlLimitExceeded = "UXML_LIMIT_EXCEEDED";

        /// <summary>The style sheet is not well formed at this point; the parser skipped to the next rule.</summary>
        public const string UssSyntax = "USS_SYNTAX";

        /// <summary>An at-rule (<c>@import</c>, <c>@media</c>, ...) is not supported and was skipped.</summary>
        public const string UssUnsupportedAtRule = "USS_UNSUPPORTED_AT_RULE";

        /// <summary>A selector uses a form outside the supported subset; that selector was dropped.</summary>
        public const string UssUnsupportedSelector = "USS_UNSUPPORTED_SELECTOR";

        /// <summary>A property is outside the supported subset; the declaration was dropped.</summary>
        public const string UssUnsupportedProperty = "USS_UNSUPPORTED_PROPERTY";

        /// <summary>A property value could not be read for that property; the declaration was dropped.</summary>
        public const string UssInvalidValue = "USS_INVALID_VALUE";

        /// <summary><c>!important</c> does not exist in USS; the flag was ignored and the value applied.</summary>
        public const string UssUnsupportedImportant = "USS_UNSUPPORTED_IMPORTANT";

        /// <summary>A <c>var()</c> names a custom property no ancestor defines and has no fallback.</summary>
        public const string UssUnresolvedVar = "USS_UNRESOLVED_VAR";

        /// <summary>Custom properties reference each other in a cycle.</summary>
        public const string UssVarCycle = "USS_VAR_CYCLE";

        /// <summary>A <c>resource()</c> or <c>url()</c> asset could not be found at runtime.</summary>
        public const string UssUnresolvedAsset = "USS_UNRESOLVED_ASSET";

        /// <summary>The style sheet exceeds a size or rule limit; the rest was not parsed.</summary>
        public const string UssLimitExceeded = "USS_LIMIT_EXCEEDED";

        /// <summary>The host is switched off; nothing was created.</summary>
        public const string UiDisabled = "UI_DISABLED";

        /// <summary>A screen with this id already exists.</summary>
        public const string UiScreenExists = "UI_SCREEN_EXISTS";

        /// <summary>No screen with this id exists.</summary>
        public const string UiScreenNotFound = "UI_SCREEN_NOT_FOUND";

        /// <summary>The screen was destroyed; the call had no effect.</summary>
        public const string UiScreenDestroyed = "UI_SCREEN_DESTROYED";

        /// <summary>The host already holds its maximum number of screens.</summary>
        public const string UiScreenLimit = "UI_SCREEN_LIMIT";

        /// <summary>The screen already holds its maximum number of elements.</summary>
        public const string UiElementLimit = "UI_ELEMENT_LIMIT";

        /// <summary>The UXML or USS text is larger than the host accepts.</summary>
        public const string UiTextTooLarge = "UI_TEXT_TOO_LARGE";

        /// <summary>The element handle no longer points at an element (removed, or its name vanished).</summary>
        public const string UiElementDetached = "UI_ELEMENT_DETACHED";

        /// <summary>A query selector could not be parsed or uses an unsupported form.</summary>
        public const string UiInvalidSelector = "UI_INVALID_SELECTOR";

        /// <summary>The operation does not apply to this element kind (a value on a Label, say).</summary>
        public const string UiUnsupportedOperation = "UI_UNSUPPORTED_OPERATION";

        /// <summary>An argument is null, empty or out of range.</summary>
        public const string UiInvalidArgument = "UI_INVALID_ARGUMENT";
    }
}
