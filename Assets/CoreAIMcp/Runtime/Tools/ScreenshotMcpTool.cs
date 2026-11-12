using System;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Mcp.Protocol;
using Newtonsoft.Json.Linq;

namespace CoreAI.Mcp.Tools
{
    /// <summary>
    /// MCP <c>screenshot</c> tool: captures the running game's main camera (or a named camera) to a PNG and
    /// returns it as an MCP image content item (base64). Registered whenever an
    /// <see cref="IScreenshotSource"/> exists; the Unity host always supplies one, so a missing camera is
    /// reported at CALL time instead of hiding the tool from <c>tools/list</c> for the whole session. Runs
    /// on the Unity main thread (rendering is main-thread only).
    /// </summary>
    public sealed class ScreenshotMcpTool : IMcpTool
    {
        /// <summary>Default cap on the longer edge when the client omits <c>max_resolution</c>.</summary>
        public const int DefaultMaxResolution = 1024;

        private readonly IScreenshotSource _source;

        /// <param name="source">The capture source. Never null - the tool is not registered without one.</param>
        public ScreenshotMcpTool(IScreenshotSource source)
        {
            _source = source ?? throw new ArgumentNullException(nameof(source));
        }

        /// <inheritdoc />
        public string Name => "screenshot";

        /// <inheritdoc />
        public string Description =>
            "Capture the running game's main camera (or the camera named in 'camera') and return it as a PNG " +
            "image (base64). Use it to SEE the result of a spawn or edit. max_resolution caps the longer edge " +
            "in pixels (default 1024) to keep the payload small. A camera render has no screen-space UI; " +
            "screen=true captures the displayed frame with the UI instead (needs a drawn window).";

        /// <inheritdoc />
        public string InputSchemaJson =>
            "{\"type\":\"object\",\"properties\":{\"max_resolution\":{\"type\":\"integer\"," +
            "\"description\":\"Cap on the longer image edge in pixels (default 1024). 0 = no cap.\"}," +
            "\"camera\":{\"type\":\"string\",\"description\":\"Optional name of the camera GameObject to " +
            "capture. Omit for the main camera. An unknown name fails and lists the active cameras.\"}," +
            "\"screen\":{\"type\":\"boolean\",\"description\":\"Capture the screen as the player sees it, UI " +
            "included, at the end of a frame. Not with camera; fails in batch mode (no window).\"}}}";

        /// <inheritdoc />
        public Task<McpToolResult> InvokeAsync(JObject arguments, CancellationToken cancellationToken)
        {
            int maxRes = McpArguments.Int(arguments, "max_resolution", DefaultMaxResolution);
            string cameraName = McpArguments.String(arguments, "camera")?.Trim();
            if (McpArguments.Bool(arguments, "screen"))
            {
                return CaptureScreenAsync(maxRes, cameraName);
            }

            string base64;
            string error;
            bool captured;
            if (string.IsNullOrEmpty(cameraName))
            {
                captured = _source.TryCaptureBase64Png(maxRes, out base64, out error);
            }
            else if (_source is ICameraScreenshotSource cameras)
            {
                captured = cameras.TryCaptureBase64Png(cameraName, maxRes, out base64, out error);
            }
            else
            {
                return Task.FromResult(McpToolResult.Failure(
                    "screenshot: this game's capture source cannot select a camera; omit 'camera' to capture " +
                    "the main camera."));
            }

            // WHY: the source reports WHY it failed; passing that text through is the difference between
            // "fix your camera" and a wrong-diagnosis goose chase for the agent on the other end.
            if (!captured)
            {
                return Task.FromResult(McpToolResult.Failure(
                    "screenshot: capture failed - " +
                    (string.IsNullOrEmpty(error) ? "the capture source reported no reason." : error)));
            }

            if (string.IsNullOrEmpty(base64))
            {
                return Task.FromResult(McpToolResult.Failure(
                    "screenshot: the capture source reported success but returned an empty image."));
            }

            return Task.FromResult(new McpToolResult(new[] { McpContent.CreateImage(base64, "image/png") }));
        }

        private Task<McpToolResult> CaptureScreenAsync(int maxResolution, string cameraName)
        {
            if (!string.IsNullOrEmpty(cameraName))
            {
                return Task.FromResult(McpToolResult.Failure(
                    "screenshot: screen=true captures the whole displayed frame; omit 'camera' with it."));
            }

            if (!(_source is IScreenCaptureSource screen))
            {
                return Task.FromResult(McpToolResult.Failure(
                    "screenshot: this game's capture source cannot capture the screen; omit 'screen' to render the " +
                    "main camera."));
            }

            // WHY a completion source without RunContinuationsAsynchronously: the capture ends in an end-of-frame
            // callback on the game thread, and in a WebGL player a pool-resumed continuation would never run.
            TaskCompletionSource<McpToolResult> completion = new();
            screen.CaptureScreen(maxResolution, (base64, error) =>
            {
                completion.TrySetResult(string.IsNullOrEmpty(base64)
                    ? McpToolResult.Failure("screenshot: screen capture failed - " +
                                            (string.IsNullOrEmpty(error) ? "the source reported no reason." : error))
                    : new McpToolResult(new[] { McpContent.CreateImage(base64, "image/png") }));
            });
            return completion.Task;
        }
    }
}
