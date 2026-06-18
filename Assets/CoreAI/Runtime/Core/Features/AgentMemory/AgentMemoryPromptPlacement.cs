using System;
using System.Text;

namespace CoreAI.Ai
{
    internal readonly struct AgentMemoryPromptParts
    {
        public AgentMemoryPromptParts(string prefixBlock, string tailBlock)
        {
            PrefixBlock = prefixBlock ?? "";
            TailBlock = tailBlock ?? "";
        }

        public string PrefixBlock { get; }

        public string TailBlock { get; }

        public bool HasTailUpdates => !string.IsNullOrWhiteSpace(TailBlock);
    }

    internal static class AgentMemoryPromptPlacement
    {
        private const string MemoryHeading = "## Memory";
        private const string MemoryUpdatesHeading = "## Memory (updates)";

        public static AgentMemoryPromptParts Build(AgentMemoryState state)
        {
            string current = Normalize(state?.Memory);
            if (string.IsNullOrEmpty(current))
            {
                return default;
            }

            string snapshot = Normalize(state?.SystemPromptMemorySnapshot);
            if (string.IsNullOrEmpty(snapshot))
            {
                return new AgentMemoryPromptParts(FormatMemoryBlock(current), "");
            }

            string tail = string.Equals(snapshot, current, StringComparison.Ordinal)
                ? ""
                : FormatMemoryUpdatesBlock(snapshot, current);
            return new AgentMemoryPromptParts(FormatMemoryBlock(snapshot), tail);
        }

        public static bool NeedsInitialSnapshot(AgentMemoryState state)
        {
            return !string.IsNullOrWhiteSpace(state?.Memory) &&
                   string.IsNullOrWhiteSpace(state.SystemPromptMemorySnapshot);
        }

        public static bool HasPendingUpdates(AgentMemoryState state)
        {
            string current = Normalize(state?.Memory);
            if (string.IsNullOrEmpty(current))
            {
                return false;
            }

            string snapshot = Normalize(state?.SystemPromptMemorySnapshot);
            return !string.IsNullOrEmpty(snapshot) &&
                   !string.Equals(snapshot, current, StringComparison.Ordinal);
        }

        public static bool ConsolidateSnapshot(AgentMemoryState state)
        {
            string current = Normalize(state?.Memory);
            if (state == null || string.IsNullOrEmpty(current))
            {
                return false;
            }

            int latestVersion = GetLatestVersion(state);
            if (string.Equals(state.SystemPromptMemorySnapshot ?? "", current, StringComparison.Ordinal) &&
                state.SystemPromptMemoryVersion == latestVersion)
            {
                return false;
            }

            state.SystemPromptMemorySnapshot = current;
            state.SystemPromptMemoryVersion = latestVersion;
            return true;
        }

        private static string FormatMemoryBlock(string memory)
        {
            return MemoryHeading + "\n" + memory.Trim();
        }

        private static string FormatMemoryUpdatesBlock(string snapshot, string current)
        {
            string update = TryBuildAppendDelta(snapshot, current);
            if (!string.IsNullOrWhiteSpace(update))
            {
                return MemoryUpdatesHeading + "\nNew memory since cached prefix:\n" + update.Trim();
            }

            return MemoryUpdatesHeading + "\nCurrent canonical memory. This overrides older cached ## Memory if there is a conflict:\n" +
                   current.Trim();
        }

        private static string TryBuildAppendDelta(string snapshot, string current)
        {
            if (string.IsNullOrEmpty(snapshot) ||
                string.IsNullOrEmpty(current) ||
                !current.StartsWith(snapshot, StringComparison.Ordinal))
            {
                return "";
            }

            string delta = current.Substring(snapshot.Length).Trim();
            return delta;
        }

        private static int GetLatestVersion(AgentMemoryState state)
        {
            int latest = 0;
            AgentMemoryVersionSnapshot[] versions = state?.Versions;
            if (versions == null)
            {
                return latest;
            }

            for (int i = 0; i < versions.Length; i++)
            {
                if (versions[i] != null && versions[i].Version > latest)
                {
                    latest = versions[i].Version;
                }
            }

            return latest;
        }

        private static string Normalize(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? "" : value.Trim();
        }
    }
}
