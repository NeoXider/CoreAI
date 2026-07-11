using System;
using CoreAI.Vision;
using NUnit.Framework;
using UnityEngine;

namespace CoreAI.Tests.PlayMode
{
    /// <summary>
    /// Verifies the agent-vision capture path: a real camera renders to non-empty, valid image bytes.
    /// Runs in PlayMode because <see cref="Camera.Render"/> + <c>ReadPixels</c> require a live render
    /// pipeline. Gating / rate-limit logic is covered in EditMode by <c>AgentCameraServiceEditModeTests</c>.
    /// </summary>
    public sealed class AgentCameraCapturePlayModeTests
    {
        [Test]
        public void CaptureToBytes_Jpeg_ProducesValidFrame()
        {
            GameObject camObj = new("AgentVisionTestCam");
            Camera cam = camObj.AddComponent<Camera>();
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = Color.magenta;

            try
            {
                byte[] jpg = AgentCameraService.CaptureToBytes(cam, 256, CaptureImageFormat.Jpeg,
                    out int width, out int height);

                Assert.IsNotNull(jpg);
                Assert.Greater(jpg.Length, 0, "Capture must produce JPEG bytes.");
                Assert.Greater(width, 0);
                Assert.Greater(height, 0);
                Assert.LessOrEqual(Mathf.Max(width, height), 1024, "Long edge must respect the size cap.");
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
        public void CaptureToBytes_Png_ProducesValidFrame()
        {
            GameObject camObj = new("AgentVisionTestCamPng");
            Camera cam = camObj.AddComponent<Camera>();

            try
            {
                byte[] png = AgentCameraService.CaptureToBytes(cam, 128, CaptureImageFormat.Png,
                    out int _, out int _);

                Assert.IsNotNull(png);
                Assert.Greater(png.Length, 0, "Capture must produce PNG bytes.");
                // PNG 8-byte signature.
                Assert.AreEqual(0x89, png[0]);
                Assert.AreEqual(0x50, png[1]); // 'P'
                Assert.AreEqual(0x4E, png[2]); // 'N'
                Assert.AreEqual(0x47, png[3]); // 'G'
            }
            finally
            {
                UnityEngine.Object.Destroy(camObj);
            }
        }

        [Test]
        public void CaptureToBytes_ClampsResolution_AndDoesNotThrow()
        {
            GameObject camObj = new("AgentVisionTestCamClamp");
            Camera cam = camObj.AddComponent<Camera>();

            try
            {
                byte[] tiny =
                    AgentCameraService.CaptureToBytes(cam, 1, CaptureImageFormat.Jpeg, out int tw, out int th);
                byte[] huge =
                    AgentCameraService.CaptureToBytes(cam, 8000, CaptureImageFormat.Jpeg, out int hw, out int hh);

                Assert.Greater(tiny.Length, 0);
                Assert.Greater(huge.Length, 0);
                Assert.GreaterOrEqual(Mathf.Max(tw, th), AgentCameraService.MinSize);
                Assert.LessOrEqual(Mathf.Max(hw, hh), AgentCameraService.MaxSize);
            }
            finally
            {
                UnityEngine.Object.Destroy(camObj);
            }
        }

        [Test]
        public void CaptureToBytes_NullCamera_Throws()
        {
            Assert.Throws<ArgumentNullException>(() =>
                AgentCameraService.CaptureToBytes(null, 64, CaptureImageFormat.Jpeg, out int _, out int _));
        }
    }
}
