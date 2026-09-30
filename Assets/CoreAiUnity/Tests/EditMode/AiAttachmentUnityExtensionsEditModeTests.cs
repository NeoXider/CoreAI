using System;
using CoreAI.Ai;
using CoreAI.Vision;
using NUnit.Framework;
using UnityEngine;

namespace CoreAI.Tests.EditMode
{
    /// <summary>
    /// The Unity half of the attachment API (<see cref="AiAttachmentUnityExtensions"/>): textures (direct encode and
    /// the pooled blit path), sprite regions, render textures, camera captures that restore the camera and the active
    /// render texture, the 1024 default long edge, the release of a readback above 4 megapixels, and text assets.
    /// Every image test checks the encoded signature and the decoded size.
    /// </summary>
    [TestFixture]
    public sealed class AiAttachmentUnityExtensionsEditModeTests
    {
        private static readonly byte[] JpegSignature = { 0xFF, 0xD8, 0xFF };
        private static readonly byte[] PngSignature = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };

        [SetUp]
        public void RequireGraphicsDevice()
        {
            if (SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null)
            {
                Assert.Ignore("No graphics device (batchmode -nographics): textures cannot be rendered or read back.");
            }
        }

        [Test]
        public void ReadableTexture_AtNativeSize_IsEncodedDirectly()
        {
            Texture2D texture = Filled(8, 4, TextureFormat.RGBA32);
            try
            {
                AiAttachment jpeg = texture.ToAiAttachment(maxSide: 0, fileName: "tile.jpg");
                AiAttachment png = texture.ToAiAttachment(0, CaptureImageFormat.Png);

                AssertImage(jpeg, "image/jpeg", JpegSignature, 8, 4);
                AssertImage(png, "image/png", PngSignature, 8, 4);
                Assert.AreEqual("tile.jpg", jpeg.FileName);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(texture);
            }
        }

        [Test]
        public void GpuOnlyTexture_GoesThroughTheBlitPath()
        {
            Texture2D texture = Filled(16, 8, TextureFormat.RGBA32);
            texture.Apply(false, true);
            try
            {
                Assert.IsFalse(texture.isReadable);

                AssertImage(texture.ToAiAttachment(maxSide: 0), "image/jpeg", JpegSignature, 16, 8);
                AssertImage(texture.ToAiAttachment(maxSide: 8, format: CaptureImageFormat.Png), "image/png",
                    PngSignature, 8, 4);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(texture);
            }
        }

        [Test]
        public void LargeTexture_IsDownscaledTo1024ByDefault()
        {
            Texture2D texture = Filled(2048, 1024, TextureFormat.RGBA32);
            try
            {
                Assert.AreEqual(1024, AiAttachmentUnityExtensions.DefaultMaxSide);
                AssertImage(texture.ToAiAttachment(), "image/jpeg", JpegSignature, 1024, 512);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(texture);
            }
        }

        [Test]
        public void Sprite_EncodesOnlyItsOwnRegion()
        {
            Texture2D texture = Filled(16, 8, TextureFormat.RGBA32);
            Sprite sprite = Sprite.Create(texture, new Rect(4, 0, 8, 8), new Vector2(0.5f, 0.5f));
            sprite.name = "coin";
            try
            {
                AiAttachment attachment = sprite.ToAiAttachment();

                AssertImage(attachment, "image/png", PngSignature, 8, 8);
                Assert.AreEqual("coin", attachment.FileName);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(sprite);
                UnityEngine.Object.DestroyImmediate(texture);
            }
        }

        [Test]
        public void RenderTexture_ReadBackAndDownscale_RestoreTheActiveTarget()
        {
            RenderTexture source = new(32, 16, 0, RenderTextureFormat.ARGB32);
            RenderTexture other = new(4, 4, 0, RenderTextureFormat.ARGB32);
            source.Create();
            other.Create();
            RenderTexture previous = RenderTexture.active;
            try
            {
                RenderTexture.active = other;

                AssertImage(source.ToAiAttachment(maxSide: 0), "image/jpeg", JpegSignature, 32, 16);
                AssertImage(source.ToAiAttachment(maxSide: 16, format: CaptureImageFormat.Png), "image/png",
                    PngSignature, 16, 8);
                Assert.AreSame(other, RenderTexture.active, "The caller's active render texture is restored.");
            }
            finally
            {
                RenderTexture.active = previous;
                source.Release();
                other.Release();
                UnityEngine.Object.DestroyImmediate(source);
                UnityEngine.Object.DestroyImmediate(other);
            }
        }

        [TestCase(CameraClearFlags.SolidColor)]
        [TestCase(CameraClearFlags.Depth)]
        [TestCase(CameraClearFlags.Nothing)]
        public void CameraCapture_RestoresTheCameraTargetAndTheActiveRenderTexture(CameraClearFlags clearFlags)
        {
            GameObject host = new("AiAttachmentUnityExtensions_Camera");
            RenderTexture cameraTarget = new(64, 32, 16);
            RenderTexture active = new(4, 4, 0);
            RenderTexture previous = RenderTexture.active;
            try
            {
                Camera camera = host.AddComponent<Camera>();
                camera.clearFlags = clearFlags;
                camera.backgroundColor = Color.red;
                camera.targetTexture = cameraTarget;
                RenderTexture.active = active;

                AiAttachment frame = camera.CaptureAiAttachment(maxSide: 32);

                AssertImage(frame, "image/jpeg", JpegSignature, 32, 16);
                Assert.AreEqual(host.name, frame.FileName);
                Assert.AreSame(cameraTarget, camera.targetTexture, "The camera's own target texture is restored.");
                Assert.AreSame(active, RenderTexture.active, "The active render texture is restored.");
            }
            finally
            {
                RenderTexture.active = previous;
                UnityEngine.Object.DestroyImmediate(host);
                cameraTarget.Release();
                active.Release();
                UnityEngine.Object.DestroyImmediate(cameraTarget);
                UnityEngine.Object.DestroyImmediate(active);
            }
        }

        [Test]
        public void ReadbackAboveFourMegapixels_IsNotKeptAlive()
        {
            RenderTexture small = new(8, 8, 0, RenderTextureFormat.ARGB32);
            RenderTexture huge = new(2049, 2048, 0, RenderTextureFormat.ARGB32);
            small.Create();
            huge.Create();
            try
            {
                small.ToAiAttachment(maxSide: 0);
                Assert.IsTrue(AiAttachmentUnityExtensions.HasCachedReadback, "A small readback is cached for reuse.");

                AssertImage(huge.ToAiAttachment(maxSide: 0), "image/jpeg", JpegSignature, 2049, 2048);
                Assert.IsFalse(AiAttachmentUnityExtensions.HasCachedReadback,
                    "A readback above 4 megapixels must be released right after the capture.");
            }
            finally
            {
                small.Release();
                huge.Release();
                UnityEngine.Object.DestroyImmediate(small);
                UnityEngine.Object.DestroyImmediate(huge);
            }
        }

        [Test]
        public void TextAsset_IsATextAttachmentNamedByTheCaller()
        {
            TextAsset asset = new("return 42") { name = "enemy" };
            try
            {
                AiAttachment lua = asset.ToAiAttachment("enemy.lua");
                AiAttachment plain = asset.ToAiAttachment();

                Assert.AreEqual(AiAttachmentCategory.Text, lua.Category);
                Assert.AreEqual("application/x-lua", lua.ResolvedMediaType);
                Assert.AreEqual("text/plain", plain.ResolvedMediaType, "An extension-less asset name is plain text.");
                StringAssert.Contains("--- attached file: enemy.lua (application/x-lua) ---\nreturn 42",
                    AiUserMessageBuilder.BuildUserMessage("review", new[] { lua }).Text);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(asset);
            }
        }

        private static Texture2D Filled(int width, int height, TextureFormat format)
        {
            Texture2D texture = new(width, height, format, false);
            Color32[] pixels = new Color32[width * height];
            for (int i = 0; i < pixels.Length; i++)
            {
                pixels[i] = new Color32((byte)(i * 7), (byte)(i * 13), 200, 255);
            }

            texture.SetPixels32(pixels);
            texture.Apply(false, false);
            return texture;
        }

        private static void AssertImage(AiAttachment attachment, string mediaType, byte[] signature, int width,
            int height)
        {
            Assert.AreEqual(mediaType, attachment.ResolvedMediaType);
            Assert.AreEqual(AiAttachmentCategory.Image, attachment.Category);
            byte[] bytes = attachment.Data;
            Assert.GreaterOrEqual(bytes.Length, signature.Length);
            for (int i = 0; i < signature.Length; i++)
            {
                Assert.AreEqual(signature[i], bytes[i], $"Signature byte {i} of {mediaType}.");
            }

            Texture2D decoded = new(2, 2);
            try
            {
                Assert.IsTrue(decoded.LoadImage(bytes), "The encoded bytes must decode.");
                Assert.AreEqual(width, decoded.width);
                Assert.AreEqual(height, decoded.height);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(decoded);
            }
        }
    }
}
