namespace CoreAI.Ai
{
    /// <summary>Receives lifecycle notifications from the Lua execution and repair pipeline.</summary>
    public interface ILuaExecutionObserver
    {
        /// <summary>Notifies observers that Lua execution completed successfully.</summary>
        void OnLuaSuccess(string resultSummary);

        /// <summary>Notifies observers that Lua execution failed.</summary>
        void OnLuaFailure(string errorMessage);

        /// <summary>Notifies observers that a Lua repair attempt has been scheduled.</summary>
        void OnLuaRepairScheduled(int nextGeneration, string errorPreview);
    }

    /// <summary>Lua execution observer used when the host does not collect Lua diagnostics.</summary>
    public sealed class NullLuaExecutionObserver : ILuaExecutionObserver
    {
        /// <inheritdoc />
        public void OnLuaSuccess(string resultSummary)
        {
        }

        /// <inheritdoc />
        public void OnLuaFailure(string errorMessage)
        {
        }

        /// <inheritdoc />
        public void OnLuaRepairScheduled(int nextGeneration, string errorPreview)
        {
        }
    }
}