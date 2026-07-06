using System;
using System.Collections.Generic;
using CoreAI.Ai;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode
{
    /// <summary>
    /// Persistence and sharing tests for <see cref="LuaModRuntime"/> driven through a fake in-memory
    /// <see cref="ILuaModSourceStore"/>: auto-persist on load, rehydrate of active (and skip of
    /// dormant) mods, dormant-marking on unload, deletion on forget, an export/import round-trip
    /// between two runtimes, and the capability masking that strips <see cref="LuaCapabilities.Full"/>
    /// from persisted/shared mods unless explicitly allowed.
    /// </summary>
    public sealed class LuaModPersistenceEditModeTests
    {
        /// <summary>
        /// In-memory <see cref="ILuaModSourceStore"/> capturing source + manifest per id so the test
        /// can assert exactly what the runtime persisted without touching the file system.
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

        /// <summary>
        /// Minimal capability-scoped game bindings so a mod can request any tier (including
        /// <see cref="LuaCapabilities.Full"/>) and load without depending on the real game surface.
        /// </summary>
        private sealed class StubBindings : IGameLuaRuntimeBindings, ICapabilityScopedLuaBindings
        {
            public void RegisterGameplayApis(Sandbox.LuaApiRegistry registry)
            {
            }

            public void RegisterGameplayApis(Sandbox.LuaApiRegistry registry, LuaCapabilities capabilities)
            {
            }
        }

        private static LuaModRuntime NewRuntime(FakeSourceStore store)
        {
            return new LuaModRuntime(new StubBindings(), sourceStore: store);
        }

        [Test]
        public void LoadMod_AutoPersistsSourceAndActiveManifest()
        {
            FakeSourceStore store = new();
            LuaModRuntime runtime = NewRuntime(store);

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
        public void RehydrateFromStore_LoadsActiveMods_SkipsInactive()
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

            LuaModRuntime runtime = NewRuntime(store);
            int loaded = runtime.RehydrateFromStore(LuaCapabilities.All);

            Assert.AreEqual(1, loaded, "Only the active mod must be rehydrated.");
            Assert.IsTrue(runtime.IsLoaded("active"));
            Assert.IsFalse(runtime.IsLoaded("dormant"), "A dormant mod must not auto-load.");
        }

        [Test]
        public void UnloadMod_MarksStoredManifestInactive_WithoutDeleting()
        {
            FakeSourceStore store = new();
            LuaModRuntime runtime = NewRuntime(store);
            runtime.LoadMod("m", "local x = 1", LuaCapabilities.Read);

            Assert.IsTrue(runtime.UnloadMod("m"));

            Assert.IsTrue(store.Contains("m"), "Unloading must keep the persisted package (dormant, not deleted).");
            Assert.IsFalse(store.ManifestOf("m").Active, "Unloading must mark the stored manifest inactive.");
        }

        [Test]
        public void ForgetMod_DeletesFromStore()
        {
            FakeSourceStore store = new();
            LuaModRuntime runtime = NewRuntime(store);
            runtime.LoadMod("m", "local x = 1", LuaCapabilities.Read);
            Assert.IsTrue(store.Contains("m"));

            Assert.IsTrue(runtime.ForgetMod("m"));

            Assert.IsFalse(runtime.IsLoaded("m"));
            Assert.IsFalse(store.Contains("m"), "ForgetMod must delete the persisted package entirely.");
        }

        [Test]
        public void ExportThenImport_RoundTripsModIntoAnotherRuntime()
        {
            FakeSourceStore sourceStore = new();
            LuaModRuntime source = NewRuntime(sourceStore);
            source.LoadMod("shared", "hooks_on('ping', function() end)", LuaCapabilities.Read);

            string bundle = source.ExportMod("shared");
            Assert.IsNotNull(bundle, "ExportMod must return a bundle for a loaded mod.");
            StringAssert.Contains("shared", bundle);

            FakeSourceStore destStore = new();
            LuaModRuntime destination = NewRuntime(destStore);

            Assert.IsTrue(destination.ImportMod(bundle, LuaCapabilities.All));
            Assert.IsTrue(destination.IsLoaded("shared"), "Imported mod must be loaded in the destination runtime.");
            Assert.IsTrue(destStore.Contains("shared"), "Imported mod must be persisted in the destination store.");
            Assert.IsTrue(destination.TryGetModSource("shared", out string importedSource));
            Assert.AreEqual("hooks_on('ping', function() end)", importedSource);
        }

        [Test]
        public void RehydrateFromStore_MasksFull_UnlessAllowFull()
        {
            FakeSourceStore store = new();
            store.Save("priv", "local x = 1", new LuaModManifest
            {
                Id = "priv",
                Capabilities = (LuaCapabilities.Read | LuaCapabilities.Full).ToString(),
                Active = true
            });

            // Default rehydrate (allowFull:false) intersects with the host grant and strips Full.
            LuaModRuntime masked = NewRuntime(store);
            Assert.AreEqual(1, masked.RehydrateFromStore(LuaCapabilities.All | LuaCapabilities.Full));
            IReadOnlyList<LuaModInfo> maskedMods = masked.ListMods();
            Assert.AreEqual(1, maskedMods.Count);
            Assert.AreEqual(LuaCapabilities.None, maskedMods[0].Capabilities & LuaCapabilities.Full,
                "Full must be masked on rehydrate when allowFull is false.");
            Assert.AreEqual(LuaCapabilities.Read, maskedMods[0].Capabilities & LuaCapabilities.Read,
                "Non-Full capabilities allowed by the host grant must survive masking.");

            // allowFull:true keeps Full when the host grant also includes it.
            LuaModRuntime allowed = NewRuntime(store);
            Assert.AreEqual(1, allowed.RehydrateFromStore(LuaCapabilities.All | LuaCapabilities.Full, true));
            IReadOnlyList<LuaModInfo> allowedMods = allowed.ListMods();
            Assert.AreEqual(LuaCapabilities.Full, allowedMods[0].Capabilities & LuaCapabilities.Full,
                "Full must survive rehydrate when allowFull is true and the host grant includes it.");
        }

        [Test]
        public void ImportMod_MasksFull_UnlessAllowFull()
        {
            FakeSourceStore exportStore = new();
            LuaModRuntime exporter = NewRuntime(exportStore);
            exporter.LoadMod("priv", "local x = 1", LuaCapabilities.Read | LuaCapabilities.Full);
            string bundle = exporter.ExportMod("priv");
            Assert.IsNotNull(bundle);

            // Import with allowFull:false must strip Full even though the host grant includes it.
            FakeSourceStore maskedStore = new();
            LuaModRuntime masked = NewRuntime(maskedStore);
            Assert.IsTrue(masked.ImportMod(bundle, LuaCapabilities.All | LuaCapabilities.Full));
            Assert.AreEqual(LuaCapabilities.None, masked.ListMods()[0].Capabilities & LuaCapabilities.Full,
                "Full must be masked on import when allowFull is false.");

            // Import with allowFull:true keeps Full.
            FakeSourceStore allowedStore = new();
            LuaModRuntime allowed = NewRuntime(allowedStore);
            Assert.IsTrue(allowed.ImportMod(bundle, LuaCapabilities.All | LuaCapabilities.Full, true));
            Assert.AreEqual(LuaCapabilities.Full, allowed.ListMods()[0].Capabilities & LuaCapabilities.Full,
                "Full must survive import when allowFull is true and the host grant includes it.");
        }

        [Test]
        public void ExportMod_UnknownId_ReturnsNull()
        {
            FakeSourceStore store = new();
            LuaModRuntime runtime = NewRuntime(store);

            Assert.IsNull(runtime.ExportMod("nope"));
        }

        [Test]
        public void ImportMod_MalformedBundle_ReturnsFalse()
        {
            FakeSourceStore store = new();
            LuaModRuntime runtime = NewRuntime(store);

            Assert.IsFalse(runtime.ImportMod("not json {{", LuaCapabilities.All));
            Assert.IsFalse(runtime.ImportMod("", LuaCapabilities.All));
        }
    }
}
