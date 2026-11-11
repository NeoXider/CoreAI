using System;
using System.Collections.Generic;
using CoreAI.Infrastructure.Llm;
using CoreAI.Mods.Rbx.Binding;
using UnityEngine;
using VContainer.Unity;

namespace CoreAI.Composition
{
    /// <summary>
    /// A part of the one-click play world that another package or the game supplies: the overlay chat, the
    /// default controls and camera, the look profile, the default world content, or anything else. The
    /// bootstrap calls it twice, in the order the slots are listed on <see cref="CoreAiPlayWorldBootstrap"/>.
    /// </summary>
    public interface ICoreAiPlayWorldModule
    {
        /// <summary>
        /// Before any lifetime scope builds: the scopes exist but are inactive, so a module can configure
        /// them (for example add a child module or set an option) or add its own scene objects.
        /// </summary>
        void OnComposing(CoreAiPlayWorldComposition composition);

        /// <summary>After both scopes built their containers: resolve services, open UI, start play.</summary>
        void OnComposed(CoreAiPlayWorldComposition composition);
    }

    /// <summary>What the one-click play world is made of, handed to every <see cref="ICoreAiPlayWorldModule"/>.</summary>
    public sealed class CoreAiPlayWorldComposition
    {
        public CoreAiPlayWorldComposition(
            GameObject root,
            Camera camera,
            Light sun,
            RbxWorldHost worldHost,
            CoreAILifetimeScope coreScope,
            CoreAiModsLifetimeScope modsScope)
        {
            Root = root;
            Camera = camera;
            Sun = sun;
            WorldHost = worldHost;
            CoreScope = coreScope;
            ModsScope = modsScope;
        }

        /// <summary>The bootstrap's own GameObject; created objects are its children.</summary>
        public GameObject Root { get; }

        public Camera Camera { get; }

        /// <summary>The directional light; null when the bootstrap was told not to create one and none was set.</summary>
        public Light Sun { get; }

        public RbxWorldHost WorldHost { get; }

        public CoreAILifetimeScope CoreScope { get; }

        public CoreAiModsLifetimeScope ModsScope { get; }
    }

    /// <summary>
    /// Creates the objects of a play world the bootstrap was not given. The default makes plain Unity objects;
    /// a game or a test replaces it through <see cref="CoreAiPlayWorldBootstrap.Factory"/>.
    /// </summary>
    public interface ICoreAiPlayWorldFactory
    {
        Camera CreateCamera(Transform parent);

        Light CreateSun(Transform parent);

        RbxWorldHost CreateWorldHost(Transform parent);

        /// <summary>Creates the CoreAI scope on an inactive GameObject, so it builds only when activated.</summary>
        CoreAILifetimeScope CreateCoreScope(Transform parent);

        /// <summary>
        /// Creates the mods scope on an inactive GameObject, parented to <paramref name="coreScope"/> and wired
        /// to <paramref name="worldHost"/>, with <paramref name="settings"/> and <paramref name="modStoreId"/>.
        /// </summary>
        CoreAiModsLifetimeScope CreateModsScope(
            Transform parent,
            CoreAILifetimeScope coreScope,
            RbxWorldHost worldHost,
            CoreAiInstantEditSettings settings,
            string modStoreId);

        /// <summary>Activates a scope the factory created; its container builds now.</summary>
        void Activate(LifetimeScope scope);
    }

    /// <summary>
    /// The one-click play world: drop it into a scene (or let <c>CoreAI/Setup/Create Play + Create World</c>
    /// add it) and press Play. In a built player as in the Editor it composes the CoreAI scope, the mods scope
    /// with a wired <see cref="RbxWorldHost"/>, a camera and a sun when the scene has none, then the modules in
    /// its slots. Nothing on this path is editor-only, and every part it is given in the Inspector is used
    /// instead of a created one.
    /// </summary>
    /// <remarks>
    /// WHY a component and not a prefab or scene asset: it composes the same graph from code in every
    /// project, with no hand-written YAML to drift from the scopes it builds, and the menu only adds it.
    /// WHY it runs first (<see cref="DefaultExecutionOrderAttribute"/>): scene components that look for the
    /// scopes in their own Awake must find them already composed.
    /// </remarks>
    [DefaultExecutionOrder(-10000)]
    [DisallowMultipleComponent]
    public sealed class CoreAiPlayWorldBootstrap : MonoBehaviour
    {
        [Header("Use these instead of creating them (optional)")]
        [Tooltip("CoreAI scope to use; empty creates one. An assigned scope builds by itself.")]
        [SerializeField]
        private CoreAILifetimeScope coreScope;

        [Tooltip("Mods scope to use; empty creates one parented to the CoreAI scope and wired to the world host.")]
        [SerializeField]
        private CoreAiModsLifetimeScope modsScope;

        [Tooltip("World host to use; empty creates one. Used only for a mods scope the bootstrap creates.")]
        [SerializeField]
        private RbxWorldHost worldHost;

        [Tooltip("Camera to use; empty uses Camera.main, or creates one tagged MainCamera.")]
        [SerializeField]
        private Camera worldCamera;

        [Tooltip("Create a directional light when RenderSettings.sun is empty.")]
        [SerializeField]
        private bool createSunWhenMissing = true;

        [Header("Created mods scope")]
        [Tooltip("Mods store namespace of a created mods scope; empty shares the main game's store.")]
        [SerializeField]
        private string modStoreId = "";

        [Tooltip("Instant edit loop of a created mods scope.")]
        [SerializeField]
        private CoreAiInstantEditSettings instantEdit = new();

        [Header("Extension points (components implementing ICoreAiPlayWorldModule)")]
        [Tooltip("Default controls and camera (P1).")]
        [SerializeField]
        private MonoBehaviour controlsModule;

        [Tooltip("Look profile: sky, light, post-processing, material tier.")]
        [SerializeField]
        private MonoBehaviour lookModule;

        [Tooltip("Default world content, for example a baseplate and a spawn point.")]
        [SerializeField]
        private MonoBehaviour worldContentModule;

        [Tooltip("Overlay chat and agent HUD (P2).")]
        [SerializeField]
        private MonoBehaviour overlayChatModule;

        [Tooltip("Any further modules, run after the four slots above in this order.")]
        [SerializeField]
        private MonoBehaviour[] additionalModules = Array.Empty<MonoBehaviour>();

        [Tooltip("Compose in Awake while playing. Off: call Compose() from code.")]
        [SerializeField]
        private bool composeOnAwake = true;

        [NonSerialized]
        private readonly List<ICoreAiPlayWorldModule> _codeModules = new();

        /// <summary>Replaces how missing objects are created; set before <see cref="Compose"/>. Null uses the default.</summary>
        public ICoreAiPlayWorldFactory Factory { get; set; }

        /// <summary>Overrides the CoreAISettings lookup in tests; null asks <see cref="CoreAISettingsAsset.Instance"/>.</summary>
        internal Func<bool> SettingsAvailableForTests { get; set; }

        /// <summary>True once <see cref="Compose"/> succeeded.</summary>
        public bool IsComposed { get; private set; }

        /// <summary>The one actionable line that stopped the composition; empty when it did not fail.</summary>
        public string Failure { get; private set; } = "";

        /// <summary>What was composed; null before <see cref="Compose"/> succeeded.</summary>
        public CoreAiPlayWorldComposition Composition { get; private set; }

        /// <summary>The instant edit settings a created mods scope gets.</summary>
        public CoreAiInstantEditSettings InstantEdit => instantEdit ??= new CoreAiInstantEditSettings();

        /// <summary>Raised once the world is composed and every module ran.</summary>
        public event Action<CoreAiPlayWorldBootstrap> Composed;

        /// <summary>Adds a module from code; it runs after the Inspector slots. Call before <see cref="Compose"/>.</summary>
        public void AddModule(ICoreAiPlayWorldModule module)
        {
            if (module == null)
            {
                throw new ArgumentNullException(nameof(module));
            }

            if (IsComposed)
            {
                throw new InvalidOperationException("AddModule must be called before the play world composes.");
            }

            if (!_codeModules.Contains(module))
            {
                _codeModules.Add(module);
            }
        }

        private void Awake()
        {
            if (composeOnAwake && Application.isPlaying)
            {
                Compose();
            }
        }

        /// <summary>
        /// Composes the play world once. Answers false, with <see cref="Failure"/> set and logged once, when a
        /// prerequisite is missing; nothing is created then.
        /// </summary>
        public bool Compose()
        {
            if (IsComposed)
            {
                return true;
            }

            string missing = DescribeMissingPrerequisite();
            if (missing.Length > 0)
            {
                Fail(missing);
                return false;
            }

            List<ICoreAiPlayWorldModule> modules = CollectModules(out string moduleProblem);
            if (moduleProblem.Length > 0)
            {
                Fail(moduleProblem);
                return false;
            }

            ICoreAiPlayWorldFactory factory = Factory ?? new CoreAiPlayWorldDefaultFactory();
            Transform root = transform;
            Camera camera = worldCamera != null ? worldCamera : Camera.main;
            if (camera == null)
            {
                camera = factory.CreateCamera(root);
            }

            Light sun = RenderSettings.sun;
            if (sun == null)
            {
                // WHY: a scene made from Unity's templates has a Directional Light but leaves RenderSettings.sun
                // empty; a second, created sun doubled the daylight and kept every night lit, because the day
                // cycle drives only one of them.
                sun = FindSceneDirectionalLight();
                if (sun != null)
                {
                    RenderSettings.sun = sun;
                }
            }

            if (sun == null && createSunWhenMissing)
            {
                sun = factory.CreateSun(root);
            }

            // WHY the world host before the scopes: it is the one created object that runs on creation (its
            // Awake initializes the world over the camera resolved just above), while both scopes are created
            // inactive and build only on Activate, CoreAI first, then the mods scope wired to both.
            bool createdMods = modsScope == null;
            RbxWorldHost host = worldHost;
            if (createdMods && host == null)
            {
                host = factory.CreateWorldHost(root);
            }

            bool createdCore = coreScope == null;
            CoreAILifetimeScope core = createdCore ? factory.CreateCoreScope(root) : coreScope;
            CoreAiModsLifetimeScope mods = createdMods
                ? factory.CreateModsScope(core.transform, core, host, InstantEdit, modStoreId)
                : modsScope;
            if (!createdMods && host == null)
            {
                host = mods.RobloxWorldHost;
            }

            CoreAiPlayWorldComposition composition = new(gameObject, camera, sun, host, core, mods);
            Composition = composition;
            foreach (ICoreAiPlayWorldModule module in modules)
            {
                RunModule(module, composition, true);
            }

            if (createdCore)
            {
                factory.Activate(core);
            }

            if (createdMods)
            {
                factory.Activate(mods);
            }

            IsComposed = true;
            Failure = "";
            foreach (ICoreAiPlayWorldModule module in modules)
            {
                RunModule(module, composition, false);
            }

            RaiseComposed();
            return true;
        }

        /// <summary>The scene's first enabled directional light, or null.</summary>
        private static Light FindSceneDirectionalLight()
        {
            foreach (Light light in FindObjectsByType<Light>(FindObjectsInactive.Exclude, FindObjectsSortMode.None))
            {
                if (light.type == LightType.Directional && light.enabled)
                {
                    return light;
                }
            }

            return null;
        }

        /// <summary>
        /// Why the play world cannot compose here, as one line that says what to do; empty when it can. Only
        /// what a created scope needs is checked: an assigned scope is the host's own responsibility.
        /// </summary>
        internal string DescribeMissingPrerequisite()
        {
            bool settingsAvailable = SettingsAvailableForTests != null
                ? SettingsAvailableForTests()
                : CoreAISettingsAsset.Instance != null;
            if (coreScope == null && !settingsAvailable)
            {
                return "CoreAI play world: no CoreAISettings asset was found, so the CoreAI scope cannot start. "
                       + "Run CoreAI/Setup/Create Default Assets (it creates Assets/Resources/CoreAISettings), or "
                       + "assign a configured CoreAILifetimeScope to this bootstrap.";
            }

            return "";
        }

        private List<ICoreAiPlayWorldModule> CollectModules(out string problem)
        {
            problem = "";
            List<ICoreAiPlayWorldModule> modules = new();
            List<MonoBehaviour> slots = new() { controlsModule, lookModule, worldContentModule, overlayChatModule };
            if (additionalModules != null)
            {
                slots.AddRange(additionalModules);
            }

            foreach (MonoBehaviour slot in slots)
            {
                if (slot == null)
                {
                    continue;
                }

                if (!(slot is ICoreAiPlayWorldModule module))
                {
                    problem = "CoreAI play world: '" + slot.GetType().Name + "' in a module slot does not implement "
                              + "ICoreAiPlayWorldModule; remove it from the slot or implement the interface.";
                    return modules;
                }

                if (!modules.Contains(module))
                {
                    modules.Add(module);
                }
            }

            foreach (ICoreAiPlayWorldModule module in _codeModules)
            {
                if (!modules.Contains(module))
                {
                    modules.Add(module);
                }
            }

            return modules;
        }

        private void RunModule(ICoreAiPlayWorldModule module, CoreAiPlayWorldComposition composition, bool composing)
        {
            // WHY contained: one broken module must not leave the world half composed; its failure is said
            // once and the rest of the world still starts.
            try
            {
                if (composing)
                {
                    module.OnComposing(composition);
                }
                else
                {
                    module.OnComposed(composition);
                }
            }
            catch (Exception ex)
            {
                Debug.LogError("[CoreAI] Play world module " + module.GetType().Name + " failed in "
                               + (composing ? "OnComposing" : "OnComposed") + ": " + ex.Message, this);
            }
        }

        private void Fail(string message)
        {
            if (string.Equals(Failure, message, StringComparison.Ordinal))
            {
                return;
            }

            Failure = message;
            Debug.LogError("[CoreAI] " + message, this);
        }

        private void RaiseComposed()
        {
            Action<CoreAiPlayWorldBootstrap> handlers = Composed;
            if (handlers == null)
            {
                return;
            }

            foreach (Delegate handler in handlers.GetInvocationList())
            {
                try
                {
                    ((Action<CoreAiPlayWorldBootstrap>)handler)(this);
                }
                catch (Exception ex)
                {
                    Debug.LogError("[CoreAI] A play world Composed handler failed: " + ex.Message, this);
                }
            }
        }
    }

    /// <summary>The default <see cref="ICoreAiPlayWorldFactory"/>: plain Unity objects under the bootstrap.</summary>
    public sealed class CoreAiPlayWorldDefaultFactory : ICoreAiPlayWorldFactory
    {
        /// <inheritdoc />
        public Camera CreateCamera(Transform parent)
        {
            GameObject cameraObject = new("Main Camera");
            cameraObject.tag = "MainCamera";
            cameraObject.transform.SetParent(parent, false);
            cameraObject.transform.position = new Vector3(0f, 12f, -24f);
            cameraObject.transform.LookAt(new Vector3(0f, 2f, 0f));
            Camera camera = cameraObject.AddComponent<Camera>();
            cameraObject.AddComponent<AudioListener>();
            return camera;
        }

        /// <inheritdoc />
        public Light CreateSun(Transform parent)
        {
            GameObject sunObject = new("Sun");
            sunObject.transform.SetParent(parent, false);
            sunObject.transform.rotation = Quaternion.Euler(50f, -30f, 0f);
            Light sun = sunObject.AddComponent<Light>();
            sun.type = LightType.Directional;
            sun.intensity = 1.1f;
            sun.shadows = LightShadows.Soft;
            RenderSettings.sun = sun;
            return sun;
        }

        /// <inheritdoc />
        public RbxWorldHost CreateWorldHost(Transform parent)
        {
            GameObject hostObject = new("RbxWorld");
            hostObject.transform.SetParent(parent, false);
            return hostObject.AddComponent<RbxWorldHost>();
        }

        /// <inheritdoc />
        public CoreAILifetimeScope CreateCoreScope(Transform parent)
        {
            GameObject scopeObject = new("CoreAI");
            scopeObject.SetActive(false);
            scopeObject.transform.SetParent(parent, false);
            return scopeObject.AddComponent<CoreAILifetimeScope>();
        }

        /// <inheritdoc />
        public CoreAiModsLifetimeScope CreateModsScope(
            Transform parent,
            CoreAILifetimeScope coreScope,
            RbxWorldHost worldHost,
            CoreAiInstantEditSettings settings,
            string modStoreId)
        {
            GameObject scopeObject = new("CoreAI Mods");
            scopeObject.SetActive(false);
            scopeObject.transform.SetParent(parent, false);
            CoreAiModsLifetimeScope scope = scopeObject.AddComponent<CoreAiModsLifetimeScope>();
            scope.parentReference.Object = coreScope;
            scope.SetRobloxWorldHost(worldHost);
            if (settings != null)
            {
                scope.SetInstantEditSettings(settings);
            }

            scope.SetModStoreId(modStoreId ?? "");
            return scope;
        }

        /// <inheritdoc />
        public void Activate(LifetimeScope scope)
        {
            if (scope != null)
            {
                scope.gameObject.SetActive(true);
            }
        }
    }
}
