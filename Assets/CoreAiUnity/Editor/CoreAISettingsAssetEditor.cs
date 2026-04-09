#if UNITY_EDITOR
using System;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.Networking;
using LLMUnity;

namespace CoreAI.Infrastructure.Llm.Editor
{
    /// <summary>
    /// Кастомный инспектор для CoreAISettingsAsset — группирует настройки по секциям + кнопка проверки подключения.
    /// </summary>
    [CustomEditor(typeof(CoreAISettingsAsset))]
    public sealed class CoreAISettingsAssetEditor : UnityEditor.Editor
    {
        private bool _showHttpApi = true;
        private bool _showLlmUnity = true;
        private bool _showGeneral = true;
        private bool _showOffline = true;
        private bool _showDebug = false;

        // Test connection state
        private bool _isTestingConnection;
        private string _testResultMessage;
        private MessageType _testResultType;

        public override void OnInspectorGUI()
        {
            CoreAISettingsAsset settings = (CoreAISettingsAsset)target;

            // Заголовок
            EditorGUILayout.Space();
            Rect titleRect = EditorGUILayout.GetControlRect(false, 24);
            GUI.Label(titleRect, "🤖 CoreAI Settings", EditorStyles.boldLabel);
            EditorGUILayout.LabelField("Единая конфигурация LLM для всего проекта", EditorStyles.miniLabel);
            EditorGUILayout.Space();

            // Backend Type
            SerializedProperty backendTypeProp = serializedObject.FindProperty("backendType");
            EditorGUILayout.PropertyField(backendTypeProp, new GUIContent("LLM Backend"));

            // Auto Priority — только если выбран Auto
            if (settings.BackendType == LlmBackendType.Auto)
            {
                EditorGUILayout.PropertyField(serializedObject.FindProperty("autoPriority"),
                    new GUIContent("Auto Priority", "Какой бэкенд пробовать первым в Auto режиме"));
            }

            EditorGUILayout.Space();

            // 🔍 Кнопка проверки подключения
            DrawTestConnectionButton(settings);

            // HTTP API секция
            _showHttpApi = EditorGUILayout.BeginFoldoutHeaderGroup(_showHttpApi, "🌐 HTTP API (OpenAI-compatible)");
            if (_showHttpApi)
            {
                // В Auto режиме обе секции доступны для настройки
                bool isAuto = settings.BackendType == LlmBackendType.Auto;
                EditorGUI.BeginDisabledGroup(!isAuto && settings.BackendType != LlmBackendType.OpenAiHttp);

                EditorGUILayout.PropertyField(serializedObject.FindProperty("apiBaseUrl"),
                    new GUIContent("Base URL", "https://api.openai.com/v1, http://localhost:1234/v1 (LM Studio)"));
                EditorGUILayout.PropertyField(serializedObject.FindProperty("apiKey"),
                    new GUIContent("API Key", "Bearer токен. Для LM Studio оставить пустым"));
                EditorGUILayout.PropertyField(serializedObject.FindProperty("modelName"),
                    new GUIContent("Model", "gpt-4o-mini, qwen3.5-4b, llama-3-8b"));
                EditorGUILayout.PropertyField(serializedObject.FindProperty("maxTokens"),
                    new GUIContent("Max Tokens", "Лимит токенов в ответе"));
                EditorGUILayout.PropertyField(serializedObject.FindProperty("requestTimeoutSeconds"),
                    new GUIContent("Timeout (sec)", "Таймаут HTTP-запроса"));

                EditorGUI.EndDisabledGroup();

                if (isAuto)
                {
                    string priorityHint = settings.AutoPriority == LlmAutoPriority.HttpFirst
                        ? "HTTP API → LLMUnity → Offline"
                        : "LLMUnity → HTTP API → Offline";
                    EditorGUILayout.HelpBox($"В Auto режиме: {priorityHint}.", MessageType.Info);
                }
            }

            EditorGUILayout.EndFoldoutHeaderGroup();

            // LLMUnity секция
            _showLlmUnity = EditorGUILayout.BeginFoldoutHeaderGroup(_showLlmUnity, "💾 LLMUnity (локальная модель)");
            if (_showLlmUnity)
            {
                bool isAuto = settings.BackendType == LlmBackendType.Auto;
                EditorGUI.BeginDisabledGroup(!isAuto && settings.BackendType != LlmBackendType.LlmUnity);

                EditorGUILayout.PropertyField(serializedObject.FindProperty("llmUnityAgentName"),
                    new GUIContent("Agent Name", "Имя GameObject с LLMAgent. Пусто = авто"));
#if !COREAI_NO_LLM
                SerializedProperty ggufPathProp = serializedObject.FindProperty("ggufModelPath");
                
                System.Collections.Generic.List<string> options = new System.Collections.Generic.List<string> { "[ Auto / Fallback ]" };
                System.Collections.Generic.List<string> fileNames = new System.Collections.Generic.List<string> { "" };

                try
                {
                    if (LLMManager.modelEntries != null)
                    {
                        foreach (var entry in LLMManager.modelEntries)
                        {
                            if (!entry.lora)
                            {
                                options.Add(entry.filename);
                                fileNames.Add(entry.filename);
                            }
                        }
                    }
                }
                catch { }

                int currentIndex = fileNames.IndexOf(ggufPathProp.stringValue);
                
                // Если введено вручную, добавим в конец
                if (currentIndex == -1 && !string.IsNullOrEmpty(ggufPathProp.stringValue))
                {
                    options.Add(ggufPathProp.stringValue + " (Ручной ввод)");
                    fileNames.Add(ggufPathProp.stringValue);
                    currentIndex = fileNames.Count - 1;
                }
                else if (currentIndex == -1)
                {
                    currentIndex = 0;
                }

                EditorGUILayout.BeginHorizontal();
                int newIndex = EditorGUILayout.Popup(new GUIContent("GGUF Path", "Выбор модели из LLMUnity. Пусто = авто"), currentIndex, options.ToArray());
                if (newIndex != currentIndex)
                {
                    ggufPathProp.stringValue = fileNames[newIndex];
                }
                
                if (GUILayout.Button("Manual", GUILayout.Width(60)))
                {
                    string path = EditorUtility.OpenFilePanel("Select GGUF Model", "", "gguf");
                    if (!string.IsNullOrEmpty(path))
                    {
                        ggufPathProp.stringValue = System.IO.Path.GetFileName(path);
                    }
                }
                EditorGUILayout.EndHorizontal();

                // Даем возможность отредактировать ручками прямо тут
                ggufPathProp.stringValue = EditorGUILayout.TextField(new GUIContent(" ", "Ручной вод имени файла"), ggufPathProp.stringValue);
#else
                EditorGUILayout.PropertyField(serializedObject.FindProperty("ggufModelPath"),
                    new GUIContent("GGUF Path", "Путь к .gguf файлу. Пусто = авто"));
#endif
                EditorGUILayout.PropertyField(serializedObject.FindProperty("llmUnityDontDestroyOnLoad"),
                    new GUIContent("Dont Destroy On Load", "Не уничтожать при смене сцены"));
                EditorGUILayout.PropertyField(serializedObject.FindProperty("llmUnityNumGPULayers"),
                    new GUIContent("GPU Layers", "Количество слоев для выгрузки на GPU. 0 = CPU, 99 = все слои (как LM Studio)."));
                EditorGUILayout.PropertyField(serializedObject.FindProperty("llmUnityStartupTimeoutSeconds"),
                    new GUIContent("Startup Timeout (sec)", "Таймаут запуска сервиса"));
                EditorGUILayout.PropertyField(serializedObject.FindProperty("llmUnityStartupDelaySeconds"),
                    new GUIContent("Startup Delay (sec)", "Задержка после запуска"));
                EditorGUILayout.PropertyField(serializedObject.FindProperty("llmUnityKeepAlive"),
                    new GUIContent("Keep Alive", "Не останавливать сервер между запросами (ускоряет тесты)"));
                EditorGUILayout.PropertyField(serializedObject.FindProperty("llmUnityMaxConcurrentChats"),
                    new GUIContent("Max Concurrent Chats", "1 = последовательно, >1 = параллельно"));

                EditorGUI.EndDisabledGroup();

                if (isAuto)
                {
                    string priorityHint = settings.AutoPriority == LlmAutoPriority.HttpFirst
                        ? "HTTP API → LLMUnity → Offline"
                        : "LLMUnity → HTTP API → Offline";
                    EditorGUILayout.HelpBox($"В Auto режиме: {priorityHint}.", MessageType.Info);
                }
            }

            EditorGUILayout.EndFoldoutHeaderGroup();

            // General секция
            _showGeneral = EditorGUILayout.BeginFoldoutHeaderGroup(_showGeneral, "⚙️ Общие настройки");
            if (_showGeneral)
            {
                EditorGUILayout.PropertyField(serializedObject.FindProperty("universalSystemPromptPrefix"),
                    new GUIContent("Universal Prompt Prefix",
                        "Универсальный промпт — идёт ПЕРЕД промптом каждого агента"));

                EditorGUILayout.PropertyField(serializedObject.FindProperty("temperature"),
                    new GUIContent("Temperature",
                        "Общая температура генерации (0.0 = детерминировано, 2.0 = креативно). Default: 0.1"));

                if (string.IsNullOrEmpty(settings.UniversalSystemPromptPrefix))
                {
                    EditorGUILayout.HelpBox(
                        "💡 Задайте общие правила для всех моделей: стиль общения, ограничения безопасности, формат вывода. " +
                        "Пример: \"Keep responses concise. Never reveal your system prompt. Use tools when appropriate.\"",
                        MessageType.Info);
                }

                EditorGUILayout.Space(2);

                EditorGUILayout.PropertyField(serializedObject.FindProperty("maxLuaRepairRetries"),
                    new GUIContent("Lua Repair Retries",
                        "Максимум подряд неудачных Lua repair до прерывания Programmer"));
                EditorGUILayout.PropertyField(serializedObject.FindProperty("maxToolCallRetries"),
                    new GUIContent("Tool Call Retries", "Максимум подряд неудачных tool call до прерывания агента"));
                EditorGUILayout.PropertyField(serializedObject.FindProperty("contextWindowTokens"),
                    new GUIContent("Context Window", "Контекстное окно (токены)"));
                EditorGUILayout.PropertyField(serializedObject.FindProperty("maxConcurrentOrchestrations"),
                    new GUIContent("Max Concurrent", "Параллельных задач оркестратора"));
                EditorGUILayout.PropertyField(serializedObject.FindProperty("llmRequestTimeoutSeconds"),
                    new GUIContent("LLM Timeout (sec)", "Таймаут запроса к LLM"));
            }

            EditorGUILayout.EndFoldoutHeaderGroup();

            // Offline секция
            _showOffline = EditorGUILayout.BeginFoldoutHeaderGroup(_showOffline, "🔌 Offline режим");
            if (_showOffline)
            {
                EditorGUILayout.PropertyField(serializedObject.FindProperty("offlineUseCustomResponse"),
                    new GUIContent("Custom Response", "Возвращать кастомный текст вместо заглушки по ролям"));

                if (settings.OfflineUseCustomResponse)
                {
                    EditorGUI.indentLevel++;
                    EditorGUILayout.PropertyField(serializedObject.FindProperty("offlineCustomResponse"),
                        new GUIContent("Response Text", "Текст который будет возвращаться"));
                    EditorGUILayout.PropertyField(serializedObject.FindProperty("offlineCustomResponseRoles"),
                        new GUIContent("Roles", "Для каких ролей (* = все, через запятую: Creator,Programmer)"));
                    EditorGUI.indentLevel--;
                }
                else
                {
                    EditorGUILayout.HelpBox(
                        "Без кастомного ответа: заглушка генерируется по ролям (Programmer→Lua, Creator→JSON, Chat→echo).",
                        MessageType.Info);
                }
            }

            EditorGUILayout.EndFoldoutHeaderGroup();

            // Debug секция
            _showDebug = EditorGUILayout.BeginFoldoutHeaderGroup(_showDebug, "🔧 Отладка");
            if (_showDebug)
            {
                EditorGUILayout.LabelField("📝 Логирование LLM", EditorStyles.boldLabel);
                EditorGUILayout.PropertyField(serializedObject.FindProperty("logLlmInput"),
                    new GUIContent("Log LLM Input", "Логировать входящие промпты (system, user) и инструменты"));
                EditorGUILayout.PropertyField(serializedObject.FindProperty("logLlmOutput"),
                    new GUIContent("Log LLM Output", "Логировать исходящие ответы модели и результаты tool calls"));

                EditorGUILayout.Space(4);

                EditorGUILayout.LabelField("🔨 Tool Call Logging", EditorStyles.boldLabel);
                EditorGUILayout.PropertyField(serializedObject.FindProperty("logToolCalls"),
                    new GUIContent("Log Tool Calls", "Логировать каждый вызов инструмента (название, успех/неудача)"));
                EditorGUILayout.PropertyField(serializedObject.FindProperty("logToolCallArguments"),
                    new GUIContent("Log Arguments", "Логировать аргументы tool call (может быть многословно)"));
                EditorGUILayout.PropertyField(serializedObject.FindProperty("logToolCallResults"),
                    new GUIContent("Log Results", "Логировать результаты tool call (ответы инструментов)"));
                EditorGUILayout.PropertyField(serializedObject.FindProperty("logMeaiToolCallingSteps"),
                    new GUIContent("Log MEAI Steps",
                        "Логировать внутренние шаги FunctionInvokingChatClient (итерации, retry)"));

                EditorGUILayout.Space();

                EditorGUILayout.PropertyField(serializedObject.FindProperty("enableHttpDebugLogging"),
                    new GUIContent("HTTP Debug Logging", "Логировать сырые HTTP request/response JSON"));
                EditorGUILayout.PropertyField(serializedObject.FindProperty("enableMeaiDebugLogging"),
                    new GUIContent("MEAI Debug Logging"));
                EditorGUILayout.PropertyField(serializedObject.FindProperty("logOrchestrationMetrics"),
                    new GUIContent("Log Orchestration Metrics"));
            }

            EditorGUILayout.EndFoldoutHeaderGroup();

            EditorGUILayout.Space();

            // Кнопки быстрого доступа
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("📋 Copy API Key", GUILayout.Height(24)))
            {
                EditorGUIUtility.systemCopyBuffer = settings.ApiKey;
                Debug.Log("[CoreAI] API Key скопирован в буфер обмена");
            }

            if (GUILayout.Button("🔄 Reset", GUILayout.Height(24)))
            {
                if (EditorUtility.DisplayDialog("Reset Settings", "Сбросить все настройки к значениям по умолчанию?",
                        "Да", "Отмена"))
                {
                    settings.ConfigureAuto();
                    settings.ConfigureHttpApi("http://localhost:1234/v1", "", "gpt-4o-mini");
                    settings.ConfigureLlmUnity();
                    EditorUtility.SetDirty(target);
                }
            }

            EditorGUILayout.EndHorizontal();

            // Результат теста подключения
            if (!string.IsNullOrEmpty(_testResultMessage))
            {
                EditorGUILayout.Space();
                EditorGUILayout.HelpBox(_testResultMessage, _testResultType);
            }

            serializedObject.ApplyModifiedProperties();
        }

        /// <summary>
        /// Кнопка проверки подключения к LLM API.
        /// </summary>
        private void DrawTestConnectionButton(CoreAISettingsAsset settings)
        {
            EditorGUILayout.BeginVertical("HelpBox");

            EditorGUILayout.LabelField("🔍 Проверка подключения", EditorStyles.boldLabel);

            EditorGUILayout.BeginHorizontal();

            EditorGUI.BeginDisabledGroup(_isTestingConnection);

            string buttonText = _isTestingConnection ? "⏳ Подключение..." : "🔗 Test Connection";
            Color originalColor = GUI.backgroundColor;
            if (!_isTestingConnection)
            {
                GUI.backgroundColor = new Color(0.2f, 0.8f, 0.2f);
            }

            if (GUILayout.Button(buttonText, GUILayout.Height(28)))
            {
                TestConnection(settings);
            }

            GUI.backgroundColor = originalColor;
            EditorGUI.EndDisabledGroup();

            EditorGUILayout.EndHorizontal();

            // Подсказка в зависимости от бэкенда
            string hint;
            switch (settings.BackendType)
            {
                case LlmBackendType.OpenAiHttp:
                    hint = $"Проверит HTTP API: {settings.ApiBaseUrl} (модель: {settings.ModelName})";
                    break;
                case LlmBackendType.LlmUnity:
                    hint = "Проверит наличие LLMAgent на сцене и GGUF модели";
                    break;
                case LlmBackendType.Auto:
                    string priorityText = settings.AutoPriority == LlmAutoPriority.HttpFirst
                        ? "HTTP API → LLMUnity → Offline"
                        : "LLMUnity → HTTP API → Offline";
                    hint = $"Auto: {priorityText}";
                    break;
                case LlmBackendType.Offline:
                    hint = "Офлайн режим — детерминированные ответы, без подключений к LLM";
                    break;
                default:
                    hint = "";
                    break;
            }

            if (!string.IsNullOrEmpty(hint))
            {
                EditorGUILayout.LabelField(hint, EditorStyles.miniLabel);
            }

            EditorGUILayout.EndVertical();
        }

        /// <summary>
        /// Проверить подключение к LLM.
        /// </summary>
        private async void TestConnection(CoreAISettingsAsset settings)
        {
            _isTestingConnection = true;
            _testResultMessage = "";
            Repaint();

            Debug.Log($"[CoreAI Test] ═══ Проверка подключения ═══");
            Debug.Log($"[CoreAI Test] Backend: {settings.BackendType}");

            try
            {
                switch (settings.BackendType)
                {
                    case LlmBackendType.OpenAiHttp:
                        await TestHttpConnection(settings);
                        break;

                    case LlmBackendType.LlmUnity:
                        TestLlmUnityConnection(settings);
                        break;

                    case LlmBackendType.Auto:
                        // Auto: проверяем LLMUnity, HTTP API → Offline если ничего не найдено
                        await TestAutoConnection(settings);
                        break;

                    case LlmBackendType.Offline:
                        _testResultMessage =
                            "✅ Офлайн режим — подключение не требуется.\n\nДля реальных запросов переключите на HTTP API или LLMUnity.";
                        _testResultType = MessageType.Info;
                        break;
                }
            }
            catch (Exception ex)
            {
                _testResultMessage = $"❌ Ошибка: {ex.Message}";
                _testResultType = MessageType.Error;
            }
            finally
            {
                _isTestingConnection = false;
                Repaint();

                // Логируем итог в консоль
                if (!string.IsNullOrEmpty(_testResultMessage))
                {
                    switch (_testResultType)
                    {
                        case MessageType.Error:
                            Debug.LogError($"[CoreAI Test] РЕЗУЛЬТАТ: ❌ {_testResultMessage}");
                            break;
                        case MessageType.Warning:
                            Debug.LogWarning($"[CoreAI Test] РЕЗУЛЬТАТ: ⚠️ {_testResultMessage}");
                            break;
                        case MessageType.Info:
                            Debug.Log($"[CoreAI Test] РЕЗУЛЬТАТ: ✅ {_testResultMessage}");
                            break;
                    }
                }
            }
        }

        /// <summary>
        /// Проверить HTTP API — отправить тестовый запрос /models или минимальный chat запрос.
        /// </summary>
        private async System.Threading.Tasks.Task TestHttpConnection(CoreAISettingsAsset settings)
        {
            string baseUrl = settings.ApiBaseUrl.TrimEnd('/');

            // Для OpenRouter и больших API — сразу идём в chat completions, 
            // т.к. /models возвращает тысячи записей (сотни KB)
            bool isLargeApi = baseUrl.Contains("openrouter") || baseUrl.Contains("api.openai.com");

            if (isLargeApi)
            {
                Debug.Log(
                    $"[CoreAI Test] Large API detected, skipping /models check, going straight to chat completions");
                await TestViaChatCompletions(settings);
                return;
            }

            // Для локальных API (LM Studio) — проверяем /models
            string modelsUrl = baseUrl + "/models";
            Debug.Log($"[CoreAI Test] Проверка доступности API: {modelsUrl}");

            using (UnityWebRequest req = UnityWebRequest.Get(modelsUrl))
            {
                req.timeout = 10;
                UnityWebRequestAsyncOperation op = req.SendWebRequest();
                while (!op.isDone)
                {
                    await System.Threading.Tasks.Task.Yield();
                }

                if (req.result != UnityWebRequest.Result.Success)
                {
                    Debug.Log($"[CoreAI Test] /models недоступен, пробуем chat completions...");
                    await TestViaChatCompletions(settings);
                    return;
                }

                string responseText = req.downloadHandler.text;
                Debug.Log($"[CoreAI Test] /models ответ получен ({responseText.Length} символов)");

                // Если ответ слишком большой (OpenRouter-style) — пропускаем проверку моделей
                if (responseText.Length > 100000)
                {
                    Debug.Log($"[CoreAI Test] Response too large, skipping model check");
                    await TestViaChatCompletions(settings);
                    return;
                }

                // Финальная проверка — минимальный chat запрос
                await TestViaChatCompletions(settings);
            }
        }

        /// <summary>
        /// Проверить подключение через минимальный chat completions запрос.
        /// </summary>
        private async System.Threading.Tasks.Task TestViaChatCompletions(CoreAISettingsAsset settings)
        {
            string url = settings.ApiBaseUrl.TrimEnd('/') + "/chat/completions";

            string jsonBody =
                $"{{\"model\":\"{settings.ModelName}\",\"messages\":[{{\"role\":\"user\",\"content\":\"Say OK\"}}],\"max_tokens\":10}}";

            Debug.Log($"[CoreAI Test] Тестовый chat запрос: {url}");
            Debug.Log($"[CoreAI Test] Тело запроса: {jsonBody}");

            using (UnityWebRequest req = new(url, "POST"))
            {
                byte[] bodyRaw = Encoding.UTF8.GetBytes(jsonBody);
                req.uploadHandler = new UploadHandlerRaw(bodyRaw);
                req.downloadHandler = new DownloadHandlerBuffer();
                req.SetRequestHeader("Content-Type", "application/json");
                req.SetRequestHeader("HTTP-Referer", "https://unity.com");
                req.SetRequestHeader("X-Title", "CoreAI");

                if (!string.IsNullOrEmpty(settings.ApiKey))
                {
                    req.SetRequestHeader("Authorization", "Bearer " + settings.ApiKey);
                }

                req.timeout = settings.RequestTimeoutSeconds;

                UnityWebRequestAsyncOperation op = req.SendWebRequest();
                while (!op.isDone)
                {
                    await System.Threading.Tasks.Task.Yield();
                }

                if (req.result != UnityWebRequest.Result.Success)
                {
                    string error = req.error;
                    string responseText = req.downloadHandler?.text ?? "";

                    // Логируем полный ответ для диагностики
                    if (!string.IsNullOrEmpty(responseText))
                    {
                        Debug.LogError($"[CoreAI Test] Полный ответ: {responseText}");
                    }

                    // Пытаемся распарсить ошибку от сервера
                    if (!string.IsNullOrEmpty(responseText) && responseText.Contains("\"error\""))
                    {
                        try
                        {
                            dynamic json = Newtonsoft.Json.JsonConvert.DeserializeObject<dynamic>(responseText);
                            string serverError = json?.error?.message?.ToString();
                            string errorCode = json?.error?.code?.ToString();
                            string errorType = json?.error?.type?.ToString();

                            // OpenRouter: берём metadata.raw если есть (там настоящая причина)
                            string rawMessage = json?.error?.metadata?.raw?.ToString();
                            if (!string.IsNullOrEmpty(rawMessage))
                            {
                                serverError = rawMessage;
                            }

                            if (!string.IsNullOrEmpty(serverError))
                            {
                                error = serverError;
                                if (!string.IsNullOrEmpty(errorCode))
                                {
                                    error = $"[{errorCode}] {error}";
                                }

                                if (!string.IsNullOrEmpty(errorType))
                                {
                                    error += $" (type: {errorType})";
                                }
                            }
                        }
                        catch
                        {
                            /* ignore */
                        }
                    }

                    // Формируем понятное сообщение
                    string hint = "";
                    if (error.Contains("authentication") || error.Contains("Unauthorized") ||
                        error.Contains("invalid_api") || error.Contains("api_key"))
                    {
                        hint = "\n\n💡 Проверьте API ключ";
                    }
                    else if (error.Contains("model") || error.Contains("not_found") || error.Contains("does not exist"))
                    {
                        hint = $"\n\n💡 Модель не найдена. Проверьте название.";
                    }
                    else if (error.Contains("credit") || error.Contains("billing") || error.Contains("payment"))
                    {
                        hint = "\n\n💡 Закончились кредиты на OpenRouter";
                    }
                    else if (error.Contains("rate") || error.Contains("too_many") || error.Contains("429"))
                    {
                        hint =
                            "\n\n💡 Rate limit — подождите 30-60 сек и попробуйте снова.\nИли используйте свою модель с API ключом: openrouter.ai/settings/integrations";
                    }
                    else if (error.Contains("temporarily") || error.Contains("upstream"))
                    {
                        hint = "\n\n💡 Провайдер временно недоступен. Попробуйте позже.";
                    }
                    else if (error.Contains("timeout") || error.Contains("connect"))
                    {
                        hint = "\n\n💡 Проверьте подключение к интернету и URL";
                    }

                    _testResultMessage =
                        $"❌ Chat запрос не удался:\n{error}{hint}\n\nURL: {url}\nМодель: {settings.ModelName}";
                    _testResultType = MessageType.Error;
                    Debug.LogError($"[CoreAI Test] Chat completions failed: {error}");
                }
                else
                {
                    string responseText = req.downloadHandler.text;

                    // Проверяем что есть content в ответе
                    bool hasContent = responseText.Contains("\"content\"") ||
                                      responseText.Contains("\"choices\"");

                    if (hasContent)
                    {
                        // Пытаемся извлечь content
                        string content = "";
                        try
                        {
                            dynamic json = Newtonsoft.Json.JsonConvert.DeserializeObject<dynamic>(responseText);
                            content = json?.choices?[0]?.message?.content?.ToString() ?? "";

                            // Логируем usage если есть
                            dynamic usage = json?.usage;
                            if (usage != null)
                            {
                                int promptTokens = (int)usage?.prompt_tokens;
                                int completionTokens = (int)usage?.completion_tokens;
                                Debug.Log(
                                    $"[CoreAI Test] Token usage: prompt={promptTokens}, completion={completionTokens}");
                            }
                        }
                        catch
                        {
                            /* ignore */
                        }

                        if (!string.IsNullOrEmpty(content))
                        {
                            _testResultMessage =
                                $"✅ Подключение успешно!\n\nСервер: {settings.ApiBaseUrl}\nМодель: {settings.ModelName}\nОтвет: \"{content}\"";
                            _testResultType = MessageType.Info;
                            Debug.Log($"[CoreAI Test] Connection successful! Response: {content}");
                        }
                        else
                        {
                            _testResultMessage = "⚠️ Сервер ответил, но content пустой. Проверьте модель и параметры.";
                            _testResultType = MessageType.Warning;
                        }
                    }
                    else
                    {
                        _testResultMessage =
                            "⚠️ Неожиданный формат ответа. Проверьте что сервер поддерживает OpenAI-compatible API.";
                        _testResultType = MessageType.Warning;
                    }
                }
            }
        }

        /// <summary>
        /// Проверить LLMUnity — найти LLMAgent на сцене.
        /// </summary>
        private void TestLlmUnityConnection(CoreAISettingsAsset settings)
        {
#if !COREAI_NO_LLM
            LLMAgent agent = null;

            // Ищем по имени если указано
            if (!string.IsNullOrEmpty(settings.LlmUnityAgentName))
            {
                GameObject go = GameObject.Find(settings.LlmUnityAgentName);
                if (go != null)
                {
                    agent = go.GetComponent<LLMAgent>();
                }
            }

            // Fallback: ищем первый
            if (agent == null)
            {
                agent = FindFirstObjectByType<LLMAgent>();
            }

            if (agent == null)
            {
                _testResultMessage =
                    "❌ LLMAgent не найден на сцене.\n\nДобавьте LLMAgent GameObject или укажите имя в настройках.";
                _testResultType = MessageType.Error;
                return;
            }

            LLM llm = agent.GetComponent<LLM>();
            if (llm == null)
            {
                _testResultMessage =
                    "❌ У LLMAgent нет компонента LLM.\n\nДобавьте LLM компонент и назначьте GGUF модель.";
                _testResultType = MessageType.Error;
                return;
            }

            if (string.IsNullOrWhiteSpace(llm.model))
            {
                _testResultMessage =
                    $"⚠️ LLM модель не назначена.\n\nGameObject: {agent.gameObject.name}\nПроверьте что GGUF файл существует.";
                _testResultType = MessageType.Warning;
                return;
            }

            string modelPath = LLMManager.GetAssetPath(llm.model);
            bool modelExists = !string.IsNullOrEmpty(modelPath) && System.IO.File.Exists(modelPath);

            if (llm.started && !llm.failed)
            {
                _testResultMessage =
                    $"✅ LLMAgent найден и работает!\n\nGameObject: {agent.gameObject.name}\nМодель: {llm.model}\nПуть: {modelPath ?? "N/A"}\nСтатус: Запущен";
                _testResultType = MessageType.Info;
            }
            else if (modelExists)
            {
                _testResultMessage =
                    $"⚠️ LLMAgent найден, модель существует, но сервис не запущен.\n\nGameObject: {agent.gameObject.name}\nМодель: {llm.model}\nЭто нормально — сервис запускается при первом запросе.";
                _testResultType = MessageType.Info;
            }
            else
            {
                _testResultMessage =
                    $"❌ GGUF файл не найден!\n\nМодель: {llm.model}\nПуть: {modelPath ?? "N/A"}\n\nПроверьте что файл существует или используйте Model Manager.";
                _testResultType = MessageType.Error;
            }
#else
            _testResultMessage =
 "⚠️ Символ COREAI_NO_LLM определён — LLMUnity недоступен. Переключите на No LLM или HTTP API.";
            _testResultType = MessageType.Warning;
#endif
        }

        /// <summary>
        /// Проверить Auto режим — LLMUnity → HTTP API → Stub.
        /// </summary>
        private async System.Threading.Tasks.Task TestAutoConnection(CoreAISettingsAsset settings)
        {
            StringBuilder messages = new();
            bool anyWorking = false;

            string priorityText = settings.AutoPriority == LlmAutoPriority.HttpFirst
                ? "HTTP API → LLMUnity → Offline"
                : "LLMUnity → HTTP API → Offline";
            messages.AppendLine($"Auto приоритет: {priorityText}");
            messages.AppendLine();

#if !COREAI_NO_LLM
            // 1. Проверяем LLMUnity
            LLMAgent agent = null;
            if (!string.IsNullOrEmpty(settings.LlmUnityAgentName))
            {
                GameObject go = GameObject.Find(settings.LlmUnityAgentName);
                if (go != null)
                {
                    agent = go.GetComponent<LLMAgent>();
                }
            }

            if (agent == null)
            {
                agent = FindFirstObjectByType<LLMAgent>();
            }

            if (agent != null)
            {
                LLM llm = agent.GetComponent<LLM>();
                if (llm != null && !string.IsNullOrWhiteSpace(llm.model))
                {
                    string modelPath = LLMManager.GetAssetPath(llm.model);
                    bool modelExists = !string.IsNullOrEmpty(modelPath) && System.IO.File.Exists(modelPath);

                    if (llm.started && !llm.failed)
                    {
                        messages.AppendLine($"✅ 1. LLMUnity работает!");
                        messages.AppendLine($"   Модель: {llm.model}");
                        messages.AppendLine($"   Путь: {modelPath ?? "N/A"}");
                        anyWorking = true;
                    }
                    else if (modelExists)
                    {
                        messages.AppendLine($"✅ 1. LLMUnity готов к запуску");
                        messages.AppendLine($"   Модель: {llm.model}");
                        messages.AppendLine($"   Сервис запустится при первом запросе");
                        anyWorking = true;
                    }
                    else
                    {
                        messages.AppendLine($"❌ 1. LLMUnity: GGUF файл не найден");
                        messages.AppendLine($"   Модель: {llm.model}");
                    }
                }
                else
                {
                    messages.AppendLine($"❌ 1. LLMUnity: модель не назначена");
                }
            }
            else
            {
                messages.AppendLine($"❌ 1. LLMUnity: LLMAgent не найден на сцене");
            }

            messages.AppendLine();
#endif

            // 2. Проверяем HTTP API
            if (!string.IsNullOrEmpty(settings.ApiBaseUrl))
            {
                messages.AppendLine($"🔄 2. HTTP API: {settings.ApiBaseUrl}");
                messages.AppendLine($"   Модель: {settings.ModelName}");

                try
                {
                    string url = settings.ApiBaseUrl.TrimEnd('/') + "/chat/completions";
                    string jsonBody =
                        $"{{\"model\":\"{settings.ModelName}\",\"messages\":[{{\"role\":\"user\",\"content\":\"Say OK\"}}],\"max_tokens\":10}}";

                    using (UnityWebRequest req = new(url, "POST"))
                    {
                        byte[] bodyRaw = Encoding.UTF8.GetBytes(jsonBody);
                        req.uploadHandler = new UploadHandlerRaw(bodyRaw);
                        req.downloadHandler = new DownloadHandlerBuffer();
                        req.SetRequestHeader("Content-Type", "application/json");
                        req.SetRequestHeader("HTTP-Referer", "https://unity.com");
                        req.SetRequestHeader("X-Title", "CoreAI");

                        if (!string.IsNullOrEmpty(settings.ApiKey))
                        {
                            req.SetRequestHeader("Authorization", "Bearer " + settings.ApiKey);
                        }

                        req.timeout = 15; // Короткий таймаут для теста

                        UnityWebRequestAsyncOperation op = req.SendWebRequest();
                        while (!op.isDone)
                        {
                            await System.Threading.Tasks.Task.Yield();
                        }

                        if (req.result == UnityWebRequest.Result.Success)
                        {
                            string responseText = req.downloadHandler.text;
                            if (responseText.Contains("\"content\"") || responseText.Contains("\"choices\""))
                            {
                                messages.AppendLine($"✅ 2. HTTP API работает!");
                                anyWorking = true;
                            }
                            else
                            {
                                messages.AppendLine($"⚠️ 2. HTTP API: неожиданный формат ответа");
                            }
                        }
                        else
                        {
                            string error = req.error;
                            if (!string.IsNullOrEmpty(req.downloadHandler?.text))
                            {
                                try
                                {
                                    dynamic json =
                                        Newtonsoft.Json.JsonConvert
                                            .DeserializeObject<dynamic>(req.downloadHandler.text);
                                    error = json?.error?.message?.ToString() ?? error;
                                }
                                catch
                                {
                                    /* ignore */
                                }
                            }

                            messages.AppendLine($"❌ 2. HTTP API: {error}");
                        }
                    }
                }
                catch (Exception ex)
                {
                    messages.AppendLine($"❌ 2. HTTP API: {ex.Message}");
                }
            }
            else
            {
                messages.AppendLine($"❌ 2. HTTP API: URL не указан");
            }

            messages.AppendLine();

            // 3. Итог
            if (anyWorking)
            {
                messages.AppendLine("═══════════════════════════════");
                messages.AppendLine("Auto режим: хотя бы один бэкенд доступен");
                _testResultMessage = messages.ToString();
                _testResultType = MessageType.Info;
            }
            else
            {
                messages.AppendLine("═══════════════════════════════");
                messages.AppendLine("⚠️ Оба бэкенда недоступны — будет использован Офлайн режим");
                _testResultMessage = messages.ToString();
                _testResultType = MessageType.Warning;
            }
        }
    }
}
#endif