using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Ai;

namespace CoreAI.Infrastructure.Llm
{
    /// <summary>
    /// Делегирует <see cref="CompleteAsync"/> в <see cref="ILlmClientRegistry"/> по <see cref="LlmCompletionRequest.AgentRoleId"/>.
    /// </summary>
    public sealed class RoutingLlmClient : ILlmClient
    {
        private readonly ILlmClientRegistry _registry;

        /// <param name="registry">Реестр профилей и маршрутов ролей.</param>
        public RoutingLlmClient(ILlmClientRegistry registry)
        {
            _registry = registry;
        }

        /// <summary>Выставляет <see cref="LlmCompletionRequest.RoutingProfileId"/> до логирования внешним декоратором.</summary>
        public void PreflightAnnotate(LlmCompletionRequest request)
        {
            if (request == null)
            {
                return;
            }

            ILlmClient inner = _registry.ResolveClientForRole(request.AgentRoleId);
            request.RoutingProfileId = DescribeInner(inner);
            request.ContextWindowTokens = _registry.ResolveContextWindowForRole(request.AgentRoleId);
        }

        /// <inheritdoc />
        public Task<LlmCompletionResult> CompleteAsync(
            LlmCompletionRequest request,
            CancellationToken cancellationToken = default)
        {
            if (request == null)
            {
                return Task.FromResult(new LlmCompletionResult { Ok = false, Error = "LlmCompletionRequest is null" });
            }

            ILlmClient inner = _registry.ResolveClientForRole(request.AgentRoleId);
            request.RoutingProfileId = DescribeInner(inner);
            request.ContextWindowTokens = _registry.ResolveContextWindowForRole(request.AgentRoleId);
            return inner.CompleteAsync(request, cancellationToken);
        }

        /// <summary>
        /// Маршрутизируемый стриминг: выбирает клиент по <see cref="LlmCompletionRequest.AgentRoleId"/>
        /// и делегирует <see cref="ILlmClient.CompleteStreamingAsync"/>. Без этого override'а
        /// default-реализация интерфейса вызывала бы <see cref="CompleteAsync"/> и отдавала бы
        /// весь ответ одним чанком — стриминг в UI был бы не виден.
        /// </summary>
        public async IAsyncEnumerable<LlmStreamChunk> CompleteStreamingAsync(
            LlmCompletionRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            if (request == null)
            {
                yield return new LlmStreamChunk { IsDone = true, Error = "LlmCompletionRequest is null" };
                yield break;
            }

            ILlmClient inner = _registry.ResolveClientForRole(request.AgentRoleId);
            request.RoutingProfileId = DescribeInner(inner);
            request.ContextWindowTokens = _registry.ResolveContextWindowForRole(request.AgentRoleId);

            await foreach (LlmStreamChunk chunk in inner.CompleteStreamingAsync(request, cancellationToken))
            {
                yield return chunk;
            }
        }

        private static string DescribeInner(ILlmClient inner)
        {
            if (inner == null)
            {
                return "?";
            }

#if !COREAI_NO_LLM
            if (inner is OpenAiChatLlmClient)
            {
                return "OpenAiHttp";
            }
#endif
#if !COREAI_NO_LLM && !UNITY_WEBGL
            if (inner is MeaiLlmUnityClient)
            {
                return "LlmUnity";
            }
#endif
            if (inner is StubLlmClient)
            {
                return "Stub";
            }

            return inner.GetType().Name;
        }
    }
}