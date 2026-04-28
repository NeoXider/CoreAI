using System;
using System.Collections.Generic;

namespace CoreAI.Ai
{
    /// <summary>
    /// Portable LLM routing profile. Unity and other hosts adapt this model to concrete clients.
    /// </summary>
    public sealed class LlmRouteProfile
    {
        /// <summary>Unique profile id referenced by route rules.</summary>
        public string ProfileId { get; set; } = "default";

        /// <summary>Product-facing execution mode for this profile.</summary>
        public LlmExecutionMode Mode { get; set; } = LlmExecutionMode.Auto;

        /// <summary>Provider or local model id. Hosts may interpret this as an alias.</summary>
        public string Model { get; set; } = "";

        /// <summary>Context window in tokens for requests routed to this profile.</summary>
        public int ContextWindowTokens { get; set; } = 8192;

        /// <summary>Optional maximum response tokens for this profile.</summary>
        public int? MaxOutputTokens { get; set; }

        /// <summary>Named capabilities such as streaming, tools, vision, or json_mode.</summary>
        public IReadOnlyList<string> Capabilities { get; set; } = Array.Empty<string>();
    }
}
