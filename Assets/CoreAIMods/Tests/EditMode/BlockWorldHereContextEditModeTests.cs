using CoreAI.Demos.BlockWorld;
using CoreAI.Mods.Rbx.Datatypes;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode
{
    /// <summary>
    /// What the Programmer is told about "here" in the Block World: always a concrete cell (BuildHereCell), named as the
    /// crosshair's place cell when there is a target, and as the ground ahead, with "do not survey", when there is none.
    /// </summary>
    [TestFixture]
    public sealed class BlockWorldHereContextEditModeTests
    {
        [Test]
        public void WithATarget_HereIsTheCrosshairsPlaceCell()
        {
            string line = BlockWorldDemoModule.DescribeHere(new RbxVector3(3, 1, 3), "crosshair");

            StringAssert.Contains("BuildHereCell (3, 1, 3)", line);
            StringAssert.Contains("the block the player looks at", line);
            StringAssert.DoesNotContain("nothing within reach", line);
        }

        [Test]
        public void WithoutATarget_HereIsTheGroundAhead_AndNoSurveyIsNeeded_Twin()
        {
            string line = BlockWorldDemoModule.DescribeHere(new RbxVector3(2, 1, 5), "ahead");

            StringAssert.Contains("BuildHereCell (2, 1, 5)", line);
            StringAssert.Contains("three cells in front of the player", line);
            StringAssert.Contains("do not survey", line);
        }

        [Test]
        public void TheStandingContext_NamesBuildHereCell_AndATerrainWriteThere()
        {
            StringAssert.Contains("BuildHereCell", BlockWorldDemoModule.HereContext);
            StringAssert.Contains("always set", BlockWorldDemoModule.HereContext);
            StringAssert.Contains("Terrain:FillBlock", BlockWorldDemoModule.HereContext);
            StringAssert.Contains("no need to survey", BlockWorldDemoModule.HereContext);
        }
    }
}
