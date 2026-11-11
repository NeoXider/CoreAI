namespace CoreAI.RuntimeUi
{
    /// <summary>
    /// The engine-free view of an element that selector matching needs. The Unity host adapts its
    /// visual elements to it; tests and tools can adapt anything else (a parsed UXML node, a fake).
    /// </summary>
    public interface IUssElement
    {
        /// <summary>Type name matched by type selectors (for UI Toolkit, the C# class name: <c>Button</c>).</summary>
        string TypeName { get; }

        /// <summary>Element name matched by <c>#name</c>; null or empty when unnamed.</summary>
        string Name { get; }

        /// <summary>The parent for combinator matching, or null at the top of the styled tree.</summary>
        IUssElement StyleParent { get; }

        /// <summary>Current interaction states (<see cref="UssPseudoState.Root"/> on a screen root).</summary>
        UssPseudoState PseudoStates { get; }

        /// <summary>True when the element carries the class.</summary>
        bool HasClass(string className);
    }
}
