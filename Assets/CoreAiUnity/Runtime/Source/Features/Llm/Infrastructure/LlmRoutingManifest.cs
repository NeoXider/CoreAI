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
        [Tooltip("Точное совпадение с RoleId, либо * для любой роли (обычно последним в списке).")]
        public string rolePattern = "Creator";

        /// <summary>Profile id from the manifest profile list.</summary>
        [Tooltip("Id из записи в profiles.")] public string profileId = "default";

        /// <summary>Sort order; lower values are evaluated first.</summary>
        [Tooltip("Меньше — раньше проверяется (среди совпадений порядок в списке тоже важен).")]
        public int sortOrder;
    }

    /// <summary>
    /// Named LLM backend profile used by role routing.
    /// </summary>
    [Serializable]
    public sealed class LlmBackendProfileEntry
    {
        /// <summary>Unique profile id referenced by route entries.</summary>
        [Tooltip("Уникальный id, на который ссылаются routes.")]
        public string profileId = "default";

        /// <summary>Legacy backend kind used by existing assets.</summary>
        public LlmBackendKind kind = LlmBackendKind.LlmUnity;

        /// <summary>Product-facing execution mode for this profile.</summary>
        public LlmExecutionMode executionMode = LlmExecutionMode.Auto;

        /// <summary>For HTTP-backed modes: profile-specific OpenAI-compatible settings.</summary>
        [Tooltip("Для OpenAiHttp: asset с Use Open Ai Compatible Http = true.")]
        public OpenAiHttpLlmSettings httpSettings;

        /// <summary>For local model mode: GameObject name with <c>LLMAgent</c>; empty selects the first available agent.</summary>
        [Tooltip("Для LlmUnity: имя GameObject с LLMAgent (пусто = первый FindFirstObjectByType).")]
        public string unityAgentGameObjectName = "";

        /// <summary>Maximum LLM requests allowed by this profile in the current session; zero disables the profile limit.</summary>
        [Min(0)] [Tooltip("Для ClientLimited: максимум запросов в текущей сессии. 0 = без лимита.")]
        public int maxRequestsPerSession;

        /// <summary>Maximum prompt characters allowed by this profile; zero disables the profile limit.</summary>
        [Min(0)] [Tooltip("Для ClientLimited: максимум символов prompt на запрос. 0 = без лимита.")]
        public int maxPromptChars;

        /// <summary>Context window in tokens for requests routed to this profile.</summary>
        [Min(256)] [Tooltip("Контекстное окно (токены) для профиля. По умолчанию 8192.")]
        public int contextWindowTokens = 8192;
    }

    /// <summary>
    /// Routes <see cref="ILlmClient"/> requests by <see cref="LlmCompletionRequest.AgentRoleId"/>.
    /// </summary>
    [CreateAssetMenu(menuName = "CoreAI/LLM/Llm Routing Manifest", fileName = "LlmRoutingManifest")]
    public sealed class LlmRoutingManifest : ScriptableObject
    {
        [Tooltip("Если выключено — CoreAILifetimeScope использует только legacy Open Ai Http + LLMUnity без таблицы.")]
        [SerializeField]
        private bool enableRoleRouting = true;

        [SerializeField] private List<LlmBackendProfileEntry> profiles = new();

        [SerializeField] private List<LlmRoleRouteEntry> routes = new();

        /// <summary>Whether role routing is enabled; otherwise the lifetime scope fallback client is used.</summary>
        public bool EnableRoleRouting => enableRoleRouting;

        /// <summary>Named backend profiles available to route entries.</summary>
        public IReadOnlyList<LlmBackendProfileEntry> Profiles => profiles;

        /// <summary>Role-to-profile routing rules.</summary>
        public IReadOnlyList<LlmRoleRouteEntry> Routes => routes;
    }
}