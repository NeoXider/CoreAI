using CoreAI.Mods.Roblox.Spatial;
using CoreAI.Mods.Roblox.Instances;
using UnityEngine;

namespace CoreAI.Mods.Roblox.Binding
{
    /// <summary>
    /// Scene entry point owning one Roblox world: configures RobloxSpace, wires the
    /// GameObject binder into a fresh InstanceRegistry, and bootstraps the canonical game
    /// tree. Explicit entry-point component per ARCHITECTURE_RULES.md §2 — no static
    /// singleton, no scene reflection; composition roots reference it directly and read
    /// <see cref="Registry"/>/<see cref="Game"/>/<see cref="Binder"/>.
    /// </summary>
    [ExecuteAlways]
    public sealed class RobloxWorldHost : MonoBehaviour
    {
        [SerializeField]
        [Tooltip("Meters per stud (D3, LOCKED default 0.28). Constant for the whole session.")]
        private float _metersPerStud = RobloxSpace.DefaultMetersPerStud;

        public InstanceRegistry Registry { get; private set; }

        public RbxDataModel Game { get; private set; }

        public InstanceGameObjectBinder Binder { get; private set; }

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

            RobloxSpace.Configure(_metersPerStud);
            Binder = new InstanceGameObjectBinder(transform);
            Registry = new InstanceRegistry(null, Binder);
            Game = DataModelBootstrap.CreateGame(Registry);
        }

        private void OnDestroy()
        {
            // WHY: registry-driven teardown releases backing GameObjects through the binder,
            // keeping the ledger and the scene consistent even on scene unload.
            Game?.Destroy();
            Game = null;
            Registry = null;
            Binder = null;
        }
    }
}
