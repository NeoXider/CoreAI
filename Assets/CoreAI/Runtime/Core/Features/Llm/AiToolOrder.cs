using System;
using System.Collections.Generic;

namespace CoreAI.Ai
{
    /// <summary>
    /// Shared deterministic ordering for LLM tools before they are rendered into prompts or
    /// provider-native tool arrays.
    /// </summary>
    public static class AiToolOrder
    {
        /// <summary>
        /// Returns a stable snapshot sorted by tool name using ordinal comparison.
        /// </summary>
        public static IReadOnlyList<ILlmTool> Canonical(IReadOnlyList<ILlmTool> tools)
        {
            if (tools == null || tools.Count == 0)
            {
                return Array.Empty<ILlmTool>();
            }

            List<ToolEntry> entries = new(tools.Count);
            for (int i = 0; i < tools.Count; i++)
            {
                entries.Add(new ToolEntry(tools[i], i));
            }

            entries.Sort(CompareEntries);

            ILlmTool[] sorted = new ILlmTool[entries.Count];
            for (int i = 0; i < entries.Count; i++)
            {
                sorted[i] = entries[i].Tool;
            }

            return sorted;
        }

        private static int CompareEntries(ToolEntry x, ToolEntry y)
        {
            int nameCompare = StringComparer.Ordinal.Compare(x.Tool?.Name, y.Tool?.Name);
            return nameCompare != 0 ? nameCompare : x.Index.CompareTo(y.Index);
        }

        private readonly struct ToolEntry
        {
            public ToolEntry(ILlmTool tool, int index)
            {
                Tool = tool;
                Index = index;
            }

            public ILlmTool Tool { get; }
            public int Index { get; }
        }
    }
}