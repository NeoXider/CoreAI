using CoreAI.Mods.Rbx.Spatial;
using CoreAI.Mods.Rbx.Instances;
using UnityEngine;

namespace CoreAI.Mods.Rbx.Binding
{
    /// <summary>
    /// Scene entry point owning one Roblox world: configures RbxSpace, wires the
    /// GameObject binder into a fresh InstanceRegistry, and bootstraps the canonical game
    /// tree. Explicit entry-point component per ARCHITECTURE_RULES.md §2 — no static
    /// singleton, no scene reflection; composition roots reference it directly and read
    /// <see cref="Registry"/>/<see cref="Game"/>/<see cref="Binder"/>.
    /// </summary>
    [ExecuteAlways]
    public sealed class RbxWorldHost : MonoBehaviour
    {
        [SerializeField]
        [Tooltip("Meters per stud (D3, LOCKED default 0.28). Constant for the whole session.")]
        private float _metersPerStud = RbxSpace.DefaultMetersPerStud;

        [SerializeField]
        [Tooltip("Camera driven by workspace.CurrentCamera / camera_set_cframe. Empty = Camera.main, " +
                 "resolved once at Initialize.")]
        private Camera _camera;

        public InstanceRegistry Registry { get; private set; }

        public RbxDataModel Game { get; private set; }

        public InstanceGameObjectBinder Binder { get; private set; }

        /// <summary>Camera seam for the Lua surface; null when the scene has no camera at all.</summary>
        public IRbxCameraRig CameraRig { get; private set; }

        /// <summary>Click-pick seam behind ClickDetector.MouseClick; null when the scene has no
        /// camera (nothing to raycast through).</summary>
        public IClickPickSource PickSource { get; private set; }

        /// <summary>Input seam behind game:GetService("UserInputService"); resolved once at
        /// Initialize like the camera rig.</summary>
        public IInputSource InputSource { get; private set; }

        public bool IsInitialized => Registry != null;

        private void Awake()
        {
            // WHY: ExecuteAlways exists so OnDestroy reliably fires for edit-mode-created
            // hosts too (Unity skips OnDestroy on never-awakened scripts), but bootstrap
            // stays a play-mode/explicit act — adding the component in the editor must not
            // silently spawn a world.
            if (Application.isPlaying)
            {
                Initialize();
            }
        }

        /// <summary>Idempotent; a composition root may call it before Awake ordering runs.</summary>
        public void Initialize()
        {
            if (IsInitialized)
            {
                return;
            }

            RbxSpace.Configure(_metersPerStud);
            Binder = new InstanceGameObjectBinder(transform);
            Registry = new InstanceRegistry(null, Binder);
            Registry.Diagnostics = Debug.LogError;
            Game = DataModelBootstrap.CreateGame(Registry);
            // WHY: the camera reference is resolved ONCE here at composition; Lua camera writes
            // must never pay a scene search per call.
            Camera sceneCamera = _camera != null ? _camera : Camera.main;
            CameraRig = sceneCamera != null ? new UnityCameraRig(sceneCamera.transform, Binder) : null;
            // WHY: ClickDetector picking rays through the SAME rendering camera; no camera means no
            // pick source, and the bindings fall back to the headless no-op.
            PickSource = sceneCamera != null ? new UnityClickPickSource(sceneCamera, Binder) : null;
            // TODO: com.unity.inputsystem may be removed — the source stays behind IInputSource so
            // only this composition line changes when the backend is swapped.
            InputSource = new UnityNewInputSource();
        }

        private void OnDestroy()
        {
            // WHY: registry-driven teardown releases backing GameObjects through the binder,
            // keeping the ledger and the scene consistent even on scene unload.
            Game?.Destroy();
            Game = null;
            Registry = null;
            Binder = null;
            CameraRig = null;
            PickSource = null;
            InputSource = null;
        }
    }
}
