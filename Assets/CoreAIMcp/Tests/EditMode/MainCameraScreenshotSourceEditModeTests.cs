using System;
using CoreAI.Mcp.Server;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Rendering;

namespace CoreAI.Mcp.Tests
{
    /// <summary>
    /// The Unity capture behind the <c>screenshot</c> tool. It used to create and Destroy() a texture per
    /// call, which logs an error outside play mode and leaks the texture; it now goes through the shared
    /// pooled capture. A named camera is captured exactly or the call fails naming the cameras available.
    /// Self-skips without a graphics device (batch mode with -nographics).
    /// </summary>
    public sealed class MainCameraScreenshotSourceEditModeTests
    {
        private GameObject _cameraObject;
        private string _cameraName;

        [SetUp]
        public void SetUp()
        {
            if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null)
            {
                Assert.Ignore("No graphics device: a camera cannot render in this run.");
            }

            _cameraName = "McpCaptureCamera_" + Guid.NewGuid().ToString("N");
            _cameraObject = new GameObject(_cameraName);
            Camera camera = _cameraObject.AddComponent<Camera>();
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = Color.magenta;
        }

        [TearDown]
        public void TearDown()
        {
            if (_cameraObject != null) UnityEngine.Object.DestroyImmediate(_cameraObject);
        }

        [Test]
        public void NamedCapture_OutsidePlayMode_ReturnsAPng_WithoutLoggingAnError()
        {
            MainCameraScreenshotSource source = new();

            bool captured = source.TryCaptureBase64Png(_cameraName, 64, out string base64, out string error);

            Assert.IsTrue(captured, error);
            byte[] png = Convert.FromBase64String(base64);
            CollectionAssert.AreEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, new[] { png[0], png[1], png[2], png[3] });
        }

        [Test]
        public void DefaultCapture_OutsidePlayMode_ReturnsAPng_WithoutLoggingAnError()
        {
            _cameraObject.tag = "MainCamera";
            MainCameraScreenshotSource source = new();

            bool captured = source.TryCaptureBase64Png(48, out string base64, out string error);

            Assert.IsTrue(captured, error);
            Assert.IsNotEmpty(base64, "the old per-call Destroy() logged an error here and the run failed on it.");
        }

        [Test]
        public void NamedCapture_IsCaseInsensitiveWhenTheMatchIsUnique()
        {
            MainCameraScreenshotSource source = new();

            Assert.IsTrue(source.TryCaptureBase64Png(_cameraName.ToUpperInvariant(), 32, out string base64, out string error),
                error);
            Assert.IsNotEmpty(base64);
        }

        [Test]
        public void NamedCapture_UnknownName_FailsAndListsTheActiveCameras()
        {
            MainCameraScreenshotSource source = new();

            bool captured = source.TryCaptureBase64Png("NoSuchCamera_" + Guid.NewGuid().ToString("N"), 64,
                out string base64, out string error);

            Assert.IsFalse(captured, "an unknown camera must never fall back to another one.");
            Assert.IsNull(base64);
            StringAssert.Contains(_cameraName, error);
        }
    }
}
