using System;
using System.Diagnostics;
using System.Threading;
using Cysharp.Threading.Tasks;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;

namespace CoreAI.Vision
{
    /// <summary>How a camera frame was read back from the GPU.</summary>
    public enum CameraReadbackMode
    {
        /// <summary>Synchronous <c>ReadPixels</c> on the main thread (stalls until the GPU finishes the frame).</summary>
        Sync,

        /// <summary><see cref="AsyncGPUReadback"/>: the frame arrives a few frames later without stalling.</summary>
        AsyncGpu
    }

    /// <summary>One encoded camera frame and what producing it cost the main thread.</summary>
    public sealed class CameraFrameReadbackResult
    {
        public CameraFrameReadbackResult(byte[] bytes, CameraReadbackMode mode, int framesWaited,
            double mainThreadMilliseconds)
        {
            Bytes = bytes;
            Mode = mode;
            FramesWaited = framesWaited;
            MainThreadMilliseconds = mainThreadMilliseconds;
        }

        /// <summary>The encoded JPEG or PNG.</summary>
        public byte[] Bytes { get; }

        /// <summary>Which path produced the frame.</summary>
        public CameraReadbackMode Mode { get; }

        /// <summary>Frames between the render and the moment the pixels were available (0 for the sync path).</summary>
        public int FramesWaited { get; }

        /// <summary>
        /// Main-thread time the capture blocked, in milliseconds: render submission, readback (the whole GPU stall on
        /// the sync path) and encoding. Waiting frames do not count; they never block.
        /// </summary>
        public double MainThreadMilliseconds { get; }
    }

    /// <summary>
    /// Renders a camera into a pooled offscreen target and returns the encoded frame. The camera's own target
    /// texture and the active render texture are restored, and its transform is never touched. Every camera tool
    /// captures through this one seam: <see cref="AgentCameraService"/> (<c>camera_capture</c>). Replace it to
    /// change how frames are read back; the default is <see cref="CameraFrameReadback.CreateDefault"/>.
    /// </summary>
    public interface ICameraFrameReadback
    {
        /// <summary>
        /// Captures <paramref name="camera"/> at exactly <paramref name="width"/>x<paramref name="height"/> pixels after
        /// the current frame has finished drawing. Call from the Unity main thread; the task may complete frames later.
        /// Throws <see cref="OperationCanceledException"/> on cancellation.
        /// </summary>
        UniTask<CameraFrameReadbackResult> CaptureAsync(Camera camera, int width, int height,
            CaptureImageFormat format, int jpegQuality, CancellationToken cancellationToken);
    }

    /// <summary>Factory for the shipped readback strategy.</summary>
    public static class CameraFrameReadback
    {
        /// <summary>
        /// <see cref="AsyncGpuCameraFrameReadback"/> where <see cref="SystemInfo.supportsAsyncGPUReadback"/> is true,
        /// falling back to <see cref="SyncCameraFrameReadback"/> per capture elsewhere (WebGL 2, devices without
        /// support, a failed request). A new instance per call; it holds no GPU resources between captures.
        /// </summary>
        public static ICameraFrameReadback CreateDefault()
        {
            return new AsyncGpuCameraFrameReadback();
        }

        internal static double ElapsedMilliseconds(long startTimestamp)
        {
            return (Stopwatch.GetTimestamp() - startTimestamp) * 1000.0 / Stopwatch.Frequency;
        }
    }

    /// <summary>
    /// The synchronous path: renders into a pooled target, <c>ReadPixels</c> into one cached readback texture and
    /// encodes, all in one main-thread step (the same pipeline as <c>Camera.CaptureAiAttachment</c>). Works on every
    /// platform, WebGL included; the cost is a GPU stall on the capture frame.
    /// </summary>
    public sealed class SyncCameraFrameReadback : ICameraFrameReadback
    {
        /// <inheritdoc/>
        public async UniTask<CameraFrameReadbackResult> CaptureAsync(Camera camera, int width, int height,
            CaptureImageFormat format, int jpegQuality, CancellationToken cancellationToken)
        {
            if (camera == null)
            {
                throw new ArgumentNullException(nameof(camera));
            }

            await UniTask.SwitchToMainThread(cancellationToken);
            // WHY: let the current frame finish so the capture reflects a fully drawn frame.
            await UniTask.Yield(PlayerLoopTiming.LastPostLateUpdate, cancellationToken);
            return CaptureNow(camera, width, height, format, jpegQuality);
        }

        internal static CameraFrameReadbackResult CaptureNow(Camera camera, int width, int height,
            CaptureImageFormat format, int jpegQuality)
        {
            if (camera == null)
            {
                throw new InvalidOperationException("The camera was destroyed before its frame was captured.");
            }

            long started = Stopwatch.GetTimestamp();
            byte[] bytes = AiAttachmentUnityExtensions.CaptureCamera(camera, Math.Max(1, width), Math.Max(1, height),
                format, jpegQuality);
            return new CameraFrameReadbackResult(bytes, CameraReadbackMode.Sync, 0,
                CameraFrameReadback.ElapsedMilliseconds(started));
        }
    }

    /// <summary>
    /// The asynchronous path: renders into a pooled target (<see cref="RenderTexture.GetTemporary(int, int, int, RenderTextureFormat, RenderTextureReadWrite)"/>),
    /// requests an <see cref="AsyncGPUReadback"/>, waits for it frame by frame without blocking, then encodes the
    /// raw pixels straight from native memory. The main thread pays for the render submission and the encode, never
    /// for a GPU stall. Falls back to <see cref="SyncCameraFrameReadback"/> when the platform has no async readback
    /// (WebGL 2), the request fails, the target format cannot be encoded, or the request is not done within
    /// <see cref="MaxWaitFrames"/> frames. No threads and no blocking waits, so it is safe on every player.
    /// </summary>
    public sealed class AsyncGpuCameraFrameReadback : ICameraFrameReadback
    {
        /// <summary>Default number of frames to wait for a readback before falling back to the sync path.</summary>
        public const int DefaultMaxWaitFrames = 30;

        private readonly ICameraFrameReadback _fallback;
        private readonly Func<bool> _isSupported;

        /// <summary>Platform detection through <see cref="SystemInfo.supportsAsyncGPUReadback"/>, sync fallback.</summary>
        public AsyncGpuCameraFrameReadback()
            : this(null, null, DefaultMaxWaitFrames)
        {
        }

        /// <summary>
        /// <paramref name="fallback"/> captures when async readback cannot (null = <see cref="SyncCameraFrameReadback"/>);
        /// <paramref name="isSupported"/> replaces the platform check (null = <see cref="SystemInfo.supportsAsyncGPUReadback"/>).
        /// </summary>
        public AsyncGpuCameraFrameReadback(ICameraFrameReadback fallback, Func<bool> isSupported, int maxWaitFrames)
        {
            _fallback = fallback ?? new SyncCameraFrameReadback();
            _isSupported = isSupported ?? (() => SystemInfo.supportsAsyncGPUReadback);
            MaxWaitFrames = Math.Max(1, maxWaitFrames);
        }

        /// <summary>Frames to wait for a pending readback before the frame is captured through the fallback instead.</summary>
        public int MaxWaitFrames { get; }

        /// <inheritdoc/>
        public async UniTask<CameraFrameReadbackResult> CaptureAsync(Camera camera, int width, int height,
            CaptureImageFormat format, int jpegQuality, CancellationToken cancellationToken)
        {
            if (camera == null)
            {
                throw new ArgumentNullException(nameof(camera));
            }

            if (!_isSupported())
            {
                return await _fallback.CaptureAsync(camera, width, height, format, jpegQuality, cancellationToken);
            }

            await UniTask.SwitchToMainThread(cancellationToken);
            // WHY: let the current frame finish so the capture reflects a fully drawn frame.
            await UniTask.Yield(PlayerLoopTiming.LastPostLateUpdate, cancellationToken);
            if (camera == null)
            {
                throw new InvalidOperationException("The camera was destroyed before its frame was captured.");
            }

            width = Math.Max(1, width);
            height = Math.Max(1, height);
            RenderTexture target = RenderTexture.GetTemporary(width, height, 24, RenderTextureFormat.ARGB32,
                RenderTextureReadWrite.Default);
            try
            {
                GraphicsFormat graphicsFormat = target.graphicsFormat;
                if (!IsEncodable(graphicsFormat))
                {
                    return await _fallback.CaptureAsync(camera, width, height, format, jpegQuality,
                        cancellationToken);
                }

                long started = Stopwatch.GetTimestamp();
                Render(camera, target);
                AsyncGPUReadbackRequest request = AsyncGPUReadback.Request(target, 0);
                double mainThreadMs = CameraFrameReadback.ElapsedMilliseconds(started);
                int framesWaited = 0;
                while (!request.done && framesWaited < MaxWaitFrames)
                {
                    await UniTask.Yield(PlayerLoopTiming.Update, cancellationToken);
                    framesWaited++;
                    request.Update();
                }

                if (!request.done || request.hasError)
                {
                    return await _fallback.CaptureAsync(camera, width, height, format, jpegQuality,
                        cancellationToken);
                }

                long encodeStarted = Stopwatch.GetTimestamp();
                NativeArray<byte> pixels = request.GetData<byte>();
                if (pixels.Length < width * height * 4)
                {
                    return await _fallback.CaptureAsync(camera, width, height, format, jpegQuality,
                        cancellationToken);
                }

                byte[] bytes;
                try
                {
                    bytes = Encode(pixels, graphicsFormat, width, height, format, jpegQuality);
                }
                catch (Exception ex) when (ex is ArgumentException || ex is InvalidOperationException ||
                                           ex is UnityException)
                {
                    // WHY: an encoder that rejects this platform's readback layout must cost one slower frame, not the capture.
                    return await _fallback.CaptureAsync(camera, width, height, format, jpegQuality,
                        cancellationToken);
                }

                mainThreadMs += CameraFrameReadback.ElapsedMilliseconds(encodeStarted);
                return new CameraFrameReadbackResult(bytes, CameraReadbackMode.AsyncGpu, framesWaited, mainThreadMs);
            }
            finally
            {
                RenderTexture.ReleaseTemporary(target);
            }
        }

        /// <summary>True for the 8-bit four-channel layouts the encoder reads straight from a readback buffer.</summary>
        internal static bool IsEncodable(GraphicsFormat format)
        {
            return format == GraphicsFormat.R8G8B8A8_SRGB || format == GraphicsFormat.R8G8B8A8_UNorm ||
                   format == GraphicsFormat.B8G8R8A8_SRGB || format == GraphicsFormat.B8G8R8A8_UNorm;
        }

        private static void Render(Camera camera, RenderTexture target)
        {
            // WHY: an HDR render target - see AiAttachmentUnityExtensions.CaptureCamera; the readback target stays
            // the 8-bit sRGB texture the encoder reads, and the HDR frame is converted into it.
            bool hdr = camera.allowHDR && SystemInfo.SupportsRenderTextureFormat(RenderTextureFormat.DefaultHDR);
            RenderTexture rendered = hdr
                ? RenderTexture.GetTemporary(target.width, target.height, 24, RenderTextureFormat.DefaultHDR)
                : target;
            RenderTexture previousTarget = camera.targetTexture;
            RenderTexture previousActive = RenderTexture.active;
            try
            {
                if (camera.clearFlags != CameraClearFlags.Skybox && camera.clearFlags != CameraClearFlags.SolidColor)
                {
                    // WHY: a pooled target keeps whatever the last user drew into it, and a camera that clears only depth or nothing would composite its frame over that stale image.
                    RenderTexture.active = rendered;
                    GL.Clear(true, true, Color.clear);
                }

                camera.targetTexture = rendered;
                camera.Render();
                if (hdr)
                {
                    UnityEngine.Graphics.Blit(rendered, target);
                }
            }
            finally
            {
                camera.targetTexture = previousTarget;
                RenderTexture.active = previousActive;
                if (hdr)
                {
                    RenderTexture.ReleaseTemporary(rendered);
                }
            }
        }

        private static byte[] Encode(NativeArray<byte> pixels, GraphicsFormat graphicsFormat, int width, int height,
            CaptureImageFormat format, int jpegQuality)
        {
            if (format == CaptureImageFormat.Png)
            {
                // WHY: a camera's background alpha is whatever its clear colour says (often near zero); the sync path reads RGB only, so the PNG is made opaque to match it.
                NativeArray<byte> opaque = new(pixels.Length, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);
                try
                {
                    NativeArray<byte>.Copy(pixels, opaque);
                    for (int i = 3; i < opaque.Length; i += 4)
                    {
                        opaque[i] = 255;
                    }

                    NativeArray<byte> png = ImageConversion.EncodeNativeArrayToPNG(opaque, graphicsFormat,
                        (uint)width, (uint)height);
                    try
                    {
                        return png.ToArray();
                    }
                    finally
                    {
                        png.Dispose();
                    }
                }
                finally
                {
                    opaque.Dispose();
                }
            }

            NativeArray<byte> jpg = ImageConversion.EncodeNativeArrayToJPG(pixels, graphicsFormat, (uint)width,
                (uint)height, 0, Mathf.Clamp(jpegQuality, 1, 100));
            try
            {
                return jpg.ToArray();
            }
            finally
            {
                jpg.Dispose();
            }
        }
    }
}
