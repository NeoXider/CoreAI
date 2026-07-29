using UnityEngine;

namespace CoreAI.Infrastructure.Logging
{
    /// <summary>
    /// Destination a game log entry is written to once it passed the filter.
    /// </summary>
    public interface IGameLogSink
    {
        /// <summary>Writes an already formatted and already filtered log entry.</summary>
        void Write(GameLogLevel level, string message, Object context = null);
    }
}
