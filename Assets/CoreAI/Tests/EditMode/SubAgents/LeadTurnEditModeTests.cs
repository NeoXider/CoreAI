using System.Collections.Generic;
using System.Threading.Tasks;
using CoreAI.Ai;
using CoreAI.Ai.SubAgents;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace CoreAI.Core.Tests.EditMode.SubAgents
{
    /// <summary>
    /// What a lead turn needs to plan and delegate: the <c>declare_tasks</c> tool over the plan, the lead
    /// instructions (default and replaced), and the lead request the session builds from the host's request.
    /// </summary>
    [TestFixture]
    public sealed class LeadTurnEditModeTests
    {
        [Test]
        public void DeclareTasks_PutsEveryTaskOnThePlan_WithTheNamedSubAgent()
        {
            OrchestratorSession session = SubAgentTestSupport.Session(new ScriptedSubAgentRunner());
            OrchestrationPlan plan = session.BeginPlan("castle", SubAgentTestSupport.Parent());

            JObject result = JObject.Parse(session.DeclareTool.Declare(
                "1. builder: Build four walls\n- scripter: Open the gate on touch\nRaise a flag: red"));

            Assert.IsTrue((bool)result["ok"], result.ToString());
            Assert.AreEqual(3, (int)result["total_tasks"]);
            IReadOnlyList<PlanTaskSnapshot> tasks = plan.Snapshot().Tasks;
            Assert.AreEqual("Build four walls", tasks[0].Title);
            Assert.AreEqual("builder", tasks[0].SubAgentId);
            Assert.AreEqual("Open the gate on touch", tasks[1].Title);
            Assert.AreEqual("scripter", tasks[1].SubAgentId);
            Assert.AreEqual("Raise a flag: red", tasks[2].Title,
                "A prefix that is no sub-agent id stays part of the title.");
            Assert.AreEqual("", tasks[2].SubAgentId);
            Assert.AreEqual(PlanTaskState.Pending, tasks[0].State);
            Assert.AreEqual(tasks[0].Id, (string)result["declared"][0]["task_id"]);
        }

        [Test]
        public async Task DeclaredTaskId_IsTheTaskADelegationRuns()
        {
            ScriptedSubAgentRunner runner = new();
            OrchestratorSession session = SubAgentTestSupport.Session(runner);
            OrchestrationPlan plan = session.BeginPlan("castle", SubAgentTestSupport.Parent());
            string taskId = (string)JObject.Parse(session.DeclareTool.Declare("builder: Build four walls"))
                ["declared"][0]["task_id"];

            SubAgentDelegationResult result = await SubAgentTestSupport.Delegate(session, "Build four walls",
                taskId: taskId);

            Assert.IsTrue(result.Ok, result.Message);
            Assert.AreEqual(taskId, result.TaskId);
            Assert.AreEqual(1, plan.Snapshot().TotalTasks, "The delegation ran the declared task, not a new one.");
            Assert.AreEqual(PlanTaskState.Succeeded, plan.Snapshot().Tasks[0].State);
        }

        [TestCase("")]
        [TestCase("  \n \n")]
        [TestCase("builder:")]
        public void DeclareTasks_RefusesAnEmptyPlan_AndDeclaresNothing(string tasks)
        {
            OrchestratorSession session = SubAgentTestSupport.Session(new ScriptedSubAgentRunner());
            OrchestrationPlan plan = session.BeginPlan("castle", SubAgentTestSupport.Parent());

            JObject result = JObject.Parse(session.DeclareTool.Declare(tasks));

            Assert.IsFalse((bool)result["ok"]);
            Assert.AreEqual("invalid_arguments", (string)result["error"]);
            Assert.AreEqual(0, plan.Snapshot().TotalTasks);
        }

        [Test]
        public void DeclareTasks_RefusesAPlanAboveTheCap_AndAcceptsOneAtIt()
        {
            OrchestratorSession session = SubAgentTestSupport.Session(new ScriptedSubAgentRunner());
            OrchestrationPlan plan = session.BeginPlan("castle", SubAgentTestSupport.Parent());
            List<string> lines = new();
            for (int i = 0; i < DeclareTasksTool.MaxTasksPerCall; i++)
            {
                lines.Add("builder: part " + i);
            }

            JObject atCap = JObject.Parse(session.DeclareTool.Declare(string.Join("\n", lines)));
            lines.Add("builder: one too many");
            JObject aboveCap = JObject.Parse(session.DeclareTool.Declare(string.Join("\n", lines)));

            Assert.IsTrue((bool)atCap["ok"]);
            Assert.IsFalse((bool)aboveCap["ok"]);
            Assert.AreEqual("invalid_arguments", (string)aboveCap["error"]);
            Assert.AreEqual(DeclareTasksTool.MaxTasksPerCall, plan.Snapshot().TotalTasks,
                "The refused call declared nothing.");
        }

        [Test]
        public void DeclareTasks_WithoutARunningPlan_IsRefused()
        {
            OrchestratorSession session = SubAgentTestSupport.Session(new ScriptedSubAgentRunner());

            JObject result = JObject.Parse(session.DeclareTool.Declare("builder: Build four walls"));

            Assert.IsFalse((bool)result["ok"]);
            Assert.AreEqual("plan_not_active", (string)result["error"]);
        }

        [Test]
        public async Task DeclareTasks_ThroughTheMeaiBinding_ReturnsTheSameResult()
        {
            OrchestratorSession session = SubAgentTestSupport.Session(new ScriptedSubAgentRunner());
            session.BeginPlan("castle", SubAgentTestSupport.Parent());
            Microsoft.Extensions.AI.AIFunction function = session.DeclareTool.CreateAIFunction();

            object raw = await function.InvokeAsync(new Microsoft.Extensions.AI.AIFunctionArguments(
                new Dictionary<string, object> { ["tasks"] = "builder: Build four walls" }));

            JObject result = JObject.Parse(SubAgentTestSupport.AsText(raw));
            Assert.IsTrue((bool)result["ok"], result.ToString());
            Assert.AreEqual(DeclareTasksTool.DefaultName, function.Name);
            Assert.IsFalse(session.DeclareTool.IsMutating);
        }

        [Test]
        public void DefaultLeadInstructions_NameTheToolsTheSubAgentsAndTheBudget()
        {
            string text = new DefaultLeadInstructionsProvider().BuildInstructions(new LeadInstructionsContext(
                new[] { SubAgentTestSupport.Builder(), SubAgentTestSupport.Scripter() },
                "delegate_task", "declare_tasks", 8, 1));

            StringAssert.Contains("declare_tasks", text);
            StringAssert.Contains("delegate_task", text);
            StringAssert.Contains("at most 8 sub-agent runs", text);
            StringAssert.Contains("- builder: Builds structures in the world.", text);
            StringAssert.Contains("- scripter: Writes game logic in Lua.", text);
            StringAssert.DoesNotContain("same turn", text, "With parallelism 1 the lead is not invited to batch.");
            Assert.AreEqual(text, new DefaultLeadInstructionsProvider().BuildInstructions(new LeadInstructionsContext(
                    new[] { SubAgentTestSupport.Builder(), SubAgentTestSupport.Scripter() },
                    "delegate_task", "declare_tasks", 8, 1)),
                "The same context gives the same text, so the lead prompt stays stable.");
        }

        [Test]
        public void DefaultLeadInstructions_InviteBatching_OnlyWhenParallelismAllowsIt()
        {
            string text = new DefaultLeadInstructionsProvider().BuildInstructions(new LeadInstructionsContext(
                new[] { SubAgentTestSupport.Builder() }, "delegate_task", "declare_tasks", 8, 3));

            StringAssert.Contains("independent tasks may be delegated in the same turn", text);
        }

        [Test]
        public void LeadRequest_KeepsTheHostRequest_AndAddsTheLeadToolsAndInstructions()
        {
            OrchestratorSession session = new("conversation-1", new OrchestratorModeOptions(),
                new SubAgentRegistry(new[] { SubAgentTestSupport.Builder() }), new ScriptedSubAgentRunner(),
                leadInstructions: new FixedInstructions("LEAD RULES"));
            OrchestrationPlan plan = session.BeginPlan("castle", SubAgentTestSupport.Parent());
            ILlmTool hostTool = new DelegateLlmTool("host_tool", "Host tool.", (System.Func<string>)(() => "x"));
            ILlmTool staleDelegate = new DelegateLlmTool(DelegateTaskTool.DefaultName, "Stale.",
                (System.Func<string>)(() => "stale"));
            AiTaskRequest host = new()
            {
                RoleId = BuiltInAgentRoleIds.Creator,
                Hint = "castle",
                TraceId = "host-trace",
                RequestSystemInstructions = "Host tail.",
                RequestTools = new[] { hostTool, staleDelegate },
                AllowedToolNames = new[] { "host_tool" }
            };

            AiTaskRequest lead = session.CreateLeadRequest(host, plan);

            Assert.AreEqual(BuiltInAgentRoleIds.Creator, lead.RoleId);
            Assert.AreEqual("castle", lead.Hint);
            Assert.AreEqual("host-trace", lead.TraceId, "A host trace is kept.");
            Assert.AreEqual(AiTaskHistoryMode.RoleDefault, lead.HistoryMode, "The lead keeps its conversation.");
            Assert.AreEqual("Host tail.\n\nLEAD RULES", lead.RequestSystemInstructions);
            CollectionAssert.AreEqual(new[] { hostTool, session.DelegateTool, session.DeclareTool }, lead.RequestTools,
                "Host tools stay, and the session's own lead tools replace a stale one of the same name.");
            CollectionAssert.AreEqual(
                new[] { "host_tool", DelegateTaskTool.DefaultName, DeclareTasksTool.DefaultName }, lead.AllowedToolNames,
                "A host allowlist keeps its names and gains the lead tools, so delegation is never filtered away.");
            Assert.AreEqual(2, host.RequestTools.Count, "The host's request is not modified.");
            CollectionAssert.AreEqual(new[] { "host_tool" }, host.AllowedToolNames);
            Assert.AreEqual("Host tail.", host.RequestSystemInstructions);
        }

        [Test]
        public void SessionRegistry_HandsItsLeadInstructionsToEverySession()
        {
            OrchestratorSessionRegistry registry = new(new OrchestratorModeOptions(),
                new SubAgentRegistry(new[] { SubAgentTestSupport.Builder() }), new ScriptedSubAgentRunner(),
                leadInstructions: new FixedInstructions("REGISTRY RULES"));
            OrchestratorSession session = registry.GetOrCreate("conversation-7");
            OrchestrationPlan plan = session.BeginPlan("castle", SubAgentTestSupport.Parent());

            AiTaskRequest lead = session.CreateLeadRequest(new AiTaskRequest { Hint = "castle" }, plan);

            Assert.AreEqual("REGISTRY RULES", lead.RequestSystemInstructions);
            Assert.AreEqual(SubAgentTestSupport.LeadTrace, lead.TraceId, "A request without a trace takes the plan's.");
        }

        private sealed class FixedInstructions : ILeadInstructionsProvider
        {
            private readonly string _text;

            public FixedInstructions(string text)
            {
                _text = text;
            }

            public string BuildInstructions(LeadInstructionsContext context) => _text;
        }
    }
}
