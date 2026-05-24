using System;
using System.Collections.Generic;
using CoreAI.Infrastructure.Llm;

namespace CoreAI.Infrastructure.Llm
{
    /// <summary>
    /// Unity-side implementation of <see cref="IToolExecutionNotifier"/>
    /// that delegates to <see cref="CoreAi.NotifyToolExecuted"/>.
    /// </summary>
    public sealed class CoreAiToolExecutionNotifier : IToolExecutionNotifier
    {
        public static readonly CoreAiToolExecutionNotifier Instance = new();

        public void NotifyToolExecuted(
            string roleId,
            string toolName,
            IDictionary<string, object> arguments,
            object result)
        {
            CoreAi.NotifyToolExecuted(roleId, toolName, arguments, result);
        }
    }
}