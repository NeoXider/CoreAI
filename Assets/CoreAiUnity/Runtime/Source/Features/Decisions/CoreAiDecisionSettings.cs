using System.Collections.Generic;
using CoreAI.Ai;
#if COREAI_LLM
using CoreAI.Infrastructure.Llm;
#endif

namespace CoreAI.Decisions
{
    /// <summary>
    /// Configuration of the decision block for one lifetime scope (<see cref="CoreAiDecisionsInstaller"/>). Plain
    /// data: the route table, decision-only endpoints and the optional overrides. Without routes every consumer is
    /// refused with <see cref="DecisionErrorReasons.NoRoute"/> and its policy's fallback decides.
    /// </summary>
    public sealed class CoreAiDecisionSettings
    {
        /// <summary>Endpoint id used by <see cref="LocalLlamaServer"/>.</summary>
        public const string LocalEndpointId = "local-decisions";

        /// <summary>Default base URL of a local <c>llama-server</c> started with <c>--port 8080</c>.</summary>
        public const string DefaultLocalBaseUrl = "http://127.0.0.1:8080/v1";

        /// <summary>Consumer routes (for example <c>npc-brain</c> and <c>autopilot</c>) with their budgets.</summary>
        public DecisionRouteTable Routes { get; set; } = new();

        /// <summary>
        /// Decision-only endpoints such as a <c>llama-server</c> sidecar. Kept out of the chat registry on purpose:
        /// there they would be activated and probed as chat endpoints. They win over registry entries with the same id.
        /// </summary>
        public List<LlmEndpointDescriptor> Endpoints { get; set; } = new();

        /// <summary>Also route to the runtime chat registry's endpoints that declare a decision protocol.</summary>
        public bool IncludeRegistryEndpoints { get; set; } = true;

        /// <summary>Resolves endpoint secret references; null uses a registered provider, else environment variables.</summary>
        public ILlmEndpointSecretProvider SecretProviderOverride { get; set; }

#if COREAI_LLM
        /// <summary>The HTTP transport for decision calls; null picks the platform transport (UnityWebRequest on WebGL).</summary>
        public IOpenAiHttpTransport TransportOverride { get; set; }

        /// <summary>Options of the default backends (OpenRouter route, logprob forcing, System One dialect).</summary>
        public DecisionClientFactoryOptions FactoryOptions { get; set; } = new();
#endif

        /// <summary>
        /// The documented local setup: one System One endpoint at <paramref name="baseUrl"/> (a <c>llama-server</c>
        /// serving Liquid <c>d1-2B</c>) and every consumer routed to it with the default local budget.
        /// </summary>
        public static CoreAiDecisionSettings LocalLlamaServer(string baseUrl = DefaultLocalBaseUrl, string model = "d1-2B")
        {
            CoreAiDecisionSettings settings = new();
            settings.Endpoints.Add(new LlmEndpointDescriptor
            {
                EndpointId = LocalEndpointId,
                DisplayName = "Local decisions (llama-server)",
                Kind = LlmEndpointKind.HttpOpenAi,
                BaseUrl = baseUrl ?? DefaultLocalBaseUrl,
                Model = model ?? "",
                DecisionProtocol = LlmDecisionProtocol.SystemOne,
                RequestTimeoutSeconds = 10,
                ResponseWaitTimeoutSeconds = 10
            });
            settings.Routes = DecisionRouteTable.AllTo(LocalEndpointId);
            return settings;
        }
    }
}
