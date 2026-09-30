using System;
using CoreAI.Ai;
using CoreAI.Vision;
using UnityEngine;

namespace CoreAI
{
    /// <summary>
    /// Turns Unity objects into <see cref="AiAttachment"/>s for <c>CoreAi.AskAsync</c> / <c>StreamAsync</c> /
    /// <c>SmartAskAsync</c> and <see cref="AiTaskRequest.Attachments"/>. Everything else (files, bytes, base64,
    /// data URLs, text) is engine-free on <see cref="AiAttachment"/> itself.
    /// <code>
    /// string a = await CoreAi.AskAsync("What is on screen?", Camera.main.CaptureAiAttachment());
    /// string b = await CoreAi.AskAsync("Name this item", itemIcon.ToAiAttachment());          // Sprite
    /// string c = await CoreAi.AskAsync("Compare", new[] { before.ToAiAttachment(), after.ToAiAttachment() });
    /// string d = await CoreAi.AskAsync("Review", levelScript.ToAiAttachment("level.lua"));   // TextAsset
    /// </code>
    /// <para>
    /// Must run on the Unity main thread. Allocations are what the engine forces — one encoded
    /// <c>byte[]</c> per image, wrapped by the attachment without a copy (CoreAI reads it on every provider request
    /// of the turn, so do not reuse it before the request's task completes): a readable, uncompressed
    /// <see cref="Texture2D"/> at its own size is encoded directly; anything else (a GPU-only or compressed
    /// texture, a sprite region, a render texture, a camera, a downscale) goes through a pooled
    /// <see cref="RenderTexture.GetTemporary(int, int, int)"/> and one cached readback texture that is only
    /// reinitialized when the size or format changes. A readback above <see cref="MaxCachedReadbackPixels"/> is
    /// released right after the capture, so one huge capture never stays pinned in memory.
    /// </para>
    /// <para>
    /// Textures and render textures are downscaled to a long edge of <see cref="DefaultMaxSide"/> by default (what
    /// vision models use anyway); pass <c>maxSide: 0</c> for the native size. Cameras default to 512.
    /// </para>
    /// </summary>
    public static class AiAttachmentUnityExtensions
    {
        /// <summary>Default JPEG quality (1..100) for every helper here.</summary>
        public const int DefaultJpegQuality = 75;

        /// <summary>Default long edge (pixels) for texture, sprite and render-texture attachments; 0 = native size.</summary>
        public const int DefaultMaxSide = 1024;

        /// <summary>The largest readback (4 megapixels) that stays cached between captures.</summary>
        public const int MaxCachedReadbackPixels = 4 * 1024 * 1024;

        private static Texture2D _readback;

        /// <summary>
        /// Encodes a texture as an image attachment. <paramref name="maxSide"/> &gt; 0 downscales so the long
        /// edge fits (default <see cref="DefaultMaxSide"/>; vision models rarely use more); 0 keeps the native size,
        /// which for a 4096² texture means a 64 MB GPU readback. Works for GPU-only and compressed textures too.
        /// </summary>
        public static AiAttachment ToAiAttachment(
            this Texture2D texture,
            int maxSide = DefaultMaxSide,
            CaptureImageFormat format = CaptureImageFormat.Jpeg,
            int jpegQuality = DefaultJpegQuality,
            string fileName = "")
        {
            if (texture == null)
            {
                throw new ArgumentNullException(nameof(texture));
            }

            Fit(texture.width, texture.height, maxSide, out int width, out int height);
            if (width == texture.width && height == texture.height && texture.isReadable &&
                IsDirectlyEncodable(texture.format))
            {
                return Wrap(Encode(texture, format, jpegQuality), format, fileName);
            }

            return Wrap(BlitAndEncode(texture, null, width, height, format, jpegQuality), format, fileName);
        }

        /// <summary>
        /// Encodes the sprite's own region (not the whole atlas) as an image attachment; PNG by default so
        /// transparency survives. <paramref name="maxSide"/> as in <see cref="ToAiAttachment(Texture2D, int, CaptureImageFormat, int, string)"/>.
        /// </summary>
        public static AiAttachment ToAiAttachment(
            this Sprite sprite,
            int maxSide = DefaultMaxSide,
            CaptureImageFormat format = CaptureImageFormat.Png,
            int jpegQuality = DefaultJpegQuality,
            string fileName = "")
        {
            if (sprite == null)
            {
                throw new ArgumentNullException(nameof(sprite));
            }

            Texture2D texture = sprite.texture;
            Rect region;
            try
            {
                region = sprite.textureRect;
            }
            catch (UnityException)
            {
                // WHY: a tightly packed atlas sprite has no rectangular region; send the whole texture.
                region = new Rect(0, 0, texture.width, texture.height);
            }

            Fit(Mathf.RoundToInt(region.width), Mathf.RoundToInt(region.height), maxSide,
                out int width, out int height);
            return Wrap(BlitAndEncode(texture, region, width, height, format, jpegQuality), format,
                string.IsNullOrEmpty(fileName) ? sprite.name : fileName);
        }

        /// <summary>
        /// Encodes the current content of a render texture as an image attachment. <paramref name="maxSide"/> as in
        /// <see cref="ToAiAttachment(Texture2D, int, CaptureImageFormat, int, string)"/> (default
        /// <see cref="DefaultMaxSide"/>, 0 = native size).
        /// </summary>
        public static AiAttachment ToAiAttachment(
            this RenderTexture renderTexture,
            int maxSide = DefaultMaxSide,
            CaptureImageFormat format = CaptureImageFormat.Jpeg,
            int jpegQuality = DefaultJpegQuality,
            string fileName = "")
        {
            if (renderTexture == null)
            {
                throw new ArgumentNullException(nameof(renderTexture));
            }

            Fit(renderTexture.width, renderTexture.height, maxSide, out int width, out int height);
            byte[] bytes = width == renderTexture.width && height == renderTexture.height
                ? ReadBackAndEncode(renderTexture, width, height, format, jpegQuality, format == CaptureImageFormat.Png)
                : BlitAndEncode(renderTexture, null, width, height, format, jpegQuality);
            return Wrap(bytes, format, fileName);
        }

        /// <summary>
        /// Renders <paramref name="camera"/> offscreen (the world as that camera sees it; screen-space overlay UI
        /// is not part of a camera render) and returns the frame as an image attachment. The long edge is
        /// <paramref name="maxSide"/> (default 512; 0 = the camera's pixel size), aspect ratio kept. The camera's
        /// target texture and the active render texture are restored.
        /// </summary>
        public static AiAttachment CaptureAiAttachment(
            this Camera camera,
            int maxSide = 512,
            CaptureImageFormat format = CaptureImageFormat.Jpeg,
            int jpegQuality = DefaultJpegQuality,
            string fileName = "")
        {
            if (camera == null)
            {
                throw new ArgumentNullException(nameof(camera));
            }

            int pixelWidth = camera.pixelWidth > 0 ? camera.pixelWidth : 1600;
            int pixelHeight = camera.pixelHeight > 0 ? camera.pixelHeight : 900;
            Fit(pixelWidth, pixelHeight, maxSide, out int width, out int height);
            return Wrap(CaptureCamera(camera, width, height, format, jpegQuality), format,
                string.IsNullOrEmpty(fileName) ? camera.name : fileName);
        }

        /// <summary>
        /// A text attachment from a <see cref="TextAsset"/> (Lua, JSON, Markdown, ...), inlined into the prompt for
        /// every model. <paramref name="fileName"/> (default: the asset name) drives the media type; Unity strips
        /// the extension from asset names, so pass e.g. <c>"enemy.lua"</c> to label it precisely.
        /// </summary>
        public static AiAttachment ToAiAttachment(this TextAsset textAsset, string fileName = "", string mediaType = "")
        {
            if (textAsset == null)
            {
                throw new ArgumentNullException(nameof(textAsset));
            }

            return AiAttachment.FromText(string.IsNullOrEmpty(fileName) ? textAsset.name : fileName, textAsset.text,
                mediaType);
        }

        /// <summary>
        /// Renders <paramref name="camera"/> into a pooled render texture of exactly
        /// <paramref name="width"/>×<paramref name="height"/> and returns the encoded frame (RGB, no alpha).
        /// Shared by the camera tools so every capture reuses the same pooled targets.
        /// </summary>
        internal static byte[] CaptureCamera(Camera camera, int width, int height, CaptureImageFormat format,
            int jpegQuality)
        {
            RenderTexture target = RenderTexture.GetTemporary(width, height, 24);
            RenderTexture previousTarget = camera.targetTexture;
            RenderTexture previousActive = RenderTexture.active;
            try
            {
                if (camera.clearFlags != CameraClearFlags.Skybox && camera.clearFlags != CameraClearFlags.SolidColor)
                {
                    // WHY: a pooled target keeps whatever the last user drew into it, and a camera that clears only depth or nothing would composite its frame over that stale image.
                    RenderTexture.active = target;
                    GL.Clear(true, true, Color.clear);
                }

                camera.targetTexture = target;
                camera.Render();
                return ReadBackAndEncode(target, width, height, format, jpegQuality, false);
            }
            finally
            {
                camera.targetTexture = previousTarget;
                RenderTexture.active = previousActive;
                RenderTexture.ReleaseTemporary(target);
            }
        }

        private static byte[] BlitAndEncode(Texture source, Rect? region, int width, int height,
            CaptureImageFormat format, int jpegQuality)
        {
            RenderTexture target = RenderTexture.GetTemporary(width, height, 0, RenderTextureFormat.ARGB32,
                RenderTextureReadWrite.sRGB);
            RenderTexture previousActive = RenderTexture.active;
            try
            {
                if (region.HasValue)
                {
                    Rect r = region.Value;
                    Graphics.Blit(source, target,
                        new Vector2(r.width / source.width, r.height / source.height),
                        new Vector2(r.x / source.width, r.y / source.height));
                }
                else
                {
                    Graphics.Blit(source, target);
                }

                return ReadBackAndEncode(target, width, height, format, jpegQuality, format == CaptureImageFormat.Png);
            }
            finally
            {
                RenderTexture.active = previousActive;
                RenderTexture.ReleaseTemporary(target);
            }
        }

        private static byte[] ReadBackAndEncode(RenderTexture source, int width, int height,
            CaptureImageFormat format, int jpegQuality, bool keepAlpha)
        {
            Texture2D readback = GetReadback(width, height, keepAlpha ? TextureFormat.RGBA32 : TextureFormat.RGB24);
            RenderTexture previousActive = RenderTexture.active;
            try
            {
                RenderTexture.active = source;
                readback.ReadPixels(new Rect(0, 0, width, height), 0, 0, false);
                readback.Apply(false, false);
                return Encode(readback, format, jpegQuality);
            }
            finally
            {
                RenderTexture.active = previousActive;
                if ((long)width * height > MaxCachedReadbackPixels)
                {
                    ReleaseReadback();
                }
            }
        }

        /// <summary>Whether a readback texture is currently cached (test hook for the release-above-4-megapixels rule).</summary>
        internal static bool HasCachedReadback => _readback != null;

        private static Texture2D GetReadback(int width, int height, TextureFormat textureFormat)
        {
            if (_readback == null)
            {
                _readback = new Texture2D(width, height, textureFormat, false)
                {
                    name = "CoreAI attachment readback",
                    hideFlags = HideFlags.HideAndDontSave
                };
                Application.quitting += ReleaseReadback;
#if UNITY_EDITOR
                UnityEditor.AssemblyReloadEvents.beforeAssemblyReload += ReleaseReadback;
#endif
            }
            else if (_readback.width != width || _readback.height != height || _readback.format != textureFormat)
            {
                _readback.Reinitialize(width, height, textureFormat, false);
            }

            return _readback;
        }

        private static void ReleaseReadback()
        {
            Application.quitting -= ReleaseReadback;
#if UNITY_EDITOR
            UnityEditor.AssemblyReloadEvents.beforeAssemblyReload -= ReleaseReadback;
#endif
            if (_readback == null)
            {
                return;
            }

            if (Application.isPlaying)
            {
                UnityEngine.Object.Destroy(_readback);
            }
            else
            {
                UnityEngine.Object.DestroyImmediate(_readback);
            }

            _readback = null;
        }

        private static byte[] Encode(Texture2D texture, CaptureImageFormat format, int jpegQuality)
        {
            return format == CaptureImageFormat.Png
                ? texture.EncodeToPNG()
                : texture.EncodeToJPG(Mathf.Clamp(jpegQuality, 1, 100));
        }

        private static AiAttachment Wrap(byte[] bytes, CaptureImageFormat format, string fileName)
        {
            return AiAttachment.Image(bytes, format == CaptureImageFormat.Png ? "image/png" : "image/jpeg",
                fileName ?? "");
        }

        private static bool IsDirectlyEncodable(TextureFormat format)
        {
            return format == TextureFormat.RGBA32 || format == TextureFormat.ARGB32 ||
                   format == TextureFormat.RGB24 || format == TextureFormat.BGRA32;
        }

        private static void Fit(int sourceWidth, int sourceHeight, int maxSide, out int width, out int height)
        {
            width = Mathf.Max(1, sourceWidth);
            height = Mathf.Max(1, sourceHeight);
            int longEdge = Mathf.Max(width, height);
            if (maxSide <= 0 || longEdge <= maxSide)
            {
                return;
            }

            float scale = maxSide / (float)longEdge;
            width = Mathf.Max(1, Mathf.RoundToInt(width * scale));
            height = Mathf.Max(1, Mathf.RoundToInt(height * scale));
        }
    }
}
