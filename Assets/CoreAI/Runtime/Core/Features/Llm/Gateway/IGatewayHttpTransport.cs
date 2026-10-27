#if COREAI_LLM
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace CoreAI.Infrastructure.Llm.Gateway
{
    /// <summary>Narrow, bounded HTTP boundary for optional entitlement and usage services.</summary>
    public interface IGatewayHttpTransport
    {
        Task<GatewayHttpResponse> SendAsync(GatewayHttpRequest request,
            CancellationToken cancellationToken = default);
    }

    /// <summary>One service request; implementations must honor bounds and never forward auth on redirects.</summary>
    public sealed class GatewayHttpRequest
    {
        public string Method { get; set; } = "GET";
        public string Url { get; set; } = "";
        public string JsonBody { get; set; } = "";
        public int TimeoutMilliseconds { get; set; } = 15000;
        public int MaxResponseBytes { get; set; } = 65536;
        public IReadOnlyList<KeyValuePair<string, string>> Headers { get; set; } =
            new List<KeyValuePair<string, string>>();
    }

    /// <summary>Bounded response data; error bodies are never diagnostic log text.</summary>
    public sealed class GatewayHttpResponse
    {
        public int StatusCode { get; set; }
        public string BodyText { get; set; } = "";
        public IReadOnlyDictionary<string, IEnumerable<string>> Headers { get; set; } =
            new Dictionary<string, IEnumerable<string>>();
        public bool IsSuccess => StatusCode >= 200 && StatusCode < 300;
    }
}
#endif
