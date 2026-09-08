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
        /// Настраиваемый аналог <see cref="ILlmTool.IsMutating"/>: инструмент-делегат с побочным эффектом
        /// (спавн, запись в сохранение, вызов сервера) объявляет его здесь и попадает в цепочку
        /// сериализации мутаций, не превращаясь ради этого в класс. По умолчанию <c>false</c> — read-only.
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
        /// Граница исключений тела делегата: всё, что вылетает из вызова (кроме отмены), становится
        /// результатом <c>"Error: …"</c>, который модель может прочитать и исправить, а не сбоем запроса.
        /// <para>
        /// ПОЧЕМУ здесь нет классификации «привязка аргументов vs тело»: раньше она искала метод
        /// делегата в стеке исключения, а под IL2CPP/WebGL фреймы срываются — тогда исключение ИЗ ТЕЛА
        /// выглядело как сбой привязки, политика записывала «инструмент не вызывался», и декораторы
        /// ретрая/фолбэка повторяли ход, который уже изменил мир. Теперь различение делает
        /// <c>ToolExecutionPolicy</c> консервативно на границе вызова; всё, что
        /// добралось сюда, по определению уже «вызов», и единственно безопасная трактовка — «тело
        /// исполнялось». Поэтому граница одна для синхронных и асинхронных сбоев и не зависит от формы стека.
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
