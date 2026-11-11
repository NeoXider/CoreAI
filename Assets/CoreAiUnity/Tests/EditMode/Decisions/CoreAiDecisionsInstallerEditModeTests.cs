using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Ai;
using CoreAI.Decisions;
using NUnit.Framework;
using VContainer;

namespace CoreAI.Tests.EditMode
{
    /// <summary>
    /// The decision block's composition: every service registers and resolves, a host registration made first wins
    /// (the replace-with-fake proof), the local preset routes every consumer to a System One sidecar, and the chat
    /// registry's endpoints are routed only when they declare a decision protocol.
    /// </summary>
    public sealed class CoreAiDecisionsInstallerEditModeTests
    {
        [Test]
        public void Installer_RegistersEveryBlock()
        {
            ContainerBuilder builder = new();

            builder.RegisterCoreAiDecisions(CoreAiDecisionSettings.LocalLlamaServer());
            using IObjectResolver container = builder.Build();

            Assert.IsNotNull(container.Resolve<IDecisionClock>());
            Assert.IsNotNull(container.Resolve<IDecisionEndpointSource>());
            Assert.IsNotNull(container.Resolve<IDecisionRouter>());
            Assert.IsNotNull(container.Resolve<IDecisionClientFactory>());
            Assert.IsNotNull(container.Resolve<IDecisionHttpGet>());
            Assert.IsNotNull(container.Resolve<IDecisionCapabilityProbe>());
            Assert.IsNotNull(container.Resolve<IDecisionClientProvider>());
            Assert.IsInstanceOf<InMemoryDecisionTraceSink>(container.Resolve<IDecisionTraceSink>());
            Assert.AreSame(container.Resolve<IDecisionRouter>(), container.Resolve<DecisionRouter>());
#if COREAI_LLM
            Assert.IsInstanceOf<DecisionClientFactory>(container.Resolve<IDecisionClientFactory>());
#else
            Assert.IsInstanceOf<UnavailableDecisionClientFactory>(container.Resolve<IDecisionClientFactory>());
#endif
        }

        [Test]
        public void LocalPreset_RoutesEveryConsumerToTheSystemOneSidecar()
        {
            ContainerBuilder builder = new();
            builder.RegisterCoreAiDecisions(CoreAiDecisionSettings.LocalLlamaServer());
            using IObjectResolver container = builder.Build();

            DecisionRouteResolution npc = container.Resolve<IDecisionRouter>().Resolve(DecisionConsumers.NpcBrain);
            DecisionRouteResolution autopilot = container.Resolve<IDecisionRouter>().Resolve(DecisionConsumers.Autopilot);

            Assert.IsTrue(npc.Ok, npc.Message);
            Assert.IsTrue(autopilot.Ok, autopilot.Message);
            Assert.AreEqual(CoreAiDecisionSettings.LocalEndpointId, npc.Endpoint.EndpointId);
            Assert.AreEqual(LlmDecisionProtocol.SystemOne, npc.Endpoint.DecisionProtocol);
            Assert.AreEqual("http://127.0.0.1:8080/v1/systemone", SystemOneWire.BuildUrl(npc.Endpoint.BaseUrl));
        }

        [Test]
        public void NoSettings_RefusesEveryConsumerWithNoRoute()
        {
            ContainerBuilder builder = new();
            builder.RegisterCoreAiDecisions();
            using IObjectResolver container = builder.Build();

            Assert.AreEqual(DecisionErrorReasons.NoRoute,
                container.Resolve<IDecisionRouter>().Resolve(DecisionConsumers.NpcBrain).Reason);
        }

        [Test]
        public async Task HostRegistrationsMadeFirst_Win_AndTheProviderUsesThem()
        {
            FakeDecisionClient fake = new();
            FakeFactory factory = new(fake);
            ContainerBuilder builder = new();
            builder.RegisterInstance<IDecisionClientFactory>(factory);
            builder.RegisterInstance<IDecisionTraceSink>(new NullDecisionTraceSink());

            builder.RegisterCoreAiDecisions(CoreAiDecisionSettings.LocalLlamaServer());
            using IObjectResolver container = builder.Build();
            DecisionResult result = await container.Resolve<IDecisionClientProvider>()
                .ForConsumer(DecisionConsumers.NpcBrain)
                .DecideAsync(new DecisionRequest("s", DecisionQuestion.Choice("action", "pick", "attack", "flee")));

            Assert.AreSame(factory, container.Resolve<IDecisionClientFactory>());
            Assert.IsInstanceOf<NullDecisionTraceSink>(container.Resolve<IDecisionTraceSink>());
            Assert.IsTrue(result.Ok, result.Error);
            Assert.AreEqual(1, fake.Calls);
            Assert.AreEqual(CoreAiDecisionSettings.LocalEndpointId, factory.Created[0].EndpointId);
        }

        [Test]
        public void RegistryEndpoint_WithTheProtocol_Routes_AndTheChatOneIsRefused()
        {
            FakeRegistry registry = new();
            LlmEndpointDescriptor chat = new()
            {
                EndpointId = "chat", Kind = LlmEndpointKind.HttpOpenAi, BaseUrl = "https://openrouter.ai/api/v1",
                Model = "openai/gpt-4o-mini"
            };
            LlmEndpointDescriptor jev = new()
            {
                EndpointId = "jev", Kind = LlmEndpointKind.HttpOpenAi, BaseUrl = "https://openrouter.ai/api/v1",
                Model = "typesafe/jev-1.14", DecisionProtocol = LlmDecisionProtocol.OpenRouter
            };
            registry.Snapshots.Add(new LlmEndpointSnapshot { Descriptor = chat, State = LlmEndpointLifecycleState.Ready });
            registry.Snapshots.Add(new LlmEndpointSnapshot { Descriptor = jev, State = LlmEndpointLifecycleState.Ready });
            CoreAiDecisionSettings settings = new()
            {
                Routes = new DecisionRouteTable
                {
                    Profiles = new List<DecisionRouteProfile>
                    {
                        new() { ConsumerPattern = DecisionConsumers.NpcBrain, EndpointId = "jev" },
                        new() { ConsumerPattern = DecisionConsumers.Autopilot, EndpointId = "chat" }
                    }
                }
            };
            ContainerBuilder builder = new();
            builder.RegisterInstance<ILlmEndpointRegistry>(registry);

            builder.RegisterCoreAiDecisions(settings);
            using IObjectResolver container = builder.Build();
            IDecisionRouter router = container.Resolve<IDecisionRouter>();

            Assert.IsTrue(router.Resolve(DecisionConsumers.NpcBrain).Ok);
            Assert.AreEqual(DecisionErrorReasons.CapabilityMissing, router.Resolve(DecisionConsumers.Autopilot).Reason);
        }

        [Test]
        public void IncludeRegistryEndpointsOff_IgnoresTheRegistry()
        {
            FakeRegistry registry = new();
            registry.Snapshots.Add(new LlmEndpointSnapshot
            {
                Descriptor = new LlmEndpointDescriptor
                {
                    EndpointId = "jev", Kind = LlmEndpointKind.HttpOpenAi, BaseUrl = "https://openrouter.ai/api/v1",
                    Model = "typesafe/jev-1.14", DecisionProtocol = LlmDecisionProtocol.OpenRouter
                },
                State = LlmEndpointLifecycleState.Ready
            });
            ContainerBuilder builder = new();
            builder.RegisterInstance<ILlmEndpointRegistry>(registry);

            builder.RegisterCoreAiDecisions(new CoreAiDecisionSettings
            {
                Routes = DecisionRouteTable.AllTo("jev"), IncludeRegistryEndpoints = false
            });
            using IObjectResolver container = builder.Build();

            Assert.AreEqual(DecisionErrorReasons.EndpointNotFound,
                container.Resolve<IDecisionRouter>().Resolve(DecisionConsumers.NpcBrain).Reason);
        }

        private sealed class FakeDecisionClient : IDecisionClient
        {
            public int Calls { get; private set; }

            public Task<DecisionResult> DecideAsync(DecisionRequest request, CancellationToken cancellationToken = default)
            {
                Calls++;
                DecisionQuestion question = request.Questions[0];
                DecisionAnswer answer = new(question.Id, question.Kind, new[] { "attack", "flee" }, new[] { 0.2, 0.8 }, 1,
                    0.8, true);
                return Task.FromResult(DecisionResult.Success(new[] { answer }, new LlmUsageRecord(), "fake", "fake"));
            }
        }

        private sealed class FakeFactory : IDecisionClientFactory
        {
            private readonly IDecisionClient _client;

            public FakeFactory(IDecisionClient client)
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

        private sealed class FakeRegistry : ILlmEndpointRegistry
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

            public bool RemoveProfile(string profileId, string replacementProfileId = null) =>
                throw new NotSupportedException();

            public void AssignRoleProfile(string rolePattern, string profileId, int sortOrder = 0) =>
                throw new NotSupportedException();

            public bool ClearRoleProfile(string rolePattern) => throw new NotSupportedException();

            public string GetRoleProfile(string roleId) => "";
        }
    }
}
