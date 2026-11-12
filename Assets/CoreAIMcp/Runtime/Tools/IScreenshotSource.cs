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

    /// <summary>
    /// A screenshot source that can also capture one named camera, so an agent can look through a camera
    /// it placed instead of only the main one. The <c>screenshot</c> tool's <c>camera</c> argument needs it.
    /// </summary>
    public interface ICameraScreenshotSource : IScreenshotSource
    {
        /// <summary>
        /// Captures the active camera named <paramref name="cameraName"/>. An unknown name MUST fail (never
        /// fall back to another camera) with an <paramref name="error"/> that names the cameras available.
        /// </summary>
        bool TryCaptureBase64Png(string cameraName, int maxResolution, out string base64Png, out string error);
    }

    /// <summary>
    /// A screenshot source that can also capture the screen as the player sees it, screen-space UI included (the
    /// chat, the HUD, the Hub), which a camera render leaves out. The capture happens at the end of a rendered
    /// frame, so it answers through a callback; the <c>screenshot</c> tool's <c>screen</c> argument needs it.
    /// </summary>
    public interface IScreenCaptureSource
    {
        /// <summary>
        /// Captures the displayed frame and calls <paramref name="done"/> exactly once on the main thread, with a raw
        /// base64 PNG or with the reason it could not (no window in batch mode, not playing, no frame ended in time).
        /// </summary>
        void CaptureScreen(int maxResolution, System.Action<string, string> done);
    }
}
