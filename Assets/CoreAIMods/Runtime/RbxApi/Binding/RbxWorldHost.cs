using System;
using CoreAI.Logging;
using CoreAI.Mods.Rbx.Datatypes;
using CoreAI.Mods.Rbx.Spatial;
using CoreAI.Mods.Rbx.Instances;
using CoreAI.Mods.Rbx.Instances.Adornments;
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

        [SerializeField]
        [Tooltip("Let the world's Lighting service drive the sun light, the ambient colours and the sky once a " +
                 "script (or a saved world) sets them. Off keeps the scene's own lighting whatever scripts do.")]
        private bool _driveSceneLighting = true;

        [SerializeField]
        [Tooltip("Directional light Lighting.ClockTime turns. Empty = RenderSettings.sun, then the scene's " +
                 "first directional light, resolved at the first Lighting change.")]
        private Light _sunLight;

        [SerializeField]
        [Tooltip("Draw PointLight instances as Unity lights on their parent parts.")]
        private bool _drawLights = true;

        [SerializeField]
        [Tooltip("Most PointLights on at once; the nearest to the camera stay on. 0 = the tier default " +
                 "(64 on desktop Forward+, 8 on WebGL).")]
        [Min(0)]
        private int _lightBudget;

        [SerializeField]
        [Tooltip("Play Sound instances through pooled AudioSources. Off keeps every sound's timeline running silently.")]
        private bool _playSounds = true;

        [SerializeField]
        [Tooltip("Most sounds heard at once; the loudest are heard. 0 = the tier default (32 on desktop, 16 on WebGL).")]
        [Min(0)]
        private int _soundBudget;

        [SerializeField]
        [Tooltip("Draw ParticleEmitter instances as pooled Unity particle systems on their parent parts.")]
        private bool _drawParticles = true;

        [SerializeField]
        [Tooltip("Draw Highlight instances as a wireframe box around each part instead of the silhouette outline. The "
                 + "wireframe is used anyway where the outline shader is not supported.")]
        private bool _highlightWireframe;

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

        /// <summary>
        /// Who owns the input: host UI (chat, Hub, menus) claims it under its own id with
        /// <see cref="InputFocusState.SetCaptured"/>, and while it is held UserInputService fires every
        /// event with <c>gameProcessedEvent = true</c>. Survives world replacement.
        /// </summary>
        public InputFocusState InputFocus { get; } = new();

        /// <summary>The host cursor behind <c>UserInputService.MouseBehavior</c>.</summary>
        public IRbxCursorPort CursorPort { get; private set; }

        /// <summary>Real raycasts, per-body gravity and contact events for the bound world.</summary>
        public UnityRbxPhysicsPort PhysicsPort { get; private set; }

        /// <summary>Rendering camera resolved once for this host, or null in headless scenes.</summary>
        public Camera SceneCamera { get; private set; }

        /// <summary>Draws and collides the world's voxel Terrain and resolves rays that hit it.</summary>
        public Terrain.RbxTerrainPresenter TerrainPresenter { get; private set; }

        /// <summary>The sink driving the scene's sun and ambient from Lighting; null when switched off.</summary>
        public UnityRbxLightingSink LightingSink { get; private set; }

        /// <summary>Keeps PointLight instances drawn as Unity lights; null when switched off.</summary>
        public RbxLightReconciler Lights { get; private set; }

        /// <summary>The Unity lights behind <see cref="Lights"/>; null when switched off.</summary>
        public UnityRbxLightBackend LightBackend { get; private set; }

        /// <summary>Keeps SelectionBox instances drawn around their adornees; null before a world is bound.</summary>
        public RbxSelectionBoxReconciler SelectionBoxes { get; private set; }

        /// <summary>The Unity objects behind <see cref="SelectionBoxes"/>.</summary>
        public UnityRbxSelectionBoxBackend SelectionBoxBackend { get; private set; }

        /// <summary>Keeps Highlight instances drawn over their objects; null before a world is bound.</summary>
        public RbxHighlightReconciler Highlights { get; private set; }

        /// <summary>The draws behind <see cref="Highlights"/>.</summary>
        public UnityRbxHighlightBackend HighlightBackend { get; private set; }

        /// <summary>
        /// The sounds this host provides to every world it runs (MVP17): <c>Sound.SoundId</c> names a key registered
        /// here, or a path under a <c>Resources</c> folder; <c>SoundService:GetSoundIds()</c> lists the keys. It starts
        /// empty: a game registers its own clips.
        /// </summary>
        public RbxSoundLibrary Sounds { get; } = new(UnityRbxAudioBackend.TryLoadFromResources);

        /// <summary>Keeps Sound instances audible through <see cref="AudioBackend"/>; null when switched off.</summary>
        public RbxSoundReconciler SoundReconciler { get; private set; }

        /// <summary>The pooled AudioSources behind <see cref="SoundReconciler"/>; null when switched off.</summary>
        public UnityRbxAudioBackend AudioBackend { get; private set; }

        /// <summary>The sound budget of this host's tier, or the configured override.</summary>
        public RbxSoundBudget SoundBudget => _soundBudget > 0
            ? new RbxSoundBudget("configured", _soundBudget)
            : Application.platform == RuntimePlatform.WebGLPlayer ? RbxSoundBudget.WebGl : RbxSoundBudget.Desktop;

        /// <summary>Registers <paramref name="clip"/> as the sound <paramref name="key"/> for every world of this host.</summary>
        public void RegisterSound(string key, AudioClip clip)
        {
            if (clip == null)
            {
                throw new ArgumentNullException(nameof(clip));
            }

            Sounds.Register(key, clip.length, clip);
        }

        /// <summary>Keeps ParticleEmitter instances drawn as Unity particle systems; null when switched off.</summary>
        public RbxParticleReconciler ParticleReconciler { get; private set; }

        /// <summary>The particle systems behind <see cref="ParticleReconciler"/>; null when switched off.</summary>
        public UnityRbxParticleBackend ParticleBackend { get; private set; }

        /// <summary>The particle budget of this host's tier: 64 emitters of 1,000 particles on desktop, 16 of 200 on WebGL.</summary>
        public RbxParticleBudget ParticleBudget => Application.platform == RuntimePlatform.WebGLPlayer
            ? RbxParticleBudget.WebGl
            : RbxParticleBudget.Desktop;

        /// <summary>The light budget of this host's tier, or the configured override.</summary>
        public RbxLightBudget LightBudget => _lightBudget > 0
            ? new RbxLightBudget("configured", _lightBudget)
            : Application.platform == RuntimePlatform.WebGLPlayer ? RbxLightBudget.WebGl : RbxLightBudget.Desktop;

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

        /// <summary>
        /// Chooses the camera the world drives (workspace.CurrentCamera, click picking) instead of the
        /// serialized one or <c>Camera.main</c>; null restores that fallback. Call it before
        /// <see cref="Initialize"/>: the camera rig and the pick source are built there and the Rbx API
        /// holds them from composition on, so a different camera afterwards is refused rather than
        /// half-applied. Passing the camera the world already drives is a no-op at any time.
        /// </summary>
        public void SetCamera(Camera camera)
        {
            if (IsInitialized)
            {
                if (camera == SceneCamera || (camera == null && _camera == null))
                {
                    return;
                }

                throw new InvalidOperationException(
                    "RbxWorldHost.SetCamera must be called before Initialize: this world already drives '"
                    + (SceneCamera != null ? SceneCamera.name : "no camera")
                    + "' and the Rbx API holds its camera rig. Set the camera first, or assign it in the "
                    + "inspector.");
            }

            _camera = camera;
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
            CursorPort ??= new UnityCursorPort();
            InputSource = new UnityNewInputSource(InputFocus, CursorPort);
            PhysicsPort = new UnityRbxPhysicsPort(Binder);
            TerrainPresenter = Terrain.RbxTerrainPresenter.Attach(gameObject, Game, Logger);
            PhysicsPort.TerrainHits = TerrainPresenter;
            if (PickSource is UnityClickPickSource unityPick)
            {
                unityPick.TerrainHits = TerrainPresenter;
            }

            AttachLighting();
            AttachEffects();
        }

        private void LateUpdate()
        {
            // WHY after Update, like the lights: the boxes follow the parts the scripts and the physics just moved.
            SelectionBoxes?.Tick();
            Highlights?.Tick();
            HighlightBackend?.Draw();
            TickEffects();
            if (Lights == null)
            {
                return;
            }

            // WHY after Update: the Lua frame (and every PointLight write it makes) runs in the tick
            // driver's Update, so the lights drawn this frame are the ones the scripts just left.
            RbxVector3 viewer = CameraRig != null ? CameraRig.GetCFrame().Position : new RbxVector3(0f, 0f, 0f);
            Lights.Tick(viewer);
        }

        /// <summary>
        /// Hands the current world's Lighting service the scene sink and builds the light reconciler over
        /// the current registry and binder; the previous world's are released first.
        /// </summary>
        private void AttachLighting()
        {
            DetachLighting();
            if (_driveSceneLighting && Game?.FindFirstChildOfClass("Lighting") is RbxLighting lighting)
            {
                LightingSink ??= new UnityRbxLightingSink(_sunLight);
                lighting.AttachSink(LightingSink);
            }

            if (_drawLights && Registry != null && Binder != null)
            {
                InstanceGameObjectBinder binder = Binder;
                LightBackend = new UnityRbxLightBackend(binder, Registry);
                Lights = new RbxLightReconciler(Registry, LightBackend, binder.GetLivePositionStuds,
                    LightBudget, message => Logger.Warn(message, LogTag.World));
            }

            if (Registry != null && Binder != null)
            {
                InstanceGameObjectBinder boxBinder = Binder;
                SelectionBoxBackend = new UnityRbxSelectionBoxBackend();
                SelectionBoxes = new RbxSelectionBoxReconciler(Registry, SelectionBoxBackend,
                    (InstanceId id, out RbxCFrame cframe, out RbxVector3 size) =>
                    {
                        PartProperties part = boxBinder.GetPartPropertiesOrDefault(id);
                        cframe = part.CFrame;
                        size = part.Size;
                        return true;
                    });
            }

            if (Registry != null && Binder != null)
            {
                HighlightBackend = new UnityRbxHighlightBackend(Binder, _highlightWireframe);
                Highlights = new RbxHighlightReconciler(Registry, HighlightBackend);
            }
        }

        private void DetachLighting()
        {
            if (Game?.FindFirstChildOfClass("Lighting") is RbxLighting lighting
                && ReferenceEquals(lighting.Sink, LightingSink))
            {
                lighting.AttachSink(null);
            }

            LightingSink?.Revert();
            Lights?.Dispose();
            Lights = null;
            LightBackend?.Dispose();
            LightBackend = null;
            SelectionBoxes?.Dispose();
            SelectionBoxes = null;
            SelectionBoxBackend?.Dispose();
            SelectionBoxBackend = null;
            Highlights?.Dispose();
            Highlights = null;
            HighlightBackend?.Dispose();
            HighlightBackend = null;
        }

        /// <summary>
        /// Hands the current world's SoundService this host's <see cref="Sounds"/> and builds the sound and particle
        /// reconcilers over the current registry (MVP17); the previous world's voices and emitters are released first.
        /// </summary>
        private void AttachEffects()
        {
            DetachEffects();
            EnsureSoundLibraryAttached();
            if (Registry == null || Binder == null || Game == null)
            {
                return;
            }

            InstanceGameObjectBinder binder = Binder;
            if (_playSounds)
            {
                AudioBackend = new UnityRbxAudioBackend(transform);
                SoundReconciler = new RbxSoundReconciler(Registry, Game, AudioBackend, binder.GetLivePositionStuds,
                    SoundBudget, message => Logger.Warn(message, LogTag.World));
                EnsureAudioListener();
            }

            if (_drawParticles)
            {
                RbxParticleBudget particleBudget = ParticleBudget;
                ParticleBackend = new UnityRbxParticleBackend(binder, Registry, transform, particleBudget);
                ParticleReconciler = new RbxParticleReconciler(Registry, ParticleBackend, binder.GetLivePositionStuds,
                    id => binder.GetPartPropertiesOrDefault(id).Size, particleBudget,
                    message => Logger.Warn(message, LogTag.World));
            }
        }

        private void DetachEffects()
        {
            SoundReconciler?.Dispose();
            SoundReconciler = null;
            AudioBackend?.Dispose();
            AudioBackend = null;
            ParticleReconciler?.Dispose();
            ParticleReconciler = null;
            ParticleBackend?.Dispose();
            ParticleBackend = null;
        }

        private void TickEffects()
        {
            EnsureSoundLibraryAttached();
            // WHY the camera: SoundService.ListenerType defaults to Camera, so the world is heard from it, and the
            // particle budget keeps the emitters nearest to it.
            RbxVector3 viewer = CameraRig != null ? CameraRig.GetCFrame().Position : new RbxVector3(0f, 0f, 0f);
            SoundReconciler?.Tick(viewer);
            ParticleReconciler?.Tick(viewer);
        }

        /// <summary>
        /// A world restored without a SoundService gets one from the Lua bindings after this host attached, so the
        /// library is handed over whenever the service has none.
        /// </summary>
        private void EnsureSoundLibraryAttached()
        {
            if (Game != null && !Game.IsDestroyed
                             && Game.FindFirstChildOfClass("SoundService") is RbxSoundService service
                             && service.Library == null)
            {
                service.AttachLibrary(Sounds);
            }
        }

        /// <summary>Gives the scene camera an AudioListener when the scene has none, so a played sound is heard.</summary>
        private void EnsureAudioListener()
        {
            if (!Application.isPlaying || SceneCamera == null || SceneCamera.GetComponent<AudioListener>() != null)
            {
                return;
            }

            // WHY a one-time search: a scene usually brings its own listener elsewhere (a player rig), and a second
            // one would make Unity warn every frame.
            if (UnityEngine.Object.FindAnyObjectByType<AudioListener>() == null)
            {
                SceneCamera.gameObject.AddComponent<AudioListener>();
            }
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

            DetachLighting();
            DetachEffects();
            InstanceGameObjectBinder outgoingBinder = Binder;
            InstanceRegistry outgoingRegistry = Registry;
            RbxDataModel outgoingGame = Game;
            GameObject outgoingRoot = _ownedSessionRoot;

            Registry = registry;
            Game = game;
            Binder = binder;
            Binder.MaterialVariantSource =
                game.GetService("MaterialService") as IRbxMaterialVariantSource;
            // WHY the old port is disposed here: it still listens to the outgoing binder, and a
            // stale listener would relay contacts from a world that no longer exists.
            PhysicsPort?.Dispose();
            PhysicsPort = new UnityRbxPhysicsPort(binder);
            TerrainPresenter?.Bind(
                CoreAI.Mods.Rbx.Instances.Terrain.RbxTerrainBootstrap.Find(game.FindFirstChildOfClass("Workspace")));
            PhysicsPort.TerrainHits = TerrainPresenter;
            CameraRig = cameraRig;
            // WHY: a replacement world is staged with a fresh source that knows no host ports; the
            // focus claims and the cursor lock belong to the host, not to the world being replaced.
            if (inputSource is UnityNewInputSource unitySource)
            {
                unitySource.AdoptHostPorts(InputFocus, CursorPort);
            }

            InputSource = inputSource;
            PickSource = pickSource;
            if (pickSource is UnityClickPickSource replacementPick)
            {
                replacementPick.TerrainHits = TerrainPresenter;
            }

            _metersPerStud = metersPerStud;
            _ownedSessionRoot = sessionRoot;
            Registry.Diagnostics = message => Logger.Error(message, LogTag.World);
            AttachLighting();
            AttachEffects();

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
            DetachLighting();
            DetachEffects();
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
