#if COREAI_LLM
#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using CoreAI.Ai;
using CoreAI.Logging;
using MEAI = Microsoft.Extensions.AI;

namespace CoreAI.Infrastructure.Llm.Gateway
{
    /// <summary>Composes a real portable server-managed chat client without a container or Unity.</summary>
    public static class GatewayClientFactory
    {
        public static GatewayChatClient Create(IOpenAiHttpSettings settings,
            IOpenAiHttpTransport completionTransport, GatewayClientOptions? options = null,
            ILlmAuthContextProvider? authContextProvider = null, IRequestHeaderProvider? headerProvider = null,
            IGatewayAuthRefresher? authRefresher = null, ILlmEntitlementPolicy? entitlementPolicy = null,
            ILlmUsageSink? usageSink = null, IGatewayHttpTransport? serviceTransport = null,
            Func<IReadOnlyList<MEAI.ChatMessage>, MEAI.ChatOptions?, GatewayRequestContext>? contextFactory = null,
            ILog? log = null, ILlmAsyncMarshaler? asyncMarshaler = null,
            bool supportsNativeToolCalling = true, CancellationToken hostLifetime = default)
        {
            if (settings == null) throw new ArgumentNullException(nameof(settings));
            if (completionTransport == null) throw new ArgumentNullException(nameof(completionTransport));
            GatewayClientOptions snapshot = (options ?? new GatewayClientOptions()).Snapshot();
            GatewayClientOptions.ValidateEndpoint(settings.ApiBaseUrl, snapshot.AllowInsecureHttp);
            GatewayAuthorizationSettings scoped = new GatewayAuthorizationSettings(
                settings, authContextProvider, headerProvider);
            IDisposable? ownedService = null;
            bool needsService = (snapshot.EnableClientPreflight && entitlementPolicy == null) ||
                                (snapshot.ReportUsageToGateway && usageSink == null);
            if (needsService)
            {
                if (string.IsNullOrWhiteSpace(snapshot.GatewayBaseUrl))
                    snapshot.GatewayBaseUrl = settings.ApiBaseUrl;
                if (serviceTransport == null)
                {
#if UNITY_WEBGL && !UNITY_EDITOR
                    throw new InvalidOperationException("WebGL gateway services require a host transport.");
#else
                    HttpClientGatewayTransport transport = new HttpClientGatewayTransport();
                    serviceTransport = transport;
                    ownedService = transport;
#endif
                }
                try
                {
                    GatewayServiceClient services = new GatewayServiceClient(snapshot, serviceTransport,
                        () => scoped.AuthorizationHeader);
                    entitlementPolicy ??= new GatewayEntitlementPolicy(services, snapshot.PreflightUnavailablePolicy);
                    usageSink ??= new GatewayUsageSink(services);
                }
                catch
                {
                    ownedService?.Dispose();
                    throw;
                }
            }

            MEAI.IChatClient raw = new MeaiOpenAiChatClient(scoped, completionTransport,
                supportsNativeToolCalling, log ?? NullLog.Instance, asyncMarshaler);
            return new GatewayChatClient(raw, scoped, snapshot, entitlementPolicy, usageSink,
                authRefresher, contextFactory, log, asyncMarshaler, ownedService, hostLifetime);
        }
    }
}
#endif
