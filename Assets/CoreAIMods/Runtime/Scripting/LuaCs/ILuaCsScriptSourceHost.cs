using CoreAI.Authority;
using CoreAI.Mods.Rbx.Instances.Scripts;

namespace CoreAI.Ai.LuaCs
{
    /// <summary>
    /// What the Lua script members need from the mod runtime (MVP4 SC-2, SC-4; step C2): <c>Source</c> is a view over
    /// the runtime's sources, and writing <c>Source</c> or <c>RunContext</c> reloads or restarts the script the instance
    /// is bound to. The bindings check who may call; the runtime implements the effect.
    /// </summary>
    /// <remarks>
    /// WHY a seam: the bindings sit below the runtime and never hold it; the runtime installs itself here when it is
    /// built over these bindings.
    /// </remarks>
    internal interface ILuaCsScriptSourceHost
    {
        /// <summary>
        /// The current text of <paramref name="sourceId"/>: the last loaded revision, or the store's for a source that is
        /// not loaded. False when neither holds it.
        /// </summary>
        bool TryReadScriptSource(string sourceId, out string source);

        /// <summary>
        /// SC-2: <paramref name="source"/> becomes the text of the loaded source <paramref name="sourceId"/> through a
        /// reload in the default mode, with its revision, store write and gate like any reload. Throws the reload's
        /// error, which leaves the run as it was.
        /// </summary>
        void WriteScriptSource(ActorContext caller, string sourceId, string source);

        /// <summary>
        /// SC-4: sets <paramref name="script"/>'s <c>RunContext</c> and restarts the loaded source bound to it under the
        /// new context, keeping what it made; the stored manifest records the new context. Refused when the source's
        /// header declares a context, which the next reload would apply again. A failed restart puts the old
        /// <c>RunContext</c> back and throws.
        /// </summary>
        void WriteScriptRunContext(ActorContext caller, RbxBaseScript script, int runContext);
    }
}
