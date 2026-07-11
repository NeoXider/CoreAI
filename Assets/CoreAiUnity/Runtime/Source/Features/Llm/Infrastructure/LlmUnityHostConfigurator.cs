#if COREAI_HAS_LLMUNITY && !UNITY_WEBGL
using System;
using System.IO;
using CoreAI.Infrastructure.Logging;
using LLMUnity;
using UnityEngine;

namespace CoreAI.Infrastructure.Llm
{
    /// <summary>
    /// Creates and configures runtime LLMUnity host objects.
    /// </summary>
    public static class LlmUnityHostConfigurator
    {
        /// <summary>
        /// Applies CoreAI settings to the runtime LLMUnity host objects.
        /// </summary>
        public static void ApplyFromSettings(LLM llm, LLMAgent agent, CoreAISettingsAsset settings, IGameLogger logger)
        {
            if (llm == null || agent == null || settings == null || logger == null)
            {
                return;
            }

            agent.llm = llm;

            if (agent.overflowStrategy != UndreamAI.LlamaLib.ContextOverflowStrategy.None)
            {
                logger.LogWarning(
                    GameLogFeature.Llm,
                    $"LLMUnity: LLMAgent '{agent.name}' had overflowStrategy={agent.overflowStrategy}, but CoreAI " +
                    "owns conversation-context management and forces it to None. LLMUnity's overflow handling never " +
                    "runs here because CoreAI builds the whole prompt and calls Chat(addToHistory: false).");
            }

            // CoreAI owns conversation-context management (LlmAssistedConversationContextManager builds
            // the whole prompt and calls Chat(addToHistory: false)), so LLMUnity's own overflow handling
            // has no history to act on. Force it to None so it can never silently truncate/summarize
            // behind CoreAI's back and so the Inspector no longer implies LLMUnity manages the context -
            // this mirrors the HTTP/OpenAI path, where the server never manages history either.
            // overflowStrategy lives on LLMAgent and is not start-guarded, so it is safe to set anytime.
            agent.overflowStrategy = UndreamAI.LlamaLib.ContextOverflowStrategy.None;

            // Everything below configures the native LLM server and MUST happen before it starts.
            // LLMUnity guards remote/port/numGPULayers/flashAttention/model with AssertNotStarted (or an
            // implicit RestartServer) and logs an error if they are set after the server has started -
            // which happens when a scene-placed LLM auto-starts in Awake before CoreAI discovers it.
            // Skip start-sensitive configuration in that case instead of tripping LLMUnity's guard.
            if (llm.started)
            {
                logger.LogWarning(
                    GameLogFeature.Llm,
                    $"LLMUnity: LLM '{llm.name}' had already started before CoreAI could configure it, so " +
                    "remote/port/numGPULayers/flashAttention/model were left untouched. For CoreAI to drive a " +
                    "scene-placed LLM over HTTP, let CoreAI create the LLM or set Remote + Port on it before play.");
                return;
            }

            // CoreAI drives LLMUnity as an OpenAI-compatible HTTP server rather than calling
            // LLMAgent.Chat() in-process. llm.remote/llm.port MUST be set before anything can start
            // the native service (CreateServiceAsync runs SetupServer(StartServer) then Start()) -
            // flipping remote=true on an already-started LLM does not bind the socket. Setting them
            // first here, ahead of any Start()/warmup, guarantees LLMUnity's llama.cpp server binds
            // to CoreAI's configured port and exposes POST /v1/chat/completions with native tool_calls.
            llm.remote = true;
            llm.port = settings.LlmUnityServerPort;

            llm.dontDestroyOnLoad = settings.LlmUnityDontDestroyOnLoad;
            llm.numGPULayers = settings.NumGPULayers;
            llm.flashAttention = true;

            if (string.IsNullOrWhiteSpace(llm.model))
            {
                bool assigned = LlmUnityModelBootstrap.TryAssignModelFromGgufHint(llm, logger, settings.GgufModelPath);
                if (!assigned)
                {
                    LlmUnityModelBootstrap.TryAutoAssignResolvableModel(llm, logger);
                }
            }
        }
    }

    /// <summary>
    /// Runtime holder for LLMUnity objects created by CoreAI.
    /// </summary>
    public static class LlmUnityRuntimeHost
    {
        /// <summary>Creates and configures an LLMAgent from CoreAI settings.</summary>
        public static LLMAgent Create(CoreAISettingsAsset settings, IGameLogger logger)
        {
            if (settings == null || logger == null)
            {
                throw new ArgumentNullException(settings == null ? nameof(settings) : nameof(logger));
            }

            string goName = string.IsNullOrWhiteSpace(settings.LlmUnityRuntimeHostObjectName)
                ? "CoreAI_LLMUnity_Runtime"
                : settings.LlmUnityRuntimeHostObjectName.Trim();

            GameObject go = new(goName);
            go.SetActive(false);
            LLM llm = go.AddComponent<LLM>();
            LLMAgent agent = go.AddComponent<LLMAgent>();

            LlmUnityHostConfigurator.ApplyFromSettings(llm, agent, settings, logger);

            if (settings.LlmUnityDontDestroyOnLoad)
            {
                UnityEngine.Object.DontDestroyOnLoad(go);
            }

            go.SetActive(true);
            return agent;
        }
    }
}
#endif
