using System;
using System.Collections.Generic;
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
            Assert.IsTrue(tool.AllowDuplicates);
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

            Assert.IsTrue(inner.FindObjectsCalled, "IAIFunctionsLlmTool function should have been called by function name.");
            Assert.That(json, Does.Contain("Player"));
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
    }
}
