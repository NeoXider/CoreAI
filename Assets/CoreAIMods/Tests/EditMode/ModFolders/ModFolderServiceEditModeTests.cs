using System;
using System.Collections.Generic;
using CoreAI.Mods.Folders;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode.ModFolders
{
    /// <summary>The folder operations over a store that keeps folders in mod headers, with their persistence and named failures.</summary>
    [TestFixture]
    public sealed class ModFolderServiceEditModeTests
    {
        private static FakeModFolderStore Store()
        {
            return new FakeModFolderStore()
                .Add("racer", "Games/Arcade")
                .Add("retro", "Games/Arcade/Retro")
                .Add("tetris", "Games/Puzzle")
                .Add("old1", "GamesOld")
                .Add("clock", "Tools")
                .Add("loose", null);
        }

        private static bool HasFolder(ModFolderTree tree, string path)
        {
            return tree.TryFindFolder(path, out _);
        }

        [Test]
        public void CreateFolder_PersistsAnEmptyFolderThatSurvivesARestart()
        {
            FakeModFolderStore store = Store();

            ModFolderResult result = new ModFolderService(store).CreateFolder(" Archive / 2025 ");

            Assert.IsTrue(result.Succeeded, result.Message);
            Assert.AreEqual("Archive/2025", result.Path.Value);
            Assert.IsTrue(result.CreatedFolder);
            ModFolderTree tree = new ModFolderService(store.Reopen()).BuildTree();
            Assert.IsTrue(tree.TryFindFolder("Archive/2025", out int index));
            Assert.IsTrue(tree.GetFolder(index).IsEmpty);
        }

        [Test]
        public void CreateFolder_ExistingFolderInAnyCasing_FailsWithFolderAlreadyExists()
        {
            ModFolderService service = new(Store());

            ModFolderResult result = service.CreateFolder("games/arcade");

            Assert.AreEqual(ModFolderErrorCode.FolderAlreadyExists, result.Error);
            Assert.AreEqual("FOLDER_ALREADY_EXISTS", result.Code);
            StringAssert.Contains("Games/Arcade", result.Message);
        }

        [Test]
        public void CreateFolder_ReusesTheCasingOfExistingParents()
        {
            FakeModFolderStore store = Store();

            ModFolderResult result = new ModFolderService(store).CreateFolder("games/new");

            Assert.AreEqual("Games/new", result.Path.Value);
            CollectionAssert.Contains(store.Folders, "Games/new");
        }

        [TestCase("Games/[x]", ModFolderErrorCode.InvalidCharacter)]
        [TestCase("", ModFolderErrorCode.RootNotAllowed)]
        [TestCase("a/b/c/d/e/f/g/h/i", ModFolderErrorCode.TooDeep)]
        public void CreateFolder_BadPath_FailsWithItsNamedErrorAndWritesNothing(string path, ModFolderErrorCode expected)
        {
            FakeModFolderStore store = Store();

            ModFolderResult result = new ModFolderService(store).CreateFolder(path);

            Assert.AreEqual(expected, result.Error);
            Assert.AreEqual(0, store.FolderListWrites);
        }

        [Test]
        public void RenameFolder_MovesEveryModInTheBranch_AndLeavesTheSiblingAlone()
        {
            FakeModFolderStore store = Store();

            ModFolderResult result = new ModFolderService(store).RenameFolder("Games", "Play");

            Assert.IsTrue(result.Succeeded, result.Message);
            Assert.AreEqual(3, result.ModsChanged);
            Assert.AreEqual("Games", result.PreviousPath.Value);
            Assert.AreEqual("Play", result.Path.Value);
            Assert.AreEqual("Play/Arcade", store.FolderOf("racer"));
            Assert.AreEqual("Play/Arcade/Retro", store.FolderOf("retro"));
            Assert.AreEqual("Play/Puzzle", store.FolderOf("tetris"));
            Assert.AreEqual("GamesOld", store.FolderOf("old1"));
            ModFolderTree tree = new ModFolderService(store.Reopen()).BuildTree();
            Assert.IsFalse(HasFolder(tree, "Games"));
            Assert.IsTrue(HasFolder(tree, "Play/Arcade/Retro"));
        }

        [Test]
        public void RenameFolder_CaseOnly_IsAllowed()
        {
            FakeModFolderStore store = Store();

            ModFolderResult result = new ModFolderService(store).RenameFolder("Tools", "TOOLS");

            Assert.IsTrue(result.Succeeded, result.Message);
            Assert.AreEqual("TOOLS", store.FolderOf("clock"));
        }

        [Test]
        public void RenameFolder_OntoASibling_FailsWithFolderAlreadyExists()
        {
            FakeModFolderStore store = Store();

            ModFolderResult result = new ModFolderService(store).RenameFolder("Games/Arcade", "puzzle");

            Assert.AreEqual(ModFolderErrorCode.FolderAlreadyExists, result.Error);
            Assert.AreEqual("Games/Arcade", store.FolderOf("racer"));
            Assert.AreEqual(0, store.MoveCalls.Count);
        }

        [TestCase("", "X", ModFolderErrorCode.RootNotAllowed)]
        [TestCase("Nowhere", "X", ModFolderErrorCode.FolderNotFound)]
        [TestCase("Tools", "A/B", ModFolderErrorCode.InvalidName)]
        [TestCase("Tools", "..", ModFolderErrorCode.ReservedName)]
        [TestCase("Tools", " ", ModFolderErrorCode.EmptySegment)]
        public void RenameFolder_BadInput_FailsWithItsNamedError(string path, string name, ModFolderErrorCode expected)
        {
            Assert.AreEqual(expected, new ModFolderService(Store()).RenameFolder(path, name).Error);
        }

        [Test]
        public void MoveFolder_IntoItselfOrItsSubfolder_FailsWithMoveIntoItself()
        {
            FakeModFolderStore store = Store();
            ModFolderService service = new(store);

            Assert.AreEqual(ModFolderErrorCode.MoveIntoItself, service.MoveFolder("Games", "Games").Error);
            Assert.AreEqual(ModFolderErrorCode.MoveIntoItself, service.MoveFolder("Games", "games/Arcade/Retro").Error);
            Assert.AreEqual(0, store.MoveCalls.Count);
        }

        [Test]
        public void MoveFolder_ToAnotherParent_MovesTheBranchAndKeepsTheEmptiedParent()
        {
            FakeModFolderStore store = Store();

            ModFolderResult result = new ModFolderService(store).MoveFolder("Games/Puzzle", "Tools");

            Assert.IsTrue(result.Succeeded, result.Message);
            Assert.AreEqual("Tools/Puzzle", store.FolderOf("tetris"));
            ModFolderTree tree = new ModFolderService(store.Reopen()).BuildTree();
            Assert.IsTrue(HasFolder(tree, "Games"));
            Assert.IsFalse(HasFolder(tree, "Games/Puzzle"));
        }

        [Test]
        public void MoveFolder_LastContentOfItsParent_KeepsTheParentAsAnEmptyFolder()
        {
            FakeModFolderStore store = new FakeModFolderStore().Add("a", "Outer/Inner");

            ModFolderResult result = new ModFolderService(store).MoveFolder("Outer/Inner", "");

            Assert.IsTrue(result.Succeeded, result.Message);
            Assert.AreEqual("Inner", store.FolderOf("a"));
            CollectionAssert.AreEqual(new[] { "Outer" }, store.Folders);
        }

        [Test]
        public void MoveFolder_EmptyDeclaredFolder_MovesItsPersistedEntries()
        {
            FakeModFolderStore store = Store().WithFolders("Archive/2025/Q1");

            ModFolderResult result = new ModFolderService(store).MoveFolder("Archive/2025", "Tools");

            Assert.IsTrue(result.Succeeded, result.Message);
            CollectionAssert.AreEquivalent(new[] { "Archive", "Tools/2025/Q1" }, store.Folders);
        }

        [Test]
        public void MoveFolder_OntoAnExistingName_FailsWithFolderAlreadyExists()
        {
            FakeModFolderStore store = Store().Add("other", "Tools/Arcade");

            ModFolderResult result = new ModFolderService(store).MoveFolder("Games/Arcade", "Tools");

            Assert.AreEqual(ModFolderErrorCode.FolderAlreadyExists, result.Error);
        }

        [Test]
        public void MoveFolder_BranchWouldGetTooDeep_FailsWithTooDeep()
        {
            FakeModFolderStore store = Store().Add("deep", "a/b/c/d/e/f");

            ModFolderResult result = new ModFolderService(store).MoveFolder("Games", "a/b/c/d/e/f");

            Assert.AreEqual(ModFolderErrorCode.TooDeep, result.Error);
            Assert.AreEqual("Games/Arcade", store.FolderOf("racer"));
        }

        [Test]
        public void MoveFolder_SameParent_ChangesNothing()
        {
            FakeModFolderStore store = Store();

            ModFolderResult result = new ModFolderService(store).MoveFolder("Games/Arcade", "Games");

            Assert.IsTrue(result.Succeeded);
            Assert.IsFalse(result.Changed);
            Assert.AreEqual(0, store.MoveCalls.Count);
        }

        [Test]
        public void MoveFolder_AModWriteFails_MovesTheOthersBackAndRestoresTheFolderList()
        {
            FakeModFolderStore store = Store().WithFolders("Games/Arcade/Empty");
            store.FailFolderWritesFor.Add("retro");

            ModFolderResult result = new ModFolderService(store).MoveFolder("Games/Arcade", "Tools");

            Assert.AreEqual(ModFolderErrorCode.StoreFailed, result.Error);
            StringAssert.Contains("retro", result.Message);
            Assert.AreEqual("Games/Arcade", store.FolderOf("racer"));
            Assert.AreEqual("Games/Arcade/Retro", store.FolderOf("retro"));
            CollectionAssert.AreEqual(new[] { "Games/Arcade/Empty" }, store.Folders);
        }

        [Test]
        public void MoveMod_KeepsTheFolderItEmpties()
        {
            FakeModFolderStore store = Store();

            ModFolderResult result = new ModFolderService(store).MoveMod("clock", "Games");

            Assert.IsTrue(result.Succeeded, result.Message);
            Assert.AreEqual("Games", store.FolderOf("clock"));
            Assert.AreEqual("Tools", result.PreviousPath.Value);
            ModFolderTree tree = new ModFolderService(store.Reopen()).BuildTree();
            Assert.IsTrue(tree.TryFindFolder("Tools", out int tools));
            Assert.IsTrue(tree.GetFolder(tools).IsEmpty);
        }

        [Test]
        public void MoveMod_ToANewFolder_CreatesIt_InTheExistingCasing()
        {
            FakeModFolderStore store = Store();

            ModFolderResult result = new ModFolderService(store).MoveMod("loose", "games/arcade/New One");

            Assert.IsTrue(result.Succeeded, result.Message);
            Assert.IsTrue(result.CreatedFolder);
            Assert.AreEqual("Games/Arcade/New One", store.FolderOf("loose"));
        }

        [Test]
        public void MoveMod_ToTheTopLevel_ClearsTheHeaderCategory()
        {
            FakeModFolderStore store = Store();

            Assert.IsTrue(new ModFolderService(store).MoveMod("racer", "").Succeeded);

            Assert.AreEqual("", store.FolderOf("racer"));
            StringAssert.Contains("category:\n", store.SourceOf("racer"));
        }

        [Test]
        public void MoveMod_UnknownModOrBadPath_FailsWithItsNamedError()
        {
            ModFolderService service = new(Store());

            Assert.AreEqual(ModFolderErrorCode.ModNotFound, service.MoveMod("nope", "Games").Error);
            Assert.AreEqual(ModFolderErrorCode.EmptySegment, service.MoveMod("racer", "a//b").Error);
        }

        [Test]
        public void MoveMod_AlreadyThere_ChangesNothing()
        {
            FakeModFolderStore store = Store();

            ModFolderResult result = new ModFolderService(store).MoveMod("racer", "Games/Arcade");

            Assert.IsTrue(result.Succeeded);
            Assert.IsFalse(result.Changed);
            Assert.AreEqual(0, store.MoveCalls.Count);
        }

        [Test]
        public void MoveMod_WriteFails_RestoresTheFolderList()
        {
            FakeModFolderStore store = Store();
            store.FailFolderWritesFor.Add("clock");

            ModFolderResult result = new ModFolderService(store).MoveMod("clock", "Games");

            Assert.AreEqual(ModFolderErrorCode.StoreFailed, result.Error);
            Assert.AreEqual(0, store.Folders.Count);
            Assert.AreEqual("Tools", store.FolderOf("clock"));
        }

        [Test]
        public void DeleteFolder_WithAModDeepInside_FailsWithFolderNotEmpty()
        {
            FakeModFolderStore store = Store();

            ModFolderResult result = new ModFolderService(store).DeleteFolder("Games");

            Assert.AreEqual(ModFolderErrorCode.FolderNotEmpty, result.Error);
            StringAssert.Contains("3 mod(s)", result.Message);
        }

        [Test]
        public void DeleteFolder_EmptyBranch_RemovesItWithItsEmptySubfolders_AndKeepsTheParent()
        {
            FakeModFolderStore store = new FakeModFolderStore().WithFolders("Archive/2025/Q1", "Archive/2025/Q2");

            ModFolderResult result = new ModFolderService(store).DeleteFolder("archive/2025");

            Assert.IsTrue(result.Succeeded, result.Message);
            CollectionAssert.AreEqual(new[] { "Archive" }, store.Folders);
            Assert.AreEqual(ModFolderErrorCode.RootNotAllowed, new ModFolderService(store).DeleteFolder("").Error);
            Assert.AreEqual(ModFolderErrorCode.FolderNotFound, new ModFolderService(store).DeleteFolder("Gone").Error);
        }

        [Test]
        public void SetBranchEnabled_Off_SwitchesExactlyTheBranch_AndTheSiblingStaysOn()
        {
            FakeModFolderStore store = Store();

            ModFolderResult result = new ModFolderService(store).SetBranchEnabled("Games", false);

            Assert.IsTrue(result.Succeeded, result.Message);
            Assert.AreEqual(3, result.ModsChanged);
            Assert.IsFalse(store.IsEnabled("racer"));
            Assert.IsFalse(store.IsEnabled("retro"));
            Assert.IsFalse(store.IsEnabled("tetris"));
            Assert.IsTrue(store.IsEnabled("old1"), "GamesOld is a sibling of Games, not inside it.");
            Assert.IsTrue(store.IsEnabled("clock"));
            Assert.IsTrue(store.IsEnabled("loose"));
        }

        [Test]
        public void SetBranchEnabled_On_StartsInLoadOrder_AndSkipsModsAlreadyOn()
        {
            FakeModFolderStore store = new FakeModFolderStore()
                .Add("c", "G", false, loadOrder: 2)
                .Add("a", "G", false, loadOrder: 5)
                .Add("b", "G", false)
                .Add("on", "G", true, loadOrder: 1);

            ModFolderResult result = new ModFolderService(store).SetBranchEnabled("g", true);

            Assert.AreEqual(3, result.ModsChanged);
            CollectionAssert.AreEqual(new[] { "b+", "c+", "a+" }, store.EnableCalls);
        }

        [Test]
        public void SetBranchEnabled_TopLevel_SwitchesEveryMod()
        {
            FakeModFolderStore store = Store();

            ModFolderResult result = new ModFolderService(store).SetBranchEnabled("", false);

            Assert.AreEqual(6, result.ModsChanged);
        }

        [Test]
        public void SetBranchEnabled_OneModFails_TheRestStillSwitch()
        {
            FakeModFolderStore store = Store();
            store.FailEnableFor.Add("retro");

            ModFolderResult result = new ModFolderService(store).SetBranchEnabled("Games", false);

            Assert.AreEqual(ModFolderErrorCode.StoreFailed, result.Error);
            Assert.AreEqual(2, result.ModsChanged);
            CollectionAssert.AreEqual(new[] { "retro" }, result.FailedModIds);
            Assert.IsFalse(store.IsEnabled("racer"));
            Assert.IsTrue(store.IsEnabled("retro"));
        }

        [Test]
        public void SetBranchEnabled_UnknownFolder_FailsWithFolderNotFound()
        {
            Assert.AreEqual(ModFolderErrorCode.FolderNotFound,
                new ModFolderService(Store()).SetBranchEnabled("Nope", true).Error);
        }

        [Test]
        public void FolderListWriteFails_CreateFailsWithStoreFailed()
        {
            FakeModFolderStore store = Store();
            store.FailFolderListWrites = true;

            ModFolderResult result = new ModFolderService(store).CreateFolder("New");

            Assert.AreEqual(ModFolderErrorCode.StoreFailed, result.Error);
            StringAssert.Contains("folder list write refused", result.Message);
        }

        [Test]
        public void UnreadableStore_FailsWithStoreFailed_AndCancellationPropagates()
        {
            Assert.AreEqual(ModFolderErrorCode.StoreFailed,
                new ModFolderService(new ThrowingStore(new InvalidOperationException("disk gone"))).MoveMod("a", "b").Error);
            Assert.Throws<OperationCanceledException>(() =>
                new ModFolderService(new ThrowingStore(new OperationCanceledException())).MoveMod("a", "b"));
        }

        [Test]
        public void ResolveFolder_ReturnsTheExistingCasing_AndWritesNothing()
        {
            FakeModFolderStore store = Store();
            ModFolderService service = new(store);

            ModFolderResult existing = service.ResolveFolder("games/ARCADE");
            ModFolderResult created = service.ResolveFolder("AI/Made");

            Assert.AreEqual("Games/Arcade", existing.Path.Value);
            Assert.IsFalse(existing.CreatedFolder);
            Assert.AreEqual("AI/Made", created.Path.Value);
            Assert.IsTrue(created.CreatedFolder);
            Assert.AreEqual(ModFolderErrorCode.InvalidCharacter, service.ResolveFolder("AI:Made").Error);
            Assert.AreEqual(0, store.FolderListWrites);
        }

        [Test]
        public void LegacyFolderTheRulesRefuse_CanBeRenamedToAValidName()
        {
            FakeModFolderStore store = new FakeModFolderStore().AddSource("w",
                "--[[@coreai\nid: w\ncategory: Tools: WIP\n]]\nprint(1)\n");
            ModFolderService service = new(store);

            Assert.AreEqual(ModFolderErrorCode.InvalidCharacter, service.MoveFolder("Tools: WIP", "Games").Error);
            ModFolderResult renamed = service.RenameFolder("Tools: WIP", "Work in progress");

            Assert.IsTrue(renamed.Succeeded, renamed.Message);
            Assert.AreEqual("Work in progress", store.FolderOf("w"));
        }

        private sealed class ThrowingStore : IModFolderStore
        {
            private readonly Exception _exception;

            public ThrowingStore(Exception exception)
            {
                _exception = exception;
            }

            public IReadOnlyList<ModFolderModEntry> ListMods()
            {
                throw _exception;
            }

            public IReadOnlyList<string> LoadFolders()
            {
                return Array.Empty<string>();
            }

            public void SaveFolders(IReadOnlyList<string> folders)
            {
            }

            public void SetModFolder(string modId, string folderPath)
            {
            }

            public void SetModEnabled(string modId, bool enabled)
            {
            }
        }
    }
}
