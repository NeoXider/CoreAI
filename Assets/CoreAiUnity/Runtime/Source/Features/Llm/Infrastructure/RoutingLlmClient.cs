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
    public sealed class RoutingLlmClient : ILlmClient, ILlmPreflightAnnotator, ILlmRequestHeaderScope
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

            LlmRoleRouteSnapshot route = _registry.ResolveRouteForRole(
                request.AgentRoleId, request.RoutingProfileId);
            request.RoutingProfileId = route.ProfileId;
            request.ContextWindowTokens = route.ContextWindowTokens;
        }

        IDisposable ILlmRequestHeaderScope.BeginRequestHeaders(LlmCompletionRequest request)
        {
            if (request == null)
            {
                return null;
            }

            LlmRoleRouteSnapshot route = _registry.ResolveRouteForRole(
                request.AgentRoleId, request.RoutingProfileId);
            return LlmRequestHeaderScopes.Begin(route?.Client, request);
        }

        /// <inheritdoc />
        public bool SupportsNativeToolCallingForRole(string agentRoleId)
        {
            return SupportsNativeToolCallingForRole(agentRoleId, "");
        }

        /// <inheritdoc />
        public bool SupportsNativeToolCallingForRole(string agentRoleId, string routingProfileId)
        {
            // WHY: the tool contract must follow the endpoint the request will actually reach —
            // an agent pinned via WithLlmProfile or re-routed at runtime otherwise keeps the old
            // endpoint's native/text tool strategy and tools silently stop working.
            ILlmClient inner = _registry.ResolveRouteForRole(agentRoleId, routingProfileId)?.Client;
            return inner?.SupportsNativeToolCallingForRole(agentRoleId) == true;
        }

        /// <inheritdoc />
        public int? ResolveContextWindowTokensForRole(string agentRoleId, string routingProfileId)
        {
            LlmRoleRouteSnapshot route = _registry.ResolveRouteForRole(agentRoleId, routingProfileId);
            if (route == null || !route.IsRouted || route.ContextWindowTokens <= 0)
            {
                // WHY: an unrouted request is served by the legacy backend, whose window is owned by
                // settings-based budgets — report "no routing knowledge" instead of a constant.
                return null;
            }

            return route.ContextWindowTokens;
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

            ILlmClient inner = Prepare(request, false, out LlmExecutionMode capturedMode, out long capturedGeneration);
            try
            {
                LlmCompletionResult result = await inner.CompleteAsync(request, cancellationToken);
                LlmErrorCode faultCode = result?.ErrorCode ?? LlmErrorCode.None;
                // WHY: a failed result returned after the caller already cancelled is the caller's stop whatever
                // its code; the result and the published event carry one classification
                // (LlmCancellation.ClassifyCode), while route health below still judges faultCode, the code the
                // endpoint reported. WHY copy-on-write: the inner client may reuse the instance; the Error text
                // follows the code.
                if (result != null && !result.Ok &&
                    LlmCancellation.IsReportedCallerStop(result.ErrorCode, cancellationToken))
                {
                    result = result.WithError(LlmCancellation.CancelledErrorText, LlmErrorCode.Cancelled);
                }

                PublishCompleted(request, capturedMode, capturedGeneration, false, result != null && result.Ok,
                    result?.Error ?? "",
                    result?.ErrorCode ?? LlmErrorCode.None,
                    ResolveRouteHealthCode(faultCode, cancellationToken));
                PublishUsage(request, capturedMode, false, result);
                return result;
            }
            catch (Exception ex)
            {
                // WHY one catch: LlmCancellation decides first (a cancelled caller owns every fault, a library
                // timeout is a timeout), and only then the typed code - collapsing to ProviderError would hide
                // AuthExpired/BackendUnavailable from the degraded-health path and from diagnostics subscribers.
                LlmErrorCode code = ClassifyThrown(ex, cancellationToken, out LlmErrorCode faultCode);
                PublishCompleted(request, capturedMode, capturedGeneration, false, false, DescribeThrown(ex, code),
                    code, ResolveRouteHealthCode(faultCode, cancellationToken));
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

            ILlmClient inner = Prepare(request, true, out LlmExecutionMode capturedMode, out long capturedGeneration);
            bool ok = true;
            string error = "";
            LlmErrorCode errorCode = LlmErrorCode.None;
            // WHY kept apart from errorCode: endpoint health judges the fault the endpoint reported, the
            // published code what the caller sees after LlmCancellation had its say.
            LlmErrorCode faultCode = LlmErrorCode.None;
            LlmStreamChunk lastUsageChunk = null;
            bool completedPublished = false;
            int streamedCompletionChars = 0;
            string lastSeenModel = "";

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
                    catch (Exception ex)
                    {
                        // WHY every exception publishes: a transport exception other than timeout/cancel
                        // previously escaped with no completion event at all — subscribers saw a request
                        // start and never finish. See CompleteAsync for the classification order.
                        completedPublished = true;
                        LlmErrorCode code = ClassifyThrown(ex, cancellationToken, out LlmErrorCode thrownFaultCode);
                        PublishCompleted(request, capturedMode, capturedGeneration, true, false,
                            DescribeThrown(ex, code), code, ResolveRouteHealthCode(thrownFaultCode, cancellationToken));
                        PublishUsage(request, capturedMode, true, lastUsageChunk, false, streamedCompletionChars,
                            lastSeenModel);
                        throw;
                    }

                    if (!hasNext)
                    {
                        break;
                    }

                    LlmStreamChunk chunk = enumerator.Current;
                    if (chunk != null && (chunk.ErrorCode != LlmErrorCode.None || !string.IsNullOrEmpty(chunk.Error)))
                    {
                        // WHY: an error chunk after the caller cancelled is the caller's stop whatever its code
                        // (LlmCancellation.ClassifyCode); route health keeps the code the endpoint reported.
                        // WHY copy-on-write: the inner client may reuse the chunk instance (the timeout
                        // decorator copies for the same rewrite); the Error text follows the code.
                        if (chunk.ErrorCode != LlmErrorCode.None)
                        {
                            faultCode = chunk.ErrorCode;
                        }

                        if (LlmCancellation.IsReportedCallerStop(chunk.ErrorCode, cancellationToken))
                        {
                            chunk = chunk.WithError(LlmCancellation.CancelledErrorText, LlmErrorCode.Cancelled);
                        }
                    }

                    if (chunk != null && !string.IsNullOrEmpty(chunk.Error))
                    {
                        ok = false;
                        error = chunk.Error;
                        errorCode = chunk.ErrorCode;
                    }

                    // WHY: count reasoning too — reasoning models (DeepSeek/Qwen) can emit far more
                    // reasoning than answer, so ignoring it underestimates completion tokens on the
                    // Token Budget page when the server omits usage.
                    streamedCompletionChars += chunk.Text?.Length ?? 0;
                    streamedCompletionChars += chunk.ReasoningText?.Length ?? 0;
                    if (!string.IsNullOrEmpty(chunk.Model))
                    {
                        lastSeenModel = chunk.Model;
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

                completedPublished = true;
                PublishCompleted(request, capturedMode, capturedGeneration, true, ok, error, errorCode,
                    ResolveRouteHealthCode(faultCode, cancellationToken));
                PublishUsage(request, capturedMode, true, lastUsageChunk, ok, streamedCompletionChars, lastSeenModel);
            }
            finally
            {
                if (!completedPublished)
                {
                    // WHY: a consumer that abandons the stream (break + dispose) never lets the
                    // generator reach the post-loop publish — without this, every abandoned stream
                    // leaks an LlmRequestStarted with no matching LlmRequestCompleted.
                    PublishCompleted(request, capturedMode, capturedGeneration, true, false,
                        string.IsNullOrEmpty(error) ? "stream abandoned by consumer" : error,
                        errorCode == LlmErrorCode.None ? LlmErrorCode.Cancelled : errorCode,
                        ResolveRouteHealthCode(faultCode, cancellationToken));
                    PublishUsage(request, capturedMode, true, lastUsageChunk, false, streamedCompletionChars,
                        lastSeenModel);
                }

                await enumerator.DisposeAsync();
            }
        }

        /// <summary>
        /// The code the caller sees for a thrown fault: <see cref="LlmCancellation"/> first (a cancelled caller
        /// owns every fault, a library timeout is a timeout), then the typed code of the
        /// <see cref="LlmClientException"/> found in the fault or its inner chain, else a provider error.
        /// <paramref name="faultCode"/> is what the endpoint itself reported, for route health.
        /// </summary>
        private static LlmErrorCode ClassifyThrown(
            Exception exception,
            CancellationToken callerToken,
            out LlmErrorCode faultCode)
        {
            LlmClientException typed = LlmCancellation.FindClientException(exception);
            faultCode = typed?.ErrorCode ?? LlmErrorCode.ProviderError;
            LlmErrorCode classified = LlmCancellation.Classify(exception, callerToken);
            return classified != LlmErrorCode.None ? classified : faultCode;
        }

        private static string DescribeThrown(Exception exception, LlmErrorCode code)
        {
            if (code == LlmErrorCode.Cancelled)
            {
                return LlmCancellation.CancelledErrorText;
            }

            return LlmCancellation.FindTimeout(exception) != null ? "timeout" : exception.Message;
        }

        /// <summary>
        /// What endpoint health learns from a failed request whose endpoint reported <paramref name="faultCode"/>
        /// (the code before <see cref="LlmCancellation"/> rewrote it for the caller); <see cref="LlmErrorCode.None"/>
        /// means "nothing".
        /// <para>
        /// WHY the caller's token matters here: a transport, timeout or provider fault that happened while the
        /// caller was cancelling says nothing about the endpoint, and marking it degraded on a request nobody
        /// was waiting for is a false alarm. A permanent refusal (an expired key, an exhausted balance) is the
        /// exception - the next request would be refused the same way, so it is reported even when a caller
        /// cancel raced it. The refusal may sit behind a decorator's OperationCanceledException (see
        /// FallbackLlmClientDecorator), which is why callers pass the code found through the inner chain.
        /// </para>
        /// </summary>
        private static LlmErrorCode ResolveRouteHealthCode(LlmErrorCode faultCode, CancellationToken callerToken)
        {
            if (!IsEndpointLevelFailure(faultCode))
            {
                return LlmErrorCode.None;
            }

            return callerToken.IsCancellationRequested && FallbackLlmClientDecorator.IsRetryableError(faultCode)
                ? LlmErrorCode.None
                : faultCode;
        }

        private ILlmClient Prepare(
            LlmCompletionRequest request,
            bool streaming,
            out LlmExecutionMode capturedMode,
            out long capturedGeneration)
        {
            // WHY: one atomic route observation — resolving client/profile/context/mode separately
            // lets a concurrent endpoint switch pair endpoint A's client with endpoint B's metadata.
            LlmRoleRouteSnapshot route = _registry.ResolveRouteForRole(
                request.AgentRoleId, request.RoutingProfileId);
            ILlmClient inner = route.Client;
            request.RoutingProfileId = route.ProfileId;
            request.ContextWindowTokens = route.ContextWindowTokens;
            capturedMode = route.Mode;
            capturedGeneration = route.Generation;
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
                streaming,
                request.ActorId));
            return inner;
        }

        /// <param name="routeHealthCode">
        /// What endpoint health learns (<see cref="ResolveRouteHealthCode"/>); may differ from
        /// <paramref name="errorCode"/>, which is what the caller sees.
        /// </param>
        private void PublishCompleted(
            LlmCompletionRequest request,
            LlmExecutionMode capturedMode,
            long capturedGeneration,
            bool streaming,
            bool success,
            string error,
            LlmErrorCode errorCode,
            LlmErrorCode routeHealthCode)
        {
            if (!success && routeHealthCode != LlmErrorCode.None)
            {
                // WHY: a Ready endpoint whose key expired or whose backend died mid-conversation must
                // surface degraded health on its snapshot; otherwise the UI keeps reporting Ready
                // until restart while every request fails.
                _registry.ReportRouteFailure(
                    request?.RoutingProfileId ?? "", capturedGeneration, routeHealthCode, error);
            }
            else if (success)
            {
                _registry.ReportRouteFailure(
                    request?.RoutingProfileId ?? "", capturedGeneration, LlmErrorCode.None, "");
            }

            _requestCompletedPublisher?.Publish(new LlmRequestCompleted(
                request?.TraceId,
                request?.AgentRoleId,
                request?.RoutingProfileId,
                capturedMode,
                streaming,
                success,
                error,
                errorCode,
                request?.ActorId));
        }

        private void PublishUsage(
            LlmCompletionRequest request,
            LlmExecutionMode capturedMode,
            bool streaming,
            LlmCompletionResult result)
        {
            if (result == null)
            {
                return;
            }

            int? promptTokens = result.PromptTokens;
            int? completionTokens = result.CompletionTokens;
            int? totalTokens = result.TotalTokens;
            // WHY: providers that synthesize an empty usage object report 0/0/0 with HasValue set —
            // that is as useless to the budget UI as a missing object, so require meaningful counts.
            bool hasServerUsage =
                (promptTokens ?? 0) > 0 ||
                (completionTokens ?? 0) > 0 ||
                (totalTokens ?? 0) > 0 ||
                result.CacheReadTokens > 0 ||
                result.CacheWriteTokens > 0;
            if (!hasServerUsage)
            {
                // WHY: many local OpenAI-compatible servers (LM Studio in particular) omit `usage`
                // entirely, so without a fallback the Token Budget page reports all zeros.
                if (!result.Ok ||
                    !TryEstimateUsage(request, result.Content?.Length ?? 0,
                        out promptTokens, out completionTokens, out totalTokens))
                {
                    return;
                }
            }

            _usageReportedPublisher?.Publish(new LlmUsageReported(
                request?.TraceId,
                request?.AgentRoleId,
                request?.RoutingProfileId,
                capturedMode,
                result.Model,
                promptTokens,
                completionTokens,
                totalTokens,
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
            bool success,
            int streamedCompletionChars,
            string lastSeenModel)
        {
            bool chunkHasServerUsage =
                chunk != null &&
                ((chunk.PromptTokens ?? 0) > 0 ||
                 (chunk.CompletionTokens ?? 0) > 0 ||
                 (chunk.TotalTokens ?? 0) > 0 ||
                 chunk.CacheReadTokens > 0 ||
                 chunk.CacheWriteTokens > 0);
            if (chunkHasServerUsage)
            {
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
                return;
            }

            // WHY: streaming backends without `usage` on the final chunk (LM Studio commonly omits it
            // even when include_usage is requested) previously published nothing — Token Budget stayed 0.
            if (!success ||
                !TryEstimateUsage(request, streamedCompletionChars,
                    out int? promptTokens, out int? completionTokens, out int? totalTokens))
            {
                return;
            }

            _usageReportedPublisher?.Publish(new LlmUsageReported(
                request?.TraceId,
                request?.AgentRoleId,
                request?.RoutingProfileId,
                capturedMode,
                lastSeenModel,
                promptTokens,
                completionTokens,
                totalTokens,
                streaming,
                true));
        }

        /// <summary>
        /// Estimates token usage from request/response character counts when the server reported none.
        /// Uses the project-wide ~4 chars/token heuristic (see CalibratingTokenEstimator's Latin weight).
        /// </summary>
        // TODO: LlmUsageReported has no "estimated" flag (Core messaging model is outside this change's
        // scope); add one so budget UIs can badge estimated numbers as approximate.
        private static bool TryEstimateUsage(
            LlmCompletionRequest request,
            int completionChars,
            out int? promptTokens,
            out int? completionTokens,
            out int? totalTokens)
        {
            int promptChars = 0;
            if (request != null)
            {
                promptChars += request.SystemPrompt?.Length ?? 0;
                promptChars += request.UserPayload?.Length ?? 0;
                if (request.ChatHistory != null)
                {
                    foreach (Microsoft.Extensions.AI.ChatMessage message in request.ChatHistory)
                    {
                        promptChars += message?.Text?.Length ?? 0;
                    }
                }
            }

            int prompt = EstimateTokensFromChars(promptChars);
            int completion = EstimateTokensFromChars(completionChars);
            if (prompt <= 0 && completion <= 0)
            {
                promptTokens = null;
                completionTokens = null;
                totalTokens = null;
                return false;
            }

            promptTokens = prompt;
            completionTokens = completion;
            totalTokens = prompt + completion;
            return true;
        }

        private static int EstimateTokensFromChars(int chars)
        {
            const int estimatedCharsPerToken = 4;
            return chars <= 0 ? 0 : Math.Max(1, (chars + estimatedCharsPerToken - 1) / estimatedCharsPerToken);
        }

        /// <summary>
        /// Failures that describe the ENDPOINT rather than one request: a key the provider no longer accepts,
        /// an account that cannot pay, a backend that does not answer. Only these reach route health.
        /// </summary>
        private static bool IsEndpointLevelFailure(LlmErrorCode errorCode)
        {
            return errorCode == LlmErrorCode.AuthExpired ||
                   errorCode == LlmErrorCode.PaymentRequired ||
                   errorCode == LlmErrorCode.BackendUnavailable;
        }

        private static string DescribeInner(ILlmClient inner)
        {
            if (inner == null)
            {
                return "?";
            }

#if COREAI_LLM
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
