using System;
using System.Collections.Generic;
using CoreAI.Ai;
using UnityEngine;

namespace CoreAI.Infrastructure.Llm
{
    /// <summary>
    /// Maps an agent role pattern to a backend profile id.
    /// </summary>
    [Serializable]
    public sealed class LlmRoleRouteEntry
    {
        /// <summary>Exact role id match, or <c>*</c> for any role.</summary>
        [Tooltip("Exact RoleId match, or * for any role. Keep wildcard routes last.")]
        public string rolePattern = "Creator";

        /// <summary>Profile id from the manifest profile list.</summary>
        [Tooltip("Unique profile id referenced by routes.")]
        public string profileId = "default";

        /// <summary>Sort order; lower values are evaluated first.</summary>
        [Tooltip("Lower values are checked first; list order breaks ties.")]
        public int sortOrder;
    }

    /// <summary>
    /// Named LLM backend profile used by role routing.
    /// </summary>
    [Serializable]
    public sealed class LlmBackendProfileEntry
    {
        /// <summary>Unique profile id referenced by route entries.</summary>
        [Tooltip("Unique profile id referenced by routes.")]
        public string profileId = "default";

        /// <summary>Legacy backend kind used by existing assets.</summary>
        public LlmBackendKind kind = LlmBackendKind.LlmUnity;

        /// <summary>Product-facing execution mode for this profile.</summary>
        public LlmExecutionMode executionMode = LlmExecutionMode.Auto;

        /// <summary>For HTTP-backed modes: profile-specific OpenAI-compatible settings.</summary>
        [Tooltip("OpenAI-compatible HTTP settings asset used by OpenAiHttp profiles.")]
        public OpenAiHttpLlmSettings httpSettings;

        /// <summary>For local model mode: GameObject name with <c>LLMAgent</c>; empty selects the first available agent.</summary>
        [Tooltip("GameObject name with LLMAgent for LlmUnity profiles. Empty uses the first LLMAgent found.")]
        public string unityAgentGameObjectName = "";

        /// <summary>Maximum LLM requests allowed by this profile in the current session; zero disables the profile limit.</summary>
        [Min(0)]
        [Tooltip("ClientLimited maximum requests in the current session. Zero disables the limit.")]
        public int maxRequestsPerSession;

        /// <summary>Maximum prompt characters allowed by this profile; zero disables the profile limit.</summary>
        [Min(0)]
        [Tooltip("ClientLimited maximum prompt characters per request. Zero disables the limit.")]
        public int maxPromptChars;

        /// <summary>Context window in tokens for requests routed to this profile.</summary>
        [Min(256)]
        [Tooltip("Context window size in tokens for this profile. Default is 128K.")]
        public int contextWindowTokens = CoreAISettings.DefaultContextWindowTokens;
    }

    /// <summary>
    /// Routes <see cref="ILlmClient"/> requests by <see cref="LlmCompletionRequest.AgentRoleId"/>.
    /// </summary>
    [CreateAssetMenu(menuName = "CoreAI/LLM/Llm Routing Manifest", fileName = "LlmRoutingManifest")]
    public sealed class LlmRoutingManifest : ScriptableObject
    {
        [Tooltip("When disabled, CoreAILifetimeScope uses only legacy OpenAI HTTP plus LLMUnity routing.")]
        [SerializeField]
        private bool enableRoleRouting = true;

        [SerializeField]
        private List<LlmBackendProfileEntry> profiles = new();

        [SerializeField]
        private List<LlmRoleRouteEntry> routes = new();

        /// <summary>Whether role routing is enabled; otherwise the lifetime scope fallback client is used.</summary>
        public bool EnableRoleRouting => enableRoleRouting;

        /// <summary>Named backend profiles available to route entries.</summary>
        public IReadOnlyList<LlmBackendProfileEntry> Profiles => profiles;

        /// <summary>Role-to-profile routing rules.</summary>
        public IReadOnlyList<LlmRoleRouteEntry> Routes => routes;

        /// <summary>Alias for <see cref="ToRouteTable"/> to match the Options + SO wrapper rule.</summary>
        public LlmRouteTable ToOptions()
        {
            return ToRouteTable();
        }

        /// <summary>Converts this Unity asset to the portable CoreAI route table.</summary>
        public LlmRouteTable ToRouteTable()
        {
            List<LlmRouteProfile> portableProfiles = new();
            foreach (LlmBackendProfileEntry profile in profiles)
            {
                if (profile == null || string.IsNullOrWhiteSpace(profile.profileId))
                {
                    continue;
                }

                portableProfiles.Add(new LlmRouteProfile
                {
                    ProfileId = profile.profileId.Trim(),
                    Mode = ResolveProfileMode(profile),
                    Model = profile.httpSettings != null
                        ? profile.httpSettings.Model
                        : profile.unityAgentGameObjectName ?? "",
                    ContextWindowTokens = profile.contextWindowTokens < 256
                        ? CoreAISettings.DefaultContextWindowTokens
                        : profile.contextWindowTokens,
                    Capabilities = new[] { "chat", "streaming", "tools" }
                });
            }

            List<LlmRouteRule> portableRules = new();
            foreach (LlmRoleRouteEntry route in routes)
            {
                if (route == null ||
                    string.IsNullOrWhiteSpace(route.rolePattern) ||
                    string.IsNullOrWhiteSpace(route.profileId))
                {
                    continue;
                }

                portableRules.Add(new LlmRouteRule
                {
                    RolePattern = route.rolePattern.Trim(),
                    ProfileId = route.profileId.Trim(),
                    SortOrder = route.sortOrder
                });
            }

            return new LlmRouteTable
            {
                Profiles = portableProfiles,
                Rules = portableRules
            };
        }

        private static LlmExecutionMode ResolveProfileMode(LlmBackendProfileEntry profile)
        {
            if (profile.executionMode != LlmExecutionMode.Auto)
            {
                return profile.executionMode;
            }

            if (profile.httpSettings != null && profile.httpSettings.ExecutionMode != LlmExecutionMode.ClientOwnedApi)
            {
                return profile.httpSettings.ExecutionMode;
            }

            switch (profile.kind)
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
