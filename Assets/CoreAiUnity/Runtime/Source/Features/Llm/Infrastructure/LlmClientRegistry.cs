using System;
using System.Collections.Generic;
using System.Linq;
using CoreAI.Ai;
using CoreAI.Infrastructure.Logging;
#if !COREAI_NO_LLM && !UNITY_WEBGL
using LLMUnity;
#endif
using UnityEngine;
using IAgentMemoryStore = CoreAI.Ai.IAgentMemoryStore;

namespace CoreAI.Infrastructure.Llm
{
    /// <summary>
    /// Кэш <see cref="ILlmClient"/> по profileId + матчинг ролей по <see cref="LlmRoutingManifest"/>.
    /// </summary>
    public sealed class LlmClientRegistry : ILlmClientRegistry, ILlmRoutingController
    {
        private readonly IGameLogger _logger;
        private readonly IAgentMemoryStore _memoryStore;
        private readonly ICoreAISettings _settings;
        private readonly object _gate = new();
        private ILlmClient _legacyFallback = new StubLlmClient();
        private Dictionary<string, ILlmClient> _byProfileId = new(StringComparer.Ordinal);
        private Dictionary<string, int> _contextByProfileId = new(StringComparer.Ordinal);
        private List<(string pattern, string profileId, int order)> _routes = new();
        private bool _useManifestRouting;

        /// <param name="logger">Логи конфигурации маршрутизации (без прямого Unity Debug).</param>
        public LlmClientRegistry(IGameLogger logger, ICoreAISettings settings, IAgentMemoryStore memoryStore = null)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _memoryStore = memoryStore;
        }

        /// <summary>Клиент по умолчанию, если маршрутизация выключена или нет совпадения.</summary>
        public void SetLegacyFallback(ILlmClient legacy)
        {
            _legacyFallback = legacy ?? new StubLlmClient();
        }

        /// <inheritdoc />
        public void ApplyManifest(LlmRoutingManifest manifest)
        {
            lock (_gate)
            {
                if (manifest == null || !manifest.EnableRoleRouting)
                {
                    _useManifestRouting = false;
                    _byProfileId.Clear();
                    _contextByProfileId.Clear();
                    _routes.Clear();
                    return;
                }

                _useManifestRouting = true;
                Dictionary<string, ILlmClient> newClients = new(StringComparer.Ordinal);
                Dictionary<string, int> newContexts = new(StringComparer.Ordinal);
                foreach (LlmBackendProfileEntry p in manifest.Profiles)
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
                        newContexts[id] = p.contextWindowTokens < 256 ? 8192 : p.contextWindowTokens;
                    }
                }

                _byProfileId = newClients;
                _contextByProfileId = newContexts;
                _routes = manifest.Routes
                    .Where(r => r != null && !string.IsNullOrWhiteSpace(r.profileId) &&
                                !string.IsNullOrWhiteSpace(r.rolePattern))
                    .Select(r => (Pattern: r.rolePattern.Trim(), ProfileId: r.profileId.Trim(), Order: r.sortOrder))
                    .OrderBy(t => t.Order)
                    .ThenBy(t => t.Pattern == "*" ? 1 : 0)
                    .Select(t => (t.Pattern, t.ProfileId, t.Order))
                    .ToList();
            }
        }

        /// <inheritdoc />
        public ILlmClient ResolveClientForRole(string roleId)
        {
            string role = string.IsNullOrWhiteSpace(roleId) ? BuiltInAgentRoleIds.Creator : roleId.Trim();
            lock (_gate)
            {
                if (!_useManifestRouting || _routes.Count == 0 || _byProfileId.Count == 0)
                {
                    return _legacyFallback;
                }

                foreach ((string pattern, string profileId, int _) in _routes)
                {
                    if (!RoleMatches(pattern, role))
                    {
                        continue;
                    }

                    if (_byProfileId.TryGetValue(profileId, out ILlmClient client))
                    {
                        return client;
                    }
                }

                return _legacyFallback;
            }
        }

        public int ResolveContextWindowForRole(string roleId)
        {
            string role = string.IsNullOrWhiteSpace(roleId) ? BuiltInAgentRoleIds.Creator : roleId.Trim();
            lock (_gate)
            {
                if (!_useManifestRouting || _routes.Count == 0 || _contextByProfileId.Count == 0)
                {
                    return 8192;
                }

                foreach ((string pattern, string profileId, int _) in _routes)
                {
                    if (!RoleMatches(pattern, role))
                    {
                        continue;
                    }

                    if (_contextByProfileId.TryGetValue(profileId, out int ctx))
                    {
                        return ctx < 256 ? 8192 : ctx;
                    }
                }

                return 8192;
            }
        }

        private static bool RoleMatches(string pattern, string roleId)
        {
            if (pattern == "*")
            {
                return true;
            }

            return string.Equals(pattern, roleId, StringComparison.Ordinal);
        }

        private ILlmClient BuildProfileClient(LlmBackendProfileEntry p)
        {
            switch (p.kind)
            {
                case LlmBackendKind.Stub:
                    return new StubLlmClient();
                case LlmBackendKind.OpenAiHttp:
#if COREAI_NO_LLM
                    return new StubLlmClient();
#else
                    if (p.httpSettings == null || !p.httpSettings.UseOpenAiCompatibleHttp)
                    {
                        _logger.LogWarning(
                            GameLogFeature.Llm,
                            $"LlmClientRegistry: профиль '{p.profileId}' OpenAiHttp без валидного httpSettings.");
                        return new StubLlmClient();
                    }

                    return new OpenAiChatLlmClient(p.httpSettings, _settings, _logger, _memoryStore);
#endif
                case LlmBackendKind.LlmUnity:
#if COREAI_NO_LLM || UNITY_WEBGL
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

                    return new MeaiLlmUnityClient(agent, _settings, _logger, _memoryStore);
#endif
                default:
                    return new StubLlmClient();
            }
        }
    }
}
