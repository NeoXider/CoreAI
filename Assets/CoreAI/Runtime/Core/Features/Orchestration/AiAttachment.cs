using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

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
        /// A text-like file (<c>text/*</c>, <c>application/json</c>, Lua, Markdown, source code, …). Decoded by its
        /// byte-order mark, else by the <c>charset</c> parameter of its media type, else as UTF-8, and inlined
        /// verbatim into the prompt text, so it reaches EVERY model including text-only local ones.
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
    /// Provide either inline data (any category) or a <see cref="Uri"/> (images only). The media type may be
    /// given explicitly via <see cref="MediaType"/> (parameters such as <c>; charset=utf-16</c> are allowed and
    /// the charset is honoured for text), or left empty to be inferred from the <see cref="FileName"/>
    /// extension. An unknown extension with no explicit media type is a loud error at compose time, never a
    /// silent drop.
    /// </para>
    /// <para>
    /// Routing by <see cref="Category"/> (see <see cref="AiUserMessageBuilder"/>):
    /// <list type="bullet">
    /// <item><description><see cref="AiAttachmentCategory.Image"/> → native image part (vision models only).</description></item>
    /// <item><description><see cref="AiAttachmentCategory.Text"/> → inlined into the prompt text (any model).</description></item>
    /// <item><description><see cref="AiAttachmentCategory.Unsupported"/> → throws with the supported categories.</description></item>
    /// </list>
    /// </para>
    /// <para>
    /// Allocation and lifetime contract: every factory keeps the caller's buffer (a <c>byte[]</c>, a
    /// <see cref="ReadOnlyMemory{T}"/> slice, a <see cref="MemoryStream"/> buffer or a text string) and never
    /// copies it, and a request keeps the caller's attachment list without copying it either. CoreAI reads that
    /// buffer again on every provider request of the turn (each tool-call roundtrip, the final summary request
    /// and every orchestrator retry), so keep the buffer and the list unchanged until the returned
    /// <see cref="Task"/> completes or the returned stream is fully enumerated or disposed. The object is
    /// immutable, so its resolved media type and category are computed once and cached.
    /// </para>
    /// </summary>
    public sealed class AiAttachment
    {
        private static readonly string[] ImageMediaTypes =
        {
            "image/png", "image/jpeg", "image/webp", "image/gif"
        };

        private static readonly string[] TextMediaTypes =
        {
            "application/json", "application/x-lua", "application/xml", "application/yaml",
            "application/x-yaml", "application/javascript", "application/toml", "application/sql"
        };

        /// <summary>Supported image media types (lower-case, normalized). Only these reach a vision model.</summary>
        public static readonly IReadOnlyList<string> SupportedImageMediaTypes = Array.AsReadOnly(ImageMediaTypes);

        /// <summary>
        /// Media types (beyond the whole <c>text/*</c> family) that are treated as text and inlined into the
        /// prompt. Extend the extension map in <see cref="InferMediaTypeFromFileName"/> in lock-step.
        /// </summary>
        public static readonly IReadOnlyList<string> SupportedTextMediaTypes = Array.AsReadOnly(TextMediaTypes);

        // WHY: A single text file inlined into the prompt is bounded so one oversized paste cannot blow the context window; images are not capped here because they ride the native image part, gated by the model.
        /// <summary>Maximum size (bytes, as given) of a single inlined text attachment.</summary>
        public const int MaxInlineTextBytes = 256 * 1024;

        /// <summary>Maximum combined size (bytes, as given) of ALL inlined text attachments on one request.</summary>
        public const int MaxTotalInlineTextBytes = 1024 * 1024;

        /// <summary>The one BOM-less, non-throwing UTF-8 codec every attachment path shares.</summary>
        internal static readonly UTF8Encoding Utf8 = new(false, false);

        private static readonly UnicodeEncoding Utf16Le = new(false, false, false);
        private static readonly UnicodeEncoding Utf16Be = new(true, false, false);
        private static readonly UTF32Encoding Utf32Le = new(false, false, false);
        private static readonly UTF32Encoding Utf32Be = new(true, false, false);

        private readonly ReadOnlyMemory<byte> _memory;
        private readonly bool _hasData;
        private readonly string _text;
        private byte[] _array;
        private string _resolvedMediaType;
        private int _category = -1;

        private AiAttachment(string fileName, string mediaType, byte[] data, Uri uri)
        {
            FileName = fileName ?? "";
            MediaType = mediaType ?? "";
            _array = data;
            _memory = data;
            _hasData = data != null;
            Uri = uri;
        }

        private AiAttachment(string fileName, string mediaType, ReadOnlyMemory<byte> data)
        {
            FileName = fileName ?? "";
            MediaType = mediaType ?? "";
            _memory = data;
            _hasData = true;
            if (MemoryMarshal.TryGetArray(data, out ArraySegment<byte> segment) && segment.Array != null &&
                segment.Offset == 0 && segment.Count == segment.Array.Length)
            {
                _array = segment.Array;
            }
        }

        private AiAttachment(string fileName, string mediaType, string text)
        {
            FileName = fileName ?? "";
            MediaType = mediaType ?? "";
            _text = text;
            _hasData = true;
        }

        /// <summary>Original file name (e.g. <c>hero.png</c>, <c>level.lua</c>). Optional but recommended:
        /// it labels the inlined text block and drives media-type inference when <see cref="MediaType"/> is empty.</summary>
        public string FileName { get; }

        /// <summary>IANA media type as given (e.g. <c>image/png</c>, <c>text/plain; charset=utf-16</c>). Empty =
        /// infer from <see cref="FileName"/> at compose time. <see cref="ResolvedMediaType"/> is the normalized form.</summary>
        public string MediaType { get; }

        /// <summary>
        /// Inline file bytes. Required for text attachments; either this or <see cref="Uri"/> for images.
        /// Returns the caller's own array when the attachment was built from one; an attachment built from a
        /// slice or a string materializes (and caches) an array only when this property is read — prefer
        /// <see cref="Memory"/>, which never copies.
        /// </summary>
        public byte[] Data
        {
            get
            {
                if (_array != null || !_hasData)
                {
                    return _array;
                }

                _array = _text != null ? Utf8.GetBytes(_text) : _memory.ToArray();
                return _array;
            }
        }

        /// <summary>
        /// Zero-copy view of the inline bytes (empty for a URI image). This is what CoreAI hands to the model
        /// client, so an attachment built from a slice or a pooled buffer is sent without a copy.
        /// </summary>
        public ReadOnlyMemory<byte> Memory => _text != null ? Data : _memory;

        /// <summary>Remote/asset URI for an image attachment. Ignored for text (text must be inline data).</summary>
        public Uri Uri { get; }

        /// <summary>True when the attachment carries inline bytes or text (as opposed to only a <see cref="Uri"/>).</summary>
        public bool HasInlineData => _hasData;

        /// <summary>The effective media type: <see cref="MediaType"/> when set (lower-case, without parameters),
        /// otherwise inferred from <see cref="FileName"/>. Empty when neither is available. Computed once.</summary>
        public string ResolvedMediaType =>
            _resolvedMediaType ??= string.IsNullOrWhiteSpace(MediaType)
                ? InferMediaTypeFromFileName(FileName)
                : NormalizeMediaType(MediaType);

        /// <summary>The routing category derived from <see cref="ResolvedMediaType"/>. Computed once.</summary>
        public AiAttachmentCategory Category
        {
            get
            {
                if (_category < 0)
                {
                    _category = (int)Classify(ResolvedMediaType);
                }

                return (AiAttachmentCategory)_category;
            }
        }

        /// <summary>Text given to <see cref="FromText"/>, inlined as-is (no encode/decode round trip); otherwise null.</summary>
        internal string InlineText => _text;

        /// <summary>Byte length of the inline payload, without materializing an array.</summary>
        internal int InlineByteCount => _text != null ? Utf8.GetByteCount(_text) : _memory.Length;

        /// <summary>
        /// Creates an image attachment from raw bytes; the array is referenced, not copied — keep it unchanged
        /// until the request's <see cref="Task"/> completes or its stream is fully enumerated or disposed.
        /// <paramref name="mediaType"/> must be one of <see cref="SupportedImageMediaTypes"/>; when it is empty the
        /// type is detected from the PNG/JPEG/GIF/WEBP signature, else from an image <paramref name="fileName"/>.
        /// </summary>
        /// <exception cref="ArgumentException">The data is empty, the media type is not an image type, or no image
        /// type can be detected. Text files go through <see cref="FromFile(string, byte[], string)"/> or
        /// <see cref="FromText"/>.</exception>
        public static AiAttachment Image(byte[] data, string mediaType = "", string fileName = "")
        {
            if (data == null || data.Length == 0)
            {
                throw new ArgumentException("Image attachment requires non-empty Data.", nameof(data));
            }

            return new AiAttachment(fileName, ResolveImageMediaType(data, mediaType, fileName), data, null);
        }

        /// <summary>
        /// Creates an image attachment over a slice of memory (an encoder's pooled buffer, a
        /// <see cref="MemoryStream"/> buffer, a region of a larger file) without copying it. CoreAI reads the memory
        /// again on every provider request of the turn, so keep it unchanged until the request's
        /// <see cref="Task"/> completes or its stream is fully enumerated or disposed. The media type is resolved as
        /// in <see cref="Image(byte[], string, string)"/>.
        /// </summary>
        public static AiAttachment Image(ReadOnlyMemory<byte> data, string mediaType = "", string fileName = "")
        {
            if (data.IsEmpty)
            {
                throw new ArgumentException("Image attachment requires non-empty data.", nameof(data));
            }

            return new AiAttachment(fileName, ResolveImageMediaType(data.Span, mediaType, fileName), data);
        }

        /// <summary>Creates an image attachment that references a URI (vision-capable models only). A non-empty
        /// <paramref name="mediaType"/> must be an image type; an empty one is inferred from
        /// <paramref name="fileName"/> at compose time.</summary>
        public static AiAttachment ImageUri(Uri uri, string mediaType = "", string fileName = "")
        {
            if (uri == null)
            {
                throw new ArgumentNullException(nameof(uri));
            }

            if (!string.IsNullOrWhiteSpace(mediaType))
            {
                RequireImageMediaType(mediaType);
            }

            return new AiAttachment(fileName, mediaType, null, uri);
        }

        /// <summary>
        /// Creates an attachment from a file name and its bytes, inferring the media type from the extension
        /// when <paramref name="mediaType"/> is empty. Works for both images and text-like files; the routing
        /// category is decided later from the resolved media type. The array is referenced, not copied.
        /// </summary>
        public static AiAttachment FromFile(string fileName, byte[] data, string mediaType = "")
        {
            if (data == null)
            {
                throw new ArgumentNullException(nameof(data));
            }

            return new AiAttachment(fileName, mediaType, data, null);
        }

        /// <summary>
        /// Reads a file from disk (image or text-like) into an attachment named after the file; the media type
        /// is inferred from the extension unless given. One array, the size of the file. Not for WebGL
        /// <c>StreamingAssets</c> (no file system there): download the bytes and call
        /// <see cref="FromFile(string, byte[], string)"/> instead.
        /// </summary>
        public static AiAttachment FromFile(string path, string mediaType = "")
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                throw new ArgumentException("A file path is required.", nameof(path));
            }

            return new AiAttachment(Path.GetFileName(path), mediaType, File.ReadAllBytes(path), null);
        }

        /// <summary>Asynchronous <see cref="FromFile(string, string)"/>, for large files read off the caller's thread.</summary>
        public static async Task<AiAttachment> FromFileAsync(
            string path,
            string mediaType = "",
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                throw new ArgumentException("A file path is required.", nameof(path));
            }

            // WHY: resuming on the caller's context keeps WebGL and Unity-API callers safe; the WebGL-unsafe ConfigureAwait(false) is banned by the async-primitives guard.
            byte[] data = await File.ReadAllBytesAsync(path, cancellationToken);
            return new AiAttachment(Path.GetFileName(path), mediaType, data, null);
        }

        /// <summary>
        /// Creates an attachment from the rest of <paramref name="stream"/> (from its current position; a position
        /// past the end yields an empty attachment). A <see cref="MemoryStream"/> whose buffer is publicly visible
        /// (<see cref="MemoryStream.TryGetBuffer"/> succeeds) is referenced without a copy — keep it unwritten until
        /// the request's <see cref="Task"/> completes or its stream is fully enumerated or disposed; any other
        /// seekable stream is read into one array of the exact size, a non-seekable one is copied once.
        /// </summary>
        public static AiAttachment FromStream(Stream stream, string mediaType = "", string fileName = "")
        {
            if (stream == null)
            {
                throw new ArgumentNullException(nameof(stream));
            }

            if (stream is MemoryStream memoryStream && memoryStream.TryGetBuffer(out ArraySegment<byte> buffer))
            {
                int start = (int)Math.Min(memoryStream.Position, buffer.Count);
                ReadOnlyMemory<byte> rest = new(buffer.Array, buffer.Offset + start, buffer.Count - start);
                memoryStream.Position = buffer.Count;
                return new AiAttachment(fileName, mediaType, rest);
            }

            if (stream.CanSeek)
            {
                long remaining = Math.Max(0L, stream.Length - stream.Position);
                byte[] data = new byte[checked((int)remaining)];
                int read = 0;
                while (read < data.Length)
                {
                    int n = stream.Read(data, read, data.Length - read);
                    if (n <= 0)
                    {
                        break;
                    }

                    read += n;
                }

                return read == data.Length
                    ? new AiAttachment(fileName, mediaType, data, null)
                    : new AiAttachment(fileName, mediaType, new ReadOnlyMemory<byte>(data, 0, read));
            }

            MemoryStream copy = new();
            stream.CopyTo(copy);
            copy.TryGetBuffer(out ArraySegment<byte> copied);
            return new AiAttachment(fileName, mediaType, new ReadOnlyMemory<byte>(copied.Array, 0, copied.Count));
        }

        /// <summary>
        /// Creates a text-like attachment straight from a string (a Lua script, JSON, a log, a
        /// <c>TextAsset.text</c>). The string is inlined as-is — no encode/decode round trip. The media type
        /// is <paramref name="mediaType"/>, else inferred from <paramref name="fileName"/>, else <c>text/plain</c>;
        /// an explicit non-text media type throws.
        /// </summary>
        public static AiAttachment FromText(string fileName, string text, string mediaType = "")
        {
            if (text == null)
            {
                throw new ArgumentNullException(nameof(text));
            }

            if (!string.IsNullOrWhiteSpace(mediaType))
            {
                if (Classify(mediaType) != AiAttachmentCategory.Text)
                {
                    throw new ArgumentException(
                        $"FromText needs a text media type; '{mediaType}' is not one.", nameof(mediaType));
                }
            }
            else if (Classify(InferMediaTypeFromFileName(fileName)) != AiAttachmentCategory.Text)
            {
                mediaType = "text/plain";
            }

            return new AiAttachment(fileName, mediaType, text);
        }

        /// <summary>Creates an attachment from base64 text (no <c>data:</c> prefix), decoded once into an exact-size array.</summary>
        /// <exception cref="FormatException">The text is not valid base64.</exception>
        public static AiAttachment FromBase64(string base64, string mediaType, string fileName = "")
        {
            if (base64 == null)
            {
                throw new ArgumentNullException(nameof(base64));
            }

            if (!TryDecodeBase64(base64.AsSpan(), int.MaxValue, out ReadOnlyMemory<byte> bytes))
            {
                throw new FormatException("The attachment payload is not valid base64.");
            }

            return new AiAttachment(fileName, mediaType, bytes);
        }

        /// <summary>
        /// Creates an attachment from a <c>data:&lt;media-type&gt;[;parameters];base64,&lt;payload&gt;</c> URL (what
        /// browsers, canvases and many tools produce; RFC 2397). The payload is decoded once, straight from the
        /// string; parameters such as <c>charset</c> are kept on <see cref="MediaType"/>.
        /// </summary>
        /// <exception cref="FormatException">The text is not a base64 data URL.</exception>
        public static AiAttachment FromDataUrl(string dataUrl, string fileName = "")
        {
            if (!TryFromDataUrl(dataUrl, out AiAttachment attachment, fileName))
            {
                throw new FormatException(
                    "Expected a data URL of the form data:<media-type>[;parameters];base64,<payload>.");
            }

            return attachment;
        }

        /// <summary>Non-throwing <see cref="FromDataUrl"/>.</summary>
        public static bool TryFromDataUrl(string dataUrl, out AiAttachment attachment, string fileName = "")
        {
            attachment = null;
            if (!TryDecodeDataUrl(dataUrl, int.MaxValue, false, out ReadOnlyMemory<byte> bytes, out string mediaType))
            {
                return false;
            }

            attachment = new AiAttachment(fileName, mediaType, bytes);
            return true;
        }

        /// <summary>
        /// Classifies a media type (parameters and case ignored) into a routing <see cref="AiAttachmentCategory"/>.
        /// </summary>
        public static AiAttachmentCategory Classify(string mediaType)
        {
            if (string.IsNullOrWhiteSpace(mediaType))
            {
                return AiAttachmentCategory.Unsupported;
            }

            string normalized = NormalizeMediaType(mediaType);
            if (IndexOf(ImageMediaTypes, normalized) >= 0)
            {
                return AiAttachmentCategory.Image;
            }

            return IsNormalizedTextMediaType(normalized) ? AiAttachmentCategory.Text : AiAttachmentCategory.Unsupported;
        }

        /// <summary>True when <paramref name="mediaType"/> is one of the supported image media types.</summary>
        public static bool IsSupportedImageMediaType(string mediaType)
        {
            return IndexOf(ImageMediaTypes, NormalizeMediaType(mediaType)) >= 0;
        }

        /// <summary>True when <paramref name="mediaType"/> is treated as text (inlined into the prompt).</summary>
        public static bool IsTextMediaType(string mediaType)
        {
            return IsNormalizedTextMediaType(NormalizeMediaType(mediaType));
        }

        /// <summary>
        /// Drops parameters (<c>text/plain; charset=utf-8</c> becomes <c>text/plain</c>), lower-cases the media
        /// type and folds the common <c>image/jpg</c> alias to <c>image/jpeg</c>, so comparisons and the emitted
        /// <c>data:</c> URL use canonical spelling. An already canonical value is returned as the same instance.
        /// The <c>charset</c> parameter is not lost: text decoding reads it from <see cref="MediaType"/>.
        /// </summary>
        public static string NormalizeMediaType(string mediaType)
        {
            if (string.IsNullOrWhiteSpace(mediaType))
            {
                return "";
            }

            int parameters = mediaType.IndexOf(';');
            string head = parameters >= 0 ? mediaType.Substring(0, parameters) : mediaType;
            if (string.IsNullOrWhiteSpace(head))
            {
                return "";
            }

            string trimmed = IsCanonical(head) ? head : head.Trim().ToLowerInvariant();
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
            int bytes = _hasData ? InlineByteCount : 0;
            return bytes > 0
                ? $"[attachment: {name} {type} {FormatSize(bytes)}]"
                : $"[attachment: {name} {type}]";
        }

        /// <summary>
        /// The codec a byte-based text attachment is decoded with, and the length of the byte-order mark to skip:
        /// a UTF-8/UTF-16LE/UTF-16BE/UTF-32LE/UTF-32BE BOM wins, then the <c>charset</c> parameter of
        /// <see cref="MediaType"/>, then UTF-8. Deterministic from the bytes and the media type, so the length pass
        /// and the write pass of the composer always agree.
        /// </summary>
        /// <exception cref="ArgumentException">The <c>charset</c> names an encoding this runtime does not have.</exception>
        internal Encoding ResolveTextEncoding(ReadOnlySpan<byte> data, out int byteOrderMarkLength)
        {
            byteOrderMarkLength = 0;
            if (data.Length >= 3 && data[0] == 0xEF && data[1] == 0xBB && data[2] == 0xBF)
            {
                byteOrderMarkLength = 3;
                return Utf8;
            }

            if (data.Length >= 4 && data[0] == 0xFF && data[1] == 0xFE && data[2] == 0 && data[3] == 0)
            {
                byteOrderMarkLength = 4;
                return Utf32Le;
            }

            if (data.Length >= 4 && data[0] == 0 && data[1] == 0 && data[2] == 0xFE && data[3] == 0xFF)
            {
                byteOrderMarkLength = 4;
                return Utf32Be;
            }

            if (data.Length >= 2 && data[0] == 0xFF && data[1] == 0xFE)
            {
                byteOrderMarkLength = 2;
                return Utf16Le;
            }

            if (data.Length >= 2 && data[0] == 0xFE && data[1] == 0xFF)
            {
                byteOrderMarkLength = 2;
                return Utf16Be;
            }

            return EncodingForCharset(data, ReadCharset(MediaType));
        }

        /// <summary>
        /// Decodes a <c>data:&lt;type&gt;[;parameters];base64,&lt;payload&gt;</c> URL straight from the string (no
        /// substring of the payload). The <c>;base64</c> marker is the one right before the first comma (RFC 2397);
        /// anything between the type and it is parameters, kept on the returned media type for a non-image.
        /// <paramref name="imagesOnly"/> accepts only <see cref="SupportedImageMediaTypes"/> and returns their
        /// canonical instances, so the image path allocates nothing but the decoded bytes. A payload whose decoded
        /// size exceeds <paramref name="maxDecodedBytes"/> is refused before decoding.
        /// </summary>
        internal static bool TryDecodeDataUrl(
            string dataUrl,
            int maxDecodedBytes,
            bool imagesOnly,
            out ReadOnlyMemory<byte> bytes,
            out string mediaType)
        {
            const string Base64Marker = ";base64";
            bytes = default;
            mediaType = null;
            if (string.IsNullOrEmpty(dataUrl) || !dataUrl.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            int comma = dataUrl.IndexOf(',');
            int marker = comma - Base64Marker.Length;
            if (marker <= 5 ||
                !dataUrl.AsSpan(marker, Base64Marker.Length).Equals(Base64Marker.AsSpan(),
                    StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            ReadOnlySpan<char> header = dataUrl.AsSpan(5, marker - 5);
            int typeEnd = header.IndexOf(';');
            ReadOnlySpan<char> type = (typeEnd >= 0 ? header.Slice(0, typeEnd) : header).Trim();
            if (type.IsEmpty)
            {
                return false;
            }

            string canonical = null;
            for (int i = 0; i < ImageMediaTypes.Length; i++)
            {
                if (type.Equals(ImageMediaTypes[i].AsSpan(), StringComparison.OrdinalIgnoreCase))
                {
                    canonical = ImageMediaTypes[i];
                    break;
                }
            }

            if (canonical == null)
            {
                if (imagesOnly)
                {
                    return false;
                }

                canonical = typeEnd >= 0 ? header.ToString() : NormalizeMediaType(type.ToString());
            }

            if (!TryDecodeBase64(dataUrl.AsSpan(comma + 1), maxDecodedBytes, out bytes) || bytes.IsEmpty)
            {
                bytes = default;
                return false;
            }

            mediaType = canonical;
            return true;
        }

        /// <summary>
        /// Decodes base64 into one array of the exact decoded size (whitespace-free input) or a slice of a
        /// minimally larger one (input with whitespace, which only lengthens the text and so never makes the
        /// estimate too small). Input whose decoded size would exceed <paramref name="maxDecodedBytes"/> is
        /// refused without being decoded.
        /// </summary>
        internal static bool TryDecodeBase64(ReadOnlySpan<char> base64, int maxDecodedBytes, out ReadOnlyMemory<byte> bytes)
        {
            bytes = default;
            int length = base64.Length;
            if (length == 0)
            {
                bytes = Array.Empty<byte>();
                return true;
            }

            int padding = 0;
            if (base64[length - 1] == '=')
            {
                padding++;
                if (length > 1 && base64[length - 2] == '=')
                {
                    padding++;
                }
            }

            long estimate = length % 4 == 0 ? length / 4 * 3L - padding : (length + 3L) / 4 * 3;
            if (estimate > maxDecodedBytes && SignificantUpperBound(base64) - 2 > maxDecodedBytes)
            {
                return false;
            }

            byte[] buffer = new byte[estimate];
            if (!Convert.TryFromBase64Chars(base64, buffer, out int written) || written > maxDecodedBytes)
            {
                return false;
            }

            bytes = written == buffer.Length ? buffer : new ReadOnlyMemory<byte>(buffer, 0, written);
            return true;
        }

        /// <summary>Decoded-size upper bound counting only non-whitespace characters, so line-wrapped base64 that
        /// really fits the cap is not refused; the true size is at most 2 bytes (padding) below it.</summary>
        private static long SignificantUpperBound(ReadOnlySpan<char> base64)
        {
            int significant = 0;
            for (int i = 0; i < base64.Length; i++)
            {
                if (!char.IsWhiteSpace(base64[i]))
                {
                    significant++;
                }
            }

            return (significant + 3L) / 4 * 3;
        }

        /// <summary>The media type an <see cref="Image(byte[], string, string)"/> call resolves to, or a clear throw.</summary>
        private static string ResolveImageMediaType(ReadOnlySpan<byte> data, string mediaType, string fileName)
        {
            if (!string.IsNullOrWhiteSpace(mediaType))
            {
                RequireImageMediaType(mediaType);
                return mediaType;
            }

            string sniffed = SniffImageMediaType(data);
            if (sniffed != null)
            {
                return sniffed;
            }

            string inferred = InferMediaTypeFromFileName(fileName);
            if (IndexOf(ImageMediaTypes, inferred) >= 0)
            {
                return inferred;
            }

            throw new ArgumentException(
                "AiAttachment.Image could not detect the image type: the bytes are not PNG, JPEG, GIF or WEBP" +
                (string.IsNullOrWhiteSpace(fileName) ? "" : $" and '{fileName}' has no image extension") +
                ". Pass the media type (" + string.Join(", ", ImageMediaTypes) + ") or use AiAttachment.FromFile " +
                "for other files.", nameof(mediaType));
        }

        private static void RequireImageMediaType(string mediaType)
        {
            if (Classify(mediaType) != AiAttachmentCategory.Image)
            {
                throw new ArgumentException(
                    $"AiAttachment.Image needs an image media type ({string.Join(", ", ImageMediaTypes)}); " +
                    $"'{mediaType}' is not one. Use AiAttachment.FromFile or AiAttachment.FromText for text files.",
                    nameof(mediaType));
            }
        }

        /// <summary>The canonical image type from the file signature, or null when it is none of the supported ones.</summary>
        internal static string SniffImageMediaType(ReadOnlySpan<byte> data)
        {
            if (data.Length >= 8 && data[0] == 0x89 && data[1] == 0x50 && data[2] == 0x4E && data[3] == 0x47 &&
                data[4] == 0x0D && data[5] == 0x0A && data[6] == 0x1A && data[7] == 0x0A)
            {
                return ImageMediaTypes[0];
            }

            if (data.Length >= 3 && data[0] == 0xFF && data[1] == 0xD8 && data[2] == 0xFF)
            {
                return ImageMediaTypes[1];
            }

            if (data.Length >= 12 && data[0] == (byte)'R' && data[1] == (byte)'I' && data[2] == (byte)'F' &&
                data[3] == (byte)'F' && data[8] == (byte)'W' && data[9] == (byte)'E' && data[10] == (byte)'B' &&
                data[11] == (byte)'P')
            {
                return ImageMediaTypes[2];
            }

            if (data.Length >= 6 && data[0] == (byte)'G' && data[1] == (byte)'I' && data[2] == (byte)'F' &&
                data[3] == (byte)'8' && (data[4] == (byte)'7' || data[4] == (byte)'9') && data[5] == (byte)'a')
            {
                return ImageMediaTypes[3];
            }

            return null;
        }

        /// <summary>The value of the <c>charset</c> parameter of a media type, unquoted and trimmed; empty when absent.</summary>
        private static ReadOnlySpan<char> ReadCharset(string mediaType)
        {
            if (string.IsNullOrEmpty(mediaType))
            {
                return ReadOnlySpan<char>.Empty;
            }

            ReadOnlySpan<char> rest = mediaType.AsSpan();
            int semicolon = rest.IndexOf(';');
            while (semicolon >= 0)
            {
                rest = rest.Slice(semicolon + 1);
                semicolon = rest.IndexOf(';');
                ReadOnlySpan<char> parameter = (semicolon >= 0 ? rest.Slice(0, semicolon) : rest).Trim();
                int equals = parameter.IndexOf('=');
                if (equals > 0 && parameter.Slice(0, equals).Trim().Equals("charset".AsSpan(),
                        StringComparison.OrdinalIgnoreCase))
                {
                    return parameter.Slice(equals + 1).Trim().Trim('"').Trim();
                }
            }

            return ReadOnlySpan<char>.Empty;
        }

        private Encoding EncodingForCharset(ReadOnlySpan<byte> data, ReadOnlySpan<char> charset)
        {
            if (charset.IsEmpty || IsAny(charset, "utf-8", "utf8", "us-ascii", "ascii"))
            {
                return Utf8;
            }

            if (IsAny(charset, "utf-16le", "unicode", "ucs-2"))
            {
                return Utf16Le;
            }

            if (IsAny(charset, "utf-16be", "unicodefffe"))
            {
                return Utf16Be;
            }

            if (IsAny(charset, "utf-16", "utf16"))
            {
                return LooksBigEndian(data, 2) ? Utf16Be : Utf16Le;
            }

            if (IsAny(charset, "utf-32le", "utf-32", "utf32"))
            {
                return IsAny(charset, "utf-32le") || !LooksBigEndian(data, 4) ? Utf32Le : Utf32Be;
            }

            if (IsAny(charset, "utf-32be"))
            {
                return Utf32Be;
            }

            try
            {
                return Encoding.GetEncoding(charset.ToString());
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
            {
                throw new ArgumentException(
                    $"Text attachment '{DescribeName()}' declares charset '{charset.ToString()}', which this runtime " +
                    "cannot decode. Convert the file to UTF-8 or declare a supported charset (utf-8, utf-16, utf-32, " +
                    "iso-8859-1).");
            }
        }

        /// <summary>
        /// For a BOM-less UTF-16/32 payload declared without an endianness: big-endian when the zero high-order
        /// bytes of ASCII-range characters sit first in each unit, which is how ordinary text reveals its byte order.
        /// </summary>
        private static bool LooksBigEndian(ReadOnlySpan<byte> data, int unit)
        {
            int leading = 0;
            int trailing = 0;
            int limit = Math.Min(data.Length - data.Length % unit, 4096);
            for (int i = 0; i < limit; i += unit)
            {
                if (data[i] == 0 && data[i + unit - 1] != 0)
                {
                    leading++;
                }
                else if (data[i] != 0 && data[i + unit - 1] == 0)
                {
                    trailing++;
                }
            }

            return leading > trailing;
        }

        private static bool IsAny(ReadOnlySpan<char> value, string a, string b = null, string c = null,
            string d = null)
        {
            return value.Equals(a.AsSpan(), StringComparison.OrdinalIgnoreCase) ||
                   (b != null && value.Equals(b.AsSpan(), StringComparison.OrdinalIgnoreCase)) ||
                   (c != null && value.Equals(c.AsSpan(), StringComparison.OrdinalIgnoreCase)) ||
                   (d != null && value.Equals(d.AsSpan(), StringComparison.OrdinalIgnoreCase));
        }

        private string DescribeName()
        {
            return string.IsNullOrWhiteSpace(FileName) ? "(unnamed)" : FileName.Trim();
        }

        private static bool IsNormalizedTextMediaType(string normalized)
        {
            return normalized.StartsWith("text/", StringComparison.Ordinal) || IndexOf(TextMediaTypes, normalized) >= 0;
        }

        private static int IndexOf(string[] values, string value)
        {
            for (int i = 0; i < values.Length; i++)
            {
                if (string.Equals(values[i], value, StringComparison.Ordinal))
                {
                    return i;
                }
            }

            return -1;
        }

        private static bool IsCanonical(string mediaType)
        {
            if (char.IsWhiteSpace(mediaType[0]) || char.IsWhiteSpace(mediaType[mediaType.Length - 1]))
            {
                return false;
            }

            for (int i = 0; i < mediaType.Length; i++)
            {
                char c = mediaType[i];
                if ((c >= 'A' && c <= 'Z') || c > 127)
                {
                    return false;
                }
            }

            return true;
        }

        private static string FormatSize(int bytes)
        {
            if (bytes < 1024)
            {
                return bytes + " B";
            }

            if (bytes < 1024 * 1024)
            {
                return bytes / 1024 + " KB";
            }

            return bytes / (1024 * 1024) + " MB";
        }
    }
}
