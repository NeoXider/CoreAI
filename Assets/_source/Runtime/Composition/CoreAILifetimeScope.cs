using CoreAI.Ai;
using CoreAI.Infrastructure.Logging;
using CoreAI.Infrastructure.Llm;
using CoreAI.Infrastructure.Lua;
using CoreAI.Infrastructure.Messaging;
using CoreAI.Infrastructure.Prompts;
using LLMUnity;
using UnityEngine;
using VContainer;
using VContainer.Unity;

namespace CoreAI.Composition
{
    /// <summary>
    /// Корневой VContainer scope **ядра CoreAI** (лог, MessagePipe, промпты, LLM, оркестратор).
    /// Имя отделено от «игрового» <see cref="LifetimeScope"/>: на сцене игры добавляйте свои feature-scope'ы
    /// с <b>Parent</b> = этот объект, чтобы не путать с корнем тайтла.
    /// </summary>
    public sealed class CoreAILifetimeScope : LifetimeScope
    {
        [Tooltip("Если null — логируются все фичи (DefaultGameLogSettings). Иначе — фильтр по флагам и минимальному уровню.")]
        [SerializeField]
        private GameLogSettingsAsset gameLogSettings;

        [Tooltip("Опционально: переопределения и кастомные агенты (системный/user шаблоны из TextAsset).")]
        [SerializeField]
        private AgentPromptsManifest agentPromptsManifest;

        [Tooltip("Если Use OpenAi Compatible Http включён в asset — ILlmClient ходит в chat/completions (OpenAI-совместимо), иначе LLMAgent / заглушка.")]
        [SerializeField]
        private OpenAiHttpLlmSettings openAiHttpLlmSettings;

        protected override void Configure(IContainerBuilder builder)
        {
            if (gameLogSettings != null)
                builder.RegisterInstance<IGameLogSettings>(gameLogSettings);
            else
                builder.Register<DefaultGameLogSettings>(Lifetime.Singleton).As<IGameLogSettings>();

            builder.RegisterAgentPrompts(agentPromptsManifest);
            builder.RegisterCore();

            builder.Register<LoggingLuaRuntimeBindings>(Lifetime.Singleton).As<IGameLuaRuntimeBindings>();
            builder.Register<LoggingLuaExecutionObserver>(Lifetime.Singleton).As<ILuaExecutionObserver>();

            var openAi = openAiHttpLlmSettings;
            builder.Register<ILlmClient>(_ => ResolveLlmClient(openAi), Lifetime.Singleton);
            builder.RegisterCorePortable();
            builder.RegisterEntryPoint<AiGameCommandRouter>();
            builder.RegisterEntryPoint<CoreAIGameEntryPoint>();
        }

        private static ILlmClient ResolveLlmClient(OpenAiHttpLlmSettings openAi)
        {
            if (openAi != null && openAi.UseOpenAiCompatibleHttp)
                return new OpenAiChatLlmClient(openAi);
#if COREAI_NO_LLM
            return new StubLlmClient();
#else
            var agent = Object.FindFirstObjectByType<LLMAgent>();
            if (agent == null)
                return new StubLlmClient();

            // Если LLMUnity в сцене оставили без модели (GGUF путь пуст), она пишет ошибку и не поднимется.
            // Тогда безопаснее использовать stub и не пытаться дергать LLMUnity.
            var llm = agent.GetComponent<LLM>();
            if (llm != null && string.IsNullOrWhiteSpace(llm.model))
                return new StubLlmClient();

            return new LlmUnityLlmClient(agent);
#endif
        }
    }
}
