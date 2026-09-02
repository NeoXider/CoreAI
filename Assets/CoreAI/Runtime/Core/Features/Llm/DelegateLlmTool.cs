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

        /// <summary>
        /// Settable counterpart of <see cref="ILlmTool.ToolTimeoutMsOverride"/>, so a delegate-registered
        /// tool that waits for a human (a confirmation prompt, an inline card) can get its own budget
        /// without first being rewritten as a class. <c>null</c> keeps the global setting.
        /// </summary>
        public int? ToolTimeoutMsOverride { get; set; }

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
                ValueTask<object> invocation;
                try
                {
                    invocation = InnerFunction.InvokeAsync(arguments, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex) when (ShouldConvertSynchronousFault(ex))
                {
                    return $"Error: {ex.Message}";
                }

                bool completedSynchronously = invocation.IsCompleted;
                try
                {
                    return await invocation.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex) when (!completedSynchronously)
                {
                    // WHY: MEAI argument binding is synchronous — a fault observed only after the inner
                    // ValueTask went async can only come from the delegate body (or its returned Task),
                    // regardless of stack shape. This covers non-async lambdas returning a Task whose
                    // frames never include the lambda itself.
                    return $"Error: {(TryGetDelegateException(ex, out Exception inner) ? inner : ex).Message}";
                }
                catch (Exception ex) when (ShouldConvertSynchronousFault(ex))
                {
                    return $"Error: {(TryGetDelegateException(ex, out Exception inner) ? inner : ex).Message}";
                }
            }

            /// <summary>
            /// Classifies a synchronously-observed fault: delegate-body exceptions become error results,
            /// MEAI argument-coercion failures escape so the policy traces them as never-invoked.
            /// A conversion-shaped exception with no delegate frame is treated as coercion (the residual
            /// ambiguity: a synchronous body throw whose frames were stripped, e.g. under IL2CPP).
            /// </summary>
            private bool ShouldConvertSynchronousFault(Exception ex)
            {
                if (TryGetDelegateException(ex, out _))
                {
                    return true;
                }

#if !COREAI_LLM
                // WHY: ToolExecutionPolicy is stripped with the LLM module and no policy traces
                // coercion failures; converting every synchronous fault to an error result is the
                // safe standalone behavior.
                return true;
#else
                return !Infrastructure.Llm.ToolExecutionPolicy.LooksLikeArgumentConversionError(ex);
#endif
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
            foreach (ParameterInfo parameter in action.Method.GetParameters())
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
