namespace CoreAI.RuntimeUi
{
    /// <summary>A set of <see cref="UssPropertyId"/>s as two 64-bit words (the id range stays below 128).</summary>
    public struct RuntimeUiPropertyMask
    {
        private ulong _low;
        private ulong _high;

        /// <summary>True when no property is in the set.</summary>
        public bool IsEmpty => _low == 0UL && _high == 0UL;

        /// <summary>True when the property is in the set.</summary>
        public bool Has(UssPropertyId id)
        {
            int index = (int)id;
            return index < 64 ? (_low & (1UL << index)) != 0UL : (_high & (1UL << (index - 64))) != 0UL;
        }

        /// <summary>Adds the property.</summary>
        public void Add(UssPropertyId id)
        {
            int index = (int)id;
            if (index < 64)
            {
                _low |= 1UL << index;
            }
            else
            {
                _high |= 1UL << (index - 64);
            }
        }

        /// <summary>Removes every property.</summary>
        public void Clear()
        {
            _low = 0UL;
            _high = 0UL;
        }
    }
}
