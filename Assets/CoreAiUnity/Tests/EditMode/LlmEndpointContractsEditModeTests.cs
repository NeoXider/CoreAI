using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Ai;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode
{
    public sealed class LlmEndpointContractsEditModeTests
    {
        private sealed class CapturingOrchestrator : IAiOrchestrationService
        {
            public AiTaskRequest LastTask { get; private set; }

            public Task<string> RunTaskAsync(
                AiTaskRequest task,
                CancellationToken cancellationToken = default)
            {
                LastTask = task;
                return Task.FromResult("ok");
            }

            public void CancelTasks(string cancellationScope)
            {
            }
        }

        private sealed class LegacyRegistry : ILlmClientRegistry
        {
            private readonly ILlmClient _client = new StubLlmClient();

            public ILlmClient ResolveClientForRole(string roleId)
            {
                return _client;
            }

            public int ResolveContextWindowForRole(string roleId)
            {
                return 4096;
            }

            public LlmExecutionMode ResolveExecutionModeForRole(string roleId)
            {
                return LlmExecutionMode.Offline;
            }

            public string ResolveProfileIdForRole(string roleId)
            {
                return "legacy";
            }
        }

        [TearDown]
        public void TearDown()
        {
            CoreAIAgent.Reset();
        }

        [Test]
        public void HttpDescriptor_ValidatesPortableFields()
        {
            LlmEndpointDescriptor invalid = new()
            {
                EndpointId = "",
                Kind = LlmEndpointKind.HttpOpenAi,
                BaseUrl = "relative",
                ContextWindowTokens = 128,
                ParallelSlots = 0
            };

            Assert.That(invalid.Validate(), Has.Count.EqualTo(4));

            LlmEndpointDescriptor valid = new()
            {
                EndpointId = "cloud-main",
                Kind = LlmEndpointKind.HttpOpenAi,
                BaseUrl = "https://example.test/v1",
                ContextWindowTokens = 8192,
                ParallelSlots = 2
            };

            Assert.That(valid.Validate(), Is.Empty);
        }

        [Test]
        public void RuntimeProfile_RejectsSelfAndDuplicateFallbacks()
        {
            LlmRuntimeProfile profile = new()
            {
                ProfileId = "primary",
                EndpointId = "cloud-main",
                FallbackProfileIds = new[] { "primary", "backup", "backup", "" }
            };

            IReadOnlyList<string> errors = profile.Validate();

            Assert.That(errors, Has.Count.EqualTo(3));
        }

        [Test]
        public void LegacyClientRegistry_DefaultOverloadsRemainCompatible()
        {
            LegacyRegistry registry = new();
            ILlmClientRegistry portable = registry;

            Assert.AreSame(
                registry.ResolveClientForRole("npc"),
                portable.ResolveClientForRole("npc", "explicit"));
            Assert.AreEqual(4096, portable.ResolveContextWindowForRole("npc", "explicit"));
            Assert.AreEqual(LlmExecutionMode.Offline,
                portable.ResolveExecutionModeForRole("npc", "explicit"));
            Assert.AreEqual("legacy", portable.ResolveProfileIdForRole("npc", "explicit"));
        }

        [Test]
        public async Task AgentAskAsync_PropagatesConfiguredProfile()
        {
            CapturingOrchestrator orchestrator = new();
            CoreAIAgent.Initialize(orchestrator, new AgentMemoryPolicy(), null);
            AgentConfig agent = new AgentBuilder("Magic")
                .WithMode(AgentMode.ChatOnly)
                .WithSystemPrompt("Route this test agent.")
                .WithLlmProfile("local-magic")
                .BuildDetached();

            await agent.AskAsync(orchestrator, "cast");

            Assert.IsNotNull(orchestrator.LastTask);
            Assert.AreEqual("local-magic", orchestrator.LastTask.RoutingProfileId);
        }
    }
}
