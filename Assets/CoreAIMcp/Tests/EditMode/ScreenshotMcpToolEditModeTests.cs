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
