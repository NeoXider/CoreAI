using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MEAI = Microsoft.Extensions.AI;

namespace CoreAI.Ai
{
    /// <summary>
    /// A tool result that shows the model one or more images: <see cref="Text"/> is what the tool message says
    /// (a small JSON or plain summary), <see cref="Images"/> travel out-of-band and are delivered to the model
    /// as image parts of a follow-up user message before the next request (tool messages cannot carry images
    /// on OpenAI-compatible APIs). The follow-up names the tool (<c>Image returned by tool 'render_preview'.</c>);
    /// earlier images of other calls stay in the turn so the model can compare them (the latest 8 such messages
    /// are kept). Camera tools (<c>camera_capture</c>, <c>screenshot</c>, <c>capture_camera</c>) are the exception:
    /// their frame replaces the previous frame.
    /// <para>
    /// The same object is what <c>CoreAi.OnToolExecuted</c> (and any <c>IToolExecutionNotifier</c>) receives
    /// as the result: read <see cref="Images"/> there; <see cref="ToString"/> is the text only.
    /// </para>
    /// <para>
    /// This is the lean path for a camera, a render, a chart or any tool that produces pictures: no base64,
    /// no data URL inside JSON, no parse or decode on the way — the encoder's bytes reach the provider
    /// serializer untouched. Return it from the tool body and create the function with
    /// <c>MarshalResult = LlmToolImageResult.PreserveResult</c> so MEAI does not turn it into JSON:
    /// </para>
    /// <code>
    /// AIFunctionFactory.Create((Func&lt;Task&lt;object&gt;&gt;)RenderAsync, new AIFunctionFactoryOptions
    /// {
    ///     Name = "render_preview",
    ///     MarshalResult = LlmToolImageResult.PreserveResult
    /// });
    /// // inside RenderAsync:
    /// return new LlmToolImageResult("{\"ok\":true,\"imageAttached\":true}", AiAttachment.Image(png, "image/png"));
    /// </code>
    /// <para>
    /// Fail-closed: only images a vision model can receive are delivered (supported image type, inline bytes of
    /// 1 byte to <see cref="MaxImageBytes"/>, or a URI); any other image is dropped and the text says so. The
    /// images are ignored by JSON serializers, so the bytes can never leak into a tool message as text.
    /// </para>
    /// </summary>
    public sealed class LlmToolImageResult
    {
        /// <summary>The largest inline image delivered from a tool result (4 MB).</summary>
        public const int MaxImageBytes = 4 * 1024 * 1024;

        private readonly string _compatibilityText;

        /// <summary>Creates a result with one image.</summary>
        public LlmToolImageResult(string text, AiAttachment image)
            : this(text, image == null ? Array.Empty<AiAttachment>() : new[] { image })
        {
        }

        /// <summary>Creates a result with any number of images (the list is kept, not copied).</summary>
        public LlmToolImageResult(string text, IReadOnlyList<AiAttachment> images)
        {
            Text = text ?? "";
            Images = images ?? Array.Empty<AiAttachment>();
        }

        private LlmToolImageResult(string text, IReadOnlyList<AiAttachment> images, string compatibilityText)
            : this(text, images)
        {
            _compatibilityText = compatibilityText;
        }

        /// <summary>The model-facing tool message text. Never contains the image bytes.</summary>
        public string Text { get; }

        /// <summary>Images delivered to the model as image parts. Ignored by JSON serializers on purpose.</summary>
        [Newtonsoft.Json.JsonIgnore]
        [System.Text.Json.Serialization.JsonIgnore]
        public IReadOnlyList<AiAttachment> Images { get; }

        /// <summary>True for a legacy base64 camera result that <c>ToolExecutionPolicy</c> converted.</summary>
        internal bool IsLegacyCameraResult => _compatibilityText != null;

        /// <summary>
        /// <see cref="Text"/>; for a legacy base64 camera result that CoreAI converted, the tool's original text,
        /// so code that read the raw result before the lift sees exactly what it saw before.
        /// </summary>
        public override string ToString()
        {
            return _compatibilityText ?? Text;
        }

        /// <summary>
        /// An <c>AIFunctionFactoryOptions.MarshalResult</c> that keeps an <see cref="LlmToolImageResult"/> as-is
        /// instead of serializing it to JSON; any other value is serialized to a <see cref="JsonElement"/> exactly
        /// like MEAI's default marshaller, so a function that never returns images behaves as before.
        /// <see cref="DelegateLlmTool"/> already uses it.
        /// </summary>
        public static ValueTask<object> PreserveResult(object result, Type resultType, CancellationToken cancellationToken)
        {
            if (result == null || result is LlmToolImageResult)
            {
                return new ValueTask<object>(result);
            }

            JsonSerializerOptions options = MEAI.AIJsonUtilities.DefaultOptions;
            return new ValueTask<object>(
                JsonSerializer.SerializeToElement(result, options.GetTypeInfo(resultType ?? result.GetType())));
        }

        /// <summary>
        /// A legacy <c>dataUrl</c> camera result converted to the typed form: <paramref name="liftedText"/> is the
        /// summary the model reads, <paramref name="originalText"/> is what <see cref="ToString"/> keeps returning.
        /// </summary>
        internal static LlmToolImageResult FromLegacyCameraResult(string originalText, string liftedText, AiAttachment image)
        {
            return new LlmToolImageResult(liftedText, new[] { image }, originalText);
        }

        /// <summary>The same images (and compatibility text) with a different model-facing text, e.g. after a size cut.</summary>
        internal LlmToolImageResult WithText(string text)
        {
            return new LlmToolImageResult(text, Images, _compatibilityText);
        }

        /// <summary>
        /// This result with only the images a vision model can receive. Returns the same instance when every
        /// image qualifies; otherwise a copy whose text names what was dropped (never the bytes).
        /// </summary>
        internal LlmToolImageResult KeepDeliverable()
        {
            int keep = 0;
            for (int i = 0; i < Images.Count; i++)
            {
                if (IsDeliverable(Images[i]))
                {
                    keep++;
                }
            }

            if (keep == Images.Count)
            {
                return this;
            }

            List<AiAttachment> kept = new(keep);
            StringBuilder note = new(Text);
            note.Append("\n[image not attached: ");
            bool first = true;
            for (int i = 0; i < Images.Count; i++)
            {
                AiAttachment image = Images[i];
                if (IsDeliverable(image))
                {
                    kept.Add(image);
                    continue;
                }

                if (!first)
                {
                    note.Append("; ");
                }

                first = false;
                note.Append(DescribeRejection(image));
            }

            note.Append(']');
            return new LlmToolImageResult(note.ToString(), kept, _compatibilityText);
        }

        private static bool IsDeliverable(AiAttachment image)
        {
            if (image == null || image.Category != AiAttachmentCategory.Image)
            {
                return false;
            }

            if (image.HasInlineData)
            {
                int length = image.Memory.Length;
                return length > 0 && length <= MaxImageBytes;
            }

            return image.Uri != null;
        }

        private static string DescribeRejection(AiAttachment image)
        {
            if (image == null)
            {
                return "null image";
            }

            if (image.Category != AiAttachmentCategory.Image)
            {
                return $"'{image.ResolvedMediaType}' is not a supported image type";
            }

            return image.HasInlineData
                ? $"{image.Memory.Length} bytes is outside 1..{MaxImageBytes}"
                : "no image data";
        }
    }
}
