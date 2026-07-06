using CoreAI.Ai;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode
{
    public sealed class AiLuaPayloadParserEditModeTests
    {
        [Test]
        public void TryGetExecutableLua_FromMarkdown()
        {
            const string s = "x\n```lua\nreturn add(1,2)\n```";
            Assert.IsTrue(AiLuaPayloadParser.TryGetExecutableLua(s, out string lua));
            Assert.AreEqual("return add(1,2)", lua);
        }

        [Test]
        public void TryGetExecutableLua_FromExecuteLuaJson()
        {
            const string s = "{\"commandType\":\"ExecuteLua\",\"payload\":{\"code\":\"return 3\"}}";
            Assert.IsTrue(AiLuaPayloadParser.TryGetExecutableLua(s, out string lua));
            Assert.AreEqual("return 3", lua);
        }

        [Test]
        public void TryGetExecutableLua_FromExecuteLuaJson_InMarkdownFence()
        {
            const string s = "```json\n{\"commandType\":\"ExecuteLua\",\"payload\":{\"code\":\"return 4\"}}\n```";
            Assert.IsTrue(AiLuaPayloadParser.TryGetExecutableLua(s, out string lua));
            Assert.AreEqual("return 4", lua);
        }

        [Test]
        public void TryGetExecutableLua_StubApplyWaveModifier_NoLua()
        {
            const string s =
                "{\"commandType\":\"ApplyWaveModifier\",\"payload\":{\"agentRole\":\"Creator\",\"modifierId\":\"stub\",\"wave\":1}}";
            Assert.IsFalse(AiLuaPayloadParser.TryGetExecutableLua(s, out _));
        }
    }
}