using System;
using CoreAI.Ai;
using CoreAI.Infrastructure.Llm;
using UnityEngine;

namespace CoreAI.Tests.PlayMode
{
    /// <summary>
    /// Какой бэкенд использовать в Play Mode тестах с реальной моделью (см. <see cref="PlayModeProductionLikeLlmFactory.TryCreate"/>).
    /// Переменная окружения: <c>COREAI_PLAYMODE_LLM_BACKEND</c> = <c>auto</c> | <c>http</c> | <c>llmunity</c> (без значения = auto).
    /// </summary>
    public enum PlayModeProductionLikeLlmBackend
    {
        /// <summary>Как <see cref="CoreAI.Composition.CoreAILifetimeScope"/>: сначала OpenAI-compatible HTTP при наличии BASE+MODEL, иначе LLMUnity.</summary>
        Auto = 0,

        /// <summary>Только HTTP (LM Studio и т.д.).</summary>
        OpenAiCompatibleHttp = 1,

        /// <summary>Только локальный LLMUnity (рантайм LLM+LLMAgent).</summary>
        LlmUnity = 2
    }

    /// <summary>
    /// <see cref="ILlmClient"/> как в игре (HTTP или LLMUnity). Освободить после теста — уничтожает временные объекты Unity.
    /// </summary>
    public sealed class PlayModeProductionLikeLlmHandle : IDisposable
    {
        public ILlmClient Client { get; }
        public PlayModeProductionLikeLlmBackend ResolvedBackend { get; }

        private readonly OpenAiHttpLlmSettings _settingsAsset;
        private readonly GameObject _llmUnityHarnessRoot;

        internal PlayModeProductionLikeLlmHandle(
            ILlmClient client,
            PlayModeProductionLikeLlmBackend resolvedBackend,
            OpenAiHttpLlmSettings settingsAsset,
            GameObject llmUnityHarnessRoot)
        {
            Client = client;
            ResolvedBackend = resolvedBackend;
            _settingsAsset = settingsAsset;
            _llmUnityHarnessRoot = llmUnityHarnessRoot;
        }

        public void Dispose()
        {
            if (_llmUnityHarnessRoot != null)
                UnityEngine.Object.Destroy(_llmUnityHarnessRoot);
            if (_settingsAsset != null)
                UnityEngine.Object.Destroy(_settingsAsset);
        }
    }

    /// <summary>
    /// Единая точка создания «продакшен-подобного» <see cref="ILlmClient"/> для Play Mode: тот же выбор, что в <see cref="CoreAI.Composition.CoreAILifetimeScope"/> (HTTP vs LLMUnity).
    /// </summary>
    public static partial class PlayModeProductionLikeLlmFactory
    {
        private const string EnvBackend = "COREAI_PLAYMODE_LLM_BACKEND";

        /// <summary>Если <paramref name="explicitPreference"/> null — читается <c>COREAI_PLAYMODE_LLM_BACKEND</c>, иначе <see cref="PlayModeProductionLikeLlmBackend.Auto"/>.</summary>
        public static PlayModeProductionLikeLlmBackend ResolvePreference(PlayModeProductionLikeLlmBackend? explicitPreference)
        {
            if (explicitPreference.HasValue)
                return explicitPreference.Value;
            return ParseEnvBackend();
        }

        public static PlayModeProductionLikeLlmBackend ParseEnvBackend()
        {
            var v = Environment.GetEnvironmentVariable(EnvBackend);
            if (string.IsNullOrWhiteSpace(v))
                return PlayModeProductionLikeLlmBackend.Auto;
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
            var pref = ResolvePreference(explicitPreference);

            switch (pref)
            {
                case PlayModeProductionLikeLlmBackend.Auto:
                    if (TryCreateOpenAi(openAiTemperature, openAiTimeoutSeconds, out handle, out var ignHttp))
                        return true;
                    if (TryCreateLlmUnity(out handle, out var ignUnity))
                        return true;
                    ignoreReason =
                        "Режим Auto: нет OpenAI-compatible HTTP (COREAI_OPENAI_TEST_BASE/MODEL или USE_PROJECT_DEFAULTS) и нет LLMUnity GGUF. " +
                        ignHttp + " " + ignUnity;
                    return false;

                case PlayModeProductionLikeLlmBackend.OpenAiCompatibleHttp:
                    return TryCreateOpenAi(openAiTemperature, openAiTimeoutSeconds, out handle, out ignoreReason);

                case PlayModeProductionLikeLlmBackend.LlmUnity:
                    return TryCreateLlmUnity(out handle, out ignoreReason);

                default:
                    ignoreReason = "Неизвестный PlayModeProductionLikeLlmBackend.";
                    return false;
            }
        }

        private static bool TryCreateOpenAi(
            float temperature,
            int timeoutSeconds,
            out PlayModeProductionLikeLlmHandle handle,
            out string ignoreReason)
        {
            handle = null;
            var baseUrl = PlayModeOpenAiTestConfig.ResolveBaseUrl();
            var model = PlayModeOpenAiTestConfig.ResolveModelId();
            if (string.IsNullOrWhiteSpace(baseUrl) || string.IsNullOrWhiteSpace(model))
            {
                ignoreReason =
                    "OpenAI-compatible HTTP: задайте COREAI_OPENAI_TEST_BASE и COREAI_OPENAI_TEST_MODEL или COREAI_OPENAI_TEST_USE_PROJECT_DEFAULTS=1.";
                return false;
            }

            var apiKey = Environment.GetEnvironmentVariable("COREAI_OPENAI_TEST_API_KEY") ?? "";
            var settings = ScriptableObject.CreateInstance<OpenAiHttpLlmSettings>();
            settings.SetRuntimeConfiguration(
                true,
                baseUrl.Trim().TrimEnd('/'),
                apiKey,
                model.Trim(),
                temperature,
                timeoutSeconds);

            var client = new OpenAiChatLlmClient(settings);
            handle = new PlayModeProductionLikeLlmHandle(
                client,
                PlayModeProductionLikeLlmBackend.OpenAiCompatibleHttp,
                settings,
                null);
            ignoreReason = null;
            return true;
        }

        private static bool TryCreateLlmUnity(out PlayModeProductionLikeLlmHandle handle, out string ignoreReason)
        {
            handle = null;
#if COREAI_NO_LLM
            ignoreReason = "Сборка с COREAI_NO_LLM — LLMUnity недоступен.";
            return false;
#else
            var go = PlayModeLlmUnityTestHarness.CreateRuntimeLlmAndAgent(out _, out var agent);
            if (go == null || agent == null)
            {
                ignoreReason =
                    "LLMUnity: не удалось поднять LLM+LLMAgent или назначить GGUF (Model Manager, предпочтение qwen+0.8 в имени файла).";
                return false;
            }

            var client = new LlmUnityLlmClient(agent);
            handle = new PlayModeProductionLikeLlmHandle(client, PlayModeProductionLikeLlmBackend.LlmUnity, null, go);
            ignoreReason = null;
            return true;
#endif
        }
    }
}
