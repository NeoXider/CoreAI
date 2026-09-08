using System;
using System.Collections.Generic;
using CoreAI.Session;
using System.Text;

namespace CoreAI.Ai
{
    /// <summary>
    /// Composes system and user prompts for AI task requests.
    /// </summary>
    public sealed class AiPromptComposer
    {
        private readonly IAgentSystemPromptProvider _systemPrompts;
        private readonly IAgentUserPromptTemplateProvider _userTemplates;
        private readonly ILuaScriptVersionStore _luaScriptVersions;
        private readonly IDataOverlayVersionStore _dataOverlayVersions;
        private readonly AgentMemoryPolicy _memoryPolicy;
        private readonly ICoreAISettings _settings;
        private readonly IEnumerable<IAiPromptContextProvider> _contextProviders;

        /// <summary>Initializes a new instance of AiPromptComposer.</summary>
        public AiPromptComposer(
            IAgentSystemPromptProvider systemPrompts,
            IAgentUserPromptTemplateProvider userTemplates,
            ILuaScriptVersionStore luaScriptVersions,
            IDataOverlayVersionStore dataOverlayVersions = null,
            AgentMemoryPolicy memoryPolicy = null,
            ICoreAISettings settings = null,
            IEnumerable<IAiPromptContextProvider> contextProviders = null)
        {
            _systemPrompts = systemPrompts;
            _userTemplates = userTemplates;
            _luaScriptVersions = luaScriptVersions ?? new NullLuaScriptVersionStore();
            _dataOverlayVersions = dataOverlayVersions ?? new NullDataOverlayVersionStore();
            _memoryPolicy = memoryPolicy;
            _settings = settings;
            _contextProviders = contextProviders ?? Array.Empty<IAiPromptContextProvider>();
        }

        /// <summary>
        /// Builds the final system prompt for a role from global prefix, role base prompt,
        /// and per-role prompt additions.
        /// </summary>
        public string GetSystemPrompt(string roleId, string overrideBasePrompt = null)
        {
            bool skipPrefix = _memoryPolicy != null &&
                              _memoryPolicy.IsUniversalPrefixOverridden(roleId);

            string prefix = skipPrefix
                ? ""
                : _settings?.UniversalSystemPromptPrefix ?? CoreAISettings.UniversalSystemPromptPrefix;

            string basePrompt;
            if (!string.IsNullOrWhiteSpace(overrideBasePrompt))
            {
                basePrompt = overrideBasePrompt.Trim();
            }
            else if (_systemPrompts.TryGetSystemPrompt(roleId, out string s) && !string.IsNullOrWhiteSpace(s))
            {
                basePrompt = s.Trim();
            }
            else
            {
                basePrompt = $"You are agent \"{roleId}\".";
            }

            string additional = "";
            if (_memoryPolicy != null &&
                _memoryPolicy.TryGetAdditionalSystemPrompt(roleId, out string extra) &&
                !string.IsNullOrWhiteSpace(extra))
            {
                additional = extra.Trim();
            }

            StringBuilder sb = new();
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

        /// <summary>
        /// Заголовок блока рантайм-контекста на ЛЕГАСИ-пути <see cref="AppendRuntimeContext"/>, где блок
        /// дописывается прямо в системный промпт и ему нужен собственный заголовок.
        /// </summary>
        public const string RuntimeContextHeading = "## Runtime Context";

        /// <summary>
        /// Дописывает рантайм-контекст запроса в конец системного промпта под заголовком
        /// <see cref="RuntimeContextHeading"/>. Легаси-путь для вызывающих вне оркестратора; сам оркестратор
        /// кладёт тот же блок хвостовым сообщением <c>## World State</c> (см.
        /// <c>AiOrchestrator.BuildWorldStateInstructions</c>) и берёт его через <see cref="BuildRuntimeContext"/>.
        /// </summary>
        public string AppendRuntimeContext(string systemPrompt, AiTaskRequest request, string roleId, string traceId)
        {
            string runtimeContext = BuildRuntimeContext(request, roleId, traceId);
            if (runtimeContext.Length == 0)
            {
                return systemPrompt ?? "";
            }

            return (systemPrompt ?? "").TrimEnd() + "\n\n" + RuntimeContextHeading + "\n" + runtimeContext;
        }

        /// <summary>
        /// Собирает секции рантайм-контекста запроса БЕЗ заголовка: сначала per-role
        /// <see cref="IAgentRuntimeContextProvider"/>, затем глобальные <see cref="IAiPromptContextProvider"/>,
        /// секции разделены пустой строкой. Заголовок ставит тот, кто размещает блок: оркестратор — свой
        /// <c>## World State</c>, <see cref="AppendRuntimeContext"/> — <see cref="RuntimeContextHeading"/>.
        /// <para>
        /// Раньше заголовок <c>## Runtime Context</c> ставился здесь, и оркестратор оборачивал его во второй,
        /// <c>## World State</c>: модель получала два заголовка подряд для одного блока, а два дока описывали
        /// один блок под разными именами.
        /// </para>
        /// </summary>
        public string BuildRuntimeContext(AiTaskRequest request, string roleId, string traceId)
        {
            if (_contextProviders == null)
            {
                return "";
            }

            StringBuilder sections = new();
            if (_memoryPolicy != null &&
                _memoryPolicy.TryGetRuntimeContextProvider(roleId, out IAgentRuntimeContextProvider roleProvider) &&
                roleProvider != null)
            {
                AppendContextSection(sections, roleProvider.BuildContext(request, roleId, traceId));
            }

            foreach (IAiPromptContextProvider provider in _contextProviders)
            {
                if (provider == null)
                {
                    continue;
                }

                AppendContextSection(sections, provider.BuildContext(request, roleId, traceId));
            }

            if (sections.Length == 0)
            {
                return "";
            }

            return sections.ToString().TrimEnd();
        }

        private static void AppendContextSection(StringBuilder sections, string section)
        {
            if (string.IsNullOrWhiteSpace(section))
            {
                return;
            }

            sections.AppendLine(section.Trim());
            sections.AppendLine();
        }

        /// <summary>
        /// Builds the user-facing prompt payload from session state and the requested AI task.
        /// <para>
        /// Два пути. Без пользовательского шаблона роли — <see cref="BuildDefaultUserBody"/>: либо сырая
        /// подсказка, либо JSON-конверт, где каждая подстановка экранирована <see cref="EscapeJson"/>.
        /// С шаблоном (<see cref="IAgentUserPromptTemplateProvider"/>) — шаблон это ПРОЗА автора роли
        /// (пример: <c>Designer hint: {hint}</c>), поэтому <c>{hint}</c>, <c>{source_tag}</c> и ключи
        /// телеметрии подставляются дословно: экранирование здесь показало бы модели обратные слэши
        /// посреди фразы. Единственная подстановка с JSON-контрактом — <c>{telemetry}</c>: это готовый
        /// самодостаточный объект, экранированный внутри.
        /// </para>
        /// </summary>
        public string BuildUserPayload(GameSessionSnapshot snap, AiTaskRequest task)
        {
            string roleId = task.RoleId ?? BuiltInAgentRoleIds.Creator;
            string body;
            if (_userTemplates.TryGetUserTemplate(roleId, out string tmpl))
            {
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
                body = BuildDefaultUserBody(snap, task);
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

        /// <summary>
        /// Builds the default user-message body for roles that have no custom user-template.
        /// <para>
        /// The JSON envelope (<c>{"telemetry":{...},"hint":"...","ai_task_source":"..."}</c>) exists solely to
        /// deliver structured game state (telemetry) — and the task source alongside it — to autonomous
        /// agents (e.g. the Arena's Creator/Analyzer read the <c>telemetry</c> field to plan waves). When the
        /// game has published <b>no telemetry</b> (plain chat, or any turn without live game-state) the
        /// envelope carries nothing the model can act on and only wraps the user's text in confusing JSON, so
        /// the raw hint is sent instead. This keeps casual chat clean — like a normal assistant — while
        /// preserving the state-aware payload the moment the game feeds telemetry. The task source rides along
        /// with telemetry and is therefore delivered only when there is game-state context to carry it.
        /// </para>
        /// </summary>
        private static string BuildDefaultUserBody(GameSessionSnapshot snap, AiTaskRequest task)
        {
            if (snap?.Telemetry == null || snap.Telemetry.Count == 0)
            {
                return task.Hint ?? "";
            }

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

        /// <summary>
        /// Экранирует строку для вставки внутрь JSON-литерала конверта. Экранируются ВСЕ символы, которые
        /// JSON запрещает в строке сырыми: обратный слэш, кавычка и управляющие символы ниже U+0020.
        /// Раньше экранировались только слэш и кавычка, и перевод строки в подсказке ученика или табуляция
        /// в значении телеметрии давали невалидный конверт. Кириллица и прочий не-ASCII остаются как есть:
        /// в JSON они законны, а в промпте читаемы.
        /// </summary>
        private static string EscapeJson(string s)
        {
            if (string.IsNullOrEmpty(s))
            {
                return "";
            }

            StringBuilder escaped = null;
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                string replacement = c switch
                {
                    '\\' => "\\\\",
                    '"' => "\\\"",
                    '\n' => "\\n",
                    '\r' => "\\r",
                    '\t' => "\\t",
                    '\b' => "\\b",
                    '\f' => "\\f",
                    _ => c < ' ' ? "\\u" + ((int)c).ToString("x4") : null
                };

                if (replacement == null)
                {
                    escaped?.Append(c);
                    continue;
                }

                escaped ??= new StringBuilder(s.Length + 16).Append(s, 0, i);
                escaped.Append(replacement);
            }

            return escaped == null ? s : escaped.ToString();
        }

        private static string AppendLuaRepairContext(string body, AiTaskRequest task)
        {
            if (string.IsNullOrEmpty(task.LuaRepairErrorMessage))
            {
                return body;
            }

            string err = ShortenForPrompt(task.LuaRepairErrorMessage, 500);
            string code = ShortenForPrompt(task.LuaRepairPreviousCode ?? "", 1200);
            return $"{body}; lua_repair_generation={task.LuaRepairGeneration}; lua_error={err}; fix_this_lua={code}";
        }

        private static string ShortenForPrompt(string s, int max)
        {
            if (string.IsNullOrEmpty(s))
            {
                return "";
            }

            s = s.Replace('\r', ' ').Replace('\n', ' ');
            return s.Length <= max ? s : s.Substring(0, max) + "...";
        }
    }
}
