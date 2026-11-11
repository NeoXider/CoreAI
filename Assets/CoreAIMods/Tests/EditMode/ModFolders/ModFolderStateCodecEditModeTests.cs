using CoreAI.Mods.Folders;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode.ModFolders
{
    /// <summary>The text formats of the persisted folders and of a viewer's fold state.</summary>
    [TestFixture]
    public sealed class ModFolderStateCodecEditModeTests
    {
        [Test]
        public void Folders_RoundTrip_SortedDeduplicatedWithoutTheTopLevel()
        {
            string text = ModFolderStateCodec.EncodeFolders(new[] { "Tools", " games / Arcade ", "", "GAMES/arcade", "Games" });

            Assert.AreEqual("coreai-mod-folders 1\nfolder Games\nfolder games/Arcade\nfolder Tools\n", text);
            ModFolderDocument read = ModFolderStateCodec.DecodeFolders(text);
            Assert.IsTrue(read.Succeeded);
            Assert.AreEqual(0, read.SkippedLines);
            CollectionAssert.AreEqual(new[] { "Games", "games/Arcade", "Tools" }, read.Folders);
        }

        [Test]
        public void Folders_EncodeOrder_IsStable()
        {
            string first = ModFolderStateCodec.EncodeFolders(new[] { "b", "A", "a/c" });
            string second = ModFolderStateCodec.EncodeFolders(new[] { "a/c", "b", "A" });

            Assert.AreEqual(first, second);
            Assert.AreEqual("coreai-mod-folders 1\nfolder A\nfolder a/c\nfolder b\n", first);
        }

        [Test]
        public void Folders_EmptyText_IsAnEmptyDocument()
        {
            ModFolderDocument read = ModFolderStateCodec.DecodeFolders("");

            Assert.IsTrue(read.Succeeded);
            Assert.AreEqual(0, read.Folders.Count);
        }

        [Test]
        public void Folders_UnknownHeader_IsRefusedWithUnsupportedFormat()
        {
            ModFolderDocument read = ModFolderStateCodec.DecodeFolders("coreai-mod-folders 2\nfolder A\n");

            Assert.AreEqual(ModFolderErrorCode.UnsupportedFormat, read.Error);
            Assert.AreEqual(0, read.Folders.Count);
            StringAssert.Contains("coreai-mod-folders 2", read.Message);
        }

        [Test]
        public void Folders_BadLines_AreSkippedAndCounted()
        {
            ModFolderDocument read = ModFolderStateCodec.DecodeFolders(
                "﻿coreai-mod-folders 1\r\nfolder A\r\nfolder B//C\r\nunknown line\r\nfolder a\r\n\r\nfolder [x]\r\n");

            Assert.IsTrue(read.Succeeded);
            CollectionAssert.AreEqual(new[] { "A" }, read.Folders);
            Assert.AreEqual(4, read.SkippedLines);
        }

        [Test]
        public void FoldState_RoundTrip_KeepsDefaultAndToggles()
        {
            ModFolderFoldState state = new(false);
            state.SetExpanded(ModFolderPath.FromStored("Games"), true);
            state.SetExpanded(ModFolderPath.FromStored("Tools: legacy"), true);

            string text = ModFolderStateCodec.EncodeFoldState(state);
            ModFolderFoldStateDocument read = ModFolderStateCodec.DecodeFoldState(text);

            Assert.AreEqual("coreai-mod-folds 1\ndefault collapsed\ntoggled Games\ntoggled Tools: legacy\n", text);
            Assert.IsTrue(read.Succeeded);
            Assert.IsFalse(read.State.ExpandedByDefault);
            Assert.IsTrue(read.State.IsExpanded(ModFolderPath.FromStored("games")));
            Assert.IsTrue(read.State.IsExpanded(ModFolderPath.FromStored("Tools: legacy")));
            Assert.IsFalse(read.State.IsExpanded(ModFolderPath.FromStored("Other")));
        }

        [Test]
        public void FoldState_WrongHeader_IsRefusedAndFallsBackToTheDefault()
        {
            ModFolderFoldStateDocument read = ModFolderStateCodec.DecodeFoldState("coreai-mod-folders 1\n", false);

            Assert.AreEqual(ModFolderErrorCode.UnsupportedFormat, read.Error);
            Assert.IsFalse(read.State.ExpandedByDefault);
            Assert.AreEqual(0, read.State.ToggledCount);
        }

        [Test]
        public void TextListStore_RoundTripsThroughItsCallbacks()
        {
            string stored = null;
            TextModFolderListStore store = new(() => stored, text => stored = text);

            Assert.AreEqual(0, store.Load().Count);
            store.Save(new[] { "Tools", "Games/Empty" });

            Assert.AreEqual("coreai-mod-folders 1\nfolder Games/Empty\nfolder Tools\n", stored);
            CollectionAssert.AreEqual(new[] { "Games/Empty", "Tools" }, store.Load());
        }

        [Test]
        public void TextListStore_UnknownDocument_RefusesToLoad_SoAnOperationDoesNotOverwriteIt()
        {
            string stored = "something else\n";
            int writes = 0;
            TextModFolderListStore store = new(() => stored, text => writes++);

            Assert.Throws<System.IO.InvalidDataException>(() => store.Load());
            ModFolderResult result = new ModFolderService(new ListBackedStore(store)).CreateFolder("New");

            Assert.AreEqual(ModFolderErrorCode.StoreFailed, result.Error);
            Assert.AreEqual(0, writes);
        }

        [Test]
        public void InMemoryListStore_NormalizesWhatItKeeps()
        {
            InMemoryModFolderListStore store = new();

            store.Save(new[] { " b ", "A", "a", "" });

            CollectionAssert.AreEqual(new[] { "A", "b" }, store.Load());
        }

        [Test]
        public void FoldState_Rebase_FollowsARename_AndForget_DropsABranch()
        {
            ModFolderFoldState state = new();
            state.SetExpanded(ModFolderPath.FromStored("Games/Arcade"), false);
            state.SetExpanded(ModFolderPath.FromStored("Games/Arcade/Retro"), false);
            state.SetExpanded(ModFolderPath.FromStored("GamesOld"), false);

            state.Rebase(ModFolderPath.FromStored("Games"), ModFolderPath.FromStored("Play"));

            CollectionAssert.AreEqual(new[] { "GamesOld", "Play/Arcade", "Play/Arcade/Retro" }, state.GetToggledPaths());
            state.Forget(ModFolderPath.FromStored("Play"));
            CollectionAssert.AreEqual(new[] { "GamesOld" }, state.GetToggledPaths());
        }

        private sealed class ListBackedStore : IModFolderStore
        {
            private readonly IModFolderListStore _folders;

            public ListBackedStore(IModFolderListStore folders)
            {
                _folders = folders;
            }

            public System.Collections.Generic.IReadOnlyList<ModFolderModEntry> ListMods()
            {
                return System.Array.Empty<ModFolderModEntry>();
            }

            public System.Collections.Generic.IReadOnlyList<string> LoadFolders()
            {
                return _folders.Load();
            }

            public void SaveFolders(System.Collections.Generic.IReadOnlyList<string> folders)
            {
                _folders.Save(folders);
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
