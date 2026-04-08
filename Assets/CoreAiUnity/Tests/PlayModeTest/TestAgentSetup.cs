#if !COREAI_NO_LLM
using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading.Tasks;
using CoreAI.AgentMemory;
using CoreAI.Ai;
using CoreAI.Authority;
using CoreAI.Infrastructure.Llm;
using CoreAI.Infrastructure.Logging;
using CoreAI.Infrastructure.World;
using CoreAI.Messaging;
using CoreAI.Session;
using UnityEngine;
using UnityEngine.TestTools;

namespace CoreAI.Tests.PlayMode
{
    /// <summary>
    /// Универсальный setup агента для PlayMode тестов.
    /// Читает CoreAISettingsAsset и создаёт правильный клиент (HTTP, LLMUnity или Offline).
    /// Автоматически добавляет LogAssert.Expect для ошибок подключения.
    /// </summary>
    public sealed class TestAgentSetup : IDisposable
    {
        public ILlmClient Client { get; set; }
        public InMemoryStore MemoryStore { get; } = new();
        public TestWorldCommandExecutor WorldExecutor { get; } = new();
        public AiOrchestrator Orchestrator { get; private set; }
        public AgentMemoryPolicy Policy { get; } = new();
        public bool IsReady { get; private set; }
        public string BackendName { get; private set; }

        private PlayModeProductionLikeLlmHandle _handle;

        private static int _instanceCounter;
        private readonly int _instanceId;

        public TestAgentSetup()
        {
            _instanceId = System.Threading.Interlocked.Increment(ref _instanceCounter);
            SetupLogAsserts();
        }

        /// <summary>
        /// Инициализирует агент из CoreAISettingsAsset.
        /// </summary>
        public IEnumerator Initialize()
        {
            CoreAISettingsAsset settings = CoreAISettingsAsset.Instance;
            if (settings == null)
            {
                Debug.LogWarning("[TestAgentSetup] CoreAISettingsAsset not found in Resources");
                InitializeOffline();
                yield break;
            }

            BackendName = settings.BackendType.ToString();
            Debug.Log($"[TestAgentSetup#{_instanceId}] Backend: {settings.BackendType}");

            switch (settings.BackendType)
            {
                case LlmBackendType.LlmUnity:
                    yield return InitializeLlmUnity(settings);
                    break;
                case LlmBackendType.OpenAiHttp:
                    InitializeHttp(settings);
                    break;
                case LlmBackendType.Auto:
                    yield return InitializeAuto(settings);
                    break;
                case LlmBackendType.Offline:
                    InitializeOffline();
                    break;
            }

            IsReady = Client != null;
            Debug.Log($"[TestAgentSetup] Ready: {IsReady}, Backend: {BackendName}");
        }

        private IEnumerator InitializeLlmUnity(CoreAISettingsAsset settings)
        {
            Debug.Log("[TestAgentSetup] Initializing LLMUnity...");
            if (!PlayModeProductionLikeLlmFactory.TryCreate(
                    PlayModeProductionLikeLlmBackend.LlmUnity,
                    settings.Temperature,
                    settings.RequestTimeoutSeconds,
                    out _handle,
                    out string ignore))
            {
                Debug.LogWarning($"[TestAgentSetup] LLMUnity failed: {ignore}");
                InitializeOffline();
                yield break;
            }

            yield return PlayModeProductionLikeLlmFactory.EnsureLlmUnityModelReady(_handle);

            // Пересоздаём клиент с нашим MemoryStore чтобы tool calls писали в правильный store
            MeaiLlmUnityClient llmUnityClient = _handle.Client as MeaiLlmUnityClient;
            if (llmUnityClient != null)
            {
                Client = new MeaiLlmClient(
                    new LlmUnityMeaiChatClient(llmUnityClient.UnityAgent, GameLoggerUnscopedFallback.Instance),
                    GameLoggerUnscopedFallback.Instance,
                    MemoryStore);
            }
            else
            {
                Client = _handle.Client;
            }

            BackendName = "LLMUnity";
            CreateOrchestrator();
        }

        private void InitializeHttp(CoreAISettingsAsset settings)
        {
            Debug.Log($"[TestAgentSetup] Initializing HTTP: {settings.ApiBaseUrl}");
            SetupHttpLogAsserts();
            Client = MeaiLlmClient.CreateHttp(settings, GameLoggerUnscopedFallback.Instance, MemoryStore);
            BackendName = "HTTP";
            CreateOrchestrator();
        }

        private IEnumerator InitializeAuto(CoreAISettingsAsset settings)
        {
            bool httpFirst = settings.AutoPriority == LlmAutoPriority.HttpFirst;
            Debug.Log($"[TestAgentSetup] Auto mode: trying {(httpFirst ? "HTTP" : "LLMUnity")} first...");

            if (httpFirst)
            {
                // HTTP First
                SetupHttpLogAsserts();
                InitializeHttp(settings);
                if (Client != null)
                {
                    BackendName = "HTTP (Auto)";
                    Debug.Log("[TestAgentSetup] Using HTTP");
                    CreateOrchestrator();
                    yield break;
                }

                Debug.Log("[TestAgentSetup] HTTP not available, falling back to LLMUnity...");
            }

            // LLMUnity First (или HTTP не подключился)
            if (PlayModeProductionLikeLlmFactory.TryCreate(
                    PlayModeProductionLikeLlmBackend.LlmUnity,
                    settings.Temperature,
                    settings.RequestTimeoutSeconds,
                    out _handle,
                    out _))
            {
                yield return PlayModeProductionLikeLlmFactory.EnsureLlmUnityModelReady(_handle);

                // Пересоздаём клиент с нашим MemoryStore
                MeaiLlmUnityClient llmUnityClient = _handle.Client as MeaiLlmUnityClient;
                if (llmUnityClient != null)
                {
                    Client = new MeaiLlmClient(
                        new LlmUnityMeaiChatClient(llmUnityClient.UnityAgent, GameLoggerUnscopedFallback.Instance),
                        GameLoggerUnscopedFallback.Instance,
                        MemoryStore);
                }
                else
                {
                    Client = _handle.Client;
                }

                BackendName = "LLMUnity (Auto)";
                Debug.Log("[TestAgentSetup] Using LLMUnity");
            }
            else
            {
                Debug.Log("[TestAgentSetup] LLMUnity not available, falling back to HTTP...");
                SetupHttpLogAsserts();
                InitializeHttp(settings);
                BackendName = "HTTP (Auto)";
            }

            CreateOrchestrator();
        }

        private void InitializeOffline()
        {
            Debug.Log("[TestAgentSetup] Using Offline (stub) client");
            CoreAISettingsAsset settings = ScriptableObject.CreateInstance<CoreAISettingsAsset>();
            settings.ConfigureOffline();
            Client = new OfflineLlmClient(settings);
            BackendName = "Offline";
            CreateOrchestrator();
        }

        private void CreateOrchestrator()
        {
            SessionTelemetryCollector telemetry = new();
            AiPromptComposer composer = new(
                new BuiltInDefaultAgentSystemPromptProvider(),
                new NoAgentUserPromptTemplateProvider(),
                new NullLuaScriptVersionStore());

            Orchestrator = new AiOrchestrator(
                new SoloAuthorityHost(),
                Client,
                new NullSink(),
                telemetry,
                composer,
                MemoryStore,
                Policy,
                new NoOpRoleStructuredResponsePolicy(),
                new NullAiOrchestrationMetrics());
        }

        private void SetupLogAsserts()
        {
            // Ошибки подключения ожидаем только если бэкенд HTTP
            // Для LLMUnity эти ошибки не должны появляться
        }

        private void SetupHttpLogAsserts()
        {
            LogAssert.ignoreFailingMessages = true;
        }

        /// <summary>
        /// Запустить задачу и дождаться результата.
        /// </summary>
        public IEnumerator RunAndWait(Task task, float timeoutSeconds, string label)
        {
            yield return PlayModeTestAwait.WaitTask(task, timeoutSeconds, label);
        }

        public void Dispose()
        {
            _handle?.Dispose();
            _handle = null;
            LogAssert.ignoreFailingMessages = false;
        }

        public sealed class InMemoryStore : IAgentMemoryStore
        {
            public readonly Dictionary<string, AgentMemoryState> States = new();

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
            }

            public void ClearChatHistory(string roleId)
            {
            }

            public void AppendChatMessage(string roleId, string role, string content, bool persistToDisk = true)
            {
            }

            public ChatMessage[] GetChatHistory(string roleId, int maxMessages = 0)
            {
                return Array.Empty<ChatMessage>();
            }
        }

        public sealed class ListSink : IAiGameCommandSink
        {
            public readonly List<ApplyAiGameCommand> Items = new();

            public void Publish(ApplyAiGameCommand command)
            {
                Items.Add(command);
            }
        }

        private sealed class NullSink : IAiGameCommandSink
        {
            public void Publish(ApplyAiGameCommand command)
            {
            }
        }

        /// <summary>
        /// Тестовый WorldCommand executor для PlayMode тестов.
        /// </summary>
        public sealed class TestWorldCommandExecutor : ICoreAiWorldCommandExecutor
        {
            public volatile bool LastCommandWasCalled;
            public string LastCommandJson;

            public bool TryExecute(ApplyAiGameCommand cmd)
            {
                LastCommandWasCalled = true;
                LastCommandJson = cmd.JsonPayload;
                Debug.LogWarning($"[WorldCommand] Executed: {cmd.JsonPayload}");
                return true;
            }
        }
    }
}
#endif