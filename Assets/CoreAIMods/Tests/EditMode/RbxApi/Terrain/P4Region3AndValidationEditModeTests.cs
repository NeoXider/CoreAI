using CoreAI.Mods.Rbx.Datatypes;
using CoreAI.Mods.Rbx.Instances;
using CoreAI.Mods.Rbx.Instances.Terrain;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode.RbxApi.Terrain
{
    /// <summary>
    /// P4, engine-free: the <c>Region3</c> datatype (<c>Region3.yaml</c>: <c>new(min, max)</c>,
    /// <c>CFrame</c>, <c>Size</c>, <c>ExpandToGrid</c>) and the region rules of the Terrain calls
    /// (<c>Terrain.yaml</c>: resolution "must be 4", "aligned to the voxel grid", 4,194,304 voxels), each
    /// refusal with its passing twin.
    /// </summary>
    [TestFixture]
    public sealed class P4Region3AndValidationEditModeTests
    {
        private const string Member = "Terrain:ReadVoxels";

        private static RbxRegion3 Region(float x0, float y0, float z0, float x1, float y1, float z1)
        {
            return new RbxRegion3(new RbxVector3(x0, y0, z0), new RbxVector3(x1, y1, z1));
        }

        [Test]
        public void P4_Region3_CFrameIsTheCentre_AndSizeIsMaxMinusMin()
        {
            RbxRegion3 region = Region(-4f, 0f, 8f, 12f, 4f, 16f);

            Assert.AreEqual(new RbxVector3(4f, 2f, 12f), region.CFrame.Position);
            Assert.AreEqual(RbxCFrame.Identity.XVector, region.CFrame.XVector, "a Region3 never rotates");
            Assert.AreEqual(new RbxVector3(16f, 4f, 8f), region.Size);
        }

        [Test]
        public void P4_Region3_SwappedCorners_GiveANegativeSize()
        {
            RbxRegion3 region = Region(8f, 8f, 8f, 0f, 0f, 0f);

            Assert.AreEqual(new RbxVector3(-8f, -8f, -8f), region.Size,
                "the mirror: switching the bounds switches the polarity of the size");
        }

        [Test]
        public void P4_Region3_ExpandToGrid_FloorsTheMinimumAndCeilsTheMaximum()
        {
            RbxRegion3 expanded = Region(-1f, 2.5f, 4f, 5f, 7.9f, 8f).ExpandToGrid(4);

            Assert.AreEqual(new RbxVector3(-4f, 0f, 4f), expanded.Min);
            Assert.AreEqual(new RbxVector3(8f, 8f, 8f), expanded.Max);
        }

        [Test]
        public void P4_Region3_ExpandToGrid_OfAnAlignedRegion_IsTheSameRegion()
        {
            RbxRegion3 region = Region(-8f, 0f, 4f, 16f, 4f, 12f);

            Assert.AreEqual(region, region.ExpandToGrid(4));
        }

        [Test]
        public void P4_Region3_ExpandToGrid_RefusesANonPositiveResolution()
        {
            RbxApiStubException error = Assert.Throws<RbxApiStubException>(() =>
                Region(0f, 0f, 0f, 4f, 4f, 4f).ExpandToGrid(0));

            Assert.AreEqual("BAD_ARGUMENT", error.Code);
            StringAssert.Contains("ExpandToGrid(4)", error.Fix);
        }

        [Test]
        public void P4_Region3_ToString_IsTheCFrameThenTheSize()
        {
            Assert.AreEqual("2, 2, 2, 1, 0, 0, 0, 1, 0, 0, 0, 1; 4, 4, 4", Region(0f, 0f, 0f, 4f, 4f, 4f).ToString());
        }

        [Test]
        public void P4_Validate_AnAlignedRegionAtResolution4_GivesItsCells()
        {
            TerrainCellBox box = TerrainRegionValidator.Validate(Region(-8f, 0f, 4f, 16f, 4f, 12f), 4, Member);

            Assert.AreEqual(new TerrainCellBox(-2, 0, 1, 6, 1, 2), box);
        }

        [Test]
        public void P4_Validate_ResolutionOtherThan4_IsBadArgument()
        {
            RbxError error = Assert.Throws<RbxError>(() =>
                TerrainRegionValidator.Validate(Region(0f, 0f, 0f, 8f, 8f, 8f), 2, Member));

            Assert.AreEqual(RbxErrorCode.BadArgument, error.Code);
            StringAssert.Contains("resolution must be 4", error.RawMessage);
            StringAssert.Contains("pass 4", error.Fix);
        }

        [Test]
        public void P4_Validate_UnalignedRegion_IsBadArgument_NamingExpandToGrid()
        {
            RbxError error = Assert.Throws<RbxError>(() =>
                TerrainRegionValidator.Validate(Region(0f, 0f, 0f, 6f, 8f, 8f), 4, Member));

            Assert.AreEqual(RbxErrorCode.BadArgument, error.Code);
            StringAssert.Contains("not aligned", error.RawMessage);
            StringAssert.Contains("ExpandToGrid(4)", error.Fix);
        }

        [Test]
        public void P4_Validate_RegionOverTheVoxelLimit_IsBadArgument()
        {
            RbxError error = Assert.Throws<RbxError>(() =>
                TerrainRegionValidator.Validate(Region(0f, 0f, 0f, 1024f, 260f, 1024f), 4, Member));

            Assert.AreEqual(RbxErrorCode.BadArgument, error.Code);
            StringAssert.Contains("4194304", error.RawMessage);
        }

        [Test]
        public void P4_Validate_RegionExactlyAtTheVoxelLimit_Passes()
        {
            TerrainCellBox box = TerrainRegionValidator.Validate(Region(0f, 0f, 0f, 1024f, 256f, 1024f), 4, Member);

            Assert.AreEqual(TerrainRegionValidator.MaxRegionVoxels, box.CellCount);
        }

        [Test]
        public void P4_Validate_NegativeSize_IsBadArgument()
        {
            RbxError error = Assert.Throws<RbxError>(() =>
                TerrainRegionValidator.Validate(Region(8f, 0f, 0f, 0f, 8f, 8f), 4, Member));

            StringAssert.Contains("negative size", error.RawMessage);
        }

        [Test]
        public void P4_Validate_NonFiniteCorner_IsBadArgument()
        {
            RbxError error = Assert.Throws<RbxError>(() =>
                TerrainRegionValidator.Validate(Region(0f, float.NaN, 0f, 8f, 8f, 8f), 4, Member));

            StringAssert.Contains("non-finite", error.RawMessage);
        }

        [Test]
        public void P4_Validate_ACornerOneUlpOffTheGrid_StillPasses()
        {
            float nearlySixteen = 16f * 0.1f * 10f;

            TerrainCellBox box = TerrainRegionValidator.Validate(Region(0f, 0f, 0f, nearlySixteen, 4f, 4f), 4, Member);

            Assert.AreEqual(4, box.SizeX);
        }
    }
}
