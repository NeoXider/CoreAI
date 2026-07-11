using CoreAI.Infrastructure.Llm;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode
{
    public sealed class LlmResponseSanitizerTests
    {
        [Test]
        public void StripLeadingSystemPromptEcho_NoSystem_ReturnsOriginal()
        {
            string c = "Привет!";
            Assert.AreEqual(c, LlmResponseSanitizer.StripLeadingSystemPromptEcho(c, null));
            Assert.AreEqual(c, LlmResponseSanitizer.StripLeadingSystemPromptEcho(c, ""));
        }

        [Test]
        public void StripLeadingSystemPromptEcho_ShortPrefixMatch_DoesNotStrip()
        {
            string system = "Hi";
            string content = "Hello there";
            Assert.AreEqual(content, LlmResponseSanitizer.StripLeadingSystemPromptEcho(content, system));
        }

        [Test]
        public void StripLeadingSystemPromptEcho_LongEcho_StripsOnce()
        {
            string system = new string('a', 250) + " SYSTEM";
            string content = system + " visible reply";
            string got = LlmResponseSanitizer.StripLeadingSystemPromptEcho(content, system);
            Assert.AreEqual("visible reply", got);
        }

        [Test]
        public void StripLeadingSystemPromptEcho_DoubleEcho_StripsTwice()
        {
            string system = new string('b', 220) + "X";
            string content = system + system + "tail";
            string got = LlmResponseSanitizer.StripLeadingSystemPromptEcho(content, system);
            Assert.AreEqual("tail", got);
        }

        [Test]
        public void StripLeadingSystemPromptEcho_TrimsLeadingWhitespaceBeforeCompare()
        {
            string system = new string('c', 200) + "Y";
            string content = "  \n" + system + " ok";
            string got = LlmResponseSanitizer.StripLeadingSystemPromptEcho(content, system);
            Assert.AreEqual("ok", got);
        }
    }
}
