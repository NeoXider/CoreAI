using CoreAI.Mods.Rbx.Instances;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode.RbxApi.Instances
{
    /// <summary>
    /// A snapshot round trip keeps the runtime-infrastructure flag of the nodes the runtime rebuilds itself: the
    /// Workspace Terrain restored by <see cref="InstanceTreeSerializer"/> stays infrastructure, so it is no authored
    /// content and costs no instance quota after a save and load, and the bootstrap does not build a second one. The
    /// twin: an ordinary folder comes back as the content it was.
    /// </summary>
    [TestFixture]
    public sealed class RuntimeInfrastructureSnapshotEditModeTests
    {
        private static RbxDataModel RoundTrip(InstanceRegistry source, RbxDataModel game, out InstanceRegistry restored)
        {
            InstanceTreeSnapshot snapshot = InstanceTreeSerializer.Capture(game);
            restored = new InstanceRegistry();
            RbxDataModel restoredGame = (RbxDataModel)InstanceTreeSerializer.Restore(snapshot, restored);
            DataModelBootstrap.AttachWorldRoot(restored, restoredGame);
            return restoredGame;
        }

        [Test]
        public void RawRoundTrip_KeepsTheTerrainRuntimeInfrastructure_AndBuildsNoSecondOne()
        {
            InstanceRegistry source = new();
            RbxDataModel game = DataModelBootstrap.CreateGame(source);
            RbxInstance sourceTerrain = game.FindFirstChildOfClass("Workspace").FindFirstChildOfClass("Terrain");
            Assert.IsNotNull(sourceTerrain, "every bootstrapped world has its Terrain");
            Assert.IsTrue(InstanceTreeSerializer.CaptureNode(sourceTerrain).IsRuntimeInfrastructure,
                "the snapshot carries the flag");

            RbxDataModel restoredGame = RoundTrip(source, game, out InstanceRegistry restored);

            RbxInstance workspace = restoredGame.FindFirstChildOfClass("Workspace");
            int terrains = 0;
            foreach (RbxInstance child in workspace.GetChildren())
            {
                if (child.ClassName == "Terrain")
                {
                    terrains++;
                }
            }

            Assert.AreEqual(1, terrains, "the bootstrap keeps the restored Terrain instead of adding one");
            RbxInstance terrain = workspace.FindFirstChildOfClass("Terrain");
            Assert.AreEqual(sourceTerrain.Id, terrain.Id);
            Assert.IsTrue(restored.TryGetRecord(terrain.Id, out InstanceRecord record));
            Assert.IsTrue(record.IsRuntimeInfrastructure, "a restored Terrain is still runtime infrastructure");
            Assert.IsFalse(record.IsAuthoredContent);
        }

        [Test]
        public void RawRoundTrip_AnOrdinaryFolder_ComesBackAsTheContentItWas()
        {
            InstanceRegistry source = new();
            RbxDataModel game = DataModelBootstrap.CreateGame(source);
            RbxInstance host = source.Create("Folder");
            host.Name = "Host";
            host.Parent = source.WorldRoot;
            RbxInstance authored = source.Create("Folder", null, OriginTag.FromConsole("7"));
            authored.Name = "Authored";
            authored.Parent = source.WorldRoot;

            RbxDataModel restoredGame = RoundTrip(source, game, out InstanceRegistry restored);

            RbxInstance workspace = restoredGame.FindFirstChildOfClass("Workspace");
            Assert.IsTrue(restored.TryGetRecord(workspace.FindFirstChild("Host").Id, out InstanceRecord hostRecord));
            Assert.IsFalse(hostRecord.IsRuntimeInfrastructure, "twin: a host folder is not made infrastructure");
            Assert.IsTrue(restored.TryGetRecord(workspace.FindFirstChild("Authored").Id, out InstanceRecord authoredRecord));
            Assert.IsFalse(authoredRecord.IsRuntimeInfrastructure);
            Assert.IsTrue(authoredRecord.IsAuthoredContent, "console-authored content stays authored content");
        }
    }
}
