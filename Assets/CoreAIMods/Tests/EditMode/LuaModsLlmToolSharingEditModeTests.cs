using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Ai;
using CoreAI.Ai.LuaCs;
using CoreAI.Infrastructure.Llm;
using CoreAI.Logging;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine;

namespace CoreAI.Tests.EditMode
{
    /// <summary>
    /// Export/import/forget sharing flow for persistent Lua mods, exercised through a
    /// <see cref="LuaCsModRuntime"/> wired with a fake <see cref="ILuaModSourceStore"/> and managed by a
    /// <see cref="LuaModsLlmTool"/>. Asserts that export yields a bundle, import loads + persists it
    /// into a second runtime, forget removes it from storage, and that a read-only tool
    /// (<c>allowModManagement: false</c>) blocks the import/forget mutations.
    /// </summary>
    public sealed class LuaModsLlmToolSharingEditModeTests
    {
        private CoreAISettingsAsset _settings;
        private SynchronizationContext _savedContext;

        [SetUp]
        public void SetUp()
        {
            _settings = ScriptableObject.CreateInstance<CoreAISettingsAsset>();

            // The Lua-CSharp runtime bridges its async VM to a synchronous call site; detaching the Unity
            // SynchronizationContext for the duration of each test lets those continuations complete on the
            // thread pool instead of deadlocking the blocked main thread (see LuaCsModRuntimeEditModeTests).
            _savedContext = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(null);
        }

        [TearDown]
        public void TearDown()
        {
            SynchronizationContext.SetSynchronizationContext(_savedContext);
            UnityEngine.Object.DestroyImmediate(_settings);
        }

        /// <summary>In-memory package store so the test can assert what the runtime persisted.</summary>
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

            public bool Contains(string id)
            {
                return _entries.ContainsKey(id);
            }
        }

        private LuaModsLlmTool CreateTool(
            LuaCsModRuntime runtime,
            LuaCapabilities granted = LuaCapabilities.All,
            bool allowManagement = true)
        {
            return new LuaModsLlmTool(runtime, _settings, NullLog.Instance, granted, allowManagement);
        }

        private static async Task<JObject> ExecuteAsync(LuaModsLlmTool tool, string action, string modId = null, string code = null)
        {
            string json = await tool.ExecuteAsync(action, modId, code);
            return JObject.Parse(json);
        }

        [Test]
        public async Task Export_Import_Forget_SharesModBetweenRuntimes()
        {
            FakeSourceStore sourceStore = new();
            LuaCsModRuntime source = new(sourceStore: sourceStore);
            LuaModsLlmTool sourceTool = CreateTool(source, LuaCapabilities.Read);

            JObject load = await ExecuteAsync(sourceTool, "load", "shared", "hooks_on('ping', function() end)");
            Assert.IsTrue(load.Value<bool>("success"), load.ToString());
            Assert.IsTrue(sourceStore.Contains("shared"), "load must auto-persist the mod.");

            // Export yields a non-empty shareable bundle that carries the mod id.
            string bundle = source.ExportMod("shared");
            Assert.IsFalse(string.IsNullOrEmpty(bundle), "ExportMod must return a bundle.");
            StringAssert.Contains("shared", bundle);

            // Import into a second runtime loads + persists the mod there.
            FakeSourceStore destStore = new();
            LuaCsModRuntime destination = new(sourceStore: destStore);
            Assert.IsTrue(destination.ImportMod(bundle, LuaCapabilities.All));
            Assert.IsTrue(destination.IsLoaded("shared"), "import must load the mod.");
            Assert.IsTrue(destStore.Contains("shared"), "import must persist the mod.");

            // Forget removes it from both runtime and storage.
            Assert.IsTrue(destination.ForgetMod("shared"));
            Assert.IsFalse(destination.IsLoaded("shared"));
            Assert.IsFalse(destStore.Contains("shared"), "forget must delete the persisted package.");
        }

        [Test]
        public async Task ReadOnlyTool_BlocksMutations_ButAllowsInspection()
        {
            FakeSourceStore store = new();
            LuaCsModRuntime runtime = new(sourceStore: store);
            runtime.LoadMod("existing", "local x = 1", LuaCapabilities.Read);

            LuaModsLlmTool readOnly = CreateTool(runtime, allowManagement: false);

            // Inspection still works.
            Assert.IsTrue((await ExecuteAsync(readOnly, "list")).Value<bool>("success"));
            Assert.IsTrue((await ExecuteAsync(readOnly, "get_source", "existing")).Value<bool>("success"));

            // Mutating actions are blocked, leaving the mod and its persisted package intact.
            Assert.IsFalse((await ExecuteAsync(readOnly, "load", "new_mod", "local y = 2")).Value<bool>("success"));
            Assert.IsFalse((await ExecuteAsync(readOnly, "unload", "existing")).Value<bool>("success"));
            Assert.IsTrue(runtime.IsLoaded("existing"));
            Assert.IsTrue(store.Contains("existing"));
        }
    }
}
