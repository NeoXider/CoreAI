#if COREAI_LLM
using System;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Ai;

namespace CoreAI.Infrastructure.Llm.Gateway
{
    /// <summary>Maps measured usage to the observed optional v1 analytics contract.</summary>
    public sealed class GatewayUsageSink : ILlmUsageSink
    {
        private readonly IGatewayServiceClient _service;
        public GatewayUsageSink(IGatewayServiceClient service) =>
            _service = service ?? throw new ArgumentNullException(nameof(service));

        public Task RecordAsync(LlmUsageRecord record, CancellationToken cancellationToken = default)
        {
            if (record == null) throw new ArgumentNullException(nameof(record));
            return _service.ReportUsageAsync(new GatewayUsagePayload
            {
                TraceId = record.TraceId, RoleId = record.RoleId, RoutingProfileId = record.RoutingProfileId,
                Model = record.Model, PromptTokens = record.PromptTokens,
                CompletionTokens = record.CompletionTokens, TotalTokens = record.TotalTokens,
                Streaming = record.Streaming, Success = record.Success, Estimated = false
            }, cancellationToken);
        }
    }
}
#endif
