using CoreAI.Ai;
using CoreAI.Infrastructure.Llm;
using VContainer;

namespace CoreAI.Decisions
{
    /// <summary>
    /// Registers the decision block: <see cref="IDecisionClock"/>, <see cref="IDecisionEndpointSource"/>,
    /// <see cref="IDecisionRouter"/>, <see cref="IDecisionClientFactory"/>, <see cref="IDecisionHttpGet"/>,
    /// <see cref="IDecisionCapabilityProbe"/>, <see cref="IDecisionClientProvider"/> and
    /// <see cref="IDecisionTraceSink"/>. A registration made before this call wins for each of them, so any block can
    /// be replaced without editing package code. Consumers ask <see cref="IDecisionClientProvider.ForConsumer"/>.
    /// </summary>
    public static class CoreAiDecisionsInstaller
    {
        /// <summary>Registers the decision services.</summary>
        /// <param name="builder">Target container builder.</param>
        /// <param name="settings">Routes, endpoints and overrides; null registers the block with no routes.</param>
        public static void RegisterCoreAiDecisions(this IContainerBuilder builder, CoreAiDecisionSettings settings = null)
        {
            CoreAiDecisionSettings decisions = settings ?? new CoreAiDecisionSettings();
            if (!builder.Exists(typeof(CoreAiDecisionSettings)))
            {
                builder.RegisterInstance(decisions);
            }

            if (!builder.Exists(typeof(IDecisionClock), true))
            {
                // WHY: the player-loop delay is what makes the per-call deadline fire in a WebGL player.
                builder.Register<IDecisionClock>(
                    _ => new MarshalerDecisionClock(UnityMainThreadLlmAsyncMarshaler.Instance),
                    Lifetime.Singleton);
            }

            if (!builder.Exists(typeof(IDecisionTraceSink), true))
            {
                builder.Register(_ => new InMemoryDecisionTraceSink(), Lifetime.Singleton)
                    .As<IDecisionTraceSink>()
                    .AsSelf();
            }

            if (!builder.Exists(typeof(IDecisionEndpointSource), true))
            {
                builder.Register<IDecisionEndpointSource>(c =>
                {
                    StaticDecisionEndpointSource sidecar = new(decisions.Endpoints);
                    return decisions.IncludeRegistryEndpoints && c.TryResolve(out ILlmEndpointRegistry registry)
                        ? new RegistryDecisionEndpointSource(registry, sidecar)
                        : sidecar;
                }, Lifetime.Singleton);
            }

            if (!builder.Exists(typeof(IDecisionRouter), true))
            {
                builder.Register(c => new DecisionRouter(decisions.Routes, c.Resolve<IDecisionEndpointSource>()),
                        Lifetime.Singleton)
                    .As<IDecisionRouter>()
                    .AsSelf();
            }

            if (!builder.Exists(typeof(IDecisionClientFactory), true))
            {
                builder.Register<IDecisionClientFactory>(c => CreateFactory(decisions, Secrets(c, decisions)),
                    Lifetime.Singleton);
            }

            if (!builder.Exists(typeof(IDecisionHttpGet), true))
            {
                builder.Register<IDecisionHttpGet>(_ => CreateHttpGet(), Lifetime.Singleton);
            }

            if (!builder.Exists(typeof(IDecisionCapabilityProbe), true))
            {
                builder.Register<IDecisionCapabilityProbe>(c => new ModelListDecisionCapabilityProbe(
                    c.Resolve<IDecisionHttpGet>(), Secrets(c, decisions), c.Resolve<IDecisionClock>()), Lifetime.Singleton);
            }

            if (!builder.Exists(typeof(IDecisionClientProvider), true))
            {
                builder.Register<IDecisionClientProvider>(c => new DecisionClientProvider(
                    c.Resolve<IDecisionRouter>(), c.Resolve<IDecisionClientFactory>(), c.Resolve<IDecisionClock>()),
                    Lifetime.Singleton);
            }
        }

        private static ILlmEndpointSecretProvider Secrets(IObjectResolver resolver, CoreAiDecisionSettings settings)
        {
            if (settings.SecretProviderOverride != null)
            {
                return settings.SecretProviderOverride;
            }

            return resolver.TryResolve(out ILlmEndpointSecretProvider registered)
                ? registered
                : new EnvironmentDecisionSecretProvider();
        }

        private static IDecisionClientFactory CreateFactory(CoreAiDecisionSettings settings,
            ILlmEndpointSecretProvider secrets)
        {
#if COREAI_LLM
            return new DecisionClientFactory(settings.TransportOverride ?? CreateTransport(), secrets,
                settings.FactoryOptions);
#else
            return new UnavailableDecisionClientFactory();
#endif
        }

#if COREAI_LLM
        private static IOpenAiHttpTransport CreateTransport()
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            // WHY: decisions never stream, so the non-streaming UnityWebRequest transport is the whole WebGL path;
            // the fetch SSE bridge cannot serve a non-streaming POST at all.
            return new UnityWebRequestOpenAiTransport();
#else
            return new HttpClientOpenAiTransport();
#endif
        }
#endif

        private static IDecisionHttpGet CreateHttpGet()
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            return new UnityWebRequestDecisionHttpGet();
#else
            return new HttpClientDecisionHttpGet();
#endif
        }
    }
}
