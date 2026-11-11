#if COREAI_HAS_HUB
using System;
using System.Collections.Generic;
using CoreAI.Ai;
using CoreAI.Ai.Hub;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode
{
    /// <summary>
    /// MVP4 step A2, the fourth manifest writer: a Hub save (<c>HubModServiceBase</c>) derives the manifest's
    /// <c>Context</c> by <see cref="LuaModManifest.ResolveContext"/> like the runtime, the bundled seeder and the
    /// world package (<see cref="Mvp4ScriptManifestEditModeTests"/>), and keeps the stored one when the header
    /// declares none.
    /// </summary>
    [TestFixture]
    public sealed class Mvp4HubManifestContextEditModeTests
    {
        [Test]
        public void ManifestWriters_HubSaveCarriesTheDeclaredContext()
        {
            MemoryStore store = new();
            ContextHubService service = new(store);

            service.SaveOrReload("door", Source("door", "Client"));

            Assert.AreEqual("client", store.Manifests["door"].Context);
            Assert.Greater(store.StrictSaves, 0, "the Hub saves through the strict acknowledgment");
        }

        [Test]
        public void ManifestWriters_HubSaveOfAHeaderWithoutContext_KeepsTheStoredOne()
        {
            MemoryStore store = new();
            store.Save("door", Source("door", "server"), new LuaModManifest { Id = "door", Name = "door", Context = "server" });
            ContextHubService service = new(store);

            service.SaveOrReload("door", Source("door", null));

            Assert.AreEqual("server", store.Manifests["door"].Context);
        }

        private static string Source(string id, string context)
        {
            return "--[[@coreai\nid: " + id + "\nname: " + id + "\ncapabilities: Read\n"
                   + (context == null ? "" : "context: " + context + "\n") + "]]\nlocal n = 1\n";
        }

        /// <summary>
        /// An in-memory store with the strict save acknowledgment the Hub's save path requires, as the file store
        /// the real Hub uses has.
        /// </summary>
        private sealed class MemoryStore : ILuaModSourceStore, ILuaModSourceStrictWriter
        {
            public readonly Dictionary<string, LuaModManifest> Manifests = new(StringComparer.Ordinal);
            private readonly Dictionary<string, string> _sources = new(StringComparer.Ordinal);

            public int StrictSaves { get; private set; }

            public void Save(string id, string source, LuaModManifest manifest)
            {
                _sources[id] = source;
                Manifests[id] = manifest;
            }

            public void SaveStrict(string id, string source, LuaModManifest manifest)
            {
                StrictSaves++;
                Save(id, source, manifest);
            }

            public bool TryLoad(string id, out string source, out LuaModManifest manifest)
            {
                bool found = _sources.TryGetValue(id, out source);
                Manifests.TryGetValue(id, out manifest);
                source ??= "";
                return found;
            }

            public IReadOnlyList<LuaModManifest> List()
            {
                return new List<LuaModManifest>(Manifests.Values);
            }

            public void SetActive(string id, bool active)
            {
                if (Manifests.TryGetValue(id, out LuaModManifest manifest))
                {
                    manifest.Active = active;
                }
            }

            public void Delete(string id)
            {
                _sources.Remove(id);
                Manifests.Remove(id);
            }
        }

        /// <summary>A Hub service whose runtime only remembers the loaded sources; persistence is the base class's.</summary>
        private sealed class ContextHubService : HubModServiceBase
        {
            private readonly Dictionary<string, string> _live = new(StringComparer.Ordinal);

            public ContextHubService(ILuaModSourceStore store) : base(store, LuaCapabilities.All, false)
            {
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
        }
    }
}
#endif
