using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Ai;
using CoreAI.AgentMemory;
using Microsoft.Extensions.AI;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode
{
    /// <summary>
    /// Pins the contract a skill's tools must honour: they are callable at any time, and only the
    /// skill's INSTRUCTIONS are deferred until <c>read_skill</c>.
    /// <para>
    /// WHY: a tool that silently stops being reachable is invisible — <c>call_skill_tool</c> answers a
    /// missing tool with an ordinary result, so the model reads "not found", apologises in prose and
    /// moves on. Nothing throws and nothing surfaces; the only symptom is that the action never
    /// happens. These tests exist so that failure mode cannot return unnoticed.
    /// </para>
    /// </summary>
    public sealed class SkillToolAvailabilityEditModeTests
    {
        private const string RoleId = "skill_availability_role";

        private SynchronizationContext _previousSynchronizationContext;

        /// <summary>
        /// WHY: <see cref="ResolvedInvocation_PreCancelled_DoesNotEnterToolBody"/> below is a synchronous
        /// [Test] that blocks on Assert.CatchAsync; detaching the context sends the awaited delegate's
        /// continuation to the thread pool instead of back onto this same blocked thread, which would
        /// deadlock.
        /// </summary>
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

        private static DelegateLlmTool CountingTool(string name, Action onCall)
        {
            return new DelegateLlmTool(name, "Test tool: " + name, onCall);
        }

        private static ILlmTool FindTool(IReadOnlyList<ILlmTool> tools, string name)
        {
            if (tools == null)
            {
                return null;
            }

            foreach (ILlmTool tool in tools)
            {
                if (tool != null && string.Equals(tool.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    return tool;
                }
            }

            return null;
        }

        private static async Task<string> CallSkillToolAsync(ILlmTool proxy, string toolName)
        {
            AIFunction function = ((IAIFunctionLlmTool)proxy).CreateAIFunction();
            object result = await function.InvokeAsync(
                new AIFunctionArguments(new Dictionary<string, object>
                {
                    ["tool_name"] = toolName,
                    ["arguments_json"] = "{}"
                }),
                CancellationToken.None);
            return result?.ToString();
        }

        private static AgentMemoryPolicy BuildPolicyWithSkill(SkillSet skill)
        {
            AgentConfig config = new AgentBuilder(RoleId)
                {
                    SuppressBuildWarnings = true
                }
                .WithSkill(skill)
                .WithMode(AgentMode.ToolsAndChat)
                .Build();

            AgentMemoryPolicy policy = new();
            config.ApplyToPolicy(policy);
            return policy;
        }

        [Test]
        public async Task CallSkillTool_WithoutEverReadingTheSkill_StillInvokesTheTool()
        {
            int calls = 0;
            SkillSet skill = new("Crafting", "Forge weapons",
                "1. Call forge_item to craft.", CountingTool("forge_item", () => calls++));

            AgentMemoryPolicy policy = BuildPolicyWithSkill(skill);
            ILlmTool proxy = FindTool(policy.GetToolsForRole(RoleId), "call_skill_tool");
            Assert.IsNotNull(proxy, "call_skill_tool must be registered for a role that has skills.");

            // No read_skill anywhere in this test: the deferred half is the INSTRUCTIONS, never the
            // ability to invoke.
            string response = await CallSkillToolAsync(proxy, "forge_item");

            Assert.AreEqual(1, calls,
                "a skill tool must be invocable without read_skill ever having been called; " +
                "response was: " + response);
        }

        [Test]
        public async Task ReadingOneSkill_DoesNotGateAnotherSkillsTools()
        {
            int crafted = 0;
            int fought = 0;
            SkillSet crafting = new("Crafting", "Forge weapons", "Use forge_item.",
                CountingTool("forge_item", () => crafted++));
            SkillSet combat = new("Combat", "Fight enemies", "Use strike.",
                CountingTool("strike", () => fought++));

            AgentConfig config = new AgentBuilder(RoleId)
                {
                    SuppressBuildWarnings = true
                }
                .WithSkills(crafting, combat)
                .WithMode(AgentMode.ToolsAndChat)
                .Build();
            AgentMemoryPolicy policy = new();
            config.ApplyToPolicy(policy);

            IReadOnlyList<ILlmTool> tools = policy.GetToolsForRole(RoleId);
            ILlmTool readSkill = FindTool(tools, "read_skill");
            ILlmTool proxy = FindTool(tools, "call_skill_tool");
            Assert.IsNotNull(readSkill);
            Assert.IsNotNull(proxy);

            AIFunction readFunction = ((IAIFunctionLlmTool)readSkill).CreateAIFunction();
            await readFunction.InvokeAsync(
                new AIFunctionArguments(new Dictionary<string, object> { ["skill_name"] = "Crafting" }),
                CancellationToken.None);

            await CallSkillToolAsync(proxy, "strike");

            Assert.AreEqual(1, fought,
                "reading one skill must not make another skill's tools unreachable");
        }

        [Test]
        public void SkillCatalog_CarriesNamesAndDescriptions_ButNotInstructions()
        {
            SkillSet skill = new("Crafting", "Forge weapons and armor",
                "SECRET_PROCEDURE_BODY: call forge_item then temper_blade.",
                CountingTool("forge_item", () => { }));

            string catalog = SkillSet.BuildCatalog(new[] { skill });

            Assert.That(catalog, Does.Contain("Crafting"));
            Assert.That(catalog, Does.Contain("Forge weapons and armor"));
            Assert.That(catalog, Does.Not.Contain("SECRET_PROCEDURE_BODY"),
                "the catalog is the cheap half: instructions must arrive only through read_skill");
        }

        [Test]
        public async Task SkillAddedAfterTheProxyWasBuilt_IsCallableWithoutReadingIt()
        {
            // WHY: this is the authoring path — the model writes a skill for itself mid-session. The
            // proxy was constructed before that skill existed, so a map cached at construction would
            // answer "not found" for a tool the catalog already advertises.
            int calls = 0;
            MutableSkillCatalog catalog = new();
            ILlmTool proxy = CallSkillToolLlmTool.Create(catalog);

            catalog.AddOrReplace(new SkillSet("LateSkill", "Authored mid-session",
                "Use late_tool.", CountingTool("late_tool", () => calls++)));

            string response = await CallSkillToolAsync(proxy, "late_tool");

            Assert.AreEqual(1, calls,
                "a skill registered after the proxy was built must still be invocable; " +
                "response was: " + response);
        }

        [Test]
        public async Task ReplacingARolesTools_KeepsTheSkillMetaToolsReachable()
        {
            // WHY: a host that later calls SetToolsForRole — adding a world tool, swapping a debug
            // tool — rebuilds the role's tool list wholesale. If the skill proxies are not restored
            // with it, every skill the agent was built with silently stops being callable.
            int calls = 0;
            SkillSet skill = new("Crafting", "Forge weapons", "Use forge_item.",
                CountingTool("forge_item", () => calls++));

            AgentMemoryPolicy policy = BuildPolicyWithSkill(skill);
            policy.SetToolsForRole(RoleId, new ILlmTool[]
            {
                CountingTool("unrelated_world_tool", () => { })
            });

            IReadOnlyList<ILlmTool> tools = policy.GetToolsForRole(RoleId);
            ILlmTool proxy = FindTool(tools, "call_skill_tool");
            Assert.IsNotNull(proxy,
                "call_skill_tool must survive a tool-list replacement, or every skill drops off");
            Assert.IsNotNull(FindTool(tools, "read_skill"),
                "read_skill must survive a tool-list replacement");

            string response = await CallSkillToolAsync(proxy, "forge_item");
            Assert.AreEqual(1, calls,
                "the skill's tools must still run after the role's tool list was replaced; " +
                "response was: " + response);
        }

        [Test]
        public async Task ResolvedInvocation_KeepsBindingAndArgumentsAcrossCatalogReplacement()
        {
            string observed = null;
            DelegateLlmTool first = new("capture", "capture", new Action<string>(value => observed = value));
            MutableSkillCatalog catalog = new(new[] { new SkillSet("capture-skill", "", "first", first) });
            IResolvedLlmToolCallProvider proxy = (IResolvedLlmToolCallProvider)CallSkillToolLlmTool.Create(catalog);
            Dictionary<string, object> arguments = new()
            {
                ["tool_name"] = "capture", ["arguments_json"] = "{\"value\":\"original\"}"
            };
            Assert.IsTrue(proxy.TryResolveInvocation(arguments, out ResolvedLlmToolInvocation invocation, out string error), error);
            arguments["arguments_json"] = "{\"value\":\"changed\"}";
            catalog.AddOrReplace(new SkillSet("capture-skill", "", "second",
                new DelegateLlmTool("capture", "capture", new Action<string>(_ => observed = "wrong binding"))));
            Assert.AreSame(first, invocation.SourceTool);
            await invocation.InvokeAsync(CancellationToken.None);
            Assert.AreEqual("original", observed);
        }

        [Test]
        public async Task RestrictedLiveProxy_DoesNotGainNewPermissionsOrPermitWidening()
        {
            int allowedCalls = 0;
            int deniedCalls = 0;
            MutableSkillCatalog catalog = new();
            catalog.AddOrReplace(new SkillSet("allowed", "", "", CountingTool("safe", () => allowedCalls++)));
            ILlmTool proxy = ((ISkillSetMetaLlmTool)CallSkillToolLlmTool.Create(catalog)).RestrictTo(new[] { "safe" });
            catalog.AddOrReplace(new SkillSet("added", "", "", CountingTool("blocked", () => deniedCalls++)));
            ILlmTool attemptedWidening = ((ISkillSetMetaLlmTool)proxy).RestrictTo(new[] { "safe", "blocked" });
            await CallSkillToolAsync(attemptedWidening, "safe");
            await CallSkillToolAsync(attemptedWidening, "blocked");
            Assert.AreEqual(1, allowedCalls);
            Assert.AreEqual(0, deniedCalls);
            ILlmTool empty = ((ISkillSetMetaLlmTool)proxy).RestrictTo(Array.Empty<string>());
            await CallSkillToolAsync(empty, "safe");
            Assert.AreEqual(1, allowedCalls);
        }

        [Test]
        public async Task ExistingReadersAndCallers_ObserveUpdateAndRemoval()
        {
            int calls = 0;
            MutableSkillCatalog catalog = new();
            ILlmTool caller = CallSkillToolLlmTool.Create(catalog);
            AIFunction reader = ((IAIFunctionLlmTool)ReadSkillLlmTool.Create(catalog)).CreateAIFunction();
            DelegateLlmTool target = CountingTool("live", () => calls++);
            catalog.AddOrReplace(new SkillSet("live-skill", "", "old", target));
            catalog.AddOrReplace(new SkillSet("live-skill", "", "new", target));
            object read = await reader.InvokeAsync(new AIFunctionArguments(new Dictionary<string, object>
                { ["skill_name"] = "live-skill" }), CancellationToken.None);
            Assert.AreEqual("new", Newtonsoft.Json.Linq.JObject.Parse(read.ToString())["instructions"].Value<string>());
            await CallSkillToolAsync(caller, "live");
            catalog.Remove("live-skill");
            await CallSkillToolAsync(caller, "live");
            Assert.AreEqual(1, calls);
            object missing = await reader.InvokeAsync(new AIFunctionArguments(new Dictionary<string, object>
                { ["skill_name"] = "live-skill" }), CancellationToken.None);
            Assert.IsFalse(Newtonsoft.Json.Linq.JObject.Parse(missing.ToString())["success"].Value<bool>());
        }

        [Test]
        public void ConflictingSkillTools_AreRejectedBeforeCatalogPublication()
        {
            SkillSet first = new("first", "", "", CountingTool("shared", () => { }));
            SkillSet conflict = new("second", "", "", CountingTool("shared", () => { }));
            Assert.Throws<ArgumentException>(() => ReadSkillLlmTool.Create(new[] { first, conflict }));
            Assert.Throws<ArgumentException>(() => CallSkillToolLlmTool.Create(new[] { first, conflict }));
            MutableSkillCatalog catalog = new(new[] { first });
            Assert.Throws<ArgumentException>(() => catalog.AddOrReplace(conflict));
            Assert.IsNull(catalog.Get("second"));
            Assert.AreSame(first, catalog.Get("first"));
        }

        [Test]
        public void ResolvedInvocation_PreCancelled_DoesNotEnterToolBody()
        {
            int calls = 0;
            ILlmTool caller = CallSkillToolLlmTool.Create(new[]
            {
                new SkillSet("cancel", "", "", CountingTool("mutate", () => calls++))
            });
            Assert.IsTrue(((IResolvedLlmToolCallProvider)caller).TryResolveInvocation(
                new Dictionary<string, object> { ["tool_name"] = "mutate", ["arguments_json"] = "{}" },
                out ResolvedLlmToolInvocation invocation, out string error), error);
            // WHY CatchAsync and not ThrowsAsync: ThrowsAsync demands the EXACT type, and a cancelled
            // await legitimately surfaces TaskCanceledException, which derives from
            // OperationCanceledException. This test is about the call being cancelled, not which of
            // the two the runtime happened to pick.
            Assert.CatchAsync<OperationCanceledException>(async () => await invocation.InvokeAsync(new CancellationToken(true)));
            Assert.AreEqual(0, calls);
        }

        [TestCase("[]")]
        [TestCase("null")]
        [TestCase("broken")]
        public void MalformedProxyArguments_AreRejectedBeforeBinding(string json)
        {
            int calls = 0;
            ILlmTool caller = CallSkillToolLlmTool.Create(new[]
            {
                new SkillSet("parse", "", "", CountingTool("mutate", () => calls++))
            });
            Assert.IsFalse(((IResolvedLlmToolCallProvider)caller).TryResolveInvocation(
                new Dictionary<string, object> { ["tool_name"] = "mutate", ["arguments_json"] = json },
                out ResolvedLlmToolInvocation invocation, out string error));
            Assert.IsNull(invocation);
            Assert.IsNotEmpty(error);
            Assert.AreEqual(0, calls);
        }
    }
}
