using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.AgentMemory;
using CoreAI.Ai;
using NUnit.Framework;

namespace CoreAI.Core.Tests.EditMode
{
    /// <summary>
    /// Newcomer audit items A7 (<see cref="AgentMode"/> carried into the prepared role behind
    /// <see cref="AgentBuilder.WithModeEnforcement"/>, warnings meanwhile) and A9 (a build before CoreAI starts is
    /// queued; an unconfigured role warns once).
    /// </summary>
    public sealed class AgentModeAndPendingBuildEditModeTests
    {
        [SetUp]
        public void SetUp()
        {
            CoreAIAgent.Reset();
            AgentBuilder.ClearPendingBuilds();
        }

        [TearDown]
        public void TearDown()
        {
            CoreAIAgent.Reset();
            AgentBuilder.ClearPendingBuilds();
        }

        private static AgentBuilder ChatOnlyWithTools(string roleId)
        {
            return new AgentBuilder(roleId) { SuppressBuildWarnings = true }
                .WithSystemPrompt("You are a storyteller.")
                .WithMode(AgentMode.ChatOnly)
                .WithMemory()
                .WithTool(AgentRoleTestHarness.Stub("roll_dice"));
        }

        [Test]
        public void ChatOnly_WithoutEnforcement_StillOffersItsTools_AndWarns()
        {
            AgentMemoryPolicy policy = new();
            AgentBuilder builder = ChatOnlyWithTools("Storyteller");

            builder.BuildDetached().ApplyToPolicy(policy);

            CollectionAssert.AreEquivalent(new[] { "memory", "roll_dice" },
                AgentRoleTestHarness.ToolNames(policy.GetToolsForRole("Storyteller")),
                "this release keeps today's behaviour unless the mode is enforced");
            Assert.IsTrue(builder.ValidateOnBuild().Any(issue =>
                issue.Code == AgentBuilderIssueCode.ChatOnlyModeOffersTools));
            Assert.IsFalse(policy.TryGetEnforcedMode("Storyteller", out _));
        }

        [Test]
        public async Task ChatOnly_Enforced_SendsNoTools_EvenAfterALaterAddToolForRole()
        {
            AgentMemoryPolicy policy = new();
            AgentBuilder builder = ChatOnlyWithTools("Storyteller").WithModeEnforcement();
            builder.BuildDetached().ApplyToPolicy(policy);
            policy.AddToolForRole("Storyteller", AgentRoleTestHarness.Stub("late_tool"));
            AgentRoleTestHarness.CapturingLlmClient llm = new();

            await AgentRoleTestHarness.BuildOrchestrator(llm, policy).RunTaskAsync(
                new AiTaskRequest { RoleId = "Storyteller", Hint = "tell me a tale" }, CancellationToken.None);

            Assert.IsEmpty(AgentRoleTestHarness.ToolNames(llm.LastRequest));
            Assert.IsEmpty(policy.GetToolsForRole("Storyteller"));
            Assert.IsTrue(policy.TryGetEnforcedMode("Storyteller", out AgentMode mode));
            Assert.AreEqual(AgentMode.ChatOnly, mode);
            Assert.IsFalse(builder.ValidateOnBuild().Any(issue =>
                issue.Code == AgentBuilderIssueCode.ChatOnlyModeOffersTools), "an enforced mode has nothing to warn about");
        }

        [Test]
        public void ToolsOnly_WithoutEnforcement_KeepsHistory_AndWarns()
        {
            AgentBuilder builder = new AgentBuilder("Auditor") { SuppressBuildWarnings = true }
                .WithSystemPrompt("Audit.")
                .WithMode(AgentMode.ToolsOnly)
                .WithTool(AgentRoleTestHarness.Stub("scan"));

            Assert.IsTrue(builder.BuildDetached().WithChatHistory);
            Assert.IsTrue(builder.ValidateOnBuild().Any(issue =>
                issue.Code == AgentBuilderIssueCode.ToolsOnlyModeKeepsChatHistory));
        }

        [Test]
        public async Task ToolsOnly_Enforced_SendsNoChatHistory()
        {
            AgentMemoryPolicy policy = new();
            new AgentBuilder("Auditor") { SuppressBuildWarnings = true }
                .WithSystemPrompt("Audit.")
                .WithMode(AgentMode.ToolsOnly)
                .WithModeEnforcement()
                .WithTool(AgentRoleTestHarness.Stub("scan"))
                .BuildDetached()
                .ApplyToPolicy(policy);
            InMemoryAgentMemoryStore memory = new();
            AgentRoleTestHarness.CapturingLlmClient llm = new();
            AiOrchestrator orchestrator = AgentRoleTestHarness.BuildOrchestrator(llm, policy, memory);

            await orchestrator.RunTaskAsync(new AiTaskRequest { RoleId = "Auditor", Hint = "first" });
            await orchestrator.RunTaskAsync(new AiTaskRequest { RoleId = "Auditor", Hint = "second" });

            Assert.IsFalse(policy.GetRoleConfig("Auditor").WithChatHistory);
            Assert.AreEqual(0, AgentRoleTestHarness.HistoryTurnCount(llm.LastRequest));
            CollectionAssert.Contains(AgentRoleTestHarness.ToolNames(llm.LastRequest), "scan");
        }

        [Test]
        public async Task ToolsOnly_Enforced_WithExplicitHistory_KeepsIt()
        {
            AgentMemoryPolicy policy = new();
            new AgentBuilder("Auditor") { SuppressBuildWarnings = true }
                .WithSystemPrompt("Audit.")
                .WithMode(AgentMode.ToolsOnly)
                .WithModeEnforcement()
                .WithChatHistory()
                .WithTool(AgentRoleTestHarness.Stub("scan"))
                .BuildDetached()
                .ApplyToPolicy(policy);
            InMemoryAgentMemoryStore memory = new();
            AgentRoleTestHarness.CapturingLlmClient llm = new();
            AiOrchestrator orchestrator = AgentRoleTestHarness.BuildOrchestrator(llm, policy, memory);

            await orchestrator.RunTaskAsync(new AiTaskRequest { RoleId = "Auditor", Hint = "first" });
            await orchestrator.RunTaskAsync(new AiTaskRequest { RoleId = "Auditor", Hint = "second" });

            Assert.Greater(AgentRoleTestHarness.HistoryTurnCount(llm.LastRequest), 0,
                "negative twin: an explicit WithChatHistory wins over the ToolsOnly default");
        }

        [Test]
        public void ToolsAndChat_IsUntouchedByEnforcement()
        {
            AgentMemoryPolicy policy = new();
            new AgentBuilder("Helper") { SuppressBuildWarnings = true }
                .WithSystemPrompt("Help.")
                .WithModeEnforcement()
                .WithMemory()
                .WithTool(AgentRoleTestHarness.Stub("lookup"))
                .BuildDetached()
                .ApplyToPolicy(policy);

            CollectionAssert.AreEquivalent(new[] { "memory", "lookup" },
                AgentRoleTestHarness.ToolNames(policy.GetToolsForRole("Helper")));
            Assert.IsTrue(policy.GetRoleConfig("Helper").WithChatHistory);
        }

        [Test]
        public void BuildBeforeCoreAiStarts_IsQueued_AndAppliedOnInitialisation()
        {
            AgentConfig blacksmith = new AgentBuilder("Blacksmith") { SuppressBuildWarnings = true }
                .WithSystemPrompt("You are a blacksmith.")
                .WithTool(AgentRoleTestHarness.Stub("forge"))
                .Build();
            Assert.AreEqual(1, AgentBuilder.PendingBuildCount);
            AgentMemoryPolicy policy = new();
            Assert.IsFalse(policy.HasRole("Blacksmith"));

            Assert.AreEqual(1, AgentBuilder.ApplyPendingBuilds(policy));

            Assert.AreEqual(0, AgentBuilder.PendingBuildCount);
            Assert.IsTrue(policy.HasRole(blacksmith.RoleId));
            CollectionAssert.AreEqual(new[] { "forge" },
                AgentRoleTestHarness.ToolNames(policy.GetToolsForRole("Blacksmith")));
            Assert.IsTrue(policy.TryGetAdditionalSystemPrompt("Blacksmith", out string prompt));
            Assert.AreEqual("You are a blacksmith.", prompt);
            Assert.AreEqual(0, AgentBuilder.ApplyPendingBuilds(new AgentMemoryPolicy()), "the queue drains once");
        }

        [Test]
        public void LaterBuildForTheSameRole_ReplacesTheQueuedOne()
        {
            new AgentBuilder("Blacksmith") { SuppressBuildWarnings = true }.WithSystemPrompt("old").Build();
            new AgentBuilder("Guard") { SuppressBuildWarnings = true }.WithSystemPrompt("guard").Build();
            new AgentBuilder(" Blacksmith ") { SuppressBuildWarnings = true }.WithSystemPrompt("new").Build();
            AgentMemoryPolicy policy = new();

            Assert.AreEqual(2, AgentBuilder.ApplyPendingBuilds(policy));

            Assert.IsTrue(policy.TryGetAdditionalSystemPrompt("Blacksmith", out string prompt));
            Assert.AreEqual("new", prompt);
            Assert.IsTrue(policy.HasRole("Guard"));
        }

        [Test]
        public void ClearPendingBuilds_DropsTheQueue()
        {
            new AgentBuilder("Blacksmith") { SuppressBuildWarnings = true }.WithSystemPrompt("x").Build();

            AgentBuilder.ClearPendingBuilds();

            Assert.AreEqual(0, AgentBuilder.PendingBuildCount);
            AgentMemoryPolicy policy = new();
            Assert.AreEqual(0, AgentBuilder.ApplyPendingBuilds(policy));
            Assert.IsFalse(policy.HasRole("Blacksmith"));
        }

        [Test]
        public void BuildAfterCoreAiStarts_IsAppliedAtOnce_NotQueued()
        {
            AgentMemoryPolicy policy = new();
            CoreAIAgent.Initialize(null, policy, null);

            new AgentBuilder("Blacksmith") { SuppressBuildWarnings = true }.WithSystemPrompt("x").Build();

            Assert.AreEqual(0, AgentBuilder.PendingBuildCount);
            Assert.IsTrue(policy.HasRole("Blacksmith"));
        }

        [Test]
        public void UnconfiguredRole_WarnsOnce_AndPointsAtTheQueuedBuild()
        {
            AgentMemoryPolicy policy = new();
            AgentRoleTestHarness.RecordingLog log = new();
            policy.DiagnosticsLog = log;
            new AgentBuilder("Blacksmith") { SuppressBuildWarnings = true }.WithSystemPrompt("x").Build();

            policy.GetToolsForRole("Blacksmith");
            policy.GetToolsForRole("Blacksmith");

            Assert.AreEqual(1, log.Warnings.Count, string.Join("\n", log.Warnings));
            StringAssert.Contains("'Blacksmith'", log.Warnings[0]);
            StringAssert.Contains("queued", log.Warnings[0]);
        }

        [Test]
        public void MiscasedRole_WarnsWithTheKnownSpelling()
        {
            AgentMemoryPolicy policy = new();
            AgentRoleTestHarness.RecordingLog log = new();
            policy.DiagnosticsLog = log;

            policy.GetToolsForRole("merchant");

            Assert.AreEqual(1, log.Warnings.Count);
            StringAssert.Contains("did you mean 'Merchant'", log.Warnings[0]);
        }

        [Test]
        public void ConfiguredRoles_DoNotWarn()
        {
            AgentMemoryPolicy policy = new();
            AgentRoleTestHarness.RecordingLog log = new();
            policy.DiagnosticsLog = log;
            policy.AddToolForRole("ToolOnlyHostRole", AgentRoleTestHarness.Stub("game_state"));
            policy.ApplyRoleDefinition(new RoleDefinition("DefinedRole"));

            foreach (string roleId in BuiltInAgentRoleIds.AllBuiltInRoles
                         .Concat(new[] { "ToolOnlyHostRole", "DefinedRole", BuiltInAgentRoleIds.ContextCompactionAux, "" }))
            {
                policy.GetToolsForRole(roleId);
            }

            Assert.IsEmpty(log.Warnings, string.Join("\n", log.Warnings));
        }
    }
}
