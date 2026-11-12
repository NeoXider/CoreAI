namespace CoreAI.Mods.Rbx.Instances.Scripts
{
    /// <summary>
    /// What the runtime knows of one source when it reports its run state (<c>manage_mods list</c>, the Hub): never read
    /// from the store on a listing path, only from the loaded run, the host's binding index and the manifest it holds.
    /// WHY public fields: a parameter bag filled by one caller and read by one function, as <see cref="ScriptRunInputs"/>.
    /// </summary>
    public struct ScriptRunFacts
    {
        /// <summary>The source is loaded in this runtime: it has a live run, or is registered without one.</summary>
        public bool IsLoaded;

        /// <summary>The manifest's <c>Active</c>: the source is meant to start with the world.</summary>
        public bool IsActive;

        public bool IsQuarantined;

        /// <summary>The stored package is suspended after repeated budget trips; only a load or reload by hand clears it.</summary>
        public bool IsSuspended;

        /// <summary>The context the source declares (header or manifest); <see cref="ScriptContext.Legacy"/> for none.</summary>
        public ScriptContext DeclaredContext;

        /// <summary>A live world-owned instance is bound to the source.</summary>
        public bool HasBoundInstance;

        /// <summary>
        /// The loaded source is registered without a run (MVP4 step C1-6): a ModuleScript, a detached source, or a script
        /// the run predicate did not start; <see cref="NotRunningState"/> says why.
        /// </summary>
        public bool IsRegisteredWithoutRun;

        /// <summary>Why a source registered without a run has none, as <see cref="ScriptRunRules.Evaluate"/> answered at its start.</summary>
        public ScriptRunState NotRunningState;
    }

    /// <summary>
    /// The run state reported for a source (decision SC-9's list), from <see cref="ScriptRunFacts"/>. A source with a
    /// live run is <see cref="ScriptRunState.Running"/> unless quarantined or suspended. A loaded source registered
    /// without a run reports why it did not start, or <see cref="ScriptRunState.Detached"/> once its instance is gone. A
    /// source that is not loaded is <see cref="ScriptRunState.Suspended"/> after budget trips,
    /// <see cref="ScriptRunState.Unloaded"/> when it is not active, and <see cref="ScriptRunState.Detached"/> when it is
    /// active and declared but no instance is bound to it (rule R3: it does not start at the legacy placement).
    /// </summary>
    public static class ScriptRunStates
    {
        public static ScriptRunState Describe(in ScriptRunFacts facts)
        {
            if (facts.IsLoaded && facts.IsRegisteredWithoutRun)
            {
                return facts.HasBoundInstance ? facts.NotRunningState : ScriptRunState.Detached;
            }

            if (facts.IsLoaded)
            {
                if (facts.IsQuarantined)
                {
                    return ScriptRunState.Quarantined;
                }

                return facts.IsSuspended ? ScriptRunState.Suspended : ScriptRunState.Running;
            }

            if (facts.IsSuspended)
            {
                return ScriptRunState.Suspended;
            }

            if (facts.IsActive && facts.DeclaredContext != ScriptContext.Legacy && !facts.HasBoundInstance)
            {
                return ScriptRunState.Detached;
            }

            return ScriptRunState.Unloaded;
        }
    }
}
