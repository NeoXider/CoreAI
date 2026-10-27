#if COREAI_LLM
using System;
using CoreAI.Ai;
using CoreAI.Infrastructure.Llm;
using CoreAI.Infrastructure.Llm.Gateway;
using CoreAI.Logging;
using VContainer;

namespace CoreAI.Composition
{
    /// <summary>Opt-in scoped gateway composition; caller-owned providers and transports remain replaceable.</summary>
    public static class CoreAiGatewayClientInstaller
    {
        public static void RegisterCoreAiGatewayClient(this IContainerBuilder builder,
            GatewayClientOptions options, ILlmAuthContextProvider auth = null,
            IRequestHeaderProvider headers = null, IGatewayAuthRefresher refresher = null,
            ILlmEntitlementPolicy policy = null, ILlmUsageSink sink = null,
            IGatewayHttpTransport serviceTransport = null, ILog log = null)
        {
            if (builder == null) throw new ArgumentNullException(nameof(builder));
            if (options == null) throw new ArgumentNullException(nameof(options));
            GatewayClientOptions snapshot = options.Snapshot();
            if (builder.Exists(typeof(IUnityGatewayClientBinding), true)) return;
            builder.Register(_ => new UnityGatewayClientBinding(snapshot, auth, headers, refresher,
                    policy, sink, serviceTransport, log), Lifetime.Singleton)
                .As<IUnityGatewayClientBinding>()
                .AsSelf();
        }
    }
}
#endif
