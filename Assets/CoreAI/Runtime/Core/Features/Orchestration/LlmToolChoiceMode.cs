namespace CoreAI.Ai
{
    /// <summary>
    /// Controls whether the model is free to choose, must call any tool, must call a specific tool,
    /// or must answer without tools. Maps 1-to-1 onto Microsoft.Extensions.AI <c>ChatToolMode</c>:
    /// <list type="bullet">
    /// <item><see cref="Auto"/> → <c>null</c> (provider default; model decides).</item>
    /// <item><see cref="RequireAny"/> → <c>ChatToolMode.RequireAny</c>.</item>
    /// <item><see cref="RequireSpecific"/> → <c>ChatToolMode.RequireSpecific(name)</c>; uses <c>RequiredToolName</c>.</item>
    /// <item><see cref="None"/> → <c>ChatToolMode.None</c> (model is forbidden from calling tools).</item>
    /// </list>
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

        /// <summary>Provider MUST answer with text only — no tool calls allowed.</summary>
        None = 3
    }
}
