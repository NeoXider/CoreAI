namespace CoreAI.RuntimeUi
{
    /// <summary>
    /// Hard ceilings the parsers enforce so one malformed or hostile document cannot stall the game
    /// loop. Exceeding one produces a <c>*_LIMIT_EXCEEDED</c> report and the rest of the input is
    /// skipped; nothing throws. Per-mod budgets (screens, elements, text size) are a separate, smaller
    /// layer the Lua binding applies on top.
    /// </summary>
    public sealed class RuntimeUiParseLimits
    {
        /// <summary>The defaults used when no limits are passed.</summary>
        public static readonly RuntimeUiParseLimits Default = new();

        /// <summary>Largest UXML or USS text accepted, in characters.</summary>
        public int MaxTextLength { get; set; } = 512 * 1024;

        /// <summary>Most elements one UXML document (templates included) may produce.</summary>
        public int MaxElements { get; set; } = 4096;

        /// <summary>Deepest element nesting accepted.</summary>
        public int MaxDepth { get; set; } = 64;

        /// <summary>Deepest template-in-template nesting accepted.</summary>
        public int MaxTemplateDepth { get; set; } = 8;

        /// <summary>Most rules one style sheet may hold.</summary>
        public int MaxRules { get; set; } = 4096;

        /// <summary>Most diagnostics kept per parse; later ones are counted but not stored.</summary>
        public int MaxDiagnostics { get; set; } = 256;
    }
}
