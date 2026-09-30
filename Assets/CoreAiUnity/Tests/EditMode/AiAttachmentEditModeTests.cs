#if COREAI_LLM
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
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

        // ===== Convenience factories (engine-free) =====

        [Test]
        public void EmptyPrompt_ImageOnly_CarriesOnlyTheImagePart()
        {
            ChatMessage msg = AiUserMessageBuilder.BuildUserMessage("", new[] { AiAttachment.Image(Png, "image/png") });

            Assert.AreEqual(1, msg.Contents.Count, "An image-only turn must not carry an empty text part.");
            Assert.IsInstanceOf<DataContent>(msg.Contents[0]);
        }

        [Test]
        public void ImageFromMemorySlice_IsWrappedWithoutACopy()
        {
            byte[] buffer = new byte[64];
            Png.CopyTo(buffer, 16);
            AiAttachment image = AiAttachment.Image(new ReadOnlyMemory<byte>(buffer, 16, Png.Length), "image/png");

            ChatMessage msg = AiUserMessageBuilder.BuildUserMessage("look", new[] { image });

            DataContent data = (DataContent)msg.Contents[1];
            Assert.IsTrue(System.Runtime.InteropServices.MemoryMarshal.TryGetArray(data.Data,
                out ArraySegment<byte> segment));
            Assert.AreSame(buffer, segment.Array, "The provider part must reference the caller's buffer.");
            Assert.AreEqual(16, segment.Offset);
            Assert.AreEqual(Png.Length, segment.Count);
        }

        [Test]
        public void ImageFromArray_KeepsTheCallersArray()
        {
            AiAttachment image = AiAttachment.Image(Png, "image/png");

            Assert.AreSame(Png, image.Data);
            DataContent data = (DataContent)AiUserMessageBuilder.BuildUserMessage("x", new[] { image }).Contents[1];
            Assert.IsTrue(System.Runtime.InteropServices.MemoryMarshal.TryGetArray(data.Data,
                out ArraySegment<byte> segment));
            Assert.AreSame(Png, segment.Array);
        }

        [Test]
        public void FromText_InlinesTheStringAsIs_AndMatchesTheByteForm()
        {
            const string lua = "﻿local hp = 10\nreturn hp";
            ChatMessage fromText = AiUserMessageBuilder.BuildUserMessage("review",
                new[] { AiAttachment.FromText("enemy.lua", lua) });
            ChatMessage fromBytes = AiUserMessageBuilder.BuildUserMessage("review",
                new[] { AiAttachment.FromFile("enemy.lua", Encoding.UTF8.GetBytes(lua)) });

            Assert.AreEqual(fromBytes.Text, fromText.Text, "A string and its UTF-8 bytes must compose identically.");
            StringAssert.Contains("--- attached file: enemy.lua (application/x-lua) ---\nlocal hp = 10", fromText.Text);
            Assert.AreEqual(-1, fromText.Text.IndexOf('﻿'), "The BOM must be stripped (ordinal check).");
        }

        [Test]
        public void FromText_DefaultsToPlainText_AndRejectsANonTextMediaType()
        {
            Assert.AreEqual("text/plain", AiAttachment.FromText("notes", "hi").ResolvedMediaType);
            Assert.AreEqual("text/plain", AiAttachment.FromText("hero.png", "hi").ResolvedMediaType,
                "A string is never routed as an image, whatever its name says.");
            Assert.Throws<ArgumentException>(() => AiAttachment.FromText("a", "hi", "image/png"));
        }

        [TestCase(0)]
        [TestCase(1)]
        [TestCase(2)]
        [TestCase(3)]
        [TestCase(1000)]
        public void FromBase64_AndFromDataUrl_DecodeExactly(int length)
        {
            byte[] bytes = new byte[length + 1];
            new Random(length).NextBytes(bytes);
            string base64 = Convert.ToBase64String(bytes);

            AiAttachment fromBase64 = AiAttachment.FromBase64(base64, "image/png");
            AiAttachment fromDataUrl = AiAttachment.FromDataUrl("data:image/PNG;base64," + base64, "a.png");

            CollectionAssert.AreEqual(bytes, fromBase64.Memory.ToArray());
            CollectionAssert.AreEqual(bytes, fromDataUrl.Data);
            Assert.AreEqual(bytes.Length, fromDataUrl.Data.Length, "Whitespace-free base64 decodes into an exact array.");
            Assert.AreEqual("image/png", fromDataUrl.ResolvedMediaType);
            Assert.AreEqual("a.png", fromDataUrl.FileName);
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase("image/png;base64,AAAA")]
        [TestCase("data:image/png,AAAA")]
        [TestCase("data:image/png;base64,@@@@")]
        [TestCase("data:image/png;base64,")]
        public void TryFromDataUrl_RejectsMalformedInput(string dataUrl)
        {
            Assert.IsFalse(AiAttachment.TryFromDataUrl(dataUrl, out AiAttachment attachment));
            Assert.IsNull(attachment);
            Assert.Throws<FormatException>(() => AiAttachment.FromDataUrl(dataUrl));
        }

        [Test]
        public void FromDataUrl_TextTypeIsInlined()
        {
            AiAttachment json = AiAttachment.FromDataUrl(
                "data:application/json;base64," + Convert.ToBase64String(Encoding.UTF8.GetBytes("{\"a\":1}")),
                "cfg.json");

            Assert.AreEqual(AiAttachmentCategory.Text, json.Category);
            StringAssert.Contains("{\"a\":1}", AiUserMessageBuilder.BuildUserMessage("", new[] { json }).Text);
        }

        [Test]
        public void FromStream_MemoryStreamIsReferenced_OtherStreamsAreReadExactly()
        {
            MemoryStream memory = new();
            memory.Write(new byte[] { 1, 2, 3 }, 0, 3);
            memory.Write(Png, 0, Png.Length);
            memory.Position = 3;

            AiAttachment fromMemory = AiAttachment.FromStream(memory, "image/png");
            Assert.IsTrue(System.Runtime.InteropServices.MemoryMarshal.TryGetArray(fromMemory.Memory,
                out ArraySegment<byte> segment));
            Assert.AreSame(memory.GetBuffer(), segment.Array, "A MemoryStream buffer is not copied.");
            CollectionAssert.AreEqual(Png, fromMemory.Memory.ToArray());

            using BufferedStream buffered = new(new MemoryStream(Png));
            AiAttachment fromBuffered = AiAttachment.FromStream(buffered, "image/png");
            CollectionAssert.AreEqual(Png, fromBuffered.Data);
        }

        [Test]
        public async Task FromFile_ReadsThePathAndInfersTheType()
        {
            string path = Path.Combine(Path.GetTempPath(), "coreai_attachment_" + Guid.NewGuid().ToString("N") + ".png");
            File.WriteAllBytes(path, Png);
            try
            {
                AiAttachment sync = AiAttachment.FromFile(path);
                AiAttachment async = await AiAttachment.FromFileAsync(path);

                Assert.AreEqual(Path.GetFileName(path), sync.FileName);
                Assert.AreEqual(AiAttachmentCategory.Image, sync.Category);
                CollectionAssert.AreEqual(Png, sync.Data);
                CollectionAssert.AreEqual(Png, async.Data);
            }
            finally
            {
                File.Delete(path);
            }
        }

        [TestCase(0)]
        [TestCase(1)]
        [TestCase(2)]
        [TestCase(3)]
        [TestCase(4097)]
        public void WireDataUrl_EqualsTheClassicConcatenation(int length)
        {
            byte[] bytes = new byte[length];
            new Random(length + 7).NextBytes(bytes);

            Assert.AreEqual("data:image/webp;base64," + Convert.ToBase64String(bytes),
                MeaiOpenAiChatClient.BuildBase64DataUrl("image/webp", bytes));
        }

        // ===== Core orchestration one-liners =====

        [Test]
        public async Task OrchestratorExtensions_BuildOneRequestPerShape()
        {
            CapturingOrchestrator orchestrator = new();
            AiAttachment image = AiAttachment.Image(Png, "image/png");
            AiAttachment[] several = { image, AiAttachment.FromText("a.lua", "return 1") };

            await orchestrator.RunTaskAsync("plain");
            await orchestrator.RunTaskAsync("one", image, "Creator");
            await orchestrator.RunTaskAsync("many", several);
            await foreach (LlmStreamChunk _ in orchestrator.RunStreamingAsync("stream", image))
            {
            }

            Assert.AreEqual(4, orchestrator.Requests.Count);
            Assert.AreEqual("plain", orchestrator.Requests[0].Hint);
            Assert.IsNull(orchestrator.Requests[0].Attachments);
            Assert.AreEqual(BuiltInAgentRoleIds.SmartChat, orchestrator.Requests[0].RoleId);
            Assert.AreEqual("Creator", orchestrator.Requests[1].RoleId);
            Assert.AreSame(image, orchestrator.Requests[1].Attachments.Single());
            Assert.AreSame(several, orchestrator.Requests[2].Attachments, "A caller's list is passed through as-is.");
            Assert.AreSame(image, orchestrator.Requests[3].Attachments.Single());
        }

        // ===== Allocation contract =====

        [Test]
        public void Allocation_PromptOnly_IsExactlyTheLegacyMessage()
        {
            RequireAllocationCounter();
            const string prompt = "Describe the scene.";
            long legacy = AllocatedPerCall(() => new ChatMessage(ChatRole.User, prompt));
            long built = AllocatedPerCall(() => AiUserMessageBuilder.BuildUserMessage(prompt, null));
            long builtEmpty = AllocatedPerCall(() =>
                AiUserMessageBuilder.BuildUserMessage(prompt, Array.Empty<AiAttachment>()));

            Assert.AreEqual(legacy, built);
            Assert.AreEqual(legacy, builtEmpty);
        }

        [Test]
        public void Allocation_SeveralImages_NeverCopyTheImageBytes()
        {
            RequireAllocationCounter();
            byte[] image = new byte[1024 * 1024];
            AiAttachment[] images =
            {
                AiAttachment.Image(image, "image/jpeg"), AiAttachment.Image(image, "image/png"),
                AiAttachment.Image(image, "image/webp"), AiAttachment.Image(image, "image/gif")
            };

            long perCall = AllocatedPerCall(() => AiUserMessageBuilder.BuildUserMessage("compare", images));

            TestContext.WriteLine($"prompt + 4 x 1 MB images: {perCall} B per call");
            Assert.Less(perCall, 4096, "Four 1 MB images must be wrapped, not copied.");
        }

        [Test]
        public void Allocation_TextFiles_AreWrittenOnceIntoTheFinalString()
        {
            RequireAllocationCounter();
            AiAttachment[] files =
            {
                AiAttachment.FromFile("a.lua", Encoding.UTF8.GetBytes(new string('x', 32 * 1024))),
                AiAttachment.FromText("b.md", new string('y', 32 * 1024))
            };
            int finalLength = AiUserMessageBuilder.BuildUserMessage("review", files).Text.Length;

            long perCall = AllocatedPerCall(() => AiUserMessageBuilder.BuildUserMessage("review", files));

            TestContext.WriteLine($"prompt + 2 x 32 KB text: {perCall} B per call, final text {finalLength} chars");
            Assert.Less(perCall, finalLength * 2L + 2048, "One string of the final length, nothing more.");
        }

        [Test]
        public void Allocation_WireImagePart_IsOneDataUrlString()
        {
            RequireAllocationCounter();
            ChatMessage msg = AiUserMessageBuilder.BuildUserMessage("look",
                new[] { AiAttachment.Image(new byte[256 * 1024], "image/jpeg") });
            int dataUrlChars = "data:image/jpeg;base64,".Length + 256 * 1024 / 3 * 4 + 4;

            long perCall = AllocatedPerCall(() => MeaiOpenAiChatClient.BuildOpenAiMessageContent(msg.Text, msg.Contents));

            TestContext.WriteLine($"wire content for a 256 KB image: {perCall} B per call");
            Assert.Less(perCall, dataUrlChars * 2L + 4096, "No byte copy and no second base64 copy per request.");
        }

        // ===== Text decoding (BOM, charset, fail loud) =====

        [TestCase("utf-16le")]
        [TestCase("utf-16be")]
        [TestCase("utf-32le")]
        [TestCase("utf-32be")]
        [TestCase("utf-8")]
        public void TextWithByteOrderMark_IsDecodedByTheMark(string encodingName)
        {
            Encoding encoding = EncodingFor(encodingName);
            const string lua = "local name = \"Château\" -- héros";
            byte[] preamble = encoding.GetPreamble();
            byte[] body = encoding.GetBytes(lua);
            byte[] file = new byte[preamble.Length + body.Length];
            Buffer.BlockCopy(preamble, 0, file, 0, preamble.Length);
            Buffer.BlockCopy(body, 0, file, preamble.Length, body.Length);

            string text = AiUserMessageBuilder.BuildUserMessage("", new[] { AiAttachment.FromFile("hero.lua", file) })
                .Text;

            StringAssert.Contains("\n" + lua + "\n", text);
            Assert.AreEqual(-1, text.IndexOf('﻿'), "The byte-order mark must not reach the prompt.");
            Assert.AreEqual(-1, text.IndexOf('\0'));
        }

        [TestCase("text/plain; charset=utf-16", "utf-16le")]
        [TestCase("text/plain;charset=\"UTF-16BE\"", "utf-16be")]
        [TestCase("text/plain; charset=utf-16", "utf-16be")]
        [TestCase("application/json; charset=utf-32", "utf-32le")]
        [TestCase("text/plain; charset=iso-8859-1", "iso-8859-1")]
        public void TextWithoutByteOrderMark_IsDecodedByTheDeclaredCharset(string mediaType, string encodingName)
        {
            const string json = "{\"name\":\"Château\",\"hp\":10}";
            byte[] file = EncodingFor(encodingName).GetBytes(json);

            AiAttachment attachment = AiAttachment.FromFile("config", file, mediaType);
            string text = AiUserMessageBuilder.BuildUserMessage("", new[] { attachment }).Text;

            Assert.AreEqual(AiAttachmentCategory.Text, attachment.Category);
            Assert.AreEqual(mediaType, attachment.MediaType, "The charset parameter stays on MediaType.");
            StringAssert.Contains("\n" + json + "\n", text);
        }

        [Test]
        public void MediaTypeParameters_AreDroppedForRouting_AndJsonWithCharsetIsText()
        {
            Assert.AreEqual("application/json", AiAttachment.NormalizeMediaType("Application/JSON; charset=utf-8"));
            Assert.AreEqual("text/plain", AiAttachment.NormalizeMediaType("text/plain;charset=utf-16"));
            Assert.AreEqual("image/jpeg", AiAttachment.NormalizeMediaType("image/jpg; quality=90"));
            Assert.AreEqual(AiAttachmentCategory.Text, AiAttachment.Classify("application/json; charset=utf-8"));
            Assert.AreEqual(AiAttachmentCategory.Image, AiAttachment.Classify("image/png;name=a.png"));
            const string canonical = "text/plain";
            Assert.AreSame(canonical, AiAttachment.NormalizeMediaType(canonical));

            AiAttachment json = AiAttachment.FromFile("cfg", Encoding.UTF8.GetBytes("{\"a\":1}"),
                "application/json; charset=utf-8");
            StringAssert.Contains("(application/json) ---\n{\"a\":1}",
                AiUserMessageBuilder.BuildUserMessage("", new[] { json }).Text);
        }

        [Test]
        public void Utf16WithoutMarkOrCharset_FailsLoudNamingTheFile()
        {
            byte[] file = Encoding.Unicode.GetBytes("print('hi')");

            ArgumentException ex = Assert.Throws<ArgumentException>(() =>
                AiUserMessageBuilder.BuildUserMessage("", new[] { AiAttachment.FromFile("mod.lua", file) }));

            StringAssert.Contains("mod.lua", ex.Message);
            StringAssert.Contains("NUL", ex.Message);
        }

        [Test]
        public void BinaryPayloadNamedLikeText_FailsLoudNamingTheFile()
        {
            byte[] binary = new byte[4096];
            new Random(5).NextBytes(binary);
            for (int i = 0; i < binary.Length; i++)
            {
                binary[i] = (byte)(binary[i] | 0x80);
            }

            ArgumentException ex = Assert.Throws<ArgumentException>(() =>
                AiUserMessageBuilder.BuildUserMessage("", new[] { AiAttachment.FromFile("crash.log", binary) }));

            StringAssert.Contains("crash.log", ex.Message);
            StringAssert.Contains("cannot be decoded", ex.Message);
        }

        [Test]
        public void OneStrayInvalidByte_InALargeTextFile_StillPasses()
        {
            byte[] file = Encoding.UTF8.GetBytes(new string('a', 1000));
            file[500] = 0xFF;

            string text = AiUserMessageBuilder.BuildUserMessage("",
                new[] { AiAttachment.FromFile("notes.txt", file) }).Text;

            StringAssert.Contains("�", text);
        }

        [Test]
        public void UnknownCharset_FailsLoudNamingTheFileAndCharset()
        {
            AiAttachment attachment = AiAttachment.FromFile("a.txt", Encoding.UTF8.GetBytes("hi"),
                "text/plain; charset=x-no-such-charset");

            ArgumentException ex = Assert.Throws<ArgumentException>(() =>
                AiUserMessageBuilder.BuildUserMessage("", new[] { attachment }));

            StringAssert.Contains("a.txt", ex.Message);
            StringAssert.Contains("x-no-such-charset", ex.Message);
        }

        // ===== Data URLs, streams, image factories =====

        [Test]
        public void DataUrlWithParameters_IsDecoded_AndKeepsItsCharset()
        {
            string payload = Convert.ToBase64String(Encoding.Unicode.GetBytes("hello"));
            AiAttachment text = AiAttachment.FromDataUrl("data:text/plain;charset=utf-16le;base64," + payload, "a.txt");
            AiAttachment image = AiAttachment.FromDataUrl(
                "data:image/png;name=hero.png;base64," + Convert.ToBase64String(Png));

            Assert.AreEqual("text/plain;charset=utf-16le", text.MediaType);
            Assert.AreEqual("text/plain", text.ResolvedMediaType);
            StringAssert.Contains("\nhello\n", AiUserMessageBuilder.BuildUserMessage("", new[] { text }).Text);
            Assert.AreEqual("image/png", image.MediaType);
            CollectionAssert.AreEqual(Png, image.Data);
        }

        [TestCase("data:;base64,AAAA")]
        [TestCase("data:text/plain;charset=utf-8,hello")]
        [TestCase("data:image/png;base64;x=1,AAAA")]
        public void DataUrlWithoutBase64RightBeforeTheComma_IsRejected(string dataUrl)
        {
            Assert.IsFalse(AiAttachment.TryFromDataUrl(dataUrl, out _));
        }

        [Test]
        public void Base64WithLineBreaks_DecodesToTheExactBytes_EvenAtTheSizeCap()
        {
            byte[] bytes = new byte[300];
            new Random(9).NextBytes(bytes);
            string wrapped = Convert.ToBase64String(bytes, Base64FormattingOptions.InsertLineBreaks) + "\r\n";

            Assert.IsTrue(AiAttachment.TryDecodeBase64(wrapped.AsSpan(), bytes.Length,
                out ReadOnlyMemory<byte> decoded));
            CollectionAssert.AreEqual(bytes, decoded.ToArray());
            Assert.IsFalse(AiAttachment.TryDecodeBase64(wrapped.AsSpan(), bytes.Length - 1, out _));
        }

        [Test]
        public void FromStream_PositionPastTheEnd_GivesAnEmptyAttachment()
        {
            MemoryStream memory = new();
            memory.Write(Png, 0, Png.Length);
            memory.Position = 100;
            using BufferedStream buffered = new(new MemoryStream(Png));
            buffered.Position = 100;

            Assert.AreEqual(0, AiAttachment.FromStream(memory, "image/png").Memory.Length);
            Assert.AreEqual(0, AiAttachment.FromStream(buffered, "image/png").Memory.Length);
        }

        [Test]
        public void Image_WithoutAType_SniffsTheSignature()
        {
            byte[] jpeg = { 0xFF, 0xD8, 0xFF, 0xE0, 1, 2 };
            byte[] gif = Encoding.ASCII.GetBytes("GIF89a....");
            byte[] webp = Encoding.ASCII.GetBytes("RIFF\0\0\0\0WEBPVP8 ");

            Assert.AreEqual("image/png", AiAttachment.Image(Png).ResolvedMediaType);
            Assert.AreEqual("image/jpeg", AiAttachment.Image(jpeg).ResolvedMediaType);
            Assert.AreEqual("image/gif", AiAttachment.Image(new ReadOnlyMemory<byte>(gif)).ResolvedMediaType);
            Assert.AreEqual("image/webp", AiAttachment.Image(webp).ResolvedMediaType);
            Assert.AreEqual("image/png", AiAttachment.Image(new byte[] { 1, 2 }, "", "hero.png").ResolvedMediaType,
                "Unrecognised bytes fall back to an image file name.");
        }

        [Test]
        public void Image_RejectsNonImageTypesAndUndetectableBytes()
        {
            ArgumentException textType = Assert.Throws<ArgumentException>(() =>
                AiAttachment.Image(Png, "text/plain"));
            StringAssert.Contains("text/plain", textType.Message);
            Assert.Throws<ArgumentException>(() => AiAttachment.Image(new byte[] { 1, 2, 3 }));
            Assert.Throws<ArgumentException>(() => AiAttachment.Image(new byte[] { 1, 2, 3 }, "", "notes.txt"));
            Assert.Throws<ArgumentException>(() =>
                AiAttachment.ImageUri(new Uri("https://example.com/a"), "application/json"));
            Assert.AreEqual(AiAttachmentCategory.Image, AiAttachment.Image(Png, "image/jpg").Category);
        }

        [Test]
        public void SupportedMediaTypeLists_AreReadOnly()
        {
            Assert.IsNotInstanceOf<string[]>(AiAttachment.SupportedImageMediaTypes);
            Assert.IsNotInstanceOf<string[]>(AiAttachment.SupportedTextMediaTypes);
            Assert.IsTrue(((ICollection<string>)AiAttachment.SupportedImageMediaTypes).IsReadOnly);
            Assert.Throws<NotSupportedException>(() =>
                ((IList<string>)AiAttachment.SupportedTextMediaTypes)[0] = "application/octet-stream");
            CollectionAssert.Contains(AiAttachment.SupportedImageMediaTypes, "image/png");
        }

        // ===== Null attachments, budgeting, wire cache =====

        [Test]
        public void ListOfOnlyNulls_IsTheLegacyTextTurn()
        {
            AiAttachment[] nulls = { null, null };

            Assert.IsFalse(AiUserMessageBuilder.HasAttachments(nulls));
            Assert.IsFalse(AiUserMessageBuilder.HasAttachments(null));
            Assert.IsTrue(AiUserMessageBuilder.HasAttachments(new[] { null, AiAttachment.Image(Png) }));
            Assert.AreEqual("hi", WireContentFor(AiUserMessageBuilder.BuildUserMessage("hi", nulls)));
        }

        [Test]
        public void InlinedTextTokens_CountTextFilesOnly_AndShrinkTheHistoryBudget()
        {
            AiAttachment[] attachments =
            {
                AiAttachment.FromText("a.lua", new string('x', 4000)),
                AiAttachment.FromFile("b.md", new byte[400]),
                AiAttachment.Image(new byte[100_000], "image/png"),
                null
            };

            Assert.AreEqual(1100, AiUserMessageBuilder.EstimateInlinedTextTokens(attachments));
            Assert.AreEqual(0, AiUserMessageBuilder.EstimateInlinedTextTokens(null));

            DefaultContextBudgetPolicy policy = new();
            ContextBudgetRequest plain = new() { MaxContextTokens = 32768, SystemPrompt = "sys", UserPayload = "hi" };
            ContextBudgetRequest withFiles = new()
            {
                MaxContextTokens = 32768, SystemPrompt = "sys", UserPayload = "hi", InlinedAttachmentTokens = 1100
            };
            HeuristicTokenEstimator estimator = new();
            Assert.AreEqual(policy.Compute(plain, estimator).HistoryTokenBudget - 1100,
                policy.Compute(withFiles, estimator).HistoryTokenBudget);
        }

        [Test]
        public void WireImagePart_IsEncodedOncePerTurn_AndACallersOwnPartIsNeverCached()
        {
            ChatMessage msg = AiUserMessageBuilder.BuildUserMessage("look",
                new[] { AiAttachment.Image(new byte[4096], "image/jpeg") });
            string first = ImageUrlOf(MeaiOpenAiChatClient.BuildOpenAiMessageContent(msg.Text, msg.Contents));
            string second = ImageUrlOf(MeaiOpenAiChatClient.BuildOpenAiMessageContent(msg.Text, msg.Contents));
            Assert.AreSame(first, second, "Every request of the turn reuses the one encoded data URL.");

            byte[] callerBuffer = new byte[16];
            DataContent callerPart = new(callerBuffer, "image/png");
            List<AIContent> callerContents = new() { callerPart };
            string before = ImageUrlOf(MeaiOpenAiChatClient.BuildOpenAiMessageContent("", callerContents));
            callerBuffer[0] = 0xFF;
            string after = ImageUrlOf(MeaiOpenAiChatClient.BuildOpenAiMessageContent("", callerContents));
            Assert.AreNotEqual(before, after, "A caller-built part is encoded fresh, so it never serves stale bytes.");
        }

        [Test]
        public void Allocation_RepeatedRequestsOfOneTurn_DoNotReencodeTheImages()
        {
            RequireAllocationCounter();
            AiAttachment[] images =
            {
                AiAttachment.Image(new byte[1024 * 1024], "image/jpeg"), AiAttachment.Image(new byte[1024 * 1024], "image/png"),
                AiAttachment.Image(new byte[1024 * 1024], "image/webp"), AiAttachment.Image(new byte[1024 * 1024], "image/gif")
            };
            ChatMessage msg = AiUserMessageBuilder.BuildUserMessage("compare", images);
            GC.KeepAlive(MeaiOpenAiChatClient.BuildOpenAiMessageContent(msg.Text, msg.Contents));

            long perRequest = AllocatedPerCall(() => MeaiOpenAiChatClient.BuildOpenAiMessageContent(msg.Text, msg.Contents));

            TestContext.WriteLine($"wire content for 4 x 1 MB images after the first request: {perRequest} B per request");
            Assert.Less(perRequest, 8192, "Later requests of the turn reuse the encoded data URLs (about 11 MB of strings before).");
        }

        private static string ImageUrlOf(object wireContent)
        {
            foreach (object part in (List<object>)wireContent)
            {
                Dictionary<string, object> dictionary = (Dictionary<string, object>)part;
                if (Equals(dictionary["type"], "image_url"))
                {
                    return (string)((Dictionary<string, object>)dictionary["image_url"])["url"];
                }
            }

            return null;
        }

        private static Encoding EncodingFor(string name)
        {
            return name switch
            {
                "utf-16le" => new UnicodeEncoding(false, true),
                "utf-16be" => new UnicodeEncoding(true, true),
                "utf-32le" => new UTF32Encoding(false, true),
                "utf-32be" => new UTF32Encoding(true, true),
                "utf-8" => new UTF8Encoding(true),
                _ => Encoding.GetEncoding(name)
            };
        }

        private static void RequireAllocationCounter()
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            GC.KeepAlive(new byte[1024]);
            if (GC.GetAllocatedBytesForCurrentThread() - before < 1024)
            {
                Assert.Ignore("This runtime does not report per-thread allocations.");
            }
        }

        private static long AllocatedPerCall(Func<object> action)
        {
            const int calls = 16;
            for (int i = 0; i < 3; i++)
            {
                GC.KeepAlive(action());
            }

            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < calls; i++)
            {
                GC.KeepAlive(action());
            }

            return (GC.GetAllocatedBytesForCurrentThread() - before) / calls;
        }

        private sealed class CapturingOrchestrator : IAiOrchestrationService
        {
            public List<AiTaskRequest> Requests { get; } = new();

            public Task<string> RunTaskAsync(AiTaskRequest task, CancellationToken cancellationToken = default)
            {
                Requests.Add(task);
                return Task.FromResult("ok");
            }

            public void CancelTasks(string cancellationScope)
            {
            }
        }
    }
}
#endif
