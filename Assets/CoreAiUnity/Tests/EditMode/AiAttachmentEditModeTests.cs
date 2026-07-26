#if !COREAI_NO_LLM
using System;
using System.Collections.Generic;
using System.Text;
using CoreAI.Ai;
using CoreAI.Infrastructure.Llm;
using Microsoft.Extensions.AI;
using NUnit.Framework;
using ChatMessage = Microsoft.Extensions.AI.ChatMessage;

namespace CoreAI.Tests.EditMode
{
    /// <summary>
    /// Verifies the universal attachment API (<see cref="AiAttachment"/> / <see cref="AiUserMessageBuilder"/>):
    /// image routing to multimodal parts, text-like files inlined into the prompt, unsupported binary rejected
    /// loudly, media-type inference, size caps, and the byte-identical no-attachment regression. Image parts
    /// are asserted end-to-end through <see cref="MeaiOpenAiChatClient.BuildOpenAiMessageContent"/>, the same
    /// wire builder used by both the streaming and non-streaming provider paths.
    /// </summary>
    public sealed class AiAttachmentEditModeTests
    {
        private static readonly byte[] Png = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };

        private static object WireContentFor(ChatMessage message)
        {
            return MeaiOpenAiChatClient.BuildOpenAiMessageContent(message.Text, message.Contents);
        }

        // (a) One PNG attachment produces a multimodal parts array with the correct data: URL.
        [Test]
        public void SinglePngAttachment_ProducesMultimodalImagePart()
        {
            ChatMessage msg = AiUserMessageBuilder.BuildUserMessage(
                "describe this", new List<AiAttachment> { AiAttachment.Image(Png, "image/png", "hero.png") });

            object content = WireContentFor(msg);

            List<object> parts = content as List<object>;
            Assert.IsNotNull(parts, "Image attachment must produce a multimodal parts array, not a string.");
            Assert.AreEqual(2, parts.Count);

            Dictionary<string, object> textPart = (Dictionary<string, object>)parts[0];
            Assert.AreEqual("text", textPart["type"]);
            Assert.AreEqual("describe this", textPart["text"]);

            Dictionary<string, object> imagePart = (Dictionary<string, object>)parts[1];
            Assert.AreEqual("image_url", imagePart["type"]);
            Dictionary<string, object> imageUrl = (Dictionary<string, object>)imagePart["image_url"];
            StringAssert.StartsWith("data:image/png;base64,", (string)imageUrl["url"]);
            StringAssert.Contains(Convert.ToBase64String(Png), (string)imageUrl["url"]);
        }

        // (a') Media type inferred from the file extension when MediaType is not provided.
        [Test]
        public void ImageMediaType_InferredFromExtension()
        {
            ChatMessage msg = AiUserMessageBuilder.BuildUserMessage(
                "", new List<AiAttachment> { AiAttachment.FromFile("shot.jpeg", Png) });

            List<object> parts = (List<object>)WireContentFor(msg);
            Assert.AreEqual(1, parts.Count, "Empty prompt must not emit a text part.");
            Dictionary<string, object> imagePart = (Dictionary<string, object>)parts[0];
            Dictionary<string, object> imageUrl = (Dictionary<string, object>)imagePart["image_url"];
            StringAssert.StartsWith("data:image/jpeg;base64,", (string)imageUrl["url"]);
        }

        // (b) Unsupported media type throws with the supported list.
        [Test]
        public void UnsupportedBinary_ThrowsWithSupportedCategories()
        {
            AiAttachment audio = AiAttachment.FromFile("clip.wav", new byte[] { 1, 2, 3 }, "audio/wav");

            ArgumentException ex = Assert.Throws<ArgumentException>(() =>
                AiUserMessageBuilder.BuildUserMessage("hi", new List<AiAttachment> { audio }));

            StringAssert.Contains("audio/wav", ex.Message);
            StringAssert.Contains("image/png", ex.Message);
            StringAssert.Contains("text/*", ex.Message);
        }

        // (b') Unknown extension with no explicit media type is a loud error, never a silent drop.
        [Test]
        public void UnknownExtension_NoMediaType_ThrowsLoudly()
        {
            AiAttachment unknown = AiAttachment.FromFile("mystery.fbx", new byte[] { 1, 2, 3 });

            ArgumentException ex = Assert.Throws<ArgumentException>(() =>
                AiUserMessageBuilder.BuildUserMessage("hi", new List<AiAttachment> { unknown }));

            StringAssert.Contains("mystery.fbx", ex.Message);
        }

        // (c) Null/empty attachments keep the plain-string content path byte-identical (regression).
        [Test]
        public void NoAttachments_KeepsPlainStringContent()
        {
            ChatMessage nullCase = AiUserMessageBuilder.BuildUserMessage("just text", null);
            ChatMessage emptyCase = AiUserMessageBuilder.BuildUserMessage("just text", new List<AiAttachment>());

            Assert.AreEqual("just text", WireContentFor(nullCase));
            Assert.AreEqual("just text", WireContentFor(emptyCase));
            // Same wire content as constructing a plain MEAI text message directly.
            Assert.AreEqual(
                MeaiOpenAiChatClient.BuildOpenAiMessageContent("just text",
                    new ChatMessage(ChatRole.User, "just text").Contents),
                WireContentFor(nullCase));
        }

        // (d) A Lua file is inlined with delimiters and its filename; content stays a plain string (any model).
        [Test]
        public void LuaFile_InlinedWithDelimitersAndFilename()
        {
            byte[] lua = Encoding.UTF8.GetBytes("function main() return 42 end");
            ChatMessage msg = AiUserMessageBuilder.BuildUserMessage(
                "fix this", new List<AiAttachment> { AiAttachment.FromFile("level.lua", lua) });

            object content = WireContentFor(msg);
            Assert.IsInstanceOf<string>(content,
                "Text-only attachment must stay a plain string, not multimodal parts.");
            string text = (string)content;
            StringAssert.Contains("fix this", text);
            StringAssert.Contains("--- attached file: level.lua (application/x-lua) ---", text);
            StringAssert.Contains("function main() return 42 end", text);
            StringAssert.Contains("--- end of level.lua ---", text);
        }

        // (d') Markdown file inlined, and a UTF-8 BOM is stripped from the decoded content.
        [Test]
        public void MarkdownFile_Inlined_BomStripped()
        {
            byte[] withBom = new byte[] { 0xEF, 0xBB, 0xBF };
            byte[] body = Encoding.UTF8.GetBytes("# Title");
            byte[] md = new byte[withBom.Length + body.Length];
            Buffer.BlockCopy(withBom, 0, md, 0, withBom.Length);
            Buffer.BlockCopy(body, 0, md, withBom.Length, body.Length);

            ChatMessage msg = AiUserMessageBuilder.BuildUserMessage(
                "", new List<AiAttachment> { AiAttachment.FromFile("notes.md", md) });

            string text = (string)WireContentFor(msg);
            StringAssert.Contains("(text/markdown)", text);
            StringAssert.Contains("# Title", text);
            Assert.IsFalse(text.Contains("﻿"), "UTF-8 BOM must be stripped from inlined text.");
        }

        // (e) Mixed prompt + PNG + Lua: multimodal parts where the text part contains the inlined file.
        [Test]
        public void MixedPromptImageAndText_TextPartCarriesInlinedFile()
        {
            byte[] lua = Encoding.UTF8.GetBytes("print('hi')");
            ChatMessage msg = AiUserMessageBuilder.BuildUserMessage(
                "use these",
                new List<AiAttachment>
                {
                    AiAttachment.Image(Png, "image/png", "sprite.png"),
                    AiAttachment.FromFile("mod.lua", lua)
                });

            List<object> parts = (List<object>)WireContentFor(msg);
            Assert.AreEqual(2, parts.Count, "One text part (prompt + inlined lua) and one image part.");

            Dictionary<string, object> textPart = (Dictionary<string, object>)parts[0];
            Assert.AreEqual("text", textPart["type"]);
            string text = (string)textPart["text"];
            StringAssert.Contains("use these", text);
            StringAssert.Contains("--- attached file: mod.lua (application/x-lua) ---", text);
            StringAssert.Contains("print('hi')", text);

            Dictionary<string, object> imagePart = (Dictionary<string, object>)parts[1];
            Assert.AreEqual("image_url", imagePart["type"]);
        }

        // (f) Extension inference and classification for representative types.
        [TestCase("a.png", "image/png", AiAttachmentCategory.Image)]
        [TestCase("a.jpg", "image/jpeg", AiAttachmentCategory.Image)]
        [TestCase("a.webp", "image/webp", AiAttachmentCategory.Image)]
        [TestCase("a.gif", "image/gif", AiAttachmentCategory.Image)]
        [TestCase("a.lua", "application/x-lua", AiAttachmentCategory.Text)]
        [TestCase("a.json", "application/json", AiAttachmentCategory.Text)]
        [TestCase("a.md", "text/markdown", AiAttachmentCategory.Text)]
        [TestCase("a.txt", "text/plain", AiAttachmentCategory.Text)]
        [TestCase("a.cs", "text/x-csharp", AiAttachmentCategory.Text)]
        [TestCase("a.wav", "", AiAttachmentCategory.Unsupported)]
        public void ExtensionInference_ResolvesMediaTypeAndCategory(
            string fileName, string expectedMediaType, AiAttachmentCategory expectedCategory)
        {
            AiAttachment attachment = AiAttachment.FromFile(fileName, new byte[] { 1 });
            Assert.AreEqual(expectedMediaType, attachment.ResolvedMediaType);
            Assert.AreEqual(expectedCategory, attachment.Category);
        }

        // (g) Per-file size cap throws loudly.
        [Test]
        public void OversizedTextAttachment_ThrowsWithSizeLimit()
        {
            byte[] big = new byte[AiAttachment.MaxInlineTextBytes + 1];
            AiAttachment attachment = AiAttachment.FromFile("huge.txt", big);

            ArgumentException ex = Assert.Throws<ArgumentException>(() =>
                AiUserMessageBuilder.BuildUserMessage("hi", new List<AiAttachment> { attachment }));

            StringAssert.Contains("per-file inline limit", ex.Message);
        }

        // (g') Total inline size cap across multiple text files throws loudly.
        [Test]
        public void TotalInlineSizeCap_ThrowsWhenExceeded()
        {
            int half = AiAttachment.MaxInlineTextBytes; // 256 KB each; 5 files = 1.25 MB > 1 MB total.
            List<AiAttachment> many = new();
            for (int i = 0; i < 5; i++)
            {
                many.Add(AiAttachment.FromFile($"part{i}.txt", new byte[half]));
            }

            ArgumentException ex = Assert.Throws<ArgumentException>(() =>
                AiUserMessageBuilder.BuildUserMessage("hi", many));

            StringAssert.Contains("total limit", ex.Message);
        }

        // (h) History placeholder is compact and byte-free (never raw bytes).
        [Test]
        public void DescribeForHistory_IsCompactPlaceholder()
        {
            string desc = AiAttachment.Image(new byte[12 * 1024], "image/png", "hero.png").DescribeForHistory();
            Assert.AreEqual("[attachment: hero.png image/png 12 KB]", desc);

            string luaDesc = AiAttachment.FromFile("mod.lua", Encoding.UTF8.GetBytes("x")).DescribeForHistory();
            StringAssert.StartsWith("[attachment: mod.lua application/x-lua", luaDesc);
        }
    }
}
#endif
