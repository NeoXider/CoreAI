using System.Collections.Generic;
using System.Threading.Tasks;
using CoreAI.Ai;
using Microsoft.Extensions.AI;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode
{
    /// <summary>
    /// The built-in "Lua Modding" skill: an instructions-only SkillSet the Programmer role loads
    /// on demand via read_skill. The reference must actually cover the API surface the prompt
    /// only summarizes — these tests pin the names so an API rename breaks loudly here.
    /// </summary>
    public sealed class LuaModdingSkillEditModeTests
    {
        private static SkillSet BuildSkill()
        {
            return new SkillSet(
                BuiltInLuaModdingSkillText.SkillName,
                BuiltInLuaModdingSkillText.SkillDescription,
                BuiltInLuaModdingSkillText.Instructions);
        }

        [Test]
        public void Instructions_CoverEveryApiFamilyThePromptSummarizes()
        {
            string text = BuiltInLuaModdingSkillText.Instructions;

            string[] required =
            {
                "read_skill('Rbx API')", "Instance.new",
                "coreai_world_find", "coreai_world_pos", "coreai_world_exists",
                "unity_find", "unity_set_member", "unity_call",
                "hooks_every", "hooks_on('tick'", "store_set", "store_get",
                "events_emit", "mods_export", "mods_get", "mods_call", "mods_list_exports",
                "input_key", "input_key_down", "input_mouse_button", "input_axis",
                "logic_define", "report", "print"
            };
            foreach (string api in required)
            {
                StringAssert.Contains(api, text, $"skill reference lost coverage of '{api}'");
            }

            // WHY: the classic build APIs were removed from the sandbox; documenting them again
            // would make the skill over-promise, so their absence is pinned as hard as coverage.
            string[] removedBuildApis =
            {
                "coreai_world_spawn", "coreai_world_change", "coreai_world_set_color",
                "coreai_world_destroy", "coreai_world_spawn_batch"
            };
            foreach (string api in removedBuildApis)
            {
                StringAssert.DoesNotContain(api, text, $"removed build API '{api}' must not be documented");
            }

            // The worked example and the failure catalog are the reason this skill exists.
            StringAssert.Contains("```lua", text);
            StringAssert.Contains("Common errors", text);
            StringAssert.Contains("double-escape", text);
        }

        private static async Task<string> ReadAsync(ILlmTool tool, string skillName)
        {
            AIFunction function = ((IAIFunctionLlmTool)tool).CreateAIFunction();
            object result = await function.InvokeAsync(
                new AIFunctionArguments(new Dictionary<string, object> { ["skill_name"] = skillName }));
            return result?.ToString() ?? "";
        }

        [Test]
        public async Task ReadSkillTool_ReturnsInstructionsForInstructionsOnlySkill()
        {
            ILlmTool tool = ReadSkillLlmTool.Create(new List<SkillSet> { BuildSkill() });
            Assert.AreEqual("read_skill", tool.Name);

            string json = await ReadAsync(tool, BuiltInLuaModdingSkillText.SkillName);

            StringAssert.Contains("\"success\":true", json);
            StringAssert.Contains("mods_export", json);
            StringAssert.Contains("hooks_every", json);
            // Instructions-only skill: readable payload with an empty tool list, not an error.
            StringAssert.Contains("\"tools\":[]", json);
        }

        [Test]
        public async Task ReadSkillTool_UnknownName_ListsTheLuaSkillAsAvailable()
        {
            ILlmTool tool = ReadSkillLlmTool.Create(new List<SkillSet> { BuildSkill() });

            string json = await ReadAsync(tool, "no_such_skill");

            StringAssert.Contains("\"success\":false", json);
            StringAssert.Contains(BuiltInLuaModdingSkillText.SkillName, json);
        }

        [Test]
        public void ProgrammerPrompt_PointsAtTheSkill()
        {
            StringAssert.Contains("read_skill('Lua Modding')", BuiltInAgentSystemPromptTexts.Programmer);
        }

        [Test]
        public void ResourcesOverride_WhenPresent_MatchesTheBuiltInText()
        {
            // The txt is the canonical SO/Resources-facing copy of the built-in fallback; the two
            // must never drift, otherwise editor hosts and code-only hosts see different references.
            UnityEngine.TextAsset overrideAsset =
                UnityEngine.Resources.Load<UnityEngine.TextAsset>("AgentSkills/LuaModding");
            if (overrideAsset == null)
            {
                Assert.Ignore("No Resources/AgentSkills/LuaModding override in this project.");
            }

            Assert.AreEqual(BuiltInLuaModdingSkillText.Instructions, overrideAsset.text);
        }

        [Test]
        public void RbxApiInstructions_CoverTheApiFamiliesThePromptSummarizes()
        {
            string text = BuiltInRbxApiSkillText.Instructions;

            string[] required =
            {
                "Instance.new", "game:GetService", "workspace",
                "Vector3", "CFrame", "Color3", "UDim", "Random", "Enum",
                "Position", "Size", "CFrame", "Transparency", "Anchored", "CanCollide",
                "SetAttribute", "GetAttribute", "AddTag", "HasTag",
                "BAD_ARGUMENT", "INSTANCE_DESTROYED", "PARENT_LOCKED", "NOT_IMPLEMENTED",
                "1 stud = 0.28 m", "LookVector is -Z"
            };
            foreach (string api in required)
            {
                StringAssert.Contains(api, text, $"Rbx skill reference lost coverage of '{api}'");
            }

            // Worked examples and the not-implemented catalog are the reason this skill exists.
            StringAssert.Contains("```lua", text);
            StringAssert.Contains("Not implemented", text);
        }

        [Test]
        public async Task ReadSkillTool_ReturnsRbxApiInstructions()
        {
            SkillSet rbx = new(
                BuiltInRbxApiSkillText.SkillName,
                BuiltInRbxApiSkillText.SkillDescription,
                BuiltInRbxApiSkillText.Instructions);
            ILlmTool tool = ReadSkillLlmTool.Create(new List<SkillSet> { BuildSkill(), rbx });

            string json = await ReadAsync(tool, BuiltInRbxApiSkillText.SkillName);

            StringAssert.Contains("\"success\":true", json);
            StringAssert.Contains("Instance.new", json);
        }

        [Test]
        public void ProgrammerPrompt_PointsAtTheRbxSkill()
        {
            StringAssert.Contains("read_skill('Rbx API')", BuiltInAgentSystemPromptTexts.Programmer);
        }

        [Test]
        public void RbxApiResourcesOverride_WhenPresent_MatchesTheBuiltInText()
        {
            UnityEngine.TextAsset overrideAsset =
                UnityEngine.Resources.Load<UnityEngine.TextAsset>("AgentSkills/RbxApi");
            if (overrideAsset == null)
            {
                Assert.Ignore("No Resources/AgentSkills/RbxApi override in this project.");
            }

            Assert.AreEqual(BuiltInRbxApiSkillText.Instructions, overrideAsset.text);
        }

        [Test]
        public void FullLuaInstructions_CoverTheReflectionSurface()
        {
            string text = BuiltInFullLuaSkillText.Instructions;

            string[] required =
            {
                "unity_list_objects", "unity_find_all", "unity_find_by_tag", "unity_find_by_component",
                "unity_describe_object", "unity_get_transform", "unity_set_position",
                "unity_set_rotation_euler", "unity_set_scale", "unity_parent", "unity_get_children",
                "unity_list_components", "unity_get_member", "unity_set_member", "unity_call",
                "Success/Output/Error"
            };
            foreach (string api in required)
            {
                StringAssert.Contains(api, text, $"Full Lua skill reference lost coverage of '{api}'");
            }

            StringAssert.Contains("```lua", text);
        }

        [Test]
        public async Task ReadSkillTool_ReturnsFullLuaInstructions()
        {
            SkillSet fullLua = new(
                BuiltInFullLuaSkillText.SkillName,
                BuiltInFullLuaSkillText.SkillDescription,
                BuiltInFullLuaSkillText.Instructions);
            ILlmTool tool = ReadSkillLlmTool.Create(new List<SkillSet> { BuildSkill(), fullLua });

            string json = await ReadAsync(tool, BuiltInFullLuaSkillText.SkillName);

            StringAssert.Contains("\"success\":true", json);
            StringAssert.Contains("unity_set_member", json);
        }

        [Test]
        public void ProgrammerPrompt_PointsAtTheFullLuaSkill()
        {
            StringAssert.Contains("read_skill('Full Lua')", BuiltInAgentSystemPromptTexts.Programmer);
        }

        [Test]
        public void FullLuaResourcesOverride_WhenPresent_MatchesTheBuiltInText()
        {
            UnityEngine.TextAsset overrideAsset =
                UnityEngine.Resources.Load<UnityEngine.TextAsset>("AgentSkills/FullLua");
            if (overrideAsset == null)
            {
                Assert.Ignore("No Resources/AgentSkills/FullLua override in this project.");
            }

            Assert.AreEqual(BuiltInFullLuaSkillText.Instructions, overrideAsset.text);
        }

        [Test]
        public void AddSkillForRole_SameName_ReplacesInsteadOfDuplicating()
        {
            AgentMemoryPolicy policy = new();
            policy.AddSkillForRole("TestRole", BuildSkill());
            policy.AddSkillForRole("TestRole",
                SkillSet.FromTextContent(BuiltInLuaModdingSkillText.SkillName, "v2", "updated body"));

            IReadOnlyList<ILlmTool> tools = policy.GetToolsForRole("TestRole");
            int readSkillCount = 0;
            foreach (ILlmTool tool in tools)
            {
                if (tool.Name == "read_skill")
                {
                    readSkillCount++;
                }
            }

            Assert.AreEqual(1, readSkillCount, "meta-tools must be registered once per role");
        }
    }
}
