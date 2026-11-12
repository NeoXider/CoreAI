using CoreAI.Mods.Rbx.Datatypes;
using CoreAI.Mods.Rbx.Instances;
using CoreAI.Mods.Rbx.Instances.Building;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode.RbxApi.Building
{
    /// <summary>
    /// Engine-free <c>RbxWorldPick</c>: a Terrain hit on a face names the solid cell behind it and the empty cell in
    /// front, on every axis and side, also when the hit point lies exactly on the cell boundary; a slanted normal snaps
    /// to its main axis.
    /// </summary>
    [TestFixture]
    public sealed class RbxWorldPickEditModeTests
    {
        private static RbxWorldPick TerrainHit(RbxVector3 position, RbxVector3 normal)
        {
            return new RbxWorldPick(new InstanceId(5), position, normal, 10d, true, true);
        }

        [Test]
        public void ATopFaceHit_NamesTheCellBelowAndTheCellAbove()
        {
            RbxWorldPick pick = TerrainHit(new RbxVector3(6f, 8f, -2f), new RbxVector3(0f, 1f, 0f));

            Assert.AreEqual(new RbxVector3(1f, 1f, -1f), pick.Cell, "the face at y=8 tops the cell y 4..8");
            Assert.AreEqual(new RbxVector3(1f, 2f, -1f), pick.PlaceCell);
        }

        [Test]
        public void SideAndBottomFaces_NameTheirOwnCells()
        {
            RbxWorldPick east = TerrainHit(new RbxVector3(4f, 2f, 2f), new RbxVector3(1f, 0f, 0f));
            Assert.AreEqual(new RbxVector3(0f, 0f, 0f), east.Cell);
            Assert.AreEqual(new RbxVector3(1f, 0f, 0f), east.PlaceCell);

            RbxWorldPick west = TerrainHit(new RbxVector3(4f, 2f, 2f), new RbxVector3(-1f, 0f, 0f));
            Assert.AreEqual(new RbxVector3(1f, 0f, 0f), west.Cell, "twin: the same point seen from the other side");
            Assert.AreEqual(new RbxVector3(0f, 0f, 0f), west.PlaceCell);

            RbxWorldPick bottom = TerrainHit(new RbxVector3(2f, -4f, 2f), new RbxVector3(0f, -1f, 0f));
            Assert.AreEqual(new RbxVector3(0f, -1f, 0f), bottom.Cell);
            Assert.AreEqual(new RbxVector3(0f, -2f, 0f), bottom.PlaceCell);
        }

        [Test]
        public void ASlantedNormal_SnapsToItsMainAxis_AndZeroStaysZero()
        {
            Assert.AreEqual(new RbxVector3(0f, 0f, -1f), RbxWorldPick.SnapToAxis(new RbxVector3(0.2f, 0.3f, -0.9f)));
            Assert.AreEqual(new RbxVector3(1f, 0f, 0f), RbxWorldPick.SnapToAxis(new RbxVector3(0.7f, 0.7f, 0f)),
                "a tie goes to X");
            Assert.AreEqual(new RbxVector3(0f, 0f, 0f), RbxWorldPick.SnapToAxis(new RbxVector3(0f, 0f, 0f)));
        }

        [Test]
        public void NoneIsNoHit_AndAPartHitIsOne()
        {
            Assert.IsFalse(RbxWorldPick.None.IsHit);
            Assert.IsTrue(new RbxWorldPick(new InstanceId(9), default, default, 1d, false, false).IsHit);
        }
    }
}
