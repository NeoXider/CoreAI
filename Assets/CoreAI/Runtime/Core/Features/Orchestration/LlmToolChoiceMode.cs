namespace CoreAI.Ai
{
    /// <summary>
    /// Controls whether the model is free to choose, must call any tool, must call a specific tool,
    /// or must answer without tools. Maps 1-to-1 onto Microsoft.Extensions.AI <c>ChatToolMode</c>:
    /// <list type="bullet">    /// </list>
    /// Set on <see cref="AiTaskRequest"/> by application-layer logic (intent classifiers,
    /// retry pipelines) when LLM determinism around tool calling matters more than the
    /// model's own routing heuristics.
    /// </summary>
    public enum LlmToolChoiceMode
    {
        /// <summary>Model decides whether to call a tool (default, provider-native behaviour).</summary>
        Auto = 0,

        /// <summary>Provider MUST emit at least one tool call from the available tool set.</summary>
        RequireAny = 1,

        /// <summary>Provider MUST emit a tool call with the name in <c>RequiredToolName</c>.</summary>
        RequireSpecific = 2,

        /// <summary>Disables tool choice and asks the model to answer without tool calls.</summary>
        None = 3
    }
}
