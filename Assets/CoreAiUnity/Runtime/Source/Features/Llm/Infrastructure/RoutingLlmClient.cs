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

            string requestedProfile = request.RoutingProfileId;
            ILlmClient inner = _registry.ResolveClientForRole(request.AgentRoleId, requestedProfile);
            request.RoutingProfileId = _registry.ResolveProfileIdForRole(request.AgentRoleId, requestedProfile);
            request.ContextWindowTokens = _registry.ResolveContextWindowForRole(request.AgentRoleId, requestedProfile);
        }

        /// <inheritdoc />
        public bool SupportsNativeToolCallingForRole(string agentRoleId)
        {
            ILlmClient inner = _registry.ResolveClientForRole(agentRoleId);
            return inner?.SupportsNativeToolCallingForRole(agentRoleId) == true;
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

            ILlmClient inner = Prepare(request, false, out LlmExecutionMode capturedMode);
            try
            {
                LlmCompletionResult result = await inner.CompleteAsync(request, cancellationToken);
                PublishCompleted(request, capturedMode, false, result != null && result.Ok, result?.Error ?? "",
                    result?.ErrorCode ?? LlmErrorCode.None);
                PublishUsage(request, capturedMode, false, result);
                return result;
            }
            catch (LlmOperationTimeoutException)
            {
                PublishCompleted(request, capturedMode, false, false, "timeout", LlmErrorCode.Timeout);
                throw;
            }
            catch (OperationCanceledException)
            {
                PublishCompleted(request, capturedMode, false, false, "cancelled", LlmErrorCode.Cancelled);
                throw;
            }
            catch (Exception ex)
            {
                PublishCompleted(request, capturedMode, false, false, ex.Message, LlmErrorCode.ProviderError);
                throw;
            }
        }

        /// <summary>
        /// Streams a completion through the configured provider while publishing routing diagnostics.
        /// </summary>
        public async IAsyncEnumerable<LlmStreamChunk> CompleteStreamingAsync(
            LlmCompletionRequest request,
            [EnumeratorCancellation]
            CancellationToken cancellationToken = default)
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

            ILlmClient inner = Prepare(request, true, out LlmExecutionMode capturedMode);
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
                        PublishCompleted(request, capturedMode, true, false, "timeout", LlmErrorCode.Timeout);
                        PublishUsage(request, capturedMode, true, lastUsageChunk, false);
                        throw;
                    }
                    catch (OperationCanceledException)
                    {
                        PublishCompleted(request, capturedMode, true, false, "cancelled", LlmErrorCode.Cancelled);
                        PublishUsage(request, capturedMode, true, lastUsageChunk, false);
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

                    if (chunk.PromptTokens.HasValue ||
                        chunk.CompletionTokens.HasValue ||
                        chunk.TotalTokens.HasValue ||
                        chunk.CacheReadTokens > 0 ||
                        chunk.CacheWriteTokens > 0)
                    {
                        lastUsageChunk = chunk;
                    }

                    yield return chunk;
                }

                PublishCompleted(request, capturedMode, true, ok, error, errorCode);
                PublishUsage(request, capturedMode, true, lastUsageChunk, ok);
            }
            finally
            {
                await enumerator.DisposeAsync();
            }
        }

        private ILlmClient Prepare(
            LlmCompletionRequest request,
            bool streaming,
            out LlmExecutionMode capturedMode)
        {
            string requestedProfile = request.RoutingProfileId;
            ILlmClient inner = _registry.ResolveClientForRole(request.AgentRoleId, requestedProfile);
            request.RoutingProfileId = _registry.ResolveProfileIdForRole(request.AgentRoleId, requestedProfile);
            request.ContextWindowTokens = _registry.ResolveContextWindowForRole(request.AgentRoleId, requestedProfile);
            capturedMode = _registry.ResolveExecutionModeForRole(request.AgentRoleId, requestedProfile);
            _backendSelectedPublisher?.Publish(new LlmBackendSelected(
                request.TraceId,
                request.AgentRoleId,
                request.RoutingProfileId,
                capturedMode,
                DescribeInner(inner)));
            _requestStartedPublisher?.Publish(new LlmRequestStarted(
                request.TraceId,
                request.AgentRoleId,
                request.RoutingProfileId,
                capturedMode,
                streaming));
            return inner;
        }

        private void PublishCompleted(
            LlmCompletionRequest request,
            LlmExecutionMode capturedMode,
            bool streaming,
            bool success,
            string error,
            LlmErrorCode errorCode)
        {
            _requestCompletedPublisher?.Publish(new LlmRequestCompleted(
                request?.TraceId,
                request?.AgentRoleId,
                request?.RoutingProfileId,
                capturedMode,
                streaming,
                success,
                error,
                errorCode));
        }

        private void PublishUsage(
            LlmCompletionRequest request,
            LlmExecutionMode capturedMode,
            bool streaming,
            LlmCompletionResult result)
        {
            if (result == null ||
                (!result.PromptTokens.HasValue &&
                 !result.CompletionTokens.HasValue &&
                 !result.TotalTokens.HasValue &&
                 result.CacheReadTokens <= 0 &&
                 result.CacheWriteTokens <= 0))
            {
                return;
            }

            _usageReportedPublisher?.Publish(new LlmUsageReported(
                request?.TraceId,
                request?.AgentRoleId,
                request?.RoutingProfileId,
                capturedMode,
                result.Model,
                result.PromptTokens,
                result.CompletionTokens,
                result.TotalTokens,
                streaming,
                result.Ok,
                result.CacheReadTokens,
                result.CacheWriteTokens));
        }

        private void PublishUsage(
            LlmCompletionRequest request,
            LlmExecutionMode capturedMode,
            bool streaming,
            LlmStreamChunk chunk,
            bool success)
        {
            if (chunk == null)
            {
                return;
            }

            _usageReportedPublisher?.Publish(new LlmUsageReported(
                request?.TraceId,
                request?.AgentRoleId,
                request?.RoutingProfileId,
                capturedMode,
                chunk.Model,
                chunk.PromptTokens,
                chunk.CompletionTokens,
                chunk.TotalTokens,
                streaming,
                success,
                chunk.CacheReadTokens,
                chunk.CacheWriteTokens));
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
