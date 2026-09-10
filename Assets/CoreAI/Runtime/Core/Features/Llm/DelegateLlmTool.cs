using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;

namespace CoreAI.Ai
{
    /// <summary>
    /// LLM tool implemented by a delegate callback.
    /// <para>
    /// Awaits here resume on the host <see cref="SynchronizationContext"/>: in the WebGL player a
    /// <c>ConfigureAwait(false)</c> continuation would be queued to a thread pool that does not exist.
    /// The delegate itself is bound by MEAI, which awaits an async delegate with
    /// <c>ConfigureAwait(false)</c> inside its binary — a delegate whose task completes asynchronously
    /// must therefore return it through <see cref="MeaiToolTaskBridge.Publish{T}"/>, exactly as the
    /// built-in Lua tools do.
    /// </para>
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

        /// <summary>Completes the agent turn after a successful invocation.</summary>
        public bool EndsTurn { get; set; }

        /// <summary>
        /// Settable counterpart of <see cref="ILlmTool.ToolTimeoutMsOverride"/>, so a delegate-registered
        /// tool that waits for a human (a confirmation prompt, an inline card) can get its own budget
        /// without first being rewritten as a class. <c>null</c> keeps the global setting.
        /// </summary>
        public int? ToolTimeoutMsOverride { get; set; }

        /// <summary>
        /// The settable counterpart of <see cref="ILlmTool.IsMutating"/>: a delegate tool with a side
        /// effect (a spawn, a save write, a server call) declares it here and joins the mutation
        /// serialization chain without having to become a class for that. Defaults to <c>false</c>,
        /// i.e. read-only.
        /// </summary>
        public bool IsMutating { get; set; }

        public Delegate ActionDelegate { get; }

        public DelegateLlmTool(string name, string description, Delegate action)
        {
            Name = name ?? throw new ArgumentNullException(nameof(name));
            Description = description ?? throw new ArgumentNullException(nameof(description));
            ActionDelegate = action ?? throw new ArgumentNullException(nameof(action));
            _function = CreateAIFunction(ActionDelegate, Name, Description);
            _parametersSchema = HasModelVisibleParameters(_function) ? _function.JsonSchema.ToString() : "{}";
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
                .InvokeAsync(SkillSetToolResolver.CreateArguments(argumentsJson ?? "{}"), cancellationToken);
        }

        private static AIFunction CreateAIFunction(Delegate action, string name, string description)
        {
            AIFunctionFactoryOptions options = new()
            {
                Name = name,
                Description = description
            };
            AIFunction function = AIFunctionFactory.Create(action, options);
            return new DelegateExceptionBoundaryAIFunction(function);
        }

        /// <summary>
        /// The exception boundary around the delegate body: anything thrown out of the invocation (other
        /// than cancellation) becomes an <c>"Error: ..."</c> result the model can read and correct, rather
        /// than a failure of the whole request.
        /// <para>
        /// WHY there is no "argument binding vs body" classification here: it used to look for the
        /// delegate's method in the exception's stack trace, and under IL2CPP/WebGL frames get stripped -
        /// an exception FROM THE BODY then looked like a binding failure, the policy recorded "the tool
        /// was never invoked", and the retry/fallback decorators replayed a turn that had already changed
        /// the world. The distinction is now drawn conservatively by <c>ToolExecutionPolicy</c> at the
        /// invocation boundary; anything that got this far is by definition already "an invocation", and
        /// the only safe reading is "the body ran". That is why the boundary is one and the same for
        /// synchronous and asynchronous failures and does not depend on the shape of the stack.
        /// </para>
        /// </summary>
        private sealed class DelegateExceptionBoundaryAIFunction : DelegatingAIFunction
        {
            public DelegateExceptionBoundaryAIFunction(AIFunction innerFunction)
                : base(innerFunction)
            {
            }

            protected override async ValueTask<object> InvokeCoreAsync(
                AIFunctionArguments arguments,
                CancellationToken cancellationToken)
            {
                try
                {
                    return await InnerFunction.InvokeAsync(arguments, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    // WHY: The model needs the underlying failure, not a binder's exception wrapper.
                    Exception cause = ex.GetBaseException();
                    return $"Error: {cause.Message}";
                }
            }
        }

        private static bool HasModelVisibleParameters(AIFunction function)
        {
            return function.JsonSchema.TryGetProperty("properties", out System.Text.Json.JsonElement properties) &&
                   properties.ValueKind == System.Text.Json.JsonValueKind.Object &&
                   properties.EnumerateObject().MoveNext();
        }

    }
}
