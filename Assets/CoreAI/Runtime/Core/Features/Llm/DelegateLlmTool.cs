using System;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
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
            AIFunction function = AIFunctionFactory.Create(action, options);
            return new DelegateExceptionBoundaryAIFunction(function, action.Method);
        }

        private sealed class DelegateExceptionBoundaryAIFunction : DelegatingAIFunction
        {
            private readonly MethodInfo _delegateMethod;
            private readonly Type _delegateStateMachineType;

            public DelegateExceptionBoundaryAIFunction(AIFunction innerFunction, MethodInfo delegateMethod)
                : base(innerFunction)
            {
                _delegateMethod = delegateMethod;
                _delegateStateMachineType =
                    delegateMethod.GetCustomAttribute<AsyncStateMachineAttribute>()?.StateMachineType;
            }

            protected override async ValueTask<object> InvokeCoreAsync(
                AIFunctionArguments arguments,
                CancellationToken cancellationToken)
            {
                try
                {
                    return await InnerFunction.InvokeAsync(arguments, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex) when (TryGetDelegateException(ex, out Exception delegateException))
                {
                    return $"Error: {delegateException.Message}";
                }
            }

            private bool TryGetDelegateException(Exception exception, out Exception delegateException)
            {
                for (Exception current = exception; current != null; current = current.InnerException)
                {
                    if (OriginatedInDelegate(current))
                    {
                        delegateException = current;
                        return true;
                    }
                }

                delegateException = null;
                return false;
            }

            private bool OriginatedInDelegate(Exception exception)
            {
                StackFrame[] frames = new StackTrace(exception, false).GetFrames();
                if (frames == null)
                {
                    return false;
                }

                foreach (StackFrame frame in frames)
                {
                    MethodBase method = frame.GetMethod();
                    if (method == _delegateMethod ||
                        (_delegateStateMachineType != null && method?.DeclaringType == _delegateStateMachineType))
                    {
                        return true;
                    }
                }

                return false;
            }
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
