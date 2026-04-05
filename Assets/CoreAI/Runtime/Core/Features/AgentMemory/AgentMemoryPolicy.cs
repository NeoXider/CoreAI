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
    /// 2) Chat History (LLMUnity) — автоматическое сохранение всего диалога в LLMAgent
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

            public RoleMemoryConfig(bool useMemoryTool = true, MemoryToolAction defaultAction = MemoryToolAction.Append)
            {
                UseMemoryTool = useMemoryTool;
                DefaultAction = defaultAction;
            }
        }

        public enum MemoryToolAction
        {
            /// <summary>Перезаписать всю память.</summary>
            Write,
            /// <summary>Дополнить существующую память.</summary>
            Append
        }

        public AgentMemoryPolicy()
        {
            _roleConfigs = new Dictionary<string, RoleMemoryConfig>();

            // По умолчанию: все роли используют MemoryTool с append
            foreach (string roleId in BuiltInAgentRoleIds.AllBuiltInRoles)
            {
                _roleConfigs[roleId] = new RoleMemoryConfig(
                    useMemoryTool: true,
                    defaultAction: MemoryToolAction.Append);
            }
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

            if (_roleConfigs.TryGetValue(roleId, out var config))
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

            if (_roleConfigs.TryGetValue(roleId, out var config))
            {
                return config;
            }

            return new RoleMemoryConfig(useMemoryTool: true, MemoryToolAction.Append);
        }

        /// <summary>
        /// Настроить память для роли.
        /// </summary>
        public void ConfigureRole(
            string roleId,
            bool? useMemoryTool = null,
            MemoryToolAction? defaultAction = null)
        {
            if (string.IsNullOrWhiteSpace(roleId)) return;

            roleId = roleId.Trim();

            var existing = GetRoleConfig(roleId);

            _roleConfigs[roleId] = new RoleMemoryConfig
            {
                UseMemoryTool = useMemoryTool ?? existing.UseMemoryTool,
                DefaultAction = defaultAction ?? existing.DefaultAction
            };
        }

        /// <summary>
        /// Включить MemoryTool для роли.
        /// </summary>
        public void EnableMemoryTool(string roleId)
        {
            ConfigureRole(roleId, useMemoryTool: true);
        }

        /// <summary>
        /// Выключить MemoryTool для роли.
        /// </summary>
        public void DisableMemoryTool(string roleId)
        {
            ConfigureRole(roleId, useMemoryTool: false);
        }

        /// <summary>
        /// Включить/выключить MemoryTool для всех ролей.
        /// </summary>
        public void SetMemoryToolForAll(bool enabled)
        {
            foreach (string roleId in BuiltInAgentRoleIds.AllBuiltInRoles)
            {
                ConfigureRole(roleId, useMemoryTool: enabled);
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
            var tools = new List<ILlmTool>();

            // Добавляем MemoryTool если включён
            if (IsMemoryEnabled(roleId))
            {
                tools.Add(_memoryToolInstance);
            }

            // Добавляем кастомные инструменты для роли
            if (_customTools.TryGetValue(roleId, out var custom))
            {
                tools.AddRange(custom);
            }

            return tools.Count > 0 ? tools : Array.Empty<ILlmTool>();
        }
    }
}
