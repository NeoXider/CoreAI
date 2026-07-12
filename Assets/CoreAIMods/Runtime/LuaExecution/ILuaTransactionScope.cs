namespace CoreAI.Ai
{
    /// <summary>
    /// Optional contract for gameplay-binding sets that hold mutable per-run transaction state on a
    /// shared (singleton) instance. Top-level executors call <see cref="ResetTransactions"/> around every
    /// chunk so a transaction left open by a script that died between begin and commit (error/budget)
    /// cannot bleed into the next script and silently buffer its world commands. VM-agnostic: it names no
    /// Lua VM type, so both the runtime and the one-off executor of the Lua-CSharp stack forward the reset
    /// to every wrapped binding set that implements it.
    /// </summary>
    public interface ILuaTransactionScope
    {
        /// <summary>
        /// Discards any unfinished transaction so the next chunk starts from a clean state. Safe to call
        /// when no transaction is active.
        /// </summary>
        void ResetTransactions();

        /// <summary>
        /// Pushes a fresh, isolated transaction frame for one guarded execution (a handler/timer call, a
        /// nested <c>mods_call</c>, or a load chunk). While the frame is on the stack every
        /// <c>coreai_world_begin/commit/rollback</c> and buffered command targets THAT frame only, so a
        /// nested call on a different mod's <see cref="Lua.LuaState"/> cannot flush or clear a caller's
        /// still-open transaction on the shared binding instance. Balanced by <see cref="PopTransactionScope"/>.
        /// </summary>
        void PushTransactionScope();

        /// <summary>
        /// Pops the transaction frame pushed by <see cref="PushTransactionScope"/>, discarding any
        /// unfinished transaction it still holds (rollback semantics). Safe to call unbalanced: the base
        /// frame is never removed.
        /// </summary>
        void PopTransactionScope();
    }
}
