using System;
using System.Collections.Generic;

namespace CoreAI.Ai
{
    /// <summary>
    /// Serializable role memory payload stored by <see cref="IAgentMemoryStore"/>.
    /// </summary>
    public sealed class AgentMemoryState
    {
        /// <summary>
        /// Last composed system prompt associated with the stored memory.
        /// </summary>
        public string LastSystemPrompt { get; set; }

        /// <summary>
        /// Durable memory text accumulated for the role.
        /// </summary>
        public string Memory { get; set; }

        /// <summary>
        /// Default number of memory mutation snapshots retained per role.
        /// </summary>
        public const int DefaultMaxMemoryVersions = 30;

        /// <summary>
        /// Optional per-state cap for <see cref="Versions"/>. Values below one use
        /// <see cref="DefaultMaxMemoryVersions"/>.
        /// </summary>
        public int MaxMemoryVersions { get; set; } = DefaultMaxMemoryVersions;

        /// <summary>
        /// Bounded audit trail of memory mutations. Each snapshot stores the canonical memory
        /// document after the mutation so the store can roll back without a separate diff engine.
        /// </summary>
        public AgentMemoryVersionSnapshot[] Versions { get; set; }

        /// <summary>
        /// Records one memory mutation snapshot and trims the retained history to the configured cap.
        /// </summary>
        public AgentMemoryVersionSnapshot RecordVersion(string action, string contentAfter, string note = null,
            int maxVersions = 0)
        {
            int cap = maxVersions > 0 ? maxVersions :
                MaxMemoryVersions > 0 ? MaxMemoryVersions : DefaultMaxMemoryVersions;
            int nextVersion = GetNextVersion();
            AgentMemoryVersionSnapshot snapshot = new()
            {
                Version = nextVersion,
                Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                Action = string.IsNullOrWhiteSpace(action) ? "unknown" : action.Trim(),
                ContentAfter = contentAfter ?? "",
                Note = note ?? ""
            };

            List<AgentMemoryVersionSnapshot> versions = Versions != null
                ? new List<AgentMemoryVersionSnapshot>(Versions)
                : new List<AgentMemoryVersionSnapshot>();
            versions.Add(snapshot);

            while (versions.Count > cap)
            {
                versions.RemoveAt(0);
            }

            Versions = versions.ToArray();
            return snapshot;
        }

        private int GetNextVersion()
        {
            int next = 1;
            if (Versions == null)
            {
                return next;
            }

            for (int i = 0; i < Versions.Length; i++)
            {
                if (Versions[i] != null && Versions[i].Version >= next)
                {
                    next = Versions[i].Version + 1;
                }
            }

            return next;
        }
    }

    /// <summary>
    /// One retained memory-document snapshot for audit and rollback.
    /// </summary>
    [Serializable]
    public sealed class AgentMemoryVersionSnapshot
    {
        /// <summary>Monotonic version number for the role's memory document.</summary>
        public int Version { get; set; }

        /// <summary>Unix timestamp in seconds when the mutation was recorded.</summary>
        public long Timestamp { get; set; }

        /// <summary>Mutation action that produced this version.</summary>
        public string Action { get; set; }

        /// <summary>Canonical memory document after the mutation.</summary>
        public string ContentAfter { get; set; }

        /// <summary>Short human-readable note or diff summary.</summary>
        public string Note { get; set; }
    }
}
