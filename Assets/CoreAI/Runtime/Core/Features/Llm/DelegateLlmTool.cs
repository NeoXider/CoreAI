using System;

namespace CoreAI.Ai
{
    /// <summary>
    /// LLM tool implemented by a delegate callback.
    /// </summary>
    public sealed class DelegateLlmTool : ILlmTool
    {
        public string Name { get; }
        public string Description { get; }

        /// <summary>
        /// JSON schema that describes tool parameters.
        /// </summary>
        public string ParametersSchema => "{}";

        public bool AllowDuplicates { get; set; }

        public Delegate ActionDelegate { get; }

        public DelegateLlmTool(string name, string description, Delegate action)
        {
            Name = name ?? throw new ArgumentNullException(nameof(name));
            Description = description ?? throw new ArgumentNullException(nameof(description));
            ActionDelegate = action ?? throw new ArgumentNullException(nameof(action));
        }
    }
}