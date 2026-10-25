using System;
using System.Collections.Generic;

namespace CoreAI.Hub
{
    /// <summary>
    /// Thread-safe registry of CoreAI Hub page factories.
    /// </summary>
    public sealed class HubPageRegistry
    {
        private readonly object _lock = new();
        private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);

        /// <summary>Raised after a page id is registered or replaced.</summary>
        public event Action<string> PageRegistered;

        /// <summary>Raised after a page id is removed.</summary>
        public event Action<string> PageUnregistered;

        /// <summary>
        /// Registers or replaces the factory for <paramref name="pageId"/>.
        /// Last writer wins for duplicate ids.
        /// </summary>
        public void Register(string pageId, Func<IHubPage> factory, int order = 0)
        {
            RegisterOwned(pageId, factory, null, order);
        }

        /// <summary>
        /// Registers a factory and transfers its optional registration lifetime to this registry.
        /// Replacement or removal disposes that lifetime once, outside the registry lock. Page
        /// lifetimes remain owned by their windows; caller-supplied shared services need no lifetime.
        /// </summary>
        public void RegisterOwned(string pageId, Func<IHubPage> factory, IDisposable lifetime, int order = 0)
        {
            if (string.IsNullOrWhiteSpace(pageId))
            {
                throw new ArgumentException("pageId required", nameof(pageId));
            }

            if (factory == null)
            {
                throw new ArgumentNullException(nameof(factory));
            }

            IDisposable previousLifetime;
            lock (_lock)
            {
                previousLifetime = _entries.TryGetValue(pageId, out Entry previous) ? previous.Lifetime : null;
                _entries[pageId] = new Entry(factory, order, lifetime);
            }

            try
            {
                if (!ReferenceEquals(previousLifetime, lifetime))
                {
                    previousLifetime?.Dispose();
                }
            }
            finally
            {
                PageRegistered?.Invoke(pageId);
            }
        }

        /// <summary>Removes the page registered under <paramref name="pageId"/>.</summary>
        public bool Unregister(string pageId)
        {
            return Unregister(pageId, null);
        }

        /// <summary>
        /// Removes a registration only if its factory still matches the supplied owner token.
        /// A null token removes any registration, matching the original overload.
        /// </summary>
        public bool Unregister(string pageId, Func<IHubPage> expectedFactory)
        {
            if (string.IsNullOrWhiteSpace(pageId))
            {
                return false;
            }

            Entry removed;
            lock (_lock)
            {
                if (!_entries.TryGetValue(pageId, out removed) ||
                    (expectedFactory != null && !ReferenceEquals(removed.Factory, expectedFactory)))
                {
                    return false;
                }

                _entries.Remove(pageId);
            }

            try
            {
                removed.Lifetime?.Dispose();
            }
            finally
            {
                PageUnregistered?.Invoke(pageId);
            }

            return true;
        }

        /// <summary>Resolves a page factory by id.</summary>
        public bool TryGet(string pageId, out Func<IHubPage> factory)
        {
            factory = null;
            if (string.IsNullOrWhiteSpace(pageId))
            {
                return false;
            }

            lock (_lock)
            {
                if (!_entries.TryGetValue(pageId, out Entry entry))
                {
                    return false;
                }

                factory = entry.Factory;
                return true;
            }
        }

        /// <summary>Lists registered pages ordered by order and then id.</summary>
        public IReadOnlyList<(string pageId, int order)> List()
        {
            List<(string pageId, int order)> result = new();
            lock (_lock)
            {
                foreach (KeyValuePair<string, Entry> kvp in _entries)
                {
                    result.Add((kvp.Key, kvp.Value.Order));
                }
            }

            result.Sort((a, b) =>
            {
                int orderCompare = a.order.CompareTo(b.order);
                return orderCompare != 0 ? orderCompare : string.CompareOrdinal(a.pageId, b.pageId);
            });
            return result;
        }

        private readonly struct Entry
        {
            public Entry(Func<IHubPage> factory, int order, IDisposable lifetime)
            {
                Factory = factory;
                Order = order;
                Lifetime = lifetime;
            }

            public Func<IHubPage> Factory { get; }
            public int Order { get; }
            public IDisposable Lifetime { get; }
        }
    }
}
