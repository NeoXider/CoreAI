using System;
using System.Collections.Generic;
using CoreAI.Ai;
using CoreAI.AgentMemory;
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

        private static DelegateLlmTool MakeTool(string name) =>
            new(name, $"Test tool: {name}", new Action(() => { }));

        private static SkillSet MakeCraftingSkill() => new("Crafting",
            "Forge weapons and armor from raw materials",
            "1. Call get_recipes to list recipes.\n2. Call craft_item to craft.",
            MakeTool("get_recipes"), MakeTool("craft_item"));

        private static SkillSet MakeCombatSkill() => new("Combat",
            "Fight enemies and manage encounters",
            "Call get_enemy_stats before attacking. Use calculate_damage for hits.",
            MakeTool("get_enemy_stats"), MakeTool("calculate_damage"));

        private static SkillSet MakeLoreSkill() => new("Lore",
            "World knowledge and history",
            "Call search_codex to find lore entries.",
            MakeTool("search_codex"));

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
        public void Constructor_NoTools_Throws()
        {
            Assert.Throws<ArgumentException>(() =>
                new SkillSet("Empty", "desc", "inst"));
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
        public void ReadSkillTool_Create_ReturnsDelegateLlmTool()
        {
            List<SkillSet> skills = new() { MakeCraftingSkill(), MakeCombatSkill() };
            DelegateLlmTool tool = ReadSkillLlmTool.Create(skills);

            Assert.AreEqual("read_skill", tool.Name);
            Assert.IsTrue(tool.AllowDuplicates);
            Assert.IsNotNull(tool.ActionDelegate);
        }

        [Test]
        public void ReadSkillTool_Execute_KnownSkill_ReturnsInstructions()
        {
            SkillSet crafting = MakeCraftingSkill();
            DelegateLlmTool tool = ReadSkillLlmTool.Create(new List<SkillSet> { crafting });

            // Invoke the delegate
            Func<string, object> fn = (Func<string, object>)tool.ActionDelegate;
            object result = fn("Crafting");

            string json = Newtonsoft.Json.JsonConvert.SerializeObject(result);
            Assert.That(json, Does.Contain("Crafting"));
            Assert.That(json, Does.Contain("get_recipes"));
            Assert.That(json, Does.Contain("instructions"));
            Assert.That(json, Does.Contain("call_skill_tool"), "Should contain usage hint for call_skill_tool proxy.");
            Assert.That(json, Does.Contain("tool_name"), "Should contain tool_name field in schema.");
        }

        [Test]
        public void ReadSkillTool_Execute_UnknownSkill_ReturnsError()
        {
            DelegateLlmTool tool = ReadSkillLlmTool.Create(new List<SkillSet> { MakeCraftingSkill() });
            Func<string, object> fn = (Func<string, object>)tool.ActionDelegate;
            object result = fn("NonExistent");

            string json = Newtonsoft.Json.JsonConvert.SerializeObject(result);
            Assert.That(json, Does.Contain("error"));
            Assert.That(json, Does.Contain("not found"));
        }

        [Test]
        public void ReadSkillTool_Execute_CaseInsensitive()
        {
            DelegateLlmTool tool = ReadSkillLlmTool.Create(new List<SkillSet> { MakeCraftingSkill() });
            Func<string, object> fn = (Func<string, object>)tool.ActionDelegate;
            object result = fn("crafting"); // lowercase

            string json = Newtonsoft.Json.JsonConvert.SerializeObject(result);
            Assert.That(json, Does.Contain("Crafting"));
            Assert.That(json, Does.Contain("instructions"));
        }

        [Test]
        public void ReadSkillTool_Execute_EmptyName_ReturnsError()
        {
            DelegateLlmTool tool = ReadSkillLlmTool.Create(new List<SkillSet> { MakeCraftingSkill() });
            Func<string, object> fn = (Func<string, object>)tool.ActionDelegate;
            object result = fn("");

            string json = Newtonsoft.Json.JsonConvert.SerializeObject(result);
            Assert.That(json, Does.Contain("error"));
        }

        // ══════════════════════════════════════════════════════════════════════
        //  CallSkillToolLlmTool (proxy)
        // ══════════════════════════════════════════════════════════════════════

        [Test]
        public void CallSkillTool_Create_ReturnsDelegateLlmTool()
        {
            List<SkillSet> skills = new() { MakeCraftingSkill() };
            DelegateLlmTool tool = CallSkillToolLlmTool.Create(skills);

            Assert.AreEqual("call_skill_tool", tool.Name);
            Assert.IsTrue(tool.AllowDuplicates);
        }

        [Test]
        public void CallSkillTool_Execute_UnknownTool_ReturnsError()
        {
            DelegateLlmTool tool = CallSkillToolLlmTool.Create(new List<SkillSet> { MakeCraftingSkill() });
            Func<string, string, object> fn = (Func<string, string, object>)tool.ActionDelegate;
            object result = fn("nonexistent", "{}");

            string json = Newtonsoft.Json.JsonConvert.SerializeObject(result);
            Assert.That(json, Does.Contain("error"));
            Assert.That(json, Does.Contain("not found"));
        }

        [Test]
        public void CallSkillTool_Execute_KnownTool_Invokes()
        {
            bool called = false;
            DelegateLlmTool inner = new("test_tool", "A test",
                new Func<string, object>(x => { called = true; return new { echo = x }; }));
            SkillSet skill = new("TestSkill", "Test", "instructions", inner);

            DelegateLlmTool proxy = CallSkillToolLlmTool.Create(new List<SkillSet> { skill });
            Func<string, string, object> fn = (Func<string, string, object>)proxy.ActionDelegate;
            object result = fn("test_tool", "{\"x\": \"hello\"}");

            Assert.IsTrue(called, "Inner tool should have been called.");
            string json = Newtonsoft.Json.JsonConvert.SerializeObject(result);
            Assert.That(json, Does.Contain("hello"));
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
                if (tool.Name == "read_skill") hasReadSkill = true;
                if (tool.Name == "call_skill_tool") hasCallSkillTool = true;
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
        public void ApplyToPolicy_WithSkills_RegistersCatalogProvider()
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

            // RuntimeContextProvider should produce catalog
            Assert.IsTrue(policy.TryGetRuntimeContextProvider("test_catalog", out var provider),
                "RuntimeContextProvider should be registered.");
            string context = provider.BuildContext(
                new AiTaskRequest { RoleId = "test_catalog" }, "test_catalog", "trace");

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
    }
}
