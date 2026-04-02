using System;
using System.Collections.Generic;
using System.Linq;
using CoreAI.Ai;
using LLMUnity;
using UnityEngine;

namespace CoreAI.Infrastructure.Llm
{
    /// <summary>
    /// Кэш <see cref="ILlmClient"/> по profileId + матчинг ролей по <see cref="LlmRoutingManifest"/>.
    /// </summary>
    public sealed class LlmClientRegistry : ILlmClientRegistry, ILlmRoutingController
    {
        private readonly object _gate = new object();
        private ILlmClient _legacyFallback = new StubLlmClient();
        private Dictionary<string, ILlmClient> _byProfileId = new Dictionary<string, ILlmClient>(StringComparer.Ordinal);
        private List<(string pattern, string profileId, int order)> _routes = new List<(string, string, int)>();
        private bool _useManifestRouting;

        /// <summary>Клиент по умолчанию, если маршрутизация выключена или нет совпадения.</summary>
        public void SetLegacyFallback(ILlmClient legacy) =>
            _legacyFallback = legacy ?? new StubLlmClient();

        /// <inheritdoc />
        public void ApplyManifest(LlmRoutingManifest manifest)
        {
            lock (_gate)
            {
                if (manifest == null || !manifest.EnableRoleRouting)
                {
                    _useManifestRouting = false;
                    _byProfileId.Clear();
                    _routes.Clear();
                    return;
                }

                _useManifestRouting = true;
                var newClients = new Dictionary<string, ILlmClient>(StringComparer.Ordinal);
                foreach (var p in manifest.Profiles)
                {
                    if (string.IsNullOrWhiteSpace(p?.profileId))
                        continue;
                    var id = p.profileId.Trim();
                    if (newClients.ContainsKey(id))
                        continue;
                    var c = BuildProfileClient(p);
                    if (c != null)
                        newClients[id] = c;
                }

                _byProfileId = newClients;
                _routes = manifest.Routes
                    .Where(r => r != null && !string.IsNullOrWhiteSpace(r.profileId) && !string.IsNullOrWhiteSpace(r.rolePattern))
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
            var role = string.IsNullOrWhiteSpace(roleId) ? BuiltInAgentRoleIds.Creator : roleId.Trim();
            lock (_gate)
            {
                if (!_useManifestRouting || _routes.Count == 0 || _byProfileId.Count == 0)
                    return _legacyFallback;

                foreach (var (pattern, profileId, _) in _routes)
                {
                    if (!RoleMatches(pattern, role))
                        continue;
                    if (_byProfileId.TryGetValue(profileId, out var client))
                        return client;
                }

                return _legacyFallback;
            }
        }

        private static bool RoleMatches(string pattern, string roleId)
        {
            if (pattern == "*")
                return true;
            return string.Equals(pattern, roleId, StringComparison.Ordinal);
        }

        private static ILlmClient BuildProfileClient(LlmBackendProfileEntry p)
        {
            switch (p.kind)
            {
                case LlmBackendKind.Stub:
                    return new StubLlmClient();
                case LlmBackendKind.OpenAiHttp:
                    if (p.httpSettings == null || !p.httpSettings.UseOpenAiCompatibleHttp)
                    {
                        Debug.LogWarning($"[CoreAI] LlmClientRegistry: профиль '{p.profileId}' OpenAiHttp без валидного httpSettings.");
                        return new StubLlmClient();
                    }

                    return new OpenAiChatLlmClient(p.httpSettings);
                case LlmBackendKind.LlmUnity:
#if COREAI_NO_LLM
                    return new StubLlmClient();
#else
                    LLMAgent agent = null;
                    if (!string.IsNullOrWhiteSpace(p.unityAgentGameObjectName))
                    {
                        var go = GameObject.Find(p.unityAgentGameObjectName);
                        if (go != null)
                            agent = go.GetComponent<LLMAgent>();
                    }

                    agent ??= UnityEngine.Object.FindFirstObjectByType<LLMAgent>();
                    if (agent == null)
                        return new StubLlmClient();
                    var llm = agent.GetComponent<LLM>();
                    if (llm != null)
                        LlmUnityModelBootstrap.TryAutoAssignResolvableModel(llm);
                    if (llm != null && string.IsNullOrWhiteSpace(llm.model))
                        return new StubLlmClient();
                    return new LlmUnityLlmClient(agent);
#endif
                default:
                    return new StubLlmClient();
            }
        }
    }
}
