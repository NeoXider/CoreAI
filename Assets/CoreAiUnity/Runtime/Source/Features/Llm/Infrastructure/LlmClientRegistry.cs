using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Ai;
using Cysharp.Threading.Tasks;
using CoreAI.Infrastructure.Logging;
#if COREAI_HAS_LLMUNITY && !UNITY_WEBGL && COREAI_LLM
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
        /// <summary>Reserved diagnostic profile id reported when no routing rule matched.</summary>
        internal const string LegacyFallbackProfileId = "fallback";

        /// <summary>Bounded drain before an owned llama.cpp host is force-released (see release path).</summary>
        private const int OwnedHostDrainTimeoutMs = 120_000;

        /// <summary>Poll interval of the in-flight drain loop before an owned host is released.</summary>
        private const int DrainPollIntervalMs = 10;

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

            public bool SupportsNativeToolCallingForRole(string agentRoleId)
            {
                return _supportsNativeTools;
            }

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
                // WHY: an await-based wait instead of a Task.Yield poll — polling hot-spins a
                // thread-pool worker for the entire activation when resumed off the Unity main thread.
                if (cancellationToken.CanBeCanceled && !activation.IsCompleted)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    TaskCompletionSource<bool> cancelled = new(
                        TaskCreationOptions.RunContinuationsAsynchronously);
                    using CancellationTokenRegistration registration =
                        cancellationToken.Register(() => cancelled.TrySetResult(true));
                    await Task.WhenAny(activation, cancelled.Task);
                    cancellationToken.ThrowIfCancellationRequested();
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
            _endpointClientFactory =
                endpointClientFactory ?? new LlmEndpointClientFactory(settings, logger, memoryStore);
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

        /// <inheritdoc />
        public LlmRoleRouteSnapshot ResolveRouteForRole(string roleId, string explicitProfileId)
        {
            // WHY: one _gate acquisition (Monitor is re-entrant) so a concurrent endpoint switch
            // cannot pair endpoint A's client with endpoint B's profile/context-window/mode.
            lock (_gate)
            {
                string profileId = ResolveProfileIdForRole(roleId, explicitProfileId);
                long generation = 0;
                if (!string.IsNullOrEmpty(profileId) &&
                    TryResolveReadyRuntimeEndpointLocked(profileId, out RuntimeEndpoint served, out _))
                {
                    generation = served.Generation;
                }
                else if (!string.IsNullOrEmpty(profileId) &&
                         TryResolveActivatingRuntimeEndpointLocked(profileId, out RuntimeEndpoint activating))
                {
                    // WHY: a request that resolves while the endpoint is still activating must still be
                    // able to report health for the generation it will be served by; generation 0 would
                    // silently drop those reports.
                    generation = activating.Generation;
                }

                return new LlmRoleRouteSnapshot
                {
                    Client = ResolveClientForRole(roleId, explicitProfileId),
                    ProfileId = profileId,
                    ContextWindowTokens = ResolveContextWindowForRole(roleId, explicitProfileId),
                    Mode = ResolveExecutionModeForRole(roleId, explicitProfileId),
                    IsRouted = !string.IsNullOrEmpty(profileId) &&
                               (_runtimeProfiles.ContainsKey(profileId) ||
                                _byProfileId.ContainsKey(profileId)),
                    Generation = generation
                };
            }
        }

        /// <inheritdoc />
        public void ReportRouteFailure(string profileId, long generation, LlmErrorCode errorCode, string error)
        {
            string profile = profileId?.Trim() ?? "";
            if (profile.Length == 0 || string.Equals(profile, LegacyFallbackProfileId, StringComparison.Ordinal))
            {
                return;
            }

            bool changed = false;
            lock (_gate)
            {
                if (_runtimeProfiles.TryGetValue(profile, out LlmRuntimeProfile runtimeProfile) &&
                    _runtimeEndpoints.TryGetValue(runtimeProfile.EndpointId, out RuntimeEndpoint runtime) &&
                    runtime.State == LlmEndpointLifecycleState.Ready &&
                    generation != 0 && runtime.Generation == generation)
                {
                    // WHY (generation check): a late completion from a replaced endpoint must not mark
                    // or clear its successor's health — only reports from the serving generation count.
                    // Unknown generation (0, e.g. a request that resolved while activating) is dropped
                    // rather than treated as a wildcard that could mutate an unrelated generation.
                    // WHY: the endpoint stays Ready so traffic still flows and a transient outage
                    // needs no manual re-activation, but the failure must be visible on the snapshot —
                    // otherwise the UI keeps reporting a healthy endpoint whose key expired mid-session.
                    string note = errorCode == LlmErrorCode.None
                        ? ""
                        : string.IsNullOrWhiteSpace(error)
                            ? $"Degraded: {errorCode}."
                            : $"Degraded: {errorCode}: {error.Trim()}";
                    if (!string.Equals(runtime.Error, note, StringComparison.Ordinal))
                    {
                        runtime.Error = note;
                        changed = true;
                    }
                }
            }

            if (changed)
            {
                Changed?.Invoke();
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
                    return LegacyFallbackProfileId;
                }

                LlmRouteResolution resolution = _routeResolver.Resolve(role);
                if (resolution.Found && _byProfileId.ContainsKey(resolution.Profile.ProfileId))
                {
                    return resolution.Profile.ProfileId;
                }

                return LegacyFallbackProfileId;
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
            RuntimeEndpoint replacedRuntime = null;
            Task<LlmEndpointSnapshot> activationToAwait;
            LlmEndpointSnapshot immediateSnapshot;
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
                    // WHY: publishing directly can evict a Ready endpoint whose activation already
                    // completed; nothing else observes that instance again, so its owned llama.cpp
                    // host (server/VRAM/GameObject) must be released here or it leaks.
                    if (_runtimeEndpoints.TryGetValue(id, out RuntimeEndpoint evicted) &&
                        !ReferenceEquals(evicted, runtime))
                    {
                        replacedRuntime = evicted;
                    }

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

                // WHY: snapshot under the lock — State/Error/Descriptor flags mutate in place under
                // _gate elsewhere, so snapshotting outside can observe a torn Active/State pair.
                activationToAwait = runtime.ActivationTask;
                immediateSnapshot = activationToAwait == null ? ToSnapshot(runtime) : null;
            }

            RequestOwnedHostRelease(replacedRuntime);
            SaveRuntimeState();
            Changed?.Invoke();
            return activationToAwait != null
                ? AwaitActivationForCallerAsync(activationToAwait, cancellationToken)
                : Task.FromResult(immediateSnapshot);
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
            Task<LlmEndpointSnapshot> activationToAwait;
            LlmEndpointSnapshot immediateSnapshot;
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

                // WHY: snapshot under the lock — see AddOrUpdateEndpointAsync.
                activationToAwait = runtime.ActivationTask != null && !runtime.ActivationTask.IsCompleted
                    ? runtime.ActivationTask
                    : null;
                immediateSnapshot = activationToAwait == null ? ToSnapshot(runtime) : null;
            }

            RequestOwnedHostRelease(pendingRelease);
            RequestOwnedHostRelease(release);

            SaveRuntimeState();
            Changed?.Invoke();
            return activationToAwait != null
                ? AwaitActivationForCallerAsync(activationToAwait, cancellationToken)
                : Task.FromResult(immediateSnapshot);
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
            if (mode == LlmEndpointRemovalMode.CancelInFlight)
            {
                // WHY: this registry cannot prove cancellation of tracked in-flight calls; the
                // contract requires an unambiguous rejection instead of a false-success or a
                // "false" that is indistinguishable from "endpoint not found".
                throw new NotSupportedException(
                    "LlmClientRegistry cannot cancel tracked in-flight requests. " +
                    "Use LlmEndpointRemovalMode.Drain instead.");
            }

            lock (_gate)
            {
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
                throw new ArgumentException(profile == null
                    ? "Profile is null."
                    : string.Join(" ", profile.Validate()));
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
                // WHY: "fallback" is the reserved diagnostic id reported when no route matched.
                // Retry decorators echo the annotated request back with it as an explicit profile;
                // unless a real profile with that name exists it must re-resolve like "no explicit
                // profile" instead of routing every retry to RoutingUnavailableClient.
                if (!string.Equals(explicitId, LegacyFallbackProfileId, StringComparison.Ordinal) ||
                    _runtimeProfiles.ContainsKey(explicitId) ||
                    _byProfileId.ContainsKey(explicitId))
                {
                    return explicitId;
                }
            }

            string role = string.IsNullOrWhiteSpace(roleId) ? BuiltInAgentRoleIds.Creator : roleId.Trim();
            if (_runtimeRoleProfiles.TryGetValue(role, out string profile))
            {
                return profile;
            }

            // WHY: AssignRoleProfile documents pattern keys; "npc.*" must match "npc.guard".
            // Exact match wins above; here the longest wildcard prefix wins, with bare "*" as the
            // zero-length prefix that matches every role.
            string bestProfile = "";
            int bestPrefixLength = -1;
            foreach (KeyValuePair<string, string> assignment in _runtimeRoleProfiles)
            {
                string pattern = assignment.Key;
                if (!pattern.EndsWith("*", StringComparison.Ordinal))
                {
                    continue;
                }

                string prefix = pattern.Substring(0, pattern.Length - 1);
                if (prefix.Length > bestPrefixLength &&
                    role.StartsWith(prefix, StringComparison.Ordinal))
                {
                    bestProfile = assignment.Value;
                    bestPrefixLength = prefix.Length;
                }
            }

            return bestPrefixLength >= 0 ? bestProfile : "";
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
                    runtime.Descriptor.Active && runtime.ActivationTask != null &&
                    !runtime.ActivationTask.IsCompleted &&
                    runtime.State is LlmEndpointLifecycleState.StartingNative
                        or LlmEndpointLifecycleState.WaitingForHttp)
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

            if (Monitor.IsEntered(_gate))
            {
                // WHY: a synchronously-completing factory keeps this method inline inside
                // BeginActivationLocked's caller, which holds _gate; yielding here keeps the publish
                // epilogue (persistence write and the Changed event into UI subscribers) out of that
                // lock so subscribers can never observe or re-enter the registry mid-mutation.
                await Task.Yield();
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
                         (!runtime.Descriptor.Active && !runtime.Descriptor.KeepWarm))
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

            // WHY: BeginActivationLocked reads HostReleaseTask under _gate to chain a new host behind the
            // old one's teardown; publishing it outside that lock lets an activation start a second
            // llama.cpp host on the port the previous one still holds.
            lock (_gate)
            {
                runtime.HostReleaseTask = ReleaseOwnedHostAfterDrainAsync(runtime);
            }
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

            // WHY: take the release delegate BEFORE draining. With nothing to release the drain is pure
            // waiting, and on WebGL that wait never ends (no threads, no timers), so HostReleaseTask never
            // completes and the next activation — which awaits it — leaves the endpoint in WaitingForHttp
            // forever.
            Func<Task> release = Interlocked.Exchange(ref runtime.ReleaseOwnedHostAsync, null);
            if (release == null)
            {
                return;
            }

            int drainWaitedMs = 0;
            while (Volatile.Read(ref runtime.InFlightRequests) > 0)
            {
                if (drainWaitedMs >= OwnedHostDrainTimeoutMs)
                {
                    // WHY: an SSE stream that never completes would otherwise hold the llama.cpp host
                    // (VRAM, GameObject) forever AND hang any later re-activation that awaits this
                    // release. Forcing the release after a bounded drain is the lesser harm.
                    _logger.LogWarning(
                        GameLogFeature.Llm,
                        $"LlmClientRegistry: forcing owned host release for '{runtime.Descriptor?.EndpointId}' " +
                        $"after {OwnedHostDrainTimeoutMs / 1000}s drain timeout with " +
                        $"{Volatile.Read(ref runtime.InFlightRequests)} request(s) still tracked.");
                    break;
                }

                // WHY: paced poll instead of Task.Yield — there is no completion source for the in-flight
                // counter, and a yield loop hot-spins a worker for long SSE streams. UniTask.Delay (not
                // Task.Delay) because Task.Delay needs System.Threading.Timer, which does not exist on
                // WebGL/Emscripten and would never resume there.
                await UniTask.Delay(DrainPollIntervalMs, DelayType.Realtime);
                drainWaitedMs += DrainPollIntervalMs;
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
                descriptor.KeepWarm,
                descriptor.MaxTokens,
                descriptor.ReasoningMode,
                descriptor.ThinkingBudgetTokens,
                descriptor.ExtraBodyJson);
        }

        private async Task<LlmEndpointClientActivation> BuildRuntimeClientAsync(
            LlmEndpointDescriptor descriptor,
            string sessionApiKey,
            CancellationToken cancellationToken)
        {
            LlmEndpointClientActivation activation = await _endpointClientFactory.ActivateAsync(
                descriptor, sessionApiKey, cancellationToken);
            return activation ?? new LlmEndpointClientActivation
            {
                Client = new StubLlmClient(),
                Mode = EndpointMode(descriptor.Kind)
            };
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
                return await activation;
            }

            cancellationToken.ThrowIfCancellationRequested();
            TaskCompletionSource<bool> cancelled = new(
                TaskCreationOptions.RunContinuationsAsynchronously);
            using CancellationTokenRegistration registration =
                cancellationToken.Register(() => cancelled.TrySetResult(true));
            Task completed = await Task.WhenAny(activation, cancelled.Task);
            if (completed == cancelled.Task)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            LlmEndpointSnapshot snapshot = await activation;
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

            // WHY: BeginActivationLocked requires _gate; the constructor is the only caller and an
            // activation continuation can re-enter registry state on another thread immediately.
            lock (_gate)
            {
                RestoreRuntimeStateLocked(state);
            }
        }

        private void RestoreRuntimeStateLocked(LlmEndpointRegistryState state)
        {
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

            // WHY: Offline execution mode means "no LLM backends" — restoring persisted Active/KeepWarm
            // endpoints must not boot native local models (llama.cpp) or HTTP hosts behind the user's
            // back. Descriptors/profiles/role assignments above are still restored so the Hub keeps
            // showing them; endpoints simply stay Inactive until explicitly activated.
            if (_settings is CoreAISettingsAsset offlineCheck && offlineCheck.UseOffline)
            {
                return;
            }

            foreach (RuntimeEndpoint endpoint in _runtimeEndpoints.Values)
            {
                if (!endpoint.Descriptor.Active && !endpoint.Descriptor.KeepWarm)
                {
                    continue;
                }

                string secretReference = endpoint.Descriptor.SecretReference?.Trim() ?? "";
                if (secretReference.Length > 0 &&
                    (!_secretProvider.TryResolve(secretReference, out string secret) ||
                     string.IsNullOrEmpty(secret)))
                {
                    // WHY: session keys are never persisted; auto-activating a key-auth endpoint
                    // whose secret does not resolve would deterministically fail with an empty key
                    // on every launch. Wait for a session key (Hub or AddOrUpdateEndpointAsync).
                    endpoint.Error =
                        $"Session API key required: secret '{secretReference}' did not resolve.";
                    continue;
                }

                endpoint.ActivationTask = BeginActivationLocked(endpoint, CancellationToken.None);
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

                try
                {
                    _persistenceStore.Save(state);
                }
                catch (IOException ex)
                {
                    // WHY: the in-memory mutation already happened; letting the write failure escape skips
                    // the caller's Changed notification and leaves every subscriber on stale state.
                    _logger.LogWarning(
                        GameLogFeature.Llm,
                        "LlmClientRegistry: could not persist the endpoint registry: " + ex.Message);
                    return;
                }

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
#if !COREAI_LLM
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
#if !COREAI_HAS_LLMUNITY || UNITY_WEBGL || !COREAI_LLM
#if UNITY_WEBGL
                    return new UnsupportedLocalModelLlmClient(Application.platform);
#else
                    return new UnsupportedLocalModelLlmClient(
                        LocalModelPlatformSupport.IntegrationUnavailableMessage);
#endif
#else
                    if (!LocalModelPlatformSupport.IsSupported(Application.platform))
                    {
                        return new UnsupportedLocalModelLlmClient(Application.platform);
                    }

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
