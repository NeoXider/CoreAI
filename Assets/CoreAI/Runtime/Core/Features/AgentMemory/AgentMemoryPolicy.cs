using System;
using System.Collections.Generic;
using CoreAI.AgentMemory;

namespace CoreAI.Ai
{
    /// <summary>
    /// Политика включения памяти по ролям.
    /// По умолчанию память ВКЛЮЧЕНА для всех ролей.
    /// Поддерживает 2 типа памяти:
    /// 1) MemoryTool (function call) — явное сохранение через {"tool":"memory","action":"write/append/clear"}
    /// 2) Chat History — автоматическое сохранение всего диалога в контекст (работает как с локальными, так и с HTTP API моделями, сохраняется между сессиями!)
    /// </summary>
    public sealed class AgentMemoryPolicy
    {
        private readonly Dictionary<string, RoleMemoryConfig> _roleConfigs;
        private readonly Dictionary<string, List<ILlmTool>> _customTools = new();
        private static readonly MemoryLlmTool _memoryToolInstance = new();

        /// <summary>
        /// Установить произвольные инструменты для роли (добавляются к MemoryTool).
        /// </summary>
        public void SetToolsForRole(string roleId, IReadOnlyList<ILlmTool> tools)
        {
            if (tools == null || tools.Count == 0)
            {
                _customTools.Remove(roleId);
                return;
            }

            _customTools[roleId] = new List<ILlmTool>(tools);
        }

        /// <summary>Конфигурация памяти для одной роли.</summary>
        public struct RoleMemoryConfig
        {
            /// <summary>Включена ли память через MemoryTool (function call).</summary>
            public bool UseMemoryTool;

            /// <summary>Действие по умолчанию: write (перезаписать) или append (дополнить).</summary>
            public MemoryToolAction DefaultAction;

            /// <summary>Разрешать ли дублированные вызовы инструментов (переопределяет глобальную настройку, если не null).</summary>
            public bool? AllowDuplicateToolCalls;

            /// <summary>Сохранять и использовать ли историю чата в контексте (Role: user/assistant).</summary>
            public bool WithChatHistory;

            /// <summary>Сохранять ли историю чата между сессиями (на диск).</summary>
            public bool PersistChatHistory;

            /// <summary>Бюджет токенов (опционально, если ChatHistory активна).</summary>
            public int ContextTokens;

            /// <summary>Максимальное количество сообщений для сохранения и отправки в модель.</summary>
            public int MaxChatHistoryMessages;

            /// <summary>Per-role LLM response token cap; null = use per-call/global/provider fallback.</summary>
            public int? MaxOutputTokens;

            public RoleMemoryConfig(bool useMemoryTool = true, MemoryToolAction defaultAction = MemoryToolAction.Append,
                bool withChatHistory = false, bool persistChatHistory = false, int contextTokens = 8192, bool? allowDuplicateToolCalls = null, int maxChatHistoryMessages = 30, int? maxOutputTokens = null)
            {
                UseMemoryTool = useMemoryTool;
                DefaultAction = defaultAction;
                WithChatHistory = withChatHistory;
                PersistChatHistory = persistChatHistory;
                ContextTokens = contextTokens;
                AllowDuplicateToolCalls = allowDuplicateToolCalls;
                MaxChatHistoryMessages = maxChatHistoryMessages;
                MaxOutputTokens = maxOutputTokens;
            }
        }

        public void ConfigureChatHistory(string roleId, bool enabled, int tokens, bool persist, int maxChatHistoryMessages = 30)
        {
            if (!_roleConfigs.TryGetValue(roleId, out RoleMemoryConfig c))
            {
                c = new RoleMemoryConfig();
            }

            c.WithChatHistory = enabled;
            c.ContextTokens = tokens;
            c.PersistChatHistory = persist;
            c.MaxChatHistoryMessages = maxChatHistoryMessages;
            _roleConfigs[roleId] = c;
        }


        public AgentMemoryPolicy()
        {
            _roleConfigs = new Dictionary<string, RoleMemoryConfig>();

            // По умолчанию: агентные роли используют MemoryTool с append.
            foreach (string roleId in BuiltInAgentRoleIds.AllBuiltInRoles)
            {
                _roleConfigs[roleId] = new RoleMemoryConfig(
                    true,
                    MemoryToolAction.Append);
            }

            // PlayerChat is the drop-in chat panel role. It should restore the visible conversation
            // after restart by default, while long-term facts still belong to explicit MemoryTool roles.
            _roleConfigs[BuiltInAgentRoleIds.PlayerChat] = new RoleMemoryConfig(
                useMemoryTool: false,
                withChatHistory: true,
                persistChatHistory: true);
        }

        /// <summary>
        /// Включена ли для роли подстановка и сохранение блоков памяти в ответах LLM.
        /// По умолчанию ВКЛЮЧЕНА для всех ролей.
        /// </summary>
        public bool IsMemoryEnabled(string roleId)
        {
            if (string.IsNullOrWhiteSpace(roleId))
            {
                roleId = BuiltInAgentRoleIds.Creator;
            }

            roleId = roleId.Trim();

            if (_roleConfigs.TryGetValue(roleId, out RoleMemoryConfig config))
            {
                return config.UseMemoryTool;
            }

            // Неизвестная роль — тоже включаем по умолчанию
            return true;
        }

        /// <summary>
        /// Получить конфигурацию MemoryTool для роли.
        /// </summary>
        public RoleMemoryConfig GetRoleConfig(string roleId)
        {
            if (string.IsNullOrWhiteSpace(roleId))
            {
                roleId = BuiltInAgentRoleIds.Creator;
            }

            roleId = roleId.Trim();

            if (_roleConfigs.TryGetValue(roleId, out RoleMemoryConfig config))
            {
                return config;
            }

            return new RoleMemoryConfig(true, MemoryToolAction.Append);
        }

        /// <summary>
        /// Настроить память для роли.
        /// </summary>
        public void ConfigureRole(
            string roleId,
            bool? useMemoryTool = null,
            MemoryToolAction? defaultAction = null,
            bool? allowDuplicateToolCalls = null)
        {
            if (string.IsNullOrWhiteSpace(roleId))
            {
                return;
            }

            roleId = roleId.Trim();

            RoleMemoryConfig existing = GetRoleConfig(roleId);

            _roleConfigs[roleId] = new RoleMemoryConfig
            {
                UseMemoryTool = useMemoryTool ?? existing.UseMemoryTool,
                DefaultAction = defaultAction ?? existing.DefaultAction,
                AllowDuplicateToolCalls = allowDuplicateToolCalls ?? existing.AllowDuplicateToolCalls,
                WithChatHistory = existing.WithChatHistory,
                PersistChatHistory = existing.PersistChatHistory,
                ContextTokens = existing.ContextTokens,
                MaxChatHistoryMessages = existing.MaxChatHistoryMessages,
                MaxOutputTokens = existing.MaxOutputTokens
            };
        }

        /// <summary>
        /// Set a per-role LLM response token cap. Null or non-positive values clear the override.
        /// </summary>
        public void SetMaxOutputTokens(string roleId, int? maxOutputTokens)
        {
            if (string.IsNullOrWhiteSpace(roleId))
            {
                return;
            }

            roleId = roleId.Trim();
            RoleMemoryConfig existing = GetRoleConfig(roleId);
            existing.MaxOutputTokens = maxOutputTokens.HasValue && maxOutputTokens.Value > 0
                ? maxOutputTokens.Value
                : null;
            _roleConfigs[roleId] = existing;
        }

        /// <summary>
        /// Включить MemoryTool для роли.
        /// </summary>
        public void EnableMemoryTool(string roleId)
        {
            ConfigureRole(roleId, true);
        }

        /// <summary>
        /// Выключить MemoryTool для роли.
        /// </summary>
        public void DisableMemoryTool(string roleId)
        {
            ConfigureRole(roleId, false);
        }

        /// <summary>
        /// Включить/выключить MemoryTool для всех ролей.
        /// </summary>
        public void SetMemoryToolForAll(bool enabled)
        {
            foreach (string roleId in BuiltInAgentRoleIds.AllBuiltInRoles)
            {
                ConfigureRole(roleId, enabled);
            }
        }

        /// <summary>
        /// Нужно ли подставлять память в системный промпт для этой роли.
        /// </summary>
        public bool ShouldInjectMemory(string roleId)
        {
            return IsMemoryEnabled(roleId);
        }

        /// <summary>
        /// Получить список инструментов (tools) для роли.
        /// Включает MemoryTool если память включена для роли + любые кастомные инструменты.
        /// </summary>
        public IReadOnlyList<ILlmTool> GetToolsForRole(string roleId)
        {
            List<ILlmTool> tools = new();

            // Добавляем MemoryTool если включён
            if (IsMemoryEnabled(roleId))
            {
                tools.Add(_memoryToolInstance);
            }

            // Добавляем кастомные инструменты для роли
            if (_customTools.TryGetValue(roleId, out List<ILlmTool> custom))
            {
                tools.AddRange(custom);
            }

            return tools.Count > 0 ? tools : Array.Empty<ILlmTool>();
        }

        // ===== Дополнительные системные промпты (слой 3: AgentBuilder) =====
        private readonly Dictionary<string, string> _additionalSystemPrompts = new();

        // ===== Override Universal Prefix (per-role) =====
        private readonly HashSet<string> _overrideUniversalPrefix = new();

        // ===== Streaming override (per-role) =====
        private readonly Dictionary<string, bool> _streamingOverrides = new();

        /// <summary>
        /// Установить дополнительный системный промпт для роли (из AgentBuilder).
        /// Дополняется к базовому промпту из ManifestProvider/Resources.
        /// </summary>
        public void SetAdditionalSystemPrompt(string roleId, string prompt)
        {
            if (string.IsNullOrWhiteSpace(roleId)) return;
            roleId = roleId.Trim();

            if (string.IsNullOrWhiteSpace(prompt))
            {
                _additionalSystemPrompts.Remove(roleId);
            }
            else
            {
                _additionalSystemPrompts[roleId] = prompt.Trim();
            }
        }

        /// <summary>
        /// Получить дополнительный системный промпт для роли (установленный через AgentBuilder).
        /// </summary>
        public bool TryGetAdditionalSystemPrompt(string roleId, out string prompt)
        {
            prompt = null;
            if (string.IsNullOrWhiteSpace(roleId)) return false;
            return _additionalSystemPrompts.TryGetValue(roleId.Trim(), out prompt);
        }

        /// <summary>
        /// Пометить роль как «переопределяющую universalPrefix».
        /// Если true — universalPrefix из CoreAISettings НЕ будет применяться к этой роли.
        /// Полезно когда нужен полностью кастомный системный промпт.
        /// </summary>
        public void SetOverrideUniversalPrefix(string roleId, bool shouldOverride)
        {
            if (string.IsNullOrWhiteSpace(roleId)) return;
            roleId = roleId.Trim();

            if (shouldOverride)
                _overrideUniversalPrefix.Add(roleId);
            else
                _overrideUniversalPrefix.Remove(roleId);
        }

        /// <summary>
        /// Проверить, переопределяет ли роль universalPrefix.
        /// </summary>
        public bool IsUniversalPrefixOverridden(string roleId)
        {
            if (string.IsNullOrWhiteSpace(roleId)) return false;
            return _overrideUniversalPrefix.Contains(roleId.Trim());
        }

        // ===== Streaming (per-role override) =====

        /// <summary>
        /// Задать per-role переопределение флага стриминга.
        /// <paramref name="enabled"/> = null сбрасывает override — будет использован глобальный
        /// <see cref="ICoreAISettings.EnableStreaming"/>.
        /// </summary>
        public void SetStreamingEnabled(string roleId, bool? enabled)
        {
            if (string.IsNullOrWhiteSpace(roleId)) return;
            roleId = roleId.Trim();

            if (enabled.HasValue)
            {
                _streamingOverrides[roleId] = enabled.Value;
            }
            else
            {
                _streamingOverrides.Remove(roleId);
            }
        }

        /// <summary>
        /// Получить per-role override флага стриминга, если он был задан.
        /// </summary>
        public bool TryGetStreamingOverride(string roleId, out bool enabled)
        {
            enabled = false;
            if (string.IsNullOrWhiteSpace(roleId)) return false;
            return _streamingOverrides.TryGetValue(roleId.Trim(), out enabled);
        }

        /// <summary>
        /// Вычислить эффективный флаг стриминга для роли.
        /// Порядок приоритета: per-role override → <paramref name="globalFallback"/> →
        /// <see cref="CoreAISettings.EnableStreaming"/> (глобальный статический прокси).
        /// </summary>
        public bool IsStreamingEnabled(string roleId, ICoreAISettings globalFallback = null)
        {
            if (TryGetStreamingOverride(roleId, out bool overriden))
            {
                return overriden;
            }

            if (globalFallback != null)
            {
                return globalFallback.EnableStreaming;
            }

            return CoreAISettings.EnableStreaming;
        }
    }
}