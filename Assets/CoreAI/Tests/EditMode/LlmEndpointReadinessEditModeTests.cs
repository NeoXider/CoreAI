using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Ai;
using CoreAI.Infrastructure.Llm;
using NUnit.Framework;

namespace CoreAI.Core.Tests.EditMode
{
    public sealed class LlmEndpointReadinessEditModeTests
    {
        [TestCase(200, true)]
        [TestCase(204, true)]
        [TestCase(302, false)]
        [TestCase(400, true)]
        [TestCase(405, true)]
        [TestCase(422, true)]
        [TestCase(429, true)]
        [TestCase(0, false)]
        [TestCase(401, false)]
        [TestCase(403, false)]
        [TestCase(404, false)]
        [TestCase(500, false)]
        public void HandlerPolicy_MatchesOpenAiRouteSemantics(int status, bool expected)
        {
            Assert.AreEqual(expected, LlmEndpointReadinessPolicy.IsHandlerReached(status));
        }

        [TestCase(404, true)]
        [TestCase(405, true)]
        [TestCase(400, false)]
        [TestCase(401, false)]
        [TestCase(500, false)]
        public void ModelsFallbackPolicy_OnlyAcceptsUnsupportedRoute(int status, bool expected)
        {
            Assert.AreEqual(expected, LlmEndpointReadinessPolicy.ShouldTryCompletions(status));
        }

        [Test]
        public async Task HttpClientProbe_Models404_FallsBackAndForwardsAuthorization()
        {
            List<HttpRequestMessage> requests = new();
            using HttpClient client = new(new DelegateHandler(request =>
            {
                requests.Add(request);
                return new HttpResponseMessage(
                    request.RequestUri.AbsolutePath.EndsWith("/models", StringComparison.Ordinal)
                        ? HttpStatusCode.NotFound
                        : HttpStatusCode.BadRequest);
            }));
            HttpClientOpenAiReadinessProbe probe = new(client);

            LlmEndpointReadinessResult result = await probe.ProbeAsync(new LlmEndpointReadinessRequest
            {
                BaseUrl = "http://localhost:1234/v1/",
                ApiKey = "secret-value",
                Mode = LlmEndpointReadinessMode.ModelsThenCompletions
            });

            Assert.IsTrue(result.IsReady);
            Assert.AreEqual(400, result.StatusCode);
            Assert.AreEqual(2, requests.Count);
            Assert.AreEqual("/v1/models", requests[0].RequestUri.AbsolutePath);
            Assert.AreEqual("/v1/chat/completions", requests[1].RequestUri.AbsolutePath);
            Assert.AreEqual("Bearer", requests[0].Headers.Authorization.Scheme);
            Assert.AreEqual("secret-value", requests[0].Headers.Authorization.Parameter);
            Assert.AreEqual("secret-value", requests[1].Headers.Authorization.Parameter);
        }

        [Test]
        public async Task HttpClientProbe_Models400_IsTerminalAndDoesNotFallback()
        {
            int calls = 0;
            using HttpClient client = new(new DelegateHandler(_ =>
            {
                calls++;
                return new HttpResponseMessage(HttpStatusCode.BadRequest);
            }));
            HttpClientOpenAiReadinessProbe probe = new(client);

            LlmEndpointReadinessResult result = await probe.ProbeAsync(new LlmEndpointReadinessRequest
            {
                BaseUrl = "https://example.test/v1",
                Mode = LlmEndpointReadinessMode.ModelsThenCompletions
            });

            Assert.IsFalse(result.IsReady);
            Assert.AreEqual(400, result.StatusCode);
            Assert.AreEqual(1, calls);
        }

        [Test]
        public void HttpClientProbe_CallerCancellationRemainsCancellation()
        {
            using HttpClient client = new(new AsyncDelegateHandler(
                (_, token) => Task.Delay(TimeSpan.FromMinutes(1), token)
                    .ContinueWith(_ => new HttpResponseMessage(HttpStatusCode.OK), token)));
            HttpClientOpenAiReadinessProbe probe = new(client);
            using CancellationTokenSource cancellation = new();
            cancellation.Cancel();

            Assert.CatchAsync<OperationCanceledException>(() => probe.ProbeAsync(
                new LlmEndpointReadinessRequest
                {
                    BaseUrl = "https://example.test/v1",
                    Mode = LlmEndpointReadinessMode.CompletionsOnly
                },
                cancellation.Token));
        }

        [Test]
        public async Task HttpClientProbe_RejectsUnsafeBaseUriWithoutSending()
        {
            int calls = 0;
            using HttpClient client = new(new DelegateHandler(_ =>
            {
                calls++;
                return new HttpResponseMessage(HttpStatusCode.OK);
            }));
            HttpClientOpenAiReadinessProbe probe = new(client);

            foreach (string value in new[]
                     {
                         "file:///tmp/model",
                         "https://user:password@example.test/v1",
                         "https://example.test/v1?token=secret",
                         "https://example.test/v1#fragment"
                     })
            {
                LlmEndpointReadinessResult result = await probe.ProbeAsync(
                    new LlmEndpointReadinessRequest { BaseUrl = value });
                Assert.IsFalse(result.IsReady, value);
                Assert.AreEqual(0, result.StatusCode, value);
            }

            Assert.AreEqual(0, calls);
        }

        [Test]
        public async Task HttpClientProbe_TransportTimeoutReturnsPortableFailure()
        {
            using HttpClient client = new(new AsyncDelegateHandler(async (_, token) =>
            {
                await Task.Delay(TimeSpan.FromMinutes(1), token);
                return new HttpResponseMessage(HttpStatusCode.OK);
            })) { Timeout = TimeSpan.FromMilliseconds(25) };
            HttpClientOpenAiReadinessProbe probe = new(client);

            LlmEndpointReadinessResult result = await probe.ProbeAsync(new LlmEndpointReadinessRequest
            {
                BaseUrl = "https://example.test/v1",
                Mode = LlmEndpointReadinessMode.CompletionsOnly,
                TimeoutSeconds = 10
            });

            Assert.IsFalse(result.IsReady);
            Assert.AreEqual(0, result.StatusCode);
            StringAssert.DoesNotContain("example.test", result.Error);
        }

        private sealed class DelegateHandler : HttpMessageHandler
        {
            private readonly Func<HttpRequestMessage, HttpResponseMessage> _response;

            public DelegateHandler(Func<HttpRequestMessage, HttpResponseMessage> response)
            {
                _response = response;
            }

            protected override Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request,
                CancellationToken cancellationToken) => Task.FromResult(_response(request));
        }

        private sealed class AsyncDelegateHandler : HttpMessageHandler
        {
            private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _response;

            public AsyncDelegateHandler(
                Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> response)
            {
                _response = response;
            }

            protected override Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request,
                CancellationToken cancellationToken) => _response(request, cancellationToken);
        }
    }
}
