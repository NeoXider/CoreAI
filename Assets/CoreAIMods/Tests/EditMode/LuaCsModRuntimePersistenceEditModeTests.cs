using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using CoreAI.Ai;
using CoreAI.Ai.LuaCs;
using CoreAI.Authority;
using CoreAI.Infrastructure.Lua;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode
{
    /// <summary>
    /// Persistence and versioning tests for the Lua-CSharp <see cref="LuaCsModRuntime"/>:
    /// auto-persist on load, rehydrate of active (and skip of dormant)
    /// mods, dormant-marking on unload, deletion on forget, an export/import round-trip between two
    /// runtimes, capability masking that strips <see cref="LuaCapabilities.Full"/> from persisted/shared
    /// mods unless explicitly allowed, the version history growing per edit / restoring on revert, and
    /// mods restarting in the order they were loaded. The runtime is constructed directly (no gameplay
    /// bindings needed) as a bare <see cref="LuaCsModRuntime"/>; the fakes are simple in-memory stores,
    /// and the restart tests use the real <see cref="FileLuaModSourceStore"/> over a temporary directory.
    /// </summary>
    public sealed class LuaCsModRuntimePersistenceEditModeTests
    {
        private readonly List<string> _temporaryDirectories = new();
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
            foreach (string directory in _temporaryDirectories)
            {
                if (Directory.Exists(directory))
                {
                    Directory.Delete(directory, true);
                }
            }

            _temporaryDirectories.Clear();
        }

        /// <summary>
        /// In-memory <see cref="ILuaModSourceStore"/> capturing source + manifest per id so the test can
        /// assert exactly what the runtime persisted without touching the file system.
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

        private static LuaCsModRuntime NewRuntime(ILuaModSourceStore store)
        {
            return new LuaCsModRuntime(sourceStore: store);
        }

        /// <summary>
        /// The production store over a fresh temporary directory: it lists mods by ordinal id and keeps
        /// every manifest as JSON on disk, so a second runtime over it is a real restart.
        /// </summary>
        private FileLuaModSourceStore NewFileStore()
        {
            string directory = Path.Combine(
                Path.GetTempPath(), "CoreAI-LuaCsPersistence-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            _temporaryDirectories.Add(directory);
            return new FileLuaModSourceStore(directory);
        }

        private static string[] LoadedIds(LuaCsModRuntime runtime)
        {
            IReadOnlyList<LuaModInfo> mods = runtime.ListMods();
            string[] ids = new string[mods.Count];
            for (int index = 0; index < mods.Count; index++)
            {
                ids[index] = mods[index].Id;
            }

            return ids;
        }

        private static long StoredLoadOrder(ILuaModSourceStore store, string id)
        {
            Assert.IsTrue(store.TryLoad(id, out _, out LuaModManifest manifest), "'" + id + "' must be stored.");
            return manifest.LoadOrder;
        }

        private static void SaveStoredMod(ILuaModSourceStore store, string id, long loadOrder)
        {
            store.Save(id, "local x = 1", new LuaModManifest
            {
                Id = id,
                Capabilities = LuaCapabilities.Read.ToString(),
                Active = true,
                LoadOrder = loadOrder
            });
        }

        /// <summary>In-memory <see cref="CoreAI.Logging.ILog"/> capturing per-level messages for assertions.</summary>
        private sealed class FakeLog : Logging.ILog
        {
            public readonly List<string> Warnings = new();
            public readonly List<string> Errors = new();

            public void Debug(string message, string tag = null)
            {
            }

            public void Info(string message, string tag = null)
            {
            }

            public void Warn(string message, string tag = null)
            {
                Warnings.Add(message);
            }

            public void Error(string message, string tag = null)
            {
                Errors.Add(message);
            }
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
        public void LuaCs_RehydrateFromStore_FailingMod_IsQuietlySkipped_OthersStillLoad()
        {
            // Regression: a persisted mod that fails to load (e.g. saved by a composition with a wider
            // Lua tier) used to log a full error + stack trace; it must instead be skipped with a single
            // warning while the remaining mods rehydrate normally.
            FakeSourceStore store = new();
            store.Save("broken", "error('boom')", new LuaModManifest
            {
                Id = "broken",
                Capabilities = LuaCapabilities.Read.ToString(),
                Active = true
            });
            store.Save("healthy", "local x = 1", new LuaModManifest
            {
                Id = "healthy",
                Capabilities = LuaCapabilities.Read.ToString(),
                Active = true
            });

            FakeLog log = new();
            LuaCsModRuntime runtime = new(sourceStore: store, log: log);
            int loaded = runtime.RehydrateFromStore(LuaCapabilities.All);

            Assert.AreEqual(1, loaded, "The healthy mod must still rehydrate despite the broken one.");
            Assert.IsTrue(runtime.IsLoaded("healthy"));
            Assert.IsFalse(runtime.IsLoaded("broken"), "The failing mod must stay unloaded.");

            Assert.AreEqual(0, log.Errors.Count, "A rehydrate failure must not log at error level.");
            int brokenWarnings = 0;
            foreach (string warning in log.Warnings)
            {
                if (warning.Contains("broken"))
                {
                    brokenWarnings++;
                }
            }

            Assert.AreEqual(1, brokenWarnings, "Exactly one warning must be logged for the failing mod.");
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

        // ==================== Load order across restarts ====================

        private const string BaseModId = "zz-base";
        private const string UserModId = "aa-user";
        private const string BaseModSource = "mods_export('marker', 'base-ready')";

        private const string UserModSource =
            "if mods_get('zz-base', 'marker') ~= 'base-ready' then error('zz-base has not started') end";

        /// <summary>
        /// A1-02: the user mod reads the base mod's export at init and the ids sort the other way. The
        /// file store lists by ordinal id, so a restart used to start the user mod first and skip it.
        /// </summary>
        [Test]
        public void LuaCs_RehydrateFromStore_AfterRestart_StartsModsInTheirLoadOrder_NotInIdOrder()
        {
            FileLuaModSourceStore store = NewFileStore();
            LuaCsModRuntime first = NewRuntime(store);
            first.LoadMod(BaseModId, BaseModSource, LuaCapabilities.Read);
            first.LoadMod(UserModId, UserModSource, LuaCapabilities.Read);

            LuaCsModRuntime restarted = NewRuntime(store);
            int loaded = restarted.RehydrateFromStore(LuaCapabilities.All);

            Assert.AreEqual(2, loaded, "Both mods must start again: the user mod reads the base mod's export at init.");
            CollectionAssert.AreEqual(new[] { BaseModId, UserModId }, LoadedIds(restarted),
                "A restart must start the mods in the order they were loaded, not by id.");
        }

        /// <summary>
        /// A1-02: the exact restore behind a world load is all-or-nothing, so one mod started out of its
        /// load order used to make the whole set unloadable.
        /// </summary>
        [Test]
        public void LuaCs_RehydrateExactOrThrow_AfterRestart_StartsModsInTheirLoadOrder_NotInIdOrder()
        {
            FileLuaModSourceStore store = NewFileStore();
            LuaCsModRuntime first = NewRuntime(store);
            first.LoadMod(BaseModId, BaseModSource, LuaCapabilities.Read);
            first.LoadMod(UserModId, UserModSource, LuaCapabilities.Read);

            LuaCsModRuntime restarted = NewRuntime(store);
            int started = 0;
            Assert.DoesNotThrow(
                () => started = restarted.RehydrateExactOrThrow(LuaCapabilities.All),
                "The exact restore must start the base mod before the user mod that reads it at init.");

            Assert.AreEqual(2, started);
            CollectionAssert.AreEqual(new[] { BaseModId, UserModId }, LoadedIds(restarted));
        }

        [Test]
        public void LuaCs_LoadOrder_ReloadKeepsItsPlace_UnloadThenLoadMovesToTheEnd()
        {
            FileLuaModSourceStore store = NewFileStore();
            LuaCsModRuntime runtime = NewRuntime(store);
            runtime.LoadMod("c-first", "local x = 1", LuaCapabilities.Read);
            runtime.LoadMod("b-second", "local x = 2", LuaCapabilities.Read);
            runtime.LoadMod("a-third", "local x = 3", LuaCapabilities.Read);

            runtime.ReloadMod("c-first", "local x = 10");
            Assert.IsTrue(runtime.UnloadMod("b-second"));
            runtime.LoadMod("b-second", "local x = 20", LuaCapabilities.Read);

            LuaCsModRuntime restarted = NewRuntime(store);
            Assert.AreEqual(3, restarted.RehydrateFromStore(LuaCapabilities.All));
            CollectionAssert.AreEqual(new[] { "c-first", "a-third", "b-second" }, LoadedIds(restarted),
                "A reload keeps the mod's place; an unload followed by a load moves it to the end.");
            Assert.AreEqual(1L, StoredLoadOrder(store, "c-first"));
            Assert.AreEqual(3L, StoredLoadOrder(store, "a-third"));
            Assert.AreEqual(4L, StoredLoadOrder(store, "b-second"));
        }

        [Test]
        public void LuaCs_LoadOrder_GrowsAcrossRestartsPastDormantMods_AndRehydrateNeverRestamps()
        {
            FakeSourceStore store = new();
            LuaCsModRuntime first = NewRuntime(store);
            first.LoadMod("m-one", "local x = 1", LuaCapabilities.Read);
            first.LoadMod("m-two", "local x = 2", LuaCapabilities.Read);
            Assert.IsTrue(first.UnloadMod("m-two"));

            LuaCsModRuntime second = NewRuntime(store);
            Assert.AreEqual(1, second.RehydrateFromStore(LuaCapabilities.All));
            second.LoadMod("m-three", "local x = 3", LuaCapabilities.Read);
            int savesAfterLastLoad = store.SaveCount;

            LuaCsModRuntime third = NewRuntime(store);
            Assert.AreEqual(2, third.RehydrateFromStore(LuaCapabilities.All));
            LuaCsModRuntime fourth = NewRuntime(store);
            Assert.AreEqual(2, fourth.RehydrateExactOrThrow(LuaCapabilities.All));

            Assert.AreEqual(1L, StoredLoadOrder(store, "m-one"));
            Assert.AreEqual(2L, StoredLoadOrder(store, "m-two"), "A dormant mod keeps its place.");
            Assert.AreEqual(3L, StoredLoadOrder(store, "m-three"),
                "A first load after a restart must land past every stored mod, the dormant one included.");
            Assert.AreEqual(savesAfterLastLoad, store.SaveCount, "A rehydrate never writes a manifest.");
        }

        /// <summary>
        /// Mods without a recorded order (0, or a negative value from an untrusted package, which is not
        /// rejected) start first by ordinal id, then the ordered ones ascending, through both paths.
        /// </summary>
        [Test]
        public void LuaCs_Rehydrate_ModsWithoutLoadOrder_StartBeforeOrderedOnes_ByOrdinalId()
        {
            FakeSourceStore store = new();
            SaveStoredMod(store, "b-legacy", 0);
            SaveStoredMod(store, "y-ordered", 7);
            SaveStoredMod(store, "c-negative", -4);
            SaveStoredMod(store, "a-legacy", 0);
            SaveStoredMod(store, "z-ordered", 5);
            string[] expected = { "a-legacy", "b-legacy", "c-negative", "z-ordered", "y-ordered" };

            LuaCsModRuntime rehydrated = NewRuntime(store);
            Assert.AreEqual(5, rehydrated.RehydrateFromStore(LuaCapabilities.All));
            CollectionAssert.AreEqual(expected, LoadedIds(rehydrated));

            LuaCsModRuntime exact = NewRuntime(store);
            Assert.AreEqual(5, exact.RehydrateExactOrThrow(LuaCapabilities.All));
            CollectionAssert.AreEqual(expected, LoadedIds(exact));
        }

        /// <summary>
        /// A1-02: a store written before load order existed holds a library with no recorded order; the
        /// player then loads a mod that reads the library at init, and the ids sort the other way. The
        /// library had already started at startup, so it must start first again, through both paths.
        /// </summary>
        [Test]
        public void LuaCs_Restart_ModLoadedAfterALegacyMod_StartsAfterIt_ThroughBothPaths()
        {
            FileLuaModSourceStore store = NewFileStore();
            store.Save("zz-lib", "mods_export('marker', 'lib-ready')", new LuaModManifest
            {
                Id = "zz-lib",
                Capabilities = LuaCapabilities.Read.ToString(),
                Active = true
            });
            LuaCsModRuntime session = NewRuntime(store);
            Assert.AreEqual(1, session.RehydrateFromStore(LuaCapabilities.All));
            session.LoadMod(
                "aa-mod",
                "if mods_get('zz-lib', 'marker') ~= 'lib-ready' then error('zz-lib has not started') end",
                LuaCapabilities.Read);

            LuaCsModRuntime rehydrated = NewRuntime(store);
            Assert.AreEqual(2, rehydrated.RehydrateFromStore(LuaCapabilities.All),
                "The mod loaded after the legacy library must start after it.");
            CollectionAssert.AreEqual(new[] { "zz-lib", "aa-mod" }, LoadedIds(rehydrated));

            LuaCsModRuntime exact = NewRuntime(store);
            int started = 0;
            Assert.DoesNotThrow(() => started = exact.RehydrateExactOrThrow(LuaCapabilities.All));
            Assert.AreEqual(2, started);
            CollectionAssert.AreEqual(new[] { "zz-lib", "aa-mod" }, LoadedIds(exact));
            Assert.AreEqual(0L, StoredLoadOrder(store, "zz-lib"), "A rehydrate never stamps a legacy mod.");
            Assert.AreEqual(1L, StoredLoadOrder(store, "aa-mod"));
        }

        /// <summary>
        /// A store written before mods carried a load order restores exactly as before: by ordinal id,
        /// through both paths.
        /// </summary>
        [Test]
        public void LuaCs_Rehydrate_LegacyStoreWithoutLoadOrder_StartsModsByOrdinalId_AsBefore()
        {
            FileLuaModSourceStore store = NewFileStore();
            SaveStoredMod(store, "c-legacy", 0);
            SaveStoredMod(store, "a-legacy", 0);
            SaveStoredMod(store, "b-legacy", 0);
            string[] expected = { "a-legacy", "b-legacy", "c-legacy" };

            LuaCsModRuntime rehydrated = NewRuntime(store);
            Assert.AreEqual(3, rehydrated.RehydrateFromStore(LuaCapabilities.All));
            CollectionAssert.AreEqual(expected, LoadedIds(rehydrated));

            LuaCsModRuntime exact = NewRuntime(store);
            Assert.AreEqual(3, exact.RehydrateExactOrThrow(LuaCapabilities.All));
            CollectionAssert.AreEqual(expected, LoadedIds(exact));
            Assert.AreEqual(0L, StoredLoadOrder(store, "a-legacy"), "A rehydrate never stamps a legacy mod.");
        }

        [Test]
        public void LuaCs_Restart_IndependentMods_BothStartThroughBothPaths()
        {
            FileLuaModSourceStore store = NewFileStore();
            LuaCsModRuntime first = NewRuntime(store);
            first.LoadMod("zz-one", "mods_export('value', 1)", LuaCapabilities.Read);
            first.LoadMod("aa-two", "mods_export('value', 2)", LuaCapabilities.Read);

            LuaCsModRuntime rehydrated = NewRuntime(store);
            Assert.AreEqual(2, rehydrated.RehydrateFromStore(LuaCapabilities.All));
            LuaCsModRuntime exact = NewRuntime(store);
            Assert.AreEqual(2, exact.RehydrateExactOrThrow(LuaCapabilities.All));
        }

        [Test]
        public void LuaCs_RestoreOrder_NilFirst_ThenUnorderedById_ThenAscending_TiesById_NegativeIsUnordered()
        {
            LuaModManifest tieB = new() { Id = "b", LoadOrder = 2 };
            LuaModManifest tieA = new() { Id = "a", LoadOrder = 2 };
            LuaModManifest earliest = new() { Id = "c", LoadOrder = 1 };
            LuaModManifest negative = new() { Id = "d", LoadOrder = -4 };
            LuaModManifest legacy = new() { Id = "0", LoadOrder = 0 };
            List<LuaModManifest> manifests = new() { tieB, legacy, negative, tieA, null, earliest };

            manifests.Sort(LuaCsModRuntime.CompareRestoreOrder);

            CollectionAssert.AreEqual(new[] { null, legacy, negative, earliest, tieA, tieB }, manifests,
                "A negative order counts as no recorded order: it sorts by id with the legacy mods.");
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

        [Test]
        public void LuaCs_ImportUnderNoFull_ThenRehydrateWithHostFull_DoesNotReacquireFull()
        {
            // SECURITY: a Full-DECLARING bundle imported WITHOUT host Full must not persist Full, so a later
            // restart's RehydrateFromStore — even under a host-wide allowFull=true — cannot re-grant Full to
            // this specific mod that was imported without it.
            FakeSourceStore sourceStore = new();
            LuaCsModRuntime source = NewRuntime(sourceStore);
            source.LoadMod("shared", "hooks_on('ping', function() end)",
                LuaCapabilities.Read | LuaCapabilities.Full);
            string bundle = source.ExportMod("shared");
            Assert.IsNotNull(bundle);

            FakeSourceStore store = new();
            LuaCsModRuntime importRuntime = NewRuntime(store);
            Assert.IsTrue(importRuntime.ImportMod(bundle, LuaCapabilities.All | LuaCapabilities.Full,
                false));
            Assert.AreEqual(LuaCapabilities.None,
                importRuntime.ListMods()[0].Capabilities & LuaCapabilities.Full,
                "Imported mod must load without Full when the host did not opt in.");

            // Restart: a fresh runtime rehydrates from the SAME store under a host-wide allowFull=TRUE.
            LuaCsModRuntime restarted = NewRuntime(store);
            restarted.RehydrateFromStore(LuaCapabilities.All | LuaCapabilities.Full, true);
            Assert.IsTrue(restarted.IsLoaded("shared"));
            Assert.AreEqual(LuaCapabilities.None,
                restarted.ListMods()[0].Capabilities & LuaCapabilities.Full,
                "A mod imported without Full must NOT re-acquire Full on restart, even under host-wide allowFull.");
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
            MemoryLuaScriptVersionStore versions = new(2);
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

        // ==================== B2-04: a first load refused after its chunk ran ====================

        /// <summary>
        /// B2-04: a first load refused after its chunk ran (another load took the last mod slot of its
        /// actor while the chunk ran) was refused after the failed-build rollback, so a mod reported as
        /// not loaded kept its logic-slot formula, its quota attribution and its signal connections,
        /// and answered formula calls. The refusal now runs inside that rollback.
        /// </summary>
        [TestCase(false)]
        [TestCase(true)]
        public void LuaCs_FirstLoadRefusedAfterItsChunkRan_LeavesNoFormulaNoAttributionAndNoConnection(bool withRbxApi)
        {
            ActorContext actor = new LocalActorIdentityProvider("capacity-actor")
                .GetActorContext(BuiltInAgentRoleIds.Programmer);
            LuaCsRbxApiBindings rbxApi = withRbxApi ? new LuaCsRbxApiBindings() : null;
            CoreAI.Tests.EditMode.RbxApi.Acceptance.Mvp1AcceptanceMemoryStore modData = new();
            LuaCsModStack stack = null;
            stack = LuaCsModRuntimeFactory.Create(new LuaCsModStackOptions
            {
                Logger = new CoreAI.Tests.EditMode.RbxApi.Acceptance.Mvp1AcceptanceNullLogger(),
                ModStore = modData,
                Capabilities = CapacityCapabilities,
                OneOffCapabilities = CapacityCapabilities,
                RbxApi = rbxApi,
                MaxMods = 1,
                AdditionalGameplayBindings = (registry, capabilities) =>
                    registry.Register("load_sibling", new Func<bool>(() =>
                    {
                        stack.Runtime.LoadMod(actor, "sibling", "local sibling = 1", CapacityCapabilities, false);
                        return true;
                    }))
            });
            LuaCsLogicSlots slots = stack.GameplayBindings.LogicSlots;
            slots.DeclareSlot("dmg");
            string connect = withRbxApi
                ? "game:GetService('RunService').Heartbeat:Connect(function() store_set('beat', 'yes') end)\n"
                : "";

            Exception refused = Assert.Catch(() => stack.Runtime.LoadMod(
                actor,
                "first",
                "logic_define('dmg', function() return 777 end)\n" + connect + "load_sibling()",
                CapacityCapabilities,
                false));

            StringAssert.Contains("quota", refused.Message, "precondition: the sibling took the last slot");
            Assert.IsFalse(stack.Runtime.IsLoaded("first"));
            Assert.IsTrue(stack.Runtime.IsLoaded("sibling"), "the load that took the slot stays");
            Assert.IsFalse(slots.IsOverridden("dmg"), "a mod whose load was refused answers no formula");
            Assert.IsFalse(QuotaAttributionOf(stack.Runtime).Contains("first"),
                "a refused first load drops the quota attribution it recorded");
            if (withRbxApi)
            {
                CollectionAssert.IsEmpty(rbxApi.Connections.GetOwnedBy("first"),
                    "the refused chunk's signal connection was rolled back");
                rbxApi.Scheduler.Advance(1d / 60d);
                stack.Runtime.Tick(1d / 60d);
                Assert.AreEqual("", modData.Get("first", "beat"), "no handler of the refused mod ran");
            }
        }

        // ==================== C2-01: a second build of an id already being built ====================

        /// <summary>The code of the build that must keep running: a Heartbeat handler and a scheduler thread.</summary>
        private const string SurvivingBuildCode =
            "game:GetService('RunService').Heartbeat:Connect(function() store_set('beat', 'yes') end)\n"
            + "task.delay(0, function() store_set('thread', 'ran') end)";

        /// <summary>The code of the build that must be refused: it would record that it ran.</summary>
        private const string RefusedBuildCode = "store_set('refused_ran', 'yes')";

        private static LuaCsModStack SameIdStack(
            LuaCsRbxApiBindings rbxApi,
            CoreAI.Tests.EditMode.RbxApi.Acceptance.Mvp1AcceptanceMemoryStore modData,
            Func<LuaCsModStack> self,
            Func<LuaCsModStack, string> nestedBuild)
        {
            return LuaCsModRuntimeFactory.Create(new LuaCsModStackOptions
            {
                Logger = new CoreAI.Tests.EditMode.RbxApi.Acceptance.Mvp1AcceptanceNullLogger(),
                ModStore = modData,
                Capabilities = CapacityCapabilities,
                OneOffCapabilities = CapacityCapabilities,
                RbxApi = rbxApi,
                AdditionalGameplayBindings = (registry, capabilities) =>
                    registry.Register("build_same_id", new Func<string>(() => nestedBuild(self())))
            });
        }

        private static void PumpFrames(LuaCsModStack stack, LuaCsRbxApiBindings rbxApi, int frames)
        {
            for (int frame = 0; frame < frames; frame++)
            {
                rbxApi.Scheduler.Advance(1d / 60d);
                stack.Runtime.Tick(1d / 60d);
            }
        }

        /// <summary>
        /// C2-01: a first load of an id another first load was still building ran its own chunk, was
        /// refused "loaded concurrently" at its commit, and the rollback of that refused build, keyed by
        /// mod id on the Roblox side, tore down the Heartbeat handler, the threads and the script backing
        /// of the build that won, which stayed reported as loaded and did nothing. The second build is now
        /// refused before its chunk runs, so no rollback can reach the first.
        /// </summary>
        [Test]
        public void LuaCs_FirstLoadOfAnIdAnotherLoadIsBuilding_IsRefusedBeforeItsChunkRuns_TheFirstKeepsRunning()
        {
            ActorContext actor = new LocalActorIdentityProvider("same-id-actor")
                .GetActorContext(BuiltInAgentRoleIds.Programmer);
            LuaCsRbxApiBindings rbxApi = new();
            CoreAI.Tests.EditMode.RbxApi.Acceptance.Mvp1AcceptanceMemoryStore modData = new();
            LuaCsModStack stack = null;
            stack = SameIdStack(rbxApi, modData, () => stack, self =>
            {
                try
                {
                    self.Runtime.LoadMod(actor, "dup", RefusedBuildCode, CapacityCapabilities, false);
                    return "loaded";
                }
                catch (InvalidOperationException ex)
                {
                    return ex.Message;
                }
            });

            stack.Runtime.LoadMod(
                actor,
                "dup",
                "store_set('nested', build_same_id())\n" + SurvivingBuildCode,
                CapacityCapabilities,
                false);
            PumpFrames(stack, rbxApi, 3);

            Assert.AreEqual("Mod 'dup' was loaded concurrently.", modData.Get("dup", "nested"));
            Assert.AreEqual("", modData.Get("dup", "refused_ran"), "the refused build's chunk never ran");
            Assert.IsTrue(stack.Runtime.IsLoaded("dup"));
            Assert.AreEqual(1, rbxApi.Connections.GetOwnedBy("dup").Count,
                "the loaded build keeps its Heartbeat connection");
            Assert.AreEqual("yes", modData.Get("dup", "beat"), "the loaded build's handler runs");
            Assert.AreEqual("ran", modData.Get("dup", "thread"), "the loaded build's thread ran");
            Assert.DoesNotThrow(() => stack.Runtime.ReloadMod("dup", "local again = true"),
                "the refusal leaves no build of the id marked in flight");
        }

        /// <summary>
        /// C2-01 twin for reloads: a reload of an id another reload was still building ran, won, and the
        /// outer reload was then refused "reloaded concurrently" and its rollback tore down the winner.
        /// The inner reload is now refused before the previous run's objects leave the world or its
        /// chunk runs, and the outer reload completes.
        /// </summary>
        [Test]
        public void LuaCs_ReloadOfAnIdAnotherReloadIsBuilding_IsRefusedBeforeAnythingRuns_TheFirstCompletes()
        {
            ActorContext actor = new LocalActorIdentityProvider("same-id-actor")
                .GetActorContext(BuiltInAgentRoleIds.Programmer);
            LuaCsRbxApiBindings rbxApi = new();
            CoreAI.Tests.EditMode.RbxApi.Acceptance.Mvp1AcceptanceMemoryStore modData = new();
            LuaCsModStack stack = null;
            stack = SameIdStack(rbxApi, modData, () => stack, self =>
            {
                try
                {
                    self.Runtime.ReloadMod("dup", RefusedBuildCode);
                    return "reloaded";
                }
                catch (InvalidOperationException ex)
                {
                    return ex.Message;
                }
            });
            stack.Runtime.LoadMod(actor, "dup", "local first = true", CapacityCapabilities, false);
            string outerCode = "store_set('nested', build_same_id())\n" + SurvivingBuildCode;

            stack.Runtime.ReloadMod("dup", outerCode);
            PumpFrames(stack, rbxApi, 3);

            Assert.AreEqual("Mod 'dup' was reloaded concurrently.", modData.Get("dup", "nested"));
            Assert.AreEqual("", modData.Get("dup", "refused_ran"), "the refused reload's chunk never ran");
            Assert.IsTrue(stack.Runtime.TryGetModSource("dup", out string live));
            Assert.AreEqual(outerCode, live, "the reload that was building first is the live one");
            Assert.AreEqual(1, rbxApi.Connections.GetOwnedBy("dup").Count);
            Assert.AreEqual("yes", modData.Get("dup", "beat"));
            Assert.AreEqual("ran", modData.Get("dup", "thread"));
        }

        // ==================== C2-08: load orders a store cannot follow ====================

        /// <summary>
        /// C2-08: a store holding an order no store records (long.MaxValue, from a hand-edited store)
        /// saturated every later first load at long.MaxValue, where they all tied and restored by id. The
        /// value now reads as no recorded order: later first loads keep counting past the real ones and
        /// restore in the order they were loaded, after it.
        /// </summary>
        [Test]
        public void LuaCs_AStoredLoadOrderPastTheMaximum_ReadsAsUnordered_AndLaterFirstLoadsKeepTheirOrder()
        {
            FakeSourceStore store = new();
            SaveStoredMod(store, "a-planted", long.MaxValue);
            SaveStoredMod(store, "m-older", 4);
            LuaCsModRuntime session = NewRuntime(store);

            session.LoadMod("z-first", "local x = 1", LuaCapabilities.Read);
            session.LoadMod("b-second", "local x = 2", LuaCapabilities.Read);

            Assert.AreEqual(5L, StoredLoadOrder(store, "z-first"));
            Assert.AreEqual(6L, StoredLoadOrder(store, "b-second"),
                "each later first load gets its own order instead of tying at the maximum");
            LuaCsModRuntime restarted = NewRuntime(store);
            Assert.AreEqual(4, restarted.RehydrateFromStore(LuaCapabilities.All));
            CollectionAssert.AreEqual(
                new[] { "a-planted", "m-older", "z-first", "b-second" },
                LoadedIds(restarted));
        }

        [Test]
        public void LuaCs_RestoreOrder_AnOrderPastTheMaximum_SortsWithTheUnorderedMods()
        {
            LuaModManifest planted = new() { Id = "b", LoadOrder = long.MaxValue };
            LuaModManifest atMaximum = new() { Id = "c", LoadOrder = LuaModManifest.MaximumLoadOrder };
            LuaModManifest legacy = new() { Id = "a", LoadOrder = 0 };
            LuaModManifest ordered = new() { Id = "d", LoadOrder = 3 };
            List<LuaModManifest> manifests = new() { planted, atMaximum, ordered, legacy };

            manifests.Sort(LuaCsModRuntime.CompareRestoreOrder);

            CollectionAssert.AreEqual(new[] { legacy, planted, ordered, atMaximum }, manifests);
        }

        /// <summary>
        /// C2-08: the file store answers an empty list when it cannot list its folders, and a first load
        /// stamped meanwhile got order 1, an order the store never recorded, which restores the new mod
        /// ahead of every older ordered mod. A listing the store marks unreadable now stamps no order,
        /// and says so.
        /// </summary>
        [Test]
        public void LuaCs_FirstLoadWhileTheStoreCannotBeListed_StampsNoLoadOrder_AndLogsIt()
        {
            UnlistableSourceStore store = new();
            FakeLog log = new();
            LuaCsModRuntime runtime = new(log: log, sourceStore: store);

            runtime.LoadMod("new-mod", "local x = 1", LuaCapabilities.Read);

            Assert.IsTrue(store.Inner.Contains("new-mod"), "the load still succeeds and persists");
            Assert.AreEqual(0L, store.Inner.ManifestOf("new-mod").LoadOrder,
                "no order is recorded rather than the first one");
            Assert.IsTrue(log.Errors.Exists(line => line.Contains("could not list its mods")),
                string.Join(" | ", log.Errors));
        }

        /// <summary>A store whose listing fails the way the file store's does: an empty answer marked unreadable.</summary>
        private sealed class UnlistableSourceStore : ILuaModSourceStore
        {
            public FakeSourceStore Inner { get; } = new();

            public void Save(string id, string source, LuaModManifest manifest)
            {
                Inner.Save(id, source, manifest);
            }

            public bool TryLoad(string id, out string source, out LuaModManifest manifest)
            {
                return Inner.TryLoad(id, out source, out manifest);
            }

            public IReadOnlyList<LuaModManifest> List()
            {
                return new UnreadableLuaModSourceListing("The test store's folder is gone.");
            }

            public void SetActive(string id, bool active)
            {
                Inner.SetActive(id, active);
            }

            public void Delete(string id)
            {
                Inner.Delete(id);
            }
        }

        private const string SpinningTimerSource = "hooks_every(0.01, function() while true do end end)";

        /// <summary>
        /// HUB-CRASH R3: a mod that stalls the game at every budget kept doing so after the player killed
        /// the frozen process, because the next start ran it again. A quarantine for budget trips marks
        /// the stored package inactive and suspended, so the restart does not start it.
        /// </summary>
        [Test]
        [Timeout(60000)]
        public void BudgetTripQuarantine_SuspendsTheStoredPackage_AndTheNextStartDoesNotRunIt()
        {
            FakeSourceStore store = new();
            LuaCsModRuntime runtime = new(sourceStore: store, handlerMaxSteps: 20_000);
            runtime.LoadMod("spinner", SpinningTimerSource);

            runtime.Tick(0.05);
            runtime.Tick(0.05);

            Assert.IsTrue(runtime.ListMods()[0].Quarantined, "precondition: two budget trips quarantined it");
            Assert.IsTrue(store.TryLoad("spinner", out string source, out LuaModManifest manifest));
            Assert.AreEqual(SpinningTimerSource, source, "the suspension keeps the stored source");
            Assert.IsFalse(manifest.Active, "a mod suspended for budget trips must not start with the game");
            Assert.IsTrue(manifest.SuspendedAfterBudgetTrips, "the Hub must be able to say why it is off");

            LuaCsModRuntime restarted = new(sourceStore: store, handlerMaxSteps: 20_000);
            Assert.AreEqual(0, restarted.RehydrateFromStore(LuaCapabilities.All));
            Assert.IsFalse(restarted.IsLoaded("spinner"), "the restart must not run the stalling mod again");
            Assert.AreEqual(0, restarted.RehydrateExactOrThrow(LuaCapabilities.All),
                "nor may a world restore of the same sources");

            restarted.LoadMod("spinner", "local fixed = true");

            Assert.IsTrue(store.TryLoad("spinner", out _, out LuaModManifest started));
            Assert.IsTrue(started.Active, "starting it by hand makes it start with the game again");
            Assert.IsFalse(started.SuspendedAfterBudgetTrips, "and clears the suspension");
        }

        [Test]
        [Timeout(60000)]
        public void BudgetTripQuarantine_ASuccessfulReload_ClearsTheSuspension()
        {
            FakeSourceStore store = new();
            LuaCsModRuntime runtime = new(sourceStore: store, handlerMaxSteps: 20_000);
            runtime.LoadMod("spinner", SpinningTimerSource);
            runtime.Tick(0.05);
            runtime.Tick(0.05);
            Assert.IsTrue(store.TryLoad("spinner", out _, out LuaModManifest suspended));
            Assert.IsTrue(suspended.SuspendedAfterBudgetTrips, "precondition");

            runtime.ReloadMod("spinner", "local fixed = true");

            Assert.IsFalse(runtime.ListMods()[0].Quarantined);
            Assert.IsTrue(store.TryLoad("spinner", out _, out LuaModManifest repaired));
            Assert.IsTrue(repaired.Active);
            Assert.IsFalse(repaired.SuspendedAfterBudgetTrips);
        }

        /// <summary>Negative twin: a quarantine for ordinary errors costs no time and suspends nothing.</summary>
        [Test]
        public void ErrorStreakQuarantine_DoesNotSuspendTheStoredPackage()
        {
            FakeSourceStore store = new();
            LuaCsModRuntime runtime = NewRuntime(store);
            runtime.LoadMod("thrower", "hooks_every(0.01, function() error('boom') end)");

            for (int tick = 0; tick < LuaCsModRuntime.DefaultMaxErrorsBeforeQuarantine; tick++)
            {
                runtime.Tick(0.05);
            }

            Assert.IsTrue(runtime.ListMods()[0].Quarantined, "precondition: the error streak quarantined it");
            Assert.IsTrue(store.TryLoad("thrower", out _, out LuaModManifest manifest));
            Assert.IsTrue(manifest.Active);
            Assert.IsFalse(manifest.SuspendedAfterBudgetTrips);
            Assert.AreEqual(1, NewRuntime(store).RehydrateFromStore(LuaCapabilities.All),
                "a mod quarantined for ordinary errors starts again with the game, as before");
        }

        [Test]
        public void SuspendedAfterBudgetTrips_IsWrittenOnlyWhenSet_AndSurvivesTheFileStore()
        {
            FileLuaModSourceStore store = NewFileStore();
            store.Save("plain", "local x = 1", new LuaModManifest { Id = "plain", Active = true });
            store.Save("suspended", "local x = 1", new LuaModManifest
            {
                Id = "suspended",
                Active = false,
                SuspendedAfterBudgetTrips = true
            });

            Assert.IsTrue(store.TryLoad("plain", out _, out LuaModManifest plain));
            Assert.IsFalse(plain.SuspendedAfterBudgetTrips);
            Assert.IsTrue(store.TryLoad("suspended", out _, out LuaModManifest suspended));
            Assert.IsTrue(suspended.SuspendedAfterBudgetTrips);
            StringAssert.DoesNotContain("SuspendedAfterBudgetTrips",
                Newtonsoft.Json.JsonConvert.SerializeObject(plain),
                "a manifest that was never suspended stays byte-identical to one written before the field");
        }

        /// <summary>
        /// Startup objects are runtime state and are never persisted. A restart or a world restore runs
        /// every active main chunk again through the same build, so the restarted run records its own
        /// startup objects and the first clean reload after it cleans them.
        /// </summary>
        [TestCase(false)]
        [TestCase(true)]
        public void AfterARestart_TheFirstCleanReload_CleansWhatTheRestartedRunBuilt(bool exactRestore)
        {
            const string castle = @"
                local root = Instance.new('Folder')
                root.Name = 'Castle'
                root.Parent = workspace
                for index = 1, 4 do
                    Instance.new('Part').Parent = root
                end";
            FakeSourceStore store = new();
            LuaCsModStack before = RbxStack(new LuaCsRbxApiBindings(), store);
            before.Runtime.LoadMod("castle", castle);

            LuaCsRbxApiBindings world = new();
            LuaCsModStack restarted = RbxStack(world, store);
            int started = exactRestore
                ? restarted.Runtime.RehydrateExactOrThrow(LuaCapabilities.All)
                : restarted.Runtime.RehydrateFromStore(LuaCapabilities.All);
            Assert.AreEqual(1, started, "precondition: the restart ran the mod's main chunk");

            ModReloadReport report = restarted.Runtime.ReloadMod(
                "castle", castle + "\n-- edit", ModReloadMode.CleanStartupObjects);

            Assert.AreEqual(5, report.CleanedObjects, "the restarted run's own startup objects are known");
            int castles = 0;
            foreach (CoreAI.Mods.Rbx.Instances.RbxInstance child in world.Registry.WorldRoot.GetChildren())
            {
                if (child.Name == "Castle")
                {
                    castles++;
                }
            }

            Assert.AreEqual(1, castles, "the first save after a restart must not duplicate the castle");
        }

        private static LuaCsModStack RbxStack(LuaCsRbxApiBindings bindings, ILuaModSourceStore store)
        {
            return LuaCsModRuntimeFactory.Create(new LuaCsModStackOptions
            {
                ModSourceStore = store,
                Capabilities = LuaCapabilities.All,
                OneOffCapabilities = LuaCapabilities.All,
                RbxApi = bindings
            });
        }

        private const LuaCapabilities CapacityCapabilities =
            LuaCapabilities.Read | LuaCapabilities.WorldEdit | LuaCapabilities.LogicOverride;

        private static System.Collections.IDictionary QuotaAttributionOf(LuaCsModRuntime runtime)
        {
            System.Reflection.FieldInfo field = typeof(LuaCsModRuntime).GetField(
                "_quotaActorByOwnerModId",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            Assert.IsNotNull(field, "the runtime's quota attribution map was renamed");
            return (System.Collections.IDictionary)field.GetValue(runtime);
        }
    }
}
