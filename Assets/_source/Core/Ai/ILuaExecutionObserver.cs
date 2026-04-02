namespace CoreAI.Ai
{
    public interface ILuaExecutionObserver
    {
        void OnLuaSuccess(string resultSummary);

        void OnLuaFailure(string errorMessage);

        void OnLuaRepairScheduled(int nextGeneration, string errorPreview);
    }

    public sealed class NullLuaExecutionObserver : ILuaExecutionObserver
    {
        public void OnLuaSuccess(string resultSummary)
        {
        }

        public void OnLuaFailure(string errorMessage)
        {
        }

        public void OnLuaRepairScheduled(int nextGeneration, string errorPreview)
        {
        }
    }
}
