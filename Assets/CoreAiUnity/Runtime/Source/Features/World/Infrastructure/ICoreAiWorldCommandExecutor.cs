using CoreAI.Messaging;

namespace CoreAI.Infrastructure.World
{
    /// <summary>ICoreAiWorldCommandExecutor interface.</summary>
    public interface ICoreAiWorldCommandExecutor
    {
        /// <summary>Attempts to execute and returns whether the operation succeeded.</summary>
        bool TryExecute(ApplyAiGameCommand cmd);

        string[] LastListedAnimations { get; }

        System.Collections.Generic.List<System.Collections.Generic.Dictionary<string, object>> LastListedObjects
        {
            get;
        }
    }
}
