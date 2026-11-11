using CoreAI.Mods.Folders;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode.ModFolders
{
    /// <summary>Reading and rewriting the folder of a mod in its <c>@coreai</c> header, and nothing else.</summary>
    [TestFixture]
    public sealed class ModFolderHeaderEditorEditModeTests
    {
        private const string BlockSource =
            "--[[@coreai\nid: racer\nname: Lane Racer\ncategory: Samples\nauthor: CoreAI\n]]\nprint('go')\n";

        private static int Lines(string text)
        {
            return text.Split('\n').Length;
        }

        [Test]
        public void ReadCategory_BlockLineAndNoHeader()
        {
            Assert.AreEqual("Samples", ModFolderHeaderEditor.ReadCategory(BlockSource));
            Assert.AreEqual("utility", ModFolderHeaderEditor.ReadCategory("-- @coreai id: x\n-- @coreai category: utility\nprint(1)"));
            Assert.AreEqual("", ModFolderHeaderEditor.ReadCategory("print(1)"));
            Assert.AreEqual("", ModFolderHeaderEditor.ReadCategory(null));
            Assert.AreEqual("B", ModFolderHeaderEditor.ReadCategory("--[[@coreai\nCategory: A\ncategory:  B  \n]]"));
        }

        [Test]
        public void SetCategory_ExistingEntry_IsReplacedInPlaceAndNoLineMoves()
        {
            ModFolderHeaderEdit edit = ModFolderHeaderEditor.SetCategory(BlockSource, " Games / Arcade ");

            Assert.IsTrue(edit.Succeeded, edit.Message);
            Assert.AreEqual(BlockSource.Replace("category: Samples", "category: Games/Arcade"), edit.Source);
            Assert.AreEqual(Lines(BlockSource), Lines(edit.Source));
            Assert.AreEqual("Games/Arcade", edit.Category);
        }

        [Test]
        public void SetCategory_BlockWithoutEntry_AddsALineAfterTheOpener()
        {
            string source = "--[[@coreai\nid: x\n]]\nprint(1)\n";

            ModFolderHeaderEdit edit = ModFolderHeaderEditor.SetCategory(source, "Games");

            Assert.AreEqual("--[[@coreai\ncategory: Games\nid: x\n]]\nprint(1)\n", edit.Source);
            Assert.AreEqual("Games", ModFolderHeaderEditor.ReadCategory(edit.Source));
        }

        [Test]
        public void SetCategory_OneLineBlock_StaysAValidHeader()
        {
            string source = "--[[@coreai id: x ]]\nprint(1)";

            ModFolderHeaderEdit edit = ModFolderHeaderEditor.SetCategory(source, "Games");

            Assert.AreEqual("--[[@coreai\ncategory: Games\n id: x ]]\nprint(1)", edit.Source);
            Assert.AreEqual("Games", ModFolderHeaderEditor.ReadCategory(edit.Source));
        }

        [Test]
        public void SetCategory_CrLfSource_KeepsCrLf()
        {
            string source = "--[[@coreai\r\nid: x\r\n]]\r\nprint(1)\r\n";

            ModFolderHeaderEdit edit = ModFolderHeaderEditor.SetCategory(source, "Games");

            Assert.AreEqual("--[[@coreai\r\ncategory: Games\r\nid: x\r\n]]\r\nprint(1)\r\n", edit.Source);
        }

        [Test]
        public void SetCategory_LineHeader_ReplacesOrAddsALine()
        {
            string withEntry = "-- @coreai id: x\n-- @coreai category: utility\nprint(1)";
            string withoutEntry = "\n-- @coreai id: x\nprint(1)";

            Assert.AreEqual("-- @coreai id: x\n-- @coreai category: Tools/Clock\nprint(1)",
                ModFolderHeaderEditor.SetCategory(withEntry, "Tools/Clock").Source);
            Assert.AreEqual("\n-- @coreai category: Tools\n-- @coreai id: x\nprint(1)",
                ModFolderHeaderEditor.SetCategory(withoutEntry, "Tools").Source);
        }

        [Test]
        public void SetCategory_NoHeader_PrependsOneLine()
        {
            ModFolderHeaderEdit edit = ModFolderHeaderEditor.SetCategory("-- my mod\nprint(1)", "AI");

            Assert.AreEqual("-- @coreai category: AI\n-- my mod\nprint(1)", edit.Source);
            Assert.AreEqual("AI", ModFolderHeaderEditor.ReadCategory(edit.Source));
        }

        [Test]
        public void SetCategory_TopLevel_ClearsTheValueOrChangesNothing()
        {
            Assert.AreEqual(BlockSource.Replace("category: Samples", "category:"),
                ModFolderHeaderEditor.SetCategory(BlockSource, "").Source);
            Assert.AreEqual("print(1)", ModFolderHeaderEditor.SetCategory("print(1)", "").Source);
        }

        [Test]
        public void SetCategory_EmptyValue_GetsASpace()
        {
            Assert.AreEqual("--[[@coreai\ncategory: Games\n]]",
                ModFolderHeaderEditor.SetCategory("--[[@coreai\ncategory:\n]]", "Games").Source);
        }

        [Test]
        public void SetCategory_EveryCategoryEntry_GetsTheNewValue()
        {
            string source = "--[[@coreai\ncategory: A\ncategory: B\n]]";

            Assert.AreEqual("--[[@coreai\ncategory: C\ncategory: C\n]]",
                ModFolderHeaderEditor.SetCategory(source, "C").Source);
        }

        [Test]
        public void SetCategory_UnclosedBlock_IsRefused()
        {
            ModFolderHeaderEdit edit = ModFolderHeaderEditor.SetCategory("--[[@coreai\nid: x\nprint(1)", "Games");

            Assert.AreEqual(ModFolderErrorCode.HeaderUnclosed, edit.Error);
            Assert.IsNull(edit.Source);
        }

        [Test]
        public void SetCategory_InvalidPath_IsRefusedWithThePathError()
        {
            ModFolderHeaderEdit edit = ModFolderHeaderEditor.SetCategory(BlockSource, "Games/[WIP]");

            Assert.AreEqual(ModFolderErrorCode.InvalidCharacter, edit.Error);
            Assert.IsNull(edit.Source);
        }

        [Test]
        public void SetCategory_KeyInsideAValueOrALaterComment_IsNotTouched()
        {
            string source = "--[[@coreai\ndescription: category: not a key\n]]\n-- category: later\nprint(1)";

            ModFolderHeaderEdit edit = ModFolderHeaderEditor.SetCategory(source, "Games");

            Assert.AreEqual("--[[@coreai\ncategory: Games\ndescription: category: not a key\n]]\n-- category: later\nprint(1)",
                edit.Source);
        }

        [Test]
        public void DiffersOnlyInCategory_OwnEdit_IsTrue_AndACodeChange_IsFalse()
        {
            string moved = ModFolderHeaderEditor.SetCategory(BlockSource, "Games").Source;
            string inserted = ModFolderHeaderEditor.SetCategory("print(1)", "Games").Source;

            Assert.IsTrue(ModFolderHeaderEditor.DiffersOnlyInCategory(BlockSource, moved));
            Assert.IsTrue(ModFolderHeaderEditor.DiffersOnlyInCategory("print(1)", inserted));
            Assert.IsTrue(ModFolderHeaderEditor.DiffersOnlyInCategory(BlockSource, BlockSource));
            Assert.IsFalse(ModFolderHeaderEditor.DiffersOnlyInCategory(BlockSource, moved.Replace("go", "stop")));
            Assert.IsFalse(ModFolderHeaderEditor.DiffersOnlyInCategory(BlockSource,
                moved.Replace("name: Lane Racer", "name: Other")));
            Assert.IsFalse(ModFolderHeaderEditor.DiffersOnlyInCategory(null, moved));
        }
    }
}
