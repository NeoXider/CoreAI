using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Ai;
using CoreAI.Messaging;
using MessagePipe;

namespace CoreAI.Infrastructure.Llm
{
    /// <summary>
    /// Routes LLM requests to profile-specific clients.
    /// </summary>
    public sealed class RoutingLlmClient : ILlmClient, ILlmPreflightAnnotator
    {
        private readonly ILlmClientRegistry _registry;
        private readonly IPublisher<LlmBackendSelected> _backendSelectedPublisher;
        private readonly IPublisher<LlmRequestStarted> _requestStartedPublisher;
        private readonly IPublisher<LlmRequestCompleted> _requestCompletedPublisher;
        private readonly IPublisher<LlmUsageReported> _usageReportedPublisher;

        /// <param name="registry">The registry value.</param>
        public RoutingLlmClient(
            ILlmClientRegistry registry,
            IPublisher<LlmBackendSelected> backendSelectedPublisher = null,
            IPublisher<LlmRequestStarted> requestStartedPublisher = null,
            IPublisher<LlmRequestCompleted> requestCompletedPublisher = null,
            IPublisher<LlmUsageReported> usageReportedPublisher = null)
        {
            _registry = registry;
            _backendSelectedPublisher = backendSelectedPublisher;
            _requestStartedPublisher = requestStartedPublisher;
            _requestCompletedPublisher = requestCompletedPublisher;
            _usageReportedPublisher = usageReportedPublisher;
        }

        /// <summary>Annotates an LLM request with routing metadata before it is sent.</summary>
        public void PreflightAnnotate(LlmCompletionRequest request)
        {
            if (request == null)
            {
                return;
            }

            ILlmClient inner = _registry.ResolveClientForRole(request.AgentRoleId);
            request.RoutingProfileId = _registry.ResolveProfileIdForRole(request.AgentRoleId);
            request.ContextWindowTokens = _registry.ResolveContextWindowForRole(request.AgentRoleId);
        }

        /// <inheritdoc />
        public async Task<LlmCompletionResult> CompleteAsync(
            LlmCompletionRequest request,
            CancellationToken cancellationToken = default)
        {
            if (request == null)
            {
                return new LlmCompletionResult
                {
                    Ok = false,
                    Error = "LlmCompletionRequest is null",
                    ErrorCode = LlmErrorCode.InvalidRequest
                };
            }

            ILlmClient inner = Prepare(request, false);
            try
            {
                LlmCompletionResult result = await inner.CompleteAsync(request, cancellationToken);
                PublishCompleted(request, false, result != null && result.Ok, result?.Error ?? "",
                    result?.ErrorCode ?? LlmErrorCode.None);
                PublishUsage(request, false, result);
                return result;
            }
            catch (LlmOperationTimeoutException)
            {
                PublishCompleted(request, false, false, "timeout", LlmErrorCode.Timeout);
                throw;
            }
            catch (OperationCanceledException)
            {
                PublishCompleted(request, false, false, "cancelled", LlmErrorCode.Cancelled);
                throw;
            }
            catch (Exception ex)
            {
                PublishCompleted(request, false, false, ex.Message, LlmErrorCode.ProviderError);
                throw;
            }
        }

        /// <summary>
        /// Streams a completion through the configured provider while publishing routing diagnostics.
        /// </summary>
        public async IAsyncEnumerable<LlmStreamChunk> CompleteStreamingAsync(
            LlmCompletionRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            if (request == null)
            {
                yield return new LlmStreamChunk
                {
                    IsDone = true,
                    Error = "LlmCompletionRequest is null",
                    ErrorCode = LlmErrorCode.InvalidRequest
                };
                yield break;
            }

            ILlmClient inner = Prepare(request, true);
            bool ok = true;
            string error = "";
            LlmErrorCode errorCode = LlmErrorCode.None;
            LlmStreamChunk lastUsageChunk = null;

            IAsyncEnumerator<LlmStreamChunk> enumerator =
                inner.CompleteStreamingAsync(request, cancellationToken).GetAsyncEnumerator(cancellationToken);
            try
            {
                while (true)
                {
                    bool hasNext;
                    try
                    {
                        hasNext = await enumerator.MoveNextAsync();
                    }
                    catch (LlmOperationTimeoutException)
                    {
                        PublishCompleted(request, true, false, "timeout", LlmErrorCode.Timeout);
                        PublishUsage(request, true, lastUsageChunk, false);
                        throw;
                    }
                    catch (OperationCanceledException)
                    {
                        PublishCompleted(request, true, false, "cancelled", LlmErrorCode.Cancelled);
                        PublishUsage(request, true, lastUsageChunk, false);
                        throw;
                    }

                    if (!hasNext)
                    {
                        break;
                    }

                    LlmStreamChunk chunk = enumerator.Current;
                    if (!string.IsNullOrEmpty(chunk.Error))
                    {
                        ok = false;
                        error = chunk.Error;
                        errorCode = chunk.ErrorCode;
                    }

                    if (chunk.PromptTokens.HasValue || chunk.CompletionTokens.HasValue || chunk.TotalTokens.HasValue)
                    {
                        lastUsageChunk = chunk;
                    }

                    yield return chunk;
                }

                PublishCompleted(request, true, ok, error, errorCode);
                PublishUsage(request, true, lastUsageChunk, ok);
            }
            finally
            {
                await enumerator.DisposeAsync();
            }
        }

        private ILlmClient Prepare(LlmCompletionRequest request, bool streaming)
        {
            ILlmClient inner = _registry.ResolveClientForRole(request.AgentRoleId);
            request.RoutingProfileId = _registry.ResolveProfileIdForRole(request.AgentRoleId);
            request.ContextWindowTokens = _registry.ResolveContextWindowForRole(request.AgentRoleId);
            LlmExecutionMode mode = _registry.ResolveExecutionModeForRole(request.AgentRoleId);
            _backendSelectedPublisher?.Publish(new LlmBackendSelected(
                request.TraceId,
                request.AgentRoleId,
                request.RoutingProfileId,
                mode,
                DescribeInner(inner)));
            _requestStartedPublisher?.Publish(new LlmRequestStarted(
                request.TraceId,
                request.AgentRoleId,
                request.RoutingProfileId,
                mode,
                streaming));
            return inner;
        }

        private void PublishCompleted(
            LlmCompletionRequest request,
            bool streaming,
            bool success,
            string error,
            LlmErrorCode errorCode)
        {
            _requestCompletedPublisher?.Publish(new LlmRequestCompleted(
                request?.TraceId,
                request?.AgentRoleId,
                request?.RoutingProfileId,
                request != null ? _registry.ResolveExecutionModeForRole(request.AgentRoleId) : LlmExecutionMode.Auto,
                streaming,
                success,
                error,
                errorCode));
        }

        private void PublishUsage(LlmCompletionRequest request, bool streaming, LlmCompletionResult result)
        {
            if (result == null ||
                (!result.PromptTokens.HasValue && !result.CompletionTokens.HasValue && !result.TotalTokens.HasValue))
            {
                return;
            }

            _usageReportedPublisher?.Publish(new LlmUsageReported(
                request?.TraceId,
                request?.AgentRoleId,
                request?.RoutingProfileId,
                request != null ? _registry.ResolveExecutionModeForRole(request.AgentRoleId) : LlmExecutionMode.Auto,
                result.Model,
                result.PromptTokens,
                result.CompletionTokens,
                result.TotalTokens,
                streaming,
                result.Ok));
        }

        private void PublishUsage(LlmCompletionRequest request, bool streaming, LlmStreamChunk chunk, bool success)
        {
            if (chunk == null)
            {
                return;
            }

            _usageReportedPublisher?.Publish(new LlmUsageReported(
                request?.TraceId,
                request?.AgentRoleId,
                request?.RoutingProfileId,
                request != null ? _registry.ResolveExecutionModeForRole(request.AgentRoleId) : LlmExecutionMode.Auto,
                chunk.Model,
                chunk.PromptTokens,
                chunk.CompletionTokens,
                chunk.TotalTokens,
                streaming,
                success));
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

            if (inner is ServerManagedLlmClient)
            {
                return "ServerManagedApi";
            }
#endif
#if COREAI_HAS_LLMUNITY && !UNITY_WEBGL
            if (inner is MeaiLlmUnityClient)
            {
                return "LlmUnity";
            }
#endif
            if (inner is StubLlmClient)
            {
                return "Stub";
            }

            if (inner is ClientLimitedLlmClientDecorator limited)
            {
                return "ClientLimited/" + DescribeInner(limited.Inner);
            }

            return inner.GetType().Name;
        }
    }
}
