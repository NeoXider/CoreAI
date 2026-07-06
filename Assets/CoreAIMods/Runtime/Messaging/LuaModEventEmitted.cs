namespace CoreAI.Messaging
{
    /// <summary>
    /// MessagePipe event published when a persistent Lua mod calls <c>events_emit</c>.
    /// </summary>
    public readonly struct LuaModEventEmitted
    {
        /// <summary>Creates a Lua mod event notification.</summary>
        public LuaModEventEmitted(string modId, string eventName, string payload)
        {
            ModId = modId ?? "";
            EventName = eventName ?? "";
            Payload = payload ?? "";
        }

        /// <summary>Identifier of the mod that emitted the event.</summary>
        public string ModId { get; }

        /// <summary>Event name supplied by the mod.</summary>
        public string EventName { get; }

        /// <summary>String payload supplied by the mod.</summary>
        public string Payload { get; }
    }
}
