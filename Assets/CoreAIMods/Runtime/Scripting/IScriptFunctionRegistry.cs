using System;

namespace CoreAI.Scripting
{
    /// <summary>
    /// Engine-neutral registry of host callbacks exposed to sandboxed scripts. Typed delegates cover the
    /// common case (arguments coerced from script values by parameter type); var-args callbacks cover
    /// APIs that need raw argument access, function-valued arguments, or multiple/zero returns.
    /// </summary>
    public interface IScriptFunctionRegistry
    {
        /// <summary>Registers a typed host delegate under a global name.</summary>
        void Register(string name, Delegate callback);

        /// <summary>Registers a var-args host callback under a global name.</summary>
        void RegisterVarArgs(string name, Func<ScriptCallContext, ScriptCallResult> callback);

        /// <summary>True when <paramref name="name"/> is registered (tests / introspection).</summary>
        bool Contains(string name);

        /// <summary>Exposes every registered callback on the state's global environment.</summary>
        void ApplyTo(IScriptState state);
    }
}
