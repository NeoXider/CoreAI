using System;

namespace CoreAI.Ai
{
    /// <summary>
    /// Thrown when a script calls an API whose capability tier or composition flag was withheld.
    /// Withheld-surface stubs raise it instead of leaving the global nil, so the mod's error streak
    /// carries an actionable message (the missing capability plus the suggested alternative) rather
    /// than a bare "attempt to call a nil value".
    /// </summary>
    public sealed class LuaApiWithheldException : InvalidOperationException
    {
        /// <summary>Global name of the withheld API the script called.</summary>
        public string ApiName { get; }

        /// <summary>Capability tier whose absence (or composition flag) withheld the API.</summary>
        public LuaCapabilities RequiredCapability { get; }

        public LuaApiWithheldException(string apiName, LuaCapabilities requiredCapability, string message)
            : base(message)
        {
            ApiName = apiName;
            RequiredCapability = requiredCapability;
        }
    }
}
