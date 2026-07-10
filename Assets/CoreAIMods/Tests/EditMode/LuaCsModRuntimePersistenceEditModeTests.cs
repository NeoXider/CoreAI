using System;
using System.Collections.Generic;
using System.Threading;
using CoreAI.Ai;
using CoreAI.Ai.LuaCs;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode
{
    /// <summary>
    /// Persistence and versioning parity tests for the Lua-CSharp <see cref="LuaCsModRuntime"/>, mirroring
    /// the MoonSharp <c>LuaModPersistenceEditModeTests</c> and the versioning cases in
    /// <c>LuaModRuntimeEditModeTests</c>: auto-persist on load, rehydrate of active (and skip of dormant)
    /// mods, dormant-marking on unload, deletion on forget, an export/import round-trip between two
    /// runtimes, capability masking that strips <see cref="LuaCapabilities.Full"/> from persisted/shared
    /// mods unless explicitly allowed, and the version history growing per edit / restoring on revert.
    /// The runtime is constructed directly (no gameplay bindings needed) exactly as the MoonSharp fixture
    /// builds a bare <c>LuaModRuntime</c>; the fakes mirror that fixture's in-memory stores.
    /// </summary>
    public sealed class LuaCsModRuntimePersistenceEditModeTests
    {
        private SynchronizationContext _savedContext;

        /// <summary>
        /// The Lua-CSharp runtime bridges its async VM to a synchronous call site via
        /// <c>state.ExecuteAsync(...).GetAwaiter().GetResult()</c> inside the execution guard. On Unity's
        /// main thread a <see cref="SynchronizationContext"/> is installed, so any continuation the VM
        /// posts back to it would deadlock the blocked main thread (the sync-over-async hazard that freezes
        /// the interactive Test Runner). Detaching the context for the duration of each test lets those
        /// continuations complete on the thread pool, exercising the runtime deterministically. Identical
        /// to the guard used by <see cref="LuaCsModRuntimeEditModeTests"/>.
        /// </summary>
        [SetUp]
        public void DetachSynchronizationContext()
        {
            _savedContext = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(null);
        }

        [TearDown]
        public void RestoreSynchronizationContext()
        {
            SynchronizationContext.SetSynchronizationContext(_savedContext);
        }

        /// <summary>
        /// In-memory <see cref="ILuaModSourceStore"/> capturing source + manifest per id so the test can
        /// assert exactly what the runtime persisted without touching the file system. Same shape as the
        /// MoonSharp fixture's fake.
        /// </summary>
        private sealed class FakeSourceStore : ILuaModSourceStore
        {
            private sealed class Entry
            {
                public string Source = "";
                public LuaModManifest Manifest;
            }

            private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);

            public int SaveCount { get; private set; }

            public void Save(string id, string source, LuaModManifest manifest)
            {
                SaveCount++;
                _entries[id] = new Entry { Source = source, Manifest = manifest };
            }

            public bool TryLoad(string id, out string source, out LuaModManifest manifest)
            {
                if (_entries.TryGetValue(id, out Entry entry))
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
                List<LuaModManifest> result = new();
                foreach (Entry entry in _entries.Values)
                {
                    if (entry.Manifest != null)
                    {
                        result.Add(entry.Manifest);
                    }
                }

                return result;
            }

            public void SetActive(string id, bool active)
            {
                if (_entries.TryGetValue(id, out Entry entry) && entry.Manifest != null)
                {
                    entry.Manifest.Active = active;
                }
            }

            public void Delete(string id)
            {
                _entries.Remove(id);
            }

            public bool Contains(string id)
            {
                return _entries.ContainsKey(id);
            }

            public LuaModManifest ManifestOf(string id)
            {
                return _entries.TryGetValue(id, out Entry entry) ? entry.Manifest : null;
            }

            public string SourceOf(string id)
            {
                return _entries.TryGetValue(id, out Entry entry) ? entry.Source : null;
            }
        }

        private static LuaCsModRuntime NewRuntime(FakeSourceStore store)
        {
            return new LuaCsModRuntime(sourceStore: store);
        }

        // ==================== Source-store persistence ====================

        [Test]
        public void LuaCs_LoadMod_AutoPersistsSourceAndActiveManifest()
        {
            FakeSourceStore store = new();
            LuaCsModRuntime runtime = NewRuntime(store);

            runtime.LoadMod("m", "local x = 1", LuaCapabilities.Read);

            Assert.IsTrue(store.Contains("m"), "LoadMod must persist the mod into the source store.");
            Assert.AreEqual("local x = 1", store.SourceOf("m"));

            LuaModManifest manifest = store.ManifestOf("m");
            Assert.IsNotNull(manifest);
            Assert.AreEqual("m", manifest.Id);
            Assert.IsTrue(manifest.Active, "A freshly loaded mod must be persisted as active.");
            Assert.AreEqual(LuaCapabilities.Read.ToString(), manifest.Capabilities);
        }

        [Test]
        public void LuaCs_RehydrateFromStore_LoadsActiveMods_SkipsInactive()
        {
            FakeSourceStore store = new();
            store.Save("active", "local a = 1", new LuaModManifest
            {
                Id = "active",
                Capabilities = LuaCapabilities.Read.ToString(),
                Active = true
            });
            store.Save("dormant", "local d = 1", new LuaModManifest
            {
                Id = "dormant",
                Capabilities = LuaCapabilities.Read.ToString(),
                Active = false
            });

            LuaCsModRuntime runtime = NewRuntime(store);
            int loaded = runtime.RehydrateFromStore(LuaCapabilities.All);

            Assert.AreEqual(1, loaded, "Only the active mod must be rehydrated.");
            Assert.IsTrue(runtime.IsLoaded("active"));
            Assert.IsFalse(runtime.IsLoaded("dormant"), "A dormant mod must not auto-load.");
        }

        [Test]
        public void LuaCs_UnloadMod_MarksStoredManifestInactive_WithoutDeleting()
        {
            FakeSourceStore store = new();
            LuaCsModRuntime runtime = NewRuntime(store);
            runtime.LoadMod("m", "local x = 1", LuaCapabilities.Read);

            Assert.IsTrue(runtime.UnloadMod("m"));

            Assert.IsTrue(store.Contains("m"), "Unloading must keep the persisted package (dormant, not deleted).");
            Assert.IsFalse(store.ManifestOf("m").Active, "Unloading must mark the stored manifest inactive.");
        }

        [Test]
        public void LuaCs_ForgetMod_DeletesFromStore()
        {
            FakeSourceStore store = new();
            LuaCsModRuntime runtime = NewRuntime(store);
            runtime.LoadMod("m", "local x = 1", LuaCapabilities.Read);
            Assert.IsTrue(store.Contains("m"));

            Assert.IsTrue(runtime.ForgetMod("m"));

            Assert.IsFalse(runtime.IsLoaded("m"));
            Assert.IsFalse(store.Contains("m"), "ForgetMod must delete the persisted package entirely.");
        }

        // ==================== Export / import round-trip ====================

        [Test]
        public void LuaCs_ExportThenImport_RoundTripsModIntoAnotherRuntime()
        {
            FakeSourceStore sourceStore = new();
            LuaCsModRuntime source = NewRuntime(sourceStore);
            source.LoadMod("shared", "hooks_on('ping', function() end)", LuaCapabilities.Read);

            string bundle = source.ExportMod("shared");
            Assert.IsNotNull(bundle, "ExportMod must return a bundle for a loaded mod.");
            StringAssert.Contains("shared", bundle);

            FakeSourceStore destStore = new();
            LuaCsModRuntime destination = NewRuntime(destStore);

            Assert.IsTrue(destination.ImportMod(bundle, LuaCapabilities.All));
            Assert.IsTrue(destination.IsLoaded("shared"), "Imported mod must be loaded in the destination runtime.");
            Assert.IsTrue(destStore.Contains("shared"), "Imported mod must be persisted in the destination store.");
            Assert.IsTrue(destination.TryGetModSource("shared", out string importedSource));
            Assert.AreEqual("hooks_on('ping', function() end)", importedSource);
        }

        [Test]
        public void LuaCs_ExportMod_UnknownId_ReturnsNull()
        {
            FakeSourceStore store = new();
            LuaCsModRuntime runtime = NewRuntime(store);

            Assert.IsNull(runtime.ExportMod("nope"));
        }

        [Test]
        public void LuaCs_ImportMod_MalformedBundle_ReturnsFalse()
        {
            FakeSourceStore store = new();
            LuaCsModRuntime runtime = NewRuntime(store);

            Assert.IsFalse(runtime.ImportMod("not json {{", LuaCapabilities.All));
            Assert.IsFalse(runtime.ImportMod("", LuaCapabilities.All));
        }

        // ==================== Capability masking (Full gate) ====================

        [Test]
        public void LuaCs_RehydrateFromStore_MasksFull_UnlessAllowFull()
        {
            FakeSourceStore store = new();
            store.Save("priv", "local x = 1", new LuaModManifest
            {
                Id = "priv",
                Capabilities = (LuaCapabilities.Read | LuaCapabilities.Full).ToString(),
                Active = true
            });

            // Default rehydrate (allowFull:false) intersects with the host grant and strips Full.
            LuaCsModRuntime masked = NewRuntime(store);
            Assert.AreEqual(1, masked.RehydrateFromStore(LuaCapabilities.All | LuaCapabilities.Full));
            IReadOnlyList<LuaModInfo> maskedMods = masked.ListMods();
            Assert.AreEqual(1, maskedMods.Count);
            Assert.AreEqual(LuaCapabilities.None, maskedMods[0].Capabilities & LuaCapabilities.Full,
                "Full must be masked on rehydrate when allowFull is false.");
            Assert.AreEqual(LuaCapabilities.Read, maskedMods[0].Capabilities & LuaCapabilities.Read,
                "Non-Full capabilities allowed by the host grant must survive masking.");

            // allowFull:true keeps Full when the host grant also includes it.
            LuaCsModRuntime allowed = NewRuntime(store);
            Assert.AreEqual(1, allowed.RehydrateFromStore(LuaCapabilities.All | LuaCapabilities.Full, true));
            IReadOnlyList<LuaModInfo> allowedMods = allowed.ListMods();
            Assert.AreEqual(LuaCapabilities.Full, allowedMods[0].Capabilities & LuaCapabilities.Full,
                "Full must survive rehydrate when allowFull is true and the host grant includes it.");
        }

        [Test]
        public void LuaCs_RehydrateFromStore_HonoursHostGrantCap()
        {
            FakeSourceStore store = new();
            store.Save("wide", "local x = 1", new LuaModManifest
            {
                Id = "wide",
                Capabilities = LuaCapabilities.All.ToString(),
                Active = true
            });

            // The persisted mod requests All, but the host grants only Read: the effective tier is the
            // intersection, so the rehydrated mod is capped to Read.
            LuaCsModRuntime runtime = NewRuntime(store);
            Assert.AreEqual(1, runtime.RehydrateFromStore(LuaCapabilities.Read));

            IReadOnlyList<LuaModInfo> mods = runtime.ListMods();
            Assert.AreEqual(1, mods.Count);
            Assert.AreEqual(LuaCapabilities.Read, mods[0].Capabilities,
                "RehydrateFromStore must cap the persisted request by the host grant.");
        }

        [Test]
        public void LuaCs_ImportMod_MasksFull_UnlessAllowFull()
        {
            FakeSourceStore exportStore = new();
            LuaCsModRuntime exporter = NewRuntime(exportStore);
            exporter.LoadMod("priv", "local x = 1", LuaCapabilities.Read | LuaCapabilities.Full);
            string bundle = exporter.ExportMod("priv");
            Assert.IsNotNull(bundle);

            // Import with allowFull:false must strip Full even though the host grant includes it.
            FakeSourceStore maskedStore = new();
            LuaCsModRuntime masked = NewRuntime(maskedStore);
            Assert.IsTrue(masked.ImportMod(bundle, LuaCapabilities.All | LuaCapabilities.Full));
            Assert.AreEqual(LuaCapabilities.None, masked.ListMods()[0].Capabilities & LuaCapabilities.Full,
                "Full must be masked on import when allowFull is false.");

            // Import with allowFull:true keeps Full.
            FakeSourceStore allowedStore = new();
            LuaCsModRuntime allowed = NewRuntime(allowedStore);
            Assert.IsTrue(allowed.ImportMod(bundle, LuaCapabilities.All | LuaCapabilities.Full, true));
            Assert.AreEqual(LuaCapabilities.Full, allowed.ListMods()[0].Capabilities & LuaCapabilities.Full,
                "Full must survive import when allowFull is true and the host grant includes it.");
        }

        // ==================== Version history / revert ====================

        [Test]
        public void LuaCs_Reload_WithChangedSource_AppendsRevision()
        {
            MemoryLuaScriptVersionStore versions = new();
            LuaCsModRuntime runtime = new(versionStore: versions);

            runtime.LoadMod("m", "local x = 1");
            Assert.AreEqual(1, runtime.ListModVersions("m").Count, "Initial load seeds one revision.");

            runtime.ReloadMod("m", "local x = 2");

            IReadOnlyList<LuaScriptRevision> history = runtime.ListModVersions("m");
            Assert.AreEqual(2, history.Count, "A changed reload appends a revision.");
            Assert.AreEqual("local x = 1", history[0].Source);
            Assert.AreEqual("local x = 2", history[history.Count - 1].Source);
        }

        [Test]
        public void LuaCs_Reload_WithIdenticalSource_DoesNotGrowHistory()
        {
            MemoryLuaScriptVersionStore versions = new();
            LuaCsModRuntime runtime = new(versionStore: versions);

            runtime.LoadMod("m", "local x = 1");
            int before = runtime.ListModVersions("m").Count;

            runtime.ReloadMod("m", "local x = 1");

            Assert.AreEqual(before, runtime.ListModVersions("m").Count, "A no-op reload must not add a revision.");
        }

        [Test]
        public void LuaCs_Revert_RestoresPriorSource()
        {
            MemoryLuaScriptVersionStore versions = new();
            LuaCsModRuntime runtime = new(versionStore: versions);

            runtime.LoadMod("m", "local x = 1");
            runtime.ReloadMod("m", "local x = 2");

            Assert.IsTrue(runtime.TryRevertMod("m", 0, out string restored));
            Assert.AreEqual("local x = 1", restored);
            Assert.IsTrue(runtime.TryGetModSource("m", out string live));
            Assert.AreEqual("local x = 1", live, "The live mod runs the reverted source.");
        }

        [Test]
        public void LuaCs_Revert_UnknownRevision_ReturnsFalse()
        {
            MemoryLuaScriptVersionStore versions = new();
            LuaCsModRuntime runtime = new(versionStore: versions);
            runtime.LoadMod("m", "local x = 1");

            Assert.IsFalse(runtime.TryRevertMod("m", 99, out _));
            Assert.IsFalse(runtime.TryRevertMod("m", -1, out _));
        }

        [Test]
        public void LuaCs_NoVersionStore_ListVersionsEmpty_LoadStillWorks()
        {
            LuaCsModRuntime runtime = new(); // NullLuaScriptVersionStore fallback

            runtime.LoadMod("m", "local x = 1");

            Assert.IsTrue(runtime.IsLoaded("m"));
            Assert.IsEmpty(runtime.ListModVersions("m"));
        }

        [Test]
        public void LuaCs_Revert_AfterRetentionEviction_OriginalWorks_EvictedMiddleFails()
        {
            // F-11: history is bounded (original + last N intermediate + current); a revert must resolve
            // revisions by their stable index, not by array position, once eviction has removed entries.
            MemoryLuaScriptVersionStore versions = new(maxIntermediateRevisions: 2);
            LuaCsModRuntime runtime = new(versionStore: versions);

            runtime.LoadMod("m", "local x = 0");
            for (int i = 1; i <= 10; i++)
            {
                runtime.ReloadMod("m", $"local x = {i}");
            }

            IReadOnlyList<LuaScriptRevision> history = runtime.ListModVersions("m");
            Assert.LessOrEqual(history.Count, 4, "original + 2 intermediate + current at most.");

            Assert.IsTrue(runtime.TryRevertMod("m", 0, out string restored),
                "Revision 0 (original) must remain revertible after eviction.");
            Assert.AreEqual("local x = 0", restored);

            Assert.IsFalse(runtime.TryRevertMod("m", 1, out _),
                "Revision 1 was evicted by retention; revert must fail cleanly, not resolve the wrong revision.");
        }
    }
}
