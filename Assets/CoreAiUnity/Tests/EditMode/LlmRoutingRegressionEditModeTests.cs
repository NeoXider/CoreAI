using System;
using System.Linq;
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
    /// Regression tests for the 2026-07-16 routing audit fixes: the "fallback" sentinel echo,
    /// wildcard role patterns, owned-host release on re-save, CancelInFlight semantics,
    /// key-auth restore, descriptor behavior-field round-trip, and atomic route resolution.
    /// </summary>
    public sealed class LlmRoutingRegressionEditModeTests
    {
        private sealed class MemoryStore : ILlmEndpointRegistryStore
        {
            public LlmEndpointRegistryState State { get; set; } = new();

            public LlmEndpointRegistryState Load()
            {
                return State;
            }

            public void Save(LlmEndpointRegistryState state)
            {
                State = state;
            }
        }

        private sealed class FakeFactory : ILlmEndpointClientFactory
        {
            public int Calls { get; private set; }
            public ILlmClient Client { get; set; } = new StubLlmClient();
            public Func<Task> ReleaseOwnedHostAsync { get; set; }
            public TaskCompletionSource<LlmEndpointClientActivation> Pending { get; set; }

            public Task<LlmEndpointClientActivation> ActivateAsync(
                LlmEndpointDescriptor descriptor,
                string sessionApiKey,
                CancellationToken cancellationToken)
            {
                Calls++;
                if (Pending != null)
                {
                    return Pending.Task;
                }

                return Task.FromResult(new LlmEndpointClientActivation
                {
                    Client = Client,
                    Mode = LlmExecutionMode.ClientOwnedApi,
                    ReleaseOwnedHostAsync = ReleaseOwnedHostAsync
                });
            }
        }

        private sealed class NamedClient : ILlmClient
        {
            private readonly string _content;

            public NamedClient(string content)
            {
                _content = content;
            }

            public Task<LlmCompletionResult> CompleteAsync(
                LlmCompletionRequest request,
                CancellationToken cancellationToken = default)
            {
                return Task.FromResult(new LlmCompletionResult { Ok = true, Content = _content });
            }
        }

        private sealed class UnresolvableSecretProvider : ILlmEndpointSecretProvider
        {
            public bool TryResolve(string secretReference, out string secret)
            {
                secret = "";
                return false;
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
        public async Task FallbackSentinelEcho_ResolvesLegacyFallbackInsteadOfRoutingUnavailable()
        {
            LlmClientRegistry registry = BuildRegistry(new MemoryStore(), new FakeFactory());
            registry.SetLegacyFallback(new NamedClient("legacy"));

            string reported = registry.ResolveProfileIdForRole("Chat");
            LlmCompletionResult retry = await registry.ResolveClientForRole("Chat", reported)
                .CompleteAsync(new LlmCompletionRequest());

            Assert.AreEqual("fallback", reported);
            Assert.IsTrue(retry.Ok, "A retried request annotated with the sentinel must re-resolve: " + retry.Error);
            Assert.AreEqual("legacy", retry.Content);
        }

        [Test]
        public async Task ExplicitProfileNamedFallback_StillResolvesThatProfile()
        {
            FakeFactory factory = new() { Client = new NamedClient("real-fallback") };
            LlmClientRegistry registry = BuildRegistry(new MemoryStore(), factory);
            await registry.AddOrUpdateEndpointAsync(Descriptor("fallback", "https://example.test/v1"));

            LlmCompletionResult result = await registry.ResolveClientForRole("Chat", "fallback")
                .CompleteAsync(new LlmCompletionRequest());

            Assert.AreEqual("real-fallback", result.Content);
        }

        [Test]
        public async Task WildcardRolePattern_MatchesPrefixAndPrefersExactAssignment()
        {
            LlmClientRegistry registry = BuildRegistry(new MemoryStore(), new FakeFactory());
            await registry.AddOrUpdateEndpointAsync(Descriptor("brain", "https://example.test/v1"));
            await registry.AddOrUpdateEndpointAsync(Descriptor("boss-brain", "https://example.test/v1"));
            registry.AssignRoleProfile("npc.*", "brain");
            registry.AssignRoleProfile("npc.boss", "boss-brain");

            Assert.AreEqual("brain", registry.GetRoleProfile("npc.guard"));
            Assert.AreEqual("boss-brain", registry.GetRoleProfile("npc.boss"));
            Assert.AreEqual("", registry.GetRoleProfile("player"));
        }

        [Test]
        public async Task WildcardRolePattern_LongestPrefixWinsOverStar()
        {
            LlmClientRegistry registry = BuildRegistry(new MemoryStore(), new FakeFactory());
            await registry.AddOrUpdateEndpointAsync(Descriptor("generic", "https://example.test/v1"));
            await registry.AddOrUpdateEndpointAsync(Descriptor("npc-brain", "https://example.test/v1"));
            registry.AssignRoleProfile("*", "generic");
            registry.AssignRoleProfile("npc.*", "npc-brain");

            Assert.AreEqual("npc-brain", registry.GetRoleProfile("npc.guard"));
            Assert.AreEqual("generic", registry.GetRoleProfile("player"));
        }

        [Test]
        public async Task ResaveAsInactive_ReleasesTheReadyOwnedHost()
        {
            int releases = 0;
            TaskCompletionSource<bool> released = new(TaskCreationOptions.RunContinuationsAsynchronously);
            FakeFactory factory = new()
            {
                ReleaseOwnedHostAsync = () =>
                {
                    releases++;
                    released.TrySetResult(true);
                    return Task.CompletedTask;
                }
            };
            LlmClientRegistry registry = BuildRegistry(new MemoryStore(), factory);
            LlmEndpointDescriptor descriptor = Descriptor("owned", "https://example.test/v1");
            await registry.AddOrUpdateEndpointAsync(descriptor);
            Assert.AreEqual(LlmEndpointLifecycleState.Ready, registry.GetEndpoints().Single().State);

            descriptor.Active = false;
            await registry.AddOrUpdateEndpointAsync(descriptor);

            // WHY (bounded wait): the drain-then-release runs fire-and-forget; asserting inline only
            // works while the release path happens to complete synchronously.
            Task finished = await Task.WhenAny(released.Task, Task.Delay(5000));
            Assert.AreSame(released.Task, finished,
                "Re-saving a Ready endpoint as inactive must release its owned host.");
            Assert.AreEqual(1, releases);
            Assert.AreEqual(LlmEndpointLifecycleState.Inactive, registry.GetEndpoints().Single().State);
        }

        [Test]
        public async Task CancelInFlightRemoval_ThrowsNotSupportedWithoutMutation()
        {
            LlmClientRegistry registry = BuildRegistry(new MemoryStore(), new FakeFactory());
            await registry.AddOrUpdateEndpointAsync(Descriptor("cloud", "https://example.test/v1"));

            NotSupportedException caught = null;
            try
            {
                await registry.RemoveEndpointAsync("cloud", LlmEndpointRemovalMode.CancelInFlight);
            }
            catch (NotSupportedException ex)
            {
                caught = ex;
            }

            Assert.IsNotNull(caught, "CancelInFlight must be rejected loudly, not reported as 'not found'.");
            Assert.AreEqual("cloud", registry.GetEndpoints().Single().Descriptor.EndpointId);
        }

        [Test]
        public void Restore_KeyAuthEndpointWithUnresolvableSecret_WaitsInsteadOfFailing()
        {
            LlmEndpointDescriptor descriptor = Descriptor("cloud", "https://example.test/v1");
            descriptor.SecretReference = "MISSING_ENV_KEY";
            MemoryStore store = new()
            {
                State = new LlmEndpointRegistryState { Endpoints = new[] { descriptor } }
            };
            FakeFactory factory = new();

            LlmClientRegistry registry = BuildRegistry(store, factory, new UnresolvableSecretProvider());
            LlmEndpointSnapshot snapshot = registry.GetEndpoints().Single();

            Assert.AreEqual(0, factory.Calls, "Activation with an empty key would deterministically fail.");
            Assert.AreEqual(LlmEndpointLifecycleState.Inactive, snapshot.State);
            Assert.That(snapshot.Error, Does.Contain("MISSING_ENV_KEY"));
        }

        [Test]
        public async Task DescriptorBehaviorFields_SurvivePersistenceAndSnapshots()
        {
            MemoryStore store = new();
            LlmClientRegistry registry = BuildRegistry(store, new FakeFactory());
            LlmEndpointDescriptor descriptor = Descriptor("cloud", "https://example.test/v1");
            descriptor.MaxTokens = 512;
            descriptor.ThinkingBudgetTokens = 2048;
            descriptor.ExtraBodyJson = "{\"top_k\":40}";

            await registry.AddOrUpdateEndpointAsync(descriptor);

            LlmEndpointDescriptor snapshot = registry.GetEndpoints().Single().Descriptor;
            Assert.AreEqual(512, snapshot.MaxTokens);
            Assert.AreEqual(2048, snapshot.ThinkingBudgetTokens);
            Assert.AreEqual("{\"top_k\":40}", snapshot.ExtraBodyJson);
            LlmEndpointDescriptor persisted = store.State.Endpoints.Single();
            Assert.AreEqual(512, persisted.MaxTokens);
            Assert.AreEqual(2048, persisted.ThinkingBudgetTokens);
            Assert.AreEqual("{\"top_k\":40}", persisted.ExtraBodyJson);
        }

        [Test]
        public async Task EditingBehaviorFields_IsNotCoalescedWithAnInFlightActivation()
        {
            FakeFactory factory = new() { Pending = new TaskCompletionSource<LlmEndpointClientActivation>() };
            LlmClientRegistry registry = BuildRegistry(new MemoryStore(), factory);
            LlmEndpointDescriptor descriptor = Descriptor("cloud", "https://example.test/v1");
            Task<LlmEndpointSnapshot> first = registry.AddOrUpdateEndpointAsync(descriptor);

            descriptor.MaxTokens = 999;
            Task<LlmEndpointSnapshot> second = registry.AddOrUpdateEndpointAsync(descriptor);

            Assert.AreEqual(2, factory.Calls, "A behavior-field edit must re-activate, not join the old activation.");
            factory.Pending.SetResult(new LlmEndpointClientActivation
            {
                Client = new StubLlmClient(),
                Mode = LlmExecutionMode.ClientOwnedApi
            });
            await Task.WhenAll(first, second);
        }

        [Test]
        public async Task ResolveRouteForRole_MatchesIndividualResolversUnderStableState()
        {
            FakeFactory factory = new() { Client = new NamedClient("routed") };
            LlmClientRegistry registry = BuildRegistry(new MemoryStore(), factory);
            LlmEndpointDescriptor descriptor = Descriptor("cloud", "https://example.test/v1");
            descriptor.ContextWindowTokens = 8192;
            await registry.AddOrUpdateEndpointAsync(descriptor);
            registry.AssignRoleProfile("Chat", "cloud");

            LlmRoleRouteSnapshot route = registry.ResolveRouteForRole("Chat", "");

            Assert.AreEqual(registry.ResolveProfileIdForRole("Chat"), route.ProfileId);
            Assert.AreEqual(registry.ResolveContextWindowForRole("Chat"), route.ContextWindowTokens);
            Assert.AreEqual(registry.ResolveExecutionModeForRole("Chat"), route.Mode);
            Assert.AreEqual("routed", (await route.Client.CompleteAsync(new LlmCompletionRequest())).Content);
        }

        private sealed class NativeToolsClient : ILlmClient
        {
            public bool SupportsNativeToolCalling => true;

            public Task<LlmCompletionResult> CompleteAsync(
                LlmCompletionRequest request,
                CancellationToken cancellationToken = default)
            {
                return Task.FromResult(new LlmCompletionResult { Ok = true, Content = "native" });
            }
        }

        [Test]
        public async Task ToolStrategy_FollowsTheRoutedEndpointNotTheRoleDefault()
        {
            FakeFactory factory = new() { Client = new NativeToolsClient() };
            LlmClientRegistry registry = BuildRegistry(new MemoryStore(), factory);
            registry.SetLegacyFallback(new NamedClient("legacy-no-tools"));
            await registry.AddOrUpdateEndpointAsync(Descriptor("cloud", "https://example.test/v1"));
            RoutingLlmClient routing = new(registry);

            Assert.IsFalse(routing.SupportsNativeToolCallingForRole("Chat"),
                "Unrouted role must keep the legacy backend's text tool contract.");
            Assert.IsTrue(routing.SupportsNativeToolCallingForRole("Chat", "cloud"),
                "A profile-pinned request must use the routed endpoint's native tool contract.");

            registry.AssignRoleProfile("Chat", "cloud");
            Assert.IsTrue(routing.SupportsNativeToolCallingForRole("Chat", ""),
                "After a runtime re-route the role default must follow the new endpoint.");
        }

        [Test]
        public async Task RoutedContextWindow_ReportedOnlyForRealRoutes()
        {
            FakeFactory factory = new();
            LlmClientRegistry registry = BuildRegistry(new MemoryStore(), factory);
            LlmEndpointDescriptor descriptor = Descriptor("cloud", "https://example.test/v1");
            descriptor.ContextWindowTokens = 8192;
            await registry.AddOrUpdateEndpointAsync(descriptor);
            RoutingLlmClient routing = new(registry);

            Assert.IsNull(routing.ResolveContextWindowTokensForRole("Chat", ""),
                "Unrouted requests are budgeted by settings, not by a routing constant.");
            Assert.AreEqual(8192, routing.ResolveContextWindowTokensForRole("Chat", "cloud"));

            registry.AssignRoleProfile("Chat", "cloud");
            Assert.AreEqual(8192, routing.ResolveContextWindowTokensForRole("Chat", ""));
        }

        [Test]
        public async Task KeepWarmOnly_ActivatesTheHostWithoutServingTraffic()
        {
            FakeFactory factory = new() { Client = new NamedClient("warm") };
            LlmClientRegistry registry = BuildRegistry(new MemoryStore(), factory);
            LlmEndpointDescriptor descriptor = Descriptor("warm", "https://example.test/v1");
            descriptor.Active = false;
            descriptor.KeepWarm = true;

            LlmEndpointSnapshot snapshot = await registry.AddOrUpdateEndpointAsync(descriptor);

            Assert.AreEqual(1, factory.Calls, "KeepWarm must start the host even while inactive.");
            Assert.AreEqual(LlmEndpointLifecycleState.Ready, snapshot.State);
            LlmCompletionResult routed = await registry.ResolveClientForRole("Chat", "warm")
                .CompleteAsync(new LlmCompletionRequest());
            Assert.IsFalse(routed.Ok, "An inactive keep-warm endpoint must not serve traffic.");
        }

        [Test]
        public async Task ChangedEvent_FiresOnEndpointAndRoleMutations()
        {
            LlmClientRegistry registry = BuildRegistry(new MemoryStore(), new FakeFactory());
            int changes = 0;
            registry.Changed += () => changes++;

            await registry.AddOrUpdateEndpointAsync(Descriptor("cloud", "https://example.test/v1"));
            int afterAdd = changes;
            registry.AssignRoleProfile("Chat", "cloud");
            int afterAssign = changes;
            await registry.RemoveEndpointAsync("cloud");

            Assert.GreaterOrEqual(afterAdd, 1, "Adding an endpoint must raise Changed.");
            Assert.Greater(afterAssign, afterAdd, "Assigning a role profile must raise Changed.");
            Assert.Greater(changes, afterAssign, "Removing an endpoint must raise Changed.");
        }

        private sealed class FailingThenHealthyClient : ILlmClient
        {
            public LlmErrorCode NextErrorCode { get; set; } = LlmErrorCode.AuthExpired;

            public Task<LlmCompletionResult> CompleteAsync(
                LlmCompletionRequest request,
                CancellationToken cancellationToken = default)
            {
                return Task.FromResult(NextErrorCode == LlmErrorCode.None
                    ? new LlmCompletionResult { Ok = true, Content = "ok" }
                    : new LlmCompletionResult
                    {
                        Ok = false,
                        Error = "401 Unauthorized",
                        ErrorCode = NextErrorCode
                    });
            }
        }

        [Test]
        public async Task EndpointFailureMidConversation_SurfacesDegradedHealthAndRecovers()
        {
            FailingThenHealthyClient client = new();
            FakeFactory factory = new() { Client = client };
            LlmClientRegistry registry = BuildRegistry(new MemoryStore(), factory);
            await registry.AddOrUpdateEndpointAsync(Descriptor("cloud", "https://example.test/v1"));
            registry.AssignRoleProfile("Chat", "cloud");
            RoutingLlmClient routing = new(registry);
            int changedEvents = 0;
            registry.Changed += () => changedEvents++;

            LlmCompletionResult failed = await routing.CompleteAsync(
                new LlmCompletionRequest { AgentRoleId = "Chat" });
            Assert.IsFalse(failed.Ok);
            LlmEndpointSnapshot degraded = registry.GetEndpoints().Single();
            Assert.AreEqual(LlmEndpointLifecycleState.Ready, degraded.State,
                "The endpoint must stay routable — degradation is a health note, not a hard demotion.");
            StringAssert.Contains("Degraded", degraded.Error,
                "An auth failure mid-conversation must be visible on the endpoint snapshot.");
            StringAssert.Contains("AuthExpired", degraded.Error);
            Assert.GreaterOrEqual(changedEvents, 1, "Health degradation must raise Changed for the UI.");

            client.NextErrorCode = LlmErrorCode.None;
            LlmCompletionResult recovered = await routing.CompleteAsync(
                new LlmCompletionRequest { AgentRoleId = "Chat" });
            Assert.IsTrue(recovered.Ok);
            Assert.AreEqual("", registry.GetEndpoints().Single().Error,
                "A successful request must clear the degraded health note.");
        }

        [Test]
        public async Task StaleGenerationHealthReport_DoesNotTouchTheReplacementEndpoint()
        {
            FakeFactory factory = new() { Client = new NamedClient("ok") };
            LlmClientRegistry registry = BuildRegistry(new MemoryStore(), factory);
            LlmEndpointDescriptor descriptor = Descriptor("cloud", "https://example.test/v1");
            await registry.AddOrUpdateEndpointAsync(descriptor);
            registry.AssignRoleProfile("Chat", "cloud");
            long staleGeneration = registry.ResolveRouteForRole("Chat", "").Generation;
            Assert.Greater(staleGeneration, 0L, "A routed runtime endpoint must expose its generation.");

            descriptor.Model = "replacement-model";
            await registry.AddOrUpdateEndpointAsync(descriptor);
            long currentGeneration = registry.ResolveRouteForRole("Chat", "").Generation;
            Assert.AreNotEqual(staleGeneration, currentGeneration, "Replacement must advance the generation.");

            registry.ReportRouteFailure("cloud", staleGeneration, LlmErrorCode.AuthExpired,
                "401 from the old generation");
            Assert.AreEqual("", registry.GetEndpoints().Single().Error,
                "A late failure from the replaced generation must not degrade its successor.");

            registry.ReportRouteFailure("cloud", currentGeneration, LlmErrorCode.AuthExpired, "401");
            StringAssert.Contains("Degraded", registry.GetEndpoints().Single().Error,
                "A failure from the serving generation must still degrade health.");
            registry.ReportRouteFailure("cloud", staleGeneration, LlmErrorCode.None, "");
            StringAssert.Contains("Degraded", registry.GetEndpoints().Single().Error,
                "A late success from the replaced generation must not clear the successor's degradation.");
        }

        private LlmClientRegistry BuildRegistry(
            ILlmEndpointRegistryStore store,
            FakeFactory factory,
            ILlmEndpointSecretProvider secretProvider = null)
        {
            return new LlmClientRegistry(
                GameLoggerUnscopedFallback.Instance,
                _settings,
                null,
                store,
                factory,
                secretProvider);
        }

        private static LlmEndpointDescriptor Descriptor(string id, string url)
        {
            return new LlmEndpointDescriptor
            {
                EndpointId = id,
                DisplayName = id,
                Kind = LlmEndpointKind.HttpOpenAi,
                BaseUrl = url,
                Model = "test",
                Active = true,
                ContextWindowTokens = 4096
            };
        }
    }
}
