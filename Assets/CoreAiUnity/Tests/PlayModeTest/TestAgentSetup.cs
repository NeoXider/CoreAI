#if !COREAI_NO_LLM && !UNITY_WEBGL
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
    ///  setup   PlayMode .
    ///  CoreAISettingsAsset     (HTTP, LLMUnity  Offline).
    ///   LogAssert.Expect   .
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
        ///    CoreAISettingsAsset.
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
#if COREAI_HAS_LLMUNITY
            Debug.Log("[TestAgentSetup] Initializing LLMUnity via SharedLlmUnity...");

            // SharedLlmUnity:  LLM   ,  create/destroy  
            yield return SharedLlmUnity.EnsureInitialized();

            if (!SharedLlmUnity.IsReady)
            {
                Debug.LogWarning($"[TestAgentSetup] SharedLlmUnity not ready: {SharedLlmUnity.Error}");
                InitializeOffline();
                yield break;
            }

            //     LLMAgent   MemoryStore   
            Client = SharedLlmUnity.CreateClientWithMemoryStore(MemoryStore);
            if (Client == null)
            {
                Debug.LogWarning("[TestAgentSetup] Failed to create client from SharedLlmUnity");
                InitializeOffline();
                yield break;
            }

            // _handle       LLM, SharedLlmUnity    
            BackendName = "LLMUnity";
            CreateOrchestrator();
#else
            Debug.LogWarning("[TestAgentSetup] LLMUnity package not available, falling back to Offline");
            InitializeOffline();
            yield break;
#endif
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

            // LLMUnity  SharedLlmUnity
#if COREAI_HAS_LLMUNITY
            yield return SharedLlmUnity.EnsureInitialized();
            if (SharedLlmUnity.IsReady)
            {
                Client = SharedLlmUnity.CreateClientWithMemoryStore(MemoryStore);
                if (Client != null)
                {
                    BackendName = "LLMUnity (Auto)";
                    Debug.Log("[TestAgentSetup] Using SharedLlmUnity");
                    CreateOrchestrator();
                    yield break;
                }
            }
#else
            Debug.Log("[TestAgentSetup] LLMUnity package not available, skipping.");
            yield return null;
#endif

            if (!httpFirst)
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
                new NullAiOrchestrationMetrics(), UnityEngine.ScriptableObject.CreateInstance<CoreAI.Infrastructure.Llm.CoreAISettingsAsset>());
        }

        private void SetupLogAsserts()
        {
            //       HTTP
            //  LLMUnity     
        }

        private void SetupHttpLogAsserts()
        {
            LogAssert.ignoreFailingMessages = true;
        }

        /// <summary>
        ///     .
        /// </summary>
        public IEnumerator RunAndWait(Task task, float timeoutSeconds, string label)
        {
            yield return PlayModeTestAwait.WaitTask(task, timeoutSeconds, label);
        }

        public void Dispose()
        {
            // Dispose  HTTP handle. SharedLlmUnity   
            //    LlmUnityGlobalSetup.OneTimeTearDown.
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
        ///  WorldCommand executor  PlayMode .
        /// </summary>
        public sealed class TestWorldCommandExecutor : ICoreAiWorldCommandExecutor
        {
            public volatile bool LastCommandWasCalled;
            public string LastCommandJson;
            public System.Collections.Generic.List<string> AllCommandsJson = new();

            public string[] LastListedAnimations { get; private set; } = System.Array.Empty<string>();
            public System.Collections.Generic.List<System.Collections.Generic.Dictionary<string, object>> LastListedObjects { get; private set; } = new();

            public bool TryExecute(ApplyAiGameCommand cmd)
            {
                LastCommandWasCalled = true;
                LastCommandJson = cmd.JsonPayload;
                AllCommandsJson.Add(cmd.JsonPayload);
                Debug.LogWarning($"[WorldCommand] Executed: {cmd.JsonPayload}");
                return true;
            }
        }
    }
}
#endif

