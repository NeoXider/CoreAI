using System;
using System.Collections.Generic;
using CoreAI.Logging;
using CoreAI.Mods.Rbx.Datatypes;
using CoreAI.Mods.Rbx.Instances;
using CoreAI.Mods.Rbx.Instances.Terrain;
using UnityEngine;

namespace CoreAI.Mods.Rbx.Binding.Terrain
{
    /// <summary>
    /// Scene entry point for <c>workspace.Terrain</c>: owns the world's <see cref="IRbxTerrainRenderer"/>
    /// and re-meshes changed chunks every <c>LateUpdate</c> under a per-frame budget, so a write a script
    /// makes during the frame is drawn and collides in that same frame. A host attaches it next to its
    /// world host with <see cref="Attach"/>, re-points it with <see cref="Bind"/> when the world is
    /// replaced, and swaps the renderer through <see cref="RendererFactory"/>. It also gives the world's
    /// part renderers the sky exposure of the cell they stand in (<see cref="RbxPartSkyExposurePass"/>),
    /// and builds queued colliders out of turn for bodies that need them
    /// (<see cref="IRbxTerrainColliderReadiness"/>).
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class RbxTerrainPresenter : MonoBehaviour, IRbxTerrainHitResolver, IRbxTerrainColliderReadiness
    {
        [SerializeField]
        [Min(1)]
        [Tooltip("Most chunks re-meshed in one frame (at least one always runs while any is waiting).")]
        private int _maxChunksPerFrame = 32;

        [SerializeField]
        [Min(0f)]
        [Tooltip("Frame time the re-mesh pass may use, in milliseconds, before it leaves the rest for the next frame.")]
        private float _maxMillisecondsPerFrame = 6f;

        [SerializeField]
        [Min(0)]
        [Tooltip("Part renderers given their sky exposure per frame (0 turns the pass off).")]
        private int _partExposuresPerFrame = 32;

        private readonly RbxPartSkyExposurePass _partExposure = new();
        private ILog _log;
        private RbxWorldHost _worldHost;
        private bool _worldHostResolved;

        /// <summary>The renderer of the bound Terrain; null while none is bound.</summary>
        public IRbxTerrainRenderer Renderer { get; private set; }

        /// <summary>
        /// Builds the renderer for a newly bound Terrain; null builds the default
        /// <see cref="RbxTerrainRenderer"/> under this component's transform.
        /// </summary>
        public Func<RbxTerrain, Transform, IRbxTerrainRenderer> RendererFactory { get; set; }

        /// <summary>
        /// Fills a list with the part renderers that take the sky exposure. Null uses the binder of the
        /// <see cref="RbxWorldHost"/> on this GameObject, when there is one.
        /// </summary>
        public Action<List<Renderer>> PartRendererSource { get; set; }

        /// <summary>Most chunks re-meshed per frame.</summary>
        public int MaxChunksPerFrame
        {
            get => _maxChunksPerFrame;
            set => _maxChunksPerFrame = Math.Max(1, value);
        }

        /// <summary>Re-mesh time budget per frame, in milliseconds.</summary>
        public float MaxMillisecondsPerFrame
        {
            get => _maxMillisecondsPerFrame;
            set => _maxMillisecondsPerFrame = Math.Max(0f, value);
        }

        /// <summary>Part renderers given their sky exposure per frame; 0 turns the pass off.</summary>
        public int PartExposuresPerFrame
        {
            get => _partExposuresPerFrame;
            set => _partExposuresPerFrame = Math.Max(0, value);
        }

        /// <summary>The part exposure pass this presenter runs (counters for tests and diagnostics).</summary>
        public RbxPartSkyExposurePass PartExposure => _partExposure;

        /// <summary>
        /// The presenter on <paramref name="host"/> (added when missing), bound to the Terrain of
        /// <paramref name="game"/>'s Workspace.
        /// </summary>
        public static RbxTerrainPresenter Attach(GameObject host, RbxDataModel game, ILog log = null)
        {
            if (host == null)
            {
                throw new ArgumentNullException(nameof(host));
            }

            RbxTerrainPresenter presenter = host.GetComponent<RbxTerrainPresenter>();
            if (presenter == null)
            {
                presenter = host.AddComponent<RbxTerrainPresenter>();
            }

            presenter._log = log ?? presenter._log;
            presenter.Bind(RbxTerrainBootstrap.Find(game?.FindFirstChildOfClass("Workspace")));
            return presenter;
        }

        /// <summary>
        /// Draws <paramref name="terrain"/> from now on, disposing the renderer of the previous one;
        /// null unbinds. Binding the Terrain already bound does nothing.
        /// </summary>
        public void Bind(RbxTerrain terrain)
        {
            if (Renderer != null && ReferenceEquals(Renderer.Terrain, terrain))
            {
                return;
            }

            Renderer?.Dispose();
            Renderer = null;
            if (terrain == null)
            {
                return;
            }

            Renderer = RendererFactory != null
                ? RendererFactory(terrain, transform)
                : new RbxTerrainRenderer(terrain, transform, log: _log ?? Log.Instance);
        }

        /// <summary>
        /// Asks the renderer bound right now, so a physics port handed this presenter keeps resolving
        /// Terrain hits across <see cref="Bind"/>; false while nothing is bound.
        /// </summary>
        public bool TryResolveHit(Collider collider, Vector3 pointUnity, Vector3 normalUnity,
            out InstanceId terrainId, out RbxMaterialId material, out RbxVector3 normalStuds)
        {
            if (Renderer != null)
            {
                return Renderer.TryResolveHit(collider, pointUnity, normalUnity, out terrainId, out material,
                    out normalStuds);
            }

            terrainId = default;
            material = default;
            normalStuds = default;
            return false;
        }

        /// <inheritdoc />
        public int EnsureColliders(Bounds unityBounds)
        {
            return Renderer is IRbxTerrainColliderReadiness readiness ? readiness.EnsureColliders(unityBounds) : 0;
        }

        /// <summary>Runs one budgeted re-mesh pass now; returns the chunks processed.</summary>
        public int Pump()
        {
            return Renderer?.ProcessDirty(_maxChunksPerFrame, _maxMillisecondsPerFrame) ?? 0;
        }

        /// <summary>Gives up to <see cref="PartExposuresPerFrame"/> part renderers their sky exposure now; returns them.</summary>
        public int PumpPartExposure()
        {
            if (_partExposuresPerFrame <= 0 || !(Renderer is RbxTerrainRenderer terrainRenderer))
            {
                return 0;
            }

            Action<List<Renderer>> source = PartRendererSource ?? HostPartRenderers;
            return _partExposure.Tick(terrainRenderer.SkyExposure, terrainRenderer.Terrain.Store.Revision, source,
                _partExposuresPerFrame);
        }

        private void HostPartRenderers(List<Renderer> renderers)
        {
            if (!_worldHostResolved)
            {
                _worldHostResolved = true;
                _worldHost = GetComponent<RbxWorldHost>();
            }

            if (_worldHost != null && _worldHost.Binder != null)
            {
                _worldHost.Binder.CollectPartRenderers(renderers);
            }
        }

        private void LateUpdate()
        {
            Pump();
            PumpPartExposure();
        }

        private void OnDestroy()
        {
            Renderer?.Dispose();
            Renderer = null;
        }
    }
}
