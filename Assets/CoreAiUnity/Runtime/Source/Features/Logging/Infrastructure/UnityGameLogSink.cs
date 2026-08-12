using UnityEngine;

namespace CoreAI.Infrastructure.Logging
{
    /// <summary>
    /// Writes game log events to the Unity console.
    /// </summary>
    public sealed class UnityGameLogSink : IGameLogSink
    {
        private const string Prefix = "[CoreAI] ";
        private readonly IGameLogSettings _settings;

        /// <summary>Initializes a sink with optional formatting settings.</summary>
        public UnityGameLogSink(IGameLogSettings settings = null)
        {
            _settings = settings;
        }

        /// <inheritdoc />
        public void Write(GameLogLevel level, string message, Object context = null)
        {
            switch (level)
            {
                case GameLogLevel.Debug:
                case GameLogLevel.Info:
                    if (context != null)
                    {
                        Debug.Log(Format(message), context);
                    }
                    else
                    {
                        Debug.Log(Format(message));
                    }

                    break;
                case GameLogLevel.Warning:
                    if (context != null)
                    {
                        Debug.LogWarning(Format(message), context);
                    }
                    else
                    {
                        Debug.LogWarning(Format(message));
                    }

                    break;
                case GameLogLevel.Error:
                    if (context != null)
                    {
                        Debug.LogError(Format(message), context);
                    }
                    else
                    {
                        Debug.LogError(Format(message));
                    }

                    break;
            }
        }

        private string Format(string message)
        {
            if (_settings is IGameLogFormattingSettings formattingSettings &&
                !formattingSettings.IncludeCoreAiPrefix)
            {
                return message;
            }

            return Prefix + message;
        }
    }
}
