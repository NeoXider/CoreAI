using CoreAI.Mods.Folders;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode.ModFolders
{
    /// <summary>The folder path rules: what a strict read accepts, each named refusal, and the lenient read of stored categories.</summary>
    [TestFixture]
    public sealed class ModFolderPathEditModeTests
    {
        [TestCase("Games/Arcade", "Games/Arcade")]
        [TestCase("  Games  /  Arcade  ", "Games/Arcade")]
        [TestCase("/Games/Arcade/", "Games/Arcade")]
        [TestCase("Games\\Arcade", "Games/Arcade")]
        [TestCase("Big   Games/Retro  Arcade", "Big Games/Retro Arcade")]
        [TestCase("Tools & Utils/Level #1 (beta)", "Tools & Utils/Level #1 (beta)")]
        [TestCase("Jeux vidéo/ゲーム", "Jeux vidéo/ゲーム")]
        public void Parse_ValidPath_IsNormalized(string text, string expected)
        {
            ModFolderPathParse parse = ModFolderPath.Parse(text);

            Assert.IsTrue(parse.Succeeded, parse.Message);
            Assert.AreEqual(expected, parse.Path.Value);
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase("   ")]
        [TestCase("/")]
        public void Parse_BlankPath_IsTheTopLevel(string text)
        {
            ModFolderPathParse parse = ModFolderPath.Parse(text);

            Assert.IsTrue(parse.Succeeded);
            Assert.IsTrue(parse.Path.IsRoot);
            Assert.AreEqual(0, parse.Path.Depth);
        }

        [Test]
        public void Parse_OneLevelLegacyCategory_IsAOneSegmentPath()
        {
            ModFolderPathParse parse = ModFolderPath.Parse("Samples");

            Assert.IsTrue(parse.Succeeded);
            Assert.AreEqual("Samples", parse.Path.Value);
            Assert.AreEqual(1, parse.Path.Depth);
            Assert.IsTrue(parse.Path.Parent.IsRoot);
        }

        [TestCase("Games//Arcade", ModFolderErrorCode.EmptySegment)]
        [TestCase("Games/ /Arcade", ModFolderErrorCode.EmptySegment)]
        [TestCase("Games/Ar:cade", ModFolderErrorCode.InvalidCharacter)]
        [TestCase("Games/[WIP]", ModFolderErrorCode.InvalidCharacter)]
        [TestCase("Games/A]]B", ModFolderErrorCode.InvalidCharacter)]
        [TestCase("Games/Ar\tcade", ModFolderErrorCode.InvalidCharacter)]
        [TestCase("Games/Line\nBreak", ModFolderErrorCode.InvalidCharacter)]
        [TestCase("Games/Star*", ModFolderErrorCode.InvalidCharacter)]
        [TestCase("Games/🎮", ModFolderErrorCode.InvalidCharacter)]
        [TestCase("Games/..", ModFolderErrorCode.ReservedName)]
        [TestCase("./Games", ModFolderErrorCode.ReservedName)]
        [TestCase("a/b/c/d/e/f/g/h/i", ModFolderErrorCode.TooDeep)]
        public void Parse_RuleViolation_FailsWithItsNamedError(string text, ModFolderErrorCode expected)
        {
            ModFolderPathParse parse = ModFolderPath.Parse(text);

            Assert.IsFalse(parse.Succeeded);
            Assert.AreEqual(expected, parse.Error, parse.Message);
            Assert.IsNotEmpty(parse.Message);
            Assert.IsTrue(parse.Path.IsRoot);
        }

        [Test]
        public void Parse_LoneSurrogate_FailsWithInvalidCharacter()
        {
            string lone = "Games/" + (char)0xD800 + "x";

            Assert.AreEqual(ModFolderErrorCode.InvalidCharacter, ModFolderPath.Parse(lone).Error);
        }

        [Test]
        public void Parse_EightLevels_IsTheDeepestValidPath()
        {
            Assert.IsTrue(ModFolderPath.Parse("a/b/c/d/e/f/g/h").Succeeded);
        }

        [Test]
        public void Parse_SegmentAtTheLimit_PassesAndOneMoreFails()
        {
            string atLimit = new('x', ModFolderPath.MaxSegmentLength);

            Assert.IsTrue(ModFolderPath.Parse("Games/" + atLimit).Succeeded);
            ModFolderPathParse tooLong = ModFolderPath.Parse("Games/" + atLimit + "x");
            Assert.AreEqual(ModFolderErrorCode.SegmentTooLong, tooLong.Error);
            StringAssert.Contains("segment 2", tooLong.Message);
        }

        [Test]
        public void Parse_WholePathPastTheLimit_FailsWithPathTooLong()
        {
            string segment = new('x', ModFolderPath.MaxSegmentLength);
            string path = segment + "/" + segment + "/" + segment + "/" + segment;

            ModFolderPathParse parse = ModFolderPath.Parse(path);

            Assert.AreEqual(ModFolderPath.MaxSegmentLength * 4 + 3, path.Length);
            Assert.Greater(path.Length, ModFolderPath.MaxLength);
            Assert.AreEqual(ModFolderErrorCode.PathTooLong, parse.Error);
        }

        [Test]
        public void ParseName_WithSeparator_FailsWithInvalidName()
        {
            Assert.AreEqual(ModFolderErrorCode.InvalidName, ModFolderPath.ParseName("A/B").Error);
            Assert.AreEqual(ModFolderErrorCode.InvalidName, ModFolderPath.ParseName("A\\B").Error);
            Assert.AreEqual(ModFolderErrorCode.EmptySegment, ModFolderPath.ParseName("  ").Error);
            Assert.AreEqual("Retro", ModFolderPath.ParseName(" Retro ").Path.Value);
        }

        [Test]
        public void FromStored_LegacyCategoryTheStrictRulesRefuse_IsKeptAsStored()
        {
            ModFolderPath path = ModFolderPath.FromStored("  Tools: misc //[WIP]  ");

            Assert.AreEqual("Tools: misc/[WIP]", path.Value);
            Assert.AreEqual(2, path.Depth);
            Assert.IsFalse(ModFolderPath.Parse("Tools: misc/[WIP]").Succeeded);
        }

        [TestCase("Games/Arcade")]
        [TestCase("Samples")]
        [TestCase("Jeux vidéo")]
        public void FromStored_CanonicalText_ReturnsTheSameString(string text)
        {
            Assert.AreSame(text, ModFolderPath.FromStored(text).Value);
        }

        [TestCase("Games / Arcade", "Games/Arcade")]
        [TestCase("Games\\Arcade", "Games/Arcade")]
        [TestCase(" Samples ", "Samples")]
        [TestCase("A  B", "A B")]
        [TestCase("", "")]
        public void FromStored_AgreesWithParseOnValidText(string text, string expected)
        {
            Assert.AreEqual(expected, ModFolderPath.FromStored(text).Value);
            Assert.AreEqual(expected, ModFolderPath.Parse(text).Path.Value);
        }

        [Test]
        public void Identity_IsCaseInsensitive()
        {
            ModFolderPath upper = ModFolderPath.Parse("Games/Arcade").Path;
            ModFolderPath lower = ModFolderPath.Parse("games/ARCADE").Path;

            Assert.IsTrue(upper == lower);
            Assert.AreEqual(upper.GetHashCode(), lower.GetHashCode());
        }

        [Test]
        public void IsSameOrDescendantOf_ComparesWholeSegments()
        {
            ModFolderPath games = ModFolderPath.FromStored("Games");

            Assert.IsTrue(ModFolderPath.FromStored("Games").IsSameOrDescendantOf(games));
            Assert.IsTrue(ModFolderPath.FromStored("games/Arcade/Retro").IsSameOrDescendantOf(games));
            Assert.IsFalse(ModFolderPath.FromStored("GamesOld").IsSameOrDescendantOf(games));
            Assert.IsFalse(ModFolderPath.FromStored("GamesOld/Arcade").IsSameOrDescendantOf(games));
            Assert.IsFalse(ModFolderPath.FromStored("Game").IsSameOrDescendantOf(games));
            Assert.IsTrue(games.IsSameOrDescendantOf(ModFolderPath.Root));
            Assert.IsFalse(games.IsDescendantOf(games));
        }

        [Test]
        public void RebaseOnto_MovesTheSuffix()
        {
            ModFolderPath from = ModFolderPath.FromStored("Games/Arcade");
            ModFolderPath to = ModFolderPath.FromStored("Old/Arcade");

            Assert.AreEqual("Old/Arcade/Retro", ModFolderPath.FromStored("Games/Arcade/Retro").RebaseOnto(from, to).Value);
            Assert.AreEqual("Old/Arcade", from.RebaseOnto(from, to).Value);
            Assert.AreEqual("Retro", ModFolderPath.FromStored("Games/Arcade/Retro").RebaseOnto(from, ModFolderPath.Root).Value);
            Assert.AreEqual("New/Games/Arcade",
                from.RebaseOnto(ModFolderPath.Root, ModFolderPath.FromStored("New")).Value);
        }

        [Test]
        public void NameAndParent_SplitTheLastSegment()
        {
            ModFolderPath path = ModFolderPath.FromStored("Games/Arcade/Retro");

            Assert.AreEqual("Retro", path.Name);
            Assert.AreEqual("Games/Arcade", path.Parent.Value);
            CollectionAssert.AreEqual(new[] { "Games", "Arcade", "Retro" }, path.GetSegments());
            Assert.AreEqual("", ModFolderPath.Root.Name);
            Assert.IsTrue(ModFolderPath.Root.Parent.IsRoot);
        }

        [Test]
        public void ErrorCodes_AreStableUpperCaseNames()
        {
            Assert.AreEqual("FOLDER_NOT_EMPTY", ModFolderErrors.Code(ModFolderErrorCode.FolderNotEmpty));
            Assert.AreEqual("MOVE_INTO_ITSELF", ModFolderErrors.Code(ModFolderErrorCode.MoveIntoItself));
            Assert.AreEqual("OK", ModFolderErrors.Code(ModFolderErrorCode.None));
            foreach (ModFolderErrorCode code in System.Enum.GetValues(typeof(ModFolderErrorCode)))
            {
                Assert.AreNotEqual("UNKNOWN", ModFolderErrors.Code(code), code.ToString());
            }
        }
    }
}
