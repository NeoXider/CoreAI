using System;
using System.Collections.Generic;

namespace CoreAI.RuntimeUi
{
    /// <summary>
    /// The custom properties (<c>--token: value</c>) in scope for one element: the inherited ones plus the
    /// element's own. Instances are immutable and shared: an element that declares no custom property
    /// reuses its parent's instance, so a whole subtree costs one map. Values are stored already
    /// substituted, so a <c>var()</c> lookup is a single dictionary read.
    /// </summary>
    public sealed class UssCustomProperties
    {
        /// <summary>The empty scope.</summary>
        public static readonly UssCustomProperties Empty = new(new Dictionary<string, string>(StringComparer.Ordinal));

        private readonly Dictionary<string, string> _values;

        private UssCustomProperties(Dictionary<string, string> values)
        {
            _values = values;
        }

        /// <summary>Number of custom properties in scope.</summary>
        public int Count => _values.Count;

        /// <summary>Every custom property in scope.</summary>
        public IEnumerable<KeyValuePair<string, string>> Entries => _values;

        /// <summary>Builds a scope from a name-to-value map (names keep their case).</summary>
        public static UssCustomProperties From(IEnumerable<KeyValuePair<string, string>> values)
        {
            Dictionary<string, string> copy = new(StringComparer.Ordinal);
            if (values != null)
            {
                foreach (KeyValuePair<string, string> pair in values)
                {
                    if (UssPropertyCatalog.IsCustomProperty(pair.Key) && pair.Value != null)
                    {
                        copy[pair.Key] = pair.Value.Trim();
                    }
                }
            }

            return copy.Count == 0 ? Empty : new UssCustomProperties(copy);
        }

        /// <summary>Reads a custom property.</summary>
        public bool TryGet(string name, out string value)
        {
            if (name == null)
            {
                value = null;
                return false;
            }

            return _values.TryGetValue(name, out value);
        }

        /// <summary>
        /// Returns a scope with <paramref name="overrides"/> layered on top of this one. The override values
        /// must already be substituted.
        /// </summary>
        public UssCustomProperties With(IReadOnlyList<KeyValuePair<string, string>> overrides)
        {
            if (overrides == null || overrides.Count == 0)
            {
                return this;
            }

            Dictionary<string, string> merged = new(_values, StringComparer.Ordinal);
            for (int i = 0; i < overrides.Count; i++)
            {
                merged[overrides[i].Key] = overrides[i].Value;
            }

            return new UssCustomProperties(merged);
        }
    }
}
