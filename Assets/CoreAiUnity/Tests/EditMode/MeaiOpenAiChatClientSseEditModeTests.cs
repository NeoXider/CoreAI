#if !COREAI_NO_LLM
using System.Linq;
using CoreAI.Infrastructure.Llm;
using MEAI = Microsoft.Extensions.AI;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode
{
    public sealed class MeaiOpenAiChatClientSseEditModeTests
    {
        [Test]
        public void ParseSseUpdates_MessageOnlyChunk_ParsesText()
        {
            const string sse =
                "data:{\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":\"chunk\"}}]}\n";
            var list = MeaiOpenAiChatClient.ParseSseUpdatesForTests(sse).ToList();
            Assert.AreEqual(1, list.Count);
            Assert.AreEqual("chunk", list[0].Text);
        }

        [Test]
        public void ParseSseUpdates_DataPrefixWithSpace_ParsesDelta()
        {
            const string sse = "data: {\"choices\":[{\"delta\":{\"content\":\"hi\"}}]}\n";
            var list = MeaiOpenAiChatClient.ParseSseUpdatesForTests(sse).ToList();
            Assert.AreEqual(1, list.Count);
            Assert.AreEqual("hi", list[0].Text);
        }

        [Test]
        public void ParseSseUpdates_DataPrefixWithoutSpace_ParsesDelta()
        {
            const string sse = "data:{\"choices\":[{\"delta\":{\"content\":\"local\"}}]}\n";
            var list = MeaiOpenAiChatClient.ParseSseUpdatesForTests(sse).ToList();
            Assert.AreEqual(1, list.Count);
            Assert.AreEqual("local", list[0].Text);
        }

        [Test]
        public void ParseSseDataLine_MessageOnly_InStreamChunk_EmitsText()
        {
            const string json =
                "{\"choices\":[{\"index\":0,\"message\":{\"role\":\"assistant\",\"content\":\"from message\"}}]}";
            MEAI.ChatResponseUpdate u = MeaiOpenAiChatClient.ParseSseDataLineForTests(json);
            Assert.IsNotNull(u);
            Assert.AreEqual("from message", u.Text);
        }

        [Test]
        public void ParseSseDataLine_LegacyChoicesText_EmitsText()
        {
            const string json = "{\"choices\":[{\"index\":0,\"text\":\"legacy stream\"}]}";
            MEAI.ChatResponseUpdate u = MeaiOpenAiChatClient.ParseSseDataLineForTests(json);
            Assert.IsNotNull(u);
            Assert.AreEqual("legacy stream", u.Text);
        }

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
