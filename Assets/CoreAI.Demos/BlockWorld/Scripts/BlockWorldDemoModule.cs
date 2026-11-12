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
    /// <c>Samples/Block World</c> mod folder on in this scene's mods store, so the folder's mods run in the published
    /// world and every later start rehydrates them, and it tells the Programmer on every turn where "here" is (the
    /// crosshair cell the build mod publishes), in the turn's world state. The game itself is the Lua in that folder;
    /// this module holds no rule of it.
    /// </summary>
    /// <remarks>
    /// WHY after the first world frame: the mods scope seeds the bundled mods and rehydrates the active ones in its
    /// startup sequence, and the session's store takes writes only once that world is published. The first frame of
    /// the published world is the earliest point both are true; on a fresh store the folder is then switched on and
    /// loaded at once, on every later start the rehydrate has already loaded it and this finds nothing to do, but for
    /// a mod the folder gained since the world was kept, which it installs and loads.
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
            + "workspace attribute BuildHereCell: a Vector3 of whole cell numbers, always set - the empty cell in front "
            + "of the block the player looks at (CrosshairPlaceCell), or, when the crosshair points at nothing within "
            + "reach, the first empty cell on the ground three cells in front of the player. Build up from it. A build "
            + "here is a Terrain write starting at that cell (Terrain:FillBlock or FillRegion over the cells, or "
            + "WriteVoxels); there is no need to survey or read the land first. The RbxApi skill's building section "
            + "builds a watchtower this way.";

        [Tooltip("Mod folder switched on in this scene's mods store at every start; its sub-folders are included.")]
        [SerializeField]
        private string modFolder = DefaultFolder;

        [Tooltip("Let this scene's mods create agents (AIService) and call models: the villagers need it. Off by "
                 + "default in every other scene.")]
        [SerializeField]
        private bool allowAgentsFromLua = true;

        [Tooltip("Keep the world between starts (the play world setting KeepWorldBetweenStarts): the blocks the player "
                 + "and the AI changed are there after a reload, and /save records the world now.")]
        [SerializeField]
        private bool keepWorldBetweenStarts = true;

        [Tooltip("Columns (64 studs each) the world streams around the player in a WebGL player; 0 keeps the mod's "
                 + "own default. Set as the workspace attribute BlockWorldViewRadius when the world has none (a kept "
                 + "world keeps the player's own choice from the graphics settings).")]
        [SerializeField]
        [Range(0, 16)]
        private int webGlViewRadius = 6;

        [Tooltip("The longest view radius a WebGL player may pick in the graphics settings, in columns; 0 keeps the "
                 + "world mod's own limit (16). Set as the workspace attribute BlockWorldMaxViewRadius.")]
        [SerializeField]
        [Range(0, 16)]
        private int webGlMaxViewRadius = 12;

        [Tooltip("Let the chat agent hand big jobs to a background Lua sub-agent and keep talking meanwhile "
                 + "(orchestrator mode, Team style), for this scene's CoreAI scope only. In play, /orchestrator off "
                 + "and /orchestrator on switch it.")]
        [SerializeField]
        private bool delegateBigJobs = true;

        [Tooltip("Let the bundled Lua chat mod (coreai_chat) talk to the player's assistant, so it can be turned on "
                 + "from the Hub's Mods page; while it runs, the C# chat overlay steps aside.")]
        [SerializeField]
        private bool allowLuaChat = true;

        [Tooltip("Turn the Lua chat mod on at the start instead of the C# chat overlay. Off: the mod is only "
                 + "installed, and the player's own choice in the Mods page is kept.")]
        [SerializeField]
        private bool useLuaChat;

        private const string LuaChatModId = "coreai_chat";

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
                mods.SetAllowPlayerAssistantFromLua(allowLuaChat || useLuaChat);
                mods.SetKeepWorldBetweenStarts(keepWorldBetweenStarts);
            }

            CoreAILifetimeScope core = composition?.CoreScope;
            if (delegateBigJobs && core != null && core.Container == null)
            {
                core.OrchestratorMode = new CoreAI.Ai.SubAgents.OrchestratorModeOptions { Enabled = true };
                core.DefaultSubAgents = new CoreAiDefaultSubAgents(new[]
                {
                    new DefaultSubAgentSpec(CoreAiDefaultSubAgents.ScripterId, BuiltInAgentRoleIds.Programmer,
                        "Builds in this Block World with Lua: terrain blocks from the workspace attribute "
                        + "BuildHereCell (Terrain:FillBlock, FillRegion, WriteVoxels), parts and mods with behaviour. "
                        + "Give it one whole job with sizes and places, for example 'build a 7x7 stone house with a "
                        + "door, starting at BuildHereCell'.",
                        new[] { BuiltInToolProfileIds.LuaBuilder })
                });
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
            // WHY again: a restored world is a new one, and the world mod follows the radius attributes while it runs.
            ApplyViewRadius(world);
            // WHY contained: a store that refuses a write must not throw out of Update every frame; the reason is
            // said once and the world plays on without the folder.
            try
            {
                ILuaModSourceStore store = container.Resolve<ILuaModSourceStore>();
                SwitchFolderOn(store);
                InstallLuaChat(store);
                ActorContext player = container.Resolve<IActorIdentityProvider>()
                    .GetActorContext(BuiltInAgentRoleIds.Programmer);
                LoadedByThisModule = session.CurrentConcreteRuntime.RehydrateFromStore(player, LuaCapabilities.All);
            }
            catch (Exception ex)
            {
                Fail("switching the folder '" + ModFolder + "' on failed: " + ex.Message);
            }
        }

        /// <summary>
        /// Installs the bundled Lua chat mod into the store and, with <see cref="useLuaChat"/>, turns it on.
        /// </summary>
        private void InstallLuaChat(ILuaModSourceStore store)
        {
            if (!allowLuaChat && !useLuaChat)
            {
                return;
            }

            // WHY seeded here: a world kept between starts restores its own exact set of mods, so a mod a later
            // build bundles is not in it; without this the Mods page would have no chat mod to turn on.
            ResourcesBundledModSource resources = new();
            List<BundledMod> chat = new();
            foreach (BundledMod mod in resources.Load())
            {
                if (string.Equals(mod.Id, LuaChatModId, StringComparison.Ordinal))
                {
                    chat.Add(mod);
                }
            }

            new BundledModSeeder(store, new IBundledModSource[] { new ListedBundledModSource(resources.Origin, chat) })
                .Seed();
            // WHY never switched off here: with useLuaChat off the choice is the player's, made in the Mods page.
            if (useLuaChat && store.TryLoad(LuaChatModId, out _, out LuaModManifest manifest) && manifest != null &&
                !manifest.Active)
            {
                store.SetActive(LuaChatModId, true);
            }
        }

        private void SwitchFolderOn(ILuaModSourceStore store)
        {
            _switchedOn.Clear();
            string folder = ModFolder;
            ResourcesBundledModSource resources = new();
            List<BundledMod> folderMods = new();
            foreach (BundledMod mod in resources.Load())
            {
                if (IsInFolder(LuaModHeader.Parse(mod.Source ?? "", mod.Id).Category, folder))
                {
                    folderMods.Add(mod);
                }
            }

            // WHY seeded here as well as at the start: a world kept between starts restores its own exact set of mods,
            // so a mod the folder gained in a later build is not in it; the seeder installs what is missing and keeps
            // its own rule for the rest (a newer version replaces, an equal one is left alone).
            new BundledModSeeder(store, new IBundledModSource[] { new ListedBundledModSource(resources.Origin, folderMods) })
                .Seed();
            List<string> unstored = new();
            foreach (BundledMod mod in folderMods)
            {
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

        /// <remarks>
        /// WHY a limit in WebGL: the world mod allows 16 columns, about 3.8 million cells, and a WebGL player's
        /// Terrain holds 4.2 million (<c>TerrainLimits.WebGl</c>), so a walk at that radius would soon fill it.
        /// </remarks>
        private void ApplyViewRadius(LuaCsRbxApiBindings world)
        {
            RbxInstance workspace = world?.Game?.FindFirstChildOfClass("Workspace");
            if (workspace == null || Application.platform != RuntimePlatform.WebGLPlayer)
            {
                return;
            }

            if (webGlMaxViewRadius > 0)
            {
                workspace.SetAttribute("BlockWorldMaxViewRadius", (double)webGlMaxViewRadius);
            }

            if (webGlViewRadius > 0 && workspace.GetAttribute("BlockWorldViewRadius") == null)
            {
                workspace.SetAttribute("BlockWorldViewRadius", (double)webGlViewRadius);
            }
        }

        /// <summary>The folder's bundled mods, already read, under the origin of the source they came from.</summary>
        private sealed class ListedBundledModSource : IBundledModSource
        {
            private readonly IReadOnlyList<BundledMod> _mods;

            public ListedBundledModSource(string origin, IReadOnlyList<BundledMod> mods)
            {
                Origin = origin;
                _mods = mods;
            }

            /// <inheritdoc />
            public string Origin { get; }

            /// <inheritdoc />
            public IReadOnlyList<BundledMod> Load()
            {
                return _mods;
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
        /// The line about "here" right now: the cell (<c>BuildHereCell</c>) and where it comes from (<c>BuildHereFrom</c>:
        /// <c>crosshair</c> or <c>ahead</c>); a line that says so when the build mod has published no cell yet.
        /// </summary>
        public static string DescribeHere(object cell, object from)
        {
            if (!(cell is RbxVector3 here))
            {
                return "Right now there is no BuildHereCell yet (the land in front of the player is still being made).";
            }

            string at = "(" + (int)here.X + ", " + (int)here.Y + ", " + (int)here.Z + ")";
            return Equals(from, "crosshair")
                ? "Right now \"here\" is BuildHereCell " + at + ", the empty cell in front of the block the player looks at."
                : "Right now \"here\" is BuildHereCell " + at + ", on the ground three cells in front of the player: the "
                  + "crosshair points at nothing within reach. Build there; do not survey the land.";
        }

        /// <summary>
        /// The Programmer's world state in this scene: <see cref="HereContext"/> and the "here" cell as it is now.
        /// </summary>
        /// <remarks>
        /// WHY copied on the main thread: a turn's prompt is built off it, and the world is read only from Unity's
        /// frame, so <see cref="Observe"/> keeps the last line and <see cref="BuildContext"/> only reads it.
        /// </remarks>
        internal sealed class HereContextProvider : IAgentRuntimeContextProvider
        {
            private volatile string _now = "";
            private object _lastCell;
            private object _lastFrom;

            /// <summary>Copies the "here" attributes of <paramref name="workspace"/> when they changed.</summary>
            public void Observe(RbxInstance workspace)
            {
                object cell = workspace?.GetAttribute("BuildHereCell");
                object from = workspace?.GetAttribute("BuildHereFrom");
                if (Equals(cell, _lastCell) && Equals(from, _lastFrom))
                {
                    return;
                }

                _lastCell = cell;
                _lastFrom = from;
                _now = DescribeHere(cell, from);
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
