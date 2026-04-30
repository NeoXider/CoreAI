#if !COREAI_NO_LLM
using CoreAI.Infrastructure.Llm;
using MEAI = Microsoft.Extensions.AI;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode
{
    public sealed class MeaiOpenAiChatClientSseEditModeTests
    {
        [Test]
        public void ParseSseDataLine_ReasoningOnly_DoesNotEmitAssistantText()
        {
            const string json = "{\"choices\":[{\"delta\":{\"reasoning_content\":\"think\"}}]}";
            MEAI.ChatResponseUpdate u = MeaiOpenAiChatClient.ParseSseDataLineForTests(json);
            Assert.IsNull(u);
        }

        [Test]
        public void ParseSseDataLine_ContentOnly_EmitsText()
        {
            const string json = "{\"choices\":[{\"delta\":{\"content\":\"hi\"}}]}";
            MEAI.ChatResponseUpdate u = MeaiOpenAiChatClient.ParseSseDataLineForTests(json);
            Assert.IsNotNull(u);
            Assert.AreEqual("hi", u.Text);
        }

        [Test]
        public void ParseSseDataLine_ReasoningAndContent_EmitsOnlyContent()
        {
            const string json = "{\"choices\":[{\"delta\":{\"reasoning_content\":\"x\",\"content\":\"out\"}}]}";
            MEAI.ChatResponseUpdate u = MeaiOpenAiChatClient.ParseSseDataLineForTests(json);
            Assert.IsNotNull(u);
            Assert.AreEqual("out", u.Text);
        }

        [Test]
        public void ParseCompletion_EmptyContent_UsesReasoningContent()
        {
            const string json =
                "{\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":\"\",\"reasoning_content\":\"Hello from reasoning\"}}]}";
            MEAI.ChatResponse r = MeaiOpenAiChatClient.ParseResponse(json);
            Assert.AreEqual("Hello from reasoning", r.Text);
        }

        [Test]
        public void ParseCompletion_ContentAsTextPartsArray_JoinsText()
        {
            const string json =
                "{\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":[{\"type\":\"text\",\"text\":\"a\"},{\"type\":\"text\",\"text\":\"b\"}]}}]}";
            MEAI.ChatResponse r = MeaiOpenAiChatClient.ParseResponse(json);
            Assert.AreEqual("a\nb", r.Text);
        }

        [Test]
        public void ParseCompletion_EmptyContent_UsesReasoningContent_CamelCase()
        {
            const string json =
                "{\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":\"\",\"reasoningContent\":\"Hello camel\"}}]}";
            MEAI.ChatResponse r = MeaiOpenAiChatClient.ParseResponse(json);
            Assert.AreEqual("Hello camel", r.Text);
        }
    }
}
#endif
