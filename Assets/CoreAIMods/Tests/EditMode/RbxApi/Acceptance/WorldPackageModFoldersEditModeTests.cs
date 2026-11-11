using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Ai;
using CoreAI.Ai.LuaCs;
using CoreAI.Infrastructure.Lua;
using CoreAI.Mods.Folders;
using CoreAI.Mods.Rbx.Binding;
using CoreAI.Mods.Rbx.Instances;
using CoreAI.Mods.WorldPackages;
using CoreAI.Tests.EditMode;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode.RbxApi.Acceptance
{
    /// <summary>
    /// P20 mod folders in the world package and the stores around it: empty folders travel in the optional
    /// <c>mod_folders</c> manifest key (canonical, sorted, refused when not), a mod's own folder in its manifest's
    /// category, the session reads and restores the shared folder list, the file store keeps it beside the mods,
    /// and a bundled sample the player moved keeps its folder across a version bump.
    /// </summary>
    [TestFixture]
    public sealed class WorldPackageModFoldersEditModeTests
    {
        private const string WorldId = "worldpkg-folders";

        private static readonly DateTime CapturedAtUtc = new(2026, 11, 11, 18, 0, 0, DateTimeKind.Utc);

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

        /// <summary>Two persisted folders are written canonical and sorted, and the package round-trips byte for byte.</summary>
        [Test]
        public void ModFolders_TwoFolders_AreWrittenSortedAndRoundTrip()
        {
            MemoryFolderList folders = new("Games/Empty", "Archive", "games/empty");

            byte[] package = RbxWorldPackageSerializer.WritePackage(Capture(folders));
            JObject manifest = Manifest(package);

            CollectionAssert.AreEqual(new[] { "Archive", "Games/Empty" }, manifest["mod_folders"].Values<string>());
            RbxWorldPackagePayload read = RbxWorldPackageSerializer.ReadPackage(package);
            CollectionAssert.AreEqual(new[] { "Archive", "Games/Empty" }, read.ModFolders);
            CollectionAssert.AreEqual(package, RbxWorldPackageSerializer.WritePackage(read));
        }

        /// <summary>Twin: a world without persisted folders writes no key, so its manifest is the one 7.56.0 wrote.</summary>
        [Test]
        public void ModFolders_None_WritesNoKey()
        {
            Assert.IsNull(Manifest(RbxWorldPackageSerializer.WritePackage(Capture(new MemoryFolderList())))["mod_folders"]);
            Assert.IsNull(Manifest(RbxWorldPackageSerializer.WritePackage(Capture(null)))["mod_folders"]);
        }

        /// <summary>Golden: a package whose mod manifest carries a flat category reads identically, the category raw.</summary>
        [Test]
        public void ModFolders_FlatCategoryInAModManifest_ReadsIdentically()
        {
            InMemorySourceStore sources = new();
            sources.Save("clicker", "--[[@coreai\nid: clicker\ncategory: Samples\n]]\nlocal n = 1\n",
                new LuaModManifest { Id = "clicker", Name = "clicker", Category = "Samples", Active = false });
            byte[] package = RbxWorldPackageSerializer.WritePackage(Capture(null, sources));

            RbxWorldPackagePayload read = RbxWorldPackageSerializer.ReadPackage(package);

            Assert.AreEqual("Samples", read.Mods[0].Manifest.Category);
            Assert.AreEqual("Samples", ModFolderPath.FromStored(read.Mods[0].Manifest.Category).Value);
            CollectionAssert.AreEqual(package, RbxWorldPackageSerializer.WritePackage(read));
        }

        /// <summary>Every list a writer never produces is refused with the reason in the message.</summary>
        [Test]
        public void ModFolders_NonCanonicalLists_AreRefused()
        {
            byte[] package = RbxWorldPackageSerializer.WritePackage(Capture(new MemoryFolderList("Archive", "Games/Empty")));

            AssertRefused(package, new JArray(), "empty");
            AssertRefused(package, new JArray("Games/Empty", "Archive"), "out of order");
            AssertRefused(package, new JArray("Archive", "archive"), "duplicate");
            AssertRefused(package, new JArray(" Archive "), "not canonical");
            AssertRefused(package, new JArray(""), "top level");
            AssertRefused(package, new JArray("Games//Empty/"), "EMPTY_SEGMENT");
        }

        /// <summary>A capture refuses a persisted folder the format refuses, so a world never saves a list its reader refuses.</summary>
        [Test]
        public void ModFolders_InvalidPersistedFolder_FailsTheCapture()
        {
            RbxWorldPackageException refused = Assert.Throws<RbxWorldPackageException>(
                () => Capture(new MemoryFolderList(new string('x', 300))));

            StringAssert.Contains("is not a valid folder path", refused.Message);
        }

        /// <summary>The session captures the shared list and a confirmed load puts the package's folders into it.</summary>
        [Test]
        public async Task ModFolders_Session_CapturesTheSharedList_AndALoadRestoresIt()
        {
            using P3HeadlessSession session = new(new P3MemoryPackageStore());
            MemoryFolderList folders = new("Live/Empty");
            session.Controller.ModFolderListStore = folders;

            RbxWorldPackagePayload captured = session.Controller.CaptureCurrent();
            CollectionAssert.AreEqual(new[] { "Live/Empty" }, captured.ModFolders);
            RbxWorldPackagePayload saved = P3Fixtures.CapturePayload("Saved");
            RbxWorldPackageOptionalState state = saved.OptionalState;
            state.ModFolders = new[] { "Saved/One", "Saved/Two" };
            RbxWorldPackagePayload loaded = RbxWorldPackageSerializer.ReadPackage(RbxWorldPackageSerializer.WritePackage(
                new RbxWorldPackagePayload(saved.CapturedAtUtc, saved.Settings, saved.Tree, saved.Parts, saved.CameraCFrame,
                    saved.Mods, saved.Diagnostics, state)));

            RbxWorldLoadResult result = await session.Controller.LoadConfirmedAsync(loaded);

            Assert.IsTrue(result.Success, result.Error);
            CollectionAssert.AreEqual(new[] { "Saved/One", "Saved/Two" }, folders.Load());
        }

        /// <summary>The file list store keeps the folders beside the mods, survives a new instance, and is never listed as a mod.</summary>
        [Test]
        public void ModFolders_FileStore_PersistsBesideTheMods()
        {
            string root = NewDirectory();
            FileLuaModSourceStore sources = new(root);
            sources.Save("door", "local n = 1", new LuaModManifest { Id = "door", Name = "door" });
            FileModFolderListStore first = FileModFolderListStore.For(sources, () => true);

            first.Save(new[] { "Games/Empty", "Archive" });

            CollectionAssert.AreEqual(new[] { "Archive", "Games/Empty" }, FileModFolderListStore.For(sources, () => true).Load());
            Assert.AreEqual(1, sources.List().Count, "the document is never listed as a mod");
            Assert.IsTrue(File.Exists(Path.Combine(root, FileModFolderListStore.FileName)) || File.Exists(first.DocumentPath));
        }

        /// <summary>Twin: when the browser persistence is not armed, the save is refused.</summary>
        [Test]
        public void ModFolders_FileStore_UnarmedPersistence_RefusesTheSave()
        {
            FileModFolderListStore store = new(NewDirectory(), () => false);

            Assert.Throws<IOException>(() => store.Save(new[] { "Archive" }));
        }

        /// <summary>A bundled sample the player moved keeps its folder when a newer version ships; an untouched one follows the bundle.</summary>
        [Test]
        public void Seeder_VersionBump_KeepsAFolderThePlayerChose_AndMovesAnUntouchedCopy()
        {
            InMemorySourceStore store = new();
            new BundledModSeeder(store, new IBundledModSource[]
            {
                new Samples(("moved", Sample("moved", "1.0.0", "Samples", "a")), ("kept", Sample("kept", "1.0.0", "Samples", "a")))
            }).Seed();
            Assert.IsTrue(store.TryLoad("moved", out string installed, out LuaModManifest _));
            ModFolderHeaderEdit move = ModFolderHeaderEditor.SetCategory(installed, "My/Games");
            Assert.IsTrue(move.Succeeded, move.Message);
            store.Save("moved", move.Source, CopyWithCategory(store, "moved", "My/Games"));

            new BundledModSeeder(store, new IBundledModSource[]
            {
                new Samples(("moved", Sample("moved", "1.1.0", "Samples/Games", "b")), ("kept", Sample("kept", "1.1.0", "Samples/Games", "b")))
            }).Seed();

            Assert.IsTrue(store.TryLoad("moved", out string movedSource, out LuaModManifest movedManifest));
            Assert.AreEqual("My/Games", ModFolderHeaderEditor.ReadCategory(movedSource));
            Assert.AreEqual("My/Games", movedManifest.Category);
            StringAssert.Contains("local body = 'b'", movedSource, "the new version's code shipped");
            Assert.IsTrue(store.TryLoad("kept", out string keptSource, out LuaModManifest keptManifest));
            Assert.AreEqual("Samples/Games", ModFolderHeaderEditor.ReadCategory(keptSource));
            Assert.AreEqual("Samples/Games", keptManifest.Category);
        }

        /// <summary>The header keeps a category raw: a flat one parses unchanged and a spaced one is only trimmed.</summary>
        [Test]
        public void Header_FlatCategoryParsesUnchanged_AndASpacedOneStaysRaw()
        {
            Assert.AreEqual("Samples", LuaModHeader.Parse("--[[@coreai\nid: a\ncategory: Samples\n]]\n", "a").Category);
            Assert.AreEqual("Games / Arcade", LuaModHeader.Parse("--[[@coreai\nid: a\ncategory:  Games / Arcade \n]]\n", "a").Category);
            Assert.AreEqual("Games/Arcade", LuaModHeader.Parse("--[[@coreai\nid: a\ncategory:  Games / Arcade \n]]\n", "a").FolderPath.Value);
        }

        private static void AssertRefused(byte[] package, JArray folders, string expected)
        {
            JObject manifest = Manifest(package);
            manifest["mod_folders"] = folders;
            byte[] edited = WorldPackageTestWorlds.BuildZip(
                (RbxWorldPackageSerializer.ManifestEntryName, Encoding.UTF8.GetBytes(manifest.ToString())),
                (RbxWorldPackageSerializer.WorldEntryName, EntryBytes(package, RbxWorldPackageSerializer.WorldEntryName)));
            RbxWorldPackageException refused = Assert.Throws<RbxWorldPackageException>(
                () => RbxWorldPackageSerializer.ReadPackage(edited));
            StringAssert.Contains(expected, refused.Message);
        }

        private static RbxWorldPackagePayload Capture(IModFolderListStore folders, ILuaModSourceStore sources = null)
        {
            InstanceRegistry registry = new(worldId: WorldId);
            RbxDataModel game = DataModelBootstrap.CreateGame(registry);
            return RbxWorldPackageSerializer.Capture(new RbxWorldPackageCaptureContext(
                registry, game, new InMemoryPartPropertySink(registry), WorldPackageTestWorlds.NewSettingsFor(WorldId),
                modSourceStore: sources, capturedAtUtc: CapturedAtUtc, modFolderListStore: folders));
        }

        private static string Sample(string id, string version, string category, string body)
        {
            return "--[[@coreai\nid: " + id + "\nname: " + id + "\nversion: " + version + "\ncategory: " + category
                   + "\ncapabilities: Read\n]]\nlocal body = '" + body + "'\n";
        }

        private static LuaModManifest CopyWithCategory(InMemorySourceStore store, string id, string category)
        {
            Assert.IsTrue(store.TryLoad(id, out string _, out LuaModManifest manifest));
            return new LuaModManifest
            {
                Id = manifest.Id, Name = manifest.Name, Version = manifest.Version, Category = category,
                Capabilities = manifest.Capabilities, Origin = manifest.Origin, SeededVersion = manifest.SeededVersion,
                SeededHash = manifest.SeededHash, Active = manifest.Active
            };
        }

        private static JObject Manifest(byte[] package)
        {
            return JObject.Parse(Encoding.UTF8.GetString(EntryBytes(package, RbxWorldPackageSerializer.ManifestEntryName)));
        }

        private static byte[] EntryBytes(byte[] package, string name)
        {
            using MemoryStream input = new(package, false);
            using ZipArchive archive = new(input, ZipArchiveMode.Read, false);
            using Stream stream = archive.GetEntry(name).Open();
            using MemoryStream output = new();
            stream.CopyTo(output);
            return output.ToArray();
        }

        private string NewDirectory()
        {
            string path = WorldPackageTestWorlds.NewTemporaryDirectory();
            _directories.Add(path);
            return path;
        }

        private sealed class MemoryFolderList : IModFolderListStore
        {
            private IReadOnlyList<string> _folders;

            public MemoryFolderList(params string[] folders)
            {
                _folders = folders;
            }

            public IReadOnlyList<string> Load()
            {
                return _folders;
            }

            public void Save(IReadOnlyList<string> folders)
            {
                _folders = new List<string>(folders);
            }
        }

        private sealed class Samples : IBundledModSource
        {
            private readonly List<BundledMod> _mods = new();

            public Samples(params (string Id, string Source)[] mods)
            {
                foreach ((string id, string source) in mods)
                {
                    _mods.Add(new BundledMod(id, source, LuaModHeader.Parse(source, id).Version));
                }
            }

            public string Origin => "resources";

            public IReadOnlyList<BundledMod> Load()
            {
                return _mods;
            }
        }

        private sealed class InMemorySourceStore : ILuaModSourceStore
        {
            private readonly Dictionary<string, (string Source, LuaModManifest Manifest)> _entries = new(StringComparer.Ordinal);

            public void Save(string id, string source, LuaModManifest manifest)
            {
                _entries[id] = (source, manifest);
            }

            public bool TryLoad(string id, out string source, out LuaModManifest manifest)
            {
                if (_entries.TryGetValue(id, out (string Source, LuaModManifest Manifest) entry))
                {
                    source = entry.Source;
                    manifest = entry.Manifest;
                    return true;
                }

                source = "";
                manifest = null;
                return false;
            }

            public IReadOnlyList<LuaModManifest> List()
            {
                List<LuaModManifest> manifests = new();
                foreach ((string Source, LuaModManifest Manifest) entry in _entries.Values)
                {
                    manifests.Add(entry.Manifest);
                }

                return manifests;
            }

            public void SetActive(string id, bool active)
            {
                if (_entries.TryGetValue(id, out (string Source, LuaModManifest Manifest) entry))
                {
                    entry.Manifest.Active = active;
                }
            }

            public void Delete(string id)
            {
                _entries.Remove(id);
            }
        }
    }
}
