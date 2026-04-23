using System;
using System.Collections.Generic;
using CoreAI.Session;
using System.Text;

namespace CoreAI.Ai
{
    /// <summary>
    /// Сборка системного промпта через <see cref="IAgentSystemPromptProvider"/> и user через шаблон или дефолт.
    /// 3-слойная архитектура системного промпта:
    ///   Слой 1: universalSystemPromptPrefix (общие правила для ВСЕХ агентов)
    ///   Слой 2: базовый промпт из Manifest/Resources (.txt файлы)
    ///   Слой 3: дополнительный промпт из AgentBuilder (через AgentMemoryPolicy)
    /// </summary>
    public sealed class AiPromptComposer
    {
        private readonly IAgentSystemPromptProvider _systemPrompts;
        private readonly IAgentUserPromptTemplateProvider _userTemplates;
        private readonly ILuaScriptVersionStore _luaScriptVersions;
        private readonly IDataOverlayVersionStore _dataOverlayVersions;
        private readonly AgentMemoryPolicy _memoryPolicy;
        private readonly ICoreAISettings _settings;

        /// <summary>Создаёт композер с цепочкой провайдеров промптов из DI.</summary>
        public AiPromptComposer(
            IAgentSystemPromptProvider systemPrompts,
            IAgentUserPromptTemplateProvider userTemplates,
            ILuaScriptVersionStore luaScriptVersions,
            IDataOverlayVersionStore dataOverlayVersions = null,
            AgentMemoryPolicy memoryPolicy = null,
            ICoreAISettings settings = null)
        {
            _systemPrompts = systemPrompts;
            _userTemplates = userTemplates;
            _luaScriptVersions = luaScriptVersions ?? new NullLuaScriptVersionStore();
            _dataOverlayVersions = dataOverlayVersions ?? new NullDataOverlayVersionStore();
            _memoryPolicy = memoryPolicy;
            _settings = settings;
        }

        /// <summary>
        /// Системный промпт для роли — 3-слойная сборка:
        ///   Слой 1: universalSystemPromptPrefix
        ///   Слой 2: манифест / Resources / встроенный fallback
        ///   Слой 3: дополнительный промпт из AgentBuilder
        /// </summary>
        public string GetSystemPrompt(string roleId)
        {
            // Проверяем, переопределяет ли роль universalPrefix
            bool skipPrefix = _memoryPolicy != null &&
                              _memoryPolicy.IsUniversalPrefixOverridden(roleId);

            // Слой 1: универсальный префикс из настроек (если не переопределён)
            string prefix = skipPrefix
                ? ""
                : (_settings?.UniversalSystemPromptPrefix ?? CoreAISettings.UniversalSystemPromptPrefix);

            // Слой 2: базовый промпт из провайдера (Manifest → Resources → fallback)
            string basePrompt;
            if (_systemPrompts.TryGetSystemPrompt(roleId, out string s) && !string.IsNullOrWhiteSpace(s))
            {
                basePrompt = s.Trim();
            }
            else
            {
                basePrompt = $"You are agent \"{roleId}\".";
            }

            // Слой 3: дополнительный промпт из AgentBuilder (через AgentMemoryPolicy)
            string additional = "";
            if (_memoryPolicy != null &&
                _memoryPolicy.TryGetAdditionalSystemPrompt(roleId, out string extra) &&
                !string.IsNullOrWhiteSpace(extra))
            {
                additional = extra.Trim();
            }

            // Сборка: prefix + base + additional
            System.Text.StringBuilder sb = new();
            if (!string.IsNullOrWhiteSpace(prefix))
            {
                sb.Append(prefix.TrimEnd());
                sb.Append('\n');
            }
            sb.Append(basePrompt);
            if (!string.IsNullOrEmpty(additional))
            {
                sb.Append("\n\n");
                sb.Append(additional);
            }

            return sb.ToString();
        }

        /// <summary>User-часть для LLM: шаблон роли (<c>{telemetry}</c>, <c>{hint}</c>, <c>{ключ}</c> из телеметрии) или JSON по умолчанию, плюс контекст ремонта Lua.</summary>
        public string BuildUserPayload(GameSessionSnapshot snap, AiTaskRequest task)
        {
            string roleId = task.RoleId ?? BuiltInAgentRoleIds.Creator;
            string body;
            if (_userTemplates.TryGetUserTemplate(roleId, out string tmpl))
            {
                // Шаблоны игронезависимы: подставляем {telemetry} (JSON-объект), {hint} и любые {ключ} из снимка телеметрии.
                string telemetryJson = BuildTelemetryJsonObject(snap);
                body = tmpl
                    .Replace("{telemetry}", telemetryJson)
                    .Replace("{hint}", task.Hint ?? "")
                    .Replace("{source_tag}", task.SourceTag ?? "");
                foreach (KeyValuePair<string, string> kv in snap.Telemetry)
                {
                    if (string.IsNullOrEmpty(kv.Key))
                    {
                        continue;
                    }

                    body = body.Replace("{" + kv.Key + "}", kv.Value ?? "");
                }
            }
            else
            {
                body = BuildDefaultJsonPayload(snap, task);
            }

            body = AppendMutationStateContext(body, roleId, task);
            return AppendLuaRepairContext(body, task);
        }

        private string AppendMutationStateContext(string body, string roleId, AiTaskRequest task)
        {
            if (!string.Equals(roleId, BuiltInAgentRoleIds.Programmer, StringComparison.Ordinal))
            {
                return body;
            }

            bool hasLua = _luaScriptVersions != null && !string.IsNullOrWhiteSpace(task.LuaScriptVersionKey);
            List<string> dataKeys = CollectVersionKeys(task.DataOverlayVersionKeysCsv);
            bool hasData = _dataOverlayVersions != null && dataKeys.Count > 0;
            if (!hasLua && !hasData)
            {
                return body;
            }

            LuaScriptVersionRecord luaSnapshot = null;
            if (hasLua)
            {
                _luaScriptVersions.TryGetSnapshot(task.LuaScriptVersionKey, out luaSnapshot);
            }

            List<DataOverlayVersionRecord> dataSnaps = null;
            if (hasData)
            {
                dataSnaps = new List<DataOverlayVersionRecord>(dataKeys.Count);
                for (int i = 0; i < dataKeys.Count; i++)
                {
                    _dataOverlayVersions.TryGetSnapshot(dataKeys[i], out DataOverlayVersionRecord s);
                    dataSnaps.Add(s);
                }
            }

            string section =
                MutationStatePromptFormatter.Format(task.LuaScriptVersionKey, luaSnapshot, dataKeys, dataSnaps);
            return string.IsNullOrEmpty(section) ? body : body + "\n\n" + section;
        }

        private static List<string> CollectVersionKeys(string csv)
        {
            List<string> list = new();
            if (string.IsNullOrWhiteSpace(csv))
            {
                return list;
            }

            string[] parts = csv.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < parts.Length; i++)
            {
                string k = parts[i].Trim();
                if (k.Length > 0)
                {
                    list.Add(k);
                }
            }

            return list;
        }

        private static string BuildTelemetryJsonObject(GameSessionSnapshot snap)
        {
            StringBuilder sb = new(256);
            sb.Append('{');
            bool first = true;
            foreach (KeyValuePair<string, string> kv in snap.Telemetry)
            {
                if (!first)
                {
                    sb.Append(',');
                }

                first = false;
                sb.Append('\"').Append(EscapeJson(kv.Key)).Append("\":\"").Append(EscapeJson(kv.Value)).Append('\"');
            }

            sb.Append('}');
            return sb.ToString();
        }

        private static string BuildDefaultJsonPayload(GameSessionSnapshot snap, AiTaskRequest task)
        {
            // Игронезависимый payload: игра сама выбирает ключи телеметрии и обновляет их в Core;
            // Core только хранит и отдаёт их модели как JSON-словарь.
            StringBuilder sb = new(256);
            sb.Append('{');
            sb.Append("\"telemetry\":");
            sb.Append(BuildTelemetryJsonObject(snap));
            sb.Append(',');
            sb.Append("\"hint\":\"").Append(EscapeJson(task.Hint ?? "")).Append("\",");
            sb.Append("\"ai_task_source\":\"").Append(EscapeJson(task.SourceTag ?? "")).Append("\"");
            sb.Append('}');
            return sb.ToString();
        }

        private static string EscapeJson(string s)
        {
            if (string.IsNullOrEmpty(s))
            {
                return "";
            }

            return s.Replace("\\", "\\\\").Replace("\"", "\\\"");
        }

        private static string AppendLuaRepairContext(string body, AiTaskRequest task)
        {
            if (string.IsNullOrEmpty(task.LuaRepairErrorMessage))
            {
                return body;
            }

            string err = ShortenForPrompt(task.LuaRepairErrorMessage, 500);
            string code = ShortenForPrompt(task.LuaRepairPreviousCode ?? "", 1200);
            // repair контекст оставляем строкой-хвостом (универсально и читаемо для LLM),
            // но он не содержит game-specific телеметрии.
            return $"{body}; lua_repair_generation={task.LuaRepairGeneration}; lua_error={err}; fix_this_lua={code}";
        }

        private static string ShortenForPrompt(string s, int max)
        {
            if (string.IsNullOrEmpty(s))
            {
                return "";
            }

            s = s.Replace('\r', ' ').Replace('\n', ' ');
            return s.Length <= max ? s : s.Substring(0, max) + "…";
        }
    }
}