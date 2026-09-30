using System.Collections.Generic;

namespace CoreAI.Infrastructure.Llm
{
    /// <summary>
    /// Portable abstraction for notifying subscribers when the LLM pipeline
    /// executes a tool. In Unity this delegates to <c>CoreAi.NotifyToolExecuted</c>;
    /// non-Unity hosts can supply their own handler or use <see cref="NullToolExecutionNotifier"/>.
    /// </summary>
    public interface IToolExecutionNotifier
    {
        /// <summary>
        /// Called after a tool is successfully invoked by the pipeline.
        /// Implementations must be exception-safe (callers wrap in try/catch as defense-in-depth).
        /// <paramref name="result"/> is the tool's own return value: for a tool that shows the model images
        /// (the camera tools, any tool returning <see cref="CoreAI.Ai.LlmToolImageResult"/>) it is that object, whose
        /// <c>Images</c> hold the pictures and whose <c>ToString()</c> is only the summary text.
        /// </summary>
        void NotifyToolExecuted(
            string roleId,
            string toolName,
            IDictionary<string, object> arguments,
            object result);
    }

    /// <summary>
    /// Tool execution notifier used when the host has no tool-execution subscribers.
    /// </summary>
    public sealed class NullToolExecutionNotifier : IToolExecutionNotifier
    {
        public static readonly NullToolExecutionNotifier Instance = new();

        public void NotifyToolExecuted(
            string roleId,
            string toolName,
            IDictionary<string, object> arguments,
            object result)
        {
        }
    }
}
