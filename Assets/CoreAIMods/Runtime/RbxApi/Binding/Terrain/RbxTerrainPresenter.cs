using System;
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
    /// replaced, and swaps the renderer through <see cref="RendererFactory"/>.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class RbxTerrainPresenter : MonoBehaviour, IRbxTerrainHitResolver
    {
        [SerializeField]
        [Min(1)]
        [Tooltip("Most chunks re-meshed in one frame (at least one always runs while any is waiting).")]
        private int _maxChunksPerFrame = 32;

        [SerializeField]
        [Min(0f)]
        [Tooltip("Frame time the re-mesh pass may use, in milliseconds, before it leaves the rest for the next frame.")]
        private float _maxMillisecondsPerFrame = 6f;

        private ILog _log;

        /// <summary>The renderer of the bound Terrain; null while none is bound.</summary>
        public IRbxTerrainRenderer Renderer { get; private set; }

        /// <summary>
        /// Builds the renderer for a newly bound Terrain; null builds the default
        /// <see cref="RbxTerrainRenderer"/> under this component's transform.
        /// </summary>
        public Func<RbxTerrain, Transform, IRbxTerrainRenderer> RendererFactory { get; set; }

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

        /// <summary>Runs one budgeted re-mesh pass now; returns the chunks processed.</summary>
        public int Pump()
        {
            return Renderer?.ProcessDirty(_maxChunksPerFrame, _maxMillisecondsPerFrame) ?? 0;
        }

        private void LateUpdate()
        {
            Pump();
        }

        private void OnDestroy()
        {
            Renderer?.Dispose();
            Renderer = null;
        }
    }
}
