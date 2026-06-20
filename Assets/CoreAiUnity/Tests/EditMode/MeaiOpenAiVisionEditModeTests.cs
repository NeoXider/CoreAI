#if !COREAI_NO_LLM
using System.Collections.Generic;
using CoreAI.Infrastructure.Llm;
using Microsoft.Extensions.AI;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode
{
    /// <summary>
    /// Verifies that the OpenAI message-payload builder serializes image content as multimodal
    /// <c>image_url</c> parts (so vision-capable models receive the image) while keeping text-only
    /// messages as plain strings.
    /// </summary>
    public sealed class MeaiOpenAiVisionEditModeTests
    {
        [Test]
        public void BuildOpenAiMessageContent_TextOnly_ReturnsPlainString()
        {
            object content = MeaiOpenAiChatClient.BuildOpenAiMessageContent("hello", null);
            Assert.AreEqual("hello", content);
        }

        [Test]
        public void BuildOpenAiMessageContent_NonImageContent_StaysPlainString()
        {
            List<AIContent> contents = new() { new DataContent(new byte[] { 1, 2, 3 }, "text/plain") };
            object content = MeaiOpenAiChatClient.BuildOpenAiMessageContent("hi", contents);
            Assert.AreEqual("hi", content);
        }

        [Test]
        public void BuildOpenAiMessageContent_TextPlusImage_ProducesMultimodalParts()
        {
            byte[] jpg = { 0xFF, 0xD8, 0xFF, 0xD9 };
            List<AIContent> contents = new() { new DataContent(jpg, "image/jpeg") };

            object content = MeaiOpenAiChatClient.BuildOpenAiMessageContent("look at this", contents);

            List<object> parts = content as List<object>;
            Assert.IsNotNull(parts, "Image content must produce a multimodal parts array, not a string.");
            Assert.AreEqual(2, parts.Count);

            Dictionary<string, object> textPart = (Dictionary<string, object>)parts[0];
            Assert.AreEqual("text", textPart["type"]);
            Assert.AreEqual("look at this", textPart["text"]);

            Dictionary<string, object> imagePart = (Dictionary<string, object>)parts[1];
            Assert.AreEqual("image_url", imagePart["type"]);
            Dictionary<string, object> imageUrl = (Dictionary<string, object>)imagePart["image_url"];
            StringAssert.StartsWith("data:image/jpeg;base64,", (string)imageUrl["url"]);
        }

        [Test]
        public void BuildOpenAiMessageContent_ImageOnly_OmitsEmptyTextPart()
        {
            List<AIContent> contents = new() { new DataContent(new byte[] { 0x89, 0x50 }, "image/png") };

            object content = MeaiOpenAiChatClient.BuildOpenAiMessageContent("", contents);

            List<object> parts = content as List<object>;
            Assert.IsNotNull(parts);
            Assert.AreEqual(1, parts.Count, "Empty text must not add a text part.");
            Dictionary<string, object> imagePart = (Dictionary<string, object>)parts[0];
            Assert.AreEqual("image_url", imagePart["type"]);
            Dictionary<string, object> imageUrl = (Dictionary<string, object>)imagePart["image_url"];
            StringAssert.StartsWith("data:image/png;base64,", (string)imageUrl["url"]);
        }
    }
}
#endif
