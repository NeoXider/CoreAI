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
        private readonly AIFunction _function;
        private readonly string _parametersSchema;

        public string Name { get; }
        public string Description { get; }

        /// <summary>
        /// JSON schema that describes tool parameters.
        /// </summary>
        public string ParametersSchema => _parametersSchema;

        public bool AllowDuplicates { get; set; }

        public Delegate ActionDelegate { get; }

        public DelegateLlmTool(string name, string description, Delegate action)
        {
            Name = name ?? throw new ArgumentNullException(nameof(name));
            Description = description ?? throw new ArgumentNullException(nameof(description));
            ActionDelegate = action ?? throw new ArgumentNullException(nameof(action));
            _function = CreateAIFunction(ActionDelegate, Name, Description);
            _parametersSchema = HasModelVisibleParameters(ActionDelegate) ? _function.JsonSchema.ToString() : "{}";
        }

        /// <summary>
        /// Creates the MEAI binding used by direct tool calls and by the skill proxy.
        /// </summary>
        public AIFunction CreateAIFunction()
        {
            return _function;
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

        private static AIFunction CreateAIFunction(Delegate action, string name, string description)
        {
            AIFunctionFactoryOptions options = new()
            {
                Name = name,
                Description = description
            };
            return AIFunctionFactory.Create(action, options);
        }

        private static bool HasModelVisibleParameters(Delegate action)
        {
            foreach (System.Reflection.ParameterInfo parameter in action.Method.GetParameters())
            {
                if (parameter.ParameterType != typeof(CancellationToken))
                {
                    return true;
                }
            }

            return false;
        }
    }
}
