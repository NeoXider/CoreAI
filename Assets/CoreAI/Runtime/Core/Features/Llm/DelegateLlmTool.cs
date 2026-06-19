using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;

namespace CoreAI.Ai
{
    /// <summary>
    /// LLM tool implemented by a delegate callback.
    /// </summary>
    public sealed class DelegateLlmTool : ILlmTool, IAIFunctionLlmTool, IJsonInvocableLlmTool
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

        /// <summary>
        /// Creates the MEAI binding used by direct tool calls and by the skill proxy.
        /// </summary>
        public AIFunction CreateAIFunction()
        {
            AIFunctionFactoryOptions options = new()
            {
                Name = Name,
                Description = Description
            };
            return AIFunctionFactory.Create(ActionDelegate, options);
        }

        /// <summary>
        /// Invokes the delegate from raw JSON arguments through the same MEAI binding used by direct tools.
        /// </summary>
        public async Task<object> InvokeJsonAsync(string argumentsJson, CancellationToken cancellationToken = default)
        {
            AIFunction function = CreateAIFunction();
            return await function
                .InvokeAsync(SkillSetToolResolver.CreateArguments(argumentsJson ?? "{}"), cancellationToken)
                .ConfigureAwait(false);
        }
    }
}
