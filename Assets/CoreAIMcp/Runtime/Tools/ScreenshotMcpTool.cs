using System;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Mcp.Protocol;
using Newtonsoft.Json.Linq;

namespace CoreAI.Mcp.Tools
{
    /// <summary>
    /// MCP <c>screenshot</c> tool: captures the running game's main camera to a PNG and returns it as an
    /// MCP image content item (base64). Registered whenever an <see cref="IScreenshotSource"/> exists;
    /// the Unity host always supplies one, so a missing camera is reported at CALL time instead of
    /// hiding the tool from <c>tools/list</c> for the whole session. Runs on the Unity main thread
    /// (rendering is main-thread only).
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
            "Capture the running game's main camera and return it as a PNG image (base64). Use it to SEE " +
            "the result of a spawn or edit. max_resolution caps the longer edge in pixels (default " +
            "1024) to keep the payload small; the image is downscaled to fit.";

        /// <inheritdoc />
        public string InputSchemaJson =>
            "{\"type\":\"object\",\"properties\":{\"max_resolution\":{\"type\":\"integer\"," +
            "\"description\":\"Cap on the longer image edge in pixels (default 1024). 0 = no cap.\"}}}";

        /// <inheritdoc />
        public Task<McpToolResult> InvokeAsync(JObject arguments, CancellationToken cancellationToken)
        {
            int maxRes = McpArguments.Int(arguments, "max_resolution", DefaultMaxResolution);

            // WHY: the source reports WHY it failed; passing that text through is the difference between
            // "fix your camera" and a wrong-diagnosis goose chase for the agent on the other end.
            if (!_source.TryCaptureBase64Png(maxRes, out string base64, out string error))
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
    }
}
