using System.Threading;
using System.Threading.Tasks;
using CoreAI.Mcp.Protocol;
using CoreAI.Mcp.Tools;
using NUnit.Framework;
using Newtonsoft.Json.Linq;

namespace CoreAI.Mcp.Tests
{
    /// <summary>
    /// The <c>screenshot</c> tool must hand the capture source's REAL reason to the agent - a swallowed
    /// exception used to be reported as "no active camera" while a camera was plainly on screen.
    /// </summary>
    public sealed class ScreenshotMcpToolEditModeTests
    {
        private sealed class StubScreenshotSource : IScreenshotSource
        {
            private readonly bool _success;
            private readonly string _error;

            public StubScreenshotSource(bool success, string error = null)
            {
                _success = success;
                _error = error;
            }

            public int LastMaxResolution { get; private set; }

            public bool TryCaptureBase64Png(int maxResolution, out string base64Png, out string error)
            {
                LastMaxResolution = maxResolution;
                base64Png = _success ? "QUJD" : null;
                error = _error;
                return _success;
            }
        }

        [Test]
        public async Task Capture_Failure_SurfacesTheSourceReason()
        {
            StubScreenshotSource source =
                new(false, "capture of camera 'Main Camera' at 1024x576 failed: InvalidOperationException: boom");
            ScreenshotMcpTool tool = new(source);

            McpToolResult result = await tool.InvokeAsync(new JObject(), CancellationToken.None);

            Assert.IsTrue(result.IsError);
            StringAssert.Contains("InvalidOperationException: boom", result.Content[0].Text);
        }

        [Test]
        public async Task Capture_Success_ReturnsImageContent()
        {
            ScreenshotMcpTool tool = new(new StubScreenshotSource(true));

            McpToolResult result = await tool.InvokeAsync(new JObject(), CancellationToken.None);

            Assert.IsFalse(result.IsError);
            Assert.AreEqual("image", result.Content[0].Type);
            Assert.AreEqual("QUJD", result.Content[0].Data);
        }

        [Test]
        public async Task MaxResolution_ExplicitJsonNull_UsesTheDefault()
        {
            StubScreenshotSource source = new(true);
            ScreenshotMcpTool tool = new(source);

            await tool.InvokeAsync(JObject.Parse("{\"max_resolution\":null}"), CancellationToken.None);

            Assert.AreEqual(ScreenshotMcpTool.DefaultMaxResolution, source.LastMaxResolution);
        }

        private sealed class StubCameraSource : ICameraScreenshotSource
        {
            public string LastCamera { get; private set; }
            public bool DefaultCaptured { get; private set; }

            public bool TryCaptureBase64Png(int maxResolution, out string base64Png, out string error)
            {
                DefaultCaptured = true;
                base64Png = "TUFJTg==";
                error = null;
                return true;
            }

            public bool TryCaptureBase64Png(string cameraName, int maxResolution, out string base64Png, out string error)
            {
                LastCamera = cameraName;
                bool known = cameraName == "BuildCam";
                base64Png = known ? "QlVJTEQ=" : null;
                error = known ? null : $"no active camera is named '{cameraName}'. Active cameras: Main Camera (main), BuildCam.";
                return known;
            }
        }

        [Test]
        public async Task NamedCamera_IsCapturedThroughTheCameraSource()
        {
            StubCameraSource source = new();
            ScreenshotMcpTool tool = new(source);

            McpToolResult result = await tool.InvokeAsync(JObject.Parse("{\"camera\":\" BuildCam \"}"), CancellationToken.None);

            Assert.IsFalse(result.IsError);
            Assert.AreEqual("BuildCam", source.LastCamera);
            Assert.IsFalse(source.DefaultCaptured, "a named capture must not silently fall back to the main camera.");
            Assert.AreEqual("QlVJTEQ=", result.Content[0].Data);
        }

        [Test]
        public async Task UnknownNamedCamera_FailsAndListsTheCameras()
        {
            StubCameraSource source = new();
            ScreenshotMcpTool tool = new(source);

            McpToolResult result = await tool.InvokeAsync(JObject.Parse("{\"camera\":\"Missing\"}"), CancellationToken.None);

            Assert.IsTrue(result.IsError, "a missing camera is a failure, not a picture of another camera.");
            StringAssert.Contains("BuildCam", result.Content[0].Text);
            Assert.IsFalse(source.DefaultCaptured);
        }

        [TestCase(null)]
        [TestCase("{\"camera\":null}")]
        [TestCase("{\"camera\":\"  \"}")]
        public async Task AbsentCamera_CapturesTheDefaultCamera(string arguments)
        {
            StubCameraSource source = new();
            ScreenshotMcpTool tool = new(source);

            McpToolResult result = await tool.InvokeAsync(arguments == null ? new JObject() : JObject.Parse(arguments),
                CancellationToken.None);

            Assert.IsFalse(result.IsError);
            Assert.IsTrue(source.DefaultCaptured);
            Assert.IsNull(source.LastCamera);
        }

        [Test]
        public async Task NamedCamera_WithASourceThatCannotSelect_FailsWithTheFix()
        {
            StubScreenshotSource source = new(true);
            ScreenshotMcpTool tool = new(source);

            McpToolResult result = await tool.InvokeAsync(JObject.Parse("{\"camera\":\"BuildCam\"}"), CancellationToken.None);

            Assert.IsTrue(result.IsError);
            StringAssert.Contains("omit 'camera'", result.Content[0].Text);
            Assert.AreEqual(0, source.LastMaxResolution, "the plain source must not be asked for a capture.");
        }

        [Test]
        public async Task MaxResolution_Supplied_IsPassedThrough()
        {
            StubScreenshotSource source = new(true);
            ScreenshotMcpTool tool = new(source);

            await tool.InvokeAsync(JObject.Parse("{\"max_resolution\":512}"), CancellationToken.None);

            Assert.AreEqual(512, source.LastMaxResolution);
        }
    }
}
