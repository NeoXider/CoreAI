using System;
using System.Collections.Generic;
using System.Linq;
using CoreAI.AgentMemory;
using CoreAI.Ai;
using CoreAI.Infrastructure.Logging;
using CoreAI.Infrastructure.Llm;
#if COREAI_HAS_LLMUNITY && !UNITY_WEBGL
using LLMUnity;
#endif
using UnityEngine;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace CoreAI.Tests.PlayMode
{
    /// <summary>
    /// Helper для оборачивания ILlmClient с правильным IAgentMemoryStore.
    /// Решает проблему: фабрика создаёт клиент со своим InMemoryStore,
    /// а тест использует другой store в оркестраторе.
    /// </summary>
    public static class LlmClientTestHelpers
    {
        /// <summary>
        /// Обернуть клиент из PlayModeProductionLikeLlmHandle с нужным IAgentMemoryStore.
        /// Для LLMUnity — пересоздаёт MeaiLlmClient с правильным store.
        /// Для HTTP — возвращает как есть (HTTP клиент уже правильно создаётся с null store).
        /// </summary>
        public static ILlmClient WrapWithMemoryStore(this PlayModeProductionLikeLlmHandle handle,
            IAgentMemoryStore memoryStore)
        {
#if COREAI_NO_LLM || UNITY_WEBGL
            // In this build target, we do not use LLMUnity and may not have HTTP client types compiled.
            // Just return the resolved client as-is.
            return handle.Client;
#else
            if (handle == null)
            {
                throw new ArgumentNullException(nameof(handle));
            }

#if COREAI_HAS_LLMUNITY
            // LLMUnity клиент — пересоздаём с нужным MemoryStore
            if (handle.ResolvedBackend == PlayModeProductionLikeLlmBackend.LlmUnity)
            {
                MeaiLlmUnityClient llmUnityClient = handle.Client as MeaiLlmUnityClient;
                if (llmUnityClient != null)
                {
                    return new MeaiLlmClient(
                        new LlmUnityMeaiChatClient(llmUnityClient.UnityAgent, GameLoggerUnscopedFallback.Instance),
                        GameLoggerUnscopedFallback.Instance,
                        UnityEngine.ScriptableObject.CreateInstance<CoreAI.Infrastructure.Llm.CoreAISettingsAsset>(),
                        memoryStore);
                }
            }
#endif

            // HTTP клиент — создаём новый через OpenAiChatLlmClient используя сохранённые настройки
            if (handle.ResolvedBackend == PlayModeProductionLikeLlmBackend.OpenAiCompatibleHttp)
            {
                if (handle._openAiSettings != null)
                {
                    return new OpenAiChatLlmClient(handle._openAiSettings, memoryStore);
                }
                
                if (handle._coreAiSettings != null)
                {
                    return new OpenAiChatLlmClient(handle._coreAiSettings, memoryStore);
                }
            }

            // Offline или другой — используем как есть
            return handle.Client;
#endif
        }
    }

    /// <summary>
    /// In-memory store for tests.
    /// </summary>
    public sealed class InMemoryStore : IAgentMemoryStore
    {
        public readonly Dictionary<string, AgentMemoryState> States = new();
        public readonly Dictionary<string, List<Ai.ChatMessage>> ChatHistories = new();

        public bool TryLoad(string roleId, out AgentMemoryState state)
        {
            return States.TryGetValue(roleId, out state);
        }

        public void Save(string roleId, AgentMemoryState state)
        {
            States[roleId] = state;
        }

        public void Clear(string roleId)
        {
            States.Remove(roleId);
            ChatHistories.Remove(roleId);
        }

        public void ClearChatHistory(string roleId)
        {
        }

        public void AppendChatMessage(string roleId, string role, string content, bool persistToDisk = true)
        {
            if (!ChatHistories.TryGetValue(roleId, out List<Ai.ChatMessage> list))
            {
                list = new List<Ai.ChatMessage>();
                ChatHistories[roleId] = list;
            }

            list.Add(new Ai.ChatMessage(role, content ?? ""));
        }

        public Ai.ChatMessage[] GetChatHistory(string roleId, int maxMessages = 0)
        {
            if (!ChatHistories.TryGetValue(roleId, out List<Ai.ChatMessage> list))
            {
                return Array.Empty<Ai.ChatMessage>();
            }

            if (maxMessages <= 0)
            {
                return list.ToArray();
            }

            int count = Math.Min(maxMessages, list.Count);
            return list.Skip(list.Count - count).ToArray();
        }
    }

    /// <summary>
    /// Какой бэкенд использовать в Play Mode тестах с реальной моделью (см. <see cref="PlayModeProductionLikeLlmFactory.TryCreate"/>).
    /// Переменная окружения: <c>COREAI_PLAYMODE_LLM_BACKEND</c> = <c>auto</c> | <c>http</c> | <c>llmunity</c> | <c>offline</c> (без значения = auto из CoreAISettingsAsset).
    /// Приоритет: 1) CoreAISettingsAsset  2) Env var  3) Auto fallback.
    /// </summary>
    public enum PlayModeProductionLikeLlmBackend
    {
        /// <summary>Из CoreAISettingsAsset.BackendType. Если Auto: LLMUnity → HTTP API → Offline.</summary>
        FromSettings = -1,

        /// <summary>Как <see cref="CoreAI.Composition.CoreAILifetimeScope"/>: LLMUnity → HTTP API → Offline.</summary>
        Auto = 0,

        /// <summary>Только HTTP (LM Studio и т.д.).</summary>
        OpenAiCompatibleHttp = 1,

        /// <summary>Только локальный LLMUnity (рантайм LLM+LLMAgent).</summary>
        LlmUnity = 2,

        /// <summary>Офлайн режим — без подключений к LLM.</summary>
        Offline = 3
    }

    /// <summary>
    /// <see cref="ILlmClient"/> как в игре (HTTP или LLMUnity). Освободить после теста — уничтожает временные объекты Unity.
    /// </summary>
    public sealed class PlayModeProductionLikeLlmHandle : IDisposable
    {
        public ILlmClient Client { get; }
        public PlayModeProductionLikeLlmBackend ResolvedBackend { get; }

        internal readonly OpenAiHttpLlmSettings _openAiSettings;
        internal readonly CoreAISettingsAsset _coreAiSettings;
        private readonly GameObject _llmUnityHarnessRoot;

        internal PlayModeProductionLikeLlmHandle(
            ILlmClient client,
            PlayModeProductionLikeLlmBackend resolvedBackend,
            OpenAiHttpLlmSettings openAiSettings = null,
            CoreAISettingsAsset coreAiSettings = null,
            GameObject llmUnityHarnessRoot = null)
        {
            Client = client;
            ResolvedBackend = resolvedBackend;
            _openAiSettings = openAiSettings;
            _coreAiSettings = coreAiSettings;
            _llmUnityHarnessRoot = llmUnityHarnessRoot;
        }

        public void Dispose()
        {
            if (_llmUnityHarnessRoot != null)
            {
                // Manually stop LLM and clean up LLMAgent to prevent C++ thread locks/memory leaks during rapid test teardowns
                // 1. First cancel any active background generations to prevent C++ thread deadlocks
#if COREAI_HAS_LLMUNITY && !UNITY_WEBGL
                LLMAgent agent = _llmUnityHarnessRoot.GetComponent<LLMAgent>();
                if (agent != null)
                {
                    agent.CancelRequests();
                }

                // 2. Shut down the server immediately
                LLM llm = _llmUnityHarnessRoot.GetComponent<LLM>();
                if (llm != null)
                {
                    llm.Destroy();
                }
#endif

#if UNITY_EDITOR
                if (!EditorApplication.isPlaying)
                {
                    UnityEngine.Object.DestroyImmediate(_llmUnityHarnessRoot);
                }
                else
#endif
                {
                    UnityEngine.Object.DestroyImmediate(_llmUnityHarnessRoot);
                }
            }

            if (_openAiSettings != null)
            {
#if UNITY_EDITOR
                if (!EditorApplication.isPlaying)
                {
                    UnityEngine.Object.DestroyImmediate(_openAiSettings);
                }
                else
#endif
                {
                    UnityEngine.Object.DestroyImmediate(_openAiSettings);
                }
            }
        }
    }

    /// <summary>
    /// Единая точка создания «продакшен-подобного» <see cref="ILlmClient"/> для Play Mode:
    /// использует <see cref="CoreAISettingsAsset"/> как источник истины для выбора бэкенда и настроек.
    /// Приоритет: 1) CoreAISettingsAsset  2) Env var  3) Auto fallback.
    /// </summary>
    public static partial class PlayModeProductionLikeLlmFactory
    {
        private const string EnvBackend = "COREAI_PLAYMODE_LLM_BACKEND";

        /// <summary>
        /// Определить бэкенд: CoreAISettingsAsset → Env var → Auto.
        /// Если <paramref name="explicitPreference"/> задан — он имеет приоритет.
        /// </summary>
        public static PlayModeProductionLikeLlmBackend ResolvePreference(
            PlayModeProductionLikeLlmBackend? explicitPreference)
        {
            if (explicitPreference.HasValue &&
                explicitPreference.Value != PlayModeProductionLikeLlmBackend.FromSettings)
            {
                return explicitPreference.Value;
            }

            // 1. Читаем CoreAISettingsAsset
            CoreAISettingsAsset settings = CoreAISettingsAsset.Instance;
            if (settings != null)
            {
                switch (settings.BackendType)
                {
                    case LlmBackendType.LlmUnity:
                        return PlayModeProductionLikeLlmBackend.LlmUnity;
                    case LlmBackendType.OpenAiHttp:
                        return PlayModeProductionLikeLlmBackend.OpenAiCompatibleHttp;
                    case LlmBackendType.Offline:
                        return PlayModeProductionLikeLlmBackend.Offline;
                    case LlmBackendType.Auto:
                        return PlayModeProductionLikeLlmBackend.Auto;
                }
            }

            // 2. Env var
            return ParseEnvBackend();
        }

        public static PlayModeProductionLikeLlmBackend ParseEnvBackend()
        {
            string v = Environment.GetEnvironmentVariable(EnvBackend);
            if (string.IsNullOrWhiteSpace(v))
            {
                return PlayModeProductionLikeLlmBackend.Auto;
            }

            switch (v.Trim().ToLowerInvariant())
            {
                case "http":
                case "openai":
                case "openai_http":
                case "openai-http":
                    return PlayModeProductionLikeLlmBackend.OpenAiCompatibleHttp;
                case "llmunity":
                case "llm_unity":
                case "local":
                case "gguf":
                    return PlayModeProductionLikeLlmBackend.LlmUnity;
                case "offline":
                case "no_llm":
                case "stub":
                    return PlayModeProductionLikeLlmBackend.Offline;
                case "auto":
                default:
                    return PlayModeProductionLikeLlmBackend.Auto;
            }
        }

        /// <summary>
        /// Собирает клиент оркестратора. Для <see cref="PlayModeProductionLikeLlmBackend.LlmUnity"/> вызывайте затем <see cref="EnsureLlmUnityModelReady"/> из корутины.
        /// </summary>
        public static bool TryCreate(
            PlayModeProductionLikeLlmBackend? explicitPreference,
            float openAiTemperature,
            int openAiTimeoutSeconds,
            out PlayModeProductionLikeLlmHandle handle,
            out string ignoreReason)
        {
            handle = null;
            ignoreReason = null;
            PlayModeProductionLikeLlmBackend pref = ResolvePreference(explicitPreference);

            // Получаем настройки из CoreAISettingsAsset
            CoreAISettingsAsset settings = CoreAISettingsAsset.Instance;

            switch (pref)
            {
                case PlayModeProductionLikeLlmBackend.FromSettings:
                case PlayModeProductionLikeLlmBackend.Auto:
                    // Auto: приоритет из CoreAISettingsAsset.AutoPriority
                    bool httpFirst = settings != null && settings.AutoPriority == LlmAutoPriority.HttpFirst;

                    if (httpFirst)
                    {
                        // HTTP API → LLMUnity → Offline
                        if (TryCreateOpenAi(settings, openAiTemperature, openAiTimeoutSeconds, out handle, out _))
                        {
                            return true;
                        }

                        if (TryCreateLlmUnity(settings, out handle, out _))
                        {
                            return true;
                        }
                    }
                    else
                    {
                        // LLMUnity → HTTP API → Offline (по умолчанию)
                        if (TryCreateLlmUnity(settings, out handle, out _))
                        {
                            return true;
                        }

                        if (TryCreateOpenAi(settings, openAiTemperature, openAiTimeoutSeconds, out handle, out _))
                        {
                            return true;
                        }
                    }

                    // Fallback на Offline
                    handle = new PlayModeProductionLikeLlmHandle(
                        new OfflineLlmClient(settings),
                        PlayModeProductionLikeLlmBackend.Offline,
                        coreAiSettings: settings);
                    ignoreReason = null;
                    return true;

                case PlayModeProductionLikeLlmBackend.OpenAiCompatibleHttp:
                    return TryCreateOpenAi(settings, openAiTemperature, openAiTimeoutSeconds, out handle,
                        out ignoreReason);

                case PlayModeProductionLikeLlmBackend.LlmUnity:
                    return TryCreateLlmUnity(settings, out handle, out ignoreReason);

                case PlayModeProductionLikeLlmBackend.Offline:
                    handle = new PlayModeProductionLikeLlmHandle(
                        new OfflineLlmClient(settings),
                        PlayModeProductionLikeLlmBackend.Offline,
                        coreAiSettings: settings);
                    ignoreReason = null;
                    return true;

                default:
                    ignoreReason = "Неизвестный PlayModeProductionLikeLlmBackend.";
                    return false;
            }
        }

        private static bool TryCreateOpenAi(
            CoreAISettingsAsset settings,
            float temperature,
            int timeoutSeconds,
            out PlayModeProductionLikeLlmHandle handle,
            out string ignoreReason)
        {
            handle = null;
#if COREAI_NO_LLM
            ignoreReason = "COREAI_NO_LLM: HTTP LLM clients are excluded from build.";
            return false;
#else

            // Пробуем CoreAISettingsAsset
            if (settings != null && settings.UseHttpApi && !string.IsNullOrWhiteSpace(settings.ApiBaseUrl) &&
                !string.IsNullOrWhiteSpace(settings.ModelName))
            {
                OpenAiChatLlmClient client = new(settings);
                handle = new PlayModeProductionLikeLlmHandle(
                    client,
                    PlayModeProductionLikeLlmBackend.OpenAiCompatibleHttp,
                    coreAiSettings: settings);
                ignoreReason = null;
                return true;
            }

            // Fallback: PlayModeOpenAiTestConfig (env vars)
            string baseUrl = PlayModeOpenAiTestConfig.ResolveBaseUrl();
            string model = PlayModeOpenAiTestConfig.ResolveModelId();
            if (string.IsNullOrWhiteSpace(baseUrl) || string.IsNullOrWhiteSpace(model))
            {
                ignoreReason =
                    "OpenAI-compatible HTTP: нет настроек в CoreAISettingsAsset и не заданы COREAI_OPENAI_TEST_BASE/MODEL.";
                return false;
            }

            string apiKey = Environment.GetEnvironmentVariable("COREAI_OPENAI_TEST_API_KEY") ?? "";
            OpenAiHttpLlmSettings legacySettings = ScriptableObject.CreateInstance<OpenAiHttpLlmSettings>();
            legacySettings.SetRuntimeConfiguration(
                true,
                baseUrl.Trim().TrimEnd('/'),
                apiKey,
                model.Trim(),
                temperature,
                timeoutSeconds);

            OpenAiChatLlmClient client2 = new(legacySettings);
            handle = new PlayModeProductionLikeLlmHandle(
                client2,
                PlayModeProductionLikeLlmBackend.OpenAiCompatibleHttp,
                legacySettings);
            ignoreReason = null;
            return true;
#endif
        }

        private static bool TryCreateLlmUnity(
            CoreAISettingsAsset settings,
            out PlayModeProductionLikeLlmHandle handle,
            out string ignoreReason)
        {
            handle = null;
#if COREAI_NO_LLM || UNITY_WEBGL || !COREAI_HAS_LLMUNITY
            ignoreReason = "Сборка с COREAI_NO_LLM — LLMUnity недоступен.";
            return false;
#else
            string agentName = settings?.LlmUnityAgentName;
            string ggufPath = settings?.GgufModelPath;
            int numGpuLayers = settings != null ? settings.NumGPULayers : 99;

            GameObject go =
                PlayModeLlmUnityTestHarness.CreateRuntimeLlmAndAgent(agentName, ggufPath, numGpuLayers, out _,
                    out LLMAgent agent);
            if (go == null || agent == null)
            {
                ignoreReason =
                    "LLMUnity: не удалось поднять LLM+LLMAgent или назначить GGUF.";
                return false;
            }

            // Применяем настройки из CoreAISettingsAsset
            LLM llm = go.GetComponent<LLM>();
            if (llm != null && settings != null && settings.LlmUnityDontDestroyOnLoad)
            {
                llm.dontDestroyOnLoad = true;
            }

            MeaiLlmUnityClient client = new(agent, settings, GameLoggerUnscopedFallback.Instance, new InMemoryStore());
            handle = new PlayModeProductionLikeLlmHandle(
                client,
                PlayModeProductionLikeLlmBackend.LlmUnity,
                coreAiSettings: settings,
                llmUnityHarnessRoot: go);
            ignoreReason = null;
            return true;
#endif
        }
    }
}