using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Ai;
using CoreAI.Infrastructure.Logging;
#if COREAI_HAS_LLMUNITY && !UNITY_WEBGL
using LLMUnity;
#endif
using UnityEngine;
using IAgentMemoryStore = CoreAI.Ai.IAgentMemoryStore;

namespace CoreAI.Infrastructure.Llm
{
    /// <summary>
    /// Registry of LLM clients used for role and profile routing.
    /// </summary>
    public sealed class LlmClientRegistry : ILlmClientRegistry, ILlmRoutingController, ILlmEndpointRegistry, IDisposable
    {
        private readonly IGameLogger _logger;
        private readonly IAgentMemoryStore _memoryStore;
        private readonly ICoreAISettings _settings;
        private readonly ILlmEndpointRegistryStore _persistenceStore;
        private readonly ILlmEndpointClientFactory _endpointClientFactory;
        private readonly ILlmEndpointSecretProvider _secretProvider;
        private readonly object _gate = new();
        private readonly object _persistenceGate = new();
        private ILlmClient _legacyFallback = new StubLlmClient();
        private LlmExecutionMode _legacyFallbackMode = LlmExecutionMode.Auto;
        private Dictionary<string, ILlmClient> _byProfileId = new(StringComparer.Ordinal);
        private Dictionary<string, int> _contextByProfileId = new(StringComparer.Ordinal);
        private Dictionary<string, LlmExecutionMode> _modeByProfileId = new(StringComparer.Ordinal);
        private ILlmRouteResolver _routeResolver = new LlmRouteResolver(new LlmRouteTable());
        private bool _useManifestRouting;
        private readonly Dictionary<string, RuntimeEndpoint> _runtimeEndpoints = new(StringComparer.Ordinal);
        private readonly Dictionary<string, RuntimeEndpoint> _pendingEndpoints = new(StringComparer.Ordinal);
        private readonly Dictionary<string, LlmRuntimeProfile> _runtimeProfiles = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _runtimeRoleProfiles = new(StringComparer.Ordinal);
        private long _generation;
        private long _persistenceRevision;
        private long _persistedRevision;
        private bool _disposed;

        private sealed class RuntimeEndpoint
        {
            public LlmEndpointDescriptor Descriptor;
            public ILlmClient Client;
            public LlmEndpointLifecycleState State;
            public long Generation;
            public string Error = "";
            public string SessionApiKey = "";
            public Task<LlmEndpointSnapshot> ActivationTask;
            public CancellationTokenSource ActivationCancellation;
            public int InFlightRequests;
            public LlmExecutionMode Mode;
            public Func<Task> ReleaseOwnedHostAsync;
            public int ReleaseRequested;
            public Task HostReleaseTask;
        }

        private sealed class RoutingUnavailableClient : ILlmClient
        {
            private readonly string _profileId;

            public RoutingUnavailableClient(string profileId)
            {
                _profileId = profileId ?? "";
            }

            public Task<LlmCompletionResult> CompleteAsync(
                LlmCompletionRequest request,
                CancellationToken cancellationToken = default)
            {
                return Task.FromResult(new LlmCompletionResult
                {
                    Ok = false,
                    Error = $"LLM routing profile '{_profileId}' is unavailable.",
                    ErrorCode = LlmErrorCode.RoutingError
                });
            }
        }

        private sealed class EnvironmentSecretProvider : ILlmEndpointSecretProvider
        {
            public bool TryResolve(string secretReference, out string secret)
            {
                secret = string.IsNullOrWhiteSpace(secretReference)
                    ? ""
                    : Environment.GetEnvironmentVariable(secretReference.Trim()) ?? "";
                return !string.IsNullOrEmpty(secret);
            }
        }

        private sealed class ActivatingEndpointClient : ILlmClient
        {
            private readonly LlmClientRegistry _registry;
            private readonly string _roleId;
            private readonly string _profileId;
            private readonly Task<LlmEndpointSnapshot> _activation;
            private readonly bool _supportsNativeTools;

            public ActivatingEndpointClient(
                LlmClientRegistry registry,
                string roleId,
                string profileId,
                RuntimeEndpoint endpoint)
            {
                _registry = registry;
                _roleId = roleId;
                _profileId = profileId;
                _activation = endpoint.ActivationTask;
                _supportsNativeTools = endpoint.Descriptor.Kind != LlmEndpointKind.Offline;
            }

            public bool SupportsNativeToolCallingForRole(string agentRoleId) => _supportsNativeTools;

            public async Task<LlmCompletionResult> CompleteAsync(
                LlmCompletionRequest request,
                CancellationToken cancellationToken = default)
            {
                await AwaitWithoutCancellingSharedActivation(_activation, cancellationToken);
                ILlmClient ready = _registry.ResolveClientForRole(_roleId, _profileId);
                return await ready.CompleteAsync(request, cancellationToken);
            }

            public async IAsyncEnumerable<LlmStreamChunk> CompleteStreamingAsync(
                LlmCompletionRequest request,
                [System.Runtime.CompilerServices.EnumeratorCancellation]
                CancellationToken cancellationToken = default)
            {
                await AwaitWithoutCancellingSharedActivation(_activation, cancellationToken);
                ILlmClient ready = _registry.ResolveClientForRole(_roleId, _profileId);
                await foreach (LlmStreamChunk chunk in ready.CompleteStreamingAsync(request, cancellationToken))
                {
                    yield return chunk;
                }
            }

            private static async Task AwaitWithoutCancellingSharedActivation(
                Task activation,
                CancellationToken cancellationToken)
            {
                while (!activation.IsCompleted)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    await Task.Yield();
                }

                await activation;
                cancellationToken.ThrowIfCancellationRequested();
            }
        }

        private sealed class TrackedEndpointClient : ILlmClient
        {
            private readonly RuntimeEndpoint _endpoint;

            public TrackedEndpointClient(RuntimeEndpoint endpoint)
            {
                _endpoint = endpoint;
            }

            public bool SupportsNativeToolCallingForRole(string agentRoleId)
            {
                return _endpoint.Client.SupportsNativeToolCallingForRole(agentRoleId);
            }

            public async Task<LlmCompletionResult> CompleteAsync(
                LlmCompletionRequest request,
                CancellationToken cancellationToken = default)
            {
                Interlocked.Increment(ref _endpoint.InFlightRequests);
                try
                {
                    return await _endpoint.Client.CompleteAsync(request, cancellationToken);
                }
                finally
                {
                    Interlocked.Decrement(ref _endpoint.InFlightRequests);
                }
            }

            public async IAsyncEnumerable<LlmStreamChunk> CompleteStreamingAsync(
                LlmCompletionRequest request,
                [System.Runtime.CompilerServices.EnumeratorCancellation]
                CancellationToken cancellationToken = default)
            {
                Interlocked.Increment(ref _endpoint.InFlightRequests);
                try
                {
                    await foreach (LlmStreamChunk chunk in
                                   _endpoint.Client.CompleteStreamingAsync(request, cancellationToken))
                    {
                        yield return chunk;
                    }
                }
                finally
                {
                    Interlocked.Decrement(ref _endpoint.InFlightRequests);
                }
            }
        }

        /// <inheritdoc />
        public event Action Changed;

        /// <summary>Cancels endpoint activations owned by this registry scope.</summary>
        public void Dispose()
        {
            RuntimeEndpoint[] endpoints;
            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                endpoints = _runtimeEndpoints.Values.Concat(_pendingEndpoints.Values).Distinct().ToArray();
                foreach (RuntimeEndpoint endpoint in endpoints)
                {
                    endpoint.ActivationCancellation?.Cancel();
                    endpoint.ActivationCancellation?.Dispose();
                    endpoint.ActivationCancellation = null;
                }

                _pendingEndpoints.Clear();
            }

            foreach (RuntimeEndpoint endpoint in endpoints)
            {
                RequestOwnedHostRelease(endpoint);
            }

            Changed = null;
        }

        /// <param name="logger">The logger value.</param>
        public LlmClientRegistry(
            IGameLogger logger,
            ICoreAISettings settings,
            IAgentMemoryStore memoryStore = null,
            ILlmEndpointRegistryStore persistenceStore = null,
            ILlmEndpointClientFactory endpointClientFactory = null,
            ILlmEndpointSecretProvider secretProvider = null)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _memoryStore = memoryStore;
            _persistenceStore = persistenceStore;
            _endpointClientFactory = endpointClientFactory ?? new LlmEndpointClientFactory(settings, logger, memoryStore);
            _secretProvider = secretProvider ?? new EnvironmentSecretProvider();
            RestoreRuntimeState();
        }

        /// <summary>Legacy LLM client used when no route-specific client is available.</summary>
        public void SetLegacyFallback(ILlmClient legacy)
        {
            lock (_gate)
            {
                _legacyFallback = legacy ?? new StubLlmClient();
                _legacyFallbackMode = _settings is CoreAISettingsAsset unitySettings
                    ? unitySettings.ExecutionMode
                    : LlmExecutionMode.Auto;
            }
        }

        /// <inheritdoc />
        public void ApplyManifest(LlmRoutingManifest manifest)
        {
            ApplyRouteTable(
                manifest != null ? manifest.ToRouteTable() : null,
                manifest != null ? manifest.Profiles : null,
                manifest != null && manifest.EnableRoleRouting);
        }

        /// <summary>
        /// Applies a portable route table snapshot while using Unity profile entries only to build backend adapters.
        /// </summary>
        public void ApplyRouteTable(
            LlmRouteTable routeTable,
            IReadOnlyList<LlmBackendProfileEntry> profiles,
            bool enableRouting = true)
        {
            lock (_gate)
            {
                if (!enableRouting || routeTable == null)
                {
                    _useManifestRouting = false;
                    _byProfileId.Clear();
                    _contextByProfileId.Clear();
                    _modeByProfileId.Clear();
                    _routeResolver = new LlmRouteResolver(new LlmRouteTable());
                    return;
                }

                _useManifestRouting = true;
                IReadOnlyList<string> validationErrors = routeTable.Validate();
                foreach (string error in validationErrors)
                {
                    _logger.LogWarning(GameLogFeature.Llm, "LlmRoutingManifest: " + error);
                }

                _routeResolver = new LlmRouteResolver(routeTable);
                Dictionary<string, ILlmClient> newClients = new(StringComparer.Ordinal);
                Dictionary<string, int> newContexts = new(StringComparer.Ordinal);
                Dictionary<string, LlmExecutionMode> newModes = new(StringComparer.Ordinal);
                foreach (LlmBackendProfileEntry p in profiles ?? Array.Empty<LlmBackendProfileEntry>())
                {
                    if (string.IsNullOrWhiteSpace(p?.profileId))
                    {
                        continue;
                    }

                    string id = p.profileId.Trim();
                    if (newClients.ContainsKey(id))
                    {
                        continue;
                    }

                    ILlmClient c = BuildProfileClient(p);
                    if (c != null)
                    {
                        newClients[id] = c;
                        newContexts[id] = p.contextWindowTokens < 256
                            ? CoreAISettings.DefaultContextWindowTokens
                            : p.contextWindowTokens;
                        newModes[id] = ResolveProfileMode(p);
                    }
                }

                _byProfileId = newClients;
                _contextByProfileId = newContexts;
                _modeByProfileId = newModes;
            }
        }

        /// <inheritdoc />
        public ILlmClient ResolveClientForRole(string roleId)
        {
            return ResolveClientForRole(roleId, "");
        }

        /// <inheritdoc />
        public ILlmClient ResolveClientForRole(string roleId, string explicitProfileId)
        {
            string role = string.IsNullOrWhiteSpace(roleId) ? BuiltInAgentRoleIds.Creator : roleId.Trim();
            lock (_gate)
            {
                string runtimeProfile = ResolveRuntimeProfileIdLocked(role, explicitProfileId);
                if (!string.IsNullOrEmpty(runtimeProfile) &&
                    TryResolveReadyRuntimeEndpointLocked(runtimeProfile, out RuntimeEndpoint endpoint, out _))
                {
                    return new TrackedEndpointClient(endpoint);
                }

                if (!string.IsNullOrEmpty(runtimeProfile) &&
                    TryResolveActivatingRuntimeEndpointLocked(runtimeProfile, out RuntimeEndpoint activating))
                {
                    return new ActivatingEndpointClient(this, role, runtimeProfile, activating);
                }

                if (!string.IsNullOrEmpty(runtimeProfile) &&
                    _byProfileId.TryGetValue(runtimeProfile, out ILlmClient explicitManifestClient))
                {
                    return explicitManifestClient;
                }

                if (!string.IsNullOrEmpty(runtimeProfile))
                {
                    return new RoutingUnavailableClient(runtimeProfile);
                }

                if (!_useManifestRouting || _byProfileId.Count == 0)
                {
                    return _legacyFallback;
                }

                LlmRouteResolution resolution = _routeResolver.Resolve(role);
                if (resolution.Found &&
                    _byProfileId.TryGetValue(resolution.Profile.ProfileId, out ILlmClient client))
                {
                    return client;
                }

                return _legacyFallback;
            }
        }

        public int ResolveContextWindowForRole(string roleId)
        {
            return ResolveContextWindowForRole(roleId, "");
        }

        /// <inheritdoc />
        public int ResolveContextWindowForRole(string roleId, string explicitProfileId)
        {
            string role = string.IsNullOrWhiteSpace(roleId) ? BuiltInAgentRoleIds.Creator : roleId.Trim();
            lock (_gate)
            {
                string runtimeProfile = ResolveRuntimeProfileIdLocked(role, explicitProfileId);
                if (!string.IsNullOrEmpty(runtimeProfile) &&
                    TryResolveReadyRuntimeEndpointLocked(runtimeProfile, out RuntimeEndpoint effectiveEndpoint, out _))
                {
                    return effectiveEndpoint.Descriptor.ContextWindowTokens;
                }

                if (!string.IsNullOrEmpty(runtimeProfile) &&
                    _runtimeProfiles.TryGetValue(runtimeProfile, out LlmRuntimeProfile profile) &&
                    _runtimeEndpoints.TryGetValue(profile.EndpointId, out RuntimeEndpoint endpoint))
                {
                    return endpoint.Descriptor.ContextWindowTokens;
                }

                if (!string.IsNullOrEmpty(runtimeProfile) &&
                    _contextByProfileId.TryGetValue(runtimeProfile, out int explicitContext))
                {
                    return explicitContext < 256 ? CoreAISettings.DefaultContextWindowTokens : explicitContext;
                }

                if (!_useManifestRouting || _contextByProfileId.Count == 0)
                {
                    return CoreAISettings.DefaultContextWindowTokens;
                }

                LlmRouteResolution resolution = _routeResolver.Resolve(role);
                if (resolution.Found &&
                    _contextByProfileId.TryGetValue(resolution.Profile.ProfileId, out int ctx))
                {
                    return ctx < 256 ? CoreAISettings.DefaultContextWindowTokens : ctx;
                }

                return CoreAISettings.DefaultContextWindowTokens;
            }
        }

        public LlmExecutionMode ResolveExecutionModeForRole(string roleId)
        {
            return ResolveExecutionModeForRole(roleId, "");
        }

        /// <inheritdoc />
        public LlmExecutionMode ResolveExecutionModeForRole(string roleId, string explicitProfileId)
        {
            string profileId = ResolveProfileIdForRole(roleId, explicitProfileId);
            lock (_gate)
            {
                if (!string.IsNullOrEmpty(profileId) &&
                    TryResolveReadyRuntimeEndpointLocked(profileId, out RuntimeEndpoint effectiveEndpoint, out _))
                {
                    return effectiveEndpoint.Mode;
                }

                if (_runtimeProfiles.TryGetValue(profileId, out LlmRuntimeProfile profile) &&
                    _runtimeEndpoints.TryGetValue(profile.EndpointId, out RuntimeEndpoint endpoint))
                {
                    return endpoint.State == LlmEndpointLifecycleState.Ready
                        ? endpoint.Mode
                        : EndpointMode(endpoint.Descriptor.Kind);
                }

                return !string.IsNullOrEmpty(profileId) &&
                       _modeByProfileId.TryGetValue(profileId, out LlmExecutionMode mode)
                    ? mode
                    : _legacyFallbackMode;
            }
        }

        public string ResolveProfileIdForRole(string roleId)
        {
            return ResolveProfileIdForRole(roleId, "");
        }

        /// <inheritdoc />
        public string ResolveProfileIdForRole(string roleId, string explicitProfileId)
        {
            string role = string.IsNullOrWhiteSpace(roleId) ? BuiltInAgentRoleIds.Creator : roleId.Trim();
            lock (_gate)
            {
                string runtimeProfile = ResolveRuntimeProfileIdLocked(role, explicitProfileId);
                if (!string.IsNullOrEmpty(runtimeProfile))
                {
                    if (TryResolveReadyRuntimeEndpointLocked(runtimeProfile, out _, out string effectiveProfileId))
                    {
                        return effectiveProfileId;
                    }

                    return runtimeProfile;
                }

                if (!_useManifestRouting || _byProfileId.Count == 0)
                {
                    return "fallback";
                }

                LlmRouteResolution resolution = _routeResolver.Resolve(role);
                if (resolution.Found && _byProfileId.ContainsKey(resolution.Profile.ProfileId))
                {
                    return resolution.Profile.ProfileId;
                }

                return "fallback";
            }
        }

        /// <inheritdoc />
        public IReadOnlyList<LlmEndpointSnapshot> GetEndpoints()
        {
            lock (_gate)
            {
                return _pendingEndpoints.Values
                    .Concat(_runtimeEndpoints.Where(pair => !_pendingEndpoints.ContainsKey(pair.Key))
                        .Select(pair => pair.Value))
                    .Select(ToSnapshot)
                    .ToArray();
            }
        }

        /// <inheritdoc />
        public IReadOnlyList<LlmRuntimeProfile> GetProfiles()
        {
            lock (_gate)
            {
                return _runtimeProfiles.Values.Select(FileLlmEndpointRegistryStore.CloneProfile).ToArray();
            }
        }

        /// <inheritdoc />
        public Task<LlmEndpointSnapshot> AddOrUpdateEndpointAsync(
            LlmEndpointDescriptor descriptor,
            string sessionApiKey = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (descriptor == null || descriptor.Validate().Count > 0)
            {
                throw new ArgumentException(descriptor == null
                    ? "Endpoint descriptor is null."
                    : string.Join(" ", descriptor.Validate()));
            }

            string id = descriptor.EndpointId.Trim();
            LlmEndpointDescriptor copy = FileLlmEndpointRegistryStore.CloneDescriptor(descriptor);
            RuntimeEndpoint runtime;
            lock (_gate)
            {
                string effectiveSessionApiKey = sessionApiKey;
                if (effectiveSessionApiKey == null)
                {
                    RuntimeEndpoint credentialSource = null;
                    if (_pendingEndpoints.TryGetValue(id, out RuntimeEndpoint existingPending))
                    {
                        credentialSource = existingPending;
                    }
                    else if (_runtimeEndpoints.TryGetValue(id, out RuntimeEndpoint existingRuntime))
                    {
                        credentialSource = existingRuntime;
                    }

                    effectiveSessionApiKey = credentialSource != null &&
                                             string.Equals(
                                                 credentialSource.Descriptor.SecretReference?.Trim() ?? "",
                                                 copy.SecretReference?.Trim() ?? "",
                                                 StringComparison.Ordinal)
                        ? credentialSource.SessionApiKey
                        : "";
                }

                if (_pendingEndpoints.TryGetValue(id, out RuntimeEndpoint pending) &&
                    pending.ActivationTask != null && !pending.ActivationTask.IsCompleted &&
                    DescriptorFingerprint(pending.Descriptor) == DescriptorFingerprint(copy) &&
                    string.Equals(pending.SessionApiKey, effectiveSessionApiKey ?? "", StringComparison.Ordinal))
                {
                    return AwaitActivationForCallerAsync(pending.ActivationTask, cancellationToken);
                }

                if (_runtimeEndpoints.TryGetValue(id, out RuntimeEndpoint currentRuntime) &&
                    currentRuntime.ActivationTask != null && !currentRuntime.ActivationTask.IsCompleted &&
                    DescriptorFingerprint(currentRuntime.Descriptor) == DescriptorFingerprint(copy) &&
                    string.Equals(currentRuntime.SessionApiKey, effectiveSessionApiKey ?? "", StringComparison.Ordinal))
                {
                    return AwaitActivationForCallerAsync(currentRuntime.ActivationTask, cancellationToken);
                }

                if (_pendingEndpoints.TryGetValue(id, out pending))
                {
                    pending.ActivationCancellation?.Cancel();
                }

                if (_runtimeEndpoints.TryGetValue(id, out currentRuntime) &&
                    currentRuntime.ActivationTask != null && !currentRuntime.ActivationTask.IsCompleted)
                {
                    currentRuntime.ActivationCancellation?.Cancel();
                }

                runtime = new RuntimeEndpoint
                {
                    Descriptor = copy,
                    Client = new StubLlmClient(),
                    Generation = Interlocked.Increment(ref _generation),
                    State = LlmEndpointLifecycleState.Inactive,
                    SessionApiKey = effectiveSessionApiKey ?? "",
                    Mode = EndpointMode(copy.Kind)
                };
                bool stageReplacement = copy.Active &&
                    _runtimeEndpoints.TryGetValue(id, out RuntimeEndpoint existing) &&
                    existing.Descriptor.Active && existing.State == LlmEndpointLifecycleState.Ready;
                if (stageReplacement)
                {
                    _pendingEndpoints[id] = runtime;
                }
                else
                {
                    _runtimeEndpoints[id] = runtime;
                    _pendingEndpoints.Remove(id);
                }
                if (!_runtimeProfiles.ContainsKey(id))
                {
                    _runtimeProfiles[id] = new LlmRuntimeProfile
                    {
                        ProfileId = id,
                        DisplayName = descriptor.DisplayName,
                        EndpointId = id
                    };
                }
                else if (string.Equals(_runtimeProfiles[id].EndpointId, id, StringComparison.Ordinal))
                {
                    _runtimeProfiles[id].DisplayName = descriptor.DisplayName;
                }

                if (copy.Active || copy.KeepWarm)
                {
                    runtime.ActivationTask = BeginActivationLocked(runtime, cancellationToken);
                }
            }

            SaveRuntimeState();
            Changed?.Invoke();
            return runtime.ActivationTask != null
                ? AwaitActivationForCallerAsync(runtime.ActivationTask, cancellationToken)
                : Task.FromResult(ToSnapshot(runtime));
        }

        /// <inheritdoc />
        public Task<LlmEndpointSnapshot> SetEndpointActiveAsync(
            string endpointId,
            bool active,
            bool keepWarm = false,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RuntimeEndpoint runtime;
            RuntimeEndpoint release = null;
            RuntimeEndpoint pendingRelease = null;
            lock (_gate)
            {
                string id = endpointId?.Trim() ?? "";
                if (!_runtimeEndpoints.TryGetValue(id, out runtime))
                {
                    throw new KeyNotFoundException("Unknown endpoint: " + endpointId);
                }

                runtime.Descriptor.Active = active;
                runtime.Descriptor.KeepWarm = keepWarm;
                if (!active && !keepWarm)
                {
                    if (_pendingEndpoints.TryGetValue(id, out RuntimeEndpoint pending))
                    {
                        pending.ActivationCancellation?.Cancel();
                        _pendingEndpoints.Remove(id);
                        pendingRelease = pending;
                    }

                    runtime.ActivationCancellation?.Cancel();
                    runtime.State = LlmEndpointLifecycleState.Inactive;
                    runtime.Error = "";
                    release = runtime;
                }
                else if (runtime.State != LlmEndpointLifecycleState.Ready)
                {
                    if (runtime.ActivationTask == null || runtime.ActivationTask.IsCompleted)
                    {
                        runtime.Generation = Interlocked.Increment(ref _generation);
                        runtime.ActivationTask = BeginActivationLocked(runtime, cancellationToken);
                    }
                }
            }

            RequestOwnedHostRelease(pendingRelease);
            RequestOwnedHostRelease(release);

            SaveRuntimeState();
            Changed?.Invoke();
            return runtime.ActivationTask != null && !runtime.ActivationTask.IsCompleted
                ? AwaitActivationForCallerAsync(runtime.ActivationTask, cancellationToken)
                : Task.FromResult(ToSnapshot(runtime));
        }

        /// <inheritdoc />
        public Task<bool> RemoveEndpointAsync(
            string endpointId,
            LlmEndpointRemovalMode mode = LlmEndpointRemovalMode.Drain,
            string replacementEndpointId = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            bool removed;
            RuntimeEndpoint release = null;
            RuntimeEndpoint pendingRelease = null;
            lock (_gate)
            {
                if (mode == LlmEndpointRemovalMode.CancelInFlight)
                {
                    return Task.FromResult(false);
                }

                string id = endpointId?.Trim() ?? "";
                string replacementId = replacementEndpointId?.Trim() ?? "";
                if (!string.IsNullOrEmpty(replacementId) &&
                    (string.Equals(id, replacementId, StringComparison.Ordinal) ||
                     !_runtimeEndpoints.ContainsKey(replacementId)))
                {
                    throw new KeyNotFoundException("Unknown replacement endpoint: " + replacementEndpointId);
                }

                if (_runtimeEndpoints.TryGetValue(id, out RuntimeEndpoint runtime))
                {
                    runtime.ActivationCancellation?.Cancel();
                    release = runtime;
                }
                if (_pendingEndpoints.TryGetValue(id, out RuntimeEndpoint pending))
                {
                    pending.ActivationCancellation?.Cancel();
                    _pendingEndpoints.Remove(id);
                    pendingRelease = pending;
                }
                removed = _runtimeEndpoints.Remove(id);
                foreach (string profileId in _runtimeProfiles.Values
                             .Where(p => string.Equals(p.EndpointId, id, StringComparison.Ordinal))
                             .Select(p => p.ProfileId).ToArray())
                {
                    if (!string.IsNullOrEmpty(replacementId))
                    {
                        _runtimeProfiles[profileId].EndpointId = replacementId;
                    }
                    else
                    {
                        _runtimeProfiles.Remove(profileId);
                        foreach (string role in _runtimeRoleProfiles
                                     .Where(pair => string.Equals(pair.Value, profileId, StringComparison.Ordinal))
                                     .Select(pair => pair.Key).ToArray())
                        {
                            _runtimeRoleProfiles.Remove(role);
                        }
                    }
                }
            }

            RequestOwnedHostRelease(pendingRelease);
            RequestOwnedHostRelease(release);

            if (removed)
            {
                SaveRuntimeState();
                Changed?.Invoke();
            }

            return Task.FromResult(removed);
        }

        /// <inheritdoc />
        public void AddOrUpdateProfile(LlmRuntimeProfile profile)
        {
            if (profile == null || profile.Validate().Count > 0)
            {
                throw new ArgumentException(profile == null ? "Profile is null." : string.Join(" ", profile.Validate()));
            }

            lock (_gate)
            {
                if (!_runtimeEndpoints.ContainsKey(profile.EndpointId))
                {
                    throw new KeyNotFoundException("Unknown endpoint: " + profile.EndpointId);
                }

                _runtimeProfiles[profile.ProfileId.Trim()] = FileLlmEndpointRegistryStore.CloneProfile(profile);
            }

            SaveRuntimeState();
            Changed?.Invoke();
        }

        /// <inheritdoc />
        public bool RemoveProfile(string profileId, string replacementProfileId = null)
        {
            bool removed;
            lock (_gate)
            {
                string id = profileId?.Trim() ?? "";
                string replacementId = replacementProfileId?.Trim() ?? "";
                if (!string.IsNullOrEmpty(replacementId) &&
                    (string.Equals(id, replacementId, StringComparison.Ordinal) ||
                     !_runtimeProfiles.ContainsKey(replacementId)))
                {
                    throw new KeyNotFoundException("Unknown replacement profile: " + replacementProfileId);
                }

                removed = _runtimeProfiles.Remove(id);
                foreach (string role in (removed
                             ? _runtimeRoleProfiles.Where(pair => pair.Value == id)
                             : Enumerable.Empty<KeyValuePair<string, string>>())
                         .Select(pair => pair.Key).ToArray())
                {
                    if (string.IsNullOrEmpty(replacementId))
                    {
                        _runtimeRoleProfiles.Remove(role);
                    }
                    else
                    {
                        _runtimeRoleProfiles[role] = replacementId;
                    }
                }
            }

            if (removed)
            {
                SaveRuntimeState();
                Changed?.Invoke();
            }

            return removed;
        }

        /// <inheritdoc />
        public void AssignRoleProfile(string rolePattern, string profileId, int sortOrder = 0)
        {
            lock (_gate)
            {
                string profile = profileId?.Trim() ?? "";
                if (!_runtimeProfiles.ContainsKey(profile))
                {
                    throw new KeyNotFoundException("Unknown profile: " + profileId);
                }

                _runtimeRoleProfiles[string.IsNullOrWhiteSpace(rolePattern) ? "*" : rolePattern.Trim()] = profile;
            }

            SaveRuntimeState();
            Changed?.Invoke();
        }

        /// <inheritdoc />
        public bool ClearRoleProfile(string rolePattern)
        {
            bool removed;
            lock (_gate)
            {
                string role = string.IsNullOrWhiteSpace(rolePattern) ? "*" : rolePattern.Trim();
                removed = _runtimeRoleProfiles.Remove(role);
            }

            if (removed)
            {
                SaveRuntimeState();
                Changed?.Invoke();
            }

            return removed;
        }

        /// <inheritdoc />
        public string GetRoleProfile(string roleId)
        {
            lock (_gate)
            {
                return ResolveRuntimeProfileIdLocked(roleId, "");
            }
        }

        private string ResolveRuntimeProfileIdLocked(string roleId, string explicitProfileId)
        {
            string explicitId = explicitProfileId?.Trim() ?? "";
            if (!string.IsNullOrEmpty(explicitId))
            {
                return explicitId;
            }

            string role = string.IsNullOrWhiteSpace(roleId) ? BuiltInAgentRoleIds.Creator : roleId.Trim();
            if (_runtimeRoleProfiles.TryGetValue(role, out string profile) ||
                _runtimeRoleProfiles.TryGetValue("*", out profile))
            {
                return profile;
            }

            return "";
        }

        private bool TryResolveReadyRuntimeEndpointLocked(
            string profileId,
            out RuntimeEndpoint endpoint,
            out string effectiveProfileId)
        {
            endpoint = null;
            effectiveProfileId = "";
            HashSet<string> visited = new(StringComparer.Ordinal);
            Queue<string> pending = new();
            pending.Enqueue(profileId);
            while (pending.Count > 0)
            {
                string candidate = pending.Dequeue();
                if (!visited.Add(candidate) || !_runtimeProfiles.TryGetValue(candidate, out LlmRuntimeProfile profile))
                {
                    continue;
                }

                if (_runtimeEndpoints.TryGetValue(profile.EndpointId, out RuntimeEndpoint runtime) &&
                    runtime.Descriptor.Active && runtime.State == LlmEndpointLifecycleState.Ready &&
                    runtime.Client != null)
                {
                    endpoint = runtime;
                    effectiveProfileId = candidate;
                    return true;
                }

                foreach (string fallback in profile.FallbackProfileIds ?? Array.Empty<string>())
                {
                    if (!string.IsNullOrWhiteSpace(fallback))
                    {
                        pending.Enqueue(fallback.Trim());
                    }
                }
            }

            return false;
        }

        private bool TryResolveActivatingRuntimeEndpointLocked(
            string profileId,
            out RuntimeEndpoint endpoint)
        {
            endpoint = null;
            HashSet<string> visited = new(StringComparer.Ordinal);
            Queue<string> pending = new();
            pending.Enqueue(profileId);
            while (pending.Count > 0)
            {
                string candidate = pending.Dequeue();
                if (!visited.Add(candidate) || !_runtimeProfiles.TryGetValue(candidate, out LlmRuntimeProfile profile))
                {
                    continue;
                }

                if (_runtimeEndpoints.TryGetValue(profile.EndpointId, out RuntimeEndpoint runtime) &&
                    runtime.Descriptor.Active && runtime.ActivationTask != null && !runtime.ActivationTask.IsCompleted &&
                    runtime.State is LlmEndpointLifecycleState.StartingNative or LlmEndpointLifecycleState.WaitingForHttp)
                {
                    endpoint = runtime;
                    return true;
                }

                foreach (string fallback in profile.FallbackProfileIds ?? Array.Empty<string>())
                {
                    if (!string.IsNullOrWhiteSpace(fallback))
                    {
                        pending.Enqueue(fallback.Trim());
                    }
                }
            }

            return false;
        }

        private Task<LlmEndpointSnapshot> BeginActivationLocked(
            RuntimeEndpoint runtime,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrEmpty(runtime.SessionApiKey) &&
                !string.IsNullOrWhiteSpace(runtime.Descriptor.SecretReference) &&
                _secretProvider.TryResolve(runtime.Descriptor.SecretReference.Trim(), out string resolvedSecret))
            {
                runtime.SessionApiKey = resolvedSecret ?? "";
            }

            Task priorHostRelease = runtime.HostReleaseTask;
            runtime.ActivationCancellation?.Dispose();
            runtime.ActivationCancellation = new CancellationTokenSource();
            runtime.ReleaseRequested = 0;
            runtime.State = runtime.Descriptor.Kind == LlmEndpointKind.LlmUnity
                ? LlmEndpointLifecycleState.StartingNative
                : LlmEndpointLifecycleState.WaitingForHttp;
            runtime.Error = "";
            return ActivateAfterHostReleaseAsync(
                runtime,
                priorHostRelease,
                runtime.ActivationCancellation.Token);
        }

        private async Task<LlmEndpointSnapshot> ActivateAfterHostReleaseAsync(
            RuntimeEndpoint runtime,
            Task priorHostRelease,
            CancellationToken cancellationToken)
        {
            if (priorHostRelease != null)
            {
                await priorHostRelease;
            }

            cancellationToken.ThrowIfCancellationRequested();
            return await ActivateRuntimeAsync(runtime, cancellationToken);
        }

        private async Task<LlmEndpointSnapshot> ActivateRuntimeAsync(
            RuntimeEndpoint runtime,
            CancellationToken cancellationToken)
        {
            LlmEndpointClientActivation activation = null;
            Exception failure = null;
            try
            {
                activation = await BuildRuntimeClientAsync(
                    runtime.Descriptor, runtime.SessionApiKey, cancellationToken);
            }
            catch (Exception ex)
            {
                failure = ex;
            }

            RuntimeEndpoint replacedGeneration = null;
            bool releaseActivation = false;
            LlmEndpointSnapshot snapshot;
            lock (_gate)
            {
                runtime.ReleaseOwnedHostAsync = activation?.ReleaseOwnedHostAsync;
                string id = runtime.Descriptor.EndpointId;
                bool isPublished = _runtimeEndpoints.TryGetValue(id, out RuntimeEndpoint current) &&
                                   ReferenceEquals(current, runtime);
                bool isPending = _pendingEndpoints.TryGetValue(id, out RuntimeEndpoint pending) &&
                                 ReferenceEquals(pending, runtime);
                if (!isPublished && !isPending)
                {
                    releaseActivation = true;
                    snapshot = ToSnapshot(runtime);
                }
                else if (failure is OperationCanceledException ||
                         !runtime.Descriptor.Active && !runtime.Descriptor.KeepWarm)
                {
                    runtime.State = LlmEndpointLifecycleState.Inactive;
                    runtime.Error = "";
                    releaseActivation = true;
                    if (isPending)
                    {
                        _pendingEndpoints.Remove(id);
                    }

                    snapshot = ToSnapshot(runtime);
                }
                else if (failure != null)
                {
                    runtime.Client = new StubLlmClient();
                    runtime.State = LlmEndpointLifecycleState.Failed;
                    runtime.Error = failure.Message;
                    releaseActivation = true;
                    if (isPending)
                    {
                        _pendingEndpoints.Remove(id);
                    }

                    snapshot = ToSnapshot(runtime);
                }
                else
                {
                    runtime.Client = activation?.Client ?? new StubLlmClient();
                    runtime.Mode = activation?.Mode ?? EndpointMode(runtime.Descriptor.Kind);
                    runtime.State = LlmEndpointLifecycleState.Ready;
                    runtime.Error = "";
                    if (isPending)
                    {
                        replacedGeneration = current;
                        _runtimeEndpoints[id] = runtime;
                        _pendingEndpoints.Remove(id);
                    }

                    snapshot = ToSnapshot(runtime);
                }
            }

            if (releaseActivation)
            {
                RequestOwnedHostRelease(runtime);
            }

            RequestOwnedHostRelease(replacedGeneration);

            if (failure == null)
            {
                SaveRuntimeState();
            }
            Changed?.Invoke();
            return snapshot;
        }

        private void RequestOwnedHostRelease(RuntimeEndpoint runtime)
        {
            if (runtime == null || Interlocked.Exchange(ref runtime.ReleaseRequested, 1) != 0)
            {
                return;
            }

            runtime.HostReleaseTask = ReleaseOwnedHostAfterDrainAsync(runtime);
        }

        private async Task ReleaseOwnedHostAfterDrainAsync(RuntimeEndpoint runtime)
        {
            Task activation = runtime.ActivationTask;
            if (runtime.ReleaseOwnedHostAsync == null && activation != null && !activation.IsCompleted)
            {
                try
                {
                    await activation;
                }
                catch
                {
                }
            }

            while (Volatile.Read(ref runtime.InFlightRequests) > 0)
            {
                await Task.Yield();
            }

            Func<Task> release = Interlocked.Exchange(ref runtime.ReleaseOwnedHostAsync, null);
            if (release == null)
            {
                return;
            }

            try
            {
                await release();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    GameLogFeature.Llm,
                    "LlmClientRegistry: owned endpoint host release failed: " + ex.Message);
            }
        }

        private static string DescriptorFingerprint(LlmEndpointDescriptor descriptor)
        {
            return string.Join("|",
                descriptor.EndpointId,
                descriptor.Kind,
                descriptor.BaseUrl,
                descriptor.Model,
                descriptor.SecretReference,
                descriptor.LocalModelPath,
                descriptor.UnityAgentName,
                descriptor.Port,
                descriptor.GpuLayers,
                descriptor.Remote,
                descriptor.FlashAttention,
                descriptor.ParallelSlots,
                descriptor.ContextWindowTokens,
                descriptor.Active,
                descriptor.KeepWarm);
        }

        private async Task<LlmEndpointClientActivation> BuildRuntimeClientAsync(
            LlmEndpointDescriptor descriptor,
            string sessionApiKey,
            CancellationToken cancellationToken)
        {
            if (_endpointClientFactory != null)
            {
                LlmEndpointClientActivation activation = await _endpointClientFactory.ActivateAsync(
                    descriptor, sessionApiKey, cancellationToken);
                return activation ?? new LlmEndpointClientActivation
                {
                    Client = new StubLlmClient(),
                    Mode = EndpointMode(descriptor.Kind)
                };
            }

            if (descriptor.Kind == LlmEndpointKind.Offline)
            {
                return new LlmEndpointClientActivation
                {
                    Client = new StubLlmClient(),
                    Mode = LlmExecutionMode.Offline
                };
            }

            string baseUrl = descriptor.Kind == LlmEndpointKind.LlmUnity
                ? $"http://127.0.0.1:{(descriptor.Port > 0 ? descriptor.Port : 13333)}/v1"
                : descriptor.BaseUrl;
            OpenAiHttpOptions options = new()
            {
                UseOpenAiCompatibleHttp = true,
                ApiBaseUrl = baseUrl,
                ApiKey = sessionApiKey ?? "",
                Model = string.IsNullOrWhiteSpace(descriptor.Model) ? "local" : descriptor.Model,
                RequestTimeoutSeconds = Mathf.Max(5, Mathf.RoundToInt(_settings.LlmRequestTimeoutSeconds)),
                MaxTokens = _settings.MaxTokens,
                Temperature = _settings.Temperature
            };

            if (descriptor.Kind == LlmEndpointKind.LlmUnity)
            {
#if UNITY_WEBGL
                throw new PlatformNotSupportedException("LLMUnity endpoints are not supported on WebGL.");
#else
                using HttpClient probe = new() { Timeout = TimeSpan.FromSeconds(2) };
                using HttpRequestMessage request = new(HttpMethod.Get, baseUrl + "/models");
                using HttpResponseMessage response = await probe.SendAsync(request, cancellationToken);
#endif
            }

#if COREAI_NO_LLM
            return new LlmEndpointClientActivation
            {
                Client = new StubLlmClient(),
                Mode = EndpointMode(descriptor.Kind)
            };
#else
            return new LlmEndpointClientActivation
            {
                Client = new OpenAiChatLlmClient(options, _settings, _logger, _memoryStore),
                Mode = EndpointMode(descriptor.Kind)
            };
#endif
        }

        private static LlmExecutionMode EndpointMode(LlmEndpointKind kind)
        {
            return kind == LlmEndpointKind.LlmUnity
                ? LlmExecutionMode.LocalModel
                : kind == LlmEndpointKind.Offline
                    ? LlmExecutionMode.Offline
                    : LlmExecutionMode.ClientOwnedApi;
        }

        private static async Task<LlmEndpointSnapshot> AwaitActivationForCallerAsync(
            Task<LlmEndpointSnapshot> activation,
            CancellationToken cancellationToken)
        {
            if (!cancellationToken.CanBeCanceled)
            {
                return await activation.ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();
            TaskCompletionSource<bool> cancelled = new(
                TaskCreationOptions.RunContinuationsAsynchronously);
            using CancellationTokenRegistration registration = cancellationToken.Register(
                () => cancelled.TrySetResult(true));
            Task completed = await Task.WhenAny(activation, cancelled.Task).ConfigureAwait(false);
            if (completed == cancelled.Task)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            LlmEndpointSnapshot snapshot = await activation.ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return snapshot;
        }

        private static LlmEndpointSnapshot ToSnapshot(RuntimeEndpoint runtime)
        {
            return new LlmEndpointSnapshot
            {
                Descriptor = FileLlmEndpointRegistryStore.CloneDescriptor(runtime.Descriptor),
                State = runtime.State,
                Generation = runtime.Generation,
                Error = runtime.Error,
                InFlightRequests = Volatile.Read(ref runtime.InFlightRequests)
            };
        }

        private void RestoreRuntimeState()
        {
            LlmEndpointRegistryState state = _persistenceStore?.Load();
            if (state == null)
            {
                return;
            }

            foreach (LlmEndpointDescriptor descriptor in state.Endpoints ?? Array.Empty<LlmEndpointDescriptor>())
            {
                if (descriptor == null || descriptor.Validate().Count > 0)
                {
                    continue;
                }

                LlmEndpointDescriptor copy = FileLlmEndpointRegistryStore.CloneDescriptor(descriptor);
                _runtimeEndpoints[copy.EndpointId] = new RuntimeEndpoint
                {
                    Descriptor = copy,
                    Client = new StubLlmClient(),
                    State = LlmEndpointLifecycleState.Inactive,
                    Generation = Interlocked.Increment(ref _generation),
                    Mode = EndpointMode(copy.Kind)
                };
            }

            foreach (LlmRuntimeProfile profile in state.Profiles ?? Array.Empty<LlmRuntimeProfile>())
            {
                if (profile == null || profile.Validate().Count > 0 ||
                    !_runtimeEndpoints.ContainsKey(profile.EndpointId))
                {
                    continue;
                }

                _runtimeProfiles[profile.ProfileId.Trim()] =
                    FileLlmEndpointRegistryStore.CloneProfile(profile);
            }

            foreach (LlmPersistedRoleProfile assignment in
                     state.RoleProfiles ?? Array.Empty<LlmPersistedRoleProfile>())
            {
                string pattern = assignment?.RolePattern?.Trim() ?? "";
                string profileId = assignment?.ProfileId?.Trim() ?? "";
                if (!string.IsNullOrWhiteSpace(pattern) && _runtimeProfiles.ContainsKey(profileId))
                {
                    _runtimeRoleProfiles[pattern] = profileId;
                }
            }

            foreach (RuntimeEndpoint endpoint in _runtimeEndpoints.Values)
            {
                if (endpoint.Descriptor.Active || endpoint.Descriptor.KeepWarm)
                {
                    endpoint.ActivationTask = BeginActivationLocked(endpoint, CancellationToken.None);
                }
            }
        }

        private void SaveRuntimeState()
        {
            if (_persistenceStore == null)
            {
                return;
            }

            LlmEndpointRegistryState state;
            long revision;
            lock (_gate)
            {
                state = new LlmEndpointRegistryState
                {
                    Endpoints = _runtimeEndpoints.Values
                        .Select(endpoint => FileLlmEndpointRegistryStore.CloneDescriptor(endpoint.Descriptor))
                        .ToArray(),
                    Profiles = _runtimeProfiles.Values
                        .Select(FileLlmEndpointRegistryStore.CloneProfile)
                        .ToArray(),
                    RoleProfiles = _runtimeRoleProfiles.Select(pair => new LlmPersistedRoleProfile
                    {
                        RolePattern = pair.Key,
                        ProfileId = pair.Value
                    }).ToArray()
                };
                revision = ++_persistenceRevision;
            }

            lock (_persistenceGate)
            {
                if (revision <= _persistedRevision)
                {
                    return;
                }

                _persistenceStore.Save(state);
                _persistedRevision = revision;
            }
        }

        private ILlmClient BuildProfileClient(LlmBackendProfileEntry p)
        {
            LlmExecutionMode mode = ResolveProfileMode(p);
            switch (mode)
            {
                case LlmExecutionMode.Offline:
                    if (p.kind == LlmBackendKind.Stub)
                    {
                        return new StubLlmClient();
                    }

                    return _settings is CoreAISettingsAsset unitySettings
                        ? new OfflineLlmClient(unitySettings)
                        : new StubLlmClient();
                case LlmExecutionMode.ClientOwnedApi:
                case LlmExecutionMode.ClientLimited:
                case LlmExecutionMode.ServerManagedApi:
#if COREAI_NO_LLM
                    return new StubLlmClient();
#else
                    if (p.httpSettings == null || !p.httpSettings.UseOpenAiCompatibleHttp)
                    {
                        _logger.LogWarning(
                            GameLogFeature.Llm,
                            $"LlmClientRegistry: profile '{p.profileId}' uses OpenAiHttp without valid httpSettings.");
                        return new StubLlmClient();
                    }

                    ILlmClient http = mode == LlmExecutionMode.ServerManagedApi
                        ? (ILlmClient)new RefreshOnUnauthorizedDecorator(
                            new ServerManagedLlmClient(p.httpSettings, _settings, _logger, _memoryStore))
                        : new OpenAiChatLlmClient(p.httpSettings, _settings, _logger, _memoryStore);
                    if (mode != LlmExecutionMode.ClientLimited)
                    {
                        return http;
                    }

                    int maxRequests = p.maxRequestsPerSession > 0
                        ? p.maxRequestsPerSession
                        : p.httpSettings.MaxRequestsPerSession;
                    int maxPromptChars = p.maxPromptChars > 0 ? p.maxPromptChars : p.httpSettings.MaxPromptChars;
                    return new ClientLimitedLlmClientDecorator(http, maxRequests, maxPromptChars);
#endif
                case LlmExecutionMode.LocalModel:
#if !COREAI_HAS_LLMUNITY || UNITY_WEBGL
                    return new StubLlmClient();
#else
                    LLMAgent agent = null;
                    if (!string.IsNullOrWhiteSpace(p.unityAgentGameObjectName))
                    {
                        GameObject go = GameObject.Find(p.unityAgentGameObjectName);
                        if (go != null)
                        {
                            agent = go.GetComponent<LLMAgent>();
                        }
                    }

                    agent ??= UnityEngine.Object.FindFirstObjectByType<LLMAgent>();
                    if (agent == null)
                    {
                        return new StubLlmClient();
                    }

                    LLM llm = agent.GetComponent<LLM>();
                    if (llm != null)
                    {
                        LlmUnityModelBootstrap.TryAutoAssignResolvableModel(llm, _logger);
                    }

                    if (llm != null && string.IsNullOrWhiteSpace(llm.model))
                    {
                        return new StubLlmClient();
                    }

                    if (_settings is not CoreAISettingsAsset llmUnityAssetSettings)
                    {
                        return new StubLlmClient();
                    }

                    string modelName = llm != null && !string.IsNullOrWhiteSpace(llm.model)
                        ? llm.model
                        : Path.GetFileName(llmUnityAssetSettings.GgufModelPath);
                    if (string.IsNullOrWhiteSpace(modelName))
                    {
                        modelName = "local";
                    }

                    LlmUnityServerHttpSettings adapter = new(
                        llmUnityAssetSettings, llmUnityAssetSettings.LlmUnityServerPort, modelName, "");
                    return new OpenAiChatLlmClient(adapter, _settings, _logger, _memoryStore);
#endif
                default:
                    return new StubLlmClient();
            }
        }

        private static LlmExecutionMode ResolveProfileMode(LlmBackendProfileEntry p)
        {
            if (p == null)
            {
                return LlmExecutionMode.Offline;
            }

            if (p.executionMode != LlmExecutionMode.Auto)
            {
                return p.executionMode;
            }

            if (p.httpSettings != null && p.httpSettings.ExecutionMode != LlmExecutionMode.ClientOwnedApi)
            {
                return p.httpSettings.ExecutionMode;
            }

            switch (p.kind)
            {
                case LlmBackendKind.LlmUnity:
                case LlmBackendKind.LocalModel:
                    return LlmExecutionMode.LocalModel;
                case LlmBackendKind.ClientLimited:
                    return LlmExecutionMode.ClientLimited;
                case LlmBackendKind.ServerManagedApi:
                    return LlmExecutionMode.ServerManagedApi;
                case LlmBackendKind.Stub:
                case LlmBackendKind.Offline:
                    return LlmExecutionMode.Offline;
                default:
                    return LlmExecutionMode.ClientOwnedApi;
            }
        }
    }
}
