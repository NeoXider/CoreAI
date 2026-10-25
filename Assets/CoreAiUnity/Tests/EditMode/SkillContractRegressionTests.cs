using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.AgentMemory;
using CoreAI.Ai;
using Microsoft.Extensions.AI;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode
{
    public sealed class SkillContractRegressionTests
    {
        private SynchronizationContext _previous;
        [SetUp] public void SetUp() { _previous = SynchronizationContext.Current; SynchronizationContext.SetSynchronizationContext(null); }
        [TearDown] public void TearDown() { SynchronizationContext.SetSynchronizationContext(_previous); }

        [Test]
        public async Task Authoring_CallableAlias_SurvivesRestartWithoutGrantingSibling()
        {
            FunctionPair wrapper = new();
            SavedSkills saved = new();
            InlineAsyncSkillStoreAdapter store = new(saved);
            AgentMemoryPolicy policy = new();
            AgentConfig config = new AgentBuilder("role") { SuppressBuildWarnings = true }
                .WithTool(wrapper).WithSkillAuthoring(store).BuildDetached();
            await config.ApplyToPolicyAsync(policy);
            ManageSkillsLlmTool manage = policy.GetToolsForRole("role").OfType<ManageSkillsLlmTool>().Single();
            JObject created = JObject.Parse(await manage.ExecuteAsync("create", "search", "find", "Search the scene.", "[\"find_objects\"]"));
            Assert.IsTrue(created.Value<bool>("success"), created.ToString());
            Assert.IsTrue(saved.TryLoad("search", out SkillRecord record));
            CollectionAssert.AreEqual(new[] { "find_objects" }, record.ToolNames);

            AgentMemoryPolicy restarted = new();
            await new AgentBuilder("role") { SuppressBuildWarnings = true }
                .WithTool(wrapper).WithSkillAuthoring(store).BuildDetached().ApplyToPolicyAsync(restarted);
            ILlmTool proxy = CallSkillToolLlmTool.Create(restarted.GetSkillsForRole("role"));
            IResolvedLlmToolCallProvider resolver = (IResolvedLlmToolCallProvider)proxy;
            Assert.IsTrue(resolver.TryResolveInvocation(Call("find_objects", "{\"query\":\"lamp\"}"), out ResolvedLlmToolInvocation invocation, out string error), error);
            await invocation.InvokeAsync(CancellationToken.None);
            Assert.AreEqual(1, wrapper.FindCalls);
            Assert.IsFalse(resolver.TryResolveInvocation(Call("get_hierarchy", "{\"root\":1}"), out _, out _));
            Assert.AreEqual(0, wrapper.HierarchyCalls);
        }

        [Test]
        public async Task Authoring_HostWrapperInstructionEdit_PreservesCallableBindings()
        {
            FunctionPair wrapper = new();
            AgentMemoryPolicy policy = new();
            await new AgentBuilder("role") { SuppressBuildWarnings = true }
                .WithSkill(new SkillSet("host", "scene", "Old instructions", wrapper))
                .WithSkillAuthoring().BuildDetached().ApplyToPolicyAsync(policy);
            ManageSkillsLlmTool manage = policy.GetToolsForRole("role").OfType<ManageSkillsLlmTool>().Single();
            JObject edited = JObject.Parse(await manage.ExecuteAsync("update", "host", instructions: "New instructions"));
            Assert.IsTrue(edited.Value<bool>("success"), edited.ToString());
            SkillSet skill = policy.GetSkillsForRole("role").Single(s => s.Name == "host");
            CollectionAssert.AreEquivalent(new[] { "find_objects", "get_hierarchy" }, skill.ToolNames);
            Assert.AreEqual("New instructions", skill.Instructions);
        }

        [Test]
        public async Task MemoryProxy_BindingIsInvocationLocal_AndRetainsRestriction()
        {
            MemoryLlmTool memory = new();
            ILlmTool original = CallSkillToolLlmTool.Create(new[] { new SkillSet("notes", "d", "Use memory", memory) });
            InMemoryAgentMemoryStore firstStore = new();
            InMemoryAgentMemoryStore secondStore = new();
            ILlmTool first = CallSkillToolLlmTool.BindForRole(original, firstStore, "first");
            ILlmTool second = CallSkillToolLlmTool.BindForRole(original, secondStore, "second");
            Assert.IsFalse(((IResolvedLlmToolCallProvider)original).TryResolveInvocation(Call("memory", "{\"action\":\"append\",\"content\":\"fact\"}"), out _, out _));
            Assert.IsTrue(((IResolvedLlmToolCallProvider)first).TryResolveInvocation(Call("memory", "{\"action\":\"append\",\"content\":\"first fact\"}"), out ResolvedLlmToolInvocation invocation, out string error), error);
            Assert.AreSame(memory, invocation.SourceTool);
            await invocation.InvokeAsync(CancellationToken.None);
            Assert.IsTrue(firstStore.TryLoad("first", out AgentMemoryState firstState));
            StringAssert.Contains("first fact", firstState.Memory);
            Assert.IsFalse(secondStore.TryLoad("second", out _));
            ILlmTool denied = ((ISkillSetMetaLlmTool)second).RestrictTo(new[] { "other" });
            Assert.IsFalse(((IResolvedLlmToolCallProvider)denied).TryResolveInvocation(Call("memory", "{\"action\":\"read\"}"), out _, out _));
        }

        [Test]
        public void ContextBudget_ExpandedSchemas_CountsEveryFunctionAndOnlyAllowedBindings()
        {
            FunctionPair wrapper = new();
            CharEstimator estimator = new();
            AIFunction[] functions = wrapper.CreateAIFunctions().ToArray();
            int expected = functions.Sum(function => function.Name.Length + function.Description.Length + function.JsonSchema.ToString().Length + 8);
            DefaultContextBudgetPolicy policy = new();
            ContextBudget full = policy.Compute(new ContextBudgetRequest { MaxContextTokens = 8000, Tools = new ILlmTool[] { wrapper } }, estimator);
            Assert.AreEqual(expected, full.EstimatedFixedPromptTokens);
            ILlmTool narrowed = SkillSetToolResolver.RestrictToAllowedFunctions(wrapper, new HashSet<string>(StringComparer.Ordinal) { "find_objects" });
            ContextBudget restricted = policy.Compute(new ContextBudgetRequest { MaxContextTokens = 8000, Tools = new[] { narrowed } }, estimator);
            AIFunction one = functions.Single(function => function.Name == "find_objects");
            Assert.AreEqual(one.Name.Length + one.Description.Length + one.JsonSchema.ToString().Length + 8, restricted.EstimatedFixedPromptTokens);
            Assert.Greater(restricted.HistoryTokenBudget, full.HistoryTokenBudget);
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task PreparedReplacement_ClearsPromptPrefixOptOutAndCatalog(bool asynchronous)
        {
            AgentMemoryPolicy policy = new();
            AgentConfig first = new AgentBuilder("role") { SuppressBuildWarnings = true }
                .WithSystemPrompt("obsolete instructions").WithOverrideUniversalPrefix(true)
                .WithSkill(new SkillSet("old", "old skill", "old instructions")).BuildDetached();
            AgentConfig replacement = new AgentBuilder("role") { SuppressBuildWarnings = true }
                .WithSystemPrompt("").WithOverrideUniversalPrefix(false).BuildDetached();
            if (asynchronous) { await first.ApplyToPolicyAsync(policy); await replacement.ApplyToPolicyAsync(policy); }
            else { first.ApplyToPolicy(policy); replacement.ApplyToPolicy(policy); }
            Assert.IsFalse(policy.IsUniversalPrefixOverridden("role"));
            Assert.IsFalse(policy.TryGetAdditionalSystemPrompt("role", out _));
            Assert.IsEmpty(policy.GetSkillsForRole("role"));
            Assert.IsFalse(policy.GetToolsForRole("role").Any(tool => tool is ISkillSetMetaLlmTool));
            AiPromptComposer composer = new(new Prompts(), null, null, memoryPolicy: policy);
            StringAssert.DoesNotContain("obsolete instructions", composer.GetSystemPrompt("role"));
            StringAssert.DoesNotContain("old skill", composer.GetSystemPrompt("role"));
        }

        private static IDictionary<string, object> Call(string name, string json) => new Dictionary<string, object>
        { ["tool_name"] = name, ["arguments_json"] = json };
        private sealed class CharEstimator : ITokenEstimator { public int EstimateText(string text) => text?.Length ?? 0; }
        private sealed class Prompts : IAgentSystemPromptProvider
        { public bool TryGetSystemPrompt(string roleId, out string prompt) { prompt = "Base prompt"; return true; } }
        private sealed class SavedSkills : ISkillStore
        {
            private readonly Dictionary<string, SkillRecord> _records = new(StringComparer.OrdinalIgnoreCase);
            public void Save(SkillRecord record) { _records[record.Id] = record; }
            public bool TryLoad(string id, out SkillRecord record) => _records.TryGetValue(id, out record);
            public IReadOnlyList<SkillRecord> List() => _records.Values.ToArray();
            public void Delete(string id) { _records.Remove(id); }
        }
        private sealed class FunctionPair : LlmToolBase, IAIFunctionsLlmTool
        {
            internal int FindCalls;
            internal int HierarchyCalls;
            public override string Name => "scene_tool";
            public override string Description => "Scene wrapper";
            public override string ParametersSchema => "{}";
            public IEnumerable<AIFunction> CreateAIFunctions()
            {
                yield return AIFunctionFactory.Create((Func<string, string>)Find,
                    new AIFunctionFactoryOptions { Name = "find_objects", Description = new string('f', 400) });
                yield return AIFunctionFactory.Create((Func<int, string>)Hierarchy,
                    new AIFunctionFactoryOptions { Name = "get_hierarchy", Description = new string('h', 600) });
            }
            private string Find(string query) { FindCalls++; return "found " + query; }
            private string Hierarchy(int root) { HierarchyCalls++; return "root " + root; }
        }
    }
}
