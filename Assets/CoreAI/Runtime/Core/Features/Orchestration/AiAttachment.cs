using System;
using System.Collections.Generic;

namespace CoreAI.Ai
{
    /// <summary>
    /// Routing category an <see cref="AiAttachment"/> resolves to when it is composed into the user turn.
    /// The category is derived from the (possibly inferred) media type and decides how the attachment
    /// reaches the model.
    /// </summary>
    public enum AiAttachmentCategory
    {
        /// <summary>
        /// A raster image (<c>image/png</c>, <c>image/jpeg</c>, <c>image/webp</c>, <c>image/gif</c>).
        /// Sent as a native multimodal image part; only vision-capable models can read it.
        /// </summary>
        Image = 0,

        /// <summary>
        /// A UTF-8 text-like file (<c>text/*</c>, <c>application/json</c>, Lua, Markdown, source code, …).
        /// Inlined verbatim into the prompt text, so it reaches EVERY model including text-only local ones.
        /// </summary>
        Text = 1,

        /// <summary>
        /// A media type CoreAI cannot route (audio, video, meshes, arbitrary binary). Composing such an
        /// attachment throws — CoreAI never silently drops it nor base64-inlines binary into the prompt.
        /// </summary>
        Unsupported = 2
    }

    /// <summary>
    /// One file a caller attaches to an <see cref="AiTaskRequest"/> alongside the text prompt. CoreAI is a
    /// framework, so this is a universal attachment (a texture/sprite as PNG, a Lua script, a Markdown or
    /// JSON file, a code snippet, …) — not an image-only type.
    /// <para>
    /// Provide either inline <see cref="Data"/> (any category) or a <see cref="Uri"/> (images only). The
    /// media type may be given explicitly via <see cref="MediaType"/>, or left empty to be inferred from the
    /// <see cref="FileName"/> extension. An unknown extension with no explicit media type is a loud error at
    /// compose time, never a silent drop.
    /// </para>
    /// <para>
    /// Routing by <see cref="Category"/> (see <see cref="AiUserMessageBuilder"/>):
    /// <list type="bullet">
    /// <item><description><see cref="AiAttachmentCategory.Image"/> → native image part (vision models only).</description></item>
    /// <item><description><see cref="AiAttachmentCategory.Text"/> → inlined into the prompt text (any model).</description></item>
    /// <item><description><see cref="AiAttachmentCategory.Unsupported"/> → throws with the supported categories.</description></item>
    /// </list>
    /// </para>
    /// </summary>
    public sealed class AiAttachment
    {
        /// <summary>Supported image media types (lower-case, normalized). Only these reach a vision model.</summary>
        public static readonly IReadOnlyList<string> SupportedImageMediaTypes = new[]
        {
            "image/png", "image/jpeg", "image/webp", "image/gif"
        };

        /// <summary>
        /// Media types (beyond the whole <c>text/*</c> family) that are treated as UTF-8 text and inlined
        /// into the prompt. Extend the extension map in <see cref="InferMediaTypeFromFileName"/> in lock-step.
        /// </summary>
        public static readonly IReadOnlyList<string> SupportedTextMediaTypes = new[]
        {
            "application/json", "application/x-lua", "application/xml", "application/yaml",
            "application/x-yaml", "application/javascript", "application/toml", "application/sql"
        };

        // WHY: A single text file inlined into the prompt is bounded so one oversized paste cannot blow the
        // context window; images are not capped here (they ride the native image part, gated by the model).
        /// <summary>Maximum decoded size (bytes) of a single inlined text attachment.</summary>
        public const int MaxInlineTextBytes = 256 * 1024;

        /// <summary>Maximum combined decoded size (bytes) of ALL inlined text attachments on one request.</summary>
        public const int MaxTotalInlineTextBytes = 1024 * 1024;

        private AiAttachment(string fileName, string mediaType, byte[] data, Uri uri)
        {
            FileName = fileName ?? "";
            MediaType = mediaType ?? "";
            Data = data;
            Uri = uri;
        }

        /// <summary>Original file name (e.g. <c>hero.png</c>, <c>level.lua</c>). Optional but recommended:
        /// it labels the inlined text block and drives media-type inference when <see cref="MediaType"/> is empty.</summary>
        public string FileName { get; }

        /// <summary>IANA media type (e.g. <c>image/png</c>, <c>application/x-lua</c>). Empty = infer from
        /// <see cref="FileName"/> at compose time.</summary>
        public string MediaType { get; }

        /// <summary>Inline file bytes. Required for text attachments; either this or <see cref="Uri"/> for images.</summary>
        public byte[] Data { get; }

        /// <summary>Remote/asset URI for an image attachment. Ignored for text (text must be inline <see cref="Data"/>).</summary>
        public Uri Uri { get; }

        /// <summary>The effective media type: <see cref="MediaType"/> when set, otherwise inferred from
        /// <see cref="FileName"/>. Empty when neither is available.</summary>
        public string ResolvedMediaType =>
            string.IsNullOrWhiteSpace(MediaType) ? InferMediaTypeFromFileName(FileName) : NormalizeMediaType(MediaType);

        /// <summary>The routing category derived from <see cref="ResolvedMediaType"/>.</summary>
        public AiAttachmentCategory Category => Classify(ResolvedMediaType);

        /// <summary>
        /// Creates an image attachment from raw bytes. <paramref name="mediaType"/> must be one of
        /// <see cref="SupportedImageMediaTypes"/> (or inferable from <paramref name="fileName"/>).
        /// </summary>
        public static AiAttachment Image(byte[] data, string mediaType = "", string fileName = "")
        {
            if (data == null || data.Length == 0)
            {
                throw new ArgumentException("Image attachment requires non-empty Data.", nameof(data));
            }

            return new AiAttachment(fileName, mediaType, data, uri: null);
        }

        /// <summary>Creates an image attachment that references a URI (vision-capable models only).</summary>
        public static AiAttachment ImageUri(Uri uri, string mediaType = "", string fileName = "")
        {
            if (uri == null)
            {
                throw new ArgumentNullException(nameof(uri));
            }

            return new AiAttachment(fileName, mediaType, data: null, uri);
        }

        /// <summary>
        /// Creates an attachment from a file name and its bytes, inferring the media type from the extension
        /// when <paramref name="mediaType"/> is empty. Works for both images and text-like files; the routing
        /// category is decided later from the resolved media type.
        /// </summary>
        public static AiAttachment FromFile(string fileName, byte[] data, string mediaType = "")
        {
            if (data == null)
            {
                throw new ArgumentNullException(nameof(data));
            }

            return new AiAttachment(fileName, mediaType, data, uri: null);
        }

        /// <summary>
        /// Classifies a (normalized) media type into a routing <see cref="AiAttachmentCategory"/>.
        /// </summary>
        public static AiAttachmentCategory Classify(string mediaType)
        {
            if (string.IsNullOrWhiteSpace(mediaType))
            {
                return AiAttachmentCategory.Unsupported;
            }

            string normalized = NormalizeMediaType(mediaType);
            if (IsSupportedImageMediaType(normalized))
            {
                return AiAttachmentCategory.Image;
            }

            if (IsTextMediaType(normalized))
            {
                return AiAttachmentCategory.Text;
            }

            return AiAttachmentCategory.Unsupported;
        }

        /// <summary>True when <paramref name="mediaType"/> is one of the supported image media types.</summary>
        public static bool IsSupportedImageMediaType(string mediaType)
        {
            string normalized = NormalizeMediaType(mediaType);
            foreach (string supported in SupportedImageMediaTypes)
            {
                if (string.Equals(supported, normalized, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>True when <paramref name="mediaType"/> is treated as UTF-8 text (inlined into the prompt).</summary>
        public static bool IsTextMediaType(string mediaType)
        {
            string normalized = NormalizeMediaType(mediaType);
            if (normalized.StartsWith("text/", StringComparison.Ordinal))
            {
                return true;
            }

            foreach (string supported in SupportedTextMediaTypes)
            {
                if (string.Equals(supported, normalized, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Lower-cases the media type and folds the common <c>image/jpg</c> alias to <c>image/jpeg</c>, so
        /// comparisons and the emitted <c>data:</c> URL use canonical spelling.
        /// </summary>
        public static string NormalizeMediaType(string mediaType)
        {
            if (string.IsNullOrWhiteSpace(mediaType))
            {
                return "";
            }

            string trimmed = mediaType.Trim().ToLowerInvariant();
            return string.Equals(trimmed, "image/jpg", StringComparison.Ordinal) ? "image/jpeg" : trimmed;
        }

        /// <summary>
        /// Infers a media type from a file-name extension using a small built-in map (images and common
        /// text/code formats). Returns an empty string for unknown extensions — the caller treats that as a
        /// loud error rather than guessing.
        /// </summary>
        public static string InferMediaTypeFromFileName(string fileName)
        {
            if (string.IsNullOrWhiteSpace(fileName))
            {
                return "";
            }

            int dot = fileName.LastIndexOf('.');
            if (dot < 0 || dot == fileName.Length - 1)
            {
                return "";
            }

            string ext = fileName.Substring(dot + 1).Trim().ToLowerInvariant();
            return ext switch
            {
                // Images
                "png" => "image/png",
                "jpg" or "jpeg" => "image/jpeg",
                "webp" => "image/webp",
                "gif" => "image/gif",
                // Text / data
                "txt" or "text" or "log" => "text/plain",
                "md" or "markdown" => "text/markdown",
                "csv" => "text/csv",
                "html" or "htm" => "text/html",
                "css" => "text/css",
                "json" => "application/json",
                "xml" => "application/xml",
                "yaml" or "yml" => "application/yaml",
                "toml" => "application/toml",
                "sql" => "application/sql",
                "lua" => "application/x-lua",
                "js" => "application/javascript",
                // Source code (inlined as text/plain family)
                "cs" => "text/x-csharp",
                "ts" => "text/plain",
                "py" => "text/x-python",
                "shader" or "hlsl" or "glsl" or "cginc" or "compute" => "text/plain",
                "cfg" or "ini" or "conf" => "text/plain",
                _ => ""
            };
        }

        /// <summary>
        /// A compact, byte-free one-line description for text-based history stores (never the raw bytes),
        /// e.g. <c>[attachment: hero.png image/png 12 KB]</c>. Used when persisting the user turn so the
        /// conversation reflects that a file was sent without bloating or breaking serialization.
        /// </summary>
        public string DescribeForHistory()
        {
            string name = string.IsNullOrWhiteSpace(FileName) ? "(unnamed)" : FileName.Trim();
            string type = string.IsNullOrWhiteSpace(ResolvedMediaType) ? "unknown" : ResolvedMediaType;
            int bytes = Data?.Length ?? 0;
            return bytes > 0
                ? $"[attachment: {name} {type} {FormatSize(bytes)}]"
                : $"[attachment: {name} {type}]";
        }

        private static string FormatSize(int bytes)
        {
            if (bytes < 1024)
            {
                return bytes + " B";
            }

            if (bytes < 1024 * 1024)
            {
                return (bytes / 1024) + " KB";
            }

            return (bytes / (1024 * 1024)) + " MB";
        }
    }
}
