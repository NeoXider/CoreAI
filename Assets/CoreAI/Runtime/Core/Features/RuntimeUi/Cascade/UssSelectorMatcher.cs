namespace CoreAI.RuntimeUi
{
    /// <summary>Right-to-left matching of a complex selector against an element and its ancestors.</summary>
    public static class UssSelectorMatcher
    {
        /// <summary>True when <paramref name="selector"/> matches <paramref name="element"/>.</summary>
        public static bool Matches(UssComplexSelector selector, IUssElement element)
        {
            if (selector == null || element == null)
            {
                return false;
            }

            return MatchFrom(selector, selector.Compounds.Length - 1, element);
        }

        /// <summary>
        /// True when the subject compound matches <paramref name="element"/> ignoring pseudo-classes. Used to
        /// decide which elements need interaction-state tracking at all.
        /// </summary>
        public static bool SubjectMatchesIgnoringState(UssComplexSelector selector, IUssElement element)
        {
            return selector != null && element != null && selector.Subject.MatchesIgnoringState(element);
        }

        private static bool MatchFrom(UssComplexSelector selector, int index, IUssElement element)
        {
            if (!selector.Compounds[index].Matches(element))
            {
                return false;
            }

            if (index == 0)
            {
                return true;
            }

            UssCombinator combinator = selector.Combinators[index];
            IUssElement parent = element.StyleParent;
            if (combinator == UssCombinator.Child)
            {
                return parent != null && MatchFrom(selector, index - 1, parent);
            }

            for (IUssElement ancestor = parent; ancestor != null; ancestor = ancestor.StyleParent)
            {
                if (MatchFrom(selector, index - 1, ancestor))
                {
                    return true;
                }
            }

            return false;
        }
    }
}
