using System;
using CoreAI.Mods.Rbx.Binding;
using UnityEngine;

namespace CoreAI.Mods.Performance
{
    /// <summary>
    /// A reference scene in a running Unity player or Editor: a camera and a light, the production scene host
    /// (<see cref="RbxWorldHost"/>: GameObject binder, registry, DataModel, camera, input and pick seams), the
    /// mod stack composed over it (<see cref="PerfModWorld"/>) and the per-frame driver.
    /// </summary>
    public sealed class PerfUnityWorld : IDisposable
    {
        private readonly GameObject _root;
        private readonly GameObject _cameraObject;
        private readonly GameObject _lightObject;
        private bool _disposed;

        /// <summary>Builds the empty world; the caller fills it (grid, mods) and lets frames run.</summary>
        public PerfUnityWorld(PerfRuntimeCounters counters)
        {
            // WHY the camera exists before the host: RbxWorldHost resolves Camera.main once, at Initialize,
            // exactly as a scene with a camera does.
            _cameraObject = new GameObject("CoreAI Perf Camera") { tag = "MainCamera" };
            Camera camera = _cameraObject.AddComponent<Camera>();
            camera.fieldOfView = 90f;
            camera.nearClipPlane = 0.3f;
            camera.farClipPlane = 1000f;
            // WHY straight down from 80 m with a 90 degree view: the 16,384-Part grid covers about
            // 108 m x 108 m (384 studs at 0.28 m), so every Part of every reference scene is in the frustum and
            // the render counters count the whole world, not a camera-dependent slice of it.
            _cameraObject.transform.SetPositionAndRotation(new Vector3(54f, 80f, -54f), Quaternion.Euler(90f, 0f, 0f));
            _lightObject = new GameObject("CoreAI Perf Light");
            Light light = _lightObject.AddComponent<Light>();
            light.type = LightType.Directional;
            _lightObject.transform.rotation = Quaternion.Euler(50f, -30f, 0f);

            _root = new GameObject("CoreAI Perf World");
            Host = _root.AddComponent<RbxWorldHost>();
            Host.Initialize();
            World = new PerfModWorld(Host.Registry, Host.Game, Host.Binder, counters, Host.CameraRig,
                Host.InputSource, Host.PickSource);
            Driver = _root.AddComponent<PerfFrameDriver>();
            Driver.Attach(World);
        }

        /// <summary>The production scene host.</summary>
        public RbxWorldHost Host { get; }

        /// <summary>The mod stack over the host's world.</summary>
        public PerfModWorld World { get; }

        /// <summary>Advances the scheduler every frame and times it.</summary>
        public PerfFrameDriver Driver { get; }

        /// <summary>GameObjects the binder materialized (Parts, containers).</summary>
        public int BoundObjects => Host.Binder != null ? Host.Binder.BoundCount : 0;

        /// <inheritdoc />
        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            Driver.Attach(null);
            World.Dispose();
            UnityEngine.Object.Destroy(_root);
            UnityEngine.Object.Destroy(_cameraObject);
            UnityEngine.Object.Destroy(_lightObject);
        }
    }
}
