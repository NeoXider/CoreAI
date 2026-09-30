using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Text;
using MEAI = Microsoft.Extensions.AI;

namespace CoreAI.Ai
{
    /// <summary>
    /// Composes the current-turn user message from a text prompt and its <see cref="AiAttachment"/> list.
    /// This is the single routing/validation point shared by the LLM client wire builders, so every provider
    /// path (streaming and non-streaming) gets identical, provider-safe content.
    /// <para>
    /// Routing per attachment (see <see cref="AiAttachmentCategory"/>):
    /// <list type="number">
    /// <item><description><b>Image</b> → a <see cref="MEAI.DataContent"/> / <see cref="MEAI.UriContent"/>
    /// image part, which the OpenAI-compatible client serializes to an <c>image_url</c>. Only vision-capable
    /// models read it.</description></item>
    /// <item><description><b>Text</b> → decoded by its byte-order mark (UTF-8, UTF-16LE/BE, UTF-32LE/BE), else by the
    /// <c>charset</c> of its media type, else as UTF-8, and inlined into the prompt text as a clearly delimited
    /// block, so it reaches EVERY model including text-only local ones. A payload that is not text in that
    /// encoding (NUL characters, or more than 1 in 64 characters undecodable) throws naming the file instead of
    /// sending mojibake.</description></item>
    /// <item><description><b>Unsupported</b> → throws <see cref="ArgumentException"/> listing the supported
    /// categories. Binary is never silently dropped nor base64-inlined into text.</description></item>
    /// </list>
    /// </para>
    /// <para>
    /// Allocations: every attachment is validated before anything is built, so a rejected turn allocates
    /// nothing. Image bytes are wrapped, never copied; the prompt string is reused as-is unless text files are
    /// inlined, in which case the whole prompt text is written once into a string of the exact final length.
    /// The image parts built here are the ones whose wire <c>data:</c> URL the OpenAI-compatible client encodes
    /// once and reuses for every request of the turn.
    /// </para>
    /// </summary>
    public static class AiUserMessageBuilder
    {
        private const string FileHeaderStart = "--- attached file: ";
        private const string FileTypeOpen = " (";
        private const string FileHeaderEnd = ") ---\n";
        private const string FileFooterStart = "\n--- end of ";
        private const string FileFooterEnd = " ---";
        private const string BlockSeparator = "\n\n";

        // WHY: the undecodable share at which a "text" file is treated as binary or mis-declared; one stray byte in a large legitimate file still passes.
        private const int MaxReplacementShare = 64;

        private static readonly ConditionalWeakTable<MEAI.DataContent, WireUrl> OwnedImageParts = new();

        /// <summary>
        /// True when <paramref name="attachments"/> holds at least one non-null attachment. A list of only nulls
        /// is treated exactly like no list: it composes the legacy text-only message and never appends an empty
        /// user message.
        /// </summary>
        public static bool HasAttachments(IReadOnlyList<AiAttachment> attachments)
        {
            if (attachments == null)
            {
                return false;
            }

            for (int i = 0; i < attachments.Count; i++)
            {
                if (attachments[i] != null)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// A token estimate of the text files that will be inlined into the prompt (bytes / 4, the usual
        /// English-and-code ratio), for context budgeting. Images and unresolvable attachments count as 0: images
        /// ride a separate channel and a bad attachment fails at compose time anyway.
        /// </summary>
        public static int EstimateInlinedTextTokens(IReadOnlyList<AiAttachment> attachments)
        {
            if (attachments == null)
            {
                return 0;
            }

            long bytes = 0;
            for (int i = 0; i < attachments.Count; i++)
            {
                AiAttachment attachment = attachments[i];
                if (attachment != null && attachment.HasInlineData && attachment.Category == AiAttachmentCategory.Text)
                {
                    bytes += attachment.InlineText?.Length ?? attachment.Memory.Length;
                }
            }

            return (int)Math.Min(int.MaxValue, (bytes + 3) / 4);
        }

        /// <summary>
        /// Builds the user <see cref="MEAI.ChatMessage"/> for a turn. When <paramref name="attachments"/> is
        /// null/empty this returns a plain-text message byte-identical to the legacy path (no multimodal parts).
        /// An image-only turn with an empty prompt carries only the image parts (no empty text part).
        /// </summary>
        /// <param name="prompt">The user's text prompt (may be empty).</param>
        /// <param name="attachments">Optional attachments to route into the message.</param>
        /// <exception cref="ArgumentException">
        /// An attachment resolves to an unsupported media type, a text attachment exceeds the per-file or
        /// total size cap, a text attachment carries no inline data, or a media type cannot be resolved.
        /// </exception>
        public static MEAI.ChatMessage BuildUserMessage(string prompt, IReadOnlyList<AiAttachment> attachments)
        {
            prompt ??= "";
            if (attachments == null || attachments.Count == 0)
            {
                // WHY: Preserve the exact legacy shape for the no-attachment case so existing behavior and
                // the plain-string wire content stay byte-identical.
                return new MEAI.ChatMessage(MEAI.ChatRole.User, prompt);
            }

            int imageCount = 0;
            int textCount = 0;
            long totalInlineBytes = 0;
            for (int i = 0; i < attachments.Count; i++)
            {
                AiAttachment attachment = attachments[i];
                if (attachment == null)
                {
                    continue;
                }

                string mediaType = attachment.ResolvedMediaType;
                if (string.IsNullOrWhiteSpace(mediaType))
                {
                    throw new ArgumentException(
                        $"Attachment '{DescribeName(attachment)}' has no MediaType and its extension is not " +
                        "recognized. Set AiAttachment.MediaType explicitly or use a known file extension.");
                }

                switch (attachment.Category)
                {
                    case AiAttachmentCategory.Image:
                        if ((!attachment.HasInlineData || attachment.Memory.IsEmpty) && attachment.Uri == null)
                        {
                            throw new ArgumentException(
                                $"Image attachment '{DescribeName(attachment)}' has neither inline Data nor a Uri.");
                        }

                        imageCount++;
                        break;

                    case AiAttachmentCategory.Text:
                        totalInlineBytes += ValidateTextAttachment(attachment, mediaType, totalInlineBytes);
                        textCount++;
                        break;

                    default:
                        throw new ArgumentException(
                            $"Attachment '{DescribeName(attachment)}' has unsupported media type '{mediaType}'. " +
                            "Supported categories: images (" +
                            string.Join(", ", AiAttachment.SupportedImageMediaTypes) +
                            ") sent to vision-capable models, and text-like files (text/*, " +
                            string.Join(", ", AiAttachment.SupportedTextMediaTypes) +
                            ") inlined into the prompt. Audio, video, meshes and arbitrary binary are not supported.");
                }
            }

            if (textCount > 0)
            {
                // WHY: decoding checks run after every size check, so a turn rejected for size never pays for decoding a large file.
                for (int i = 0; i < attachments.Count; i++)
                {
                    AiAttachment attachment = attachments[i];
                    if (attachment != null && attachment.Category == AiAttachmentCategory.Text &&
                        attachment.InlineText == null)
                    {
                        RequireDecodableText(attachment, attachment.ResolvedMediaType);
                    }
                }
            }

            string text = textCount == 0 ? prompt : ComposeInlinedText(prompt, attachments);
            if (imageCount == 0)
            {
                // WHY: Text-only (prompt + inlined files) stays a plain text message — no multimodal parts —
                // so text-only providers are unaffected.
                return new MEAI.ChatMessage(MEAI.ChatRole.User, text);
            }

            bool hasText = text.Length > 0;
            List<MEAI.AIContent> contents = new(imageCount + (hasText ? 1 : 0));
            if (hasText)
            {
                contents.Add(new MEAI.TextContent(text));
            }

            for (int i = 0; i < attachments.Count; i++)
            {
                AiAttachment attachment = attachments[i];
                if (attachment != null && attachment.Category == AiAttachmentCategory.Image)
                {
                    contents.Add(BuildImageContent(attachment));
                }
            }

            return new MEAI.ChatMessage(MEAI.ChatRole.User, contents);
        }

        /// <summary>
        /// The provider image part for an image attachment: a <see cref="MEAI.DataContent"/> over the
        /// attachment's own memory (no copy) or a <see cref="MEAI.UriContent"/>.
        /// </summary>
        internal static MEAI.AIContent BuildImageContent(AiAttachment attachment)
        {
            if (attachment.HasInlineData && !attachment.Memory.IsEmpty)
            {
                MEAI.DataContent content = new(attachment.Memory, attachment.ResolvedMediaType);
                OwnedImageParts.Add(content, new WireUrl());
                return content;
            }

            if (attachment.Uri != null)
            {
                return new MEAI.UriContent(attachment.Uri, attachment.ResolvedMediaType);
            }

            throw new ArgumentException(
                $"Image attachment '{DescribeName(attachment)}' has neither inline Data nor a Uri.");
        }

        /// <summary>
        /// The wire <c>data:</c> URL cached for an image part built by <see cref="BuildImageContent"/>, built with
        /// <paramref name="build"/> on first use. Only parts CoreAI created for one turn are cached, so a caller's own
        /// <see cref="MEAI.DataContent"/> is never served a stale string; the cache entry dies with the part.
        /// </summary>
        internal static string GetOrBuildWireUrl(MEAI.DataContent content, string mediaType,
            Func<string, ReadOnlyMemory<byte>, string> build)
        {
            if (!OwnedImageParts.TryGetValue(content, out WireUrl slot))
            {
                return build(mediaType, content.Data);
            }

            WireUrlValue cached = slot.Value;
            if (cached != null && string.Equals(cached.MediaType, mediaType, StringComparison.Ordinal))
            {
                return cached.Url;
            }

            string url = build(mediaType, content.Data);
            slot.Value = new WireUrlValue(mediaType, url);
            return url;
        }

        // WHY: Returns the byte count so the caller can enforce the running total cap.
        private static int ValidateTextAttachment(AiAttachment attachment, string mediaType, long runningTotalBytes)
        {
            if (!attachment.HasInlineData)
            {
                throw new ArgumentException(
                    $"Text attachment '{DescribeName(attachment)}' ({mediaType}) requires inline Data; " +
                    "URI-based text attachments are not supported (CoreAI does not fetch remote text).");
            }

            int byteCount = attachment.InlineByteCount;
            if (byteCount > AiAttachment.MaxInlineTextBytes)
            {
                throw new ArgumentException(
                    $"Text attachment '{DescribeName(attachment)}' is {byteCount} bytes, over the " +
                    $"{AiAttachment.MaxInlineTextBytes}-byte per-file inline limit.");
            }

            if (runningTotalBytes + byteCount > AiAttachment.MaxTotalInlineTextBytes)
            {
                throw new ArgumentException(
                    $"Inlined text attachments exceed the {AiAttachment.MaxTotalInlineTextBytes}-byte total limit " +
                    $"at attachment '{DescribeName(attachment)}'.");
            }

            return byteCount;
        }

        /// <summary>
        /// Decodes the payload once in small stack chunks (no allocation beyond one decoder) and throws when it is
        /// not text in the resolved encoding: a NUL character (binary data, or UTF-16/32 without a byte-order mark
        /// or charset), or more than one undecodable character in <see cref="MaxReplacementShare"/>.
        /// </summary>
        private static void RequireDecodableText(AiAttachment attachment, string mediaType)
        {
            ReadOnlySpan<byte> data = attachment.Memory.Span;
            Encoding encoding = attachment.ResolveTextEncoding(data, out int bom);
            ReadOnlySpan<byte> body = data.Slice(bom);
            Decoder decoder = encoding.GetDecoder();
            Span<char> chunk = stackalloc char[1024];
            long total = 0;
            long replacements = 0;
            while (true)
            {
                bool flush = body.IsEmpty;
                decoder.Convert(body, chunk, flush, out int bytesUsed, out int charsUsed, out bool completed);
                body = body.Slice(bytesUsed);
                for (int i = 0; i < charsUsed; i++)
                {
                    char c = chunk[i];
                    if (c == '\0')
                    {
                        throw new ArgumentException(
                            $"Text attachment '{DescribeName(attachment)}' ({mediaType}) contains NUL characters when " +
                            $"decoded as {encoding.WebName}: it is binary data, or UTF-16/UTF-32 text without a " +
                            "byte-order mark. Save it with a BOM, declare the charset (e.g. 'text/plain; charset=utf-16') " +
                            "or attach it as a supported image.");
                    }

                    if (c == '\uFFFD')
                    {
                        replacements++;
                    }
                }

                total += charsUsed;
                if (flush ? completed || charsUsed == 0 : bytesUsed == 0 && charsUsed == 0)
                {
                    break;
                }
            }

            if (replacements > 0 && replacements * MaxReplacementShare > total)
            {
                throw new ArgumentException(
                    $"Text attachment '{DescribeName(attachment)}' ({mediaType}) is not valid {encoding.WebName} text: " +
                    $"{replacements} of {total} characters cannot be decoded. It is binary data or uses another " +
                    "encoding; declare the charset (e.g. 'text/plain; charset=iso-8859-1') or convert it to UTF-8.");
            }
        }

        /// <summary>
        /// The prompt followed by one delimited block per text attachment, written once into a string of the
        /// exact final length (the UTF-8 files are decoded straight into it).
        /// </summary>
        private static string ComposeInlinedText(string prompt, IReadOnlyList<AiAttachment> attachments)
        {
            int length = prompt.Length;
            for (int i = 0; i < attachments.Count; i++)
            {
                AiAttachment attachment = attachments[i];
                if (attachment == null || attachment.Category != AiAttachmentCategory.Text)
                {
                    continue;
                }

                if (length > 0)
                {
                    length += BlockSeparator.Length;
                }

                int nameLength = DescribeName(attachment).Length;
                length += FileHeaderStart.Length + nameLength + FileTypeOpen.Length +
                          attachment.ResolvedMediaType.Length + FileHeaderEnd.Length + DecodedLength(attachment) +
                          FileFooterStart.Length + nameLength + FileFooterEnd.Length;
            }

            return string.Create(length, new Composition(prompt, attachments), static (span, state) =>
            {
                int at = Write(span, 0, state.Prompt);
                for (int i = 0; i < state.Attachments.Count; i++)
                {
                    AiAttachment attachment = state.Attachments[i];
                    if (attachment == null || attachment.Category != AiAttachmentCategory.Text)
                    {
                        continue;
                    }

                    if (at > 0)
                    {
                        at = Write(span, at, BlockSeparator);
                    }

                    string name = DescribeName(attachment);
                    at = Write(span, at, FileHeaderStart);
                    at = Write(span, at, name);
                    at = Write(span, at, FileTypeOpen);
                    at = Write(span, at, attachment.ResolvedMediaType);
                    at = Write(span, at, FileHeaderEnd);
                    at = WriteDecoded(span, at, attachment);
                    at = Write(span, at, FileFooterStart);
                    at = Write(span, at, name);
                    at = Write(span, at, FileFooterEnd);
                }
            });
        }

        private static int Write(Span<char> destination, int at, string value)
        {
            value.AsSpan().CopyTo(destination.Slice(at));
            return at + value.Length;
        }

        private static int DecodedLength(AiAttachment attachment)
        {
            string text = attachment.InlineText;
            if (text != null)
            {
                return text.Length - (HasStringBom(text) ? 1 : 0);
            }

            ReadOnlySpan<byte> data = attachment.Memory.Span;
            Encoding encoding = attachment.ResolveTextEncoding(data, out int bom);
            return encoding.GetCharCount(data.Slice(bom));
        }

        private static int WriteDecoded(Span<char> destination, int at, AiAttachment attachment)
        {
            string text = attachment.InlineText;
            if (text != null)
            {
                ReadOnlySpan<char> body = HasStringBom(text) ? text.AsSpan(1) : text.AsSpan();
                body.CopyTo(destination.Slice(at));
                return at + body.Length;
            }

            ReadOnlySpan<byte> data = attachment.Memory.Span;
            Encoding encoding = attachment.ResolveTextEncoding(data, out int bom);
            return at + encoding.GetChars(data.Slice(bom), destination.Slice(at));
        }

        private static bool HasStringBom(string text)
        {
            return text.Length > 0 && text[0] == '﻿';
        }

        private static string DescribeName(AiAttachment attachment)
        {
            return string.IsNullOrWhiteSpace(attachment.FileName) ? "(unnamed)" : attachment.FileName.Trim();
        }

        /// <summary>Per-part cache slot; its value is replaced as a whole, so a racing reader sees a consistent pair.</summary>
        private sealed class WireUrl
        {
            public volatile WireUrlValue Value;
        }

        private sealed class WireUrlValue
        {
            public WireUrlValue(string mediaType, string url)
            {
                MediaType = mediaType;
                Url = url;
            }

            public string MediaType { get; }
            public string Url { get; }
        }

        private readonly struct Composition
        {
            public Composition(string prompt, IReadOnlyList<AiAttachment> attachments)
            {
                Prompt = prompt;
                Attachments = attachments;
            }

            public string Prompt { get; }
            public IReadOnlyList<AiAttachment> Attachments { get; }
        }
    }
}
