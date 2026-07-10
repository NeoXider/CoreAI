using System;

namespace CoreAI.Hub
{
    /// <summary>
    /// Convenience base for <see cref="IHubPage"/> implementations. Stores <see cref="PageId"/>,
    /// <see cref="DisplayName"/>, and <see cref="Order"/> from the constructor and gives virtual no-op
    /// lifecycle hooks, so a page only overrides the ones it actually needs. <see cref="IHubPage"/> itself
    /// is unchanged — this is an optional convenience, not part of the public contract.
    /// </summary>
    public abstract class HubPageBase : IHubPage
    {
        protected HubPageBase(string pageId, string displayName, int order)
        {
            PageId = pageId;
            DisplayName = displayName;
            Order = order;
        }

        /// <inheritdoc />
        public string PageId { get; }

        /// <inheritdoc />
        public string DisplayName { get; }

        /// <inheritdoc />
        public int Order { get; }

        /// <inheritdoc />
        public abstract Func<object> CreatePageContent { get; }

        /// <inheritdoc />
        public virtual void OnActivated()
        {
        }

        /// <inheritdoc />
        public virtual void OnDeactivated()
        {
        }

        /// <inheritdoc />
        public virtual void OnDestroyed()
        {
        }
    }
}
