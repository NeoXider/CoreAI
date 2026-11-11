using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Ai;
using CoreAI.Decisions;
using NUnit.Framework;

namespace CoreAI.Core.Tests.EditMode.Decisions
{
    /// <summary>
    /// Routing consumers to decision endpoints: the capability gate with named refusals, consumer profiles, the
    /// registry adapter, and the provider that composes routing, budgets and replaceable backends.
    /// </summary>
    [TestFixture]
    public sealed class DecisionRoutingEditModeTests
    {
        [Test]
        public void EndpointWithoutTheCapability_IsRefused_WithANamedReason()
        {
            DecisionRouter router = Router(DecisionRouteTable.AllTo("chat"),
                DecisionFixtures.Endpoint("chat", LlmDecisionProtocol.None));

            DecisionRouteResolution route = router.Resolve(DecisionConsumers.NpcBrain);

            Assert.IsFalse(route.Ok);
            Assert.AreEqual(LlmErrorCode.RoutingError, route.ErrorCode);
            Assert.AreEqual(DecisionErrorReasons.CapabilityMissing, route.Reason);
            StringAssert.Contains("chat", route.Message);
        }

        [Test]
        public void EndpointWithTheCapability_Routes()
        {
            DecisionRouter router = Router(DecisionRouteTable.AllTo("local"),
                DecisionFixtures.Endpoint("local", LlmDecisionProtocol.SystemOne));

            DecisionRouteResolution route = router.Resolve(DecisionConsumers.NpcBrain);

            Assert.IsTrue(route.Ok, route.Message);
            Assert.AreEqual("local", route.Endpoint.EndpointId);
        }

        [Test]
        public void NoMatchingProfile_IsNoRoute()
        {
            DecisionRouteTable table = new()
            {
                Profiles = new List<DecisionRouteProfile> { new() { ConsumerPattern = "npc-*", EndpointId = "local" } }
            };

            DecisionRouteResolution route = Router(table, DecisionFixtures.Endpoint("local", LlmDecisionProtocol.SystemOne))
                .Resolve(DecisionConsumers.Autopilot);

            Assert.AreEqual(DecisionErrorReasons.NoRoute, route.Reason);
        }

        [Test]
        public void UnknownEndpoint_IsEndpointNotFound()
        {
            Assert.AreEqual(DecisionErrorReasons.EndpointNotFound,
                Router(DecisionRouteTable.AllTo("missing")).Resolve("x").Reason);
        }

        [Test]
        public void InactiveEndpoint_IsRefused()
        {
            LlmEndpointDescriptor endpoint = DecisionFixtures.Endpoint("local", LlmDecisionProtocol.SystemOne);
            endpoint.Active = false;

            Assert.AreEqual(DecisionErrorReasons.EndpointInactive,
                Router(DecisionRouteTable.AllTo("local"), endpoint).Resolve("x").Reason);
        }

        [Test]
        public void LlmUnityEndpoint_IsRefusedAsInvalid_DecisionsAreHttp()
        {
            LlmEndpointDescriptor endpoint = DecisionFixtures.Endpoint("local", LlmDecisionProtocol.SystemOne, baseUrl: "");
            endpoint.Kind = LlmEndpointKind.LlmUnity;

            Assert.AreEqual(DecisionErrorReasons.EndpointInvalid,
                Router(DecisionRouteTable.AllTo("local"), endpoint).Resolve("x").Reason);
        }

        [Test]
        public void ConsumerProfiles_ExactBeatsPrefixBeatsWildcard()
        {
            DecisionRouteTable table = new()
            {
                Profiles = new List<DecisionRouteProfile>
                {
                    new() { ConsumerPattern = "*", EndpointId = "remote" },
                    new() { ConsumerPattern = "npc-*", EndpointId = "chat-logprobs" },
                    new() { ConsumerPattern = DecisionConsumers.NpcBrain, EndpointId = "local" }
                }
            };
            DecisionRouter router = Router(table,
                DecisionFixtures.Endpoint("local", LlmDecisionProtocol.SystemOne),
                DecisionFixtures.Endpoint("chat-logprobs", LlmDecisionProtocol.ChatLogprobs),
                DecisionFixtures.Endpoint("remote", LlmDecisionProtocol.OpenRouter));

            Assert.AreEqual("local", router.Resolve(DecisionConsumers.NpcBrain).Endpoint.EndpointId);
            Assert.AreEqual("chat-logprobs", router.Resolve("npc-merchant").Endpoint.EndpointId);
            Assert.AreEqual("remote", router.Resolve(DecisionConsumers.Autopilot).Endpoint.EndpointId);
        }

        [Test]
        public void SortOrder_ComesBeforeSpecificity()
        {
            DecisionRouteTable table = new()
            {
                Profiles = new List<DecisionRouteProfile>
                {
                    new() { ConsumerPattern = DecisionConsumers.NpcBrain, EndpointId = "local", SortOrder = 10 },
                    new() { ConsumerPattern = "*", EndpointId = "remote", SortOrder = 0 }
                }
            };
            DecisionRouter router = Router(table,
                DecisionFixtures.Endpoint("local", LlmDecisionProtocol.SystemOne),
                DecisionFixtures.Endpoint("remote", LlmDecisionProtocol.OpenRouter));

            Assert.AreEqual("remote", router.Resolve(DecisionConsumers.NpcBrain).Endpoint.EndpointId);
        }

        [Test]
        public void ModelListThatRefutesTheProtocol_IsCapabilityNotAdvertised()
        {
            DecisionRouter router = Router(DecisionRouteTable.AllTo("local"),
                DecisionFixtures.Endpoint("local", LlmDecisionProtocol.SystemOne));

            router.RecordCapability(new DecisionCapabilityReport
            {
                EndpointId = "local", Reachable = true, ModelListed = true, AdvertisesDecisions = false
            });

            Assert.AreEqual(DecisionErrorReasons.CapabilityNotAdvertised, router.Resolve("x").Reason);
        }

        [Test]
        public void UnreachableModelList_ProvesNothing_AndStillRoutes()
        {
            DecisionRouter router = Router(DecisionRouteTable.AllTo("local"),
                DecisionFixtures.Endpoint("local", LlmDecisionProtocol.SystemOne));

            router.RecordCapability(new DecisionCapabilityReport { EndpointId = "local", Reachable = false });

            Assert.IsTrue(router.Resolve("x").Ok);
        }

        [Test]
        public void ReplaceTable_AppliesToTheNextResolve()
        {
            DecisionRouter router = Router(DecisionRouteTable.AllTo("a"),
                DecisionFixtures.Endpoint("a", LlmDecisionProtocol.SystemOne),
                DecisionFixtures.Endpoint("b", LlmDecisionProtocol.SystemOne));

            router.ReplaceTable(DecisionRouteTable.AllTo("b"));

            Assert.AreEqual("b", router.Resolve("x").Endpoint.EndpointId);
        }

        [Test]
        public void RegistrySource_ListsHostEndpointsFirst_AndSkipsRemoved()
        {
            FakeEndpointRegistry registry = new();
            registry.Snapshots.Add(new LlmEndpointSnapshot
            {
                Descriptor = DecisionFixtures.Endpoint("shared", LlmDecisionProtocol.None),
                State = LlmEndpointLifecycleState.Ready
            });
            registry.Snapshots.Add(new LlmEndpointSnapshot
            {
                Descriptor = DecisionFixtures.Endpoint("gone", LlmDecisionProtocol.SystemOne),
                State = LlmEndpointLifecycleState.Removed
            });
            StaticDecisionEndpointSource sidecar = new(new[]
            {
                DecisionFixtures.Endpoint("shared", LlmDecisionProtocol.SystemOne)
            });
            DecisionRouter router = new(DecisionRouteTable.AllTo("shared"),
                new RegistryDecisionEndpointSource(registry, sidecar));

            Assert.IsTrue(router.Resolve("x").Ok, "the host's sidecar entry wins over the chat registry's");
            router.ReplaceTable(DecisionRouteTable.AllTo("gone"));
            Assert.AreEqual(DecisionErrorReasons.EndpointNotFound, router.Resolve("x").Reason);
        }

        [Test]
        public void RegistrySource_ChatEndpointWithoutTheCapability_IsRefused()
        {
            FakeEndpointRegistry registry = new();
            registry.Snapshots.Add(new LlmEndpointSnapshot
            {
                Descriptor = DecisionFixtures.Endpoint("chat", LlmDecisionProtocol.None),
                State = LlmEndpointLifecycleState.Ready
            });
            DecisionRouter router = new(DecisionRouteTable.AllTo("chat"), new RegistryDecisionEndpointSource(registry));

            Assert.AreEqual(DecisionErrorReasons.CapabilityMissing, router.Resolve("x").Reason);
        }

        [Test]
        public async Task Provider_WithAFakeFactory_RoutesTheConsumerToTheFakeBackend()
        {
            ScriptedDecisionClient fake = new();
            RecordingFactory factory = new(fake);
            DecisionClientProvider provider = new(
                Router(DecisionRouteTable.AllTo("local"), DecisionFixtures.Endpoint("local", LlmDecisionProtocol.SystemOne)),
                factory, new ManualDecisionClock());

            DecisionResult result = await provider.ForConsumer(DecisionConsumers.NpcBrain)
                .DecideAsync(new DecisionRequest("s", DecisionQuestion.Choice("q", "", "a", "b")));

            Assert.IsTrue(result.Ok);
            Assert.AreEqual(1, fake.Calls);
            Assert.AreEqual(DecisionConsumers.NpcBrain, result.Usage.RoleId);
            Assert.AreEqual(1, factory.Created.Count);
        }

        [Test]
        public async Task Provider_RoutingRefusal_NeverBuildsABackend()
        {
            RecordingFactory factory = new(new ScriptedDecisionClient());
            DecisionClientProvider provider = new(
                Router(DecisionRouteTable.AllTo("chat"), DecisionFixtures.Endpoint("chat", LlmDecisionProtocol.None)),
                factory, new ManualDecisionClock());

            DecisionResult result = await provider.ForConsumer("x")
                .DecideAsync(new DecisionRequest("s", DecisionQuestion.YesNo("q", "a")));

            Assert.AreEqual(LlmErrorCode.RoutingError, result.ErrorCode);
            Assert.AreEqual(DecisionErrorReasons.CapabilityMissing, result.ErrorReason);
            Assert.AreEqual(0, factory.Created.Count);
        }

        [Test]
        public async Task Provider_ReusesTheBackend_UntilTheDescriptorChanges()
        {
            LlmEndpointDescriptor endpoint = DecisionFixtures.Endpoint("local", LlmDecisionProtocol.SystemOne);
            RecordingFactory factory = new(new ScriptedDecisionClient());
            DecisionClientProvider provider = new(Router(DecisionRouteTable.AllTo("local"), endpoint), factory,
                new ManualDecisionClock());
            IDecisionClient client = provider.ForConsumer("x");
            DecisionRequest request = new("s", DecisionQuestion.YesNo("q", "a"));

            await client.DecideAsync(request);
            await client.DecideAsync(request);
            endpoint.Model = "d1-2B-Q8_0";
            await client.DecideAsync(request);

            Assert.AreEqual(2, factory.Created.Count);
            Assert.AreSame(client, provider.ForConsumer("x"));
        }

        [Test]
        public async Task Provider_AppliesTheRouteBudget_PerConsumer()
        {
            DecisionRouteTable table = DecisionRouteTable.AllTo("local",
                new DecisionBudget { MaxCallsPerSecond = 1, MaxInFlight = 0, DeadlineMilliseconds = 0 });
            DecisionClientProvider provider = new(
                Router(table, DecisionFixtures.Endpoint("local", LlmDecisionProtocol.SystemOne)),
                new RecordingFactory(new ScriptedDecisionClient()), new ManualDecisionClock());
            DecisionRequest request = new("s", DecisionQuestion.YesNo("q", "a"));

            DecisionResult npcFirst = await provider.ForConsumer("npc-1").DecideAsync(request);
            DecisionResult npcSecond = await provider.ForConsumer("npc-1").DecideAsync(request);
            DecisionResult otherNpc = await provider.ForConsumer("npc-2").DecideAsync(request);

            Assert.IsTrue(npcFirst.Ok);
            Assert.AreEqual(DecisionErrorReasons.RateBudgetExceeded, npcSecond.ErrorReason);
            Assert.IsTrue(otherNpc.Ok, "each consumer has its own counters");
        }

#if COREAI_LLM
        [TestCase(LlmDecisionProtocol.SystemOne, typeof(SystemOneHttpDecisionClient))]
        [TestCase(LlmDecisionProtocol.OpenRouter, typeof(OpenRouterDecisionClient))]
        [TestCase(LlmDecisionProtocol.ChatLogprobs, typeof(LogprobDecisionClient))]
        [TestCase(LlmDecisionProtocol.None, typeof(UnavailableDecisionClient))]
        public void DefaultFactory_PicksTheBackendFromTheProtocol(LlmDecisionProtocol protocol, Type expected)
        {
            DecisionClientFactory factory = new(new RecordingTransport());

            Assert.IsInstanceOf(expected, factory.Create(DecisionFixtures.Endpoint("e", protocol)));
        }
#endif

        private static DecisionRouter Router(DecisionRouteTable table, params LlmEndpointDescriptor[] endpoints)
        {
            return new DecisionRouter(table, new StaticDecisionEndpointSource(endpoints));
        }

        private sealed class RecordingFactory : IDecisionClientFactory
        {
            private readonly IDecisionClient _client;

            public RecordingFactory(IDecisionClient client)
            {
                _client = client;
            }

            public List<LlmEndpointDescriptor> Created { get; } = new();

            public IDecisionClient Create(LlmEndpointDescriptor endpoint)
            {
                Created.Add(endpoint);
                return _client;
            }
        }

        internal sealed class FakeEndpointRegistry : ILlmEndpointRegistry
        {
            public event Action Changed
            {
                add { }
                remove { }
            }

            public List<LlmEndpointSnapshot> Snapshots { get; } = new();

            public IReadOnlyList<LlmEndpointSnapshot> GetEndpoints() => Snapshots;

            public IReadOnlyList<LlmRuntimeProfile> GetProfiles() => Array.Empty<LlmRuntimeProfile>();

            public Task<LlmEndpointSnapshot> AddOrUpdateEndpointAsync(LlmEndpointDescriptor descriptor,
                string sessionApiKey = null, CancellationToken cancellationToken = default)
                => throw new NotSupportedException();

            public Task<LlmEndpointSnapshot> SetEndpointActiveAsync(string endpointId, bool active,
                bool keepWarm = false, CancellationToken cancellationToken = default)
                => throw new NotSupportedException();

            public Task<bool> RemoveEndpointAsync(string endpointId,
                LlmEndpointRemovalMode mode = LlmEndpointRemovalMode.Drain, string replacementEndpointId = null,
                CancellationToken cancellationToken = default)
                => throw new NotSupportedException();

            public void AddOrUpdateProfile(LlmRuntimeProfile profile) => throw new NotSupportedException();

            public bool RemoveProfile(string profileId, string replacementProfileId = null) => throw new NotSupportedException();

            public void AssignRoleProfile(string rolePattern, string profileId, int sortOrder = 0) =>
                throw new NotSupportedException();

            public bool ClearRoleProfile(string rolePattern) => throw new NotSupportedException();

            public string GetRoleProfile(string roleId) => "";
        }
    }
}
