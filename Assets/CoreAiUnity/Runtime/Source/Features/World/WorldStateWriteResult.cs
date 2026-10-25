namespace CoreAI.Infrastructure.World
{
    /// <summary>Outcome of one local snapshot write or deletion.</summary>
    public enum WorldStateWriteStatus
    {
        Unsupported = 0,
        Succeeded,
        Failed,
        Cancelled
    }

    /// <summary>Immutable local-operation outcome and automatic-persistence arming observation.</summary>
    public readonly struct WorldStateWriteResult
    {
        public WorldStateWriteStatus Status { get; }
        public bool LocalOperationSucceeded => Status == WorldStateWriteStatus.Succeeded;
        public bool PersistenceArmed { get; }
        public string Error { get; }
        public bool IsConfirmed => LocalOperationSucceeded && PersistenceArmed;

        public WorldStateWriteResult(
            WorldStateWriteStatus status,
            bool persistenceArmed = false,
            string error = null)
        {
            Status = status;
            PersistenceArmed = persistenceArmed;
            Error = error ?? string.Empty;
        }
    }

    /// <summary>Optional per-operation results for managers that can confirm local storage operations.</summary>
    public interface IWorldStateWriteResults
    {
        WorldStateWriteResult SaveWithResult();
        WorldStateWriteResult ResetWithResult();
    }
}
