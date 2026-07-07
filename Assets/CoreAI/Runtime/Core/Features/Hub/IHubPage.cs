using System;

namespace CoreAI.Hub
{
    /// <summary>
    /// Core contract for a page that can be surfaced by a CoreAI Hub implementation.
    /// The core assembly stays UI-framework-free, so UI modules decide how to interpret
    /// the object returned by <see cref="CreatePageContent"/>.
    /// </summary>
    public interface IHubPage
    {
        /// <summary>Stable registry id for this page.</summary>
        string PageId { get; }

        /// <summary>Human-readable page name for tabs or navigation lists.</summary>
        string DisplayName { get; }

        /// <summary>Sort priority. Lower values appear first.</summary>
        int Order { get; }

        /// <summary>Creates the page content object. UI Toolkit hosts should return a VisualElement.</summary>
        Func<object> CreatePageContent { get; }

        /// <summary>Called when the page becomes the active page.</summary>
        void OnActivated();

        /// <summary>Called when the page is no longer active.</summary>
        void OnDeactivated();

        /// <summary>Called when the page instance is being discarded.</summary>
        void OnDestroyed();
    }
}
