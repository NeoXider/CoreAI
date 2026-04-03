using CoreAI.Ai;
using CoreAI.Session;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode
{
    public sealed class AiPromptComposerRepairEditModeTests
    {
        [Test]
        public void BuildUserPayload_AppendsLuaRepairFields()
        {
            var composer = new AiPromptComposer(
                new BuiltInDefaultAgentSystemPromptProvider(),
                new NoAgentUserPromptTemplateProvider(),
                new NullLuaScriptVersionStore());
            var snap = new GameSessionSnapshot();
            var task = new AiTaskRequest
            {
                RoleId = BuiltInAgentRoleIds.Programmer,
                Hint = "fix",
                LuaRepairGeneration = 2,
                LuaRepairPreviousCode = "return x",
                LuaRepairErrorMessage = "boom"
            };
            var u = composer.BuildUserPayload(snap, task);
            StringAssert.Contains("lua_repair_generation=2", u);
            StringAssert.Contains("lua_error=boom", u);
            StringAssert.Contains("fix_this_lua=", u);
        }
    }
}
