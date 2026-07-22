namespace CoreAI.Mcp.Tools
{
    /// <summary>
    /// Captures the current game view to a PNG. Kept engine-free so the <c>screenshot</c> MCP tool and
    /// its presence test do not depend on UnityEngine; the Unity implementation
    /// (<c>MainCameraScreenshotSource</c>) lives in the server layer. Implementations MUST be called on
    /// the Unity main thread (the dispatcher guarantees this).
    /// </summary>
    public interface IScreenshotSource
    {
        /// <summary>
        /// Captures the active camera / screen and returns raw base64 PNG (no <c>data:</c> prefix), or
        /// null when nothing could be captured (no camera, headless). <paramref name="maxResolution"/>
        /// caps the longer edge in pixels; values &lt;= 0 mean "no cap".
        /// </summary>
        string CaptureBase64Png(int maxResolution);
    }
}
