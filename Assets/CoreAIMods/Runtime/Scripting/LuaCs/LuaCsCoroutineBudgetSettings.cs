using UnityEngine;

namespace CoreAI.Sandbox.LuaCs
{
    /// <summary>
    /// Game-configurable override for the per-resume budget every <see cref="LuaCsCoroutineHandle"/>
    /// arms before a resume: the instruction-step cap and the wall-clock cap. Registered the same
    /// shape as <c>CoreAI.Mods.Rbx.Binding.IRbxCharacterMotorProvider</c> — a serialized field on
    /// <c>CoreAiModsLifetimeScope</c> registers one instance into the container, every construction
    /// site resolves it with <c>ResolveOrDefault</c>, and CoreAI's documented defaults
    /// (<see cref="LuaCsCoroutineHandle.DefaultBudgetPerResume"/>/
    /// <see cref="LuaCsCoroutineHandle.DefaultResumeTimeoutMs"/>) apply when nothing is registered.
    /// </summary>
    /// <remarks>
    /// WHY one settings type over two loose registered ints: two independently-resolved primitives
    /// in the same container are ambiguous — nothing ties them together as "the coroutine resume
    /// budget" — and a game overriding one without the other would silently leave the other at
    /// whatever a stray registration happened to be.
    /// <para>
    /// WHY non-positive values fall back rather than disabling the guard: a budget of zero or less
    /// has no sane interpretation as "no limit" for a per-instruction hook, and treating it that way
    /// would silently turn off the runaway-script protection for every mod in the composition. Both
    /// accessors below re-validate on every read, so this holds however the stored value got there —
    /// a bad inspector value or a live <see cref="SetResumeTimeoutMs"/> call alike.
    /// </para>
    /// <para>
    /// WHY the wall-clock half is mutable after construction while the instruction half is not:
    /// Roblox's <c>ScriptContext:SetTimeout(seconds)</c> changes only the wall-clock half, live, for
    /// every subsequent resume — including resumes of an already-pooled
    /// <c>CoreAI.Ai.LuaCs.LuaCsRbxSignalRunner</c>, since every resume re-reads this object instead
    /// of a value frozen at construction. Roblox has no scriptable equivalent for the instruction
    /// budget, so it stays composition-only and this type exposes no public setter for it.
    /// </para>
    /// </remarks>
    [System.Serializable]
    public sealed class LuaCsCoroutineBudgetSettings
    {
        [SerializeField]
        [Tooltip("Instruction-step budget armed before every guarded coroutine resume. A value <= 0 "
            + "falls back to LuaCsCoroutineHandle.DefaultBudgetPerResume.")]
        private int budgetPerResume = LuaCsCoroutineHandle.DefaultBudgetPerResume;

        [SerializeField]
        [Tooltip("Wall-clock budget (milliseconds) armed before every guarded coroutine resume. A "
            + "value <= 0 falls back to LuaCsCoroutineHandle.DefaultResumeTimeoutMs. Roblox's "
            + "ScriptContext:SetTimeout(seconds) changes this live at runtime.")]
        private int resumeTimeoutMs = LuaCsCoroutineHandle.DefaultResumeTimeoutMs;

        /// <summary>Default-valued settings: both halves resolve to CoreAI's documented defaults.</summary>
        public LuaCsCoroutineBudgetSettings()
        {
        }

        /// <param name="budgetPerResume">Instruction-step budget; &lt;= 0 falls back to the default.</param>
        /// <param name="resumeTimeoutMs">Wall-clock budget in ms; &lt;= 0 falls back to the default.</param>
        public LuaCsCoroutineBudgetSettings(int budgetPerResume, int resumeTimeoutMs)
        {
            this.budgetPerResume = budgetPerResume;
            this.resumeTimeoutMs = resumeTimeoutMs;
        }

        /// <summary>Instruction-step budget for the next resume; never non-positive.</summary>
        public int BudgetPerResume =>
            budgetPerResume > 0 ? budgetPerResume : LuaCsCoroutineHandle.DefaultBudgetPerResume;

        /// <summary>Wall-clock budget (ms) for the next resume; never non-positive.</summary>
        public int ResumeTimeoutMs =>
            resumeTimeoutMs > 0 ? resumeTimeoutMs : LuaCsCoroutineHandle.DefaultResumeTimeoutMs;

        /// <summary>
        /// Changes the wall-clock half for every subsequent resume that reads this instance live.
        /// WHY internal: the only caller is the host-gated <c>ScriptContext:SetTimeout</c> Lua
        /// binding (<c>CoreAI.Ai.LuaCs.LuaCsRbxInstanceBindings</c>) — same assembly, different
        /// namespace. A non-positive value is accepted here too and simply falls back on the next
        /// read via <see cref="ResumeTimeoutMs"/>, exactly like a bad inspector value.
        /// </summary>
        internal void SetResumeTimeoutMs(int milliseconds)
        {
            resumeTimeoutMs = milliseconds;
        }
    }
}
