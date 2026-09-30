using System;
using CoreAI.Ai;
using CoreAI.Chat;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode
{
    /// <summary>
    /// Covers the JSON contract of <see cref="CoreAiChatExternalDriver.SubmitPromptWithAttachments"/>, the
    /// opt-in WebGL harness entry that sends images and text files through the chat panel.
    /// </summary>
    public sealed class CoreAiChatExternalDriverAttachmentsEditModeTests
    {
        private static readonly string PngDataUrl =
            "data:image/png;base64," + Convert.ToBase64String(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });

        [Test]
        public void TextImagesAndFiles_ParseInOrderWithGeneratedNames()
        {
            string json = "{\"text\":\"look\",\"images\":[\"" + PngDataUrl + "\",{\"name\":\"b.png\",\"dataUrl\":\"" +
                          PngDataUrl + "\"}],\"files\":[{\"name\":\"notes.txt\",\"text\":\"KIWI-42\"}]}";

            Assert.IsTrue(CoreAiChatExternalDriver.TryParseAttachmentRequest(
                json, out CoreAiChatExternalDriver.ExternalAttachmentRequest request, out string error), error);

            Assert.AreEqual("look", request.Text);
            Assert.AreEqual(2, request.ImageCount);
            Assert.AreEqual(1, request.FileCount);
            Assert.AreEqual(3, request.Attachments.Count);
            Assert.AreEqual("image-1.png", request.Attachments[0].FileName);
            Assert.AreEqual(AiAttachmentCategory.Image, request.Attachments[0].Category);
            Assert.AreEqual("b.png", request.Attachments[1].FileName);
            Assert.AreEqual(AiAttachmentCategory.Text, request.Attachments[2].Category);
            Assert.AreEqual("notes.txt", request.Attachments[2].FileName);
        }

        [Test]
        public void ImageOnly_WithBlankText_IsAccepted()
        {
            string json = "{\"text\":\"  \",\"images\":[\"" + PngDataUrl + "\"]}";

            Assert.IsTrue(CoreAiChatExternalDriver.TryParseAttachmentRequest(
                json, out CoreAiChatExternalDriver.ExternalAttachmentRequest request, out string error), error);

            Assert.AreEqual(1, request.ImageCount);
            Assert.AreEqual(0, request.FileCount);
        }

        [TestCase("")]
        [TestCase("{\"text\":\"\"}")]
        [TestCase("not json")]
        [TestCase("{\"images\":[\"data:text/plain;base64,QQ==\"]}")]
        [TestCase("{\"images\":[\"https://example.com/a.png\"]}")]
        [TestCase("{\"images\":[42]}")]
        [TestCase("{\"files\":[{\"name\":\"a.txt\"}]}")]
        public void MalformedOrEmptyRequests_AreRejectedWithAReason(string json)
        {
            Assert.IsFalse(CoreAiChatExternalDriver.TryParseAttachmentRequest(
                json, out CoreAiChatExternalDriver.ExternalAttachmentRequest request, out string error));
            Assert.IsNull(request);
            Assert.IsFalse(string.IsNullOrWhiteSpace(error));
        }
    }
}
