namespace CoreAI.Ai
{
    /// <summary>
    /// Persistent per-mod key/value storage exposed to Lua mods as <c>store_set</c>/<c>store_get</c>.
    /// Values are strings; mods serialize structured data themselves (e.g. JSON).
    /// </summary>
    public interface ILuaModStore
    {
        /// <summary>Returns the stored value for the mod's key, or an empty string when absent.</summary>
        string Get(string modId, string key);

        /// <summary>Stores a value under the mod's key. Null values clear the key.</summary>
        void Set(string modId, string key, string value);

        /// <summary>Removes all keys stored for the mod.</summary>
        void Clear(string modId);
    }
}