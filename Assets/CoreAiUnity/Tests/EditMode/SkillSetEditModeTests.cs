using System;
using System.Collections.Generic;
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

            Assert.That(json, Does.Contain("error"));
            Assert.That(json, Does.Contain("not found"));
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
            Assert.That(json, Does.Contain("not found"));
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
            Assert.That(json, Does.Contain("not found"));
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

            public void Save(SkillRecord record)
            {
                _m[record.Id] = record;
            }

            public bool TryLoad(string id, out SkillRecord record)
            {
                return _m.TryGetValue(id, out record);
            }

            public IReadOnlyList<SkillRecord> List()
            {
                return new List<SkillRecord>(_m.Values);
            }

            public void Delete(string id)
            {
                _m.Remove(id);
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
    }
}
