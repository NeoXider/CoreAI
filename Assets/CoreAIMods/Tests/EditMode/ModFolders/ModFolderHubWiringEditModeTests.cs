#if COREAI_HAS_HUB
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using CoreAI.Ai;
using CoreAI.Ai.Hub;
using CoreAI.Mods.Folders;
using NUnit.Framework;
using UnityEngine.UIElements;

namespace CoreAI.Tests.EditMode.ModFolders
{
    /// <summary>
    /// Mod folders wired into the Hub: the mod service lists without re-reading every source, moves a mod by
    /// rewriting its header (without running a loaded one again), and the Mods page shows the folder tree, keeps
    /// the old grouping for flat categories, moves through the folder service and remembers its folds.
    /// </summary>
    [TestFixture]
    public sealed class ModFolderHubWiringEditModeTests
    {
        private static string Source(string id, string category)
        {
            return "--[[@coreai\nid: " + id + "\nname: " + id + "\n" +
                   (category == null ? "" : "category: " + category + "\n") + "]]\nprint(1)\n";
        }

        private static LuaModManifest Manifest(string id, string category, long loadOrder = 0)
        {
            return new LuaModManifest
            {
                Id = id, Name = id, Category = category ?? "", Capabilities = "All", Active = false,
                LoadOrder = loadOrder
            };
        }

        private static (FakeHubModService Service, StrictMemoryStore Store) ServiceWith(params (string Id, string Category)[] mods)
        {
            StrictMemoryStore store = new();
            foreach ((string id, string category) in mods)
            {
                store.Put(id, Source(id, category), Manifest(id, category));
            }

            return (new FakeHubModService(store), store);
        }

        private static HubModsPage Page(FakeHubModService service, InMemoryHubModFolderPreferences preferences)
        {
            HubModsPage page = new(service, 300, action => action(), null, null, preferences);
            Assert.IsInstanceOf<VisualElement>(page.CreatePageContent());
            return page;
        }

        [Test]
        public void ListMods_ThousandStoredMods_ReadsNoModSource()
        {
            StrictMemoryStore store = new();
            for (int i = 0; i < 1000; i++)
            {
                string id = "mod_" + i.ToString("D4", CultureInfo.InvariantCulture);
                store.Put(id, Source(id, "Pack " + (i % 10).ToString(CultureInfo.InvariantCulture)),
                    Manifest(id, "Pack " + (i % 10).ToString(CultureInfo.InvariantCulture), i + 1));
            }

            FakeHubModService service = new(store);
            store.Loads = 0;

            IReadOnlyList<HubModRecord> records = service.ListMods();

            Assert.AreEqual(1000, records.Count);
            Assert.AreEqual(0, store.Loads, "listing must not read a stored mod's source");
            Assert.AreEqual(1000, records[999].LoadOrder);
            Assert.AreEqual("Pack 9", records[999].Category);
        }

        [Test]
        public void ListMods_StoredManifestWithoutAName_StillReadsItsHeader_TheTwin()
        {
            StrictMemoryStore store = new();
            LuaModManifest nameless = Manifest("legacy", "");
            nameless.Name = "";
            store.Put("legacy", Source("legacy", "Old"), nameless);
            FakeHubModService service = new(store);
            store.Loads = 0;

            HubModRecord record = service.ListMods()[0];

            Assert.AreEqual(1, store.Loads);
            Assert.AreEqual("legacy", record.Name);
            Assert.AreEqual("Old", record.Category);
        }

        [Test]
        public void SetModFolder_DormantMod_RewritesTheStoredHeaderAndManifest()
        {
            (FakeHubModService service, StrictMemoryStore store) = ServiceWith(("racer", "Samples"));

            service.SetModFolder("racer", "Games/Arcade");

            Assert.AreEqual("Games/Arcade", store.Manifests["racer"].Category);
            Assert.AreEqual("Games/Arcade", ModFolderHeaderEditor.ReadCategory(store.Sources["racer"]));
            Assert.AreEqual(1, store.StrictSaves);
            Assert.AreEqual(1, service.ChangedEvents);
        }

        [Test]
        public void SetModFolder_RunningModWithoutRetitle_Throws_AndChangesNothing()
        {
            (FakeHubModService service, StrictMemoryStore store) = ServiceWith(("racer", "Samples"));
            service.Load("racer", Source("racer", "Samples"));

            Assert.Throws<InvalidOperationException>(() => service.SetModFolder("racer", "Games"));

            Assert.AreEqual("Samples", store.Manifests["racer"].Category);
        }

        [Test]
        public void SetModFolder_RunningModWithRetitle_SwapsTheLiveTextAndTheStore()
        {
            (FakeHubModService service, StrictMemoryStore store) = ServiceWith(("racer", "Samples"));
            service.Load("racer", Source("racer", "Samples"));
            service.RetitleSupported = true;

            service.SetModFolder("racer", "Games");

            Assert.AreEqual("Games", ModFolderHeaderEditor.ReadCategory(service.LiveSource("racer")));
            Assert.AreEqual("Games", store.Manifests["racer"].Category);
        }

        [Test]
        public void SetModFolder_InvalidPath_NamesTheRule()
        {
            (FakeHubModService service, _) = ServiceWith(("racer", "Samples"));

            ArgumentException error = Assert.Throws<ArgumentException>(() => service.SetModFolder("racer", "a//b"));

            StringAssert.StartsWith("EMPTY_SEGMENT:", error.Message);
        }

        [Test]
        public void Page_FlatCategories_GroupLikeBefore_AndModsWithoutACategoryAreAtTheTopLevel()
        {
            (FakeHubModService service, _) = ServiceWith(("clicker", "Samples"), ("racer", "Samples"),
                ("controls", "System"), ("loose", null));

            HubModsPage page = Page(service, new InMemoryHubModFolderPreferences());

            ModFolderTree tree = page.Tree;
            Assert.AreEqual(3, tree.FolderCount);
            List<string> rows = new();
            for (int i = 0; i < tree.VisibleRowCount; i++)
            {
                ModFolderRow row = tree.GetVisibleRow(i);
                rows.Add(row.IsFolder
                    ? tree.GetFolder(row.Index).Name + " (" + tree.GetFolder(row.Index).ModCount + ")"
                    : "  " + tree.GetMod(row.Index).Id);
            }

            CollectionAssert.AreEqual(new[] { "Samples (2)", "  clicker", "  racer", "System (1)", "  controls", "  loose" },
                rows);
            Assert.AreEqual(tree.VisibleRowCount, page.TreeView.ListView.itemsSource.Count);
        }

        [Test]
        public void Page_MoveThroughTheFolderService_SurvivesAReopen_AndKeepsTheEmptiedFolder()
        {
            (FakeHubModService service, StrictMemoryStore store) = ServiceWith(("clock", "Tools"), ("racer", "Samples"));
            InMemoryHubModFolderPreferences preferences = new();
            HubModsPage page = Page(service, preferences);

            ModFolderResult result = page.RunFolderOperation(() => page.FolderService.MoveMod("clock", "Games"), false);

            Assert.IsTrue(result.Succeeded, result.Message);
            Assert.AreEqual("Games", store.Manifests["clock"].Category);
            HubModsPage reopened = Page(new FakeHubModService(store), preferences);
            Assert.IsTrue(reopened.Tree.TryFindFolder("Games", out int games));
            Assert.AreEqual(1, reopened.Tree.GetFolder(games).ModCount);
            Assert.IsTrue(reopened.Tree.TryFindFolder("Tools", out int tools), "the emptied folder is kept");
            Assert.IsTrue(reopened.Tree.GetFolder(tools).IsEmpty);
        }

        [Test]
        public void Page_FolderRefusal_ShowsTheCode_AndChangesNothing()
        {
            (FakeHubModService service, StrictMemoryStore store) = ServiceWith(("racer", "Samples"));
            HubModsPage page = Page(service, new InMemoryHubModFolderPreferences());

            ModFolderResult result = page.RunFolderOperation(() => page.FolderService.DeleteFolder("Samples"), true);

            Assert.AreEqual(ModFolderErrorCode.FolderNotEmpty, result.Error);
            Assert.AreEqual("Samples", store.Manifests["racer"].Category);
        }

        [Test]
        public void Page_FoldState_IsSavedToThePreferences_AndReadBack()
        {
            (FakeHubModService service, _) = ServiceWith(("racer", "Samples"));
            InMemoryHubModFolderPreferences preferences = new();
            HubModsPage page = Page(service, preferences);
            Assert.IsTrue(page.Tree.TryFindFolder("Samples", out int samples));

            page.Tree.SetExpanded(samples, false);
            page.SaveFoldStateIfChanged();

            StringAssert.Contains("toggled Samples", preferences.FoldStateText);
            HubModsPage reopened = Page(service, preferences);
            Assert.IsTrue(reopened.Tree.TryFindFolder("Samples", out int again));
            Assert.IsFalse(reopened.Tree.IsExpanded(again));
        }

        [Test]
        public void Page_Opening1000NestedMods_PrintsTheTime()
        {
            StrictMemoryStore store = new();
            List<ModFolderModEntry> nested = ModFolderTreePerformanceEditModeTests.NestedMods(1000, 5);
            foreach (ModFolderModEntry entry in nested)
            {
                store.Put(entry.Id, Source(entry.Id, entry.Folder), Manifest(entry.Id, entry.Folder, entry.LoadOrder));
            }

            FakeHubModService service = new(store);
            Stopwatch watch = Stopwatch.StartNew();

            HubModsPage page = Page(service, new InMemoryHubModFolderPreferences());

            watch.Stop();
            TestContext.Progress.WriteLine("mod-folders Hub Mods page open @1000 mods (list + tree + view, no layout): " +
                                           watch.Elapsed.TotalMilliseconds.ToString("F2", CultureInfo.InvariantCulture) +
                                           " ms");
            Assert.AreEqual(1000, page.Tree.ModCount);
            Assert.Less(watch.Elapsed.TotalMilliseconds, 100.0);
        }

        private sealed class StrictMemoryStore : ILuaModSourceStore, ILuaModSourceStrictWriter
        {
            public Dictionary<string, string> Sources { get; } = new(StringComparer.Ordinal);

            public Dictionary<string, LuaModManifest> Manifests { get; } = new(StringComparer.Ordinal);

            public int Loads { get; set; }

            public int StrictSaves { get; private set; }

            public void Put(string id, string source, LuaModManifest manifest)
            {
                Sources[id] = source;
                Manifests[id] = manifest;
            }

            public void Save(string id, string source, LuaModManifest manifest)
            {
                Put(id, source, manifest);
            }

            public void SaveStrict(string id, string source, LuaModManifest manifest)
            {
                StrictSaves++;
                Put(id, source, manifest);
            }

            public bool TryLoad(string id, out string source, out LuaModManifest manifest)
            {
                Loads++;
                Manifests.TryGetValue(id, out manifest);
                return Sources.TryGetValue(id, out source);
            }

            public IReadOnlyList<LuaModManifest> List()
            {
                return new List<LuaModManifest>(Manifests.Values);
            }

            public void SetActive(string id, bool active)
            {
                Manifests[id].Active = active;
            }

            public void Delete(string id)
            {
                Sources.Remove(id);
                Manifests.Remove(id);
            }
        }

        private sealed class FakeHubModService : HubModServiceBase
        {
            private readonly Dictionary<string, string> _live = new(StringComparer.Ordinal);

            public FakeHubModService(ILuaModSourceStore store) : base(store, LuaCapabilities.All, false)
            {
                ModsChanged += () => ChangedEvents++;
            }

            public bool RetitleSupported { get; set; }

            public int ChangedEvents { get; private set; }

            public void Load(string id, string source)
            {
                _live[id] = source;
            }

            public string LiveSource(string id)
            {
                return _live[id];
            }

            public override bool IsSupported => true;

            public override bool IsLoaded(string id)
            {
                return _live.ContainsKey(id ?? "");
            }

            public override IReadOnlyList<LuaScriptRevision> ListModVersions(string id)
            {
                return Array.Empty<LuaScriptRevision>();
            }

            public override bool TryRevertMod(string id, int revisionIndex, out string restoredSource)
            {
                restoredSource = null;
                return false;
            }

            public override string ExportMod(string id)
            {
                return null;
            }

            protected override bool RuntimeImport(string bundleJson, LuaCapabilities hostGrant, bool allowFull)
            {
                return false;
            }

            public override string RecentErrors(string id)
            {
                return "";
            }

            public override IReadOnlyList<LuaModHandlerError> RecentErrorEntries(string modId = null)
            {
                return Array.Empty<LuaModHandlerError>();
            }

            public override IReadOnlyList<LuaModReport> RecentReports(string modId = null)
            {
                return Array.Empty<LuaModReport>();
            }

            public override void ClearReports()
            {
            }

            public override void ClearErrors()
            {
            }

            public override bool GetReportLoggingEnabled(string modId)
            {
                return false;
            }

            public override bool SetReportLoggingEnabled(string modId, bool enabled)
            {
                return false;
            }

            protected override IReadOnlyList<HubLoadedInfo> GetLoaded()
            {
                List<HubLoadedInfo> loaded = new();
                foreach (string id in _live.Keys)
                {
                    loaded.Add(new HubLoadedInfo(id, LuaCapabilities.All, 0, 0, 0));
                }

                return loaded;
            }

            protected override bool TryGetLiveSource(string id, out string source)
            {
                return _live.TryGetValue(id ?? "", out source);
            }

            protected override void RuntimeLoad(string id, string code, LuaCapabilities caps)
            {
                _live[id] = code;
            }

            protected override void RuntimeReload(string id, string code)
            {
                _live[id] = code;
            }

            protected override bool RuntimeUnload(string id)
            {
                return _live.Remove(id);
            }

            protected override bool RuntimeRetitle(string id, string newSource)
            {
                if (!RetitleSupported || !_live.ContainsKey(id))
                {
                    return false;
                }

                _live[id] = newSource;
                return true;
            }
        }
    }
}
#endif
