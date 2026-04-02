using CoreAI.Ai;
using CoreAI.Sandbox;
using MoonSharp.Interpreter;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode
{
    /// <summary>
    /// Проверка извлечения Lua из «ответа Programmer» и исполнения в песочнице.
    /// Полный рантайм-пайплайн (оркестратор → команда → MoonSharp) пока не подключает Lua автоматически — см. AiGameCommandRouter.
    /// </summary>
    public sealed class ProgrammerLuaPipelineEditModeTests
    {
        [Test]
        public void ProgrammerLuaResponseParser_ExtractsBlock()
        {
            const string raw = "Here is the patch:\n```lua\nreturn add(2, 3)\n```\nDone.";
            Assert.IsTrue(ProgrammerLuaResponseParser.TryExtractLuaCode(raw, out var lua));
            Assert.AreEqual("return add(2, 3)", lua);
        }

        [Test]
        public void ProgrammerLuaResponseParser_NoBlock_ReturnsFalse()
        {
            Assert.IsFalse(ProgrammerLuaResponseParser.TryExtractLuaCode("only json {}", out _));
        }

        [Test]
        public void ExtractedLua_RunsInSecureSandbox_WithWhitelistedApi()
        {
            const string raw = "```lua\nreturn add(10, 1)\n```";
            Assert.IsTrue(ProgrammerLuaResponseParser.TryExtractLuaCode(raw, out var lua));
            var env = new SecureLuaEnvironment();
            var reg = new LuaApiRegistry();
            reg.Register("add", new System.Func<double, double, double>((a, b) => a + b));
            var script = env.CreateScript(reg);
            var r = script.DoString(lua);
            Assert.AreEqual(11, (int)r.Number);
        }
    }
}
