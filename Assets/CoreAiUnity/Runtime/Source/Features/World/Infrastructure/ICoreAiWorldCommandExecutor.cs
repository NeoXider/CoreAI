using System.Collections.Generic;
using CoreAI.Messaging;

namespace CoreAI.Infrastructure.World
{
    /// <summary>ICoreAiWorldCommandExecutor interface.</summary>
    public interface ICoreAiWorldCommandExecutor
    {
        /// <summary>Attempts to execute and returns whether the operation succeeded.</summary>
        bool TryExecute(ApplyAiGameCommand cmd);

        string[] LastListedAnimations { get; }

        List<Dictionary<string, object>> LastListedObjects { get; }

        /// <summary>Prefab keys returned by the most recent <c>list_prefabs</c> command.</summary>
        IReadOnlyList<string> LastListedPrefabKeys => System.Array.Empty<string>();

        /// <summary>
        /// Extra detail for the most recent failed command (e.g. an unknown prefabKey with the available
        /// keys listed), or "" when the last command needed no extra detail.
        /// </summary>
        string LastErrorMessage => "";

        /// <summary>Per-item outcome of the most recent <c>spawn_batch</c> command, or null when none ran.</summary>
        CoreAiSpawnBatchResult LastSpawnBatchResult => null;
    }

    /// <summary>Compact per-item outcome of a <c>spawn_batch</c> command.</summary>
    public sealed class CoreAiSpawnBatchResult
    {
        public int Spawned;
        public int Failed;
        public List<string> Names = new();
    }
}
