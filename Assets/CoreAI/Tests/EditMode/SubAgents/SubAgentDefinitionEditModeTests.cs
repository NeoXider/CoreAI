using System;
using CoreAI.Ai;
using CoreAI.Ai.SubAgents;
using CoreAI.Authority;
using NUnit.Framework;

namespace CoreAI.Core.Tests.EditMode.SubAgents
{
    /// <summary>Sub-agent definitions as validated data, the registry, the mode options and the request factory.</summary>
    [TestFixture]
    public sealed class SubAgentDefinitionEditModeTests
    {
        [Test]
        public void Definition_KeepsItsDataAndDefaults()
        {
            SubAgentDefinition definition = new(" builder ", " Builder ", " Builds things. ",
                new[] { " world_command ", "world_command", "memory" });

            Assert.AreEqual("builder", definition.Id);
            Assert.AreEqual("Builder", definition.RoleId);
            Assert.AreEqual("Builds things.", definition.Description);
            CollectionAssert.AreEqual(new[] { "world_command", "memory" }, definition.AllowedToolNames);
            Assert.AreEqual("", definition.RoutingProfileId);
            Assert.IsNull(definition.MaxOutputTokens);
            Assert.AreEqual(SubAgentDefinition.DefaultMaxToolCallRoundtrips, definition.MaxToolCallRoundtrips);
            Assert.AreEqual(SubAgentDefinition.DefaultMaxResultChars, definition.MaxResultChars);
            Assert.AreEqual(SubAgentDefinition.DefaultDeadline, definition.Deadline);
            Assert.IsTrue(definition.AllowsTool("memory"));
            Assert.IsFalse(definition.AllowsTool("execute_lua"));
        }

        [Test]
        public void Definition_NullAllowlist_MeansNoTools_NotEveryTool()
        {
            SubAgentDefinition definition = new("talker", "SmartChat", "Talks.", null);

            Assert.AreEqual(0, definition.AllowedToolNames.Count);
            AiTaskRequest request = SubAgentRequestFactory.Create(definition, "say hi", "",
                SubAgentTestSupport.Parent(), "plan", "t1", 1);
            Assert.IsNotNull(request.AllowedToolNames, "A null allowlist would offer every tool of the role.");
            Assert.AreEqual(0, request.AllowedToolNames.Length);
        }

        [TestCase("delegate_task")]
        [TestCase("Delegate_Task")]
        [TestCase(" delegate_task ")]
        public void Definition_RefusesTheDelegateToolInItsAllowlist(string toolName)
        {
            ArgumentException error = Assert.Throws<ArgumentException>(() =>
                new SubAgentDefinition("builder", "Builder", "Builds.", new[] { "world_command", toolName }));

            StringAssert.Contains("delegate", error.Message);
        }

        [Test]
        public void Registry_RefusesARenamedDelegateToolInAnAllowlist()
        {
            SubAgentRegistry registry = new(null, "hand_off");
            SubAgentDefinition sneaky = new("builder", "Builder", "Builds.", new[] { "hand_off" });

            Assert.Throws<ArgumentException>(() => registry.Register(sneaky));
            Assert.AreEqual(0, registry.Definitions.Count);
        }

        [Test]
        public void Factory_DropsARenamedDelegateToolFromTheRequest()
        {
            SubAgentDefinition definition = new("builder", "Builder", "Builds.", new[] { "hand_off", "world_command" });

            AiTaskRequest request = SubAgentRequestFactory.Create(definition, "build", "",
                SubAgentTestSupport.Parent(), "plan", "t1", 1, "hand_off");

            CollectionAssert.AreEqual(new[] { "world_command" }, request.AllowedToolNames);
        }

        [TestCase("")]
        [TestCase("has space")]
        [TestCase("slash/id")]
        public void Definition_RefusesAnInvalidId(string id)
        {
            Assert.Throws<ArgumentException>(() => new SubAgentDefinition(id, "Builder", "Builds.", null));
        }

        [Test]
        public void Definition_RefusesMissingRoleDescriptionAndBadCaps()
        {
            Assert.Throws<ArgumentException>(() => new SubAgentDefinition("a", " ", "d", null));
            Assert.Throws<ArgumentException>(() => new SubAgentDefinition("a", "r", " ", null));
            Assert.Throws<ArgumentException>(() => new SubAgentDefinition("a", "r", "d", new[] { " " }));
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                new SubAgentDefinition("a", "r", "d", null, maxOutputTokens: -1));
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                new SubAgentDefinition("a", "r", "d", null, maxToolCallRoundtrips: -1));
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                new SubAgentDefinition("a", "r", "d", null, maxResultChars: 0));
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                new SubAgentDefinition("a", "r", "d", null, deadline: TimeSpan.Zero));
        }

        [Test]
        public void Registry_LooksUpCaseInsensitively_AndRefusesDuplicates()
        {
            SubAgentRegistry registry = new(new[] { SubAgentTestSupport.Builder() });

            Assert.IsTrue(registry.TryGet("BUILDER", out SubAgentDefinition found));
            Assert.AreEqual("builder", found.Id);
            Assert.Throws<ArgumentException>(() =>
                registry.Register(new SubAgentDefinition("Builder", "Builder", "Again.", null)));
            Assert.IsTrue(registry.Remove("builder"));
            Assert.IsFalse(registry.TryGet("builder", out _));
            Assert.IsFalse(registry.Remove("builder"));
        }

        [Test]
        public void Options_AreOffByDefault_WithOneSubAgentAtATime()
        {
            OrchestratorModeOptions options = new();

            Assert.IsFalse(options.Enabled, "Orchestrator mode is opt-in.");
            Assert.AreEqual(1, options.MaxParallelSubAgents);
            Assert.AreEqual(DelegateTaskTool.DefaultName, options.DelegateToolName);
            OrchestrationBudget budget = options.CreateBudget();
            Assert.AreEqual(options.MaxSubAgentCallsPerPlan, budget.MaxSubAgentCalls);
            Assert.AreEqual(0, budget.MaxTotalTokens);
            Assert.AreEqual(1, budget.MaxParallelSubAgents);
        }

        [Test]
        public void Options_ClampOutOfRangeBudgetValues()
        {
            OrchestratorModeOptions options = new()
            {
                MaxSubAgentCallsPerPlan = 0,
                MaxTokensPerPlan = -5,
                MaxParallelSubAgents = 0
            };

            OrchestrationBudget budget = options.CreateBudget();

            Assert.AreEqual(1, budget.MaxSubAgentCalls);
            Assert.AreEqual(0, budget.MaxTotalTokens);
            Assert.AreEqual(1, budget.MaxParallelSubAgents);
            Assert.Throws<ArgumentOutOfRangeException>(() => new OrchestrationBudget(0, 0, 1));
        }

        [Test]
        public void Factory_WithoutALeadTraceOrScope_StillIsolatesTheRun()
        {
            ActorContext actor = SubAgentTestSupport.Actor();
            SubAgentParentContext parent = new(BuiltInAgentRoleIds.Creator, "", "", actor);

            AiTaskRequest request = SubAgentRequestFactory.Create(SubAgentTestSupport.Builder(), "build", "",
                parent, "plan-9", "t2", 3);

            Assert.AreEqual("sub.plan-9.t2.3", request.TraceId);
            Assert.AreEqual("orchestrator/plan-9/sub/t2.3", request.CancellationScope);
            Assert.AreEqual("build", request.Hint);
        }

        [Test]
        public void SourceTag_RecognisesSubAgentTurnsAndTheirDerivedTags()
        {
            Assert.IsTrue(SubAgentRequestFactory.IsSubAgentSourceTag("SubAgent"));
            Assert.IsTrue(SubAgentRequestFactory.IsSubAgentSourceTag("SubAgent:lua_repair"));
            Assert.IsFalse(SubAgentRequestFactory.IsSubAgentSourceTag("Chat"));
            Assert.IsFalse(SubAgentRequestFactory.IsSubAgentSourceTag("SubAgentish"));
            Assert.IsFalse(SubAgentRequestFactory.IsSubAgentSourceTag(null));
        }

        [Test]
        public void ErrorCodes_HaveStableWireNames()
        {
            Assert.AreEqual("call_budget_exhausted", SubAgentErrorCodes.ToWireName(SubAgentErrorCode.CallBudgetExhausted));
            Assert.AreEqual("token_budget_exhausted",
                SubAgentErrorCodes.ToWireName(SubAgentErrorCode.TokenBudgetExhausted));
            Assert.AreEqual("nested_delegation_refused",
                SubAgentErrorCodes.ToWireName(SubAgentErrorCode.NestedDelegation));
            Assert.AreEqual("plan_cancelled", SubAgentErrorCodes.ToWireName(SubAgentErrorCode.PlanCancelled));
            Assert.AreEqual("deadline_exceeded", SubAgentErrorCodes.ToWireName(SubAgentErrorCode.DeadlineExceeded));
            foreach (SubAgentErrorCode code in Enum.GetValues(typeof(SubAgentErrorCode)))
            {
                StringAssert.IsMatch("^[a-z_]+$", SubAgentErrorCodes.ToWireName(code));
            }
        }
    }
}
