using System.Collections.Generic;
using CoreAI.Messaging;

namespace CoreAI.Infrastructure.World
{
    /// <summary>Executes curated component commands on Unity scene objects.</summary>
    public interface ICoreAiComponentCommandExecutor
    {
        /// <summary>Attempts to execute and returns whether the operation succeeded.</summary>
        bool TryExecute(ApplyAiGameCommand cmd);

        /// <summary>Most recent component type names returned by a list command.</summary>
        List<string> LastListedComponents { get; }
    }
}
