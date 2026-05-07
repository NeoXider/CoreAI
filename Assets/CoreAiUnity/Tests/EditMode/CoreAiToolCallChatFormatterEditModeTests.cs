using System.Collections.Generic;
using CoreAI.Chat;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode
{
    [TestFixture]
    public sealed class CoreAiToolCallChatFormatterEditModeTests
    {
        [Test]
        public void BuildDisplayText_EmptyToolName_UsesPlaceholder()
        {
            string s = CoreAiToolCallChatFormatter.BuildDisplayText(" ", null, null);
            StringAssert.StartsWith("[Tool] (tool)", s);
        }

        [Test]
        public void BuildDisplayText_IncludesArgsAndResult()
        {
            var args = new Dictionary<string, object?> { ["action"] = "write", ["content"] = "hello" };
            string s = CoreAiToolCallChatFormatter.BuildDisplayText("memory", args, "{\"ok\":true}", 200);
            StringAssert.Contains("memory", s);
            StringAssert.Contains("args:", s);
            StringAssert.Contains("action", s);
            StringAssert.Contains("result:", s);
            StringAssert.Contains("ok", s);
        }

        [Test]
        public void BuildDisplayText_TruncatesLongResult()
        {
            string longText = new string('x', 500);
            string s = CoreAiToolCallChatFormatter.BuildDisplayText("t", null, longText, maxCharsPerSection: 80);
            StringAssert.Contains("…", s);
            Assert.LessOrEqual(s.Length, 200);
        }
    }
}
