using CoreAI.Ai;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode
{
    public sealed class LlmResponseSanitizerEditModeTests
    {
        [Test]
        public void TryPrepareJsonObject_StripsJsonFence()
        {
            const string raw = "```json\n{\"a\":1}\n```";
            Assert.IsTrue(LlmResponseSanitizer.TryPrepareJsonObject(raw, out var j));
            Assert.AreEqual("{\"a\":1}", j);
        }

        [Test]
        public void TryPrepareJsonObject_BalancedInsideStringWithBraces()
        {
            const string raw = "{\"x\":\"}\"}";
            Assert.IsTrue(LlmResponseSanitizer.TryPrepareJsonObject(raw, out var j));
            Assert.AreEqual(raw, j);
        }

        [Test]
        public void TryPrepareJsonObject_PreambleBeforeObject()
        {
            const string raw = "Here you go:\n{\"ok\":true}";
            Assert.IsTrue(LlmResponseSanitizer.TryPrepareJsonObject(raw, out var j));
            Assert.AreEqual("{\"ok\":true}", j);
        }

        [Test]
        public void TryPrepareJsonObject_DoubleFence()
        {
            const string raw = "```\n```json\n{\"n\":2}\n```\n```";
            Assert.IsTrue(LlmResponseSanitizer.TryPrepareJsonObject(raw, out var j));
            Assert.AreEqual("{\"n\":2}", j);
        }

        [Test]
        public void TryPrepareJsonObject_NoJson_ReturnsFalse()
        {
            Assert.IsFalse(LlmResponseSanitizer.TryPrepareJsonObject("no braces here", out _));
            Assert.IsFalse(LlmResponseSanitizer.TryPrepareJsonObject(null, out _));
        }

        [Test]
        public void TryPrepareJsonObject_FirstObjectWhenTwoSequential()
        {
            const string raw = "{\"a\":1}{\"b\":2}";
            Assert.IsTrue(LlmResponseSanitizer.TryPrepareJsonObject(raw, out var j));
            Assert.AreEqual("{\"a\":1}", j);
        }

        [Test]
        public void StripMarkdownCodeFence_NoFence_Unchanged()
        {
            const string raw = "{\"x\":1}";
            Assert.AreEqual(raw, LlmResponseSanitizer.StripMarkdownCodeFences(raw));
        }
    }
}

