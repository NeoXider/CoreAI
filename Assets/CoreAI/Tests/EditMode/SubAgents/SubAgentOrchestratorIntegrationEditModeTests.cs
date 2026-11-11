using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Ai;
using CoreAI.Ai.SubAgents;
using CoreAI.Authority;
using CoreAI.Messaging;
using CoreAI.Session;
using NUnit.Framework;

namespace CoreAI.Core.Tests.EditMode.SubAgents
{
    /// <summary>
    /// A delegation run by the real <see cref="AiOrchestrator"/> over a scripted provider: the provider request a
    /// sub-agent produces carries no lead history and never offers the delegate tool, even when a host
    /// registered it on the sub-agent's role by mistake.
    /// </summary>
    [TestFixture]
    public sealed class SubAgentOrchestratorIntegrationEditModeTests
    {
        private const string LeadHistoryMarker = "LEAD-HISTORY-MARKER";

        private SynchronizationContext _previousSynchronizationContext;

        /// <summary>WHY: the orchestrator resumes on the captured context; the fixture runs it inline.</summary>
        [SetUp]
        public void DetachSynchronizationContext()
        {
            _previousSynchronizationContext = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(null);
        }

        [TearDown]
        public void RestoreSynchronizationContext()
        {
            SynchronizationContext.SetSynchronizationContext(_previousSynchronizationContext);
        }

        [Test]
        public async Task ProviderRequest_OfASubAgent_HasNoLeadHistory_AndNoDelegateTool()
        {
            InMemoryAgentMemoryStore memory = new();
            memory.AppendChatMessage(BuiltInAgentRoleIds.Creator, "user", LeadHistoryMarker + " build me a castle");
            memory.AppendChatMessage(BuiltInAgentRoleIds.Creator, "assistant", LeadHistoryMarker + " planning");
            AgentMemoryPolicy policy = new();
            CapturingClient provider = new();
            AiOrchestrator orchestrator = CreateOrchestrator(provider, memory, policy);
            OrchestratorSession session = new("conversation-1", new OrchestratorModeOptions(),
                new SubAgentRegistry(new[]
                {
                    new SubAgentDefinition("builder", BuiltInAgentRoleIds.Builder, "Builds structures.",
                        new[] { "inspect" })
                }), orchestrator);
            policy.AddToolForRole(BuiltInAgentRoleIds.Builder,
                new DelegateLlmTool("inspect", "Reads the scene.", (Func<string>)(() => "scene ok")));
            policy.AddToolForRole(BuiltInAgentRoleIds.Builder, session.DelegateTool);
            session.BeginPlan(LeadHistoryMarker + " goal", new SubAgentParentContext(BuiltInAgentRoleIds.Creator,
                "lead-trace", "lead-scope",
                new LocalActorIdentityProvider("player-1").GetActorContext(BuiltInAgentRoleIds.Creator)));

            SubAgentDelegationResult result = await SubAgentTestSupport.Delegate(session, "Build four walls",
                context: "Origin is 0,0,0");

            Assert.IsTrue(result.Ok, result.Message);
            Assert.AreEqual("walls built", result.Content);
            Assert.AreEqual(1, provider.Requests.Count);
            LlmCompletionRequest sent = provider.Requests[0];
            Assert.AreEqual(BuiltInAgentRoleIds.Builder, sent.AgentRoleId);
            foreach (Microsoft.Extensions.AI.ChatMessage message in
                     sent.ChatHistory ?? new List<Microsoft.Extensions.AI.ChatMessage>())
            {
                Assert.AreEqual(Microsoft.Extensions.AI.ChatRole.System, message.Role,
                    "A sub-agent starts a fresh conversation: only the request's own system tail may precede it.");
                StringAssert.DoesNotContain(LeadHistoryMarker, message.Text ?? "");
            }

            StringAssert.Contains("Build four walls", sent.UserPayload);
            StringAssert.Contains("Origin is 0,0,0", sent.UserPayload);
            StringAssert.DoesNotContain(LeadHistoryMarker, sent.UserPayload + sent.SystemPrompt);
            List<string> toolNames = new();
            foreach (ILlmTool tool in sent.Tools ?? Array.Empty<ILlmTool>())
            {
                toolNames.Add(tool.Name);
            }

            CollectionAssert.AreEqual(new[] { "inspect" }, toolNames,
                "Only the allowlist reaches the provider; the delegate tool never does.");
            Assert.AreEqual("lead-trace.sub.t1.1", sent.TraceId);
            Assert.AreEqual(2, memory.GetChatHistory(BuiltInAgentRoleIds.Creator).Length,
                "The sub-agent turn writes nothing into the lead's history.");
        }

        /// <summary>
        /// Integration change I1: a sub-agent turn is history-free even on a role that keeps chat history (every
        /// built-in role but the Programmer does), so it neither reads the role's stored conversation nor adds to it.
        /// </summary>
        [Test]
        public async Task RoleWithChatHistoryOn_SubAgentNeitherReadsNorExtendsIt()
        {
            const string roleHistoryMarker = "BUILDER-HISTORY-MARKER";
            InMemoryAgentMemoryStore memory = new();
            memory.AppendChatMessage(BuiltInAgentRoleIds.Builder, "user", roleHistoryMarker + " earlier chat");
            memory.AppendChatMessage(BuiltInAgentRoleIds.Builder, "assistant", "noted");
            AgentMemoryPolicy policy = new();
            policy.ConfigureChatHistory(BuiltInAgentRoleIds.Builder, true, 0, false);
            CapturingClient provider = new();
            OrchestratorSession session = new("conversation-1", new OrchestratorModeOptions(),
                new SubAgentRegistry(new[]
                {
                    new SubAgentDefinition("builder", BuiltInAgentRoleIds.Builder, "Builds structures.", null)
                }), CreateOrchestrator(provider, memory, policy));
            session.BeginPlan("goal", SubAgentTestSupport.Parent());

            SubAgentDelegationResult result = await SubAgentTestSupport.Delegate(session, "Build four walls");

            Assert.IsTrue(result.Ok, result.Message);
            StringAssert.DoesNotContain(roleHistoryMarker, AllText(provider.Requests[0]),
                "A sub-agent must not read the role's stored conversation.");
            Assert.AreEqual(2, memory.GetChatHistory(BuiltInAgentRoleIds.Builder).Length,
                "A sub-agent must not append its turn to the role's conversation.");
        }

        /// <summary>
        /// The owner's decision: a sub-agent never sees the role's durable memory by default, also when it shares
        /// the lead's role; a definition opts in explicitly. Both cases run on the same role and store.
        /// </summary>
        [TestCase(false)]
        [TestCase(true)]
        public async Task RoleMemory_StaysOutOfASubAgentTurn_UnlessTheDefinitionOptsIn(bool useRoleMemory)
        {
            const string memoryMarker = "BUILDER-MEMORY-MARKER";
            InMemoryAgentMemoryStore memory = new();
            memory.Save(BuiltInAgentRoleIds.Builder, new AgentMemoryState { Memory = memoryMarker + " walls are stone" });
            AgentMemoryPolicy policy = new();
            policy.EnableMemoryTool(BuiltInAgentRoleIds.Builder);
            CapturingClient provider = new();
            OrchestratorSession session = new("conversation-1", new OrchestratorModeOptions(),
                new SubAgentRegistry(new[]
                {
                    new SubAgentDefinition("builder", BuiltInAgentRoleIds.Builder, "Builds structures.", null,
                        useRoleMemory: useRoleMemory)
                }), CreateOrchestrator(provider, memory, policy));
            session.BeginPlan("goal", SubAgentTestSupport.Parent());

            SubAgentDelegationResult result = await SubAgentTestSupport.Delegate(session, "Build four walls");

            Assert.IsTrue(result.Ok, result.Message);
            Assert.AreEqual(useRoleMemory, AllText(provider.Requests[0]).Contains(memoryMarker),
                useRoleMemory
                    ? "A definition that opts in sees the role's memory."
                    : "Durable memory stays out of a sub-agent turn by default.");
        }

        /// <summary>
        /// The history mode wins over the chat source rule, which otherwise turns history on for a Chat turn; the
        /// twin shows that same request reading the history with the default mode.
        /// </summary>
        [TestCase(AiTaskHistoryMode.RoleDefault, true)]
        [TestCase(AiTaskHistoryMode.None, false)]
        public async Task HistoryMode_DecidesWhetherAChatTurnReadsAndExtendsHistory(
            AiTaskHistoryMode mode,
            bool expectHistory)
        {
            const string historyMarker = "CREATOR-HISTORY-MARKER";
            InMemoryAgentMemoryStore memory = new();
            memory.AppendChatMessage(BuiltInAgentRoleIds.Creator, "user", historyMarker + " earlier");
            memory.AppendChatMessage(BuiltInAgentRoleIds.Creator, "assistant", "ok");
            AgentMemoryPolicy policy = new();
            policy.ConfigureChatHistory(BuiltInAgentRoleIds.Creator, false, 0, false);
            CapturingClient provider = new();
            AiOrchestrator orchestrator = CreateOrchestrator(provider, memory, policy);

            LlmCompletionResult result = await orchestrator.RunTaskResultAsync(new AiTaskRequest
            {
                RoleId = BuiltInAgentRoleIds.Creator,
                Hint = "next step",
                SourceTag = "Chat",
                HistoryMode = mode,
                ActorContext = SubAgentTestSupport.Actor()
            });

            Assert.IsTrue(result.Ok, result.Error);
            Assert.AreEqual(expectHistory, AllText(provider.Requests[0]).Contains(historyMarker));
            Assert.AreEqual(expectHistory ? 4 : 2, memory.GetChatHistory(BuiltInAgentRoleIds.Creator).Length,
                expectHistory ? "A chat turn appends its user and assistant messages." : "A history-free turn appends nothing.");
        }

        /// <summary>
        /// Integration change I2: request tools reach the provider next to the role's tools, and a request tool
        /// replaces a role tool of the same name, so a stale role registration cannot shadow it.
        /// </summary>
        [Test]
        public async Task RequestTools_ReachTheProvider_AndReplaceARoleToolOfTheSameName()
        {
            AgentMemoryPolicy policy = new();
            policy.AddToolForRole(BuiltInAgentRoleIds.Creator,
                new DelegateLlmTool("inspect", "Role version.", (Func<string>)(() => "role")));
            ILlmTool requestInspect = new DelegateLlmTool("inspect", "Request version.", (Func<string>)(() => "request"));
            ILlmTool extra = new DelegateLlmTool("extra", "Request-only tool.", (Func<string>)(() => "extra"));
            CapturingClient provider = new();
            AiOrchestrator orchestrator = CreateOrchestrator(provider, new InMemoryAgentMemoryStore(), policy);

            LlmCompletionResult result = await orchestrator.RunTaskResultAsync(new AiTaskRequest
            {
                RoleId = BuiltInAgentRoleIds.Creator,
                Hint = "look around",
                RequestTools = new[] { requestInspect, extra },
                ActorContext = SubAgentTestSupport.Actor()
            });

            Assert.IsTrue(result.Ok, result.Error);
            LlmCompletionRequest sent = provider.Requests[0];
            List<string> names = ToolNames(sent);
            Assert.AreEqual(1, names.FindAll(name => name == "inspect").Count, "One tool per name reaches the provider.");
            CollectionAssert.Contains(names, "extra");
            foreach (ILlmTool tool in sent.Tools)
            {
                if (tool.Name == "inspect")
                {
                    Assert.AreSame(requestInspect, tool, "The request tool replaces the role tool of the same name.");
                }
            }
        }

        [Test]
        public async Task RequestTools_AreFilteredByTheAllowlistLikeRoleTools()
        {
            AgentMemoryPolicy policy = new();
            policy.AddToolForRole(BuiltInAgentRoleIds.Creator,
                new DelegateLlmTool("inspect", "Role tool.", (Func<string>)(() => "role")));
            CapturingClient provider = new();
            AiOrchestrator orchestrator = CreateOrchestrator(provider, new InMemoryAgentMemoryStore(), policy);

            LlmCompletionResult result = await orchestrator.RunTaskResultAsync(new AiTaskRequest
            {
                RoleId = BuiltInAgentRoleIds.Creator,
                Hint = "look around",
                RequestTools = new ILlmTool[]
                {
                    new DelegateLlmTool("extra", "Request-only tool.", (Func<string>)(() => "extra"))
                },
                AllowedToolNames = new[] { "inspect" },
                ActorContext = SubAgentTestSupport.Actor()
            });

            Assert.IsTrue(result.Ok, result.Error);
            CollectionAssert.AreEqual(new[] { "inspect" }, ToolNames(provider.Requests[0]));
        }

        [Test]
        public void Clone_CarriesTheContextModesAndTheRequestTools()
        {
            ILlmTool[] tools = { new DelegateLlmTool("extra", "Request-only tool.", (Func<string>)(() => "extra")) };
            AiTaskRequest source = new()
            {
                HistoryMode = AiTaskHistoryMode.None,
                MemoryMode = AiTaskMemoryMode.None,
                RequestTools = tools
            };

            AiTaskRequest clone = source.CloneForRole(BuiltInAgentRoleIds.Builder);

            Assert.AreEqual(AiTaskHistoryMode.None, clone.HistoryMode);
            Assert.AreEqual(AiTaskMemoryMode.None, clone.MemoryMode);
            Assert.AreSame(tools, clone.RequestTools);
            Assert.AreEqual(AiTaskHistoryMode.RoleDefault, new AiTaskRequest().HistoryMode,
                "The default keeps every existing request exactly as it was.");
            Assert.AreEqual(AiTaskMemoryMode.RoleDefault, new AiTaskRequest().MemoryMode);
            Assert.IsNull(new AiTaskRequest().RequestTools);
        }

        /// <summary>
        /// A lead turn built by the session, run by the real orchestrator: the provider is offered the delegate
        /// and declare tools and receives the lead instructions, while the host's request keeps its own role,
        /// hint and tools. This is what lets a lead plan and delegate once a host enables the mode.
        /// </summary>
        [Test]
        public async Task LeadRequest_OffersTheLeadTools_AndCarriesTheLeadInstructions()
        {
            AgentMemoryPolicy policy = new();
            policy.AddToolForRole(BuiltInAgentRoleIds.Creator,
                new DelegateLlmTool("get_mod_logs", "Reads mod logs.", (Func<string>)(() => "no errors")));
            CapturingClient provider = new();
            AiOrchestrator orchestrator = CreateOrchestrator(provider, new InMemoryAgentMemoryStore(), policy);
            OrchestratorSession session = new("conversation-1", new OrchestratorModeOptions(),
                new SubAgentRegistry(new[]
                {
                    new SubAgentDefinition("builder", BuiltInAgentRoleIds.Builder, "Builds structures.", null)
                }), orchestrator);
            OrchestrationPlan plan = session.BeginPlan("build a castle", SubAgentTestSupport.Parent());
            AiTaskRequest hostRequest = new()
            {
                RoleId = BuiltInAgentRoleIds.Creator,
                Hint = "build a castle",
                SourceTag = "Chat",
                RequestSystemInstructions = "Host tail.",
                ActorContext = SubAgentTestSupport.Actor()
            };

            AiTaskRequest leadRequest = session.CreateLeadRequest(hostRequest, plan);
            LlmCompletionResult result = await orchestrator.RunTaskResultAsync(leadRequest);

            Assert.IsTrue(result.Ok, result.Error);
            Assert.IsNull(hostRequest.RequestTools, "The host's own request is not modified.");
            Assert.AreEqual(SubAgentTestSupport.LeadTrace, leadRequest.TraceId);
            LlmCompletionRequest sent = provider.Requests[0];
            List<string> names = ToolNames(sent);
            CollectionAssert.IsSubsetOf(new[] { "get_mod_logs", DelegateTaskTool.DefaultName, DeclareTasksTool.DefaultName },
                names);
            string text = AllText(sent);
            StringAssert.Contains("Host tail.", text);
            StringAssert.Contains("## Orchestrator mode", text);
            StringAssert.Contains("- builder: Builds structures.", text);
            StringAssert.Contains("build a castle", sent.UserPayload);
        }

        /// <summary>
        /// The session inspector applies the same request rules as the orchestrator (one shared helper), so a
        /// diagnostic of a sub-agent request shows what the provider really gets: the request tools, no chat
        /// history and no durable memory.
        /// </summary>
        [Test]
        public void Inspector_ShowsRequestTools_AndHonoursTheContextModes()
        {
            const string historyMarker = "INSPECT-HISTORY-MARKER";
            const string memoryMarker = "INSPECT-MEMORY-MARKER";
            InMemoryAgentMemoryStore memory = new();
            memory.AppendChatMessage(BuiltInAgentRoleIds.Builder, "user", historyMarker + " earlier");
            memory.Save(BuiltInAgentRoleIds.Builder, new AgentMemoryState { Memory = memoryMarker + " stone" });
            AgentMemoryPolicy policy = new();
            policy.ConfigureChatHistory(BuiltInAgentRoleIds.Builder, true, 0, false);
            policy.EnableMemoryTool(BuiltInAgentRoleIds.Builder);
            AiPromptComposer composer = new(new StubSystemPrompts(), new StubUserTemplates(), null);
            CoreAI.Diagnostics.AgentSessionInspector inspector = new(composer, new StubSystemPrompts(), memory, null,
                policy, new CoreAISettingsOptions());
            ILlmTool requestTool = new DelegateLlmTool("extra", "Request-only tool.", (Func<string>)(() => "extra"));

            CoreAI.Diagnostics.AgentSessionSnapshot plain = inspector.Inspect(BuiltInAgentRoleIds.Builder,
                new AiTaskRequest { Hint = "walls" });
            CoreAI.Diagnostics.AgentSessionSnapshot isolated = inspector.Inspect(BuiltInAgentRoleIds.Builder,
                new AiTaskRequest
                {
                    Hint = "walls",
                    HistoryMode = AiTaskHistoryMode.None,
                    MemoryMode = AiTaskMemoryMode.None,
                    RequestTools = new[] { requestTool }
                });

            StringAssert.Contains(memoryMarker, plain.MemoryText);
            Assert.IsTrue(plain.EstimatedRequestChatHistory.Exists(m => (m.Content ?? "").Contains(historyMarker)));
            Assert.AreEqual("", isolated.MemoryText);
            Assert.IsFalse(isolated.EstimatedRequestChatHistory.Exists(m => (m.Content ?? "").Contains(historyMarker)));
            Assert.IsTrue(isolated.Tools.Exists(t => t.Name == "extra"));
            Assert.IsFalse(plain.Tools.Exists(t => t.Name == "extra"));
        }

        /// <summary>
        /// A role enforced as ToolsOnly keeps running without a conversation when a chat surface asks it; the twin
        /// is the same role without enforcement, which the chat source rule still gives its history, as before.
        /// </summary>
        [TestCase(true, false)]
        [TestCase(false, true)]
        public async Task ChatTurn_OnAToolsOnlyRole_ReadsHistoryOnlyWithoutEnforcement(bool enforce, bool expectHistory)
        {
            const string historyMarker = "AUDITOR-HISTORY-MARKER";
            AgentMemoryPolicy policy = new();
            AgentBuilder builder = new AgentBuilder("Auditor") { SuppressBuildWarnings = true }
                .WithSystemPrompt("Audit.")
                .WithMode(AgentMode.ToolsOnly);
            if (enforce)
            {
                builder = builder.WithModeEnforcement();
            }

            builder.BuildDetached().ApplyToPolicy(policy);
            InMemoryAgentMemoryStore memory = new();
            memory.AppendChatMessage("Auditor", "user", historyMarker + " earlier");
            memory.AppendChatMessage("Auditor", "assistant", "ok");
            CapturingClient provider = new();
            AiOrchestrator orchestrator = CreateOrchestrator(provider, memory, policy);

            LlmCompletionResult result = await orchestrator.RunTaskResultAsync(new AiTaskRequest
            {
                RoleId = "Auditor",
                Hint = "scan",
                SourceTag = "Chat",
                ActorContext = SubAgentTestSupport.Actor()
            });

            Assert.IsTrue(result.Ok, result.Error);
            Assert.AreEqual(expectHistory, AllText(provider.Requests[0]).Contains(historyMarker));
        }

        /// <summary>
        /// A role definition's routing profile reaches an orchestrated turn that names none; an explicit profile on
        /// the request still wins, and a role without a preference keeps the empty profile.
        /// </summary>
        [TestCase("Scout", "", "fast-profile")]
        [TestCase("Scout", "explicit-profile", "explicit-profile")]
        [TestCase("Plain", "", "")]
        public async Task RoutingProfile_FallsBackToTheRoleDefinition_UnlessTheRequestNamesOne(
            string roleId,
            string requested,
            string expected)
        {
            AgentMemoryPolicy policy = new();
            policy.ApplyRoleDefinition(new RoleDefinition("Scout") { RoutingProfileId = "fast-profile" });
            CapturingClient provider = new();
            AiOrchestrator orchestrator = CreateOrchestrator(provider, new InMemoryAgentMemoryStore(), policy);

            LlmCompletionResult result = await orchestrator.RunTaskResultAsync(new AiTaskRequest
            {
                RoleId = roleId,
                Hint = "look",
                RoutingProfileId = requested,
                ActorContext = SubAgentTestSupport.Actor()
            });

            Assert.IsTrue(result.Ok, result.Error);
            Assert.AreEqual(expected, provider.Requests[0].RoutingProfileId);
        }

        private static AiOrchestrator CreateOrchestrator(
            ILlmClient provider,
            IAgentMemoryStore memory,
            AgentMemoryPolicy policy)
        {
            AiPromptComposer composer = new(new StubSystemPrompts(), new StubUserTemplates(), null);
            return new AiOrchestrator(
                new SoloAuthorityHost(),
                provider,
                new NoOpCommandSink(),
                new SessionTelemetryCollector(),
                composer,
                memory,
                policy,
                null,
                null,
                new CoreAISettingsOptions(),
                new LocalActorIdentityProvider("player-1"));
        }

        private static string AllText(LlmCompletionRequest request)
        {
            StringBuilder text = new();
            text.Append(request.SystemPrompt).Append('\n').Append(request.UserPayload);
            foreach (Microsoft.Extensions.AI.ChatMessage message in
                     request.ChatHistory ?? new List<Microsoft.Extensions.AI.ChatMessage>())
            {
                text.Append('\n').Append(message.Text);
            }

            return text.ToString();
        }

        private static List<string> ToolNames(LlmCompletionRequest request)
        {
            List<string> names = new();
            foreach (ILlmTool tool in request.Tools ?? Array.Empty<ILlmTool>())
            {
                names.Add(tool.Name);
            }

            return names;
        }

        private sealed class CapturingClient : ILlmClient
        {
            public List<LlmCompletionRequest> Requests { get; } = new();

            public Task<LlmCompletionResult> CompleteAsync(
                LlmCompletionRequest request,
                CancellationToken cancellationToken = default)
            {
                Requests.Add(request);
                return Task.FromResult(new LlmCompletionResult { Ok = true, Content = "walls built", TotalTokens = 12 });
            }

            public async IAsyncEnumerable<LlmStreamChunk> CompleteStreamingAsync(
                LlmCompletionRequest request,
                [EnumeratorCancellation] CancellationToken cancellationToken = default)
            {
                Requests.Add(request);
                await Task.Yield();
                yield return new LlmStreamChunk { Text = "walls built", IsDone = true };
            }
        }

        private sealed class StubSystemPrompts : IAgentSystemPromptProvider
        {
            public bool TryGetSystemPrompt(string roleId, out string systemPrompt)
            {
                systemPrompt = "You are " + roleId + ".";
                return true;
            }
        }

        private sealed class StubUserTemplates : IAgentUserPromptTemplateProvider
        {
            public bool TryGetUserTemplate(string roleId, out string template)
            {
                template = "{hint}";
                return true;
            }
        }

        private sealed class NoOpCommandSink : IAiGameCommandSink
        {
            public void Publish(ApplyAiGameCommand command)
            {
            }
        }
    }
}
