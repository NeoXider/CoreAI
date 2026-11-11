using System;
using System.Collections.Generic;
using System.Threading;
using CoreAI.Vision;
using Cysharp.Threading.Tasks;
using UnityEngine;

namespace CoreAI.Tests.EditMode
{
    /// <summary>
    /// A readback that never touches the GPU: returns a fixed JPEG-shaped payload synchronously and records what it
    /// was asked for, so capture tests run without a render pipeline and without waiting for a frame.
    /// </summary>
    internal sealed class FakeCameraFrameReadback : ICameraFrameReadback
    {
        /// <summary>A minimal JPEG start/end-of-image sequence; enough for the tool, never decoded.</summary>
        public static readonly byte[] JpegBytes =
            { 0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46, 0x00, 0x01, 0xFF, 0xD9 };

        public int Calls { get; private set; }
        public Camera LastCamera { get; private set; }
        public int LastWidth { get; private set; }
        public int LastHeight { get; private set; }
        public CaptureImageFormat LastFormat { get; private set; }
        public CameraReadbackMode Mode { get; set; } = CameraReadbackMode.AsyncGpu;

        /// <summary>When set, the capture fails with this exception instead of returning bytes.</summary>
        public Exception Failure { get; set; }

        public UniTask<CameraFrameReadbackResult> CaptureAsync(Camera camera, int width, int height,
            CaptureImageFormat format, int jpegQuality, CancellationToken cancellationToken)
        {
            Calls++;
            LastCamera = camera;
            LastWidth = width;
            LastHeight = height;
            LastFormat = format;
            if (Failure != null)
            {
                return UniTask.FromException<CameraFrameReadbackResult>(Failure);
            }

            return UniTask.FromResult(new CameraFrameReadbackResult(JpegBytes, Mode, 0, 0.0));
        }
    }

    /// <summary>A rig that records every request and answers with a canned result instead of moving anything.</summary>
    internal sealed class RecordingCameraRig : IAgentCameraRig
    {
        public List<CameraLookRequest> Looks { get; } = new();
        public List<CameraOrbitRequest> Orbits { get; } = new();
        public Camera LastCamera { get; private set; }

        public CameraRigResult Look(Camera camera, CameraLookRequest request)
        {
            LastCamera = camera;
            Looks.Add(request);
            return CameraRigResult.Looked(false, null, Vector3.zero);
        }

        public CameraRigResult Orbit(Camera camera, CameraOrbitRequest request)
        {
            LastCamera = camera;
            Orbits.Add(request);
            return CameraRigResult.Orbited(request.Target, new Vector3(1f, 2f, 3f), request.Yaw ?? 0f,
                request.Pitch ?? 0f, request.Distance ?? 1f);
        }
    }

    /// <summary>A resolver that maps every name to one fixed point, so a rig test needs no scene objects.</summary>
    internal sealed class FixedCameraTargetResolver : ICameraTargetResolver
    {
        private readonly CameraTarget _target;

        public FixedCameraTargetResolver(CameraTarget target)
        {
            _target = target;
        }

        public List<string> Requested { get; } = new();

        public bool TryResolve(string objectName, Camera viewer, out CameraTarget target)
        {
            Requested.Add(objectName);
            target = _target;
            return true;
        }
    }
}
