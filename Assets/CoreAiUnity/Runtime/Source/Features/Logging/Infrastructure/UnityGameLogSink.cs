using UnityEngine;

namespace CoreAI.Infrastructure.Logging
{
    /// <summary>
    /// Writes game log events to the Unity console.
    /// </summary>
    public sealed class UnityGameLogSink
    {
        private const string Prefix = "[CoreAI] ";

        /// <summary>Replaces an existing memory value with the supplied content.</summary>
        public void Write(GameLogLevel level, string message, Object context = null)
        {
            switch (level)
            {
                case GameLogLevel.Debug:
                case GameLogLevel.Info:
                    if (context != null)
                    {
                        Debug.Log(Prefix + message, context);
                    }
                    else
                    {
                        Debug.Log(Prefix + message);
                    }

                    break;
                case GameLogLevel.Warning:
                    if (context != null)
                    {
                        Debug.LogWarning(Prefix + message, context);
                    }
                    else
                    {
                        Debug.LogWarning(Prefix + message);
                    }

                    break;
                case GameLogLevel.Error:
                    if (context != null)
                    {
                        Debug.LogError(Prefix + message, context);
                    }
                    else
                    {
                        Debug.LogError(Prefix + message);
                    }

                    break;
            }
        }
    }
}
