#if COREAI_LLM
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Ai;
using CoreAI.Infrastructure.Llm.Gateway;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode
{
    public sealed class GatewayServiceClientEditModeTests
    {
        private SynchronizationContext _previous;
        [SetUp] public void SetUp()
        {
            _previous = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(null);
        }
        [TearDown] public void TearDown() => SynchronizationContext.SetSynchronizationContext(_previous);

        [Test]
        public async Task EntitlementV1_UsesEscapedRoleDynamicBearer_AndPreservesDenial()
        {
            SideTransport transport = new SideTransport
            {
                Result = new GatewayHttpResponse
                {
                    StatusCode = 200,
                    BodyText = "{\"allowed\":false,\"reason_code\":\"quota_exceeded\",\"message\":\"denied\",\"retry_after_seconds\":8}"
                }
            };
            string token = "first";
            GatewayServiceClient client = new GatewayServiceClient(Options(), transport, () => "Bearer " + token);
            GatewayEntitlementSnapshot first = await client.CheckEntitlementAsync("role /?");
            token = "second";
            await client.CheckEntitlementAsync("next");
            Assert.That(first.IsAvailable, Is.True);
            Assert.That(first.Allowed, Is.False);
            Assert.That(first.ReasonCode, Is.EqualTo("quota_exceeded"));
            Assert.That(first.RetryAfterSeconds, Is.EqualTo(8));
            Assert.That(transport.Requests[0].Method, Is.EqualTo("GET"));
            Assert.That(transport.Requests[0].Url,
                Is.EqualTo("https://gateway.test/v1/entitlement?role_id=role%20%2F%3F"));
            Assert.That(transport.Requests[1].Headers.Single(pair => pair.Key == "Authorization").Value,
                Is.EqualTo("Bearer second"));
        }

        [TestCase("{}")]
        [TestCase("{")]
        [TestCase("null")]
        [TestCase("{\"allowed\":0}")]
        [TestCase("{\"allowed\":\"true\"}")]
        public async Task InvalidOrMissingAllowed_IsUnavailable(string body)
        {
            SideTransport transport = new SideTransport
            {
                Result = new GatewayHttpResponse { StatusCode = 200, BodyText = body }
            };
            GatewayEntitlementSnapshot result = await new GatewayServiceClient(Options(), transport)
                .CheckEntitlementAsync("role");
            Assert.That(result.IsAvailable, Is.False);
        }

        [TestCase(GatewayPreflightUnavailablePolicy.Allow, true)]
        [TestCase(GatewayPreflightUnavailablePolicy.Deny, false)]
        public async Task UnavailablePreflight_UsesExplicitConfiguredPolicy(
            GatewayPreflightUnavailablePolicy behavior, bool allowed)
        {
            SideTransport transport = new SideTransport { Failure = new IOException("sensitive") };
            GatewayEntitlementPolicy policy = new GatewayEntitlementPolicy(
                new GatewayServiceClient(Options(), transport), behavior);
            if (!allowed)
            {
                LlmClientException failure = Assert.ThrowsAsync<LlmClientException>(async () =>
                    await policy.CheckAsync(new LlmCompletionRequest(), new LlmRouteProfile()));
                Assert.That(failure.ErrorCode, Is.EqualTo(LlmErrorCode.BackendUnavailable));
                return;
            }
            LlmEntitlementDecision decision = await policy.CheckAsync(new LlmCompletionRequest(), new LlmRouteProfile());
            Assert.That(decision.IsAllowed, Is.True);
        }

        [TestCase(GatewayPreflightUnavailablePolicy.Allow)]
        [TestCase(GatewayPreflightUnavailablePolicy.Deny)]
        public async Task ValidDenial_NeverBecomesAllow(GatewayPreflightUnavailablePolicy behavior)
        {
            SideTransport transport = new SideTransport
            {
                Result = new GatewayHttpResponse { StatusCode = 200, BodyText = "{\"allowed\":false}" }
            };
            LlmEntitlementDecision decision = await new GatewayEntitlementPolicy(
                new GatewayServiceClient(Options(), transport), behavior)
                .CheckAsync(new LlmCompletionRequest(), new LlmRouteProfile());
            Assert.That(decision.IsAllowed, Is.False);
        }

        [TestCase(401)]
        [TestCase(403)]
        [TestCase(402)]
        [TestCase(429)]
        [TestCase(503)]
        public async Task FailedPreflight_StatusAndRetryRemainUnavailable(int status)
        {
            SideTransport transport = new SideTransport
            {
                Result = new GatewayHttpResponse
                {
                    StatusCode = status, BodyText = "{}",
                    Headers = new Dictionary<string, IEnumerable<string>> { ["Retry-After"] = new[] { "7" } }
                }
            };
            GatewayEntitlementSnapshot result = await new GatewayServiceClient(Options(), transport)
                .CheckEntitlementAsync("role");
            Assert.That(result.IsAvailable, Is.False);
            Assert.That(result.HttpStatus, Is.EqualTo(status));
            Assert.That(result.RetryAfterSeconds, Is.EqualTo(7));
        }

        [Test]
        public async Task ServiceCallerCancellation_Propagates()
        {
            using CancellationTokenSource stop = new CancellationTokenSource();
            SideTransport transport = new SideTransport
            {
                OnSend = request => stop.Cancel(),
                Failure = new OperationCanceledException()
            };
            GatewayServiceClient client = new GatewayServiceClient(Options(), transport);
            Assert.CatchAsync<OperationCanceledException>(
                async () => await client.CheckEntitlementAsync("role", stop.Token));
            await Task.CompletedTask;
        }

        [Test]
        public async Task ServiceResponseLimit_IsMeasuredInUtf8Bytes()
        {
            SideTransport transport = new SideTransport
            {
                Result = new GatewayHttpResponse { StatusCode = 200, BodyText = "{\"allowed\":true,\"x\":\"☃\"}" }
            };
            GatewayClientOptions options = Options();
            options.MaxResponseBytes = Encoding.UTF8.GetByteCount(transport.Result.BodyText) - 1;
            GatewayEntitlementSnapshot result = await new GatewayServiceClient(options, transport)
                .CheckEntitlementAsync("role");
            Assert.That(result.IsAvailable, Is.False);
        }

        [Test]
        public async Task UsageV1_PreservesObservedSchemaAndMeasuredZero()
        {
            SideTransport transport = new SideTransport
            {
                Result = new GatewayHttpResponse { StatusCode = 204 }
            };
            GatewayUsageSink sink = new GatewayUsageSink(new GatewayServiceClient(Options(), transport));
            await sink.RecordAsync(new LlmUsageRecord
            {
                TraceId = "trace", RoleId = "role", RoutingProfileId = "route", Model = "served",
                PromptTokens = 0, CompletionTokens = 0, TotalTokens = 0, Success = true,
                ReasoningTokens = 4, CacheReadTokens = 5
            });
            JObject payload = JObject.Parse(transport.Requests.Single().JsonBody);
            Assert.That(payload.Properties().Select(property => property.Name), Is.EquivalentTo(new[]
            {
                "trace_id", "role_id", "routing_profile_id", "model", "prompt_tokens",
                "completion_tokens", "total_tokens", "streaming", "success", "estimated"
            }));
            Assert.That((bool)payload["estimated"], Is.False);
            Assert.That((int)payload["total_tokens"], Is.Zero);
            Assert.That(transport.Requests.Single().Method, Is.EqualTo("POST"));
        }

        [Test]
        public async Task UsageRejectedStatus_IsNotReportedAsSuccess()
        {
            SideTransport transport = new SideTransport
            {
                Result = new GatewayHttpResponse { StatusCode = 429 }
            };
            GatewayServiceClient client = new GatewayServiceClient(Options(), transport);
            LlmClientException error = Assert.ThrowsAsync<LlmClientException>(
                async () => await client.ReportUsageAsync(new GatewayUsagePayload()));
            Assert.That(error.HttpStatus, Is.EqualTo(429));
            await Task.CompletedTask;
        }

        [Test]
        public async Task HttpTransport_CapsBody_DisposesResponse_AndLeavesInjectedClientOwnedByCaller()
        {
            TrackedContent body = new TrackedContent(new string('x', 32));
            int responses = 0;
            FakeHandler handler = new FakeHandler(() => new HttpResponseMessage(HttpStatusCode.OK)
            { Content = responses++ == 0 ? body : new StringContent("ok") });
            using HttpClient http = new HttpClient(handler);
            using (HttpClientGatewayTransport transport = new HttpClientGatewayTransport(http))
            {
                Assert.ThrowsAsync<InvalidDataException>(async () => await transport.SendAsync(
                    new GatewayHttpRequest { Url = "https://gateway.test/usage", MaxResponseBytes = 4 }));
            }
            Assert.That(body.Disposed, Is.True);
            using HttpResponseMessage after = await http.GetAsync("https://gateway.test/owned");
            Assert.That(handler.Calls, Is.EqualTo(2));
        }

        [Test]
        public async Task HttpTransport_CallerCancellationInterruptsStalledBody()
        {
            BlockingBody body = new BlockingBody();
            FakeHandler handler = new FakeHandler(() => new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StreamContent(body) });
            using HttpClient http = new HttpClient(handler);
            using HttpClientGatewayTransport transport = new HttpClientGatewayTransport(http);
            using CancellationTokenSource stop = new CancellationTokenSource();
            Task request = transport.SendAsync(new GatewayHttpRequest { Url = "https://gateway.test/check" }, stop.Token);
            await body.Entered.Task;
            stop.Cancel();
            Assert.CatchAsync<OperationCanceledException>(async () => await request);
            Assert.That(body.Disposed, Is.True);
        }


        [TestCase(true)]
        [TestCase(false)]
        public async Task ThrowingCancellation_StillCleansOwnedClientAndPreservesBorrowedClient(bool ownsClient)
        {
            ThrowingCancellationHandler handler = new ThrowingCancellationHandler();
            using HttpClient http = new HttpClient(handler);
            HttpClientGatewayTransport transport = new HttpClientGatewayTransport(http, ownsClient);
            Task pending = transport.SendAsync(new GatewayHttpRequest { Url = "https://gateway.test/check" });
            await handler.Entered.Task;
            Exception disposalFailure = null;
            try { transport.Dispose(); }
            catch (Exception failure) { disposalFailure = failure; }
            transport.Dispose();
            handler.FailPending();
            Assert.CatchAsync<Exception>(async () => await pending);
            if (disposalFailure != null) Assert.That(disposalFailure, Is.InstanceOf<AggregateException>());
            Assert.That(handler.ThrowingCallbacks, Is.EqualTo(1));
            Assert.That(handler.Disposals, Is.EqualTo(ownsClient ? 1 : 0));
            Assert.ThrowsAsync<ObjectDisposedException>(async () => await transport.SendAsync(
                new GatewayHttpRequest { Url = "https://gateway.test/after" }));
            if (!ownsClient)
            {
                using HttpResponseMessage after = await http.GetAsync("https://gateway.test/borrowed");
                Assert.That(after.StatusCode, Is.EqualTo(HttpStatusCode.OK));
                Assert.That(handler.Disposals, Is.Zero);
            }
        }

        [TestCase("http://remote.test/v1", false)]
        [TestCase("http://192.168.1.20:8080/v1", false)]
        [TestCase("http://127.0.0.1/v1", false)]
        [TestCase("https://user:secret@gateway.test/v1", false)]
        public void UnsafeDestination_IsRejectedWithoutEchoingCredential(string endpoint, bool allowLoopback)
        {
            ArgumentException failure = Assert.Throws<ArgumentException>(() =>
                GatewayClientOptions.ValidateEndpoint(endpoint, allowLoopback));
            Assert.That(failure.Message, Does.Not.Contain(endpoint));
        }

        [TestCase("http://127.0.0.1:8080/v1")]
        [TestCase("http://192.168.1.20:8080/v1")]
        public void ExplicitInsecureHttpOptIn_AcceptsSelectedHost(string endpoint)
        {
            Assert.That(GatewayClientOptions.ValidateEndpoint(endpoint, true).Scheme, Is.EqualTo(Uri.UriSchemeHttp));
        }

        private static GatewayClientOptions Options() => new GatewayClientOptions
        { GatewayBaseUrl = "https://gateway.test/v1" };

        private sealed class SideTransport : IGatewayHttpTransport
        {
            public readonly List<GatewayHttpRequest> Requests = new List<GatewayHttpRequest>();
            public GatewayHttpResponse Result;
            public Exception Failure;
            public Action<GatewayHttpRequest> OnSend;
            public Task<GatewayHttpResponse> SendAsync(GatewayHttpRequest request,
                CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Requests.Add(request);
                OnSend?.Invoke(request);
                return Failure == null ? Task.FromResult(Result) : Task.FromException<GatewayHttpResponse>(Failure);
            }
        }


        private sealed class ThrowingCancellationHandler : HttpMessageHandler
        {
            public readonly TaskCompletionSource<bool> Entered = new TaskCompletionSource<bool>();
            private readonly TaskCompletionSource<HttpResponseMessage> _pending = new TaskCompletionSource<HttpResponseMessage>();
            private CancellationTokenRegistration _registration;
            private int _calls;
            public int ThrowingCallbacks;
            public int Disposals;
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
            {
                if (++_calls != 1)
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("ok") });
                _registration = token.Register(() =>
                {
                    ThrowingCallbacks++;
                    throw new InvalidOperationException("Cancellation callback failed.");
                });
                Entered.TrySetResult(true);
                return _pending.Task;
            }
            public void FailPending() => _pending.TrySetException(new IOException("Late cancelled request completion."));
            protected override void Dispose(bool disposing)
            {
                Disposals++;
                _registration.Dispose();
                base.Dispose(disposing);
            }
        }

        private sealed class FakeHandler : HttpMessageHandler
        {
            private readonly Func<HttpResponseMessage> _response;
            public FakeHandler(Func<HttpResponseMessage> response) => _response = response;
            public int Calls;
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
                CancellationToken cancellationToken)
            {
                Calls++; return Task.FromResult(_response());
            }
        }

        private sealed class TrackedContent : StringContent
        {
            public TrackedContent(string body) : base(body) { }
            public bool Disposed;
            protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
        }

        private sealed class BlockingBody : Stream
        {
            public readonly TaskCompletionSource<bool> Entered = new TaskCompletionSource<bool>();
            public bool Disposed;
            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
            public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken token)
            {
                Entered.TrySetResult(true);
                await Task.Delay(Timeout.Infinite, token);
                return 0;
            }
            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            public override void Flush() { }
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long length) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
        }
    }
}
#endif
