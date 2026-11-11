using System.Collections.Generic;

namespace CoreAI.RuntimeUi
{
    /// <summary>One style rule: a selector group and its declarations, in source order.</summary>
    public sealed class UssRule
    {
        internal UssRule(UssComplexSelector[] selectors, UssDeclaration[] declarations, int line, int order)
        {
            Selectors = selectors;
            Declarations = declarations;
            Line = line;
            Order = order;
        }

        /// <summary>The selectors that survived parsing (unsupported ones were reported and dropped).</summary>
        public IReadOnlyList<UssComplexSelector> Selectors { get; }

        /// <summary>The declarations that survived validation.</summary>
        public IReadOnlyList<UssDeclaration> Declarations { get; }

        /// <summary>1-based line of the rule's selector.</summary>
        public int Line { get; }

        /// <summary>Position of the rule in its style sheet.</summary>
        public int Order { get; }
    }
}
