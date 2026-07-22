namespace CoreAI.Scripting
{
    /// <summary>
    /// Requested hardening profile for a new <see cref="IScriptState"/>. The concrete engine adapter owns
    /// the actual sandboxing (stripped globals, capped string/table builders, guarded coroutines); this
    /// object exists so future per-state knobs can be added without changing
    /// <see cref="IScriptEngine.CreateState"/>.
    /// </summary>
    public sealed class ScriptSandboxProfile
    {
        /// <summary>The standard secured profile every mod state uses today.</summary>
        public static readonly ScriptSandboxProfile Default = new();
    }
}
