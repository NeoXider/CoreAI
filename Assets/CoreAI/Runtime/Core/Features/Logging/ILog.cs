namespace CoreAI.Logging
{
    /// <summary>
    /// Minimal logging abstraction used by the CoreAI runtime.
    /// </summary>
    public interface ILog
    {
        /// <summary>Writes a debug-level log message.</summary>
        void Debug(string message, string tag = null);

        /// <summary>Writes an informational log message.</summary>
        void Info(string message, string tag = null);

        /// <summary>Writes a warning-level log message.</summary>
        void Warn(string message, string tag = null);

        /// <summary>Writes an error-level log message.</summary>
        void Error(string message, string tag = null);
    }

    /// <summary>
    /// Logger implementation used when the host has not supplied a logging sink.
    /// </summary>
    public sealed class NullLog : ILog
    {
        public static readonly NullLog Instance = new();

        public void Debug(string message, string tag = null)
        {
        }

        public void Info(string message, string tag = null)
        {
        }

        public void Warn(string message, string tag = null)
        {
        }

        public void Error(string message, string tag = null)
        {
        }
    }


    /// <summary>
    /// Holds the process-wide CoreAI logger instance.
    /// </summary>
    public static class Log
    {
        private static volatile ILog _instance = NullLog.Instance;

        /// <summary>Active CoreAI logger instance.</summary>
        public static ILog Instance
        {
            get => _instance;
            set => _instance = value ?? NullLog.Instance;
        }
    }

    /// <summary>
    /// Defines standard log tags used by CoreAI subsystems.
    /// </summary>
    public static class LogTag
    {
        /// <summary>Core runtime and shared infrastructure.</summary>
        public const string Core = "Core";

        /// <summary>VContainer, lifetime scope, bootstrap.</summary>
        public const string Composition = "Composition";

        /// <summary>MessagePipe publishing and subscription infrastructure.</summary>
        public const string MessagePipe = "MessagePipe";

        /// <summary>LLM requests, responses, routing, and transport.</summary>
        public const string Llm = "Llm";

        /// <summary>Runtime metrics and diagnostics.</summary>
        public const string Metrics = "Metrics";

        /// <summary>Lua sandbox, execution, repair.</summary>
        public const string Lua = "Lua";

        /// <summary>World commands (spawn, move, destroy).</summary>
        public const string World = "World";

        /// <summary>Agent memory and chat history.</summary>
        public const string Memory = "Memory";

        /// <summary>Runtime configuration and settings.</summary>
        public const string Config = "Config";
    }
}
