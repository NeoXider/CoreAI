using System;
using System.Collections.Generic;
using CoreAI.Ai;
using CoreAI.Ai.LuaCs;
using CoreAI.Authority;
using CoreAI.Composition;
using CoreAI.Infrastructure.Lua;
using CoreAI.Mods.Rbx.Datatypes;
using CoreAI.Mods.Rbx.Instances;
using CoreAI.Mods.WorldPackages;
using UnityEngine;
using VContainer;

namespace CoreAI.Demos.BlockWorld
{
    /// <summary>
    /// The Block World scene's own play world module: it turns the framework setting that lets mods create agents
    /// (<c>CoreAiLuaAiOptions.AllowAgentsFromLua</c>) on for this scene's mods scope only, and switches the bundled
    /// <c>Samples/Block World</c> mod folder on in this scene's mods store, so the eight mods run in the published
    /// world and every later start rehydrates them, and it tells the Programmer on every turn where "here" is (the
    /// crosshair cell the build mod publishes), in the turn's world state. The game itself is the Lua in that folder;
    /// this module holds no rule of it.
    /// </summary>
    /// <remarks>
    /// WHY after the first world frame: the mods scope seeds the bundled mods and rehydrates the active ones in its
    /// startup sequence, and the session's store takes writes only once that world is published. The first frame of
    /// the published world is the earliest point both are true; on a fresh store the folder is then switched on and
    /// loaded at once, on every later start the rehydrate has already loaded it and this finds nothing to do.
    /// </remarks>
    [DisallowMultipleComponent]
    public sealed class BlockWorldDemoModule : MonoBehaviour, ICoreAiPlayWorldModule
    {
        /// <summary>The mod folder (the <c>category:</c> of the bundled Block World mods) this scene switches on.</summary>
        public const string DefaultFolder = "Samples/Block World";

        /// <summary>
        /// What the Programmer is told on every turn of this scene (its world state): where "here" is. A model that
        /// has never seen the demo builds "here" from this; the RbxApi skill's building section has the recipe.
        /// </summary>
        public const string HereContext =
            "This game is the Block World: a voxel world in workspace.Terrain, in cells of 4 studs. \"Here\" is the "
            + "workspace attribute CrosshairPlaceCell: a Vector3 of whole cell numbers, the empty cell in front of the "
            + "block the player looks at; build up from it. CrosshairCell is the block looked at, CrosshairHasTarget "
            + "is false for the sky or beyond reach. A build here is a Terrain write starting at CrosshairPlaceCell "
            + "(Terrain:FillBlock or FillRegion over the cells, or WriteVoxels); there is no need to read the land "
            + "first. The RbxApi skill's building section builds a watchtower this way.";

        [Tooltip("Mod folder switched on in this scene's mods store at every start; its sub-folders are included.")]
        [SerializeField]
        private string modFolder = DefaultFolder;

        [Tooltip("Let this scene's mods create agents (AIService) and call models: the villagers need it. Off by "
                 + "default in every other scene.")]
        [SerializeField]
        private bool allowAgentsFromLua = true;

        [Tooltip("Columns (64 studs each) the world streams around the player in a WebGL player; 0 keeps the mod's "
                 + "own default. Set before the world mod loads, as the workspace attribute BlockWorldViewRadius.")]
        [SerializeField]
        [Range(0, 8)]
        private int webGlViewRadius = 6;

        private readonly List<string> _switchedOn = new();
        private readonly HereContextProvider _here = new();
        private CoreAiPlayWorldComposition _composition;
        private LuaCsRbxApiBindings _handledWorld;
        private AgentMemoryPolicy _policy;

        /// <summary>The mod folder switched on at every start.</summary>
        public string ModFolder => string.IsNullOrWhiteSpace(modFolder) ? DefaultFolder : modFolder.Trim();

        /// <summary>The folder's mod ids the last published world was given, in bundle order.</summary>
        public IReadOnlyList<string> SwitchedOn => _switchedOn;

        /// <summary>How many of them that world had to load itself (0 when the startup rehydrate already had).</summary>
        public int LoadedByThisModule { get; private set; }

        /// <summary>The one line that says why the folder could not be switched on; empty when it was.</summary>
        public string Failure { get; private set; } = "";

        /// <inheritdoc />
        public void OnComposing(CoreAiPlayWorldComposition composition)
        {
            CoreAiModsLifetimeScope mods = composition?.ModsScope;
            if (mods != null && mods.Container == null)
            {
                mods.SetAllowAgentsFromLua(allowAgentsFromLua);
            }
        }

        /// <inheritdoc />
        public void OnComposed(CoreAiPlayWorldComposition composition)
        {
            _composition = composition;
            IObjectResolver container = composition?.ModsScope?.Container;
            if (container == null)
            {
                Fail("the mods scope did not build, so there is no mods store to switch the folder on in.");
                return;
            }

            ApplyViewRadius(container.Resolve<RbxWorldRuntimeSessionController>().CurrentRbxApi);
            // WHY only when free: a role has one runtime context provider, and a game's own one wins.
            AgentMemoryPolicy policy = composition.CoreScope?.Container?.ResolveOrDefault<AgentMemoryPolicy>();
            if (policy != null && !policy.TryGetRuntimeContextProvider(BuiltInAgentRoleIds.Programmer, out _))
            {
                policy.SetRuntimeContextProvider(BuiltInAgentRoleIds.Programmer, _here);
                _policy = policy;
            }
        }

        private void OnDestroy()
        {
            if (_policy != null && _policy.TryGetRuntimeContextProvider(BuiltInAgentRoleIds.Programmer,
                    out IAgentRuntimeContextProvider current) && ReferenceEquals(current, _here))
            {
                _policy.ClearRuntimeContextProvider(BuiltInAgentRoleIds.Programmer);
            }

            _policy = null;
        }

        private void Update()
        {
            IObjectResolver container = _composition?.ModsScope?.Container;
            if (container == null)
            {
                return;
            }

            RbxWorldRuntimeSessionController session = container.Resolve<RbxWorldRuntimeSessionController>();
            LuaCsRbxApiBindings world = session.CurrentRbxApi;
            _here.Observe(world?.Game?.FindFirstChildOfClass("Workspace"));
            if (world == null || ReferenceEquals(world, _handledWorld) || world.Scheduler == null
                || world.Scheduler.FrameIndex == 0)
            {
                return;
            }

            _handledWorld = world;
            // WHY contained: a store that refuses a write must not throw out of Update every frame; the reason is
            // said once and the world plays on without the folder.
            try
            {
                SwitchFolderOn(container.Resolve<ILuaModSourceStore>());
                ActorContext player = container.Resolve<IActorIdentityProvider>()
                    .GetActorContext(BuiltInAgentRoleIds.Programmer);
                LoadedByThisModule = session.CurrentConcreteRuntime.RehydrateFromStore(player, LuaCapabilities.All);
            }
            catch (Exception ex)
            {
                Fail("switching the folder '" + ModFolder + "' on failed: " + ex.Message);
            }
        }

        private void SwitchFolderOn(ILuaModSourceStore store)
        {
            _switchedOn.Clear();
            string folder = ModFolder;
            List<string> unstored = new();
            foreach (BundledMod mod in new ResourcesBundledModSource().Load())
            {
                if (!IsInFolder(LuaModHeader.Parse(mod.Source ?? "", mod.Id).Category, folder))
                {
                    continue;
                }

                if (!store.TryLoad(mod.Id, out _, out LuaModManifest manifest) || manifest == null)
                {
                    unstored.Add(mod.Id);
                    continue;
                }

                if (!manifest.Active)
                {
                    store.SetActive(mod.Id, true);
                }

                _switchedOn.Add(mod.Id);
            }

            if (_switchedOn.Count == 0 || unstored.Count > 0)
            {
                Fail("the folder '" + folder + "' is not complete in this scene's mods store (not stored: "
                     + (unstored.Count == 0 ? "every bundled mod of it" : string.Join(", ", unstored))
                     + "; the store lists " + DescribeStore(store)
                     + "); the bundled Block World mods ship in Assets/CoreAIMods/Runtime/Resources/CoreAIMods/BlockWorld/.");
            }
        }

        private static string DescribeStore(ILuaModSourceStore store)
        {
            List<string> listed = new();
            foreach (LuaModManifest manifest in store.List())
            {
                listed.Add(manifest.Id + (manifest.Active ? "" : " (off)") + " in '" + manifest.Category + "'");
            }

            return listed.Count + " mods: " + string.Join(", ", listed);
        }

        private void ApplyViewRadius(LuaCsRbxApiBindings world)
        {
            if (webGlViewRadius <= 0 || Application.platform != RuntimePlatform.WebGLPlayer)
            {
                return;
            }

            RbxInstance workspace = world?.Game?.FindFirstChildOfClass("Workspace");
            if (workspace != null && workspace.GetAttribute("BlockWorldViewRadius") == null)
            {
                workspace.SetAttribute("BlockWorldViewRadius", (double)webGlViewRadius);
            }
        }

        private static bool IsInFolder(string category, string folder)
        {
            string value = (category ?? "").Trim();
            return string.Equals(value, folder, StringComparison.OrdinalIgnoreCase)
                   || value.StartsWith(folder + "/", StringComparison.OrdinalIgnoreCase);
        }

        private void Fail(string reason)
        {
            Failure = "Block World: " + reason;
            Debug.LogError("[CoreAI] " + Failure, this);
        }

        /// <summary>
        /// The Programmer's world state in this scene: <see cref="HereContext"/> and the crosshair cell as it is now.
        /// </summary>
        /// <remarks>
        /// WHY copied on the main thread: a turn's prompt is built off it, and the world is read only from Unity's
        /// frame, so <see cref="Observe"/> keeps the last line and <see cref="BuildContext"/> only reads it.
        /// </remarks>
        internal sealed class HereContextProvider : IAgentRuntimeContextProvider
        {
            private volatile string _now = "";
            private object _lastCell;
            private object _lastHasTarget;

            /// <summary>Copies the crosshair attributes of <paramref name="workspace"/> when they changed.</summary>
            public void Observe(RbxInstance workspace)
            {
                object cell = workspace?.GetAttribute("CrosshairPlaceCell");
                object hasTarget = workspace?.GetAttribute("CrosshairHasTarget");
                if (Equals(cell, _lastCell) && Equals(hasTarget, _lastHasTarget))
                {
                    return;
                }

                _lastCell = cell;
                _lastHasTarget = hasTarget;
                _now = cell is RbxVector3 place && Equals(hasTarget, true)
                    ? "Right now CrosshairPlaceCell is (" + (int)place.X + ", " + (int)place.Y + ", " + (int)place.Z + ")."
                    : "Right now the crosshair has no target.";
            }

            /// <inheritdoc />
            public string BuildContext(AiTaskRequest request, string roleId, string traceId)
            {
                string now = _now;
                return now.Length == 0 ? HereContext : HereContext + " " + now;
            }
        }
    }
}
