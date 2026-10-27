#if COREAI_LLM
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Ai;
using CoreAI.Infrastructure.Llm;
using CoreAI.Infrastructure.Llm.Gateway;
using CoreAI.Logging;
using MEAI = Microsoft.Extensions.AI;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode
{
#if !UNITY_5_3_OR_NEWER
    [NonParallelizable]
#endif
    public sealed class GatewayClientEditModeTests
    {
        private SynchronizationContext _previousContext;
        private ILlmAuthContextProvider _previousAuth;

        [SetUp]
        public void SetUp()
        {
            _previousContext = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(null);
            _previousAuth = LlmAuthContextRegistry.Current;
        }

        [TearDown]
        public void TearDown()
        {
            LlmAuthContextRegistry.SetProvider(_previousAuth);
            SynchronizationContext.SetSynchronizationContext(_previousContext);
        }


        [TestCase(false)]
        [TestCase(true)]
        public async Task ProviderIds_DefaultFactorySnapshotsSharedProviderOnce(bool streaming)
        {
            MutableHeaders provider = new MutableHeaders { IdKey = "provider-key", Trace = "provider-trace" };
            OpenAiHttpOptions settings = HttpOptions();
            settings.HeaderProvider = provider;
            WireTransport wire = new WireTransport();
            using GatewayChatClient client = GatewayClientFactory.Create(settings, wire, headerProvider: provider);
            await Invoke(client, streaming);
            Assert.That(Header(wire.Requests.Single(), "Idempotency-Key"), Is.EqualTo("provider-key"));
            Assert.That(Header(wire.Requests.Single(), "X-Request-Id"), Is.EqualTo("provider-trace"));
            Assert.That(provider.Reads, Is.EqualTo(1));
            Assert.That(provider.IdReads, Is.EqualTo(1));
            Assert.That(provider.TraceReads, Is.EqualTo(1));
        }

        [TestCase(false, false)]
        [TestCase(true, false)]
        [TestCase(false, true)]
        [TestCase(true, true)]
        public async Task ProviderIds_AuthRetryAndReusedContextRetainOnlyInvocationSnapshot(bool streaming,
            bool sharedProvider)
        {
            MutableHeaders endpoint = new MutableHeaders { IdKey = "endpoint-key", Trace = "endpoint-trace" };
            MutableHeaders host = sharedProvider ? endpoint :
                new MutableHeaders { IdKey = "host-key", Trace = "host-trace" };
            OpenAiHttpOptions settings = HttpOptions();
            settings.HeaderProvider = endpoint;
            LlmCompletionRequest reused = new LlmCompletionRequest { AgentRoleId = "role" };
            LlmRouteProfile reusedProfile = new LlmRouteProfile { ProfileId = "route", Model = "host-owned" };
            WireTransport wire = new WireTransport
            {
                AuthStreamFailures = 1,
                Response = (request, index) => index == 0
                    ? new OpenAiHttpPostResult { StatusCode = 401, BodyText = "{}" } : WireTransport.Ok()
            };
            DelegateRefresher refresh = new DelegateRefresher(() =>
            {
                endpoint.IdKey = "during-refresh"; endpoint.Trace = "during-refresh";
                return true;
            });
            using GatewayChatClient client = GatewayClientFactory.Create(settings, wire, headerProvider: host,
                authRefresher: refresh,
                contextFactory: (messages, options) => new GatewayRequestContext(reused, reusedProfile));
            await Invoke(client, streaming);
            Assert.That(wire.Requests.Count, Is.EqualTo(2));
            foreach (OpenAiHttpPostRequest request in wire.Requests)
            {
                Assert.That(Header(request, "Idempotency-Key"), Is.EqualTo("endpoint-key"));
                Assert.That(Header(request, "X-Request-Id"), Is.EqualTo("endpoint-trace"));
            }
            Assert.That(endpoint.Reads, Is.EqualTo(1));
            Assert.That(host.Reads, Is.EqualTo(1));
            endpoint.IdKey = "later-key"; endpoint.Trace = "later-trace";
            await Invoke(client, streaming);
            Assert.That(Header(wire.Requests[2], "Idempotency-Key"), Is.EqualTo("later-key"));
            Assert.That(Header(wire.Requests[2], "X-Request-Id"), Is.EqualTo("later-trace"));
            Assert.That(reused.IdempotencyKey, Is.Empty);
            Assert.That(reused.TraceId, Is.Empty);
            Assert.That(reused.ChatHistory, Is.Null);
            Assert.That(reusedProfile.Model, Is.EqualTo("host-owned"));
            Assert.That(endpoint.Reads, Is.EqualTo(2));
            Assert.That(host.Reads, Is.EqualTo(2));
        }

        [TestCase(false, false)]
        [TestCase(true, false)]
        [TestCase(false, true)]
        [TestCase(true, true)]
        public async Task ExplicitAndAmbientIds_WinOverProviderFallback(bool streaming, bool explicitContext)
        {
            MutableHeaders provider = new MutableHeaders { IdKey = "provider", Trace = "provider" };
            LlmCompletionRequest request = new LlmCompletionRequest
                { TraceId = "explicit-trace", IdempotencyKey = "explicit-key" };
            WireTransport wire = new WireTransport();
            using GatewayChatClient client = GatewayClientFactory.Create(HttpOptions(), wire,
                headerProvider: provider, contextFactory: explicitContext
                    ? (Func<IReadOnlyList<MEAI.ChatMessage>, MEAI.ChatOptions, GatewayRequestContext>)
                        ((messages, options) => new GatewayRequestContext(request, new LlmRouteProfile()))
                    : null);
            using LlmRequestContext.Scope ambient = LlmRequestContext.Begin("role", "ambient-trace", "ambient-key");
            await Invoke(client, streaming);
            Assert.That(Header(wire.Requests.Single(), "Idempotency-Key"),
                Is.EqualTo(explicitContext ? "explicit-key" : "ambient-key"));
            Assert.That(Header(wire.Requests.Single(), "X-Request-Id"),
                Is.EqualTo(explicitContext ? "explicit-trace" : "ambient-trace"));
            Assert.That(request.TraceId, Is.EqualTo("explicit-trace"));
        }

        [TestCase(false, false, null)]
        [TestCase(false, true, null)]
        [TestCase(true, false, null)]
        [TestCase(true, true, null)]
        [TestCase(false, false, "different")]
        [TestCase(false, true, "different")]
        [TestCase(true, false, "different")]
        [TestCase(true, true, "different")]
        public async Task PolicyModel_MatchesConfiguredWireForDefaultAndExplicitContexts(bool streaming,
            bool explicitContext, string optionsModel)
        {
            OpenAiHttpOptions settings = HttpOptions();
            settings.Model = "configured";
            LlmCompletionRequest request = new LlmCompletionRequest();
            LlmRouteProfile hostProfile = new LlmRouteProfile { Model = "host-owned" };
            ModelPolicy policy = new ModelPolicy();
            WireTransport wire = new WireTransport();
            using GatewayChatClient client = GatewayClientFactory.Create(settings, wire,
                new GatewayClientOptions { EnableClientPreflight = true }, entitlementPolicy: policy,
                contextFactory: explicitContext
                    ? (Func<IReadOnlyList<MEAI.ChatMessage>, MEAI.ChatOptions, GatewayRequestContext>)
                        ((messages, options) => new GatewayRequestContext(request, hostProfile))
                    : null);
            await Invoke(client, streaming, options: optionsModel == null ? null : new MEAI.ChatOptions { ModelId = optionsModel });
            Assert.That(policy.Models.Single(), Is.EqualTo("configured"));
            Assert.That((string)JObject.Parse(wire.Requests.Single().JsonBody)["model"], Is.EqualTo("configured"));
            Assert.That(hostProfile.Model, Is.EqualTo("host-owned"));
            Assert.That(request.ChatHistory, Is.Null);
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task PolicyModel_DeniedConfiguredModelNeverSendsDifferentOptionsModel(bool streaming)
        {
            OpenAiHttpOptions settings = HttpOptions();
            settings.Model = "configured";
            ModelPolicy policy = new ModelPolicy { DeniedModel = "configured" };
            WireTransport wire = new WireTransport();
            using GatewayChatClient client = GatewayClientFactory.Create(settings, wire,
                new GatewayClientOptions { EnableClientPreflight = true }, entitlementPolicy: policy);
            LlmClientException failure = Assert.ThrowsAsync<LlmClientException>(async () =>
                await Invoke(client, streaming, options: new MEAI.ChatOptions { ModelId = "different" }));
            Assert.That(failure.ErrorCode, Is.EqualTo(LlmErrorCode.InvalidRequest));
            Assert.That(policy.Models.Single(), Is.EqualTo("configured"));
            Assert.That(wire.Requests.Count, Is.Zero);
            await Task.CompletedTask;
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task PolicyModel_BackendSelectedStaysUnknownDespiteChatOptions(bool streaming)
        {
            WireTransport wire = new WireTransport();
            ModelPolicy policy = new ModelPolicy();
            using GatewayChatClient client = GatewayClientFactory.Create(HttpOptions(), wire,
                new GatewayClientOptions { EnableClientPreflight = true }, entitlementPolicy: policy);
            await Invoke(client, streaming, options: new MEAI.ChatOptions { ModelId = "ignored" });
            Assert.That(policy.Models.Single(), Is.Empty);
            Assert.That(JObject.Parse(wire.Requests.Single().JsonBody)["model"], Is.Null);
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task HeldPreflight_ModelMutationCannotChangeApprovedWireInvocation(bool streaming)
        {
            OpenAiHttpOptions settings = HttpOptions();
            settings.Model = "approved-a";
            ModelPolicy policy = new ModelPolicy { Hold = new TaskCompletionSource<bool>() };
            WireTransport wire = new WireTransport();
            LlmRouteProfile profile = new LlmRouteProfile { Model = "host-original" };
            LlmCompletionRequest request = new LlmCompletionRequest();
            using GatewayChatClient client = GatewayClientFactory.Create(settings, wire,
                new GatewayClientOptions { EnableClientPreflight = true }, entitlementPolicy: policy,
                contextFactory: (messages, options) => new GatewayRequestContext(request, profile));
            Task first = Invoke(client, streaming);
            await policy.Entered.Task;
            settings.Model = "later-b";
            profile.Model = "host-changed";
            policy.Hold.SetResult(true);
            await first;
            await Invoke(client, streaming);
            Assert.That(policy.Models, Is.EqualTo(new[] { "approved-a", "later-b" }));
            Assert.That((string)JObject.Parse(wire.Requests[0].JsonBody)["model"], Is.EqualTo("approved-a"));
            Assert.That((string)JObject.Parse(wire.Requests[1].JsonBody)["model"], Is.EqualTo("later-b"));
            Assert.That(profile.Model, Is.EqualTo("host-changed"));
            Assert.That(request.TraceId, Is.Empty);
        }

        [TestCase(false, "{\"allowed\":false}", "", LlmErrorCode.PermanentProviderError)]
        [TestCase(true, "{\"allowed\":false}", "", LlmErrorCode.PermanentProviderError)]
        [TestCase(false, "{\"allowed\":false,\"reason_code\":\"\"}", "", LlmErrorCode.PermanentProviderError)]
        [TestCase(true, "{\"allowed\":false,\"reason_code\":\"  \"}", "  ", LlmErrorCode.PermanentProviderError)]
        [TestCase(false, "{\"allowed\":false,\"reason_code\":\"quota_exceeded\"}", "quota_exceeded", LlmErrorCode.QuotaExceeded)]
        [TestCase(true, "{\"allowed\":false,\"reason_code\":\"quota_exceeded\"}", "quota_exceeded", LlmErrorCode.QuotaExceeded)]
        public async Task ActualServiceDenial_PreservesOriginalReasonAndCategory(bool streaming,
            string body, string expectedReason, LlmErrorCode expectedCode)
        {
            WireTransport wire = new WireTransport();
            ServiceReply side = new ServiceReply(body);
            using GatewayChatClient client = GatewayClientFactory.Create(HttpOptions(), wire,
                new GatewayClientOptions { EnableClientPreflight = true }, serviceTransport: side);
            LlmClientException failure = Assert.ThrowsAsync<LlmClientException>(async () => await Invoke(client, streaming));
            JObject detail = JObject.Parse(failure.ProviderErrorBody);
            Assert.That(failure.ErrorCode, Is.EqualTo(expectedCode));
            Assert.That((string)detail["reason_code"], Is.EqualTo(expectedReason));
            Assert.That((string)detail["message"], Is.EqualTo("Gateway access was denied."));
            Assert.That(wire.Requests.Count, Is.Zero);
            Assert.That(side.Calls, Is.EqualTo(1));
            await Task.CompletedTask;
        }

        [Test]
        public async Task OptionalServicesOff_UseActualServerWireWithoutModelOrSideCalls()
        {
            WireTransport wire = new WireTransport();
            CountingPolicy policy = new CountingPolicy(false);
            CountingSink sink = new CountingSink();
            using GatewayChatClient client = Create(wire, policy: policy, sink: sink);
            MEAI.ChatResponse result = await client.GetResponseAsync(Messages());
            Assert.That(result.Text, Is.EqualTo("ok"));
            Assert.That(wire.Requests.Count, Is.EqualTo(1));
            Assert.That(wire.Requests[0].Url, Is.EqualTo("https://gateway.test/v1/chat/completions"));
            Assert.That(JObject.Parse(wire.Requests[0].JsonBody)["model"], Is.Null);
            Assert.That(policy.Calls, Is.Zero);
            Assert.That(sink.Records.Count, Is.Zero);
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task DeniedPreflight_PreventsCompletionDispatch(bool streaming)
        {
            WireTransport wire = new WireTransport();
            CountingPolicy policy = new CountingPolicy(false);
            using GatewayChatClient client = Create(wire,
                new GatewayClientOptions { EnableClientPreflight = true }, policy: policy);
            LlmClientException failure = Assert.ThrowsAsync<LlmClientException>(
                async () => await Invoke(client, streaming));
            Assert.That(failure.ErrorCode, Is.EqualTo(LlmErrorCode.QuotaExceeded));
            Assert.That(wire.Requests.Count, Is.Zero);
            Assert.That(policy.Calls, Is.EqualTo(1));
            Assert.That(policy.Last.ChatHistory.Single().Text, Is.EqualTo("hello"));
            await Task.CompletedTask;
        }

        [TestCase("rate_limited")]
        [TestCase("backend_unavailable")]
        [TestCase("gateway_unavailable")]
        [TestCase("unmapped_host_denial")]
        public async Task ValidDenial_CannotBecomeRetryableProviderFailure(string reason)
        {
            WireTransport wire = new WireTransport();
            using GatewayChatClient client = Create(wire,
                new GatewayClientOptions { EnableClientPreflight = true },
                policy: new CountingPolicy(false, reason));
            LlmClientException failure = Assert.ThrowsAsync<LlmClientException>(async () =>
                await client.GetResponseAsync(Messages()));
            Assert.That(failure.ErrorCode, Is.EqualTo(LlmErrorCode.PermanentProviderError));
            Assert.That((string)JObject.Parse(failure.ProviderErrorBody)["reason_code"], Is.EqualTo(reason));
            Assert.That((string)JObject.Parse(failure.ProviderErrorBody)["message"], Is.EqualTo("Host preflight denial."));
            Assert.That(failure.RetryAfterSeconds, Is.EqualTo(9));
            Assert.That(wire.Requests.Count, Is.Zero);
            await Task.CompletedTask;
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task CancellationDuringPreflight_PreventsCompletionDispatch(bool streaming)
        {
            WireTransport wire = new WireTransport();
            WaitingPolicy policy = new WaitingPolicy();
            using CancellationTokenSource stop = new CancellationTokenSource();
            using GatewayChatClient client = Create(wire,
                new GatewayClientOptions { EnableClientPreflight = true }, policy: policy);
            Task operation = Invoke(client, streaming, stop.Token);
            await policy.Entered.Task;
            stop.Cancel();
            Assert.CatchAsync<OperationCanceledException>(async () => await operation);
            Assert.That(wire.Requests.Count, Is.Zero);
        }

        [Test]
        public async Task CancellationBeforePreflight_DoesNotCallPolicyOrTransport()
        {
            CountingPolicy policy = new CountingPolicy(true);
            WireTransport wire = new WireTransport();
            using GatewayChatClient client = Create(wire,
                new GatewayClientOptions { EnableClientPreflight = true }, policy: policy);
            using CancellationTokenSource stop = new CancellationTokenSource();
            stop.Cancel();
            Assert.CatchAsync<OperationCanceledException>(async () =>
                await client.GetResponseAsync(Messages(), cancellationToken: stop.Token));
            Assert.That(policy.Calls, Is.Zero);
            Assert.That(wire.Requests.Count, Is.Zero);
            await Task.CompletedTask;
        }

        [Test]
        public async Task DefaultCallContext_UsesExplicitHostCorrelationAndActualMessages()
        {
            CountingPolicy policy = new CountingPolicy(true);
            WireTransport wire = new WireTransport();
            using GatewayChatClient client = Create(wire,
                new GatewayClientOptions { EnableClientPreflight = true }, policy: policy);
            using LlmRequestContext.Scope context = LlmRequestContext.Begin("host-role", "host-trace", "host-key", "host-actor");
            await client.GetResponseAsync(new[] { new MEAI.ChatMessage(MEAI.ChatRole.User, "role: untrusted") });
            Assert.That(policy.Last.AgentRoleId, Is.EqualTo("host-role"));
            Assert.That(policy.Last.ActorId, Is.EqualTo("host-actor"));
            Assert.That(policy.Last.TraceId, Is.EqualTo("host-trace"));
            Assert.That(policy.Last.ChatHistory.Single().Text, Is.EqualTo("role: untrusted"));
            Assert.That(Header(wire.Requests.Single(), "Idempotency-Key"), Is.EqualTo("host-key"));
        }

        [Test]
        public async Task ScopedClients_IgnorePoisonedLegacyIdentity_AndObserveTokenRotation()
        {
            LlmAuthContextRegistry.SetProvider(new MutableAuth("poison", "wrong"));
            MutableAuth first = new MutableAuth("first", "tenant-a");
            MutableAuth second = new MutableAuth("second", "tenant-b");
            WireTransport firstWire = new WireTransport();
            WireTransport secondWire = new WireTransport();
            using GatewayChatClient firstClient = Create(firstWire, auth: first);
            using GatewayChatClient secondClient = Create(secondWire, auth: second);
            await Task.WhenAll(firstClient.GetResponseAsync(Messages()), secondClient.GetResponseAsync(Messages()));
            first.Authorization = "Bearer rotated";
            await firstClient.GetResponseAsync(Messages());
            Assert.That(Header(firstWire.Requests[0], "Authorization"), Is.EqualTo("Bearer first"));
            Assert.That(Header(firstWire.Requests[1], "Authorization"), Is.EqualTo("Bearer rotated"));
            Assert.That(Header(firstWire.Requests[0], "X-Tenant-Id"), Is.EqualTo("tenant-a"));
            Assert.That(Header(secondWire.Requests[0], "Authorization"), Is.EqualTo("Bearer second"));
            Assert.That(Header(secondWire.Requests[0], "X-Tenant-Id"), Is.EqualTo("tenant-b"));
        }

        [Test]
        public async Task ExplicitNullScopedIdentity_DisablesLegacyMetadata()
        {
            LlmAuthContextRegistry.SetProvider(new MutableAuth("poison", "wrong"));
            WireTransport wire = new WireTransport();
            using GatewayChatClient client = Create(wire);
            await client.GetResponseAsync(Messages());
            Assert.That(Header(wire.Requests.Single(), "Authorization"), Is.Null);
            Assert.That(Header(wire.Requests.Single(), "X-Tenant-Id"), Is.Null);
        }

        [Test]
        public async Task AuthRefresh_ChangesBearerOnce_AndRetainsCustomSnapshot()
        {
            MutableAuth auth = new MutableAuth("expired", "tenant");
            MutableHeaders headers = new MutableHeaders();
            WireTransport wire = new WireTransport
            {
                Response = (request, index) => index == 0
                    ? new OpenAiHttpPostResult { StatusCode = 401, BodyText = "{}" }
                    : WireTransport.Ok()
            };
            DelegateRefresher refresh = new DelegateRefresher(() =>
            {
                auth.Authorization = "Bearer renewed";
                headers.Value = "new";
                return true;
            });
            using GatewayChatClient client = Create(wire, auth: auth, headers: headers, refresher: refresh);
            await client.GetResponseAsync(Messages());
            Assert.That(refresh.Calls, Is.EqualTo(1));
            Assert.That(wire.Requests.Count, Is.EqualTo(2));
            Assert.That(Header(wire.Requests[0], "Authorization"), Is.EqualTo("Bearer expired"));
            Assert.That(Header(wire.Requests[1], "Authorization"), Is.EqualTo("Bearer renewed"));
            Assert.That(Header(wire.Requests[1], "X-Context"), Is.EqualTo("old"));
            Assert.That(Header(wire.Requests[1], "Idempotency-Key"),
                Is.EqualTo(Header(wire.Requests[0], "Idempotency-Key")));
            Assert.That(headers.Reads, Is.EqualTo(1));
            await client.GetResponseAsync(Messages());
            Assert.That(Header(wire.Requests[2], "X-Context"), Is.EqualTo("new"));
            Assert.That(headers.Reads, Is.EqualTo(2));
        }

        [Test]
        public async Task RepeatedAuthFailure_RefreshesOnlyOnce()
        {
            WireTransport wire = new WireTransport
            {
                Response = (request, index) => new OpenAiHttpPostResult { StatusCode = 403, BodyText = "{}" }
            };
            DelegateRefresher refresh = new DelegateRefresher(() => true);
            using GatewayChatClient client = Create(wire, refresher: refresh);
            Assert.ThrowsAsync<LlmClientException>(async () => await client.GetResponseAsync(Messages()));
            Assert.That(refresh.Calls, Is.EqualTo(1));
            Assert.That(wire.Requests.Count, Is.EqualTo(2));
            await Task.CompletedTask;
        }

        [Test]
        public async Task NativeStream_UsesRealUtf8ParserAndUsageFooter()
        {
            WireTransport wire = new WireTransport
            {
                Sse = "data: {\"model\":\"served\",\"choices\":[{\"delta\":{\"content\":\"snow ☃\"}}]}\n\n" +
                      "data: {\"choices\":[{\"delta\":{\"tool_calls\":[{\"index\":0,\"id\":\"call-a\",\"type\":\"function\",\"function\":{\"name\":\"read_stock\",\"arguments\":\"{\\\"item\\\":\\\"iron\\\"}\"}}]},\"finish_reason\":\"tool_calls\"}]}\n\n" +
                      "data: {\"choices\":[],\"usage\":{\"prompt_tokens\":2,\"completion_tokens\":3,\"total_tokens\":5}}\n\n" +
                      "data: [DONE]\n\n"
            };
            CountingSink sink = new CountingSink();
            using GatewayChatClient client = Create(wire,
                new GatewayClientOptions { ReportUsageToGateway = true }, sink: sink);
            List<MEAI.ChatResponseUpdate> updates = new List<MEAI.ChatResponseUpdate>();
            await foreach (MEAI.ChatResponseUpdate update in client.GetStreamingResponseAsync(Messages()))
                updates.Add(update);
            Assert.That(string.Concat(updates.Select(update => update.Text)), Does.Contain("snow ☃"));
            Assert.That(updates.SelectMany(update => update.Contents).OfType<MEAI.FunctionCallContent>().Count(),
                Is.EqualTo(1));
            Assert.That(sink.Records.Count, Is.EqualTo(1));
            Assert.That(sink.Records[0].TotalTokens, Is.EqualTo(5));
            Assert.That(sink.Records[0].Success, Is.True);
            Assert.That(sink.Records[0].Model, Is.EqualTo("served"));
            Assert.That(wire.StreamDisposed, Is.True);
        }

        [Test]
        public async Task StreamAuthRejectionBeforeContent_RefreshesExactlyOnceThroughActualWire()
        {
            WireTransport wire = new WireTransport { AuthStreamFailures = 1 };
            DelegateRefresher refresh = new DelegateRefresher(() => true);
            using GatewayChatClient client = Create(wire, refresher: refresh);
            await Invoke(client, true);
            Assert.That(wire.Requests.Count, Is.EqualTo(2));
            Assert.That(refresh.Calls, Is.EqualTo(1));
        }

        [Test]
        public async Task EarlyStreamDisposal_ReleasesActualTransportStream()
        {
            WireTransport wire = new WireTransport();
            using GatewayChatClient client = Create(wire);
            IAsyncEnumerator<MEAI.ChatResponseUpdate> stream = client.GetStreamingResponseAsync(Messages())
                .GetAsyncEnumerator();
            Assert.That(await stream.MoveNextAsync(), Is.True);
            await stream.DisposeAsync();
            Assert.That(wire.StreamDisposed, Is.True);
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task VisibleStreamAuthFailure_DoesNotRefreshOrReplay(bool nativeTool)
        {
            GatewayAuthorizationSettings settings = new GatewayAuthorizationSettings(HttpOptions(), null);
            ScriptedStream inner = new ScriptedStream { NativeTool = nativeTool };
            DelegateRefresher refresh = new DelegateRefresher(() => true);
            using GatewayChatClient client = new GatewayChatClient(inner, settings,
                new GatewayClientOptions(), authRefresher: refresh);
            List<string> text = new List<string>();
            Assert.ThrowsAsync<LlmClientException>(async () =>
            {
                await foreach (MEAI.ChatResponseUpdate update in client.GetStreamingResponseAsync(Messages()))
                    text.Add(update.Text);
            });
            Assert.That(text.Count, Is.EqualTo(1));
            if (!nativeTool) Assert.That(text[0], Is.EqualTo("visible"));
            Assert.That(inner.Calls, Is.EqualTo(1));
            Assert.That(refresh.Calls, Is.Zero);
            await Task.CompletedTask;
        }

        [Test]
        public async Task BoundedNonCooperativeTelemetry_CannotReplaceSuccessOrGrowPendingWork()
        {
            CountingSink sink = new CountingSink { Block = new TaskCompletionSource<bool>() };
            WireTransport wire = new WireTransport();
            UsageDeadlineMarshaler clock = new UsageDeadlineMarshaler(17);
            using GatewayChatClient client = GatewayClientFactory.Create(HttpOptions(), wire,
                new GatewayClientOptions
                {
                    ReportUsageToGateway = true, UsageTimeoutMilliseconds = 17, MaxConcurrentUsageReports = 1
                }, usageSink: sink, asyncMarshaler: clock);
            MEAI.ChatResponse first = await client.GetResponseAsync(Messages());
            MEAI.ChatResponse second = await client.GetResponseAsync(Messages());
            Assert.That(first.Text, Is.EqualTo("ok"));
            Assert.That(second.Text, Is.EqualTo("ok"));
            Assert.That(sink.Records.Count, Is.EqualTo(1));
            Assert.That(sink.LastToken.IsCancellationRequested, Is.True);
            sink.Block.SetResult(true);
        }

        [Test]
        public async Task CustomUsageSink_RetainsProviderReasoningAndCacheCounts()
        {
            CountingSink sink = new CountingSink();
            WireTransport wire = new WireTransport
            {
                Response = (request, index) => WireTransport.Ok(
                    "{\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":\"ok\"},\"finish_reason\":\"stop\"}],\"usage\":{\"prompt_tokens\":2,\"completion_tokens\":3,\"total_tokens\":5,\"prompt_tokens_details\":{\"cached_tokens\":1},\"completion_tokens_details\":{\"reasoning_tokens\":2}}}")
            };
            using GatewayChatClient client = Create(wire,
                new GatewayClientOptions { ReportUsageToGateway = true }, sink: sink);
            await client.GetResponseAsync(Messages());
            Assert.That(sink.Records.Single().ReasoningTokens, Is.EqualTo(2));
            Assert.That(sink.Records.Single().CacheReadTokens, Is.EqualTo(1));
        }

        [Test]
        public async Task FailingTelemetry_PreservesSuccessfulCompletion()
        {
            CountingSink sink = new CountingSink { Failure = new InvalidOperationException("sensitive token") };
            using GatewayChatClient client = Create(new WireTransport(),
                new GatewayClientOptions { ReportUsageToGateway = true }, sink: sink);
            MEAI.ChatResponse result = await client.GetResponseAsync(Messages());
            Assert.That(result.Text, Is.EqualTo("ok"));
        }

        [Test]
        public async Task HostLifetime_CancelsInflightCompletionWithoutOwningCallerTransport()
        {
            using CancellationTokenSource lifetime = new CancellationTokenSource();
            WaitingChat inner = new WaitingChat();
            GatewayAuthorizationSettings settings = new GatewayAuthorizationSettings(HttpOptions(), null);
            using GatewayChatClient client = new GatewayChatClient(inner, settings, new GatewayClientOptions(),
                hostLifetime: lifetime.Token);
            Task operation = client.GetResponseAsync(Messages());
            await inner.Entered.Task;
            lifetime.Cancel();
            Assert.CatchAsync<OperationCanceledException>(async () => await operation);
            Assert.That(inner.Token.IsCancellationRequested, Is.True);
        }

        [Test]
        public async Task DisposingClient_CancelsInflightAndRefusesNewInvocation()
        {
            WaitingChat inner = new WaitingChat();
            GatewayChatClient client = new GatewayChatClient(inner,
                new GatewayAuthorizationSettings(HttpOptions(), null), new GatewayClientOptions());
            Task operation = client.GetResponseAsync(Messages());
            await inner.Entered.Task;
            client.Dispose();
            Assert.CatchAsync<OperationCanceledException>(async () => await operation);
            Assert.ThrowsAsync<ObjectDisposedException>(async () => await client.GetResponseAsync(Messages()));
            Assert.That(inner.Disposed, Is.True);
        }

        [Test]
        public async Task BufferedToolLoop_ComposesExistingToolRunnerAroundGateway_ReportsEachRoundtrip()
        {
            int executed = 0;
            DelegateLlmTool tool = new DelegateLlmTool("read_stock", "Read stock.",
                new Func<string, string>((string item) => { executed++; return "iron: 12"; }));
            CountingSink sink = new CountingSink();
            WireTransport wire = new WireTransport
            {
                Response = (request, index) => index == 0 ? WireTransport.Ok(
                    "{\"id\":\"first\",\"model\":\"served\",\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":null,\"tool_calls\":[{\"id\":\"call-a\",\"type\":\"function\",\"function\":{\"name\":\"read_stock\",\"arguments\":\"{\\\"item\\\":\\\"iron\\\"}\"}}]},\"finish_reason\":\"tool_calls\"}],\"usage\":{\"prompt_tokens\":2,\"completion_tokens\":3,\"total_tokens\":5}}")
                    : WireTransport.Ok()
            };
            GatewayChatClient gateway = Create(wire,
                new GatewayClientOptions { ReportUsageToGateway = true }, sink: sink);
            using SmartToolCallingChatClient client = new SmartToolCallingChatClient(gateway,
                NullLog.Instance, new CoreAISettingsOptions { MaxToolCallRoundtrips = 4 },
                false, new[] { tool }, "stock");
            MEAI.ChatOptions options = new MEAI.ChatOptions
            {
                Tools = new List<MEAI.AITool> { ((IAIFunctionLlmTool)tool).CreateAIFunction() }
            };
            MEAI.ChatResponse result = await client.GetResponseAsync(Messages(), options);
            Assert.That(executed, Is.EqualTo(1));
            Assert.That(wire.Requests.Count, Is.EqualTo(2));
            Assert.That(sink.Records.Count, Is.EqualTo(2));
            Assert.That(JObject.Parse(wire.Requests[1].JsonBody)["messages"].Any(
                message => (string)message["role"] == "tool"), Is.True);
            Assert.That(result.Text, Is.EqualTo("ok"));
        }

        [Test]
        public async Task VisionAndNativeToolOptions_ArePassedThroughExistingWireClient()
        {
            WireTransport wire = new WireTransport();
            using GatewayChatClient client = Create(wire);
            MEAI.ChatMessage image = new MEAI.ChatMessage(MEAI.ChatRole.User,
                new MEAI.AIContent[]
                {
                    new MEAI.TextContent("inspect"),
                    new MEAI.DataContent(new byte[] { 1, 2, 3 }, "image/png")
                });
            await client.GetResponseAsync(new[] { image });
            JObject body = JObject.Parse(wire.Requests.Single().JsonBody);
            Assert.That((string)body["messages"][0]["content"][1]["type"], Is.EqualTo("image_url"));
        }

        private static GatewayChatClient Create(WireTransport wire, GatewayClientOptions options = null,
            ILlmEntitlementPolicy policy = null, CountingSink sink = null, MutableAuth auth = null,
            IRequestHeaderProvider headers = null, IGatewayAuthRefresher refresher = null) =>
            GatewayClientFactory.Create(HttpOptions(), wire, options, auth, headers, refresher, policy, sink);

        private static OpenAiHttpOptions HttpOptions() => new OpenAiHttpOptions
        {
            ApiBaseUrl = "https://gateway.test/v1", Model = "", ExecutionMode = LlmExecutionMode.ServerManagedApi,
            LogLlmInput = false, LogLlmOutput = false
        };
        private static MEAI.ChatMessage[] Messages() =>
            new[] { new MEAI.ChatMessage(MEAI.ChatRole.User, "hello") };
        private static string Header(OpenAiHttpPostRequest request, string name) =>
            request.Headers.FirstOrDefault(pair => string.Equals(pair.Key, name,
                StringComparison.OrdinalIgnoreCase)).Value;
        private static async Task Invoke(MEAI.IChatClient client, bool streaming,
            CancellationToken token = default, MEAI.ChatOptions options = null)
        {
            if (!streaming) { await client.GetResponseAsync(Messages(), options, token); return; }
            await foreach (MEAI.ChatResponseUpdate update in client.GetStreamingResponseAsync(
                Messages(), options, token)) { }
        }

        private sealed class MutableAuth : ILlmAuthContextProvider
        {
            public MutableAuth(string token, string tenant) { Authorization = "Bearer " + token; TenantId = tenant; }
            public string Authorization;
            public string GetAuthorizationHeader() => Authorization;
            public string TenantId { get; }
            public string UserId => "host-user";
            public string SessionId => "host-session";
        }

        private sealed class MutableHeaders : IRequestHeaderProvider
        {
            public int Reads;
            public string Value = "old";
            public string IdKey = "";
            public string Trace = "";
            public int IdReads;
            public int TraceReads;
            public IReadOnlyList<KeyValuePair<string, string>> GetHeaders()
            {
                Reads++;
                return new[]
                {
                    new KeyValuePair<string, string>("X-Context", Value),
                    new KeyValuePair<string, string>("Authorization", "blocked")
                };
            }
            public string IdempotencyKey { get { IdReads++; return IdKey; } }
            public string RequestId { get { TraceReads++; return Trace; } }
        }

        private sealed class ServiceReply : IGatewayHttpTransport
        {
            private readonly string _body;
            public ServiceReply(string body) => _body = body;
            public int Calls;
            public Task<GatewayHttpResponse> SendAsync(GatewayHttpRequest request,
                CancellationToken cancellationToken = default)
            {
                Calls++; cancellationToken.ThrowIfCancellationRequested();
                return Task.FromResult(new GatewayHttpResponse { StatusCode = 200, BodyText = _body });
            }
        }

        private sealed class ModelPolicy : ILlmEntitlementPolicy
        {
            public readonly List<string> Models = new List<string>();
            public readonly TaskCompletionSource<bool> Entered = new TaskCompletionSource<bool>();
            public TaskCompletionSource<bool> Hold;
            public string DeniedModel;
            public async Task<LlmEntitlementDecision> CheckAsync(LlmCompletionRequest request,
                LlmRouteProfile profile, CancellationToken cancellationToken = default)
            {
                Models.Add(profile.Model); Entered.TrySetResult(true);
                if (Hold != null) await Hold.Task;
                cancellationToken.ThrowIfCancellationRequested();
                return new LlmEntitlementDecision
                { IsAllowed = profile.Model != DeniedModel, ReasonCode = "model_not_allowed" };
            }
        }

        private sealed class CountingPolicy : ILlmEntitlementPolicy
        {
            private readonly bool _allowed;
            private readonly string _reason;
            public CountingPolicy(bool allowed, string reason = "quota_exceeded")
            { _allowed = allowed; _reason = reason; }
            public int Calls;
            public LlmCompletionRequest Last;
            public Task<LlmEntitlementDecision> CheckAsync(LlmCompletionRequest request,
                LlmRouteProfile profile, CancellationToken cancellationToken = default)
            {
                Calls++; Last = request;
                return Task.FromResult(new LlmEntitlementDecision
                { IsAllowed = _allowed, ReasonCode = _reason, Message = "Host preflight denial.", RetryAfterSeconds = 9 });
            }
        }

        private sealed class WaitingPolicy : ILlmEntitlementPolicy
        {
            public readonly TaskCompletionSource<bool> Entered = new TaskCompletionSource<bool>();
            public async Task<LlmEntitlementDecision> CheckAsync(LlmCompletionRequest request,
                LlmRouteProfile profile, CancellationToken cancellationToken = default)
            {
                Entered.TrySetResult(true);
                await Task.Delay(Timeout.Infinite, cancellationToken);
                return LlmEntitlementDecision.Allowed;
            }
        }

        private sealed class CountingSink : ILlmUsageSink
        {
            public readonly List<LlmUsageRecord> Records = new List<LlmUsageRecord>();
            public TaskCompletionSource<bool> Block;
            public Exception Failure;
            public CancellationToken LastToken;
            public Task RecordAsync(LlmUsageRecord record, CancellationToken cancellationToken = default)
            {
                Records.Add(record); LastToken = cancellationToken;
                if (Failure != null) return Task.FromException(Failure);
                return Block?.Task ?? Task.CompletedTask;
            }
        }

        private sealed class DelegateRefresher : IGatewayAuthRefresher
        {
            private readonly Func<bool> _refresh;
            public DelegateRefresher(Func<bool> refresh) => _refresh = refresh;
            public int Calls;
            public Task<bool> RefreshAsync(CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Calls++; return Task.FromResult(_refresh());
            }
        }

        private sealed class WireTransport : IOpenAiHttpTransport
        {
            public readonly List<OpenAiHttpPostRequest> Requests = new List<OpenAiHttpPostRequest>();
            public Func<OpenAiHttpPostRequest, int, OpenAiHttpPostResult> Response;
            public string Sse = "data: {\"choices\":[{\"delta\":{\"content\":\"ok\"},\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]\n\n";
            public bool StreamDisposed;
            public int AuthStreamFailures;
            public string DebugLabel => "gateway-fake";
            public bool SupportsSseStreaming => true;
            public Task<OpenAiHttpPostResult> PostNonStreamingAsync(OpenAiHttpPostRequest request,
                CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int index = Requests.Count;
                Requests.Add(request);
                return Task.FromResult(Response == null ? Ok() : Response(request, index));
            }
            public Task<OpenAiHttpSseOpenResult> OpenSseResponseStreamAsync(OpenAiHttpPostRequest request,
                CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Requests.Add(request);
                if (Requests.Count <= AuthStreamFailures)
                    return Task.FromResult(new OpenAiHttpSseOpenResult { StatusCode = 401, ErrorBodyText = "{}" });
                return Task.FromResult(new OpenAiHttpSseOpenResult { StatusCode = 200 }
                    .WithRawStream(new ByteStream(Encoding.UTF8.GetBytes(Sse), () => StreamDisposed = true)));
            }
            public static OpenAiHttpPostResult Ok(string body = null) => new OpenAiHttpPostResult
            {
                StatusCode = 200,
                BodyText = body ?? "{\"id\":\"ok\",\"model\":\"served\",\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":\"ok\"},\"finish_reason\":\"stop\"}],\"usage\":{\"prompt_tokens\":2,\"completion_tokens\":3,\"total_tokens\":5}}"
            };
        }

        private sealed class ByteStream : MemoryStream
        {
            private readonly Action _onDispose;
            public ByteStream(byte[] data, Action onDispose) : base(data) => _onDispose = onDispose;
            public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken token)
            {
                token.ThrowIfCancellationRequested();
                return Task.FromResult(Read(buffer, offset, Math.Min(1, count)));
            }
            protected override void Dispose(bool disposing)
            {
                _onDispose(); base.Dispose(disposing);
            }
        }

        private sealed class ScriptedStream : MEAI.IChatClient
        {
            public int Calls;
            public bool NativeTool;
            public Task<MEAI.ChatResponse> GetResponseAsync(IEnumerable<MEAI.ChatMessage> messages,
                MEAI.ChatOptions options = null, CancellationToken cancellationToken = default) =>
                throw new NotSupportedException();
            public async IAsyncEnumerable<MEAI.ChatResponseUpdate> GetStreamingResponseAsync(
                IEnumerable<MEAI.ChatMessage> messages, MEAI.ChatOptions options = null,
                [EnumeratorCancellation] CancellationToken cancellationToken = default)
            {
                Calls++;
                yield return NativeTool ? new MEAI.ChatResponseUpdate
                {
                    Role = MEAI.ChatRole.Assistant,
                    Contents = new List<MEAI.AIContent>
                    {
                        new MEAI.FunctionCallContent("call-a", "read_stock", new Dictionary<string, object>())
                    }
                } : new MEAI.ChatResponseUpdate(MEAI.ChatRole.Assistant, "visible");
                await Task.CompletedTask;
                throw new LlmClientException("auth", LlmErrorCode.AuthExpired, 401);
            }
            public object GetService(Type serviceType, object serviceKey = null) => null;
            public void Dispose() { }
        }

        private sealed class WaitingChat : MEAI.IChatClient
        {
            public readonly TaskCompletionSource<bool> Entered = new TaskCompletionSource<bool>();
            public CancellationToken Token;
            public bool Disposed;
            public async Task<MEAI.ChatResponse> GetResponseAsync(IEnumerable<MEAI.ChatMessage> messages,
                MEAI.ChatOptions options = null, CancellationToken cancellationToken = default)
            {
                Token = cancellationToken; Entered.TrySetResult(true);
                await Task.Delay(Timeout.Infinite, cancellationToken);
                throw new InvalidOperationException();
            }
            public IAsyncEnumerable<MEAI.ChatResponseUpdate> GetStreamingResponseAsync(
                IEnumerable<MEAI.ChatMessage> messages, MEAI.ChatOptions options = null,
                CancellationToken cancellationToken = default) => throw new NotSupportedException();
            public object GetService(Type serviceType, object serviceKey = null) => null;
            public void Dispose() => Disposed = true;
        }

        private sealed class UsageDeadlineMarshaler : ILlmAsyncMarshaler
        {
            private readonly int _deadline;
            public UsageDeadlineMarshaler(int deadline) => _deadline = deadline;
            public Task<T> InvokeAsync<T>(Func<Task<T>> factory, CancellationToken cancellationToken) => factory();
            public Task DelayAsync(int milliseconds, CancellationToken cancellationToken) =>
                milliseconds == _deadline ? Task.CompletedTask : Task.Delay(milliseconds, cancellationToken);
        }
    }
}
#endif
