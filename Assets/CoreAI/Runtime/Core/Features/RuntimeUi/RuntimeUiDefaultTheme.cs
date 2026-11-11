using System.Collections.Generic;

namespace CoreAI.RuntimeUi
{
    /// <summary>
    /// The theme a fresh world gets: design tokens (USS custom properties) and a small set of semantic
    /// classes that read them. A host game replaces either by passing its own to
    /// <see cref="RuntimeUiTheme"/>; screens keep working because they only reference class names and
    /// token names.
    /// </summary>
    public static class RuntimeUiDefaultTheme
    {
        /// <summary>The semantic classes, written as USS that the interpreter itself applies.</summary>
        public const string Uss =
            ".cai-panel { background-color: var(--cai-color-surface); border-radius: var(--cai-radius); " +
            "padding: var(--cai-space-3); border-width: 1px; border-color: var(--cai-color-border); }\n" +
            ".cai-row { flex-direction: row; align-items: center; }\n" +
            ".cai-column { flex-direction: column; }\n" +
            ".cai-h1 { font-size: var(--cai-font-size-h1); -unity-font-style: bold; color: var(--cai-color-text); }\n" +
            ".cai-text { font-size: var(--cai-font-size-body); color: var(--cai-color-text); }\n" +
            ".cai-small { font-size: var(--cai-font-size-small); }\n" +
            ".cai-muted { color: var(--cai-color-text-muted); }\n" +
            ".cai-btn { background-color: var(--cai-color-surface-raised); color: var(--cai-color-text); " +
            "border-radius: var(--cai-radius); padding: var(--cai-space-1) var(--cai-space-3); border-width: 1px; " +
            "border-color: var(--cai-color-border); " +
            "transition: background-color 0.12s ease-out, scale 0.12s ease-out; }\n" +
            ".cai-btn:hover { background-color: var(--cai-color-surface-hover); }\n" +
            ".cai-btn:active { scale: 0.96; }\n" +
            ".cai-btn-primary { background-color: var(--cai-color-accent); color: var(--cai-color-on-accent); " +
            "border-color: var(--cai-color-accent); }\n" +
            ".cai-btn-primary:hover { background-color: var(--cai-color-accent-hover); }\n" +
            ".is-hidden { display: none; }\n" +
            ".is-selected { border-color: var(--cai-color-accent); border-width: 2px; }\n" +
            ".is-disabled { opacity: 0.5; }\n";

        /// <summary>The default design tokens.</summary>
        public static IReadOnlyDictionary<string, string> Tokens { get; } = new Dictionary<string, string>
        {
            { "--cai-color-bg", "rgba(12, 14, 20, 0.85)" },
            { "--cai-color-surface", "rgba(24, 28, 38, 0.92)" },
            { "--cai-color-surface-raised", "rgb(44, 50, 66)" },
            { "--cai-color-surface-hover", "rgb(58, 66, 86)" },
            { "--cai-color-border", "rgba(255, 255, 255, 0.12)" },
            { "--cai-color-text", "rgb(235, 238, 245)" },
            { "--cai-color-text-muted", "rgb(160, 168, 184)" },
            { "--cai-color-accent", "rgb(255, 196, 64)" },
            { "--cai-color-accent-hover", "rgb(255, 214, 112)" },
            { "--cai-color-on-accent", "rgb(24, 20, 8)" },
            { "--cai-space-1", "4px" },
            { "--cai-space-2", "8px" },
            { "--cai-space-3", "12px" },
            { "--cai-space-4", "20px" },
            { "--cai-radius", "6px" },
            { "--cai-font-size-small", "13px" },
            { "--cai-font-size-body", "16px" },
            { "--cai-font-size-h1", "28px" }
        };
    }
}
