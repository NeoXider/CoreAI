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
        public async Task BoundSkillProxies_ShareOneDescriptorFactoryPerRoleBinding()
        {
            CountingBindingTool tool = new CountingBindingTool("first binding");
            SkillSet[] catalog = { new SkillSet("tracked", "Tracked skill", "Use the tracked tool.", tool) };
            ILlmTool[] original = { ReadSkillLlmTool.Create(catalog), CallSkillToolLlmTool.Create(catalog) };
            tool.FactoryCalls = 0;
            IReadOnlyList<ILlmTool> first = CallSkillToolLlmTool.BindToolsForRole(original,
                new InMemoryAgentMemoryStore(), "first");
            Assert.AreEqual(1, tool.FactoryCalls,
                "Paired discovery and invocation must reuse the same invocation-owned descriptors.");
            JObject discovery = await ReadSkill(first[0], "tracked");
            Assert.IsTrue(discovery.Value<bool>("success"));
            Assert.IsTrue(discovery["tools"].Single().Value<bool>("invocable"));
            Assert.AreEqual("first binding", discovery["tools"].Single().Value<string>("description"));
            Assert.IsTrue(((IResolvedLlmToolCallProvider)first[1]).TryResolveInvocation(Call("tracked_tool", "{}"),
                out ResolvedLlmToolInvocation invocation, out string error), error);
            Assert.AreEqual("first binding", (await invocation.InvokeAsync(CancellationToken.None)).ToString());
            await ReadSkill(first[0], "tracked");
            Assert.AreEqual(1, tool.FactoryCalls);
            IAIFunctionLlmTool callProxy = (IAIFunctionLlmTool)first[1];
            Assert.AreSame(callProxy.CreateAIFunction(), callProxy.CreateAIFunction(),
                "Repeated exposure of one invocation proxy must reuse its root function.");

            ILlmTool deniedRead = ((ISkillSetMetaLlmTool)first[0]).RestrictTo(new[] { "other" });
            ILlmTool deniedCall = ((ISkillSetMetaLlmTool)first[1]).RestrictTo(new[] { "other" });
            Assert.IsFalse((await ReadSkill(deniedRead, "tracked")).Value<bool>("success"));
            Assert.IsFalse(((IResolvedLlmToolCallProvider)deniedCall).TryResolveInvocation(
                Call("tracked_tool", "{}"), out _, out _));
            Assert.AreEqual(1, tool.FactoryCalls, "Restricting a bound proxy must retain its descriptor cache.");

            IReadOnlyList<ILlmTool> second = CallSkillToolLlmTool.BindToolsForRole(original,
                new InMemoryAgentMemoryStore(), "second");
            Assert.AreEqual(2, tool.FactoryCalls, "Another admitted role owns a separate descriptor binding.");
            Assert.IsTrue((await ReadSkill(second[0], "tracked")).Value<bool>("success"));
            Assert.AreEqual(2, tool.FactoryCalls);
            Assert.AreEqual(1, tool.BodyCalls);
        }

        [Test]
        public async Task BoundLiveSkillProxies_RefreshReplacementAndRemovalWhileKeepingCapturedInvocations()
        {
            CountingBindingTool originalTool = new CountingBindingTool("original body");
            MutableSkillCatalog catalog = new MutableSkillCatalog(new[]
                { new SkillSet("tracked", "Tracked skill", "Original instructions", originalTool) });
            ILlmTool[] original = { ReadSkillLlmTool.Create(catalog), CallSkillToolLlmTool.Create(catalog) };
            IReadOnlyList<ILlmTool> bound = CallSkillToolLlmTool.BindToolsForRole(original,
                new InMemoryAgentMemoryStore(), "role");
            originalTool.FactoryCalls = 0;
            JObject before = await ReadSkill(bound[0], "tracked");
            Assert.AreEqual("Original instructions", before.Value<string>("instructions"));
            IResolvedLlmToolCallProvider resolver = (IResolvedLlmToolCallProvider)bound[1];
            Assert.IsTrue(resolver.TryResolveInvocation(Call("tracked_tool", "{}"),
                out ResolvedLlmToolInvocation capturedOriginal, out string originalError), originalError);
            Assert.AreEqual(1, originalTool.FactoryCalls);

            CountingBindingTool replacement = new CountingBindingTool("replacement body");
            catalog.AddOrReplace(new SkillSet("tracked", "Tracked skill", "Replacement instructions", replacement));
            replacement.FactoryCalls = 0;
            JObject after = await ReadSkill(bound[0], "tracked");
            Assert.AreEqual("Replacement instructions", after.Value<string>("instructions"));
            Assert.AreEqual("replacement body", after["tools"].Single().Value<string>("description"));
            Assert.IsTrue(resolver.TryResolveInvocation(Call("tracked_tool", "{}"),
                out ResolvedLlmToolInvocation capturedReplacement, out string replacementError), replacementError);
            Assert.AreEqual(1, replacement.FactoryCalls);
            Assert.AreEqual("original body", (await capturedOriginal.InvokeAsync(CancellationToken.None)).ToString());
            Assert.AreEqual("replacement body", (await capturedReplacement.InvokeAsync(CancellationToken.None)).ToString());
            Assert.AreEqual(1, originalTool.BodyCalls);
            Assert.AreEqual(1, replacement.BodyCalls);

            Assert.IsTrue(catalog.Remove("tracked"));
            Assert.IsFalse((await ReadSkill(bound[0], "tracked")).Value<bool>("success"));
            Assert.IsFalse(resolver.TryResolveInvocation(Call("tracked_tool", "{}"), out _, out _));
            using CancellationTokenSource cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            Assert.CatchAsync<OperationCanceledException>(async () => { await capturedReplacement.InvokeAsync(cancelled.Token); });
            Assert.AreEqual(1, replacement.BodyCalls, "A cancelled captured call must not enter the removed tool's body.");
            Assert.AreEqual("replacement body", (await capturedReplacement.InvokeAsync(CancellationToken.None)).ToString());
            Assert.AreEqual(2, replacement.BodyCalls);
            Assert.AreEqual(1, replacement.FactoryCalls);
            Assert.AreEqual(1, originalTool.FactoryCalls);
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
        private static async Task<JObject> ReadSkill(ILlmTool proxy, string skillName)
        {
            AIFunctionArguments arguments = new AIFunctionArguments(new Dictionary<string, object> { ["skill_name"] = skillName });
            object result = await ((IAIFunctionLlmTool)proxy).CreateAIFunction().InvokeAsync(arguments, CancellationToken.None);
            return JObject.Parse(result.ToString());
        }
        private sealed class CountingBindingTool : LlmToolBase, IAIFunctionLlmTool
        {
            private readonly string _result;
            internal int FactoryCalls;
            internal int BodyCalls;
            internal CountingBindingTool(string result) { _result = result; }
            public override string Name => "tracked_tool";
            public override string Description => "A counted skill binding.";
            public override string ParametersSchema => "{}";
            public AIFunction CreateAIFunction()
            {
                FactoryCalls++;
                return AIFunctionFactory.Create((Func<string>)Invoke,
                    new AIFunctionFactoryOptions { Name = Name, Description = _result });
            }
            private string Invoke() { BodyCalls++; return _result; }
        }
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
