#if COREAI_HAS_HUB
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using CoreAI.Ai;
using CoreAI.Ai.Hub;
using CoreAI.Ai.LuaCs;
using NUnit.Framework;
using UnityEngine;

namespace CoreAI.Tests.EditMode
{
    /// <summary>
    /// Security regression guarding the Hub Mods tab Full-tier gate: the binder's <c>allowFullTier</c>
    /// flag must default to <c>false</c>, and a mod imported through the Hub service must never receive
    /// <see cref="LuaCapabilities.Full"/> unless the host explicitly opted in (allowFull), even when the
    /// bundle's own header requests Full. Full is a deliberate host decision, never derived from an
    /// untrusted mod's header on the import/share/rehydrate path.
    /// </summary>
    public sealed class CoreAiModsHubBinderFullTierEditModeTests
    {
        [Test]
        public void CoreAiModsHubBinder_AllowFullTier_DefaultsToFalse()
        {
            GameObject go = new(nameof(CoreAiModsHubBinderFullTierEditModeTests));
            try
            {
                CoreAiModsHubBinder binder = go.AddComponent<CoreAiModsHubBinder>();
                FieldInfo field = typeof(CoreAiModsHubBinder).GetField(
                    "allowFullTier", BindingFlags.Instance | BindingFlags.NonPublic);
                Assert.IsNotNull(field, "Expected a serialized 'allowFullTier' field on the binder.");
                Assert.IsFalse((bool)field.GetValue(binder),
                    "allowFullTier must default to false so untrusted mods cannot self-escalate to Full.");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(go);
            }
        }

#if !COREAI_NO_LUA
        private SynchronizationContext _savedContext;

        [SetUp]
        public void DetachSynchronizationContext()
        {
            // See LuaCsModRuntimePersistenceEditModeTests: detach the Unity context so the runtime's
            // sync-over-async execution guard does not deadlock the blocked main thread.
            _savedContext = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(null);
        }

        [TearDown]
        public void RestoreSynchronizationContext()
        {
            SynchronizationContext.SetSynchronizationContext(_savedContext);
        }

        /// <summary>In-memory package store so the test can drive import without touching the file system.</summary>
        private sealed class FakeSourceStore : ILuaModSourceStore
        {
            private sealed class Entry
            {
                public string Source = "";
                public LuaModManifest Manifest;
            }

            private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);

            public void Save(string id, string source, LuaModManifest manifest)
            {
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
        }

        private static string ExportFullTierBundle()
        {
            FakeSourceStore exportStore = new();
            LuaCsModRuntime exporter = new(sourceStore: exportStore);
            exporter.LoadMod("shared", "local x = 1", LuaCapabilities.Read | LuaCapabilities.Full);
            string bundle = exporter.ExportMod("shared");
            Assert.IsNotNull(bundle, "ExportMod must return a bundle whose header requests Full.");
            return bundle;
        }

        [Test]
        public void HubService_ImportMod_StripsFull_WhenHostDidNotOptIn()
        {
            string bundle = ExportFullTierBundle();

            FakeSourceStore store = new();
            LuaCsModRuntime runtime = new(sourceStore: store);
            // Host grant includes Full, but allowFull is false — the default Mods tab wiring.
            IHubModService service = new LuaCsModRuntimeHubService(
                runtime, store, LuaCapabilities.All | LuaCapabilities.Full, false);

            Assert.IsTrue(service.ImportMod(bundle), "Import of a valid bundle must succeed.");
            Assert.IsTrue(runtime.IsLoaded("shared"));
            Assert.AreEqual(LuaCapabilities.None, runtime.ListMods()[0].Capabilities & LuaCapabilities.Full,
                "An imported mod must not self-escalate to Full when the host has not opted in.");
        }

        [Test]
        public void HubService_ImportMod_KeepsFull_WhenHostExplicitlyOptedIn()
        {
            string bundle = ExportFullTierBundle();

            FakeSourceStore store = new();
            LuaCsModRuntime runtime = new(sourceStore: store);
            // Explicit host opt-in (allowFullTier=true) — trusted/first-party/singleplayer content only.
            IHubModService service = new LuaCsModRuntimeHubService(
                runtime, store, LuaCapabilities.All | LuaCapabilities.Full, true);

            Assert.IsTrue(service.ImportMod(bundle));
            Assert.AreEqual(LuaCapabilities.Full, runtime.ListMods()[0].Capabilities & LuaCapabilities.Full,
                "Full must survive import only when the host explicitly opted in and grants Full.");
        }
#endif
    }
}
#endif
