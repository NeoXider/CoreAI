using System;
using System.Collections.Generic;
using UnityEngine;

namespace CoreAI.Infrastructure.Llm
{
    /// <summary>
    /// Правило: роль (или *) → id профиля из списка <see cref="profiles"/>.
    /// </summary>
    [Serializable]
    public sealed class LlmRoleRouteEntry
    {
        /// <summary>Точное совпадение с RoleId или <c>*</c> для любой роли (wildcard обычно последним).</summary>
        [Tooltip("Точное совпадение с RoleId, либо * для любой роли (обычно последним в списке).")]
        public string rolePattern = "Creator";

        /// <summary>Идентификатор профиля из списка профилей манифеста.</summary>
        [Tooltip("Id из записи в profiles.")]
        public string profileId = "default";

        /// <summary>Порядок сортировки: меньше — выше при равных условиях.</summary>
        [Tooltip("Меньше — раньше проверяется (среди совпадений порядок в списке тоже важен).")]
        public int sortOrder;
    }

    /// <summary>
    /// Один именованный бэкенд (HTTP / LLMUnity / stub).
    /// </summary>
    [Serializable]
    public sealed class LlmBackendProfileEntry
    {
        /// <summary>Уникальный id профиля для ссылок из маршрутов.</summary>
        [Tooltip("Уникальный id, на который ссылаются routes.")]
        public string profileId = "default";

        /// <summary>Какой бэкенд создать для этого профиля.</summary>
        public LlmBackendKind kind = LlmBackendKind.LlmUnity;

        /// <summary>Для <see cref="LlmBackendKind.OpenAiHttp"/>: настройки HTTP (должен быть включён флаг Use Open Ai Compatible Http).</summary>
        [Tooltip("Для OpenAiHttp: asset с Use Open Ai Compatible Http = true.")]
        public OpenAiHttpLlmSettings httpSettings;

        /// <summary>Для <see cref="LlmBackendKind.LlmUnity"/>: имя GameObject на сцене с <c>LLMAgent</c> (пусто — первый по типу).</summary>
        [Tooltip("Для LlmUnity: имя GameObject с LLMAgent (пусто = первый FindFirstObjectByType).")]
        public string unityAgentGameObjectName = "";
    }

    /// <summary>
    /// Маршрутизация <see cref="ILlmClient"/> по <see cref="CoreAI.Ai.LlmCompletionRequest.AgentRoleId"/>.
    /// </summary>
    [CreateAssetMenu(menuName = "CoreAI/LLM/Llm Routing Manifest", fileName = "LlmRoutingManifest")]
    public sealed class LlmRoutingManifest : ScriptableObject
    {
        [Tooltip("Если выключено — CoreAILifetimeScope использует только legacy Open Ai Http + LLMUnity без таблицы.")]
        [SerializeField]
        private bool enableRoleRouting = true;

        [SerializeField]
        private List<LlmBackendProfileEntry> profiles = new List<LlmBackendProfileEntry>();

        [SerializeField]
        private List<LlmRoleRouteEntry> routes = new List<LlmRoleRouteEntry>();

        /// <summary>Включена ли таблица маршрутизации (иначе используется legacy-клиент из scope).</summary>
        public bool EnableRoleRouting => enableRoleRouting;

        /// <summary>Список именованных бэкендов.</summary>
        public IReadOnlyList<LlmBackendProfileEntry> Profiles => profiles;

        /// <summary>Правила «роль → профиль».</summary>
        public IReadOnlyList<LlmRoleRouteEntry> Routes => routes;
    }
}
