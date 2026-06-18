using System.Collections.Generic;
using Microsoft.Extensions.AI;

namespace CoreAI.Ai
{
    /// <summary>
    /// Defines the contract for llm tool implementations.
    /// </summary>
    public interface ILlmTool
    {
        /// <summary>Public name.</summary>
        string Name { get; }

        /// <summary>Human-readable description.</summary>
        string Description { get; }

        /// <summary>JSON schema describing tool parameters.</summary>
        string ParametersSchema { get; }

        /// <summary>True when repeated calls with the same arguments are meaningful and should not be suppressed.</summary>
        bool AllowDuplicates { get; }
    }

    /// <summary>
    /// LLM tool that can expose itself as a single Microsoft.Extensions.AI function without reflection.
    /// The returned function must complete with a serializable result for the model; null or empty payloads
    /// are normalized by the tool execution policy into an explicit tool-result message.
    /// </summary>
    public interface IAIFunctionLlmTool : ILlmTool
    {
        /// <summary>Creates the MEAI function binding for this tool.</summary>
        AIFunction CreateAIFunction();
    }

    /// <summary>
    /// LLM tool that expands into several Microsoft.Extensions.AI functions without reflection.
    /// Each returned function must complete with a serializable result for the model; null or empty payloads
    /// are normalized by the tool execution policy into an explicit tool-result message.
    /// </summary>
    public interface IAIFunctionsLlmTool : ILlmTool
    {
        /// <summary>Creates the MEAI function bindings for this tool.</summary>
        IEnumerable<AIFunction> CreateAIFunctions();
    }

    /// <summary>
    /// Base class for strongly typed LLM tools.
    /// </summary>
    public abstract class LlmToolBase : ILlmTool
    {
        public abstract string Name { get; }
        public abstract string Description { get; }
        public virtual string ParametersSchema => "{}";
        public virtual bool AllowDuplicates => false;

        protected static string JsonParams(params (string name, string type, bool required, string desc)[] p)
        {
            List<string> props = new();
            List<string> requiredProps = new();
            foreach ((string name, string type, bool required, string desc) in p)
            {
                props.Add($"\"{name}\":{{\"type\":\"{type}\",\"description\":\"{desc}\"}}");
                if (required)
                {
                    requiredProps.Add($"\"{name}\"");
                }
            }

            string requiredPart = requiredProps.Count > 0 ? $",\"required\":[{string.Join(",", requiredProps)}]" : "";
            return $"{{\"type\":\"object\",\"properties\":{{{string.Join(",", props)}}}{requiredPart}}}";
        }
    }
}
