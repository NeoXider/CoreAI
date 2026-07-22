using System.Collections.Generic;

namespace CoreAI.Scripting
{
    /// <summary>
    /// Engine-neutral read view over a script table argument. Values are host-projected: nil is null,
    /// booleans/numbers/strings are bool/double/string, nested tables are nested
    /// <see cref="IScriptTable"/> views, anything else is the engine's opaque value object.
    /// </summary>
    public interface IScriptTable
    {
        /// <summary>Host-projected value for a string key; null when the key is absent or nil.</summary>
        object this[string key] { get; }

        /// <summary>True when the raw value for the key is present and not nil.</summary>
        bool Has(string key);

        /// <summary>All entries with host-projected keys and values, in engine iteration order.</summary>
        IEnumerable<KeyValuePair<object, object>> Pairs { get; }
    }
}
