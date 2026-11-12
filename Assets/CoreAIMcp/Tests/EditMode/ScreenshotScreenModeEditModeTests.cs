using System;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Mcp.Protocol;
using CoreAI.Mcp.Tools;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace CoreAI.Mcp.Tests
{
    /// <summary>
    /// <c>screenshot screen=true</c>: the displayed frame with its UI, answered when the end-of-frame capture
    /// calls back, with the source's reason when it cannot (batch mode) and a refusal for a source without it.
    /// </summary>
    public sealed class ScreenshotScreenModeEditModeTests
    {
        [Test]
        public async Task Screen_WaitsForTheEndOfFrameCallback_ThenReturnsTheImage()
        {
            FakeScreenSource source = new();
            ScreenshotMcpTool tool = new(source);

            Task<McpToolResult> pending = tool.InvokeAsync(new JObject { ["screen"] = true, ["max_resolution"] = 640 },
                CancellationToken.None);
            Assert.IsFalse(pending.IsCompleted, "the capture answers at the end of a frame, not at once");
            Assert.AreEqual(640, source.RequestedResolution);
            source.Complete("iVBORw0KGgo=", null);
            McpToolResult result = await pending;

            Assert.IsFalse(result.IsError);
            Assert.AreEqual("image", result.Content[0].Type);
            Assert.AreEqual("iVBORw0KGgo=", result.Content[0].Data);
            Assert.AreEqual(0, source.CameraCaptures, "screen=true does not render a camera");
        }

        [Test]
        public async Task Screen_PassesTheSourceReason()
        {
            FakeScreenSource source = new();
            Task<McpToolResult> pending = new ScreenshotMcpTool(source).InvokeAsync(new JObject { ["screen"] = true },
                CancellationToken.None);
            source.Complete(null, "this run draws no screen (batch mode or -nographics)");

            McpToolResult result = await pending;

            Assert.IsTrue(result.IsError);
            StringAssert.Contains("batch mode", result.Content[0].Text);
        }

        [Test]
        public async Task Screen_WithCamera_OrWithoutSupport_IsRefused()
        {
            McpToolResult withCamera = await new ScreenshotMcpTool(new FakeScreenSource()).InvokeAsync(
                new JObject { ["screen"] = true, ["camera"] = "Map" }, CancellationToken.None);
            McpToolResult unsupported = await new ScreenshotMcpTool(new CameraOnlySource()).InvokeAsync(
                new JObject { ["screen"] = true }, CancellationToken.None);

            Assert.IsTrue(withCamera.IsError);
            StringAssert.Contains("omit 'camera'", withCamera.Content[0].Text);
            Assert.IsTrue(unsupported.IsError);
            StringAssert.Contains("cannot capture the screen", unsupported.Content[0].Text);
        }

        [Test]
        public void Schema_OffersScreen()
        {
            JObject schema = JObject.Parse(new ScreenshotMcpTool(new CameraOnlySource()).InputSchemaJson);
            Assert.AreEqual("boolean", (string)schema["properties"]["screen"]["type"]);
        }

        private sealed class FakeScreenSource : IScreenshotSource, IScreenCaptureSource
        {
            private Action<string, string> _done;

            public int RequestedResolution { get; private set; }

            public int CameraCaptures { get; private set; }

            public bool TryCaptureBase64Png(int maxResolution, out string base64Png, out string error)
            {
                CameraCaptures++;
                base64Png = "camera";
                error = null;
                return true;
            }

            public void CaptureScreen(int maxResolution, Action<string, string> done)
            {
                RequestedResolution = maxResolution;
                _done = done;
            }

            public void Complete(string base64, string error) => _done(base64, error);
        }

        private sealed class CameraOnlySource : IScreenshotSource
        {
            public bool TryCaptureBase64Png(int maxResolution, out string base64Png, out string error)
            {
                base64Png = "camera";
                error = null;
                return true;
            }
        }
    }
}
