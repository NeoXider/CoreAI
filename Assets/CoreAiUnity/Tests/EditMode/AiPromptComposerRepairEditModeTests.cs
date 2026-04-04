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