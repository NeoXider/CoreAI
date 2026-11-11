using System.Collections.Generic;
using CoreAI.Mods.Rbx.Instances;
using CoreAI.Mods.Rbx.Instances.Scripts;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode.RbxApi.Scripts
{
    /// <summary>
    /// MVP4 step C1-2, engine-free: the world side of script contexts. A durable placement creates a world-owned,
    /// bound instance (SC-1) at the SC-6 default placement or a <c>parent:</c> path, reuses and updates a bound one,
    /// adopts the unbound node a version 1 save leaves, undoes itself after a failed build, and a forgotten source's
    /// instance is destroyed or left unbound; the binding index sees restored bindings and never the synthetic legacy
    /// script. Every rule has its negative twin.
    /// </summary>
    [TestFixture]
    public sealed class ScriptInstanceHostEditModeTests
    {
        private InstanceRegistry _registry;
        private RbxDataModel _game;
        private ScriptInstanceHost _host;
        private List<string> _log;
        private List<string> _changes;

        [SetUp]
        public void SetUp()
        {
            _registry = new InstanceRegistry();
            _game = DataModelBootstrap.CreateGame(_registry);
            _log = new List<string>();
            _changes = new List<string>();
            _host = new ScriptInstanceHost(_registry, _game, _log.Add);
            _host.ScriptInstanceChanged += (sourceId, change) => _changes.Add(sourceId + ":" + change);
        }

        [TearDown]
        public void TearDown()
        {
            _host.Dispose();
        }

        private RbxInstance Service(string className)
        {
            return _game.FindFirstChildOfClass(className);
        }

        private static ScriptPlacementPlan Plan(string context, string parent = "", string[] folders = null,
            RbxLuaSourceContainer bound = null, string[] previousFolders = null)
        {
            ScriptPlacementRequest request = new()
            {
                SourceId = "door",
                HeaderContext = context,
                HeaderParent = parent,
                FolderSegments = folders,
                PreviousFolderSegments = previousFolders
            };
            if (bound != null)
            {
                request.HasBoundInstance = true;
                request.BoundClassName = bound.ClassName;
                request.BoundRunContext = bound is RbxBaseScript script ? script.RunContext : RunContextValues.Legacy;
            }

            Assert.IsTrue(ScriptPlacementPlanner.TryPlan(in request, out ScriptPlacementPlan plan, out RbxError refusal),
                refusal?.Message);
            return plan;
        }

        private InstanceRecord Record(RbxInstance instance)
        {
            Assert.IsTrue(_registry.TryGetRecord(instance.Id, out InstanceRecord record));
            return record;
        }

        private RbxInstance WorldPart(string name, RbxInstance parent, string ownerModId = null,
            string ownerActorId = null)
        {
            RbxInstance part = _registry.Create("Part", ownerModId, ownerActorId: ownerActorId);
            part.Name = name;
            part.Parent = parent;
            return part;
        }

        // ---- placement -----------------------------------------------------------------------

        [Test]
        public void C1_Place_DeclaredServer_CreatesAWorldOwnedBoundScriptAtTheDefaultPlacement()
        {
            ScriptInstancePlacement placement = _host.Place("door", Plan("server", folders: new[] { "Games", "Arena" }),
                "", true);

            RbxBaseScript script = placement.Script as RbxBaseScript;
            Assert.IsNotNull(script);
            Assert.IsTrue(placement.Created);
            Assert.AreEqual("door", script.Name);
            Assert.AreEqual("door", script.SourceId);
            Assert.AreEqual(RunContextValues.Server, script.RunContext);
            RbxInstance arena = script.Parent;
            Assert.AreEqual("Arena", arena.Name);
            Assert.AreEqual("Games", arena.Parent.Name);
            Assert.AreSame(Service("ServerScriptService"), arena.Parent.Parent);
            InstanceRecord record = Record(script);
            Assert.IsNull(record.OwnerModId, "the bound instance is world-owned (SC-1)");
            Assert.IsFalse(record.IsRuntimeInfrastructure, "it is saved with the world");
            Assert.AreEqual(OriginTag.FromMod("door"), record.OriginTag, "the ledger still names the mod");
            InstanceRecord folder = Record(arena);
            Assert.IsNull(folder.OwnerModId);
            Assert.IsNull(folder.OriginTag, "default folders belong to the host, like the world's own containers");
            Assert.IsTrue(_host.TryGetBoundScript("door", out RbxLuaSourceContainer found));
            Assert.AreSame(script, found);
            CollectionAssert.AreEqual(new[] { "door:Bound" }, _changes);
        }

        [Test]
        public void C1_Place_ARestrictedActorsScript_IsOwnedByThatActor()
        {
            RbxLuaSourceContainer script = _host.Place("door", Plan("client"), "alice", false).Script;

            Assert.AreSame(Service("ReplicatedStorage"), script.Parent);
            Assert.AreEqual("alice", Record(script).OwnerActorId);
            Assert.AreEqual(InstanceAccessScope.Owned, Record(script).AccessScope);
        }

        [Test]
        public void C1_Place_ASecondCreationForABoundSource_IsRefused()
        {
            _host.Place("door", Plan("server"), "", true);

            Assert.Throws<System.InvalidOperationException>(() => _host.Place("door", Plan("server"), "", true),
                "a source is bound to at most one instance (SC-1)");
        }

        [Test]
        public void C1_Place_ParentPath_PlacesTheScriptUnderTheResolvedInstance()
        {
            RbxInstance door = WorldPart("Door", Service("Workspace"));

            RbxLuaSourceContainer script = _host.Place("door", Plan("server", "Workspace/Door"), "", true).Script;

            Assert.AreSame(door, script.Parent, "script.Parent of a Script in a Part is that part (DoD row 3)");
        }

        [Test]
        public void C1_Place_UnresolvedParent_IsBadArgumentNamingTheSegment_AndCreatesNothing()
        {
            int before = _registry.GetLiveInstances().Count;

            RbxError error = Assert.Throws<RbxError>(() =>
                _host.Place("door", Plan("server", "Workspace/Gate/Hinge"), "", true));

            Assert.AreEqual(RbxErrorCode.BadArgument, error.Code);
            StringAssert.Contains("game.Workspace has no child named 'Gate'", error.Message);
            Assert.AreEqual(before, _registry.GetLiveInstances().Count);
            Assert.IsFalse(_host.TryGetBoundScript("door", out _));
        }

        [Test]
        public void C1_Place_AParentInsideAModsOwnSubtree_IsRefused()
        {
            RbxInstance model = _registry.Create("Model", "builder");
            model.Name = "Castle";
            model.Parent = Service("Workspace");
            WorldPart("Gate", model);

            RbxError error = Assert.Throws<RbxError>(() =>
                _host.Place("door", Plan("server", "Workspace/Castle/Gate"), "", true));

            StringAssert.Contains("belongs to mod 'builder'", error.Message);
        }

        [Test]
        public void C1_Place_AParentOwnedByAnotherActor_IsRefusedForARestrictedOwner_NotForTheHost()
        {
            WorldPart("BobsDoor", Service("Workspace"), ownerActorId: "bob");

            RbxError error = Assert.Throws<RbxError>(() =>
                _host.Place("door", Plan("server", "Workspace/BobsDoor"), "alice", false));
            StringAssert.Contains("belongs to actor 'bob'", error.Message);

            Assert.AreEqual("BobsDoor", _host.Place("door", Plan("server", "Workspace/BobsDoor"), "", true).Script
                .Parent.Name);
        }

        // ---- reuse ---------------------------------------------------------------------------

        [Test]
        public void C1_Reuse_ServerToClient_MovesADefaultPlacedScriptAndSetsItsRunContext()
        {
            RbxBaseScript script = (RbxBaseScript)_host.Place("door", Plan("server", folders: new[] { "Games" }), "",
                true).Script;

            ScriptInstancePlacement placement = _host.Place("door", Plan("client", folders: new[] { "Games" },
                bound: script), "", true);

            Assert.IsTrue(placement.Reused);
            Assert.AreSame(script, placement.Script, "the instance survives a context change of its class (SC-4)");
            Assert.AreEqual(RunContextValues.Client, script.RunContext);
            Assert.AreEqual("Games", script.Parent.Name);
            Assert.AreSame(Service("ReplicatedStorage"), script.Parent.Parent);
        }

        [Test]
        public void C1_Reuse_Negative_AScriptSomeoneMoved_StaysWhereItIs()
        {
            RbxBaseScript script = (RbxBaseScript)_host.Place("door", Plan("server"), "", true).Script;
            RbxInstance door = WorldPart("Door", Service("Workspace"));
            script.Parent = door;

            _host.Place("door", Plan("client", bound: script), "", true);

            Assert.AreSame(door, script.Parent, "only a default-placed instance follows its context");
            Assert.AreEqual(RunContextValues.Client, script.RunContext);
        }

        [Test]
        public void C1_Reuse_ANewFolder_MovesTheDefaultPlacedScript()
        {
            RbxBaseScript script = (RbxBaseScript)_host.Place("door", Plan("server", folders: new[] { "Old" }), "",
                true).Script;

            _host.Place("door", Plan("server", folders: new[] { "New" }, bound: script,
                previousFolders: new[] { "Old" }), "", true);

            Assert.AreEqual("New", script.Parent.Name);
        }

        // ---- undo ----------------------------------------------------------------------------

        [Test]
        public void C1_Undo_ACreatedPlacement_DestroysTheScriptAndTheFoldersItMade()
        {
            ScriptInstancePlacement placement = _host.Place("door", Plan("server", folders: new[] { "Games", "Arena" }),
                "", true);
            RbxInstance arena = placement.Script.Parent;
            RbxInstance games = arena.Parent;

            _host.UndoPlacement(placement);
            _host.UndoPlacement(placement);

            Assert.IsTrue(placement.Script.IsDestroyed);
            Assert.IsTrue(arena.IsDestroyed);
            Assert.IsTrue(games.IsDestroyed);
            Assert.IsFalse(_host.TryGetBoundScript("door", out _));
            CollectionAssert.AreEqual(new[] { "door:Bound", "door:Destroyed" }, _changes, "a second undo does nothing");
        }

        [Test]
        public void C1_Undo_Negative_AFolderThatHoldsSomethingElse_Stays()
        {
            ScriptInstancePlacement placement = _host.Place("door", Plan("server", folders: new[] { "Games" }), "", true);
            RbxInstance games = placement.Script.Parent;
            WorldPart("Trophy", games);

            _host.UndoPlacement(placement);

            Assert.IsTrue(placement.Script.IsDestroyed);
            Assert.IsFalse(games.IsDestroyed);
        }

        [Test]
        public void C1_Undo_AReusedPlacement_PutsParentPositionAndRunContextBack()
        {
            RbxBaseScript script = (RbxBaseScript)_host.Place("door", Plan("server"), "", true).Script;
            RbxInstance serverScripts = Service("ServerScriptService");
            WorldPart("After", serverScripts);
            int index = new List<RbxInstance>(serverScripts.GetChildren()).IndexOf(script);

            ScriptInstancePlacement placement = _host.Place("door", Plan("client", bound: script), "", true);
            Assert.AreSame(Service("ReplicatedStorage"), script.Parent);

            _host.UndoPlacement(placement);

            Assert.AreSame(serverScripts, script.Parent);
            Assert.AreEqual(index, new List<RbxInstance>(serverScripts.GetChildren()).IndexOf(script));
            Assert.AreEqual(RunContextValues.Server, script.RunContext);
            Assert.IsTrue(_host.TryGetBoundScript("door", out _), "a failed reload keeps the instance bound");
        }

        // ---- adoption ------------------------------------------------------------------------

        [Test]
        public void C1_Place_AdoptsTheUnboundScriptAVersion1SaveLeftAtThePlacement()
        {
            RbxBaseScript saved = (RbxBaseScript)_registry.Create("Script");
            saved.Name = "door";
            saved.Parent = Service("ReplicatedStorage");

            ScriptInstancePlacement placement = _host.Place("door", Plan("client"), "", true);

            Assert.IsTrue(placement.Adopted);
            Assert.AreSame(saved, placement.Script, "one script per source, not a second one next to it");
            Assert.AreEqual("door", saved.SourceId);
            Assert.AreEqual(RunContextValues.Client, saved.RunContext);

            _host.UndoPlacement(placement);

            Assert.IsFalse(saved.IsDestroyed, "undoing an adoption unbinds, it never destroys the saved node");
            Assert.IsFalse(saved.IsBound);
            Assert.AreEqual(RunContextValues.Legacy, saved.RunContext);
        }

        [Test]
        public void C1_Place_Negative_AnUnboundScriptWithAnotherNameOrAModsScript_IsNotAdopted()
        {
            RbxInstance other = _registry.Create("Script");
            other.Name = "gate";
            other.Parent = Service("ServerScriptService");
            RbxInstance modOwned = _registry.Create("Script", "builder");
            modOwned.Name = "door";
            modOwned.Parent = Service("ServerScriptService");

            ScriptInstancePlacement placement = _host.Place("door", Plan("server"), "", true);

            Assert.IsTrue(placement.Created);
            Assert.AreNotSame(other, placement.Script);
            Assert.AreNotSame(modOwned, placement.Script);
        }

        // ---- release -------------------------------------------------------------------------

        [Test]
        public void C1_Release_ADefaultPlacedChildlessScript_IsDestroyed()
        {
            RbxLuaSourceContainer script = _host.Place("door", Plan("server", folders: new[] { "Games" }), "", true)
                .Script;

            Assert.AreEqual(ScriptReleaseOutcome.Destroyed, _host.Release("door", new[] { "Games" }));

            Assert.IsTrue(script.IsDestroyed);
            Assert.IsFalse(_host.TryGetBoundScript("door", out _));
            Assert.AreEqual(ScriptReleaseOutcome.NothingBound, _host.Release("door", new[] { "Games" }));
        }

        [Test]
        public void C1_Release_Negative_AMovedScriptOrOneWithChildren_IsUnboundAndKept()
        {
            RbxLuaSourceContainer moved = _host.Place("door", Plan("server"), "", true).Script;
            moved.Parent = WorldPart("Door", Service("Workspace"));
            RbxLuaSourceContainer holder = _host.Place("gate", Plan("server"), "", true).Script;
            WorldPart("Hinge", holder);

            Assert.AreEqual(ScriptReleaseOutcome.Unbound, _host.Release("door", null));
            Assert.AreEqual(ScriptReleaseOutcome.Unbound, _host.Release("gate", null));

            Assert.IsFalse(moved.IsDestroyed);
            Assert.IsFalse(moved.IsBound);
            Assert.IsFalse(holder.IsDestroyed);
            Assert.AreEqual(2, _log.Count, "each kept instance is reported once");
        }

        // ---- the binding index ---------------------------------------------------------------

        [Test]
        public void C1_Index_SeesABindingTheTreeAlreadyHolds()
        {
            RbxBaseScript restored = (RbxBaseScript)_registry.Create("Script", null, OriginTag.FromMod("door"));
            restored.Name = "door";
            restored.SourceId = "door";
            restored.Parent = Service("ServerScriptService");

            Assert.IsTrue(_host.TryGetBoundScript("door", out RbxLuaSourceContainer found));
            Assert.AreSame(restored, found);
        }

        [Test]
        public void C1_Index_Negative_NeverReportsASyntheticLegacyScriptOrAModsOwnScript()
        {
            RbxBaseScript synthetic = (RbxBaseScript)_registry.Create("Script", "door", OriginTag.FromMod("door"),
                isRuntimeInfrastructure: true);
            synthetic.SourceId = "door";
            synthetic.Parent = Service("ServerScriptService");
            RbxBaseScript modMade = (RbxBaseScript)_registry.Create("Script", "gate");
            modMade.SourceId = "gate";
            modMade.Parent = Service("Workspace");

            Assert.IsFalse(_host.TryGetBoundScript("door", out _), "the synthetic script is runtime infrastructure");
            Assert.IsFalse(_host.TryGetBoundScript("gate", out _), "a bound instance is world-owned");
        }

        [Test]
        public void C1_Index_DestroyingTheBoundScript_ReportsItAndDetachesTheSource()
        {
            RbxLuaSourceContainer script = _host.Place("door", Plan("server"), "", true).Script;

            script.Destroy();

            Assert.IsFalse(_host.TryGetBoundScript("door", out _), "a source whose instance was destroyed is detached");
            CollectionAssert.AreEqual(new[] { "door:Bound", "door:Destroyed" }, _changes);
        }

        // ---- folder moves and the legacy hand-over --------------------------------------------

        [Test]
        public void C1_MoveFromDefaultPlacement_MovesOnlyAScriptStillThere()
        {
            RbxLuaSourceContainer script = _host.Place("door", Plan("server", folders: new[] { "Old" }), "", true)
                .Script;
            IReadOnlyList<string> from = ScriptPlacementPlanner.DefaultPath(ScriptContext.Server, new[] { "Old" });
            IReadOnlyList<string> to = ScriptPlacementPlanner.DefaultPath(ScriptContext.Server, new[] { "New" });

            Assert.IsTrue(_host.MoveFromDefaultPlacement("door", from, to));
            Assert.AreEqual("New", script.Parent.Name);
            Assert.IsFalse(_host.MoveFromDefaultPlacement("door", from, to), "it is no longer at the old placement");
        }

        [TestCase(true)]
        [TestCase(false)]
        public void C1_HandOverLegacyPlacement_MovesWhatTheSyntheticFolderHolds(bool keepOwnObjects)
        {
            RbxInstance folder = _registry.Create("Folder", "door", OriginTag.FromMod("door"),
                isRuntimeInfrastructure: true);
            folder.Name = "door";
            folder.Parent = Service("ServerScriptService");
            RbxInstance synthetic = _registry.Create("Script", "door", OriginTag.FromMod("door"),
                isRuntimeInfrastructure: true);
            synthetic.Parent = folder;
            RbxInstance own = WorldPart("Coin", folder, "door");
            RbxInstance players = WorldPart("Build", folder);
            RbxInstance underScript = WorldPart("Marker", synthetic);
            RbxLuaSourceContainer successor = _host.Place("door", Plan("server"), "", true).Script;

            int moved = _host.HandOverLegacyPlacement("door", folder, synthetic, successor, keepOwnObjects);

            Assert.IsTrue(folder.IsDestroyed);
            Assert.IsTrue(synthetic.IsDestroyed);
            Assert.AreSame(successor, players.Parent, "what someone else owns is never destroyed");
            Assert.AreSame(successor, underScript.Parent);
            if (keepOwnObjects)
            {
                Assert.AreEqual(3, moved);
                Assert.AreSame(successor, own.Parent, "keep mode keeps every object");
            }
            else
            {
                Assert.AreEqual(2, moved);
                Assert.IsTrue(own.IsDestroyed, "a clean reload removes what the mod owns, as its startup objects");
            }
        }
    }
}
