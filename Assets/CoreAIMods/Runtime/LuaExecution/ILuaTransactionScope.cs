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
    }
}