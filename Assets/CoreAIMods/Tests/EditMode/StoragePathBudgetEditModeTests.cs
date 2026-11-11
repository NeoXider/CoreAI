using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using CoreAI.Ai;
using CoreAI.Infrastructure.Lua;
using CoreAI.Mods.WorldPackages;
using CoreAI.Tests.EditMode.RbxApi.Acceptance;
using Cysharp.Threading.Tasks;
using Newtonsoft.Json;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode
{
    /// <summary>
    /// The short generated folder names (<see cref="CoreAiStorageNames"/>): new store namespaces and mod folders get
    /// them, a folder an earlier build wrote under its long name keeps loading, updating in place and deleting there,
    /// every store path fits a storage root of 121 characters with a store id, and a build that knows only the long
    /// names never reads a wrong mod from a store this build wrote.
    /// </summary>
    [TestFixture]
    public sealed class StoragePathBudgetEditModeTests
    {
        private const string StoreId = "block-world-demo";

        /// <summary>The longest storage root every store path fits with a store id (a confirmed world load's staging is the longest).</summary>
        private const int SupportedRootLength = 121;

        private readonly List<string> _directories = new();

        [TearDown]
        public void DeleteTemporaryDirectories()
        {
            foreach (string directory in _directories)
            {
                if (Directory.Exists(directory))
                {
                    Directory.Delete(directory, true);
                }
            }

            _directories.Clear();
        }

        /// <summary>Golden names: the short ones are the first 128 bits of the SHA-256 in lowercase base32; the long ones are unchanged.</summary>
        [Test]
        public void Names_AreTheDigestPrefixInBase32_AndTheLongNamesAreUnchanged()
        {
            Assert.AreEqual("m-zklycewkdo64v6wcggzzui64ju", CoreAiStorageNames.ModFolderName("a"));
            Assert.AreEqual("id-ca978112ca1bbdcafac231b39a23dc4da786eff8147c4e72b9807785afee48bb",
                CoreAiStorageNames.LegacyModFolderName("a"));
            Assert.AreEqual("id-zklycewkdo64v6wcggzzui64jwtyn37ycr6e44vzqb3yll7ojc5q",
                CoreAiStorageNames.LegacyCompactModFolderName("a"));
            Assert.AreEqual("s-nhbu52clvapwde5skj4whwatwa", CoreAiStorageNames.StoreFolderName(StoreId));
            Assert.AreEqual("id-69c34ee84ba81f6193b2527963d813b0cc27684f25227af1919b92c0277a1e0c",
                CoreAiStorageNames.LegacyStoreFolderName(StoreId));
            Assert.AreEqual(28, CoreAiStorageNames.ModFolderName("a much longer mod id than the others").Length);
            Assert.AreNotEqual(CoreAiStorageNames.ModFolderName("Case"), CoreAiStorageNames.ModFolderName("case"),
                "ids that differ only in case get different folders (lowercase base32 is case-safe on Windows)");
        }

        /// <summary>A fresh store with a store id writes the short namespace and mod folders.</summary>
        [Test]
        public void FreshStore_WritesShortNames()
        {
            string root = NewRoot(0);
            FileLuaModSourceStore store = new(root, storeId: StoreId, persistenceSyncAsync: Durable);

            store.SaveStrict("a", "return 'a'", Manifest("a"));

            string folder = Path.Combine(root, "Stores", "s-nhbu52clvapwde5skj4whwatwa", "m-zklycewkdo64v6wcggzzui64ju");
            Assert.IsTrue(File.Exists(Path.Combine(folder, "manifest.json")));
            Assert.IsTrue(store.TryLoad("a", out string source, out _));
            Assert.AreEqual("return 'a'", source);
        }

        /// <summary>
        /// Old-name twin: a store 7.56.0 wrote (long namespace and mod folders) loads, updates in place, deletes, and a
        /// new mod saved into it gets the short name beside the old ones.
        /// </summary>
        [Test]
        public void StoreWrittenWithLongNames_LoadsUpdatesInPlaceAndDeletes_AndANewModGetsAShortName()
        {
            string root = NewRoot(0);
            string namespaceFolder = Path.Combine(root, "Stores", CoreAiStorageNames.LegacyStoreFolderName(StoreId));
            string oldFolder = Path.Combine(namespaceFolder, CoreAiStorageNames.LegacyModFolderName("a"));
            Directory.CreateDirectory(oldFolder);
            File.WriteAllText(Path.Combine(oldFolder, "manifest.json"), JsonConvert.SerializeObject(Manifest("a"), Formatting.Indented));
            File.WriteAllText(Path.Combine(oldFolder, "main.lua"), "return 'old'");
            FileLuaModSourceStore store = new(root, storeId: StoreId, persistenceSyncAsync: Durable);

            Assert.IsTrue(store.TryLoad("a", out string source, out LuaModManifest manifest));
            Assert.AreEqual("return 'old'", source);
            store.SaveStrict("a", "return 'updated'", manifest);
            store.SaveStrict("b", "return 'b'", Manifest("b"));

            Assert.AreEqual("return 'updated'", File.ReadAllText(Path.Combine(oldFolder, "main.lua")), "updated in place");
            Assert.IsTrue(File.Exists(Path.Combine(namespaceFolder, CoreAiStorageNames.ModFolderName("b"), "manifest.json")));
            Assert.IsFalse(Directory.Exists(Path.Combine(namespaceFolder, CoreAiStorageNames.ModFolderName("a"))));
            Assert.AreEqual(2, store.List().Count);
            store.DeleteStrict("a");
            Assert.IsFalse(Directory.Exists(oldFolder), "deleted under its old name");
            Assert.IsFalse(store.TryLoad("a", out _, out _));
            Assert.AreEqual(1, store.List().Count);
        }

        /// <summary>
        /// After a downgrade and back a mod can sit under both names: it is listed once, the long folder (the older
        /// build's later write) is the one read, and a delete removes both.
        /// </summary>
        [Test]
        public void ModUnderBothNames_IsListedOnce_ReadFromTheLongFolder_AndDeletedEverywhere()
        {
            string root = NewRoot(0);
            FileLuaModSourceStore store = new(root, persistenceSyncAsync: Durable);
            store.SaveStrict("a", "return 'short'", Manifest("a"));
            string longFolder = Path.Combine(root, CoreAiStorageNames.LegacyModFolderName("a"));
            Directory.CreateDirectory(longFolder);
            File.WriteAllText(Path.Combine(longFolder, "manifest.json"), JsonConvert.SerializeObject(Manifest("a"), Formatting.Indented));
            File.WriteAllText(Path.Combine(longFolder, "main.lua"), "return 'long'");

            Assert.AreEqual(1, store.List().Count);
            Assert.IsTrue(store.TryLoad("a", out string source, out _));
            Assert.AreEqual("return 'long'", source);
            store.DeleteStrict("a");
            Assert.IsFalse(Directory.Exists(longFolder));
            Assert.IsFalse(Directory.Exists(Path.Combine(root, CoreAiStorageNames.ModFolderName("a"))));
            Assert.AreEqual(0, store.List().Count);
        }

        /// <summary>
        /// The downgrade case: code that knows only the long names finds no folder for a mod this build saved (and, under
        /// a store id, no namespace at all), so it sees an empty store or a listed mod it cannot load, which refuses
        /// a capture loudly; it never reads a wrong mod.
        /// </summary>
        [Test]
        public void StoreWithShortNames_SeenByCodeThatKnowsOnlyTheLongNames_IsEmptyOrUnloadable_NeverWrong()
        {
            string root = NewRoot(0);
            new FileLuaModSourceStore(root, storeId: StoreId, persistenceSyncAsync: Durable)
                .SaveStrict("a", "return 'a'", Manifest("a"));
            new FileLuaModSourceStore(root, persistenceSyncAsync: Durable).SaveStrict("b", "return 'b'", Manifest("b"));

            string oldNamespace = Path.Combine(root, "Stores", CoreAiStorageNames.LegacyStoreFolderName(StoreId));
            Assert.IsFalse(Directory.Exists(oldNamespace), "under a store id the old build sees an empty store");
            Assert.IsFalse(Directory.Exists(Path.Combine(root, CoreAiStorageNames.LegacyModFolderName("b"))),
                "in the default store the old build finds no folder for the mod, so it cannot load it");
            foreach (string folder in Directory.GetDirectories(root))
            {
                string manifestPath = Path.Combine(folder, "manifest.json");
                if (File.Exists(manifestPath))
                {
                    LuaModManifest listed = JsonConvert.DeserializeObject<LuaModManifest>(File.ReadAllText(manifestPath));
                    Assert.AreEqual(CoreAiStorageNames.ModFolderName(listed.Id), Path.GetFileName(folder),
                        "a listed manifest always names the mod its folder belongs to");
                }
            }
        }

        /// <summary>
        /// Every store path fits a storage root of 121 characters with a store id: a mod save, the exact source set a
        /// confirmed world load stages (the longest), its reopen, and the startup selection of the world package store.
        /// </summary>
        [Test]
        public async Task RootOfTheSupportedLength_KeepsEveryStorePath()
        {
            RequireWindowsPaths();
            string root = NewRoot(SupportedRootLength);
            FileLuaModSourceStore store = new(Path.Combine(root, "CoreAI", "Mods"), storeId: StoreId, persistenceSyncAsync: Durable);
            store.SaveStrict("a", "return 'a'", Manifest("a"));

            using IRbxWorldModSourceReplacement staged = await store.PrepareExactReplacementAsync(
                new[] { new RbxWorldModSource(Manifest("staged"), "return 'staged'") });
            Assert.IsTrue(staged.SourceStore.TryLoad("staged", out string stagedSource, out _));
            Assert.AreEqual("return 'staged'", stagedSource);
            staged.Activate();
            await staged.CompleteAsync();
            FileRbxWorldPackageStore packages = new(
                Path.Combine(root, "CoreAI", "Saves"), persistenceSyncAsync: Durable, startupNamespace: StoreId);
            RbxWorldPackageWriteResult selected = await packages.SelectStartupAsync(
                WorldPackageGuiWorld.WithHud("path-budget").Capture(new DateTime(2026, 11, 11, 19, 0, 0, DateTimeKind.Utc)),
                "manual", "budget");

            Assert.IsTrue(store.TryLoad("a", out string source, out _));
            Assert.AreEqual("return 'a'", source);
            Assert.IsTrue(selected.Success, selected.Error);
            StringAssert.Contains(Path.Combine("Startup", "Stores", CoreAiStorageNames.StoreFolderName(StoreId)),
                packages.StartupDirectoryForTests);
        }

        /// <summary>Twin: one character more, and the staging of a confirmed world load is refused with the named error.</summary>
        [Test]
        public async Task RootOneCharacterLonger_RefusesTheWorldLoadStaging_WithTheNamedError()
        {
            RequireWindowsPaths();
            string root = NewRoot(SupportedRootLength + 1);
            FileLuaModSourceStore store = new(Path.Combine(root, "CoreAI", "Mods"), storeId: StoreId, persistenceSyncAsync: Durable);

            LuaModSourceStorageRootTooLongException refused = null;
            try
            {
                using IRbxWorldModSourceReplacement staged = await store.PrepareExactReplacementAsync(
                    new[] { new RbxWorldModSource(Manifest("staged"), "return 1") });
            }
            catch (LuaModSourceStorageRootTooLongException exception)
            {
                refused = exception;
            }

            Assert.IsNotNull(refused);
            Assert.AreEqual(260, refused.RequiredPathLength);
        }

        private static UniTask<bool> Durable(System.Threading.CancellationToken cancellationToken)
        {
            return UniTask.FromResult(true);
        }

        private static LuaModManifest Manifest(string id)
        {
            return new LuaModManifest { Id = id, Name = id, Active = false };
        }

        private static void RequireWindowsPaths()
        {
            if (Path.DirectorySeparatorChar != '\\')
            {
                Assert.Ignore("the store's path limit applies to Windows paths only");
            }
        }

        /// <summary>A fresh directory of exactly <paramref name="length"/> characters, or a plain temporary one for 0.</summary>
        private string NewRoot(int length)
        {
            string parent = Path.Combine(Path.GetTempPath(), "CoreAI-Budget-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            string root = length > 0 && parent.Length + 1 < length
                ? Path.Combine(parent, new string('b', length - parent.Length - 1))
                : parent;
            if (length > 0 && root.Length != length)
            {
                Assert.Ignore("the temporary folder '" + Path.GetTempPath() + "' is too long for a root of " + length);
            }

            Directory.CreateDirectory(root);
            _directories.Add(parent);
            return root;
        }
    }
}
