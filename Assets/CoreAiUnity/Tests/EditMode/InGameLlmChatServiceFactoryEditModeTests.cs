using System;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Ai;
using CoreAI.Authority;
using CoreAI.Composition;
using CoreAI.Presentation.PlayerChat;
using NUnit.Framework;
using UnityEngine;
using VContainer;

namespace CoreAI.Tests.EditMode
{
    /// <summary>
    /// Covers actor isolation and default resolution for in-game chat services.
    /// </summary>
    public sealed class InGameLlmChatServiceFactoryEditModeTests
    {
        [Test]
        public async Task Resolve_TwoActors_HaveIndependentHistories()
        {
            StubLlmClient llm = new StubLlmClient();
            ActorKeyedInGameLlmChatServiceFactory factory = CreateFactory(llm);
            IInGameLlmChatService actorA = factory.Resolve(CreateActor("actor-a"));
            IInGameLlmChatService actorB = factory.Resolve(CreateActor("actor-b"));

            await actorA.SendPlayerMessageAsync("message-a");

            Assert.AreEqual(1, actorA.HistoryPairCount);
            Assert.AreEqual(0, actorB.HistoryPairCount);

            await actorB.SendPlayerMessageAsync("message-b");

            Assert.AreEqual(1, actorA.HistoryPairCount);
            Assert.AreEqual(1, actorB.HistoryPairCount);
        }

        [Test]
        public async Task ClearHistory_ActorA_LeavesActorBHistoryIntact()
        {
            StubLlmClient llm = new StubLlmClient();
            ActorKeyedInGameLlmChatServiceFactory factory = CreateFactory(llm);
            IInGameLlmChatService actorA = factory.Resolve(CreateActor("actor-a"));
            IInGameLlmChatService actorB = factory.Resolve(CreateActor("actor-b"));
            await actorA.SendPlayerMessageAsync("message-a");
            await actorB.SendPlayerMessageAsync("message-b");

            actorA.ClearHistory();

            Assert.AreEqual(0, actorA.HistoryPairCount);
            Assert.AreEqual(1, actorB.HistoryPairCount);
        }

        [Test]
        public async Task RateLimit_ActorAIsThrottled_ActorBRemainsAvailable()
        {
            StubLlmClient llm = new StubLlmClient();
            ActorKeyedInGameLlmChatServiceFactory factory = CreateFactory(llm, 1);
            IInGameLlmChatService actorA = factory.Resolve(CreateActor("actor-a"));
            IInGameLlmChatService actorB = factory.Resolve(CreateActor("actor-b"));

            LlmCompletionResult firstA = await actorA.SendPlayerMessageAsync("first-a");
            LlmCompletionResult blockedA = await actorA.SendPlayerMessageAsync("second-a");
            LlmCompletionResult firstB = await actorB.SendPlayerMessageAsync("first-b");

            Assert.IsTrue(firstA.Ok);
            Assert.IsFalse(blockedA.Ok);
            StringAssert.StartsWith("rate_limited", blockedA.Error);
            Assert.IsTrue(firstB.Ok);
            Assert.AreEqual(2, llm.CallCount);
        }

        [Test]
        public void InGameChatPanel_ReconnectOldDeparture_DoesNotEvictLiveActorAndFinalDepartureFreesCapacity()
        {
            StubLlmClient llm = new StubLlmClient();
            ActorKeyedInGameLlmChatServiceFactory factory = CreateFactory(llm, 10, 1);
            LocalActorIdentityProvider oldIdentity = CreateIdentity("actor-a", "session-old");
            LocalActorIdentityProvider newIdentity = CreateIdentity("actor-a", "session-new");
            ActorContext oldActor = oldIdentity.GetActorContext(BuiltInAgentRoleIds.SmartChat);
            ActorContext actorB = CreateIdentity("actor-b", "session-b")
                .GetActorContext(BuiltInAgentRoleIds.SmartChat);
            using IObjectResolver oldResolver = CreatePanelResolver(factory, oldIdentity);
            using IObjectResolver newResolver = CreatePanelResolver(factory, newIdentity);
            GameObject oldPanelObject = new GameObject("old-session-chat-panel");
            GameObject newPanelObject = new GameObject("new-session-chat-panel");

            try
            {
                InGameChatPanel oldPanel = oldPanelObject.AddComponent<InGameChatPanel>();
                InGameChatPanel newPanel = newPanelObject.AddComponent<InGameChatPanel>();
                oldPanel.ResolveCurrentActorChat(oldResolver);
                newPanel.ResolveCurrentActorChat(newResolver);

                Assert.AreSame(factory.Resolve(oldActor), oldPanel.BoundChatService);
                Assert.AreSame(oldPanel.BoundChatService, newPanel.BoundChatService);

                oldPanel.ReleaseCurrentActorChat();
                UnityEngine.Object.DestroyImmediate(oldPanelObject);
                oldPanelObject = null;

                Assert.Throws<InvalidOperationException>(() => factory.Resolve(actorB));

                newPanel.ReleaseCurrentActorChat();
                UnityEngine.Object.DestroyImmediate(newPanelObject);
                newPanelObject = null;

                Assert.IsNotNull(factory.Resolve(actorB));
            }
            finally
            {
                if (oldPanelObject != null)
                {
                    UnityEngine.Object.DestroyImmediate(oldPanelObject);
                }

                if (newPanelObject != null)
                {
                    UnityEngine.Object.DestroyImmediate(newPanelObject);
                }

                factory.Dispose();
            }
        }

        [Test]
        public void RegisterCorePortable_DefaultSinglePlayerServiceResolvesFromFactory()
        {
            ContainerBuilder builder = new ContainerBuilder();
            builder.RegisterInstance<ILlmClient>(new StubLlmClient());
            builder.RegisterInstance<IAgentSystemPromptProvider>(new StubPromptProvider());
            builder.RegisterCorePortable();

            using IObjectResolver container = builder.Build();
            IInGameLlmChatService defaultService = container.Resolve<IInGameLlmChatService>();
            IInGameLlmChatServiceFactory factory = container.Resolve<IInGameLlmChatServiceFactory>();
            ActorContext defaultActor = container.Resolve<IActorIdentityProvider>()
                .GetActorContext(BuiltInAgentRoleIds.SmartChat);

            Assert.IsNotNull(defaultService);
            Assert.AreSame(defaultService, factory.Resolve(defaultActor));
            Assert.AreSame(defaultService, container.Resolve<IInGameLlmChatService>());
            Assert.AreEqual(LocalActorIdentityProvider.DefaultActorId, defaultActor.ActorId);
            Assert.IsTrue(defaultActor.Grants.IsUnrestricted);
        }

        private static ActorKeyedInGameLlmChatServiceFactory CreateFactory(
            StubLlmClient llm,
            int maxRequestsPerWindow = 10,
            int maxActorInstances = ActorKeyedInGameLlmChatServiceFactory.DefaultMaxActorInstances)
        {
            StubPromptProvider prompts = new StubPromptProvider();
            return new ActorKeyedInGameLlmChatServiceFactory(
                () => new InGameLlmChatService(llm, prompts, 24, maxRequestsPerWindow, 60),
                maxActorInstances);
        }

        private static ActorContext CreateActor(string actorId)
        {
            LocalActorIdentityProvider provider = new LocalActorIdentityProvider(actorId);
            return provider.GetActorContext(BuiltInAgentRoleIds.SmartChat);
        }

        private static LocalActorIdentityProvider CreateIdentity(string actorId, string sessionId)
        {
            return new LocalActorIdentityProvider(
                actorId,
                sessionId,
                "",
                ActorGrantSet.None,
                AgentMemoryScope.Empty);
        }

        private static IObjectResolver CreatePanelResolver(
            IInGameLlmChatServiceFactory factory,
            IActorIdentityProvider identityProvider)
        {
            ContainerBuilder builder = new ContainerBuilder();
            builder.RegisterInstance(factory);
            builder.RegisterInstance(identityProvider);
            return builder.Build();
        }

        private sealed class StubLlmClient : ILlmClient
        {
            public int CallCount { get; private set; }

            public Task<LlmCompletionResult> CompleteAsync(
                LlmCompletionRequest request,
                CancellationToken cancellationToken = default)
            {
                CallCount++;
                return Task.FromResult(new LlmCompletionResult { Ok = true, Content = "reply" });
            }
        }

        private sealed class StubPromptProvider : IAgentSystemPromptProvider
        {
            public bool TryGetSystemPrompt(string roleId, out string prompt)
            {
                prompt = "system";
                return true;
            }
        }
    }
}
