using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Ai;
using CoreAI.AgentMemory;
using Microsoft.Extensions.AI;
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
    }
}
