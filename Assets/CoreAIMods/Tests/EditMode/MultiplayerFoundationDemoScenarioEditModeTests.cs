#if COREAI_LUA
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Ai;
using CoreAI.Authority;
using CoreAI.Composition;
using CoreAI.Demos;
using CoreAI.Infrastructure.Logging;
using CoreAI.Logging;
using CoreAI.Messaging;
using NUnit.Framework;
using VContainer;

namespace CoreAI.Tests.EditMode
{
    /// <summary>Production-composition regression proof for the MVP2 multiplayer-foundation demo.</summary>
    public sealed class MultiplayerFoundationDemoScenarioEditModeTests
    {
        /// <summary>Resolves both demo services through their real installers and proves every refusal.</summary>
        [Test]
        public void ProductionInstallers_FourActors_AllIsolationClaimsHold()
        {
            SynchronizationContext savedContext = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(null);
            try
            {
                ContainerBuilder builder = new ContainerBuilder();
                builder.RegisterInstance<IGameLogger>(new SilentGameLogger());
                builder.RegisterInstance<ILog>(NullLog.Instance);
                builder.RegisterInstance<IAiGameCommandSink>(new NoopCommandSink());
                builder.RegisterInstance<ILlmClient>(new StubLlmClient());
                builder.RegisterInstance<IAgentSystemPromptProvider>(new StubPromptProvider());
                builder.Register(_ => new LuaGenerationRateLimiter(), Lifetime.Singleton);
                builder.RegisterCorePortable();
                builder.RegisterCoreAiMods(
                    modStoreId: "multiplayer-foundation-editmode",
                    applicationIsPlayingProvider: () => false);
                builder.RegisterInstance<ILuaModStore>(new MemoryLuaModStore());
                builder.RegisterInstance<ILuaModSourceStore>(NullLuaModSourceStore.Instance);

                using IObjectResolver container = builder.Build();
                ILuaModRuntime mods = container.Resolve<ILuaModRuntime>();
                IInGameLlmChatServiceFactory chatFactory =
                    container.Resolve<IInGameLlmChatServiceFactory>();
                IActorIdentityProvider identityProvider = container.Resolve<IActorIdentityProvider>();
                ActorContext hostActor = identityProvider.GetActorContext(BuiltInAgentRoleIds.Programmer);
                using MultiplayerFoundationDemoScenario scenario =
                    new MultiplayerFoundationDemoScenario(mods, chatFactory, hostActor, 4);

                MultiplayerFoundationProofReport report = scenario.Run();

                Assert.AreEqual(4, report.Actors.Count);
                Assert.AreEqual(10, report.Proofs.Count);
                Assert.AreEqual(report.Proofs.Count, report.EnforcedProofCount);
                Assert.IsTrue(report.Passed, JoinSetupErrors(report.SetupErrors));

                foreach (MultiplayerFoundationActorState actor in report.Actors)
                {
                    Assert.IsTrue(actor.ChatIsIsolated, actor.ActorId + " shared a chat service.");
                    Assert.IsTrue(actor.OwnModEdited, actor.ActorId + " did not load then reload its own mod.");
                    Assert.IsTrue(actor.ModLoaded, actor.ActorId + " lost its own mod.");
                    Assert.AreEqual(1, actor.TimerCount, actor.ActorId + " beacon must keep one animation timer.");
                    Assert.AreEqual(0, actor.ChatHistoryPairCount);
                }

                for (int actorIndex = 0; actorIndex < report.Actors.Count - 1; actorIndex++)
                {
                    Assert.AreEqual(1, report.Actors[actorIndex].LoadedModCount);
                }

                MultiplayerFoundationActorState quotaActor =
                    report.Actors[report.Actors.Count - 1];
                Assert.AreEqual(MultiplayerFoundationDemoScenario.ProductionModQuota,
                    quotaActor.LoadedModCount);
                Assert.AreEqual(MultiplayerFoundationDemoScenario.ProductionModQuota,
                    quotaActor.ModQuota);

                foreach (MultiplayerFoundationProofResult proof in report.Proofs)
                {
                    Assert.IsTrue(proof.Enforced,
                        proof.Category + " unexpectedly allowed: " + proof.Reason);
                    Assert.IsTrue(proof.TargetIntact,
                        proof.Category + " damaged its target: " + proof.Reason);
                    StringAssert.Contains(proof.RequesterActorId, proof.Reason,
                        proof.Category + " refusal must name the requesting actor.");
                    StringAssert.DoesNotStartWith("BUG:", proof.Reason);
                }

                MultiplayerFoundationProofResult quotaProof =
                    report.Proofs[report.Proofs.Count - 1];
                Assert.AreEqual("PER-ACTOR QUOTA", quotaProof.Category);
                StringAssert.Contains("loaded mods quota reached", quotaProof.Reason);
                StringAssert.Contains(
                    MultiplayerFoundationDemoScenario.ProductionModQuota.ToString(),
                    quotaProof.Reason);
            }
            finally
            {
                SynchronizationContext.SetSynchronizationContext(savedContext);
            }
        }

        private static string JoinSetupErrors(IReadOnlyList<string> errors)
        {
            return errors == null || errors.Count == 0
                ? "Unexpected proof failure."
                : string.Join(" | ", errors);
        }

        private sealed class StubLlmClient : ILlmClient
        {
            public Task<LlmCompletionResult> CompleteAsync(
                LlmCompletionRequest request,
                CancellationToken cancellationToken = default)
            {
                return Task.FromResult(new LlmCompletionResult
                {
                    Ok = true,
                    Content = "actor-private reply"
                });
            }
        }

        private sealed class StubPromptProvider : IAgentSystemPromptProvider
        {
            public bool TryGetSystemPrompt(string roleId, out string systemPrompt)
            {
                systemPrompt = "Actor-private demo prompt.";
                return true;
            }
        }

        private sealed class NoopCommandSink : IAiGameCommandSink
        {
            public void Publish(ApplyAiGameCommand command)
            {
            }
        }

        private sealed class SilentGameLogger : IGameLogger
        {
            public void LogDebug(
                GameLogFeature feature,
                string message,
                UnityEngine.Object context = null)
            {
            }

            public void LogInfo(
                GameLogFeature feature,
                string message,
                UnityEngine.Object context = null)
            {
            }

            public void LogWarning(
                GameLogFeature feature,
                string message,
                UnityEngine.Object context = null)
            {
            }

            public void LogError(
                GameLogFeature feature,
                string message,
                UnityEngine.Object context = null)
            {
            }
        }

        private sealed class MemoryLuaModStore : ILuaModStore
        {
            private readonly Dictionary<string, string> _values = new Dictionary<string, string>();

            public string Get(string modId, string key)
            {
                string value;
                return _values.TryGetValue(StoreKey(modId, key), out value) ? value : "";
            }

            public void Set(string modId, string key, string value)
            {
                string storeKey = StoreKey(modId, key);
                if (value == null)
                {
                    _values.Remove(storeKey);
                    return;
                }

                _values[storeKey] = value;
            }

            public void Clear(string modId)
            {
                string prefix = (modId ?? "") + "\n";
                List<string> matches = new List<string>();
                foreach (string key in _values.Keys)
                {
                    if (key.StartsWith(prefix, StringComparison.Ordinal))
                    {
                        matches.Add(key);
                    }
                }

                foreach (string key in matches)
                {
                    _values.Remove(key);
                }
            }

            private static string StoreKey(string modId, string key)
            {
                return (modId ?? "") + "\n" + (key ?? "");
            }
        }
    }
}
#endif
