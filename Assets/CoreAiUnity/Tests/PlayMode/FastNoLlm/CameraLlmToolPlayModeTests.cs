using System;
using CoreAI.Infrastructure.World;
using NUnit.Framework;
using UnityEngine;

namespace CoreAI.Tests.PlayMode
{
    /// <summary>
    /// Verifies the camera capture path used by the vision tool: a real camera renders to a valid JPEG
    /// frame. Runs in PlayMode because <see cref="Camera.Render"/> + <c>ReadPixels</c> require a live
    /// render pipeline.
    /// </summary>
    public sealed class CameraLlmToolPlayModeTests
    {
        /// <summary>Ends the test as ignored when the editor runs with no graphics device.</summary>
        /// <remarks>
        /// WHY: these capture the screen through a RenderTexture, and a batchmode run started with
        /// -nographics has no device to create one on — "RenderTexture.Create failed" is then an
        /// unhandled error and the test fails for the machine it ran on, not for the code. The failures
        /// were invisible until 2026-09-10, because the whole PlayMode run used to abort before reaching
        /// this fixture. A graphics-capable run still exercises everything below.
        /// </remarks>
        private static void IgnoreWithoutAGraphicsDevice()
        {
            if (SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null)
            {
                Assert.Ignore("No graphics device (batchmode -nographics): a RenderTexture cannot be created.");
            }
        }

        [Test]
        public void CaptureCameraJpeg_RendersValidJpegFrame()
        {
            IgnoreWithoutAGraphicsDevice();
            GameObject camObj = new("VisionTestCam");
            Camera cam = camObj.AddComponent<Camera>();
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = Color.cyan;

            try
            {
                byte[] jpg = CameraLlmTool.CaptureCameraJpeg(cam, 128, 96);

                Assert.IsNotNull(jpg);
                Assert.Greater(jpg.Length, 0, "Capture must produce JPEG bytes.");
                // JPEG SOI marker.
                Assert.AreEqual(0xFF, jpg[0]);
                Assert.AreEqual(0xD8, jpg[1]);
            }
            finally
            {
                UnityEngine.Object.Destroy(camObj);
            }
        }

        [Test]
        public void CaptureCameraJpeg_ClampsResolutionAndDoesNotThrow()
        {
            IgnoreWithoutAGraphicsDevice();
            GameObject camObj = new("VisionTestCamClamp");
            Camera cam = camObj.AddComponent<Camera>();

            try
            {
                // Below-min and above-max are clamped to 64..1024 internally; should not throw.
                byte[] tiny = CameraLlmTool.CaptureCameraJpeg(cam, 1, 1);
                byte[] huge = CameraLlmTool.CaptureCameraJpeg(cam, 8000, 8000);
                Assert.Greater(tiny.Length, 0);
                Assert.Greater(huge.Length, 0);
            }
            finally
            {
                UnityEngine.Object.Destroy(camObj);
            }
        }

        [Test]
        public void CaptureCameraJpeg_NullCamera_Throws()
        {
            Assert.Throws<ArgumentNullException>(() => CameraLlmTool.CaptureCameraJpeg(null, 64, 64));
        }
    }
}
