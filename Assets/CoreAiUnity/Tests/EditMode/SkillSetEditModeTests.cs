using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Ai;
using CoreAI.AgentMemory;
using Microsoft.Extensions.AI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode
{
    /// <summary>
    /// EditMode tests for <see cref="SkillSet"/> — self-service skill pattern.
    /// Validates construction, catalog generation, read_skill meta-tool, and AgentBuilder integration.
    /// </summary>
    public sealed class SkillSetEditModeTests
    {
        // ── Helpers ───────────────────────────────────────────────────────────

        private static DelegateLlmTool MakeTool(string name)
        {
            return new DelegateLlmTool(name, $"Test tool: {name}", new Action(() => { }));
        }

        private static async Task<string> InvokeReadSkillAsync(ILlmTool tool, string skillName)
        {
            AIFunction function = ((IAIFunctionLlmTool)tool).CreateAIFunction();
            object result = await function.InvokeAsync(
                new AIFunctionArguments(new Dictionary<string, object>
                {
                    ["skill_name"] = skillName
                }),
                CancellationToken.None);
            return result?.ToString();
        }

        private static async Task<string> InvokeCallSkillToolAsync(ILlmTool tool, string toolName, string argumentsJson)
        {
            AIFunction function = ((IAIFunctionLlmTool)tool).CreateAIFunction();
            object result = await function.InvokeAsync(
                new AIFunctionArguments(new Dictionary<string, object>
                {
                    ["tool_name"] = toolName,
                    ["arguments_json"] = argumentsJson
                }),
                CancellationToken.None);
            return result?.ToString();
        }

        private static SkillSet MakeCraftingSkill()
        {
            return new SkillSet("Crafting",
                "Forge weapons and armor from raw materials",
                "1. Call get_recipes to list recipes.\n2. Call craft_item to craft.",
                MakeTool("get_recipes"), MakeTool("craft_item"));
        }

        private static SkillSet MakeCombatSkill()
        {
            return new SkillSet("Combat",
                "Fight enemies and manage encounters",
                "Call get_enemy_stats before attacking. Use calculate_damage for hits.",
                MakeTool("get_enemy_stats"), MakeTool("calculate_damage"));
        }

        private static SkillSet MakeLoreSkill()
        {
            return new SkillSet("Lore",
                "World knowledge and history",
                "Call search_codex to find lore entries.",
                MakeTool("search_codex"));
        }

        // ══════════════════════════════════════════════════════════════════════
        //  Construction
        // ══════════════════════════════════════════════════════════════════════

        [Test]
        public void Constructor_WithAllParams_SetsProperties()
        {
            SkillSet skill = MakeCraftingSkill();
            Assert.AreEqual("Crafting", skill.Name);
            Assert.AreEqual("Forge weapons and armor from raw materials", skill.Description);
            Assert.That(skill.Instructions, Does.Contain("get_recipes"));
            Assert.AreEqual(2, skill.Tools.Count);
            Assert.AreEqual(2, skill.ToolNames.Length);
            Assert.AreEqual("get_recipes", skill.ToolNames[0]);
            Assert.AreEqual("craft_item", skill.ToolNames[1]);
        }

        [Test]
        public void Constructor_WithoutInstructions_SetsEmptyInstructions()
        {
            SkillSet skill = new("Simple", "A simple skill", MakeTool("tool1"));
            Assert.AreEqual("Simple", skill.Name);
            Assert.AreEqual("A simple skill", skill.Description);
            Assert.AreEqual("", skill.Instructions);
        }

        [Test]
        public void Constructor_AIFunctionsToolNames_UsesCallableFunctionNames()
        {
            SkillSet skill = new("Scene", "Scene access", "instructions", new MultiFunctionSkillTool());

            CollectionAssert.AreEqual(new[] { "find_objects", "get_hierarchy" }, skill.ToolNames);
        }

        [Test]
        public void Constructor_NullDescription_UsesNameAsDescription()
        {
            SkillSet skill = new("MySkill", null, "instructions", MakeTool("t"));
            Assert.AreEqual("MySkill", skill.Description);
        }

        [Test]
        public void Constructor_NullName_Throws()
        {
            Assert.Throws<ArgumentNullException>(() =>
                new SkillSet(null, "desc", "inst", MakeTool("t")));
        }

        [Test]
        public void Constructor_NoTools_CreatesInstructionOnlySkill()
        {
            SkillSet skill = new("Empty", "desc", "inst");
            Assert.AreEqual("Empty", skill.Name);
            Assert.AreEqual("desc", skill.Description);
            Assert.AreEqual("inst", skill.Instructions);
            Assert.AreEqual(0, skill.Tools.Count);
            Assert.AreEqual(0, skill.ToolNames.Length);
        }

        [Test]
        public void Constructor_NullToolsFiltered()
        {
            SkillSet skill = new("Test", "desc", "inst", MakeTool("a"), null, MakeTool("b"));
            Assert.AreEqual(2, skill.Tools.Count);
            Assert.AreEqual(2, skill.ToolNames.Length);
        }

        // ══════════════════════════════════════════════════════════════════════
        //  Catalog
        // ══════════════════════════════════════════════════════════════════════

        [Test]
        public void BuildCatalog_MultipleSkills_ContainsNamesAndDescriptions()
        {
            SkillSet crafting = MakeCraftingSkill();
            SkillSet combat = MakeCombatSkill();

            string catalog = SkillSet.BuildCatalog(new List<SkillSet> { crafting, combat });

            Assert.That(catalog, Does.Contain("Available Skills"));
            Assert.That(catalog, Does.Contain("read_skill"));
            Assert.That(catalog, Does.Contain("call_skill_tool"));
            Assert.That(catalog, Does.Contain("**Crafting**"));
            Assert.That(catalog, Does.Contain("Forge weapons and armor"));
            Assert.That(catalog, Does.Contain("**Combat**"));
            Assert.That(catalog, Does.Contain("Fight enemies"));

            // Individual tool names should NOT be in catalog (discovered via read_skill)
            Assert.That(catalog, Does.Not.Contain("get_recipes"));
            Assert.That(catalog, Does.Not.Contain("get_enemy_stats"));
        }

        [Test]
        public void BuildCatalog_Empty_ReturnsEmpty()
        {
            Assert.AreEqual("", SkillSet.BuildCatalog(new List<SkillSet>()));
            Assert.AreEqual("", SkillSet.BuildCatalog(null));
        }

        [Test]
        public void BuildCatalog_DoesNotContainFullInstructions()
        {
            SkillSet crafting = MakeCraftingSkill();
            string catalog = SkillSet.BuildCatalog(new List<SkillSet> { crafting });

            // Catalog should have name + description but NOT full instructions
            Assert.That(catalog, Does.Contain("Crafting"));
            Assert.That(catalog, Does.Not.Contain("Call get_recipes to list recipes"));
        }

        // ══════════════════════════════════════════════════════════════════════
        //  MergeToolNames
        // ══════════════════════════════════════════════════════════════════════

        [Test]
        public void MergeToolNames_MultipleSkills_MergesWithoutDuplicates()
        {
            SkillSet a = MakeCraftingSkill();
            SkillSet b = MakeCombatSkill();

            string[] merged = SkillSet.MergeToolNames(a, b);
            Assert.AreEqual(4, merged.Length);
            Assert.Contains("get_recipes", merged);
            Assert.Contains("craft_item", merged);
            Assert.Contains("get_enemy_stats", merged);
            Assert.Contains("calculate_damage", merged);
        }

        [Test]
        public void MergeToolNames_Empty_ReturnsEmpty()
        {
            Assert.AreEqual(0, SkillSet.MergeToolNames().Length);
            Assert.AreEqual(0, SkillSet.MergeToolNames(null).Length);
        }

        // ══════════════════════════════════════════════════════════════════════
        //  ReadSkillLlmTool
        // ══════════════════════════════════════════════════════════════════════

        [Test]
        public void ReadSkillTool_Create_ReturnsInvocableMetaTool()
        {
            List<SkillSet> skills = new() { MakeCraftingSkill(), MakeCombatSkill() };
            ILlmTool tool = ReadSkillLlmTool.Create(skills);

            Assert.AreEqual("read_skill", tool.Name);
            Assert.IsTrue(tool.AllowDuplicates);
            Assert.IsInstanceOf<IAIFunctionLlmTool>(tool);
        }

        [Test]
        public async Task ReadSkillTool_Execute_KnownSkill_ReturnsInstructions()
        {
            SkillSet crafting = MakeCraftingSkill();
            ILlmTool tool = ReadSkillLlmTool.Create(new List<SkillSet> { crafting });

            string json = await InvokeReadSkillAsync(tool, "Crafting");

            Assert.That(json, Does.Contain("Crafting"));
            Assert.That(json, Does.Contain("get_recipes"));
            Assert.That(json, Does.Contain("instructions"));
            Assert.That(json, Does.Contain("call_skill_tool"), "Should contain usage hint for call_skill_tool proxy.");
            Assert.That(json, Does.Contain("tool_name"), "Should contain tool_name field in schema.");
        }

        [Test]
        public async Task ReadSkillTool_Execute_AIFunctionsTool_ListsCallableFunctionNames()
        {
            SkillSet skill = new("Scene", "Scene access", "Use scene tools.", new MultiFunctionSkillTool());
            ILlmTool tool = ReadSkillLlmTool.Create(new List<SkillSet> { skill });

            string json = await InvokeReadSkillAsync(tool, "Scene");

            Assert.That(json, Does.Contain("find_objects"));
            Assert.That(json, Does.Contain("get_hierarchy"));
            Assert.That(json, Does.Not.Contain("\"tool_name\":\"scene_tool\""),
                "read_skill must expose callable function names, not only the multi-tool container name.");
            Assert.That(json, Does.Contain("parameters_schema"));
        }

        [Test]
        public async Task ReadSkillTool_Execute_UnknownSkill_ReturnsError()
        {
            ILlmTool tool = ReadSkillLlmTool.Create(new List<SkillSet> { MakeCraftingSkill() });
            string json = await InvokeReadSkillAsync(tool, "NonExistent");

            Assert.That(json, Does.Contain("error"));
            Assert.That(json, Does.Contain("not found"));
            Assert.IsFalse(JObject.Parse(json).Value<bool>("success"));
        }

        [Test]
        public async Task ReadSkillTool_Execute_CaseInsensitive()
        {
            ILlmTool tool = ReadSkillLlmTool.Create(new List<SkillSet> { MakeCraftingSkill() });
            string json = await InvokeReadSkillAsync(tool, "crafting"); // lowercase

            Assert.That(json, Does.Contain("Crafting"));
            Assert.That(json, Does.Contain("instructions"));
        }

        [Test]
        public async Task ReadSkillTool_Execute_EmptyName_ReturnsError()
        {
            ILlmTool tool = ReadSkillLlmTool.Create(new List<SkillSet> { MakeCraftingSkill() });
            string json = await InvokeReadSkillAsync(tool, "");

            Assert.That(json, Does.Contain("error"));
            Assert.IsFalse(JObject.Parse(json).Value<bool>("success"));
        }

        // ══════════════════════════════════════════════════════════════════════
        //  CallSkillToolLlmTool (proxy)
        // ══════════════════════════════════════════════════════════════════════

        [Test]
        public void CallSkillTool_Create_ReturnsInvocableMetaTool()
        {
            List<SkillSet> skills = new() { MakeCraftingSkill() };
            ILlmTool tool = CallSkillToolLlmTool.Create(skills);

            Assert.AreEqual("call_skill_tool", tool.Name);
            // call_skill_tool dispatches to arbitrary skill tools whose effects the policy cannot
            // classify, so it participates in duplicate tracking (cross-turn echo suppression).
            Assert.IsFalse(tool.AllowDuplicates);
            Assert.IsInstanceOf<IAIFunctionLlmTool>(tool);
        }

        [Test]
        public async Task CallSkillTool_Execute_UnknownTool_ReturnsError()
        {
            ILlmTool tool = CallSkillToolLlmTool.Create(new List<SkillSet> { MakeCraftingSkill() });
            string json = await InvokeCallSkillToolAsync(tool, "nonexistent", "{}");

            Assert.That(JObject.Parse(json).Value<string>("error"), Is.Not.Null.And.Not.Empty);
            Assert.IsFalse(JObject.Parse(json).Value<bool>("success"));
        }

        [Test]
        public async Task CallSkillTool_Execute_KnownTool_Invokes()
        {
            bool called = false;
            DelegateLlmTool inner = new("test_tool", "A test",
                new Func<string, object>(x =>
                {
                    called = true;
                    return new { echo = x };
                }));
            SkillSet skill = new("TestSkill", "Test", "instructions", inner);

            ILlmTool proxy = CallSkillToolLlmTool.Create(new List<SkillSet> { skill });
            string json = await InvokeCallSkillToolAsync(proxy, "test_tool", "{\"x\": \"hello\"}");

            Assert.IsTrue(called, "Inner tool should have been called.");
            Assert.That(json, Does.Contain("hello"));
        }

        [Test]
        public async Task CallSkillTool_Execute_DelegateAction_ReturnsExplicitSuccess()
        {
            bool called = false;
            DelegateLlmTool inner = new("mark_done", "Marks the action done", new Action(() => called = true));
            SkillSet skill = new("Actions", "Action tools", "Use mark_done when ready.", inner);

            ILlmTool proxy = CallSkillToolLlmTool.Create(new List<SkillSet> { skill });
            string json = await InvokeCallSkillToolAsync(proxy, "mark_done", "{}");

            Assert.IsTrue(called, "Action tool should have been called.");
            Assert.IsTrue(JObject.Parse(json).Value<bool>("success"),
                "Void/empty action results must still produce an explicit model-visible success result.");
        }

        [Test]
        public async Task CallSkillTool_Execute_JsonInvocableTool_InvokesWithoutFunctionBinding()
        {
            JsonInvocableSkillTool inner = new();
            SkillSet skill = new("JsonActions", "JSON action tools", "Use json_action with payload.", inner);

            ILlmTool proxy = CallSkillToolLlmTool.Create(new List<SkillSet> { skill });
            string json = await InvokeCallSkillToolAsync(proxy, "json_action", "{\"value\":\"payload\"}");

            Assert.IsTrue(inner.Called, "IJsonInvocableLlmTool should be invoked directly by the skill proxy.");
            Assert.That(json, Does.Contain("payload"));
        }

        [Test]
        public async Task CallSkillTool_Execute_AIFunctionTool_Invokes()
        {
            ExplicitFunctionSkillTool inner = new("explicit_tool");
            SkillSet skill = new("TestSkill", "Test", "instructions", inner);

            ILlmTool proxy = CallSkillToolLlmTool.Create(new List<SkillSet> { skill });
            string json = await InvokeCallSkillToolAsync(proxy, "explicit_tool", "{\"value\":\"hello\"}");

            Assert.IsTrue(inner.Called, "Explicit IAIFunctionLlmTool should have been called.");
            Assert.That(json, Does.Contain("hello"));
        }

        [Test]
        public async Task CallSkillTool_Execute_AIFunctionsTool_InvokesFunctionName()
        {
            MultiFunctionSkillTool inner = new();
            SkillSet skill = new("Scene", "Scene access", "instructions", inner);

            ILlmTool proxy = CallSkillToolLlmTool.Create(new List<SkillSet> { skill });
            string json = await InvokeCallSkillToolAsync(proxy, "find_objects", "{\"query\":\"Player\"}");

            Assert.IsTrue(inner.FindObjectsCalled,
                "IAIFunctionsLlmTool function should have been called by function name.");
            Assert.That(json, Does.Contain("Player"));
        }

        [Test]
        public async Task CallSkillTool_Execute_TopLevelToolName_InvokesItInstead()
        {
            // A skill teaches the model to reach ITS tools through call_skill_tool, and the model
            // generalises to the agent's top-level tools. Refusing that call costs a real action: the
            // refusal is an ordinary tool result, so the model apologises in prose and the user sees
            // nothing happen. Downstream this read as "the model rarely spawns a quiz".
            bool called = false;
            DelegateLlmTool topLevel = new("spawn_quiz", "Shows a quiz card",
                new Func<string, object>(question =>
                {
                    called = true;
                    return new { shown = question };
                }));

            ILlmTool proxy = CallSkillToolLlmTool.Create(
                new List<SkillSet> { MakeCraftingSkill() },
                () => new List<ILlmTool> { topLevel });

            string json = await InvokeCallSkillToolAsync(proxy, "spawn_quiz", "{\"question\":\"2+2?\"}");

            Assert.IsTrue(called, "A wrapped top-level tool must run, not come back as 'not found'.");
            Assert.That(json, Does.Contain("2+2?"));
        }

        [Test]
        public async Task CallSkillTool_Execute_UnknownTool_StillFailsWhenNoTopLevelMatch()
        {
            DelegateLlmTool topLevel = new("spawn_quiz", "Shows a quiz card", new Action(() => { }));

            ILlmTool proxy = CallSkillToolLlmTool.Create(
                new List<SkillSet> { MakeCraftingSkill() },
                () => new List<ILlmTool> { topLevel });

            string json = await InvokeCallSkillToolAsync(proxy, "no_such_tool", "{}");

            Assert.IsFalse(JObject.Parse(json).Value<bool>("success"),
                "The fallback must widen the lookup, not accept any name.");
            Assert.That(JObject.Parse(json).Value<string>("error"), Is.Not.Null.And.Not.Empty);
        }

        [Test]
        public async Task CallSkillTool_Execute_TopLevelTool_SurvivesRestrictToWhenAllowed()
        {
            // Pairs with the test below, and neither is enough alone: a RestrictTo that silently
            // dropped the provider would leave the "blocked" test green for the WRONG reason — the
            // tool would be unreachable rather than forbidden. Only the allowed direction can tell
            // "the allowlist said no" apart from "the fallback was lost in the copy".
            bool called = false;
            DelegateLlmTool topLevel = new("spawn_quiz", "Shows a quiz card",
                new Action(() => called = true));

            ILlmTool proxy = CallSkillToolLlmTool.Create(
                new List<SkillSet> { MakeCraftingSkill() },
                () => new List<ILlmTool> { topLevel });
            ILlmTool restricted = ((ISkillSetMetaLlmTool)proxy).RestrictTo(new[] { "craft_item", "spawn_quiz" });

            string json = await InvokeCallSkillToolAsync(restricted, "spawn_quiz", "{}");

            Assert.IsTrue(called, "RestrictTo must carry the top-level fallback into the restricted copy.");
            Assert.IsTrue(JObject.Parse(json).Value<bool>("success"));
        }

        [Test]
        public async Task CallSkillTool_Execute_TopLevelTool_StaysBlockedByTheSessionAllowlist()
        {
            // Wrapping a name must not become a way around the per-turn allowlist: what the turn may
            // not call directly it may not call through the wrapper either.
            bool called = false;
            DelegateLlmTool topLevel = new("spawn_quiz", "Shows a quiz card",
                new Action(() => called = true));

            ILlmTool proxy = CallSkillToolLlmTool.Create(
                new List<SkillSet> { MakeCraftingSkill() },
                () => new List<ILlmTool> { topLevel });
            ILlmTool restricted = ((ISkillSetMetaLlmTool)proxy).RestrictTo(new[] { "craft_item" });

            string json = await InvokeCallSkillToolAsync(restricted, "spawn_quiz", "{}");

            Assert.IsFalse(called, "A tool outside the allowlist must not run through the wrapper.");
            Assert.That(JObject.Parse(json).Value<bool?>("success"), Is.False);
        }

        [Test]
        public async Task CallSkillTool_Execute_OwnName_DoesNotRecurse()
        {
            // The provider reports the role's whole tool list, and that list contains this proxy.
            ILlmTool proxy = null;
            proxy = CallSkillToolLlmTool.Create(
                new List<SkillSet> { MakeCraftingSkill() },
                () => new List<ILlmTool> { proxy });

            string json = await InvokeCallSkillToolAsync(proxy, "call_skill_tool", "{}");

            Assert.IsFalse(JObject.Parse(json).Value<bool>("success"),
                "Dispatching the wrapper into itself would recurse until the stack gives out.");
        }

        // ══════════════════════════════════════════════════════════════════════
        //  AgentBuilder integration
        // ══════════════════════════════════════════════════════════════════════

        [Test]
        public void AgentBuilder_WithSkill_SkillToolsNotInConfigTools()
        {
            SkillSet crafting = MakeCraftingSkill();
            SkillSet combat = MakeCombatSkill();

            AgentConfig config = new AgentBuilder("test_agent")
                {
                    SuppressBuildWarnings = true
                }
                .WithSkill(crafting)
                .WithSkill(combat)
                .WithMode(AgentMode.ToolsAndChat)
                .Build();

            // Skill tools are NOT in the main tool list — they go through call_skill_tool proxy
            Assert.AreEqual(0, config.Tools.Count);
            Assert.IsNotNull(config.Skills);
            Assert.AreEqual(2, config.Skills.Count);
        }

        [Test]
        public void AgentBuilder_WithOnlySkills_DoesNotWarnNoToolsForToolMode()
        {
            SkillSet crafting = MakeCraftingSkill();
            AgentBuilder builder = new AgentBuilder("skill_only_agent")
                {
                    SuppressBuildWarnings = true
                }
                .WithSkill(crafting)
                .WithMode(AgentMode.ToolsAndChat);

            IReadOnlyList<AgentBuilderIssue> issues = builder.ValidateOnBuild();

            Assert.IsFalse(issues.Any(i => i.Code == AgentBuilderIssueCode.NoToolsForToolMode),
                "Skill-only agents receive read_skill/call_skill_tool during ApplyToPolicy and must not warn as tool-less.");
        }

        [Test]
        public void AgentBuilder_WithSkills_Convenience()
        {
            SkillSet a = MakeCraftingSkill();
            SkillSet b = MakeCombatSkill();

            AgentConfig config = new AgentBuilder("test_multi")
                {
                    SuppressBuildWarnings = true
                }
                .WithSkills(a, b)
                .Build();

            Assert.AreEqual(0, config.Tools.Count); // skill tools routed via proxy
            Assert.AreEqual(2, config.Skills.Count);
        }

        [Test]
        public void ApplyToPolicy_WithSkills_RegistersBothMetaTools()
        {
            SkillSet crafting = MakeCraftingSkill();
            AgentConfig config = new AgentBuilder("test_policy")
                {
                    SuppressBuildWarnings = true
                }
                .WithSkill(crafting)
                .WithMode(AgentMode.ToolsAndChat)
                .Build();

            AgentMemoryPolicy policy = new();
            config.ApplyToPolicy(policy);

            // Policy should have ONLY read_skill + call_skill_tool (not individual skill tools)
            IReadOnlyList<ILlmTool> tools = policy.GetToolsForRole("test_policy");
            Assert.IsNotNull(tools);

            bool hasReadSkill = false;
            bool hasCallSkillTool = false;
            foreach (ILlmTool tool in tools)
            {
                if (tool.Name == "read_skill")
                {
                    hasReadSkill = true;
                }

                if (tool.Name == "call_skill_tool")
                {
                    hasCallSkillTool = true;
                }
            }

            Assert.IsTrue(hasReadSkill, "read_skill meta-tool should be registered.");
            Assert.IsTrue(hasCallSkillTool, "call_skill_tool proxy should be registered.");

            // Individual skill tools (get_recipes, craft_item) should NOT be in the policy tool list
            foreach (ILlmTool tool in tools)
            {
                Assert.AreNotEqual("get_recipes", tool.Name, "Skill tools should NOT be registered directly.");
                Assert.AreNotEqual("craft_item", tool.Name, "Skill tools should NOT be registered directly.");
            }
        }

        [Test]
        public void ApplyToPolicy_WithSkills_AddsCatalogToStableSystemPrompt()
        {
            SkillSet crafting = MakeCraftingSkill();
            SkillSet combat = MakeCombatSkill();
            AgentConfig config = new AgentBuilder("test_catalog")
                {
                    SuppressBuildWarnings = true
                }
                .WithSkill(crafting)
                .WithSkill(combat)
                .Build();

            AgentMemoryPolicy policy = new();
            config.ApplyToPolicy(policy);

            // Skill catalog is static per agent build, so it belongs in the stable prompt prefix.
            Assert.IsTrue(
                policy.TryGetAdditionalSystemPrompt("test_catalog", out string context),
                "Additional system prompt should include the skill catalog.");

            Assert.That(context, Does.Contain("Available Skills"));
            Assert.That(context, Does.Contain("Crafting"));
            Assert.That(context, Does.Contain("Combat"));
            Assert.That(context, Does.Contain("read_skill"));
            Assert.That(context, Does.Contain("call_skill_tool"));
        }

        // ══════════════════════════════════════════════════════════════════════
        //  Factory methods
        // ══════════════════════════════════════════════════════════════════════

        [Test]
        public void FromTextContent_CreatesSkillWithInstructions()
        {
            string instructions = "Step 1: Do this. Step 2: Do that.";
            SkillSet skill = SkillSet.FromTextContent("Test", "A test skill", instructions, MakeTool("t1"));

            Assert.AreEqual("Test", skill.Name);
            Assert.AreEqual("A test skill", skill.Description);
            Assert.AreEqual(instructions, skill.Instructions);
        }

        [Test]
        public void FromFile_NullPath_Throws()
        {
            Assert.Throws<ArgumentException>(() =>
                SkillSet.FromFile("Test", "desc", null, MakeTool("t")));
        }

        [Test]
        public void FromTextParts_ThreeParts_JoinInOrderWithHeadings()
        {
            List<KeyValuePair<string, string>> parts = new()
            {
                new KeyValuePair<string, string>("a.md", "Alpha"),
                new KeyValuePair<string, string>("b.md", "Beta"),
                new KeyValuePair<string, string>("c.md", "Gamma"),
            };

            SkillSet skill = SkillSet.FromTextParts("Multi", "desc", parts);

            Assert.AreEqual("## a.md\nAlpha\n\n## b.md\nBeta\n\n## c.md\nGamma", skill.Instructions);
        }

        [Test]
        public void FromTextParts_EmptyContent_SkippedEntirely()
        {
            List<KeyValuePair<string, string>> parts = new()
            {
                new KeyValuePair<string, string>("a.md", "Alpha"),
                new KeyValuePair<string, string>("empty.md", ""),
                new KeyValuePair<string, string>("blank.md", "   "),
                new KeyValuePair<string, string>("b.md", "Beta"),
            };

            SkillSet skill = SkillSet.FromTextParts("Multi", "desc", parts);

            Assert.AreEqual("## a.md\nAlpha\n\n## b.md\nBeta", skill.Instructions);
        }

        [Test]
        public void FromTextContent_SingleSource_AddsNoHeading()
        {
            string instructions = "## not-a-heading\nJust content.";
            SkillSet skill = SkillSet.FromTextContent("Test", "desc", instructions);

            Assert.AreEqual(instructions, skill.Instructions);
        }

        [Test]
        public void FromTextParts_NullCollection_Throws()
        {
            Assert.Throws<ArgumentNullException>(() =>
                SkillSet.FromTextParts("Test", "desc", (IEnumerable<KeyValuePair<string, string>>)null));
        }

        [Test]
        public void FromTextParts_EmptyCollection_Throws()
        {
            Assert.Throws<ArgumentException>(() =>
                SkillSet.FromTextParts("Test", "desc", new List<KeyValuePair<string, string>>()));
        }

        [Test]
        public void FromTextParts_NullPartName_Throws()
        {
            List<KeyValuePair<string, string>> parts = new()
            {
                new KeyValuePair<string, string>("a.md", "Alpha"),
                new KeyValuePair<string, string>(null, "Beta"),
            };

            Assert.Throws<ArgumentException>(() =>
                SkillSet.FromTextParts("Test", "desc", parts));
        }

        [Test]
        public void FromFiles_ReadsTempFiles_MatchesFromTextParts()
        {
            string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), System.IO.Path.GetRandomFileName());
            System.IO.Directory.CreateDirectory(dir);
            try
            {
                string fileA = System.IO.Path.Combine(dir, "overview.md");
                string fileB = System.IO.Path.Combine(dir, "details.md");
                System.IO.File.WriteAllText(fileA, "Alpha");
                System.IO.File.WriteAllText(fileB, "Beta");

                SkillSet fromFiles = SkillSet.FromFiles("Multi", "desc", new[] { fileA, fileB });
                SkillSet fromParts = SkillSet.FromTextParts("Multi", "desc", new[]
                {
                    new KeyValuePair<string, string>("overview.md", "Alpha"),
                    new KeyValuePair<string, string>("details.md", "Beta"),
                });

                Assert.AreEqual(fromParts.Instructions, fromFiles.Instructions);
                Assert.AreEqual("## overview.md\nAlpha\n\n## details.md\nBeta", fromFiles.Instructions);
            }
            finally
            {
                System.IO.Directory.Delete(dir, true);
            }
        }

        [Test]
        public void FromFiles_NullCollection_Throws()
        {
            Assert.Throws<ArgumentNullException>(() =>
                SkillSet.FromFiles("Test", "desc", (IEnumerable<string>)null));
        }

        [Test]
        public void FromFiles_EmptyCollection_Throws()
        {
            Assert.Throws<ArgumentException>(() =>
                SkillSet.FromFiles("Test", "desc", new string[0]));
        }

        [Test]
        public void FromFiles_NullEntry_Throws()
        {
            Assert.Throws<ArgumentException>(() =>
                SkillSet.FromFiles("Test", "desc", new string[] { null }));
        }

        private sealed class ExplicitFunctionSkillTool : LlmToolBase, IAIFunctionLlmTool
        {
            public ExplicitFunctionSkillTool(string name)
            {
                NameValue = name;
            }

            private string NameValue { get; }
            public bool Called { get; private set; }
            public override string Name => NameValue;
            public override string Description => "Explicit function skill tool.";

            public AIFunction CreateAIFunction()
            {
                return AIFunctionFactory.Create(
                    (Func<string, string>)Execute,
                    new AIFunctionFactoryOptions
                    {
                        Name = Name,
                        Description = Description
                    });
            }

            private string Execute(string value)
            {
                Called = true;
                return JsonConvert.SerializeObject(new { success = true, echo = value });
            }
        }

        private sealed class MultiFunctionSkillTool : ILlmTool, IAIFunctionsLlmTool
        {
            public string Name => "scene_tool";
            public string Description => "Scene functions.";
            public string ParametersSchema => "{}";
            public bool AllowDuplicates => false;
            public bool FindObjectsCalled { get; private set; }

            public IEnumerable<AIFunction> CreateAIFunctions()
            {
                yield return AIFunctionFactory.Create(
                    (Func<string, string>)FindObjects,
                    new AIFunctionFactoryOptions
                    {
                        Name = "find_objects",
                        Description = "Find objects."
                    });

                yield return AIFunctionFactory.Create(
                    (Func<int, string>)GetHierarchy,
                    new AIFunctionFactoryOptions
                    {
                        Name = "get_hierarchy",
                        Description = "Get hierarchy."
                    });
            }

            private string FindObjects(string query)
            {
                FindObjectsCalled = true;
                return JsonConvert.SerializeObject(new { success = true, query });
            }

            private string GetHierarchy(int rootId)
            {
                return JsonConvert.SerializeObject(new { success = true, rootId });
            }
        }

        private sealed class JsonInvocableSkillTool : LlmToolBase, IJsonInvocableLlmTool
        {
            public bool Called { get; private set; }
            public override string Name => "json_action";
            public override string Description => "Direct JSON skill action.";

            public override string ParametersSchema =>
                "{\"type\":\"object\",\"properties\":{\"value\":{\"type\":\"string\"}}}";

            public Task<object> InvokeJsonAsync(string argumentsJson, CancellationToken cancellationToken = default)
            {
                Called = true;
                JObject args = JObject.Parse(argumentsJson);
                return Task.FromResult<object>(new
                {
                    success = true,
                    echo = args.Value<string>("value")
                });
            }
        }
    }

    /// <summary>
    /// Covers agent-authored skills (R4): a model can create / update / delete its own skills via the
    /// <see cref="SkillAuthoringCoordinator"/>, they persist, version, and become reusable through the same
    /// role's live <c>read_skill</c> catalog.
    /// </summary>
    public sealed class SkillAuthoringEditModeTests
    {
        [TestCase(false, null)]
        [TestCase(true, null)]
        [TestCase(false, "Revised entry document")]
        [TestCase(true, "Revised entry document")]
        public async Task MultiFileUpdate_SurvivesSerializationAndRestart(bool systemTextJson, string instructions)
        {
            SkillAuthoringCoordinator coordinator = MakeCoordinator(out MutableSkillCatalog catalog, out MemorySkillStore store);
            SkillSet original = SkillSet.FromTextParts("Guide", "initial", new KeyValuePair<string, string>[]
            {
                new("SKILL.md", "Read references/api.md before acting."),
                new("references/api.md", "Reference body with complete API instructions.")
            });
            catalog.AddOrReplace(original);
            Assert.AreEqual(original.Sections.Count, coordinator.GetSkill("Guide").Sections.Count);
            Assert.AreEqual(original.Sections.Count, coordinator.ListSkills()[0].Sections.Count);
            SkillAuthoringResult result = coordinator.Update("Guide", "updated description", instructions, null);
            Assert.IsTrue(result.Success, result.Message);
            string json = systemTextJson ? System.Text.Json.JsonSerializer.Serialize(result.Record)
                : JsonConvert.SerializeObject(result.Record);
            SkillRecord persisted = systemTextJson ? System.Text.Json.JsonSerializer.Deserialize<SkillRecord>(json)
                : JsonConvert.DeserializeObject<SkillRecord>(json);
            MemorySkillStore restartedStore = new();
            restartedStore.Save(persisted);
            MutableSkillCatalog restartedCatalog = new();
            SkillAuthoringCoordinator restarted = new(restartedCatalog, restartedStore, null, _ => null);
            Assert.AreEqual(1, restarted.RehydrateFromStore());
            JObject main = JObject.Parse(ReadSkillLlmTool.ReadSkillJson(restartedCatalog, "Guide"));
            Assert.AreEqual(instructions ?? original.Sections[0].Content, (string)main["instructions"]);
            JObject reference = JObject.Parse(ReadSkillLlmTool.ReadSkillJson(restartedCatalog, "Guide", "references/api.md"));
            Assert.AreEqual(original.Sections[1].Content, (string)reference["instructions"]);
            JObject all = JObject.Parse(ReadSkillLlmTool.ReadSkillJson(restartedCatalog, "Guide", all: true));
            StringAssert.Contains(original.Sections[1].Content, (string)all["instructions"]);
            Assert.AreEqual(original.Sections.Count, ((JArray)all["sections"]).Count);
            ManageSkillsLlmTool management = new(restarted);
            JObject definition = JObject.Parse(await management.ExecuteAsync("get", "Guide"));
            Assert.AreEqual(original.Sections[1].Content, (string)definition["data"]["sections"][1]["Content"]);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void LegacyRecordWithoutSections_RehydratesAndUpdates(bool systemTextJson)
        {
            const string json = "{\"Id\":\"legacy\",\"Description\":\"old\",\"Instructions\":\"Complete legacy entry\",\"ToolNames\":[],\"Version\":3}";
            SkillRecord record = systemTextJson ? System.Text.Json.JsonSerializer.Deserialize<SkillRecord>(json)
                : JsonConvert.DeserializeObject<SkillRecord>(json);
            MemorySkillStore store = new();
            store.Save(record);
            MutableSkillCatalog catalog = new();
            SkillAuthoringCoordinator coordinator = new(catalog, store, null, _ => null);
            Assert.AreEqual(1, coordinator.RehydrateFromStore());
            Assert.AreEqual(record.Instructions, catalog.Get(record.Id).Instructions);
            SkillAuthoringResult updated = coordinator.Update(record.Id, "new description", null, null);
            Assert.IsTrue(updated.Success);
            Assert.AreEqual(record.Instructions, catalog.Get(record.Id).Instructions);
            Assert.AreEqual(record.Version + 1, updated.Record.Version);
        }

        [Test]
        public void FailedMutation_CannotChangeStoredSectionsThroughSnapshot()
        {
            MemorySkillStore store = new();
            SkillSection[] sections = { new("SKILL.md", "original"), new("references/api.md", "reference") };
            store.Save(new SkillRecord("guide", "", "", null, sections: sections));
            Assert.Throws<IOException>(() => store.Mutate<bool>("guide", current =>
            {
                current.Sections[1] = new SkillSection("references/api.md", "uncommitted");
                throw new IOException("Rejected before commit");
            }));
            Assert.IsTrue(store.TryLoad("guide", out SkillRecord preserved));
            Assert.AreEqual(sections[1].Content, preserved.Sections[1].Content);
        }

        private sealed class StubTool : ILlmTool
        {
            public StubTool(string name)
            {
                Name = name;
            }

            public string Name { get; }
            public string Description => "stub";
            public string ParametersSchema => "{}";
            public bool AllowDuplicates => false;
        }

        private sealed class MemorySkillStore : ISkillStore
        {
            private readonly Dictionary<string, SkillRecord> _m = new(StringComparer.Ordinal);
            public bool FailSave;
            public bool FailDelete;
            public Action<SkillRecord> BeforeSave;
            public Action BeforeList;
            public int ListCalls;
            public int TryLoadCalls;

            public void Save(SkillRecord record)
            {
                BeforeSave?.Invoke(record);
                if (FailSave) throw new IOException("Injected save failure");
                _m[record.Id] = record;
            }

            public bool TryLoad(string id, out SkillRecord record)
            {
                TryLoadCalls++;
                return _m.TryGetValue(id, out record);
            }

            public IReadOnlyList<SkillRecord> List()
            {
                ListCalls++;
                BeforeList?.Invoke();
                return new List<SkillRecord>(_m.Values);
            }

            public void Delete(string id)
            {
                if (FailDelete) throw new IOException("Injected delete failure");
                _m.Remove(id);
            }
        }

        private sealed class ReturnBoundaryStore : ISkillStore, ICommittedSkillStore
        {
            private readonly MemorySkillStore _inner = new();
            public Action AfterCommitBeforeReturning;
            public void Save(SkillRecord record) => _inner.Save(record);
            public void Delete(string id) => _inner.Delete(id);
            public bool TryLoad(string id, out SkillRecord record) => _inner.TryLoad(id, out record);
            public IReadOnlyList<SkillRecord> List() => _inner.List();
            public TResult Mutate<TResult>(string id, Func<SkillRecord, SkillStoreMutation<TResult>> prepare) =>
                MutateAndPublish(id, prepare, null);
            public TResult MutateAndPublish<TResult>(string id, Func<SkillRecord, SkillStoreMutation<TResult>> prepare,
                Action<TResult> publish)
            {
                TResult result = _inner.MutateAndPublish(id, prepare, publish);
                Action boundary = AfterCommitBeforeReturning;
                AfterCommitBeforeReturning = null;
                boundary?.Invoke();
                return result;
            }
        }

        private sealed class FailingRevisionStore : ILuaScriptVersionStore
        {
            private readonly MemoryLuaScriptVersionStore _inner = new();
            public bool FailWrites;
            public bool TryGetSnapshot(string key, out LuaScriptVersionRecord snapshot) => _inner.TryGetSnapshot(key, out snapshot);
            public void RecordSuccessfulExecution(string key, string source)
            {
                if (FailWrites) throw new IOException("Revision storage unavailable");
                _inner.RecordSuccessfulExecution(key, source);
            }
            public void SeedOriginal(string key, string source, bool overwriteExistingOriginal = false)
            {
                if (FailWrites) throw new IOException("Revision storage unavailable");
                _inner.SeedOriginal(key, source, overwriteExistingOriginal);
            }
            public void ResetToOriginal(string key) => _inner.ResetToOriginal(key);
            public void ResetToRevision(string key, int index) => _inner.ResetToRevision(key, index);
            public void ResetAllToOriginal() => _inner.ResetAllToOriginal();
            public IReadOnlyList<string> GetKnownKeys() => _inner.GetKnownKeys();
            public string BuildProgrammerPromptSection(string key) => _inner.BuildProgrammerPromptSection(key);
        }

        [Test]
        public void ListSkills_ReadBoundaryNeverCombinesOldContentWithNewVersion()
        {
            SkillAuthoringCoordinator coordinator = MakeCoordinator(out _, out MemorySkillStore store);
            coordinator.Create("guide", "old description", "old body", Array.Empty<string>());
            SkillAuthoringResult changed = null;
            store.BeforeList = () =>
            {
                store.BeforeList = null;
                changed = coordinator.Update("guide", "new description", "new body", null);
            };
            SkillRecord listed = coordinator.ListSkills().Single();
            Assert.AreEqual(changed.Record.Version, listed.Version);
            Assert.AreEqual(changed.Record.Instructions, listed.Instructions);
            Assert.AreEqual(changed.Record.Description, listed.Description);
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task RevisionIdentity_AmbiguousAliasesReportFailureWithoutChangingEitherHistory(bool update)
        {
            MemorySkillStore store = new();
            MemoryLuaScriptVersionStore versions = new();
            SkillAuthoringCoordinator coordinator = new(new MutableSkillCatalog(), store, versions, _ => null);
            if (update) Assert.IsTrue(coordinator.Create("Guide", "", "initial body", Array.Empty<string>()).Success);
            versions.SeedOriginal("skill:Guide", "original uppercase history");
            versions.SeedOriginal("skill:guide", "original lowercase history");
            Assert.IsTrue(versions.TryGetSnapshot("skill:Guide", out LuaScriptVersionRecord upper));
            Assert.IsTrue(versions.TryGetSnapshot("skill:guide", out LuaScriptVersionRecord lower));
            ManageSkillsLlmTool tool = new(coordinator);
            JObject outcome = JObject.Parse(await tool.ExecuteAsync(update ? "update" : "create", "guide",
                instructions: "committed body"));
            Assert.IsTrue((bool)outcome["success"]);
            Assert.IsTrue((bool)outcome["data"]["committed"]);
            Assert.IsFalse((bool)outcome["data"]["revision_recorded"]);
            Assert.IsNotEmpty((string)outcome["data"]["warning"]);
            Assert.AreEqual("committed body", coordinator.GetSkill("guide").Instructions);
            Assert.Throws<InvalidOperationException>(() => coordinator.ListRevisions("guide"));
            JObject read = JObject.Parse(await tool.ExecuteAsync("get", "guide"));
            Assert.IsFalse((bool)read["success"], "The tool must expose ambiguity instead of inventing an empty history.");
            Assert.IsTrue(versions.TryGetSnapshot("skill:Guide", out LuaScriptVersionRecord preservedUpper));
            Assert.IsTrue(versions.TryGetSnapshot("skill:guide", out LuaScriptVersionRecord preservedLower));
            Assert.AreEqual(upper.OriginalLua, preservedUpper.OriginalLua);
            Assert.AreEqual(upper.CurrentLua, preservedUpper.CurrentLua);
            CollectionAssert.AreEqual(upper.History, preservedUpper.History);
            Assert.AreEqual(lower.OriginalLua, preservedLower.OriginalLua);
            Assert.AreEqual(lower.CurrentLua, preservedLower.CurrentLua);
            CollectionAssert.AreEqual(lower.History, preservedLower.History);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void RevisionIdentity_RehydrateResolvesExistingKeyOrRejectsAmbiguity(bool ambiguous)
        {
            MemorySkillStore store = new();
            store.Save(new SkillRecord("guide", "", "persisted body", Array.Empty<string>()));
            MemoryLuaScriptVersionStore versions = new();
            versions.SeedOriginal("skill:Guide", "preserved original");
            if (ambiguous) versions.SeedOriginal("skill:guide", "conflicting original");
            string[] keys = versions.GetKnownKeys().ToArray();
            SkillAuthoringCoordinator coordinator = new(new MutableSkillCatalog(), store, versions, _ => null);
            if (ambiguous) Assert.Throws<InvalidOperationException>(() => coordinator.RehydrateFromStore());
            else
            {
                Assert.AreEqual(1, coordinator.RehydrateFromStore());
                StringAssert.Contains("preserved original", coordinator.ListRevisions("guide")[0].Source);
            }
            CollectionAssert.AreEquivalent(keys, versions.GetKnownKeys());
        }

        [Test]
        public void RecreateAfterDelete_PreservesOriginalAndRecordsNewBodyAsNextRevision()
        {
            SkillAuthoringCoordinator coordinator = MakeCoordinator(out MutableSkillCatalog catalog, out MemorySkillStore store);
            SkillAuthoringResult created = coordinator.Create("guide", "field guide", "old body", Array.Empty<string>());
            Assert.IsTrue(created.Success, created.Message);
            Assert.IsTrue(coordinator.Delete("guide").Success);
            SkillAuthoringResult recreated = coordinator.Create("guide", "field guide", "new body", Array.Empty<string>());
            Assert.IsTrue(recreated.Success, recreated.Message);
            Assert.AreEqual(0, recreated.Record.Version);
            Assert.IsTrue(recreated.RevisionRecorded == true,
                "A recreated skill with surviving history must truthfully report its revision outcome.");
            IReadOnlyList<LuaScriptRevision> history = coordinator.ListRevisions("guide");
            Assert.GreaterOrEqual(history.Count, 2, "Original plus recreated body must both remain recorded.");
            StringAssert.Contains("old body", history[0].Source, "Recreate must preserve the original history.");
            StringAssert.Contains("new body", history[history.Count - 1].Source,
                "Recreated body must be recorded as the next revision.");
            Assert.IsTrue(store.TryLoad("guide", out SkillRecord current));
            Assert.AreEqual("new body", current.Instructions);
            Assert.AreEqual("new body", catalog.Get("guide").Instructions);
        }

        [Test]
        public void ListSkills_UsesOneBatchReadAndNoPointReads()
        {
            SkillAuthoringCoordinator coordinator = MakeCoordinator(out _, out MemorySkillStore store);
            coordinator.Create("alpha", "first", "alpha body", Array.Empty<string>());
            coordinator.Create("beta", "second", "beta body", Array.Empty<string>());
            store.ListCalls = 0;
            store.TryLoadCalls = 0;
            IReadOnlyList<SkillRecord> listed = coordinator.ListSkills();
            Assert.AreEqual(2, listed.Count);
            Assert.AreEqual(1, store.ListCalls, "ListSkills must take one coherent batch snapshot.");
            Assert.AreEqual(0, store.TryLoadCalls, "ListSkills must not issue per-skill point reads.");
        }

        [Test]
        public void ListSkills_IgnoresPersistedRecordsAbsentFromLiveCatalog()
        {
            SkillAuthoringCoordinator coordinator = MakeCoordinator(out _, out MemorySkillStore store);
            store.Save(new SkillRecord("ghost", "stale", "stale body", null));
            coordinator.Create("live", "current", "live body", Array.Empty<string>());
            IReadOnlyList<SkillRecord> listed = coordinator.ListSkills();
            Assert.AreEqual(1, listed.Count);
            Assert.AreEqual("live", listed[0].Id);
            Assert.AreEqual("live body", listed[0].Instructions);
        }

        [Test]
        public void ListSkills_RetainsSessionOnlyMetadataWithoutPersistedStore()
        {
            MutableSkillCatalog catalog = new();
            SkillAuthoringCoordinator coordinator = new(catalog, null, null, _ => null);
            SkillAuthoringResult created = coordinator.Create("solo", "solo description", "solo body", Array.Empty<string>());
            Assert.IsTrue(created.Success, created.Message);
            IReadOnlyList<SkillRecord> listed = coordinator.ListSkills();
            Assert.AreEqual(1, listed.Count);
            Assert.AreEqual("solo body", listed[0].Instructions);
            Assert.AreEqual("solo description", listed[0].Description);
            Assert.AreEqual(0, listed[0].Version);
        }

        [Test]
        public void NullStore_VersionsSurviveCoordinatorReplacementWithinOneCatalog()
        {
            MutableSkillCatalog catalog = new();
            SkillAuthoringCoordinator first = new(catalog, null, null, _ => null);
            first.Create("Guide", "", "initial", Array.Empty<string>());
            SkillAuthoringResult prior = first.Update("guide", null, "first", null);
            SkillAuthoringCoordinator second = new(catalog, new NullSkillStore(), null, _ => null);
            SkillAuthoringResult next = second.Update("GUIDE", null, "second", null);
            Assert.Greater(next.Record.Version, prior.Record.Version);
            Assert.AreEqual(next.Record.Version, first.GetSkill("guide").Version);
            Assert.AreEqual(next.Record.Instructions, first.ListSkills().Single().Instructions);
            Assert.AreEqual(next.Record.Version, second.ListSkills().Single().Version);
            Assert.IsNull(next.RevisionRecorded, "No revision store was configured.");
            SkillAuthoringCoordinator isolated = new(new MutableSkillCatalog(), null, null, _ => null);
            Assert.IsNull(isolated.GetSkill("Guide"));
            Assert.IsTrue(second.Delete("guide").Success);
            Assert.IsNull(first.GetSkill("GUIDE"));
        }

        [Test]
        public void RevisionHistory_ResolvesLiveCaseAliasesAndRetainsDeletedOriginalKey()
        {
            SkillAuthoringCoordinator coordinator = new(new MutableSkillCatalog(), null,
                new MemoryLuaScriptVersionStore(), _ => null);
            coordinator.Create("Guide", "", "original body", Array.Empty<string>());
            coordinator.Update("GUIDE", null, "updated body", null);
            IReadOnlyList<LuaScriptRevision> original = coordinator.ListRevisions("Guide");
            IReadOnlyList<LuaScriptRevision> alias = coordinator.ListRevisions("guide");
            Assert.Greater(original.Count, 1);
            CollectionAssert.AreEqual(original, alias);
            coordinator.Delete("GUIDE");
            CollectionAssert.AreEqual(original, coordinator.ListRevisions("Guide"),
                "Deleting the skill preserves existing history under its original key.");
        }

        [Test]
        public void SharedStore_RevisionOrderCannotBeOvertakenBeforeFirstCallReturns()
        {
            ReturnBoundaryStore store = new();
            MemoryLuaScriptVersionStore revisions = new();
            SkillAuthoringCoordinator first = new(new MutableSkillCatalog(), store, revisions, _ => null);
            SkillAuthoringCoordinator second = new(new MutableSkillCatalog(), store, revisions, _ => null);
            first.Create("guide", "", "initial", Array.Empty<string>());
            second.RehydrateFromStore();
            SkillAuthoringResult later = null;
            // WHY: another catalog commits after the first primary transaction, before its caller resumes.
            store.AfterCommitBeforeReturning = () => later = second.Update("guide", null, "later committed body", null);
            first.Update("guide", null, "earlier committed body", null);
            Assert.IsTrue(store.TryLoad("guide", out SkillRecord final));
            Assert.AreEqual(later.Record.Version, final.Version);
            Assert.IsTrue(revisions.TryGetSnapshot("skill:guide", out LuaScriptVersionRecord history));
            StringAssert.Contains(final.Instructions, history.CurrentLua);
            StringAssert.DoesNotContain("earlier committed body", history.CurrentLua);
            Assert.AreEqual(3, history.History.Count, "Create and both different revisions must remain recorded.");
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task RevisionFailure_ReportsCommittedChangeWithoutRequestingReplay(bool failCreate)
        {
            MemorySkillStore store = new();
            MutableSkillCatalog catalog = new();
            FailingRevisionStore revisions = new();
            SkillAuthoringCoordinator coordinator = new(catalog, store, revisions, _ => null);
            if (!failCreate) coordinator.Create("guide", "", "initial", Array.Empty<string>());
            revisions.FailWrites = true;
            ManageSkillsLlmTool tool = new(coordinator);
            JObject result = JObject.Parse(await tool.ExecuteAsync(failCreate ? "create" : "update", "guide", instructions: "committed body"));
            Assert.IsTrue((bool)result["success"]);
            Assert.IsTrue((bool)result["data"]["committed"]);
            Assert.IsFalse((bool)result["data"]["revision_recorded"]);
            Assert.IsNotEmpty((string)result["data"]["warning"]);
            Assert.IsTrue(store.TryLoad("guide", out SkillRecord committed));
            Assert.AreEqual(committed.Instructions, catalog.Get("guide").Instructions);
            Assert.AreEqual("committed body", committed.Instructions);
            if (!failCreate)
            {
                Assert.IsTrue(revisions.TryGetSnapshot("skill:guide", out LuaScriptVersionRecord prior));
                StringAssert.Contains("initial", prior.CurrentLua);
                StringAssert.DoesNotContain(committed.Instructions, prior.CurrentLua);
            }
        }

        private static SkillAuthoringCoordinator MakeCoordinator(
            out MutableSkillCatalog catalog, out MemorySkillStore store)
        {
            catalog = new MutableSkillCatalog();
            store = new MemorySkillStore();
            SkillToolResolver resolver = name =>
                string.Equals(name, "memory", StringComparison.OrdinalIgnoreCase)
                    ? (ILlmTool)new StubTool("memory")
                    : null;
            return new SkillAuthoringCoordinator(
                catalog, store, new MemoryLuaScriptVersionStore(), resolver, true);
        }

        [Test]
        public void Create_PersistsAndAppearsInCatalog()
        {
            SkillAuthoringCoordinator coord =
                MakeCoordinator(out MutableSkillCatalog catalog, out MemorySkillStore store);

            SkillAuthoringResult r = coord.Create("greet", "greets the player", "Say hi warmly.", new[] { "memory" });

            Assert.IsTrue(r.Success, r.Message);
            Assert.IsNotNull(catalog.Get("greet"), "Authored skill must be in the live read_skill catalog.");
            Assert.AreEqual("Say hi warmly.", catalog.Get("greet").Instructions);
            Assert.IsTrue(store.TryLoad("greet", out SkillRecord rec));
            Assert.AreEqual(0, rec.Version);
        }

        [Test]
        public void Create_UnknownTool_Fails()
        {
            SkillAuthoringCoordinator coord = MakeCoordinator(out MutableSkillCatalog catalog, out _);

            SkillAuthoringResult r = coord.Create("bad", "d", "i", new[] { "ghost_tool" });

            Assert.IsFalse(r.Success);
            Assert.IsNull(catalog.Get("bad"), "A skill referencing an unregistered tool must not be created.");
        }

        [Test]
        public void Create_InstructionsOnly_Succeeds()
        {
            SkillAuthoringCoordinator coord = MakeCoordinator(out MutableSkillCatalog catalog, out _);

            SkillAuthoringResult r = coord.Create("note", "a note", "Remember this procedure.", new string[0]);

            Assert.IsTrue(r.Success, r.Message);
            Assert.IsNotNull(catalog.Get("note"));
        }

        [Test]
        public void Update_RevisesInstructionsAndRecordsRevision()
        {
            SkillAuthoringCoordinator coord =
                MakeCoordinator(out MutableSkillCatalog catalog, out MemorySkillStore store);
            coord.Create("greet", "d", "v0 instructions", new[] { "memory" });

            SkillAuthoringResult u = coord.Update("greet", null, "v1 instructions", null);

            Assert.IsTrue(u.Success, u.Message);
            Assert.AreEqual("v1 instructions", catalog.Get("greet").Instructions);
            Assert.IsTrue(store.TryLoad("greet", out SkillRecord rec));
            Assert.GreaterOrEqual(rec.Version, 1, "Update must auto-increment the version.");
            Assert.GreaterOrEqual(coord.ListRevisions("greet").Count, 2, "Create + update = two recorded revisions.");
        }

        [Test]
        public void Delete_RemovesFromCatalogAndStore()
        {
            SkillAuthoringCoordinator coord =
                MakeCoordinator(out MutableSkillCatalog catalog, out MemorySkillStore store);
            coord.Create("greet", "d", "i", new[] { "memory" });

            SkillAuthoringResult d = coord.Delete("greet");

            Assert.IsTrue(d.Success, d.Message);
            Assert.IsNull(catalog.Get("greet"));
            Assert.IsFalse(store.TryLoad("greet", out _));
        }

        [Test]
        public void Rehydrate_LoadsPersistedSkillsIntoCatalog()
        {
            MemorySkillStore store = new();
            store.Save(new SkillRecord("greet", "greets", "Say hi.", new List<string> { "memory" }, 2));
            MutableSkillCatalog catalog = new();
            SkillToolResolver resolver = name => new StubTool(name);
            SkillAuthoringCoordinator coord = new(catalog, store, new MemoryLuaScriptVersionStore(), resolver);

            int loaded = coord.RehydrateFromStore();

            Assert.AreEqual(1, loaded);
            Assert.IsNotNull(catalog.Get("greet"), "Persisted skill must reappear in read_skill after rehydrate.");
        }

        [Test]
        public void FailedCreate_DoesNotPublishOrPersist()
        {
            SkillAuthoringCoordinator coordinator = MakeCoordinator(out MutableSkillCatalog catalog, out MemorySkillStore store);
            store.FailSave = true;
            Assert.Throws<IOException>(() => coordinator.Create("new", "", "body", Array.Empty<string>()));
            Assert.IsNull(catalog.Get("new"));
            Assert.IsFalse(store.TryLoad("new", out _));
        }

        [Test]
        public void FailedUpdate_PreservesPublishedAndStoredVersion()
        {
            SkillAuthoringCoordinator coordinator = MakeCoordinator(out MutableSkillCatalog catalog, out MemorySkillStore store);
            coordinator.Create("existing", "", "original", Array.Empty<string>());
            SkillSet original = catalog.Get("existing");
            store.FailSave = true;
            Assert.Throws<IOException>(() => coordinator.Update("existing", null, "replacement", null));
            Assert.AreSame(original, catalog.Get("existing"));
            Assert.IsTrue(store.TryLoad("existing", out SkillRecord persisted));
            Assert.AreEqual("original", persisted.Instructions);
            Assert.AreEqual(0, persisted.Version);
        }

        [Test]
        public void FailedDelete_PreservesPublishedAndStoredSkill()
        {
            SkillAuthoringCoordinator coordinator = MakeCoordinator(out MutableSkillCatalog catalog, out MemorySkillStore store);
            coordinator.Create("existing", "", "original", Array.Empty<string>());
            SkillSet original = catalog.Get("existing");
            store.FailDelete = true;
            Assert.Throws<IOException>(() => coordinator.Delete("existing"));
            Assert.AreSame(original, catalog.Get("existing"));
            Assert.IsTrue(store.TryLoad("existing", out _));
        }

        [Test]
        public void StorageSeesOldCatalog_UntilCommitCompletes()
        {
            SkillAuthoringCoordinator coordinator = MakeCoordinator(out MutableSkillCatalog catalog, out MemorySkillStore store);
            coordinator.Create("existing", "", "original", Array.Empty<string>());
            string visibleWhileWriting = null;
            store.BeforeSave = _ => visibleWhileWriting = catalog.Get("existing").Instructions;
            coordinator.Update("existing", null, "replacement", null);
            Assert.AreEqual("original", visibleWhileWriting);
            Assert.AreEqual("replacement", catalog.Get("existing").Instructions);
        }

        [Test]
        public void PublicationFailure_IsReportedAsCommittedAndGateIsReleased()
        {
            MemorySkillStore store = new();
            Assert.Throws<SkillStorePublicationException>(() => store.MutateAndPublish("saved",
                _ => SkillStoreMutation<bool>.SaveRecord(new SkillRecord("saved", "", "body", null), true),
                _ => throw new InvalidOperationException("Injected publication failure")));
            Assert.IsTrue(store.TryLoad("saved", out SkillRecord saved));
            Assert.AreEqual("body", saved.Instructions);
            Assert.IsTrue(store.Mutate("saved", current => SkillStoreMutation<bool>.NoChange(current != null)));
        }

        [Test]
        public void ReentrantMutation_IsRejectedBeforeTakingAnotherCatalogLock()
        {
            MemorySkillStore store = new();
            SkillAuthoringCoordinator nested = new(new MutableSkillCatalog(), store, null, _ => null);
            Assert.Throws<InvalidOperationException>(() => store.Mutate("outer", _ =>
            {
                nested.Create("inner", "", "body", Array.Empty<string>());
                return SkillStoreMutation<bool>.NoChange(true);
            }));
            Assert.IsFalse(store.TryLoad("inner", out _));
            Assert.IsTrue(nested.Create("inner", "", "body", Array.Empty<string>()).Success);
        }

        [Test]
        public void FailedMutator_CannotChangeStoredRecordByReference()
        {
            MemorySkillStore store = new();
            store.Save(new SkillRecord("original", "", "preserved", null));
            Assert.Throws<InvalidOperationException>(() => store.Mutate<bool>("original", current =>
            {
                current.Instructions = "not committed";
                throw new InvalidOperationException("Injected mutator failure");
            }));
            Assert.IsTrue(store.TryLoad("original", out SkillRecord stored));
            Assert.AreEqual("preserved", stored.Instructions);
        }

        [Test]
        public void Mutation_CannotWriteAKeyItDidNotLock()
        {
            MemorySkillStore store = new();
            Assert.Throws<InvalidOperationException>(() => store.Mutate("one", _ =>
                SkillStoreMutation<bool>.SaveRecord(new SkillRecord("two", "", "body", null), true)));
            Assert.IsFalse(store.TryLoad("two", out _));
        }

        [Test]
        public async Task CompetingCoordinators_PublishLatestCommittedRevision()
        {
            MutableSkillCatalog catalog = new();
            MemorySkillStore store = new();
            SkillAuthoringCoordinator first = new(catalog, store, null, _ => null);
            SkillAuthoringCoordinator second = new(catalog, store, null, _ => null);
            first.Create("shared", "", "initial", Array.Empty<string>());
            Task[] operations = Enumerable.Range(0, 24).Select(index => Task.Run(() =>
            {
                SkillAuthoringCoordinator coordinator = index % 2 == 0 ? first : second;
                Assert.IsTrue(coordinator.Update("shared", null, "revision-" + index, null).Success);
            })).ToArray();
            await Task.WhenAll(operations);
            Assert.IsTrue(store.TryLoad("shared", out SkillRecord persisted));
            Assert.AreEqual(operations.Length, persisted.Version);
            Assert.AreEqual(persisted.Instructions, catalog.Get("shared").Instructions);
        }
    }
}

#if UNITY_5_3_OR_NEWER
namespace CoreAI.Tests.EditMode
{
    using CoreAI.Infrastructure.Llm;

    /// <summary>File commit failures and cross-instance ordering must not lose authored skills.</summary>
    public sealed class FileSkillStoreEditModeTests
    {
        private string _directory;

        [TestCase("Guide", "guide", false)]
        [TestCase("Guide", "guide", true)]
        [TestCase("guide", "guide", false)]
        [TestCase("guide", "guide", true)]
        public void RevisionIdentity_RecreationRetainsOriginalAcrossCaseAndRestart(
            string originalId, string recreatedId, bool restartBeforeRecreate)
        {
            using FileSkillStore store = new(_directory);
            using FileSkillStore reopenedStore = new(_directory);
            MemoryLuaScriptVersionStore versions = new();
            SkillAuthoringCoordinator coordinator = new(new MutableSkillCatalog(), store, versions, _ => null);
            Assert.IsTrue(coordinator.Create(originalId, "", "original body", Array.Empty<string>()).Success);
            Assert.IsTrue(versions.TryGetSnapshot("skill:" + originalId, out LuaScriptVersionRecord original));
            Assert.IsTrue(coordinator.Delete(recreatedId).Success);
            CollectionAssert.AreEqual(original.History, coordinator.ListRevisions(recreatedId),
                "Deletion must not hide history when callers use another casing.");
            if (restartBeforeRecreate)
            {
                coordinator = new SkillAuthoringCoordinator(new MutableSkillCatalog(), reopenedStore, versions, _ => null);
                Assert.AreEqual(0, coordinator.RehydrateFromStore());
            }
            SkillAuthoringResult recreated = coordinator.Create(recreatedId, "", "recreated body", Array.Empty<string>());
            Assert.IsTrue(recreated.Success, recreated.Message);
            Assert.IsTrue(recreated.RevisionRecorded == true);
            using FileSkillStore finalStore = new(_directory);
            SkillAuthoringCoordinator restarted = new(new MutableSkillCatalog(), finalStore, versions, _ => null);
            Assert.AreEqual(1, restarted.RehydrateFromStore());
            SkillAuthoringResult updated = restarted.Update(originalId, null, "updated body", null);
            Assert.IsTrue(updated.Success, updated.Message);
            Assert.IsTrue(updated.RevisionRecorded == true);
            CollectionAssert.AreEquivalent(new[] { "skill:" + originalId }, versions.GetKnownKeys());
            Assert.IsTrue(versions.TryGetSnapshot("skill:" + originalId, out LuaScriptVersionRecord final));
            Assert.AreEqual(original.OriginalLua, final.OriginalLua);
            StringAssert.Contains("updated body", final.CurrentLua);
            Assert.IsTrue(final.History.Any(revision => revision.Source.Contains("recreated body")));
            Assert.IsTrue(final.History.Any(revision => revision.Source.Contains("original body")));
            CollectionAssert.AreEqual(final.History, restarted.ListRevisions(recreatedId));
        }


        [TestCase("greet", "GREET")]
        [TestCase("a:b", "A:B")]
        public void MixedCaseIdentity_UpdatesAndDeletesOneDurableRecord(string id, string alias)
        {
            using FileSkillStore first = new(_directory);
            using FileSkillStore second = new(_directory + Path.DirectorySeparatorChar);
            first.Save(new SkillRecord(id, "initial", "original", null));
            string originalPath = Directory.GetFiles(_directory, "*.json").Single();
            second.Mutate(alias, current => SkillStoreMutation<bool>.SaveRecord(
                new SkillRecord(current.Id, "updated", "replacement", null, current.Version + 1), true));
            Assert.AreEqual(originalPath, Directory.GetFiles(_directory, "*.json").Single(),
                "An alias must resolve the same durable path, not rename it on each operation.");
            Assert.IsTrue(first.TryLoad(id, out SkillRecord updated));
            Assert.AreEqual("replacement", updated.Instructions);
            Assert.AreEqual(1, updated.Version);
            Assert.AreEqual(id, updated.Id, "Public display identity must survive normalization of the file key.");
            Assert.AreEqual(1, Directory.GetFiles(_directory, "*.json").Length);
            second.Delete(alias);
            using FileSkillStore restarted = new(_directory);
            Assert.IsFalse(restarted.TryLoad(id, out _));
            Assert.IsEmpty(restarted.List());
            Assert.IsEmpty(Directory.GetFiles(_directory, "*.json"));
        }

        [Test]
        public void DistinctUnicodeIdentities_RemainIndependentAcrossSaveRestartAndDelete()
        {
            const string firstId = "\u017f";
            const string secondId = "S";
            Assert.IsFalse(StringComparer.OrdinalIgnoreCase.Equals(firstId, secondId));
            using (FileSkillStore store = new(_directory))
            {
                store.Save(new SkillRecord(firstId, "", "first body", null));
                store.Save(new SkillRecord(secondId, "", "second body", null));
                Assert.AreEqual(2, store.List().Count);
            }
            using FileSkillStore restarted = new(_directory);
            Assert.IsTrue(restarted.TryLoad(firstId, out SkillRecord first));
            Assert.IsTrue(restarted.TryLoad(secondId, out SkillRecord second));
            Assert.AreEqual("first body", first.Instructions);
            Assert.AreEqual("second body", second.Instructions);
            restarted.Delete("s");
            Assert.IsTrue(restarted.TryLoad(firstId, out SkillRecord preserved));
            Assert.AreEqual(first.Instructions, preserved.Instructions);
            Assert.IsFalse(restarted.TryLoad(secondId, out _));
        }

        [Test]
        public void OccupiedCanonicalDestination_WithForeignIdentityRejectsWriteWithoutDataLoss()
        {
            using FileSkillStore store = new(_directory);
            store.Save(new SkillRecord("guide", "", "original", null));
            string path = Directory.GetFiles(_directory, "*.json").Single();
            string foreignJson = JsonConvert.SerializeObject(new SkillRecord("foreign", "", "preserve me", null));
            File.WriteAllText(path, foreignJson);
            Assert.Throws<InvalidDataException>(() => store.Save(new SkillRecord("guide", "", "replacement", null)));
            Assert.AreEqual(foreignJson, File.ReadAllText(path));
            Assert.AreEqual(1, Directory.GetFiles(_directory, "*.json").Length);
        }

        [Test]
        public void InvalidUnicodeIdentity_CannotOverwriteReplacementCharacterIdentity()
        {
            const string validId = "guide\uFFFD";
            using FileSkillStore store = new(_directory);
            store.Save(new SkillRecord(validId, "", "original", null));
            string path = Directory.GetFiles(_directory, "*.json").Single();
            string before = File.ReadAllText(path);
            Assert.Throws<System.Text.EncoderFallbackException>(() =>
                store.Save(new SkillRecord("guide\uD800", "", "replacement", null)));
            Assert.AreEqual(before, File.ReadAllText(path));
            Assert.IsTrue(store.TryLoad(validId, out SkillRecord preserved));
            Assert.AreEqual("original", preserved.Instructions);
        }

        [TestCase("greet", "GREET", "greet.json")]
        [TestCase("a:b", "A:B", "a_b_08bd8540.json")]
        [TestCase("A:B", "a:b", "A_B_4c851f00.json")]
        [TestCase("greet", "GREET", "skill-4c3511f697eed33f27592d5b20acba9fd451018c31ff3d6e7f108042e29da988.json")]
        public void LegacyFile_MigratesWithoutChangingBytesAndDoesNotResurrect(string id, string alias, string oldFilename)
        {
            // WHY: these filenames are captured pre-migration wire examples, including the old Windows FNV suffix.
            string legacyPath = Path.Combine(_directory, oldFilename);
            string oldJson = JsonConvert.SerializeObject(new SkillRecord(id, "legacy", "old entry", null, 7));
            File.WriteAllText(legacyPath, oldJson);
            using FileSkillStore store = new(_directory);
            Assert.IsTrue(store.TryLoad(alias, out SkillRecord migrated));
            Assert.AreEqual(id, migrated.Id);
            Assert.IsFalse(File.Exists(legacyPath));
            string canonicalPath = Directory.GetFiles(_directory, "*.json").Single();
            Assert.AreEqual(oldJson, File.ReadAllText(canonicalPath));
            store.Save(new SkillRecord(id, "updated", "updated entry", null, migrated.Version + 1));
            store.Delete(alias);
            using FileSkillStore restarted = new(_directory);
            Assert.IsEmpty(restarted.List());
            Assert.IsFalse(restarted.TryLoad(id, out _));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void AmbiguousLegacyRecords_RejectMutationsAndPreserveEveryCopy(bool includeCanonical)
        {
            using FileSkillStore store = new(_directory);
            if (includeCanonical) store.Save(new SkillRecord("a:b", "", "canonical", null));
            else File.WriteAllText(Path.Combine(_directory, "a_b_08bd8540.json"),
                JsonConvert.SerializeObject(new SkillRecord("a:b", "", "first legacy", null)));
            File.WriteAllText(Path.Combine(_directory, "A_B_4c851f00.json"),
                JsonConvert.SerializeObject(new SkillRecord("A:B", "", "second legacy", null)));
            Dictionary<string, string> before = Directory.GetFiles(_directory, "*.json")
                .ToDictionary(path => path, File.ReadAllText);
            Assert.IsFalse(store.TryLoad("a:b", out SkillRecord unread));
            Assert.IsNull(unread);
            Assert.IsEmpty(store.List(), "Conflicting copies must not publish an arbitrary winner.");
            Assert.Throws<InvalidDataException>(() => store.Save(new SkillRecord("a:b", "", "replacement", null)));
            Assert.Throws<InvalidDataException>(() => store.Delete("A:B"));
            CollectionAssert.AreEquivalent(before.Keys, Directory.GetFiles(_directory, "*.json"));
            foreach (KeyValuePair<string, string> entry in before)
                Assert.AreEqual(entry.Value, File.ReadAllText(entry.Key));
        }

        [Test]
        public void FailedLegacyRename_PreservesBytesAndReportsNoLoadedRecord()
        {
            if (Path.DirectorySeparatorChar != '\\') Assert.Ignore("Windows sharing rules inject the rename failure.");
            string legacyPath = Path.Combine(_directory, "greet.json");
            string original = JsonConvert.SerializeObject(new SkillRecord("greet", "", "original", null));
            File.WriteAllText(legacyPath, original);
            using FileSkillStore store = new(_directory);
            using (FileStream held = new(legacyPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                Assert.IsFalse(store.TryLoad("GREET", out SkillRecord unread));
                Assert.IsNull(unread);
                Assert.Catch<IOException>(() => store.Save(new SkillRecord("greet", "", "replacement", null)));
                Assert.AreEqual(original, File.ReadAllText(legacyPath));
                Assert.AreEqual(1, Directory.GetFiles(_directory, "*.json").Length);
            }
            Assert.IsTrue(store.TryLoad("GREET", out SkillRecord restored));
            Assert.AreEqual("original", restored.Instructions);
        }

        [Test]
        public void MultiFileSkill_FileRestartPreservesReferenceAndMainOnlyUpdate()
        {
            MutableSkillCatalog catalog = new();
            SkillSet original = SkillSet.FromTextParts("Guide", "initial", new KeyValuePair<string, string>[]
            {
                new("SKILL.md", "Main instructions"), new("references/api.md", "Reference instructions")
            });
            catalog.AddOrReplace(original);
            using (FileSkillStore store = new(_directory))
            {
                SkillAuthoringCoordinator coordinator = new(catalog, store, null, _ => null);
                Assert.IsTrue(coordinator.Update("GUIDE", "description", null, null).Success);
                Assert.IsTrue(coordinator.Update("guide", null, "New entry", null).Success);
            }
            using FileSkillStore restartedStore = new(_directory);
            MutableSkillCatalog restartedCatalog = new();
            SkillAuthoringCoordinator restarted = new(restartedCatalog, restartedStore, null, _ => null);
            Assert.AreEqual(1, restarted.RehydrateFromStore());
            Assert.IsTrue(restartedCatalog.Get("guide").TryGetSection("SKILL.md", out SkillSection entry));
            Assert.AreEqual("New entry", entry.Content);
            JObject reference = JObject.Parse(ReadSkillLlmTool.ReadSkillJson(restartedCatalog, "guide", "references/api.md"));
            Assert.AreEqual(original.Sections[1].Content, (string)reference["instructions"]);
            JObject all = JObject.Parse(ReadSkillLlmTool.ReadSkillJson(restartedCatalog, "guide", all: true));
            StringAssert.Contains(original.Sections[1].Content, (string)all["instructions"]);
        }

        [SetUp]
        public void SetUp()
        {
            _directory = Path.Combine(Path.GetTempPath(), "coreai-skills-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_directory);
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
        }

        [TestCase("broken JSON")]
        [TestCase("null")]
        [TestCase("{}")]
        [TestCase("{\"Id\":\"another\",\"Instructions\":\"existing\"}")]
        public void CorruptExistingRecord_RejectsEveryWriteWithoutChangingBytes(string corrupt)
        {
            string path = Path.Combine(_directory, "skill.json");
            File.WriteAllText(path, corrupt);
            using FileSkillStore store = new(_directory);
            bool invoked = false;
            Assert.Throws<InvalidDataException>(() => store.Mutate("skill", current =>
            {
                invoked = true;
                return SkillStoreMutation<bool>.SaveRecord(new SkillRecord("skill", "", "replacement", null), true);
            }));
            Assert.IsFalse(invoked);
            Assert.Throws<InvalidDataException>(() => store.Save(new SkillRecord("skill", "", "replacement", null)));
            Assert.Throws<InvalidDataException>(() => store.Delete("skill"));
            Assert.AreEqual(corrupt, File.ReadAllText(path));
        }

        [Test]
        public void UnreadableRecord_RejectsMutationBeforeInvokingCallback()
        {
            using FileSkillStore store = new(_directory);
            store.Save(new SkillRecord("skill", "", "original", null));
            string path = Directory.GetFiles(_directory, "*.json").Single();
            bool invoked = false;
            using (FileStream held = new(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                Assert.Catch<IOException>(() => store.Mutate("skill", current =>
                {
                    invoked = true;
                    return SkillStoreMutation<bool>.DeleteRecord(true);
                }));
            }
            Assert.IsFalse(invoked);
            Assert.IsTrue(store.TryLoad("skill", out SkillRecord preserved));
            Assert.AreEqual("original", preserved.Instructions);
        }

        [Test]
        public void FailedFileReplacement_PreservesDiskAndLiveCatalog()
        {
            if (Path.DirectorySeparatorChar != '\\') Assert.Ignore("Windows sharing rules provide the injected replacement failure.");
            using FileSkillStore store = new(_directory);
            MutableSkillCatalog catalog = new();
            SkillAuthoringCoordinator coordinator = new(catalog, store, null, _ => null);
            coordinator.Create("skill", "", "original", Array.Empty<string>());
            string path = Directory.GetFiles(_directory, "*.json").Single();
            string before = File.ReadAllText(path);
            using (FileStream held = new(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                Assert.Catch<IOException>(() => coordinator.Update("skill", null, "replacement", null));
            }
            Assert.AreEqual(before, File.ReadAllText(path));
            Assert.AreEqual("original", catalog.Get("skill").Instructions);
            Assert.IsEmpty(Directory.GetFiles(_directory, "*.tmp"));
        }

        [Test]
        public async Task TwoInstances_CommitAndPublishInTheSameOrder()
        {
            using FileSkillStore first = new(_directory);
            string alias = Path.DirectorySeparatorChar == '\\' ? _directory.ToUpperInvariant() : _directory;
            using FileSkillStore second = new(alias);
            first.Save(new SkillRecord("skill", "", "initial", null));
            System.Collections.Concurrent.ConcurrentQueue<int> published = new();
            Task[] operations = Enumerable.Range(0, 24).Select(index => Task.Run(() =>
            {
                FileSkillStore store = index % 2 == 0 ? first : second;
                string id = index % 2 == 0 ? "skill" : "SKILL";
                store.MutateAndPublish(id, current =>
                {
                    SkillRecord next = new(id, "", "revision-" + index, null, current.Version + 1);
                    return SkillStoreMutation<int>.SaveRecord(next, next.Version);
                }, version => published.Enqueue(version));
            })).ToArray();
            await Task.WhenAll(operations);
            CollectionAssert.AreEqual(Enumerable.Range(1, operations.Length), published.ToArray());
            Assert.IsTrue(first.TryLoad("skill", out SkillRecord record));
            Assert.AreEqual(operations.Length, record.Version);
            Assert.IsEmpty(Directory.GetFiles(_directory, "*.tmp"));
        }

        [Test]
        public async Task TwoCatalogsSharingFiles_KeepOrderedRevisionsWithoutDeadlock()
        {
            using FileSkillStore firstStore = new(_directory);
            using FileSkillStore secondStore = new(_directory);
            MutableSkillCatalog firstCatalog = new();
            MutableSkillCatalog secondCatalog = new();
            SkillAuthoringCoordinator first = new(firstCatalog, firstStore, null, _ => null);
            SkillAuthoringCoordinator second = new(secondCatalog, secondStore, null, _ => null);
            first.Create("skill", "", "initial", Array.Empty<string>());
            second.RehydrateFromStore();
            Task<SkillAuthoringResult>[] operations = Enumerable.Range(0, 24).Select(index => Task.Run(() =>
                (index % 2 == 0 ? first : second).Update("skill", null, "revision-" + index, null))).ToArray();
            SkillAuthoringResult[] results = await Task.WhenAll(operations);
            Assert.IsTrue(results.All(result => result.Success));
            CollectionAssert.AreEqual(Enumerable.Range(1, operations.Length), results.Select(result => result.Record.Version).OrderBy(version => version));
            for (int parity = 0; parity < 2; parity++)
            {
                SkillAuthoringResult last = results.Where((result, index) => index % 2 == parity)
                    .OrderByDescending(result => result.Record.Version).First();
                MutableSkillCatalog catalog = parity == 0 ? firstCatalog : secondCatalog;
                Assert.AreEqual(last.Record.Instructions, catalog.Get("skill").Instructions);
            }
            Assert.IsTrue(firstStore.TryLoad("skill", out SkillRecord stored));
            Assert.AreEqual(results.Max(result => result.Record.Version), stored.Version);
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task SaveAndDelete_WaitForOtherInstancesCommittedPublication(bool delete)
        {
            using FileSkillStore first = new(_directory);
            using FileSkillStore second = new(_directory);
            using ManualResetEventSlim entered = new();
            using ManualResetEventSlim release = new();
            using ManualResetEventSlim secondStarted = new();
            first.Save(new SkillRecord("skill", "", "original", null));
            Task firstTask = Task.Run(() => first.MutateAndPublish("skill", current =>
                SkillStoreMutation<bool>.SaveRecord(new SkillRecord("skill", "", "committed", null), true), _ =>
                {
                    // WHY: deliberately pause publication to expose a write that bypasses the shared key gate.
                    entered.Set();
                    if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("Test release was not signalled.");
                }));
            Task secondTask = null;
            try
            {
                Assert.IsTrue(entered.Wait(TimeSpan.FromSeconds(5)));
                secondTask = Task.Run(() =>
                {
                    secondStarted.Set();
                    if (delete) second.Delete("skill");
                    else second.Save(new SkillRecord("skill", "", "second", null));
                });
                Assert.IsTrue(secondStarted.Wait(TimeSpan.FromSeconds(5)));
                Assert.IsFalse(secondTask.Wait(TimeSpan.FromMilliseconds(200)), "A later write overtook committed publication.");
            }
            finally
            {
                release.Set();
                await firstTask;
                if (secondTask != null) await secondTask;
            }
            Assert.AreEqual(!delete, first.TryLoad("skill", out SkillRecord final));
            if (!delete) Assert.AreEqual("second", final.Instructions);
        }

        [Test]
        public void ReentrantPublication_FailsLoudlyAfterCommitAndReleasesGate()
        {
            using FileSkillStore store = new(_directory);
            Assert.Throws<SkillStorePublicationException>(() => store.MutateAndPublish("skill", _ =>
                SkillStoreMutation<bool>.SaveRecord(new SkillRecord("skill", "", "committed", null), true),
                committed => store.TryLoad("skill", out _)));
            Assert.IsTrue(store.TryLoad("skill", out SkillRecord record));
            Assert.AreEqual("committed", record.Instructions);
            store.Delete("skill");
            Assert.IsFalse(store.TryLoad("skill", out _));
        }
    }
}
#endif
