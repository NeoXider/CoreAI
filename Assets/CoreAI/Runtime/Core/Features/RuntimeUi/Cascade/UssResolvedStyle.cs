using System.Collections.Generic;

namespace CoreAI.RuntimeUi
{
    /// <summary>
    /// The cascade's answer for one element: the winning value of every longhand that some rule, the
    /// <c>style</c> attribute or the API sets, plus the custom properties in scope. It is a reusable
    /// scratch buffer: the resolver overwrites it for each element, so callers copy what they keep.
    /// </summary>
    public sealed class UssResolvedStyle
    {
        private readonly UssValue[] _values = new UssValue[(int)UssPropertyId.Count];
        private readonly bool[] _isSet = new bool[(int)UssPropertyId.Count];
        private readonly List<UssPropertyId> _set = new((int)UssPropertyId.Count);

        /// <summary>The longhands set for this element, in first-set order.</summary>
        public IReadOnlyList<UssPropertyId> SetProperties => _set;

        /// <summary>The custom properties in scope for this element (inherited plus its own).</summary>
        public UssCustomProperties CustomProperties { get; internal set; } = UssCustomProperties.Empty;

        /// <summary>True when the longhand is set.</summary>
        public bool IsSet(UssPropertyId id)
        {
            int index = (int)id;
            return index >= 0 && index < _isSet.Length && _isSet[index];
        }

        /// <summary>The value of a set longhand, or a value with <see cref="UssValueType.None"/>.</summary>
        public UssValue Get(UssPropertyId id)
        {
            int index = (int)id;
            return index >= 0 && index < _values.Length && _isSet[index] ? _values[index] : default;
        }

        /// <summary>Reads a set longhand.</summary>
        public bool TryGet(UssPropertyId id, out UssValue value)
        {
            int index = (int)id;
            if (index >= 0 && index < _values.Length && _isSet[index])
            {
                value = _values[index];
                return true;
            }

            value = default;
            return false;
        }

        internal void Clear()
        {
            for (int i = 0; i < _set.Count; i++)
            {
                int index = (int)_set[i];
                _isSet[index] = false;
                _values[index] = default;
            }

            _set.Clear();
            CustomProperties = UssCustomProperties.Empty;
        }

        internal void Set(UssPropertyId id, UssValue value)
        {
            int index = (int)id;
            if (!_isSet[index])
            {
                _isSet[index] = true;
                _set.Add(id);
            }

            _values[index] = value;
        }
    }
}
