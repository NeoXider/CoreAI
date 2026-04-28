namespace CoreAI.Ai
{
    /// <summary>
    /// Optional scope values used to isolate agent memory across users, sessions, topics, or tenants.
    /// </summary>
    public readonly struct AgentMemoryScope
    {
        /// <summary>
        /// Creates an immutable memory scope.
        /// </summary>
        public AgentMemoryScope(string tenantId, string userId, string sessionId, string topicId)
        {
            TenantId = tenantId ?? "";
            UserId = userId ?? "";
            SessionId = sessionId ?? "";
            TopicId = topicId ?? "";
        }

        /// <summary>Product or organization boundary.</summary>
        public string TenantId { get; }

        /// <summary>Current player, learner, or account id.</summary>
        public string UserId { get; }

        /// <summary>Current gameplay, chat, lesson, or practice session id.</summary>
        public string SessionId { get; }

        /// <summary>Optional domain topic, quest, scene, or course id.</summary>
        public string TopicId { get; }

        /// <summary>Default empty scope that preserves role-only memory keys.</summary>
        public static AgentMemoryScope Empty => new("", "", "", "");
    }
}
