using System.Collections.Generic;
using UnityEngine.UIElements;

namespace CoreAI.RuntimeUi
{
    /// <summary>
    /// The screen's bookkeeping for one visual element: its place in the styled tree (the physical
    /// UI Toolkit hierarchy, so combinators match exactly as the editor importer's would), its
    /// interaction states, which states it listens for, the inline properties it owns and its handle.
    /// </summary>
    internal sealed class RuntimeUiNodeRecord : IUssElement
    {
        public RuntimeUiNodeRecord(RuntimeUiScreen screen, VisualElement element, RuntimeUiNodeRecord parent,
            RuntimeUiAuthoredElement authored)
        {
            Screen = screen;
            Element = element;
            Parent = parent;
            Authored = authored;
            TypeName = element.GetType().Name;
        }

        public RuntimeUiScreen Screen { get; }

        public VisualElement Element { get; }

        public RuntimeUiNodeRecord Parent { get; set; }

        /// <summary>The factory info, or null for an internal part of a control (a slider's tracker, say).</summary>
        public RuntimeUiAuthoredElement Authored { get; }

        public bool IsAuthored => Authored != null;

        public RuntimeUiElementKind Kind => Authored?.Kind ?? RuntimeUiElementKind.VisualElement;

        public List<RuntimeUiNodeRecord> Children { get; } = new();

        public string TypeName { get; }

        public string Name => Element.name;

        public IUssElement StyleParent => Parent;

        public UssPseudoState PseudoStates { get; set; }

        /// <summary>States some selector can test on this element; callbacks are registered for these.</summary>
        public UssPseudoState TrackedStates { get; set; }

        /// <summary>True when a selector tests this element's state on an ancestor compound.</summary>
        public bool StateAffectsDescendants { get; set; }

        public bool CallbacksRegistered { get; set; }

        public UssCustomProperties Scope { get; set; } = UssCustomProperties.Empty;

        public RuntimeUiPropertyMask Applied;

        public bool DisplayNone { get; set; }

        public RuntimeUiElement Handle { get; set; }

        /// <summary>Position among authored elements with the same name, in document order.</summary>
        public int NameOccurrence { get; set; }

        public bool IsListItem { get; set; }

        public bool HasClass(string className)
        {
            return Element.ClassListContains(className);
        }
    }
}
