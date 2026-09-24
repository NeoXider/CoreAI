using CoreAI.Ai;
using CoreAI.Session;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode
{
    public sealed class AiPromptComposerRepairEditModeTests
    {
        [Test]
        public void BuildUserPayload_LongLuaRepairError_ClippedWithCount()
        {
            AiPromptComposer composer = new(
                new BuiltInDefaultAgentSystemPromptProvider(),
                new NoAgentUserPromptTemplateProvider(),
                new NullLuaScriptVersionStore());
            AiTaskRequest task = new()
            {
                RoleId = BuiltInAgentRoleIds.Programmer,
                Hint = "fix",
                LuaRepairGeneration = 1,
                LuaRepairPreviousCode = new string('c', 1500),
                LuaRepairErrorMessage = new string('e', 700)
            };

            string u = composer.BuildUserPayload(new GameSessionSnapshot(), task);

            StringAssert.Contains("lua_error=" + new string('e', 500) + "…[+200 chars];", u);
            StringAssert.Contains("fix_this_lua=" + new string('c', 1200) + "…[+300 chars]", u);
        }

        [Test]
        public void BuildUserPayload_AppendsLuaRepairFields()
        {
            AiPromptComposer composer = new(
                new BuiltInDefaultAgentSystemPromptProvider(),
                new NoAgentUserPromptTemplateProvider(),
                new NullLuaScriptVersionStore());
            GameSessionSnapshot snap = new();
            AiTaskRequest task = new()
            {
                RoleId = BuiltInAgentRoleIds.Programmer,
                Hint = "fix",
                LuaRepairGeneration = 2,
                LuaRepairPreviousCode = "return x",
                LuaRepairErrorMessage = "boom"
            };
            string u = composer.BuildUserPayload(snap, task);
            StringAssert.Contains("lua_repair_generation=2", u);
            StringAssert.Contains("lua_error=boom", u);
            StringAssert.Contains("fix_this_lua=", u);
        }
    }
}
