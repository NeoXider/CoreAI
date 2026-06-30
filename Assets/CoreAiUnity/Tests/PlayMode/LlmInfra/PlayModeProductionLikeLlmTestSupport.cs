using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
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
    /// Helper   ILlmClient   IAgentMemoryStore.
    ///  :      InMemoryStore,
    ///     store  .
    /// </summary>
    public static class LlmClientTestHelpers
    {
        /// <summary>
        ///    PlayModeProductionLikeLlmHandle   IAgentMemoryStore.
        ///  LLMUnity   MeaiLlmClient   store.
        ///  HTTP     (HTTP      null store).
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
            // LLMUnity      MemoryStore
            if (handle.ResolvedBackend == PlayModeProductionLikeLlmBackend.LlmUnity)
            {
                MeaiLlmUnityClient llmUnityClient = handle.Client as MeaiLlmUnityClient;
                if (llmUnityClient != null)
                {
                    return new MeaiLlmClient(
                        new LlmUnityMeaiChatClient(llmUnityClient.UnityAgent, GameLoggerUnscopedFallback.Instance),
                        GameLoggerUnscopedFallback.Instance,
                        ScriptableObject.CreateInstance<CoreAISettingsAsset>(),
                        memoryStore);
                }
            }
#endif

            // HTTP      OpenAiChatLlmClient   
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

            // Offline      
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
    ///     Play Mode     (. <see cref="PlayModeProductionLikeLlmFactory.TryCreate"/>).
    ///  : <c>COREAI_PLAYMODE_LLM_BACKEND</c> = <c>auto</c> | <c>http</c> | <c>llmunity</c> | <c>offline</c> (  = auto  CoreAISettingsAsset).
    /// : 1) CoreAISettingsAsset  2) Env var  3) Auto fallback.
    /// </summary>
    public enum PlayModeProductionLikeLlmBackend
    {
        /// <summary> CoreAISettingsAsset.BackendType.  Auto: LLMUnity  HTTP API  Offline.</summary>
        FromSettings = -1,

        /// <summary> <see cref="CoreAI.Composition.CoreAILifetimeScope"/>: LLMUnity  HTTP API  Offline.</summary>
        Auto = 0,

        /// <summary> HTTP (LM Studio  ..).</summary>
        OpenAiCompatibleHttp = 1,

        /// <summary>  LLMUnity ( LLM+LLMAgent).</summary>
        LlmUnity = 2,

        /// <summary>      LLM.</summary>
        Offline = 3
    }

    /// <summary>
    /// <see cref="ILlmClient"/>    (HTTP  LLMUnity).        Unity.
    /// </summary>
    public sealed class PlayModeProductionLikeLlmHandle : IDisposable
    {
        public ILlmClient Client { get; }
        public PlayModeProductionLikeLlmBackend ResolvedBackend { get; }

        /// <summary>
        /// The resolved live-test configuration for the HTTP path (base URL / model / streaming / native-tools),
        /// or <c>null</c> when the backend was not driven by the unified config surface.
        /// </summary>
        public PlayModeOpenAiTestConfig.ResolvedConfig ResolvedConfig { get; }

        internal readonly OpenAiHttpLlmSettings _openAiSettings;
        internal readonly CoreAISettingsAsset _coreAiSettings;
        private readonly GameObject _llmUnityHarnessRoot;

        // True when _coreAiSettings was created by the factory (env/file path) rather than being
        // the shared CoreAISettingsAsset.Instance; only owned assets are destroyed on Dispose.
        private readonly bool _ownsCoreAiSettings;

        internal PlayModeProductionLikeLlmHandle(
            ILlmClient client,
            PlayModeProductionLikeLlmBackend resolvedBackend,
            OpenAiHttpLlmSettings openAiSettings = null,
            CoreAISettingsAsset coreAiSettings = null,
            GameObject llmUnityHarnessRoot = null,
            bool ownsCoreAiSettings = false,
            PlayModeOpenAiTestConfig.ResolvedConfig resolvedConfig = null)
        {
            Client = client;
            ResolvedBackend = resolvedBackend;
            _openAiSettings = openAiSettings;
            _coreAiSettings = coreAiSettings;
            _llmUnityHarnessRoot = llmUnityHarnessRoot;
            _ownsCoreAiSettings = ownsCoreAiSettings;
            ResolvedConfig = resolvedConfig;
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

            // Destroy only factory-created settings; never the shared CoreAISettingsAsset.Instance.
            if (_ownsCoreAiSettings && _coreAiSettings != null)
            {
                UnityEngine.Object.DestroyImmediate(_coreAiSettings);
            }
        }
    }

    /// <summary>
    ///    - <see cref="ILlmClient"/>  Play Mode:
    ///  <see cref="CoreAISettingsAsset"/>        .
    /// : 1) CoreAISettingsAsset  2) Env var  3) Auto fallback.
    /// </summary>
    public static partial class PlayModeProductionLikeLlmFactory
    {
        private const string EnvBackend = "COREAI_PLAYMODE_LLM_BACKEND";

        /// <summary>
        ///  : CoreAISettingsAsset  Env var  Auto.
        ///  <paramref name="explicitPreference"/>     .
        /// </summary>
        public static PlayModeProductionLikeLlmBackend ResolvePreference(
            PlayModeProductionLikeLlmBackend? explicitPreference)
        {
            if (explicitPreference.HasValue &&
                explicitPreference.Value != PlayModeProductionLikeLlmBackend.FromSettings)
            {
                return explicitPreference.Value;
            }

            // 1.  CoreAISettingsAsset
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
        ///   .  <see cref="PlayModeProductionLikeLlmBackend.LlmUnity"/>   <see cref="EnsureLlmUnityModelReady"/>  .
        /// </summary>
        public static bool TryCreate(
            PlayModeProductionLikeLlmBackend? explicitPreference,
            float openAiTemperature,
            int openAiTimeoutSeconds,
            out PlayModeProductionLikeLlmHandle handle,
            out string ignoreReason)
        {
            return TryCreate(explicitPreference, openAiTemperature, openAiTimeoutSeconds, null, out handle,
                out ignoreReason);
        }

        /// <summary>
        ///   ,    per-test model override ( vision-).
        ///  <paramref name="modelOverride"/>      OpenAI-compatible HTTP.
        /// </summary>
        public static bool TryCreate(
            PlayModeProductionLikeLlmBackend? explicitPreference,
            float openAiTemperature,
            int openAiTimeoutSeconds,
            string modelOverride,
            out PlayModeProductionLikeLlmHandle handle,
            out string ignoreReason)
        {
            handle = null;
            ignoreReason = null;
            PlayModeProductionLikeLlmBackend pref = ResolvePreference(explicitPreference);

            //    CoreAISettingsAsset
            CoreAISettingsAsset settings = CoreAISettingsAsset.Instance;

            switch (pref)
            {
                case PlayModeProductionLikeLlmBackend.FromSettings:
                case PlayModeProductionLikeLlmBackend.Auto:
                    // Auto:   CoreAISettingsAsset.AutoPriority
                    bool httpFirst = settings != null && settings.AutoPriority == LlmAutoPriority.HttpFirst;

                    if (httpFirst)
                    {
                        // HTTP API  LLMUnity  Offline
                        if (TryCreateOpenAi(settings, openAiTemperature, openAiTimeoutSeconds, modelOverride,
                                out handle, out _))
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
                        // LLMUnity  HTTP API  Offline ( )
                        if (TryCreateLlmUnity(settings, out handle, out _))
                        {
                            return true;
                        }

                        if (TryCreateOpenAi(settings, openAiTemperature, openAiTimeoutSeconds, modelOverride,
                                out handle, out _))
                        {
                            return true;
                        }
                    }

                    // Fallback  Offline
                    handle = new PlayModeProductionLikeLlmHandle(
                        new OfflineLlmClient(settings),
                        PlayModeProductionLikeLlmBackend.Offline,
                        coreAiSettings: settings);
                    ignoreReason = null;
                    return true;

                case PlayModeProductionLikeLlmBackend.OpenAiCompatibleHttp:
                    return TryCreateOpenAi(settings, openAiTemperature, openAiTimeoutSeconds, modelOverride,
                        out handle, out ignoreReason);

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
                    ignoreReason = " PlayModeProductionLikeLlmBackend.";
                    return false;
            }
        }

        private static bool TryCreateOpenAi(
            CoreAISettingsAsset settings,
            float temperature,
            int timeoutSeconds,
            string modelOverride,
            out PlayModeProductionLikeLlmHandle handle,
            out string ignoreReason)
        {
            handle = null;
#if COREAI_NO_LLM
            ignoreReason = "COREAI_NO_LLM: HTTP LLM clients are excluded from build.";
            return false;
#else
            // Unified resolution surface: env vars > gitignored local file > project defaults (opt-in).
            // Env/file overrides the settings asset; the asset is the auto-detect fallback below.
            PlayModeOpenAiTestConfig.ResolvedConfig config = PlayModeOpenAiTestConfig.Resolve(modelOverride);

            // 1) Explicit env/file configuration wins (and is the only place streaming/native-tools toggles live).
            if (config.IsComplete)
            {
                // Wire config (base URL / key / model / temperature / timeout).
                OpenAiHttpLlmSettings httpSettings = ScriptableObject.CreateInstance<OpenAiHttpLlmSettings>();
                httpSettings.SetRuntimeConfiguration(
                    true,
                    config.BaseUrl,
                    config.ApiKey,
                    config.Model,
                    temperature,
                    timeoutSeconds);

                // Behavioral config (streaming etc.) carried by an ICoreAISettings snapshot.
                CoreAISettingsAsset behavior = BuildBehaviorSettings(config.Streaming, timeoutSeconds);

                ILlmClient httpClient = MeaiLlmClient.CreateHttp(
                    httpSettings, behavior, GameLoggerUnscopedFallback.Instance);

                // CreateHttp wires native tool calling ON; honor an explicit native-tools=false toggle.
                if (!config.NativeTools)
                {
                    httpClient = new NonNativeToolsLlmClientDecorator(httpClient);
                }

                handle = new PlayModeProductionLikeLlmHandle(
                    httpClient,
                    PlayModeProductionLikeLlmBackend.OpenAiCompatibleHttp,
                    openAiSettings: httpSettings,
                    coreAiSettings: behavior,
                    ownsCoreAiSettings: true,
                    resolvedConfig: config);
                ignoreReason = null;
                return true;
            }

            // 2) Auto-detect: a fully configured CoreAISettingsAsset (HTTP backend) from the project.
            if (settings != null && settings.UseHttpApi && !string.IsNullOrWhiteSpace(settings.ApiBaseUrl) &&
                !string.IsNullOrWhiteSpace(settings.ModelName))
            {
                ILlmClient client = new OpenAiChatLlmClient(settings);
                if (modelOverride != null && !string.IsNullOrWhiteSpace(modelOverride))
                {
                    // The asset path cannot retarget the model without mutating the shared asset; surface a clear hint.
                    Debug.LogWarning(
                        "[PlayModeProductionLikeLlmFactory] modelOverride is ignored when the project " +
                        "CoreAISettingsAsset drives the HTTP backend. Set COREAI_TEST_BASE_URL/MODEL " +
                        "(or the local config file) to use per-test model overrides.");
                }

                handle = new PlayModeProductionLikeLlmHandle(
                    client,
                    PlayModeProductionLikeLlmBackend.OpenAiCompatibleHttp,
                    coreAiSettings: settings);
                ignoreReason = null;
                return true;
            }

            // 3) Unconfigured: tell the developer exactly what to set.
            ignoreReason = PlayModeOpenAiTestConfig.BuildIgnoreReason(config);
            return false;
#endif
        }

#if !COREAI_NO_LLM
        /// <summary>
        /// Builds a throwaway <see cref="CoreAISettingsAsset"/> carrying the resolved behavioral flags
        /// (streaming, orchestration timeout). The asset has no public streaming setter, so the serialized
        /// field is set via reflection — a deliberate test-only escape hatch that avoids touching runtime code.
        /// </summary>
        private static CoreAISettingsAsset BuildBehaviorSettings(bool enableStreaming, int timeoutSeconds)
        {
            CoreAISettingsAsset behavior = ScriptableObject.CreateInstance<CoreAISettingsAsset>();
            behavior.SetOrchestratorTimeoutSeconds(timeoutSeconds);

            // The tool-call roundtrip cap lives on the SETTINGS the HTTP client reads (SmartToolCallingChatClient
            // resolves _settings.MaxToolCallRoundtrips), NOT on the per-run benchmark settings passed to the
            // orchestrator. Benchmark scenes such as the G6 free-build emit 24+ spawns, well past the default 10,
            // so honor COREAI_BENCHMARK_ROUNDTRIPS here too — otherwise the run is silently capped at 10.
            // Note: this is the GLOBAL fallback. A scenario can additionally set a PER-AGENT override via
            // AgentBuilder.WithMaxToolCallRoundtrips (G6 uses 0 = unlimited), which takes priority over this.
            string roundtripsRaw = System.Environment.GetEnvironmentVariable("COREAI_BENCHMARK_ROUNDTRIPS");
            if (!string.IsNullOrWhiteSpace(roundtripsRaw)
                && int.TryParse(roundtripsRaw, out int roundtrips) && roundtrips >= 1)
            {
                behavior.SetMaxToolCallRoundtrips(roundtrips);
            }
            else
            {
                // Benchmark default: match GameCreationBenchmarkPlayModeTests.ResolveBenchmarkRoundtrips (40),
                // so a free-build scene is never throttled to the production default of 10 by accident.
                behavior.SetMaxToolCallRoundtrips(40);
            }

            if (!enableStreaming)
            {
                System.Reflection.FieldInfo field = typeof(CoreAISettingsAsset).GetField(
                    "enableStreaming",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                field?.SetValue(behavior, false);
            }

            return behavior;
        }
#endif

        private static bool TryCreateLlmUnity(
            CoreAISettingsAsset settings,
            out PlayModeProductionLikeLlmHandle handle,
            out string ignoreReason)
        {
            handle = null;
#if COREAI_NO_LLM || UNITY_WEBGL || !COREAI_HAS_LLMUNITY
            ignoreReason = "  COREAI_NO_LLM  LLMUnity .";
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
                    "LLMUnity:    LLM+LLMAgent   GGUF.";
                return false;
            }

            //    CoreAISettingsAsset
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

#if !COREAI_NO_LLM
    /// <summary>
    /// Forwarding <see cref="ILlmClient"/> decorator that forces native tool calling OFF, so the
    /// orchestrator falls back to the text/prompt tool contract. Used by the live-suite config when
    /// <c>COREAI_TEST_NATIVE_TOOLS=false</c> against providers/models whose native function-calling is
    /// unreliable. All completion/streaming behavior is delegated unchanged to the inner client.
    /// </summary>
    internal sealed class NonNativeToolsLlmClientDecorator : ILlmClient
    {
        private readonly ILlmClient _inner;

        public NonNativeToolsLlmClientDecorator(ILlmClient inner)
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        }

        public bool SupportsNativeToolCalling => false;

        public bool SupportsNativeToolCallingForRole(string agentRoleId) => false;

        public void SetTools(IReadOnlyList<ILlmTool> tools) => _inner.SetTools(tools);

        public Task<LlmCompletionResult> CompleteAsync(
            LlmCompletionRequest request,
            System.Threading.CancellationToken cancellationToken = default)
        {
            return _inner.CompleteAsync(request, cancellationToken);
        }

        public async IAsyncEnumerable<LlmStreamChunk> CompleteStreamingAsync(
            LlmCompletionRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation]
            System.Threading.CancellationToken cancellationToken = default)
        {
            await foreach (LlmStreamChunk chunk in _inner.CompleteStreamingAsync(request, cancellationToken))
            {
                yield return chunk;
            }
        }
    }
#endif
}