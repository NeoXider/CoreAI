using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace CoreAI.Ai
{
    /// <summary>Portable endpoint implementation kind.</summary>
    public enum LlmEndpointKind
    {
        /// <summary>OpenAI-compatible HTTP endpoint.</summary>
        HttpOpenAi = 0,

        /// <summary>llama.cpp endpoint owned through LLMUnity.</summary>
        LlmUnity = 1,

        /// <summary>Offline or deterministic endpoint.</summary>
        Offline = 2
    }

    /// <summary>Lifecycle state shared by HTTP and local endpoints.</summary>
    public enum LlmEndpointLifecycleState
    {
        Inactive = 0,
        StartingNative = 1,
        WaitingForHttp = 2,
        Ready = 3,
        Draining = 4,
        Stopping = 5,
        Failed = 6,
        Removed = 7
    }

    /// <summary>How removal handles requests already using an endpoint generation.</summary>
    public enum LlmEndpointRemovalMode
    {
        /// <summary>Stops new routing immediately while already-resolved calls retain their client generation.</summary>
        Drain = 0,

        /// <summary>
        /// Requests cancellation of tracked in-flight calls. Registries that cannot prove cancellation
        /// must reject removal instead of reporting a false success.
        /// </summary>
        CancelInFlight = 1
    }

    /// <summary>
    /// Portable endpoint configuration. Credentials are supplied separately at registration time;
    /// <see cref="SecretReference"/> is only a host-owned lookup key.
    /// </summary>
    public sealed class LlmEndpointDescriptor
    {
        public string EndpointId { get; set; } = "";
        public string DisplayName { get; set; } = "";
        public LlmEndpointKind Kind { get; set; }
        public string BaseUrl { get; set; } = "";
        public string Model { get; set; } = "";
        public string SecretReference { get; set; } = "";
        public bool Active { get; set; } = true;
        public bool KeepWarm { get; set; }
        public int ContextWindowTokens { get; set; } = CoreAISettings.DefaultContextWindowTokens;
        public string LocalModelPath { get; set; } = "";
        public string UnityAgentName { get; set; } = "";
        public int Port { get; set; }
        public int GpuLayers { get; set; }
        public bool Remote { get; set; }
        public bool FlashAttention { get; set; }
        public int ParallelSlots { get; set; } = 1;

        /// <summary>Returns portable validation errors without contacting the endpoint.</summary>
        public IReadOnlyList<string> Validate()
        {
            List<string> errors = new();
            if (string.IsNullOrWhiteSpace(EndpointId))
            {
                errors.Add("Endpoint id is empty.");
            }

            if (ContextWindowTokens < 256)
            {
                errors.Add("Context window must be at least 256 tokens.");
            }

            if (Kind == LlmEndpointKind.HttpOpenAi &&
                !Uri.TryCreate(BaseUrl?.Trim(), UriKind.Absolute, out Uri uri))
            {
                errors.Add("HTTP endpoint base URL must be absolute.");
            }

            if (Kind == LlmEndpointKind.HttpOpenAi &&
                Uri.TryCreate(BaseUrl?.Trim(), UriKind.Absolute, out Uri httpUri) &&
                httpUri.Scheme != Uri.UriSchemeHttp && httpUri.Scheme != Uri.UriSchemeHttps)
            {
                errors.Add("HTTP endpoint base URL must use http or https.");
            }

            if (Kind == LlmEndpointKind.LlmUnity && Port is < 0 or > 65535)
            {
                errors.Add("LLMUnity port must be between 0 and 65535.");
            }

            if (ParallelSlots < 1)
            {
                errors.Add("Parallel slots must be positive.");
            }

            return errors;
        }
    }

    /// <summary>Immutable observation of one endpoint generation.</summary>
    public sealed class LlmEndpointSnapshot
    {
        public LlmEndpointDescriptor Descriptor { get; set; }
        public LlmEndpointLifecycleState State { get; set; }
        public long Generation { get; set; }
        public string Error { get; set; } = "";
        public int InFlightRequests { get; set; }
    }

    /// <summary>Runtime profile that points routing policy at an endpoint.</summary>
    public sealed class LlmRuntimeProfile
    {
        public string ProfileId { get; set; } = "";
        public string DisplayName { get; set; } = "";
        public string EndpointId { get; set; } = "";
        public IReadOnlyList<string> FallbackProfileIds { get; set; } = Array.Empty<string>();

        /// <summary>Returns validation errors that do not require registry state.</summary>
        public IReadOnlyList<string> Validate()
        {
            List<string> errors = new();
            if (string.IsNullOrWhiteSpace(ProfileId))
            {
                errors.Add("Profile id is empty.");
            }

            if (string.IsNullOrWhiteSpace(EndpointId))
            {
                errors.Add("Profile endpoint id is empty.");
            }

            HashSet<string> seen = new(StringComparer.Ordinal);
            foreach (string fallback in FallbackProfileIds ?? Array.Empty<string>())
            {
                string id = fallback?.Trim();
                if (string.IsNullOrWhiteSpace(id))
                {
                    errors.Add("Fallback profile id is empty.");
                }
                else if (!seen.Add(id))
                {
                    errors.Add($"Duplicate fallback profile id: {id}.");
                }
                else if (string.Equals(id, ProfileId?.Trim(), StringComparison.Ordinal))
                {
                    errors.Add("Profile cannot fall back to itself.");
                }
            }

            return errors;
        }
    }

    /// <summary>
    /// Runtime endpoint and profile registry. Hosts own concrete clients, readiness probes, persistence,
    /// and secret storage while callers use this portable surface in players and WebGL.
    /// </summary>
    public interface ILlmEndpointRegistry
    {
        event Action Changed;

        IReadOnlyList<LlmEndpointSnapshot> GetEndpoints();
        IReadOnlyList<LlmRuntimeProfile> GetProfiles();

        Task<LlmEndpointSnapshot> AddOrUpdateEndpointAsync(
            LlmEndpointDescriptor descriptor,
            string sessionApiKey = null,
            CancellationToken cancellationToken = default);

        Task<LlmEndpointSnapshot> SetEndpointActiveAsync(
            string endpointId,
            bool active,
            bool keepWarm = false,
            CancellationToken cancellationToken = default);

        Task<bool> RemoveEndpointAsync(
            string endpointId,
            LlmEndpointRemovalMode mode = LlmEndpointRemovalMode.Drain,
            string replacementEndpointId = null,
            CancellationToken cancellationToken = default);

        void AddOrUpdateProfile(LlmRuntimeProfile profile);
        bool RemoveProfile(string profileId, string replacementProfileId = null);
        void AssignRoleProfile(string rolePattern, string profileId, int sortOrder = 0);
        bool ClearRoleProfile(string rolePattern);
        string GetRoleProfile(string roleId);
    }

    /// <summary>Host-owned resolver for persisted credential references.</summary>
    public interface ILlmEndpointSecretProvider
    {
        /// <summary>Resolves a credential without exposing it through persisted endpoint state.</summary>
        bool TryResolve(string secretReference, out string secret);
    }
}
