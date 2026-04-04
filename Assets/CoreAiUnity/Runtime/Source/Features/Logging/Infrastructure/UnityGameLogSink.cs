using UnityEngine;

namespace CoreAI.Infrastructure.Logging
{
    /// <summary>
    /// Низкоуровневый вывод в Unity Console без фильтрации по фичам.
    /// Единственный класс рантайма CoreAI, который вызывает <see cref="UnityEngine.Debug"/> напрямую.
    /// </summary>
    public sealed class UnityGameLogSink
    {
        private const string Prefix = "[CoreAI] ";

        /// <summary>Прямой вывод в Unity Console с префиксом проекта.</summary>
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