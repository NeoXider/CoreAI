using System.Collections.Generic;

namespace CoreAI.RuntimeUi
{
    /// <summary>
    /// A parsed UXML document. <see cref="Root"/> stands for the <c>UXML</c> element: its children are the
    /// top-level elements of the screen. Parsing never throws; everything that was not built as
    /// written is in <see cref="Diagnostics"/>.
    /// </summary>
    public sealed class UxmlDocument
    {
        internal UxmlDocument(UxmlNode root, IReadOnlyList<RuntimeUiDiagnostic> diagnostics,
            IReadOnlyDictionary<string, string> templates, int elementCount)
        {
            Root = root;
            Diagnostics = diagnostics;
            Templates = templates;
            ElementCount = elementCount;
        }

        /// <summary>The document root; its children are the top-level elements.</summary>
        public UxmlNode Root { get; }

        /// <summary>Reports produced while parsing (templates included).</summary>
        public IReadOnlyList<RuntimeUiDiagnostic> Diagnostics { get; }

        /// <summary>Declared template aliases and their <c>src</c>.</summary>
        public IReadOnlyDictionary<string, string> Templates { get; }

        /// <summary>Number of element nodes built, template contents included, the root excluded.</summary>
        public int ElementCount { get; }

        /// <summary>Parses UXML text. Never throws.</summary>
        public static UxmlDocument Parse(string text, UxmlParseOptions options = null)
        {
            return UxmlParser.Parse(text, options);
        }
    }

    /// <summary>Options for <see cref="UxmlParser"/>.</summary>
    public sealed class UxmlParseOptions
    {
        /// <summary>Resolves <c>Template</c> sources for <c>Instance</c> and <c>item-template</c>; may be null.</summary>
        public IUxmlTemplateResolver TemplateResolver { get; set; }

        /// <summary>Hard limits; null uses <see cref="RuntimeUiParseLimits.Default"/>.</summary>
        public RuntimeUiParseLimits Limits { get; set; }
    }
}
