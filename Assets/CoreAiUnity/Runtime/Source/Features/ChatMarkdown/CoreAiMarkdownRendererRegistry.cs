namespace CoreAI.ChatMarkdown
{
    /// <summary>
    /// Process-local attachment point through which the optional Markdown renderer assembly offers its backend.
    /// Empty when <c>com.neoxider.markdownrenderer</c> is not installed: every consumer then keeps plain text.
    /// </summary>
    /// <remarks>
    /// WHY a static point and not a container registration: the renderer assembly compiles only when its package is
    /// installed, so no composition root can name its types (the same reason as <c>CoreAiLookApplierRegistry</c>).
    /// It registers at subsystem registration (and on editor load), before any panel renders; a host replaces or
    /// clears it afterwards, for example from a <c>RuntimeInitializeLoadType.BeforeSceneLoad</c> method.
    /// </remarks>
    public static class CoreAiMarkdownRendererRegistry
    {
        private static ICoreAiMarkdownRenderer _current;

        /// <summary>The registered backend; null when none is installed or a host cleared it.</summary>
        public static ICoreAiMarkdownRenderer Current => _current;

        /// <summary>True when Markdown can be rendered.</summary>
        public static bool IsAvailable => _current != null;

        /// <summary>Registers the backend; a later call replaces it and null switches Markdown rendering off.</summary>
        public static void Register(ICoreAiMarkdownRenderer renderer)
        {
            _current = renderer;
        }
    }
}
