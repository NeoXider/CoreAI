using System.Collections.Generic;
using CoreAI.Messaging;

namespace CoreAI.Infrastructure.World
{
    /// <summary>Executes curated component commands on Unity scene objects.</summary>
    public interface ICoreAiComponentCommandExecutor
    {
        /// <summary>Attempts to execute and returns whether the operation succeeded.</summary>
        bool TryExecute(ApplyAiGameCommand cmd);

        /// <summary>
        /// Attempts to execute and returns whether the operation succeeded, yielding the component type
        /// names of a <c>list_components</c> command through <paramref name="listedComponents"/> (empty for
        /// every other action). Tool calls run in parallel, so the listing must travel with the call
        /// instead of through shared executor state.
        /// </summary>
        bool TryExecute(ApplyAiGameCommand cmd, out List<string> listedComponents);
    }
}
