using System;
using CoreAI.Logging;
using CoreAI.Mods.Rbx.Datatypes;
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

        /// <summary>Rendering camera resolved once for this host, or null in headless scenes.</summary>
        public Camera SceneCamera { get; private set; }

        /// <summary>Scale owned by the currently published host session.</summary>
        public float MetersPerStud => _metersPerStud;

        public bool IsInitialized => Registry != null;

        private ILog _log;
        private GameObject _ownedSessionRoot;

        internal Action<UnityEngine.Object> RetiredRootDestroyerForTests { get; set; }

        // WHY: a scene component is created by Unity, not by the container, so it cannot be injected;
        // the process-wide CoreAI logger is the sanctioned no-DI seam and CoreServicesInstaller points
        // it at the scoped, settings-filtered logger at startup. SetLog upgrades it when a composition
        // root does resolve one.
        private ILog Logger => _log ??= Log.Instance;

        /// <summary>Supplies the composition-scoped logger this world reports diagnostics through.
        /// Safe before or after <see cref="Initialize"/> — an already-built binder is re-pointed too,
        /// so scene Awake ordering cannot decide which logger the world ends up with.</summary>
        public void SetLog(ILog log)
        {
            if (log == null)
            {
                return;
            }

            _log = log;
            Binder?.SetLog(log);
        }

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
            Binder = new InstanceGameObjectBinder(transform, Logger);
            WorldInstanceAdapter worldInstanceAdapter = new(Binder);
            Registry = new InstanceRegistry(null, Binder, worldInstanceAdapter: worldInstanceAdapter);
            // WHY: InstanceRegistry stays engine-free and only exposes an Action<string> hook, so the
            // Unity-side host is what adapts that hook onto the CoreAI logger — the registry never
            // learns about UnityEngine or the logging stack. Resolved per call, so a later SetLog wins.
            Registry.Diagnostics = message => Logger.Error(message, LogTag.World);
            Game = DataModelBootstrap.CreateGame(Registry);
            Binder.MaterialVariantSource =
                Game.GetService("MaterialService") as IRbxMaterialVariantSource;
            // WHY: the camera reference is resolved ONCE here at composition; Lua camera writes
            // must never pay a scene search per call.
            Camera sceneCamera = _camera != null ? _camera : Camera.main;
            SceneCamera = sceneCamera;
            CameraRig = sceneCamera != null ? new UnityCameraRig(sceneCamera.transform, Binder) : null;
            // WHY: ClickDetector picking rays through the SAME rendering camera; no camera means no
            // pick source, and the bindings fall back to the headless no-op.
            PickSource = sceneCamera != null ? new UnityClickPickSource(sceneCamera, Binder) : null;
            // TODO: com.unity.inputsystem may be removed — the source stays behind IInputSource so
            // only this composition line changes when the backend is swapped.
            InputSource = new UnityNewInputSource();
        }

        /// <summary>Publishes an already restored replacement and retires the outgoing host state.</summary>
        public void PublishReplacement(
            GameObject sessionRoot,
            InstanceGameObjectBinder binder,
            InstanceRegistry registry,
            RbxDataModel game,
            IRbxCameraRig cameraRig,
            IInputSource inputSource,
            IClickPickSource pickSource,
            float metersPerStud)
        {
            if (sessionRoot == null || binder == null || registry == null || game == null)
            {
                throw new ArgumentNullException(nameof(sessionRoot));
            }

            InstanceGameObjectBinder outgoingBinder = Binder;
            InstanceRegistry outgoingRegistry = Registry;
            RbxDataModel outgoingGame = Game;
            GameObject outgoingRoot = _ownedSessionRoot;

            Registry = registry;
            Game = game;
            Binder = binder;
            Binder.MaterialVariantSource =
                game.GetService("MaterialService") as IRbxMaterialVariantSource;
            CameraRig = cameraRig;
            InputSource = inputSource;
            PickSource = pickSource;
            _metersPerStud = metersPerStud;
            _ownedSessionRoot = sessionRoot;
            Registry.Diagnostics = message => Logger.Error(message, LogTag.World);

            try
            {
                outgoingBinder?.BeginHostTeardown();
            }
            catch (System.Exception ex)
            {
                Logger.Warn("[RbxWorldHost] Outgoing binder teardown failed: " + ex.Message);
            }

            try
            {
                outgoingRegistry?.MarkDetached();
            }
            catch (System.Exception ex)
            {
                Logger.Warn("[RbxWorldHost] Outgoing registry detach failed: " + ex.Message);
            }

            try
            {
                outgoingGame?.Destroy();
            }
            catch (System.Exception ex)
            {
                Logger.Warn("[RbxWorldHost] Outgoing world teardown failed: " + ex.Message);
            }

            try
            {
                if (outgoingRoot != null)
                {
                    Action<UnityEngine.Object> destroyer = RetiredRootDestroyerForTests;
                    if (destroyer != null)
                    {
                        destroyer(outgoingRoot);
                    }
                    else
                    {
                        DestroyUnityObject(outgoingRoot);
                    }
                }
            }
            catch (System.Exception ex)
            {
                try
                {
                    Logger.Warn("[RbxWorldHost] Outgoing root destroy failed: " + ex.Message);
                }
                catch
                {
                }
            }
        }

        private void OnDestroy()
        {
            Binder?.BeginHostTeardown();

            // WHY: the mod stack outlives this component — LuaCsRbxApiBindings captures the registry
            // once at install time, so clearing our own property hides nothing from running scripts.
            // Marking it detached is what turns the next Instance.new into a named WORLD_DETACHED
            // error instead of a PARENT_LOCKED about a Workspace that died with us.
            Registry?.MarkDetached();

            // WHY: registry-driven teardown releases backing GameObjects through the binder,
            // keeping the ledger and the scene consistent even on scene unload.
            Game?.Destroy();
            DestroyUnityObject(_ownedSessionRoot);
            Game = null;
            Registry = null;
            Binder = null;
            CameraRig = null;
            PickSource = null;
            InputSource = null;
            SceneCamera = null;
            _ownedSessionRoot = null;
        }

        private static void DestroyUnityObject(UnityEngine.Object target)
        {
            if (target == null)
            {
                return;
            }

            if (Application.isPlaying)
            {
                Destroy(target);
            }
            else
            {
                DestroyImmediate(target);
            }
        }
    }
}
