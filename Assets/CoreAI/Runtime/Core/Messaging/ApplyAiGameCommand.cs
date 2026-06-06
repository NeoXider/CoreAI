using CoreAI.Ai;

namespace CoreAI.Messaging
{
    /// <summary>
    /// Serializable command requested by an AI agent for host-side execution.
    /// </summary>
    public sealed class ApplyAiGameCommand
    {
        /// <summary>Stable command type identifier used by the command router.</summary>
        public string CommandTypeId { get; set; } = "";

        /// <summary>Command payload serialized as JSON.</summary>
        public string JsonPayload { get; set; } = "";

        /// <summary>Role id of the agent that produced the command.</summary>
        public string SourceRoleId { get; set; } = "";

        /// <summary>Original task hint that led to this command.</summary>
        public string SourceTaskHint { get; set; } = "";

        /// <summary>Subsystem tag associated with the command source.</summary>
        public string SourceTag { get; set; } = "";

        /// <summary>Lua repair generation associated with this command.</summary>
        public int LuaRepairGeneration { get; set; }

        /// <summary>Trace id used to correlate logs and metrics.</summary>
        public string TraceId { get; set; } = "";

        /// <summary>Script key used by Lua version tracking.</summary>
        public string LuaScriptVersionKey { get; set; } = "";

        /// <summary>Comma-separated data overlay keys affected by the command.</summary>
        public string DataOverlayVersionKeysCsv { get; set; } = "";
    }

    /// <summary>
    /// Defines the contract for ai game command sink implementations.
    /// </summary>
    public interface IAiGameCommandSink
    {
        /// <summary>Publishes an AI game command to the host messaging system.</summary>
        void Publish(ApplyAiGameCommand command);
    }
}