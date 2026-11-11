using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using CoreAI.Ai;
using CoreAI.Infrastructure;
using CoreAI.Infrastructure.Lua;
using CoreAI.Mods.WorldPackages;
using CoreAI.Tests.EditMode.RbxApi.Acceptance;
using Cysharp.Threading.Tasks;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode
{
    /// <summary>
    /// A file store under a long storage root: a mod save that cannot be read back never reports success. The mod
    /// source store refuses every save into a folder too long for its Windows paths with a named error that says
    /// what to do, the seeder counts such a mod as failed instead of installed, stores written by 7.56.0 keep
    /// loading from their folders, and the world package store either writes a package it can read back or says
    /// it could not.
    /// </summary>
    [TestFixture]
    public sealed class FileStoreLongRootEditModeTests
    {
        /// <summary>The store id of the Block World demo, whose 93-character test root showed the defect.</summary>
        private const string DemoStoreId = "block-world-demo";

        /// <summary>
        /// What the store adds below <c>CoreAiStorageRoot</c> for a store id, the longest file it writes included:
        /// <c>\CoreAI\Mods\Stores\id-&lt;64 hex&gt;\id-&lt;64 hex&gt;\manifest.json.tmp</c>.
        /// </summary>
        private const int StoreSuffixLength = 173;

        private const int WindowsMaximumPathLength = 259;

        private readonly List<string> _directories = new();

        [SetUp]
        public void RequireWindowsPaths()
        {
            if (Path.DirectorySeparatorChar != '\\')
            {
                Assert.Ignore("the store's path limit applies to Windows paths only");
            }
        }

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

        /// <summary>
        /// The defect as the demo agent found it: a 93-character storage root, the demo's store id, a save. It is now
        /// refused with the named error (by the best-effort save and the strict one alike, and by the admission check),
        /// nothing is written, and nothing pretends to be stored.
        /// </summary>
        [Test]
        public void LongRoot_EverySave_IsRefusedWithANamedError_NeverSilently()
        {
            string root = NewRoot(93);
            using IDisposable scope = CoreAiStorageRoot.Override(root);
            FileLuaModSourceStore store = new(storeId: DemoStoreId, persistenceSyncAsync: _ => UniTask.FromResult(true));

            LuaModSourceStorageRootTooLongException refused = Assert.Throws<LuaModSourceStorageRootTooLongException>(
                () => store.Save("welcome", "return 'hi'", new LuaModManifest { Id = "welcome", Active = true }));
            LuaModSourceStorageRootTooLongException strict = Assert.Throws<LuaModSourceStorageRootTooLongException>(
                () => store.SaveStrict("welcome", "return 'hi'", new LuaModManifest { Id = "welcome", Active = true }));

            StringAssert.Contains(root, refused.Message);
            StringAssert.Contains("at most 259", refused.Message);
            StringAssert.Contains("CoreAiStorageRoot.Override", refused.Message);
            Assert.Greater(refused.RequiredPathLength, WindowsMaximumPathLength);
            Assert.IsNotNull(strict);
            Assert.IsFalse(((ILuaModSourceAdmission)store).CanAdmit("welcome", out string refusal));
            StringAssert.Contains("at most 259", refusal);
            Assert.IsFalse(store.TryLoad("welcome", out _, out _));
            Assert.AreEqual(0, store.List().Count);
            Assert.IsFalse(Directory.Exists(Path.Combine(root, "CoreAI", "Mods", "Stores"))
                           && Directory.GetFiles(Path.Combine(root, "CoreAI", "Mods", "Stores"), "*", SearchOption.AllDirectories).Length > 0,
                "a refused save writes nothing");
        }

        /// <summary>Twin: under a root short enough, the same save is kept and reads back.</summary>
        [Test]
        public void ShortRoot_TheSameSave_IsKeptAndReadsBack()
        {
            string root = NewRoot(WindowsMaximumPathLength - StoreSuffixLength);
            using IDisposable scope = CoreAiStorageRoot.Override(root);
            FileLuaModSourceStore store = new(storeId: DemoStoreId, persistenceSyncAsync: _ => UniTask.FromResult(true));

            store.Save("welcome", "return 'hi'", new LuaModManifest { Id = "welcome", Active = true });

            Assert.IsTrue(store.TryLoad("welcome", out string source, out LuaModManifest manifest));
            Assert.AreEqual("return 'hi'", source);
            Assert.AreEqual("welcome", manifest.Id);
            Assert.AreEqual(1, store.List().Count);
            Assert.IsTrue(((ILuaModSourceAdmission)store).CanAdmit("other", out string _));
        }

        /// <summary>
        /// The seeder's count tells the truth: under the long root every bundled mod is counted as failed with the
        /// store's reason, none as installed; the twin under a short root installs them all.
        /// </summary>
        [Test]
        public void Seeder_UnderALongRoot_CountsFailures_AndUnderAShortRootInstalls()
        {
            string longRoot = NewRoot(93);
            BundledModSeeder.SeedResult refused;
            using (CoreAiStorageRoot.Override(longRoot))
            {
                refused = new BundledModSeeder(
                    new FileLuaModSourceStore(storeId: DemoStoreId, persistenceSyncAsync: _ => UniTask.FromResult(true)),
                    new IBundledModSource[] { new ThreeMods() }).Seed();
            }

            string shortRoot = NewRoot(WindowsMaximumPathLength - StoreSuffixLength);
            BundledModSeeder.SeedResult installed;
            FileLuaModSourceStore kept;
            using (CoreAiStorageRoot.Override(shortRoot))
            {
                kept = new FileLuaModSourceStore(storeId: DemoStoreId, persistenceSyncAsync: _ => UniTask.FromResult(true));
                installed = new BundledModSeeder(kept, new IBundledModSource[] { new ThreeMods() }).Seed();
                Assert.AreEqual(3, kept.List().Count);
            }

            Assert.AreEqual(0, refused.Installed);
            Assert.AreEqual(3, refused.Failed);
            StringAssert.Contains("at most 259", refused.FirstFailure);
            StringAssert.Contains("failed=3", refused.ToString());
            Assert.AreEqual(3, installed.Installed);
            Assert.AreEqual(0, installed.Failed);
            Assert.AreEqual("", installed.FirstFailure);
        }

        /// <summary>A store that accepts a save and keeps nothing is caught by the read-back: the mod counts as failed.</summary>
        [Test]
        public void Seeder_StoreThatDropsASaveSilently_CountsItAsFailed()
        {
            BundledModSeeder.SeedResult result = new BundledModSeeder(
                new ForgetfulStore(), new IBundledModSource[] { new ThreeMods() }).Seed();

            Assert.AreEqual(0, result.Installed);
            Assert.AreEqual(3, result.Failed);
            StringAssert.Contains("does not hold the source", result.FirstFailure);
        }

        /// <summary>
        /// Golden: a store exactly as 7.56.0 wrote it (the store id and each mod id as SHA-256 hex folder names) keeps
        /// loading, lists, and an update is written into the same folder.
        /// </summary>
        [Test]
        public void Store_Written_By_7_56_0_KeepsLoadingFromItsFolders()
        {
            string root = NewRoot(0);
            string modFolder = Path.Combine(root, "Stores", "id-" + Sha256Hex(DemoStoreId), "id-" + Sha256Hex("welcome"));
            Directory.CreateDirectory(modFolder);
            File.WriteAllText(Path.Combine(modFolder, "manifest.json"), @"{
  ""Id"": ""welcome"",
  ""Name"": ""Welcome"",
  ""Description"": """",
  ""Version"": ""1.0.0"",
  ""Category"": ""Samples"",
  ""Tags"": """",
  ""Origin"": ""resources"",
  ""SeededVersion"": ""1.0.0"",
  ""SeededHash"": ""0badf00d"",
  ""Author"": """",
  ""OwnerActorId"": """",
  ""Capabilities"": """",
  ""Active"": true,
  ""UpdateAvailable"": false,
  ""Entry"": ""main.lua""
}");
            File.WriteAllText(Path.Combine(modFolder, "main.lua"), "-- @coreai\nreturn 'welcome'");

            FileLuaModSourceStore store = new(root, storeId: DemoStoreId, persistenceSyncAsync: _ => UniTask.FromResult(true));

            Assert.IsTrue(store.TryLoad("welcome", out string source, out LuaModManifest manifest));
            Assert.AreEqual("-- @coreai\nreturn 'welcome'", source);
            Assert.AreEqual("Samples", manifest.Category);
            Assert.AreEqual(1, store.List().Count);
            store.Save("welcome", "return 'updated'", manifest);
            Assert.AreEqual("return 'updated'", File.ReadAllText(Path.Combine(modFolder, "main.lua")));
            Assert.AreEqual(1, Directory.GetDirectories(Path.GetDirectoryName(modFolder)).Length, "no second folder");
        }

        /// <summary>
        /// The world package store under the same long root and with the same store id for its startup area: an
        /// autosave and a startup selection either succeed and read back, or report a failure; never a success
        /// without a readable package.
        /// </summary>
        [Test]
        public async Task WorldPackageStore_UnderALongRoot_NeverReportsAWriteItCannotReadBack()
        {
            string root = NewRoot(93);
            string saves = Path.Combine(root, "CoreAI", "Saves");
            FileRbxWorldPackageStore store = new(
                saves,
                persistenceSyncAsync: _ => UniTask.FromResult(true),
                startupNamespace: DemoStoreId);
            RbxWorldPackagePayload payload = WorldPackageGuiWorld.WithHud("worldpkg-long-root")
                .Capture(new DateTime(2026, 11, 11, 18, 0, 0, DateTimeKind.Utc));

            RbxWorldPackageWriteResult autosave = await store.CreateAutoAsync("execute_lua", payload);
            RbxWorldPackageWriteResult startup = await store.SelectStartupAsync(payload, "manual", "long");

            if (autosave.Success)
            {
                IReadOnlyList<RbxAutoSaveInfo> autosaves = store.ListAutoSaves();
                Assert.AreEqual(1, autosaves.Count);
                Assert.IsNotNull(RbxWorldPackageSerializer.ReadPackage(
                    File.ReadAllBytes(Path.Combine(saves, "Auto", autosaves[0].FileName))));
            }
            else
            {
                Assert.IsNotEmpty(autosave.Error, "a failed autosave says why");
            }

            if (startup.Success)
            {
                string[] selected = Directory.GetFiles(store.StartupDirectoryForTests, "*.world");
                Assert.AreEqual(1, selected.Length);
                Assert.IsNotNull(RbxWorldPackageSerializer.ReadPackage(File.ReadAllBytes(selected[0])));
            }
            else
            {
                Assert.IsNotEmpty(startup.Error, "a failed startup selection says why");
            }
        }

        /// <summary>
        /// A fresh directory whose full path is exactly <paramref name="length"/> characters, or a plain temporary one
        /// for 0; ignores the test when the temporary folder itself is too long for a short root.
        /// </summary>
        private string NewRoot(int length)
        {
            string parent = Path.Combine(Path.GetTempPath(), "CoreAI-Root-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            string root = length > 0 && parent.Length + 1 < length
                ? Path.Combine(parent, new string('r', length - parent.Length - 1))
                : parent;
            if (length > 0 && root.Length > length && length < 93)
            {
                Assert.Ignore("the temporary folder '" + Path.GetTempPath() + "' is too long for a short-root twin");
            }

            Directory.CreateDirectory(root);
            _directories.Add(parent);
            return root;
        }

        private static string Sha256Hex(string text)
        {
            using SHA256 algorithm = SHA256.Create();
            byte[] digest = algorithm.ComputeHash(new UTF8Encoding(false, true).GetBytes(text));
            StringBuilder hex = new(digest.Length * 2);
            foreach (byte value in digest)
            {
                hex.Append(value.ToString("x2"));
            }

            return hex.ToString();
        }

        private sealed class ThreeMods : IBundledModSource
        {
            public string Origin => "resources";

            public IReadOnlyList<BundledMod> Load()
            {
                return new[]
                {
                    new BundledMod("welcome", "-- @coreai\n-- version: 1.0.0\nreturn 1", "1.0.0"),
                    new BundledMod("coins", "-- @coreai\n-- version: 1.0.0\nreturn 2", "1.0.0"),
                    new BundledMod("timer", "-- @coreai\n-- version: 1.0.0\nreturn 3", "1.0.0")
                };
            }
        }

        /// <summary>A host store that accepts every save and keeps nothing.</summary>
        private sealed class ForgetfulStore : ILuaModSourceStore
        {
            public void Save(string id, string source, LuaModManifest manifest)
            {
            }

            public bool TryLoad(string id, out string source, out LuaModManifest manifest)
            {
                source = "";
                manifest = null;
                return false;
            }

            public IReadOnlyList<LuaModManifest> List()
            {
                return Array.Empty<LuaModManifest>();
            }

            public void SetActive(string id, bool active)
            {
            }

            public void Delete(string id)
            {
            }
        }
    }
}
