using CoreAI.Audit;
using CoreAI.Authority;
using CoreAI.Diagnostics;
using CoreAI.Features.Audit;
using CoreAI.Infrastructure.Logging;
using CoreAI.Infrastructure.Messaging;
using CoreAI.Logging;
using CoreAI.Messaging;
using CoreAI.Unity.Logging;
using MessagePipe;
using MessagePipe.VContainer;
using VContainer;

namespace CoreAI.Composition
{
    /// <summary>
    /// Registers shared logging, MessagePipe brokers, and the global MessagePipe service provider
    /// used by CoreAI runtime services.
    /// </summary>
    public static class CoreServicesInstaller
    {
        private sealed class ActorIdentityCompositionEntryPoint
        {
        }

        /// <summary>
        /// Composition-issued host identity used by every unconfigured single-player path.
        /// </summary>
        internal static IActorIdentityProvider DefaultLocalHostIdentityProvider { get; } =
            ActorIdentityComposition.CreateLocalHost(new ActorIdentityCompositionEntryPoint());

        /// <summary>Registers CoreAI domain services with the dependency injection container.</summary>
        public static void RegisterCore(this IContainerBuilder builder)
        {
            if (!builder.Exists(typeof(IActorIdentityProvider), true))
            {
                builder.RegisterInstance<IActorIdentityProvider>(DefaultLocalHostIdentityProvider);
            }

            builder.Register<UnityGameLogSink>(Lifetime.Singleton).AsSelf().As<IGameLogSink>();
            builder.Register<FilteringGameLogger>(Lifetime.Singleton).As<IGameLogger>();

            builder.Register<UnityLog>(Lifetime.Singleton).As<ILog>();

            // WHY host-first: a host that registered its own log or performance source keeps it; otherwise the
            // process-wide Unity readers serve every scope, so their history survives scene loads.
            if (!builder.Exists(typeof(IRuntimeLogSource), true))
            {
                builder.RegisterInstance<IRuntimeLogSource>(UnityRuntimeLogCapture.Shared);
            }

            if (!builder.Exists(typeof(IRuntimePerformanceSource), true))
            {
                builder.RegisterInstance<IRuntimePerformanceSource>(UnityRuntimePerformanceMonitor.Shared);
            }

            builder.RegisterBuildCallback(static resolver =>
            {
                (resolver.ResolveOrDefault<IRuntimeLogSource>() as UnityRuntimeLogCapture)?.EnsureStarted();
                (resolver.ResolveOrDefault<IRuntimePerformanceSource>() as UnityRuntimePerformanceMonitor)
                    ?.EnsureStarted();
            });

            MessagePipeOptions opts = builder.RegisterMessagePipe();
            builder.RegisterMessageBroker<ApplyAiGameCommand>(opts);
            builder.RegisterMessageBroker<LlmBackendSelected>(opts);
            builder.RegisterMessageBroker<LlmRequestStarted>(opts);
            builder.RegisterMessageBroker<LlmRequestCompleted>(opts);
            builder.RegisterMessageBroker<LlmUsageReported>(opts);
            builder.RegisterMessageBroker<LlmToolCallStarted>(opts);
            builder.RegisterMessageBroker<LlmToolCallCompleted>(opts);
            builder.RegisterMessageBroker<LlmToolCallFailed>(opts);
            // Published by RefreshOnUnauthorizedDecorator when a server-managed auth refresh fails so host
            // UI can prompt for re-login. Without this broker GetPublisher<LlmAuthExpired> threw and the
            // event was silently swallowed.
            builder.RegisterMessageBroker<LlmAuthExpired>(opts);

            // WebGL/IL2CPP: VContainer's TypeAnalyzer may fail on constructor metadata for
            // MessagePipeAiCommandSink; explicit factory matches QueuedAiOrchestrator registration.
            builder.Register<IAiGameCommandSink>(c =>
                    new MessagePipeAiCommandSink(c.Resolve<IPublisher<ApplyAiGameCommand>>()),
                Lifetime.Singleton);

            builder.RegisterBuildCallback(static resolver =>
            {
                Log.Instance = resolver.Resolve<ILog>();
                GlobalMessagePipe.SetProvider(resolver.AsServiceProvider());
            });

            // WHY: Audit interceptors resolve in their build callback and must see this scope's
            // MessagePipe provider before subscribing.
            builder.RegisterAuditLog();
        }
    }
}
