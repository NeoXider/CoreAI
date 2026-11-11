using System;
using CoreAI.Mods.Rbx.Binding;
using CoreAI.Mods.Rbx.Datatypes;
using CoreAI.Mods.Rbx.Instances;
using CoreAI.Mods.Rbx.Instances.Terrain;
using CoreAI.Mods.WorldPackages;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode.RbxApi.Acceptance
{
    /// <summary>
    /// MVP4 step A3 (dev-docs/MVP4_SCRIPT_CONTEXTS_DESIGN.md, SC-16): a world package staged into a replica
    /// registry keeps the server's ids and every captured revision and marks nothing diverged, which settles the
    /// design's open point that <c>InstanceTreeSerializer.Restore</c> runs unchanged on a replica; a local write
    /// after the commit is diverged.
    /// </summary>
    [TestFixture]
    public sealed class Mvp4ReplicaStagingEditModeTests
    {
        private const string WorldId = "mvp4-replica";

        private static readonly DateTime CapturedAtUtc = new(2026, 11, 11, 17, 0, 0, DateTimeKind.Utc);

        [Test]
        public void ReplicaStaging_V1Package_KeepsEveryCapturedRevision_AndMarksNothingDiverged()
        {
            RbxWorldPackagePayload payload = RbxWorldPackageSerializer.ReadPackage(
                RbxWorldPackageSerializer.WritePackage(CaptureServerWorld()));
            ReplicaRbxWorldSessionHost host = NewClientHost();

            using IRbxWorldSessionCandidate candidate = host.Stage(payload);

            Assert.AreEqual(RegistryAuthority.Replica, candidate.Registry.Authority);
            Assert.IsFalse(candidate.Registry.IsApplyingReplication, "the restore closed its replication scope");
            foreach (InstanceSnapshot node in payload.Tree.Instances)
            {
                Assert.IsTrue(candidate.Registry.TryGetRecord(new InstanceId(node.Id), out InstanceRecord record),
                    "the replica holds the server's id " + node.Id);
                Assert.AreEqual(node.Revision, record.Revision, node.ClassName + " " + node.Name + " keeps its revision");
                Assert.IsFalse(record.IsLocallyDiverged, node.ClassName + " " + node.Name + " is not diverged");
            }

            RbxTerrain terrain = RbxTerrainBootstrap.Find(candidate.Game.FindFirstChildOfClass("Workspace"));
            Assert.AreEqual(8L, terrain.Store.SolidCellCount, "the cells reach the replica too");
            candidate.Commit();
            Assert.AreSame(candidate.Registry, host.Registry);
        }

        /// <summary>Twin: a write the client makes on its own after the commit is diverged from the server's world.</summary>
        [Test]
        public void ReplicaStaging_LocalWriteAfterCommit_IsDiverged()
        {
            RbxWorldPackagePayload payload = CaptureServerWorld();
            ReplicaRbxWorldSessionHost host = NewClientHost();
            IRbxWorldSessionCandidate candidate = host.Stage(payload);
            candidate.Commit();
            RbxInstance wall = host.Registry.WorldRoot.FindFirstChild("Wall");
            Assert.IsTrue(host.Registry.TryGetRecord(wall.Id, out InstanceRecord record));
            long revision = record.Revision;

            wall.Name = "Moved";

            Assert.IsTrue(record.IsLocallyDiverged);
            Assert.AreEqual(revision, record.Revision, "a replica never counts its own revisions");
        }

        [Test]
        public void ReplicaHost_RefusesAnAuthoritativeRegistry()
        {
            InstanceRegistry authoritative = new(worldId: WorldId);
            RbxDataModel game = DataModelBootstrap.CreateGame(authoritative);

            Assert.Throws<ArgumentException>(() => new ReplicaRbxWorldSessionHost(authoritative, game));
        }

        /// <summary>Twin of the option: a restore without it builds an authoritative registry, as every restore did.</summary>
        [Test]
        public void RestoreFresh_WithoutTheOption_BuildsAnAuthoritativeRegistry()
        {
            RbxWorldPackageRestoreResult restored = RbxWorldPackageSerializer.RestoreFresh(CaptureServerWorld());

            Assert.AreEqual(RegistryAuthority.Authoritative, restored.Registry.Authority);
            Assert.AreEqual(RegistryAuthority.Authoritative, new RbxWorldPackageRestoreOptions().RegistryAuthority);
            restored.Game.Destroy();
        }

        private static ReplicaRbxWorldSessionHost NewClientHost()
        {
            InstanceRegistry replica = new(worldId: WorldId, authority: RegistryAuthority.Replica);
            RbxDataModel game = DataModelBootstrap.CreateGame(replica);
            return new ReplicaRbxWorldSessionHost(replica, game, WorldPackageTestWorlds.NewSettingsFor(WorldId));
        }

        /// <summary>A server world with every kind of state a restore writes: Part state, a lit torch, driven lighting, cells, StarterPlayer.</summary>
        private static RbxWorldPackagePayload CaptureServerWorld()
        {
            InstanceRegistry registry = new(worldId: WorldId);
            RbxDataModel game = DataModelBootstrap.CreateGame(registry);
            InMemoryPartPropertySink sink = new(registry);
            RbxInstance wall = registry.Create("Part", null, "console:session-1", ownerActorId: "server-host");
            wall.Name = "Wall";
            wall.SetAttribute("Health", 50d);
            wall.AddTag("Castle");
            PartProperties properties = PartProperties.CreateDefault();
            properties.Anchored = true;
            properties.Size = new RbxVector3(4f, 8f, 1f);
            sink.SetPartProperties(wall.Id, in properties);
            wall.Parent = registry.WorldRoot;
            RbxPointLight torch = (RbxPointLight)registry.Create("PointLight");
            torch.SetBrightness(3d);
            torch.Parent = wall;
            ((RbxLighting)game.FindFirstChildOfClass("Lighting")).SetClockTime(20d);
            ((RbxStarterPlayer)game.FindFirstChildOfClass("StarterPlayer")).CameraMode = RbxCameraMode.LockFirstPerson;
            Assert.IsTrue(TerrainMaterials.TryGetSlot("Grass", out byte grass));
            RbxTerrainBootstrap.Find(registry.WorldRoot).Store.FillBox(new TerrainCellBox(0, 0, 0, 2, 2, 2), grass, 255);
            RbxWorldPackagePayload payload = RbxWorldPackageSerializer.Capture(new RbxWorldPackageCaptureContext(
                registry, game, sink, WorldPackageTestWorlds.NewSettingsFor(WorldId), capturedAtUtc: CapturedAtUtc));
            game.Destroy();
            return payload;
        }
    }
}
