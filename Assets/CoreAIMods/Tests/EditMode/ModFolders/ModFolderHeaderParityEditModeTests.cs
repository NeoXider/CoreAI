using System.IO;
using CoreAI.Ai;
using CoreAI.Mods.Folders;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode.ModFolders
{
    /// <summary>
    /// The folder header editor and the real header parser (<see cref="LuaModHeader"/>) agree: the editor reads
    /// the category the parser reads, and after an edit the parser sees the new folder with every other header
    /// field unchanged.
    /// </summary>
    [TestFixture]
    public sealed class ModFolderHeaderParityEditModeTests
    {
        private static readonly string[] Corpus =
        {
            "--[[@coreai\nid: racer\nname: Lane Racer\nversion: 1.5.0\nactive: false\ncapabilities: All\ncategory: Samples\nauthor: CoreAI\ndescription: A racer: fast.\ntags: arcade, cars\n]]\nprint(1)\n",
            "--[[@coreai\r\nid: crlf\r\ncategory: player/controllers\r\n]]\r\nprint(1)\r\n",
            "--[[@coreai id: oneline ]]\nprint(1)",
            "--[[@coreai\nid: nocat\nname: No Category\n]]\n",
            "-- @coreai id: lines\n-- @coreai name: Lines\n-- @coreai category: utility\nprint(1)",
            "\n\n-- @coreai id: lead\nprint(1)",
            "-- plain comment\nprint(1)",
            "",
            "--[[@coreai\nCATEGORY: Upper\ncategory:   last wins  \n]]",
            "--[[@coreai\nid: unclosed\nprint(1)",
            "  --[[@coreai\nid: indented\ncategory: X\n]]"
        };

        [Test]
        public void ReadCategory_MatchesTheHeaderParser()
        {
            foreach (string source in Corpus)
            {
                Assert.AreEqual(LuaModHeader.Parse(source, "fallback").Category, ModFolderHeaderEditor.ReadCategory(source),
                    source);
            }
        }

        [Test]
        public void BundledSamples_ReadAndMoveWithEveryOtherFieldUnchanged()
        {
            const string samplesFolder = "Assets/CoreAIMods/Runtime/Resources/CoreAIMods";
            if (!Directory.Exists(samplesFolder))
            {
                Assert.Ignore("The bundled samples are read relative to the repository root; the current directory is " +
                              System.Environment.CurrentDirectory);
            }

            string[] files = Directory.GetFiles(samplesFolder, "*.lua");
            Assert.IsNotEmpty(files);
            foreach (string file in files)
            {
                string source = File.ReadAllText(file);
                LuaModHeader before = LuaModHeader.Parse(source, "fallback");
                Assert.AreEqual(before.Category, ModFolderHeaderEditor.ReadCategory(source), file);

                ModFolderHeaderEdit edit = ModFolderHeaderEditor.SetCategory(source, "Samples/Games");
                Assert.IsTrue(edit.Succeeded, file + ": " + edit.Message);
                LuaModHeader after = LuaModHeader.Parse(edit.Source, "fallback");
                Assert.AreEqual("Samples/Games", after.Category, file);
                Assert.AreEqual(before.Id, after.Id, file);
                Assert.AreEqual(before.Name, after.Name, file);
                Assert.AreEqual(before.Description, after.Description, file);
                Assert.AreEqual(source.Split('\n').Length, edit.Source.Split('\n').Length,
                    file + ": a sample that already has a category keeps its line count");
                Assert.IsTrue(ModFolderHeaderEditor.DiffersOnlyInCategory(source, edit.Source), file);
            }
        }

        [TestCase("Games/Arcade")]
        [TestCase("")]
        [TestCase("Jeux vidéo/Retro (beta)")]
        public void SetCategory_ParserSeesTheNewFolder_AndEveryOtherFieldUnchanged(string folder)
        {
            foreach (string source in Corpus)
            {
                ModFolderHeaderEdit edit = ModFolderHeaderEditor.SetCategory(source, folder);
                if (!edit.Succeeded)
                {
                    Assert.AreEqual(ModFolderErrorCode.HeaderUnclosed, edit.Error, source);
                    continue;
                }

                LuaModHeader before = LuaModHeader.Parse(source, "fallback");
                LuaModHeader after = LuaModHeader.Parse(edit.Source, "fallback");
                Assert.AreEqual(folder, after.Category, source);
                Assert.AreEqual(before.Id, after.Id, source);
                Assert.AreEqual(before.Name, after.Name, source);
                Assert.AreEqual(before.Version, after.Version, source);
                Assert.AreEqual(before.Capabilities, after.Capabilities, source);
                Assert.AreEqual(before.Author, after.Author, source);
                Assert.AreEqual(before.Description, after.Description, source);
                Assert.AreEqual(before.Tags, after.Tags, source);
                Assert.AreEqual(before.Active, after.Active, source);
            }
        }
    }
}
