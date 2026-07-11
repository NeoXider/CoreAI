using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
    public sealed class LlmClientRegistry : ILlmClientRegistry, ILlmRoutingController
    {
        private readonly IGameLogger _logger;
        private readonly IAgentMemoryStore _memoryStore;
        private readonly ICoreAISettings _settings;
        private readonly object _gate = new();
        private ILlmClient _legacyFallback = new StubLlmClient();
        private LlmExecutionMode _legacyFallbackMode = LlmExecutionMode.Auto;
        private Dictionary<string, ILlmClient> _byProfileId = new(StringComparer.Ordinal);
        private Dictionary<string, int> _contextByProfileId = new(StringComparer.Ordinal);
        private Dictionary<string, LlmExecutionMode> _modeByProfileId = new(StringComparer.Ordinal);
        private ILlmRouteResolver _routeResolver = new LlmRouteResolver(new LlmRouteTable());
        private bool _useManifestRouting;

        /// <param name="logger">The logger value.</param>
        public LlmClientRegistry(IGameLogger logger, ICoreAISettings settings, IAgentMemoryStore memoryStore = null)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _memoryStore = memoryStore;
        }

        /// <summary>Legacy LLM client used when no route-specific client is available.</summary>
        public void SetLegacyFallback(ILlmClient legacy)
        {
            _legacyFallback = legacy ?? new StubLlmClient();
            _legacyFallbackMode = _settings is CoreAISettingsAsset unitySettings
                ? unitySettings.ExecutionMode
                : LlmExecutionMode.Auto;
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
            string role = string.IsNullOrWhiteSpace(roleId) ? BuiltInAgentRoleIds.Creator : roleId.Trim();
            lock (_gate)
            {
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
            string role = string.IsNullOrWhiteSpace(roleId) ? BuiltInAgentRoleIds.Creator : roleId.Trim();
            lock (_gate)
            {
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
            string profileId = ResolveProfileIdForRole(roleId);
            lock (_gate)
            {
                return !string.IsNullOrEmpty(profileId) &&
                       _modeByProfileId.TryGetValue(profileId, out LlmExecutionMode mode)
                    ? mode
                    : _legacyFallbackMode;
            }
        }

        public string ResolveProfileIdForRole(string roleId)
        {
            string role = string.IsNullOrWhiteSpace(roleId) ? BuiltInAgentRoleIds.Creator : roleId.Trim();
            lock (_gate)
            {
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
