#if COREAI_LLM
using System;
using System.Collections.Generic;
using System.Linq;
using CoreAI.Ai;
using MEAI = Microsoft.Extensions.AI;

namespace CoreAI.Infrastructure.Llm.Gateway
{
    /// <summary>Explicit host request and route metadata for one raw model invocation.</summary>
    public sealed class GatewayRequestContext
    {
        public GatewayRequestContext(LlmCompletionRequest request, LlmRouteProfile profile)
        {
            Request = request ?? throw new ArgumentNullException(nameof(request));
            Profile = profile ?? throw new ArgumentNullException(nameof(profile));
        }

        public LlmCompletionRequest Request { get; }
        public LlmRouteProfile Profile { get; }

        /// <summary>Creates metadata from the active correlation scope and the actual messages.</summary>
        public static GatewayRequestContext FromMessages(IReadOnlyList<MEAI.ChatMessage> messages,
            MEAI.ChatOptions options)
        {
            LlmRequestContextFrame current = LlmRequestContext.Current;
            LlmCompletionRequest request = new LlmCompletionRequest
            {
                AgentRoleId = current?.AgentRoleId ?? "",
                ActorId = current?.ActorId ?? "",
                TraceId = current?.TraceId ?? "",
                IdempotencyKey = current?.IdempotencyKey ?? "",
                ChatHistory = new List<MEAI.ChatMessage>(messages)
            };
            if (options?.AdditionalProperties?.TryGetValue(LlmProviderResponseState.MetadataKey,
                    out object observation) == true && observation is LlmProviderResponseState responseState)
                request.ProviderResponseState = responseState;
            return new GatewayRequestContext(request, new LlmRouteProfile
            {
                Mode = LlmExecutionMode.ServerManagedApi,
                Model = ""
            });
        }

        /// <summary>Copies mutable host context before binding invocation-owned metadata.</summary>
        internal GatewayRequestContext Snapshot(IReadOnlyList<MEAI.ChatMessage> messages)
        {
            LlmRequestContextFrame ambient = LlmRequestContext.Current;
            LlmCompletionRequest request = new LlmCompletionRequest
            {
                ProviderResponseState = Request.ProviderResponseState,
                ActorId = Request.ActorId,
                AgentRoleId = Request.AgentRoleId,
                SystemPrompt = Request.SystemPrompt,
                UserPayload = Request.UserPayload,
                ChatHistory = new List<MEAI.ChatMessage>(messages),
                Attachments = Request.Attachments,
                TraceId = string.IsNullOrWhiteSpace(Request.TraceId) ? ambient?.TraceId ?? "" : Request.TraceId,
                IdempotencyKey = string.IsNullOrWhiteSpace(Request.IdempotencyKey)
                    ? ambient?.IdempotencyKey ?? "" : Request.IdempotencyKey,
                RoutingProfileId = string.IsNullOrWhiteSpace(Request.RoutingProfileId)
                    ? Profile.ProfileId : Request.RoutingProfileId,
                ContextWindowTokens = Request.ContextWindowTokens,
                MaxOutputTokens = Request.MaxOutputTokens,
                MaxToolCallRoundtrips = Request.MaxToolCallRoundtrips,
                Temperature = Request.Temperature,
                SendTemperature = Request.SendTemperature,
                TemperatureOverride = Request.TemperatureOverride,
                TemperatureIsFallback = Request.TemperatureIsFallback,
                Reasoning = Request.Reasoning,
                Sampling = Request.Sampling,
                EffectiveReasoningEffort = Request.EffectiveReasoningEffort,
                EffectiveReasoningDialect = Request.EffectiveReasoningDialect,
                EffectiveTemperature = Request.EffectiveTemperature,
                EffectiveMaxOutputTokens = Request.EffectiveMaxOutputTokens,
                Tools = Request.Tools,
                AllowedToolNames = Request.AllowedToolNames,
                AllowDuplicateToolCalls = Request.AllowDuplicateToolCalls,
                ForcedToolMode = Request.ForcedToolMode,
                RequiredToolName = Request.RequiredToolName,
                AllowTextShapedToolCallsOnNativeEndpoint = Request.AllowTextShapedToolCallsOnNativeEndpoint
            };
            return new GatewayRequestContext(request, new LlmRouteProfile
            {
                ProfileId = Profile.ProfileId,
                Mode = LlmExecutionMode.ServerManagedApi,
                Model = Profile.Model,
                ContextWindowTokens = Profile.ContextWindowTokens,
                MaxOutputTokens = Profile.MaxOutputTokens,
                Capabilities = Profile.Capabilities?.ToArray() ?? Array.Empty<string>()
            });
        }
    }
}
#endif
