using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Ai;
using CoreAI.Infrastructure.Llm;
using CoreAI.Infrastructure.Logging;
using NUnit.Framework;
using UnityEngine;

namespace CoreAI.Tests.EditMode
{
    /// <summary>
    /// Disposal guards and resolve-time generation leases of the real LLM client registry.
    /// </summary>
    [TestFixture]
    public sealed class LlmClientRegistryLeaseEditModeTests
    {
        private sealed class CapturingClient : ILlmClient
        {
            public Task<LlmCompletionResult> CompleteAsync(
                LlmCompletionRequest request,
                CancellationToken cancellationToken = default)
            {
                return Task.FromResult(new LlmCompletionResult { Ok = true, Content = "ok" });
            }

            public async IAsyncEnumerable<LlmStreamChunk> CompleteStreamingAsync(
                LlmCompletionRequest request,
                [System.Runtime.CompilerServices.EnumeratorCancellation]
                CancellationToken cancellationToken = default)
            {
                yield return new LlmStreamChunk { Text = "ok" };
                await Task.Yield();
                yield return new LlmStreamChunk { IsDone = true, Text = string.Empty };
            }
        }

        private sealed class PerEndpointFactory : ILlmEndpointClientFactory
        {
            private readonly ILlmClient _client;

            public PerEndpointFactory(ILlmClient client)
            {
                _client = client;
            }

            public Task<LlmEndpointClientActivation> ActivateAsync(
                LlmEndpointDescriptor descriptor,
                string sessionApiKey,
                CancellationToken cancellationToken)
            {
                return Task.FromResult(new LlmEndpointClientActivation
                {
                    Client = _client,
                    Mode = LlmExecutionMode.ClientOwnedApi
                });
            }
        }

        private sealed class MemoryStore : ILlmEndpointRegistryStore
        {
            public LlmEndpointRegistryState Load()
            {
                return new LlmEndpointRegistryState();
            }

            public void Save(LlmEndpointRegistryState state)
            {
            }
        }

        private CoreAISettingsAsset _settings;

        [SetUp]
        public void SetUp()
        {
            _settings = ScriptableObject.CreateInstance<CoreAISettingsAsset>();
        }

        [TearDown]
        public void TearDown()
        {
            UnityEngine.Object.DestroyImmediate(_settings);
        }

        [Test]
        public async Task ResolveClientForRole_TakesNoLease_AndCompletionReturnsToZero()
        {
            LlmClientRegistry registry = CreateRegistry(new CapturingClient());
            try
            {
                await registry.AddOrUpdateEndpointAsync(Descriptor("endpoint-a"));
                registry.AssignRoleProfile("role-a", "endpoint-a");

                ILlmClient client = registry.ResolveClientForRole("role-a");
                Assert.AreEqual(0, InFlightRequests(registry),
                    "Resolving alone must not take a lease: callers that only read the tool contract " +
                    "or the context window never execute or dispose the handle, so the count would leak.");

                LlmCompletionResult result = await client.CompleteAsync(new LlmCompletionRequest
                {
                    AgentRoleId = "role-a",
                    UserPayload = "hi"
                });
                Assert.IsTrue(result.Ok);
                Assert.AreEqual(0, InFlightRequests(registry),
                    "The lease must be released when the request finishes.");
            }
            finally
            {
                registry.Dispose();
            }
        }

        [Test]
        public async Task RepeatedResolveWithoutExecuting_DoesNotLeakTheInFlightCount()
        {
            // WHY: RoutingLlmClient resolves a route on every SupportsNativeToolCallingForRole and
            // ResolveContextWindowTokensForRole call and neither executes nor disposes the handle. A
            // lease taken at resolve time therefore never comes back, and the endpoint drain — which
            // waits for InFlightRequests to reach zero — hangs forever.
            LlmClientRegistry registry = CreateRegistry(new CapturingClient());
            try
            {
                await registry.AddOrUpdateEndpointAsync(Descriptor("endpoint-a"));
                registry.AssignRoleProfile("role-a", "endpoint-a");

                for (int i = 0; i < 25; i++)
                {
                    registry.ResolveClientForRole("role-a");
                }

                Assert.AreEqual(0, InFlightRequests(registry),
                    "25 resolves without a request must leave the in-flight count at zero");
            }
            finally
            {
                registry.Dispose();
            }
        }

        [Test]
        public async Task ResolveClientForRole_TakesNoLease_AndStreamReturnsToZero()
        {
            LlmClientRegistry registry = CreateRegistry(new CapturingClient());
            try
            {
                await registry.AddOrUpdateEndpointAsync(Descriptor("endpoint-a"));
                registry.AssignRoleProfile("role-a", "endpoint-a");

                ILlmClient client = registry.ResolveClientForRole("role-a");
                Assert.AreEqual(0, InFlightRequests(registry),
                    "Resolving alone must not take a lease.");

                await foreach (LlmStreamChunk _ in client.CompleteStreamingAsync(new LlmCompletionRequest
                {
                    AgentRoleId = "role-a",
                    UserPayload = "hi"
                }))
                {
                }

                Assert.AreEqual(0, InFlightRequests(registry));
            }
            finally
            {
                registry.Dispose();
            }
        }

        [Test]
        public async Task ResolveClientForRole_AfterDispose_ThrowsObjectDisposedException()
        {
            LlmClientRegistry registry = CreateRegistry(new CapturingClient());
            await registry.AddOrUpdateEndpointAsync(Descriptor("endpoint-a"));
            registry.AssignRoleProfile("role-a", "endpoint-a");
            registry.Dispose();

            Assert.Throws<ObjectDisposedException>(() => registry.ResolveClientForRole("role-a"));
        }

        [Test]
        public void ResolveRouteForRole_AfterDispose_ThrowsObjectDisposedException()
        {
            LlmClientRegistry registry = CreateRegistry(new CapturingClient());
            registry.Dispose();

            Assert.Throws<ObjectDisposedException>(() => registry.ResolveRouteForRole("role-a", ""));
        }

        [Test]
        public void GetEndpoints_AfterDispose_ThrowsObjectDisposedException()
        {
            LlmClientRegistry registry = CreateRegistry(new CapturingClient());
            registry.Dispose();

            Assert.Throws<ObjectDisposedException>(() => registry.GetEndpoints());
        }

        [Test]
        public void AssignRoleProfile_AfterDispose_ThrowsObjectDisposedException()
        {
            LlmClientRegistry registry = CreateRegistry(new CapturingClient());
            registry.Dispose();

            Assert.Throws<ObjectDisposedException>(() => registry.AssignRoleProfile("role-a", "endpoint-a"));
        }

        [Test]
        public void ReportRouteFailure_AfterDispose_ThrowsObjectDisposedException()
        {
            LlmClientRegistry registry = CreateRegistry(new CapturingClient());
            registry.Dispose();

            Assert.Throws<ObjectDisposedException>(
                () => registry.ReportRouteFailure("endpoint-a", 1, LlmErrorCode.BackendUnavailable, "down"));
        }

        [Test]
        public void AddOrUpdateEndpointAsync_AfterDispose_ThrowsObjectDisposedException()
        {
            LlmClientRegistry registry = CreateRegistry(new CapturingClient());
            registry.Dispose();

            Assert.ThrowsAsync<ObjectDisposedException>(
                async () => await registry.AddOrUpdateEndpointAsync(Descriptor("endpoint-a")));
        }

        private LlmClientRegistry CreateRegistry(ILlmClient client)
        {
            return new LlmClientRegistry(
                GameLoggerUnscopedFallback.Instance,
                _settings,
                null,
                new MemoryStore(),
                new PerEndpointFactory(client));
        }

        private static int InFlightRequests(LlmClientRegistry registry)
        {
            IReadOnlyList<LlmEndpointSnapshot> endpoints = registry.GetEndpoints();
            Assert.AreEqual(1, endpoints.Count);
            return endpoints[0].InFlightRequests;
        }

        private static LlmEndpointDescriptor Descriptor(string id)
        {
            return new LlmEndpointDescriptor
            {
                EndpointId = id,
                DisplayName = id,
                Kind = LlmEndpointKind.HttpOpenAi,
                BaseUrl = "https://example.test/v1",
                Model = "test",
                Active = true,
                ContextWindowTokens = 8192
            };
        }
    }
}
