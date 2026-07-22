using System;
using System.Collections.Generic;
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
    /// <item><description><b>Text</b> → decoded as UTF-8 (BOM-tolerant) and inlined into the prompt text as a
    /// clearly delimited block, so it reaches EVERY model including text-only local ones.</description></item>
    /// <item><description><b>Unsupported</b> → throws <see cref="ArgumentException"/> listing the supported
    /// categories. Binary is never silently dropped nor base64-inlined into text.</description></item>
    /// </list>
    /// </para>
    /// </summary>
    public static class AiUserMessageBuilder
    {
        /// <summary>
        /// Builds the user <see cref="MEAI.ChatMessage"/> for a turn. When <paramref name="attachments"/> is
        /// null/empty this returns a plain-text message byte-identical to the legacy path (no multimodal parts).
        /// </summary>
        /// <param name="prompt">The user's text prompt (may be empty).</param>
        /// <param name="attachments">Optional attachments to route into the message.</param>
        /// <exception cref="ArgumentException">
        /// An attachment resolves to an unsupported media type, a text attachment exceeds the per-file or
        /// total size cap, a text attachment carries no inline data, or a media type cannot be resolved.
        /// </exception>
        public static MEAI.ChatMessage BuildUserMessage(string prompt, IReadOnlyList<AiAttachment> attachments)
        {
            if (attachments == null || attachments.Count == 0)
            {
                // WHY: Preserve the exact legacy shape for the no-attachment case so existing behavior and
                // the plain-string wire content stay byte-identical.
                return new MEAI.ChatMessage(MEAI.ChatRole.User, prompt ?? "");
            }

            StringBuilder text = new(prompt ?? "");
            List<MEAI.AIContent> imageParts = new();
            long totalInlineBytes = 0;

            foreach (AiAttachment attachment in attachments)
            {
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

                switch (AiAttachment.Classify(mediaType))
                {
                    case AiAttachmentCategory.Image:
                        imageParts.Add(BuildImageContent(attachment, mediaType));
                        break;

                    case AiAttachmentCategory.Text:
                        totalInlineBytes += AppendTextAttachment(text, attachment, mediaType, totalInlineBytes);
                        break;

                    default:
                        throw new ArgumentException(
                            $"Attachment '{DescribeName(attachment)}' has unsupported media type '{mediaType}'. " +
                            "Supported categories: images (" + string.Join(", ", AiAttachment.SupportedImageMediaTypes) +
                            ") sent to vision-capable models, and UTF-8 text-like files (text/*, " +
                            string.Join(", ", AiAttachment.SupportedTextMediaTypes) +
                            ") inlined into the prompt. Audio, video, meshes and arbitrary binary are not supported.");
                }
            }

            if (imageParts.Count == 0)
            {
                // WHY: Text-only (prompt + inlined files) stays a plain text message — no multimodal parts —
                // so text-only providers are unaffected.
                return new MEAI.ChatMessage(MEAI.ChatRole.User, text.ToString());
            }

            List<MEAI.AIContent> contents = new(imageParts.Count + 1)
            {
                new MEAI.TextContent(text.ToString())
            };
            contents.AddRange(imageParts);
            return new MEAI.ChatMessage(MEAI.ChatRole.User, contents);
        }

        private static MEAI.AIContent BuildImageContent(AiAttachment attachment, string mediaType)
        {
            if (attachment.Data != null && attachment.Data.Length > 0)
            {
                return new MEAI.DataContent(attachment.Data, mediaType);
            }

            if (attachment.Uri != null)
            {
                return new MEAI.UriContent(attachment.Uri, mediaType);
            }

            throw new ArgumentException(
                $"Image attachment '{DescribeName(attachment)}' has neither inline Data nor a Uri.");
        }

        // WHY: Returns the decoded byte count so the caller can enforce the running total cap.
        private static long AppendTextAttachment(
            StringBuilder text, AiAttachment attachment, string mediaType, long runningTotalBytes)
        {
            if (attachment.Data == null)
            {
                throw new ArgumentException(
                    $"Text attachment '{DescribeName(attachment)}' ({mediaType}) requires inline Data; " +
                    "URI-based text attachments are not supported (CoreAI does not fetch remote text).");
            }

            if (attachment.Data.Length > AiAttachment.MaxInlineTextBytes)
            {
                throw new ArgumentException(
                    $"Text attachment '{DescribeName(attachment)}' is {attachment.Data.Length} bytes, over the " +
                    $"{AiAttachment.MaxInlineTextBytes}-byte per-file inline limit.");
            }

            if (runningTotalBytes + attachment.Data.Length > AiAttachment.MaxTotalInlineTextBytes)
            {
                throw new ArgumentException(
                    $"Inlined text attachments exceed the {AiAttachment.MaxTotalInlineTextBytes}-byte total limit " +
                    $"at attachment '{DescribeName(attachment)}'.");
            }

            string decoded = DecodeUtf8(attachment.Data);
            string name = string.IsNullOrWhiteSpace(attachment.FileName) ? "(unnamed)" : attachment.FileName.Trim();

            if (text.Length > 0)
            {
                text.Append("\n\n");
            }

            text.Append("--- attached file: ").Append(name).Append(" (").Append(mediaType).Append(") ---\n");
            text.Append(decoded);
            text.Append("\n--- end of ").Append(name).Append(" ---");
            return attachment.Data.Length;
        }

        // WHY: BOM-tolerant UTF-8 decode. new UTF8Encoding(false) does not itself strip a leading BOM, so a
        // file saved with a BOM would keep a stray U+FEFF; detect and skip it explicitly.
        private static string DecodeUtf8(byte[] data)
        {
            if (data == null || data.Length == 0)
            {
                return "";
            }

            int offset = 0;
            if (data.Length >= 3 && data[0] == 0xEF && data[1] == 0xBB && data[2] == 0xBF)
            {
                offset = 3;
            }

            return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false)
                .GetString(data, offset, data.Length - offset);
        }

        private static string DescribeName(AiAttachment attachment)
        {
            return string.IsNullOrWhiteSpace(attachment.FileName) ? "(unnamed)" : attachment.FileName.Trim();
        }
    }
}
