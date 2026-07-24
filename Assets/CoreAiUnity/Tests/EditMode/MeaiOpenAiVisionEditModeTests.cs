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

    /// <summary>
    /// Covers the vision capability gate (<see cref="VisionCapability"/>) and the autonomous tool-result
    /// image lift (<see cref="CoreAI.Infrastructure.World.CameraLlmTool.TryExtractImageContentFromResult"/> /
    /// <see cref="CoreAI.Infrastructure.World.CameraLlmTool.TryParseImageDataUri"/>).
    /// </summary>
    public sealed class VisionCapabilityGateEditModeTests
    {
        [TestCase("gpt-4o", true)]
        [TestCase("gpt-4o-mini", true)]
        [TestCase("qwen2-vl-7b", true)]
        [TestCase("gemini-1.5-pro", true)]
        [TestCase("claude-sonnet-4", true)]
        [TestCase("llava-1.6", true)]
        [TestCase("gpt-3.5-turbo", false)]
        [TestCase("qwen3-4b", false)]
        [TestCase("", false)]
        [TestCase(null, false)]
        public void ModelLooksVisionCapable_MatchesKnownVisionModels(string model, bool expected)
        {
            Assert.AreEqual(expected, VisionCapability.ModelLooksVisionCapable(model));
        }

        [Test]
        public void IsEnabled_OnForcesVision_EvenForTextOnlyModel()
        {
            Assert.IsTrue(VisionCapability.IsEnabled(VisionSupportMode.On, "gpt-3.5-turbo"));
        }

        [Test]
        public void IsEnabled_OffDisablesVision_EvenForVisionModel()
        {
            Assert.IsFalse(VisionCapability.IsEnabled(VisionSupportMode.Off, "gpt-4o"));
        }

        [Test]
        public void IsEnabled_AutoDefaultsOn_ExceptUtilityModels()
        {
            // Auto is ON by default now, including chat models whose name isn't a known vision marker
            // (e.g. local qwen builds LM Studio reports as multimodal).
            Assert.IsTrue(VisionCapability.IsEnabled(VisionSupportMode.Auto, "gpt-4o"));
            Assert.IsTrue(VisionCapability.IsEnabled(VisionSupportMode.Auto, "gpt-3.5-turbo"));
            Assert.IsTrue(VisionCapability.IsEnabled(VisionSupportMode.Auto, "qwen3.5-4b-mtp"));
            Assert.IsTrue(VisionCapability.IsEnabled(VisionSupportMode.Auto, ""));

            // Only obvious non-chat utility models are treated as text-only under Auto.
            Assert.IsFalse(VisionCapability.IsEnabled(VisionSupportMode.Auto, "text-embedding-3-small"));
            Assert.IsFalse(VisionCapability.IsEnabled(VisionSupportMode.Auto, "nomic-embed-text-v1.5"));
            Assert.IsFalse(VisionCapability.IsEnabled(VisionSupportMode.Auto, "whisper-large-v3"));
        }

        [Test]
        public void TryParseImageDataUri_ValidPngDataUri_ParsesBytesAndMediaType()
        {
            string b64 = System.Convert.ToBase64String(new byte[] { 0x89, 0x50, 0x4E, 0x47 });
            bool ok = Infrastructure.World.CameraLlmTool.TryParseImageDataUri(
                "data:image/png;base64," + b64, out DataContent image);

            Assert.IsTrue(ok);
            Assert.IsNotNull(image);
            Assert.AreEqual("image/png", image.MediaType);
        }

        [TestCase("data:text/plain;base64,aGk=")] // not an image
        [TestCase("data:image/png;base64,!!notbase64!!")]
        [TestCase("data:image/png,nodelimiters")] // no ';'
        [TestCase("plainstring")]
        [TestCase("")]
        public void TryParseImageDataUri_InvalidInputs_ReturnFalse(string dataUri)
        {
            Assert.IsFalse(Infrastructure.World.CameraLlmTool.TryParseImageDataUri(dataUri, out DataContent image));
            Assert.IsNull(image);
        }

        [Test]
        public void TryExtractImageContentFromResult_SuccessWithDataUri_ReturnsImage()
        {
            string b64 = System.Convert.ToBase64String(new byte[] { 1, 2, 3 });
            string json = "{\"success\":true,\"dataUri\":\"data:image/jpeg;base64," + b64 + "\"}";

            bool ok = Infrastructure.World.CameraLlmTool.TryExtractImageContentFromResult(
                json, out DataContent image);

            Assert.IsTrue(ok);
            Assert.AreEqual("image/jpeg", image.MediaType);
        }

        [TestCase("{\"success\":false,\"dataUri\":\"data:image/jpeg;base64,AQID\"}")] // failed capture
        [TestCase("{\"success\":true}")] // missing dataUri
        [TestCase("not json at all")]
        [TestCase("")]
        public void TryExtractImageContentFromResult_NonImageOrFailed_ReturnsFalse(string json)
        {
            Assert.IsFalse(Infrastructure.World.CameraLlmTool.TryExtractImageContentFromResult(
                json, out DataContent image));
            Assert.IsNull(image);
        }
    }
}
#endif
