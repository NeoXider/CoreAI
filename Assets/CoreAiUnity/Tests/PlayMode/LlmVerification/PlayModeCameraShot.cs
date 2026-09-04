using System.IO;
using NUnit.Framework;
using UnityEngine;

namespace CoreAI.Tests.PlayMode
{
    /// <summary>
    /// Writes a PNG of what a camera sees, straight from a RenderTexture.
    /// <para>
    /// WHY: <c>ScreenCapture.CaptureScreenshot</c> needs a presented frame and <c>WaitForEndOfFrame</c>
    /// never resumes under <c>-batchmode</c> (no render loop), so the interactive screenshot path hangs a
    /// headless run forever. <c>Camera.Render</c> into an explicit target works in both.
    /// </para>
    /// </summary>
    internal static class PlayModeCameraShot
    {
        internal const int Width = 1600;
        internal const int Height = 900;

        /// <summary>Absolute path of <paramref name="fileName"/> inside the project's <c>artifacts/</c> folder.</summary>
        internal static string ArtifactPath(string fileName)
        {
            string projectRoot = Directory.GetParent(Application.dataPath)?.FullName
                                 ?? Directory.GetCurrentDirectory();
            string folder = Path.Combine(projectRoot, "artifacts");
            Directory.CreateDirectory(folder);
            return Path.Combine(folder, fileName);
        }

        internal static void Capture(Camera camera, string path)
        {
            if (camera == null)
            {
                TestContext.WriteLine($"[Screenshot] no camera to capture from; '{path}' not written");
                return;
            }

            RenderTexture target = new(Width, Height, 24, RenderTextureFormat.ARGB32) { antiAliasing = 4 };
            Texture2D image = new(Width, Height, TextureFormat.RGB24, false);
            RenderTexture previousActive = RenderTexture.active;
            RenderTexture previousTarget = camera.targetTexture;
            try
            {
                camera.targetTexture = target;
                Render(camera, target);
                RenderTexture.active = target;
                image.ReadPixels(new Rect(0, 0, Width, Height), 0, 0);
                image.Apply();
                File.WriteAllBytes(path, image.EncodeToPNG());
            }
            finally
            {
                camera.targetTexture = previousTarget;
                RenderTexture.active = previousActive;
                Object.DestroyImmediate(image);
                target.Release();
                Object.DestroyImmediate(target);
            }

            TestContext.WriteLine($"[Screenshot] {path} exists={File.Exists(path)}");
        }

        /// <summary>Renders one frame into <paramref name="target"/> through the active pipeline.</summary>
        private static void Render(Camera camera, RenderTexture target)
        {
            // WHY: under a scriptable pipeline Camera.Render() bypasses the URP light loop — every shot
            // came out lit by ambient alone, with no sun and no cast shadows, in this sheet AND in the
            // castle showcase. SubmitRenderRequest is the supported SRP path and restores real lighting.
            // The legacy call stays as the fallback for a project running the built-in pipeline.
            UnityEngine.Rendering.RenderPipeline.StandardRequest request = new() { destination = target };
            if (UnityEngine.Rendering.RenderPipeline.SupportsRenderRequest(camera, request))
            {
                UnityEngine.Rendering.RenderPipeline.SubmitRenderRequest(camera, request);
                return;
            }

            camera.Render();
        }
    }
}
