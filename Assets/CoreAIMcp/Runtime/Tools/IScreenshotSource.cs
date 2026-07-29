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
        /// Captures the active camera / screen into raw base64 PNG (no <c>data:</c> prefix).
        /// <paramref name="maxResolution"/> caps the longer edge in pixels; values &lt;= 0 mean "no cap".
        /// <para>
        /// WHY (the out-parameter shape): a bare "returns null on failure" contract forced the tool to
        /// guess a reason, and it guessed wrong - an exception inside the capture was reported to the
        /// agent as "no active camera". Implementations MUST put the real reason in
        /// <paramref name="error"/> and log the underlying exception.
        /// </para>
        /// </summary>
        /// <returns>True when <paramref name="base64Png"/> holds an image; false with <paramref name="error"/> set.</returns>
        bool TryCaptureBase64Png(int maxResolution, out string base64Png, out string error);
    }
}
