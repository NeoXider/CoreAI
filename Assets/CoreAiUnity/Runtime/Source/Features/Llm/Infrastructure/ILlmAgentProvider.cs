using UnityEngine;

namespace CoreAI.Infrastructure.Llm
{
#if COREAI_HAS_LLMUNITY && !UNITY_WEBGL
    using LLMUnity;

    /// <summary>
    /// Resolves LLMUnity agents by logical agent name.
    /// </summary>
    public interface ILlmAgentProvider
    {
        /// <summary>Resolves an LLMAgent or Unity object by configured agent name.</summary>
        LLMAgent Resolve(string agentName);
    }
#else
    /// <summary>
    /// Resolves LLMUnity agents by logical agent name.
    /// </summary>
    public interface ILlmAgentProvider
    {
        /// <summary>Resolves an LLMAgent or Unity object by configured agent name.</summary>
        Object Resolve(string agentName);
    }

    public sealed class SceneLlmAgentProvider : ILlmAgentProvider
    {
        public Object Resolve(string agentName) => null;
    }
#endif
}
