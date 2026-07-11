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
                "coreai_world_spawn", "coreai_world_change", "coreai_world_set_color",
                "coreai_world_destroy", "coreai_world_list_prefabs",
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
