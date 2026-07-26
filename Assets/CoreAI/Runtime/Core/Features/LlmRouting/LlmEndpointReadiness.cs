using System;
using System.Threading;
using System.Threading.Tasks;

namespace CoreAI.Ai
{
    /// <summary>OpenAI-compatible route used to prove that an endpoint handler is reachable.</summary>
    public enum LlmEndpointReadinessMode
    {
        /// <summary>Checks <c>/models</c>, then falls back to <c>/chat/completions</c> when unsupported.</summary>
        ModelsThenCompletions = 0,

        /// <summary>Checks the completions handler directly, as required by embedded llama.cpp hosts.</summary>
        CompletionsOnly = 1
    }

    /// <summary>Portable input for an OpenAI-compatible endpoint readiness probe.</summary>
    public sealed class LlmEndpointReadinessRequest
    {
        public string BaseUrl { get; set; } = "";
        public string ApiKey { get; set; } = "";
        public int TimeoutSeconds { get; set; } = 5;
        public LlmEndpointReadinessMode Mode { get; set; }
    }

    /// <summary>Portable readiness outcome without host-specific HTTP types.</summary>
    public sealed class LlmEndpointReadinessResult
    {
        public bool IsReady { get; set; }
        public int StatusCode { get; set; }
        public string Error { get; set; } = "";
    }

    /// <summary>Host-supplied probe for OpenAI-compatible endpoint readiness.</summary>
    public interface ILlmEndpointReadinessProbe
    {
        Task<LlmEndpointReadinessResult> ProbeAsync(
            LlmEndpointReadinessRequest request,
            CancellationToken cancellationToken = default);
    }

    /// <summary>Shared status policy used by .NET and Unity HTTP adapters.</summary>
    public static class LlmEndpointReadinessPolicy
    {
        public static bool IsHandlerReached(long status)
        {
            return status is >= 200 and < 500 &&
                   status is not (>= 300 and < 400) &&
                   status is not 401 and not 403 and not 404;
        }

        public static bool ShouldTryCompletions(long modelsStatus)
        {
            return modelsStatus is 404 or 405;
        }
    }
}
