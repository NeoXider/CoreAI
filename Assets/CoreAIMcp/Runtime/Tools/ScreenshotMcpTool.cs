using System;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Mcp.Protocol;
using Newtonsoft.Json.Linq;

namespace CoreAI.Mcp.Tools
{
    /// <summary>
    /// MCP <c>screenshot</c> tool: captures the running game's main camera to a PNG and returns it as an
    /// MCP image content item (base64). Present ONLY when an <see cref="IScreenshotSource"/> is available
    /// in the current composition. Runs on the Unity main thread (rendering is main-thread only).
    /// </summary>
    public sealed class ScreenshotMcpTool : IMcpTool
    {
        /// <summary>Default cap on the longer edge when the client omits <c>max_resolution</c>.</summary>
        public const int DefaultMaxResolution = 1024;

        private readonly IScreenshotSource _source;

        /// <param name="source">The capture source. Never null - the tool is omitted when none exists.</param>
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
            int maxRes = DefaultMaxResolution;
            JToken token = arguments?["max_resolution"];
            if (token != null && token.Type != JTokenType.Null)
            {
                try
                {
                    maxRes = token.Value<int>();
                }
                catch (Exception)
                {
                    maxRes = DefaultMaxResolution;
                }
            }

            string base64 = _source.CaptureBase64Png(maxRes);
            if (string.IsNullOrEmpty(base64))
            {
                return Task.FromResult(McpToolResult.Failure(
                    "screenshot: nothing could be captured (no active camera or headless session)."));
            }

            return Task.FromResult(new McpToolResult(new[] { McpContent.CreateImage(base64, "image/png") }));
        }
    }
}
