using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Threading;
using CoreAI.Ai;
using CoreAI.Ai.LuaCs;
using CoreAI.Infrastructure.Logging;
using CoreAI.Mods.Rbx.Instances;
using CoreAI.Mods.Rbx.Instances.Scripts;
using CoreAI.Mods.WorldPackages;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode.RbxApi.ScriptsLua
{
    /// <summary>
    /// MVP4 step C1-4 through the production mod stack and the world package: the writer picks the lowest format
    /// version that can express the world (rule R4), so a world of mods that declare nothing is written byte for byte
    /// as before, and a declared mod's bound instance is saved with its binding. Every rule has its negative twin.
    /// </summary>
    [TestFixture]
    public sealed class ScriptWorldPackageEditModeTests
    {
        private const string WorldId = "c1-script-world";
        private const string ServerHeader = "-- @coreai context: server\n";
        private static readonly DateTime CapturedAtUtc = new(2026, 11, 11, 21, 0, 0, DateTimeKind.Utc);

        private SynchronizationContext _savedContext;
        private World _world;

        [SetUp]
        public void SetUp()
        {
            _savedContext = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(null);
            _world = new World();
        }

        [TearDown]
        public void TearDown()
        {
            _world?.Dispose();
            _world = null;
            SynchronizationContext.SetSynchronizationContext(_savedContext);
        }

        // ---- R4: the writer's version ----------------------------------------------------------

        [Test]
        public void Writer_LegacyOnlyWorld_StaysV1ByteIdentical()
        {
            _world.Load("legacy", "local p = Instance.new('Part') p.Name = 'Built' p.Parent = workspace");

            RbxWorldPackagePayload production = _world.Capture();
            byte[] package = RbxWorldPackageSerializer.WritePackage(production);

            Assert.AreEqual(1, production.FormatVersion, "a world of mods that declare nothing needs no bindings");
            CollectionAssert.AreEqual(
                RbxWorldPackageSerializer.WritePackage(
                    _world.Capture(RbxWorldPackageSerializer.LegacyScriptPlacementFormatVersion)),
                package, "byte for byte the version 1 package every earlier build wrote");
            JObject manifest = EntryJson(package, RbxWorldPackageSerializer.ManifestEntryName);
            Assert.AreEqual(1, (int)manifest["format_version"]);
            Assert.AreEqual(1, (int)manifest["minimum_reader_version"]);
            Assert.IsNull(FindInstanceOfClass(EntryJson(package, RbxWorldPackageSerializer.WorldEntryName), "Script"),
                "the synthetic legacy script is runtime infrastructure and is never saved");
        }

        [Test]
        public void Writer_DeclaredMod_WritesV2()
        {
            _world.Load("door", ServerHeader + "store_set('ok', 'yes')");
            RbxBaseScript script = _world.BoundScript("door");

            RbxWorldPackagePayload production = _world.Capture();
            byte[] package = RbxWorldPackageSerializer.WritePackage(production);

            Assert.AreEqual(2, production.FormatVersion);
            Assert.IsEmpty(production.Diagnostics, "the runtime keeps every binding whole, so capture repairs nothing");
            JObject manifest = EntryJson(package, RbxWorldPackageSerializer.ManifestEntryName);
            Assert.AreEqual(2, (int)manifest["format_version"]);
            Assert.AreEqual(2, (int)manifest["minimum_reader_version"]);
            JObject node = FindInstance(EntryJson(package, RbxWorldPackageSerializer.WorldEntryName), script.Id);
            Assert.IsNotNull(node, "the bound instance is saved with the world");
            Assert.AreEqual("{\"source_id\":\"door\",\"enabled\":true,\"run_context\":1}",
                node["script"].ToString(Newtonsoft.Json.Formatting.None));

            RbxWorldPackageRestoreResult restored =
                RbxWorldPackageSerializer.RestoreFresh(RbxWorldPackageSerializer.ReadPackage(package));
            try
            {
                Assert.IsFalse(restored.LegacyScriptPlacement);
                Assert.IsTrue(restored.Registry.TryGet(script.Id, out RbxInstance restoredScript));
                Assert.AreEqual("door", ((RbxBaseScript)restoredScript).SourceId);
            }
            finally
            {
                restored.Game.Destroy();
            }
        }

        [Test]
        public void Writer_ADeclaredManifestWithoutAnInstance_StillWritesV2()
        {
            _world.Load("door", ServerHeader + "store_set('ok', 'yes')");
            _world.Stack.Runtime.UnloadMod("door");
            _world.BoundScript("door").Destroy();

            RbxWorldPackagePayload production = _world.Capture();

            Assert.AreEqual(2, production.FormatVersion,
                "only version 2 can say that a declared source is detached; version 1 would start it at the legacy placement");
        }

        // ---- helpers ---------------------------------------------------------------------------

        private static JObject FindInstance(JObject worldJson, InstanceId id)
        {
            foreach (JToken node in (JArray)worldJson["instances"])
            {
                if ((string)node["id"] == id.Value.ToString(System.Globalization.CultureInfo.InvariantCulture))
                {
                    return (JObject)node;
                }
            }

            return null;
        }

        private static JObject FindInstanceOfClass(JObject worldJson, string className)
        {
            foreach (JToken node in (JArray)worldJson["instances"])
            {
                if ((string)node["class_name"] == className)
                {
                    return (JObject)node;
                }
            }

            return null;
        }

        private static JObject EntryJson(byte[] package, string name)
        {
            using MemoryStream input = new(package, false);
            using ZipArchive archive = new(input, ZipArchiveMode.Read, false);
            using Stream stream = archive.GetEntry(name).Open();
            using StreamReader reader = new(stream, Encoding.UTF8);
            using Newtonsoft.Json.JsonTextReader json = new(reader)
            {
                DateParseHandling = Newtonsoft.Json.DateParseHandling.None
            };
            return JObject.Load(json);
        }

        internal sealed class World : IDisposable
        {
            public World()
            {
                Registry = new InstanceRegistry(worldId: WorldId);
                Roblox = new LuaCsRbxApiBindings(registry: Registry);
                Store = new KeyValueStore();
                Sources = new MemorySourceStore();
                Stack = LuaCsModRuntimeFactory.Create(new LuaCsModStackOptions
                {
                    Logger = new SilentGameLogger(),
                    ModStore = Store,
                    ModSourceStore = Sources,
                    RbxApi = Roblox
                });
            }

            public InstanceRegistry Registry { get; }

            public LuaCsRbxApiBindings Roblox { get; }

            public KeyValueStore Store { get; }

            public MemorySourceStore Sources { get; }

            public LuaCsModStack Stack { get; }

            public void Load(string modId, string source)
            {
                Stack.Runtime.LoadMod(modId, source);
            }

            public RbxBaseScript BoundScript(string modId)
            {
                Assert.IsTrue(Roblox.ScriptInstances.TryGetBoundScript(modId, out RbxLuaSourceContainer script),
                    "mod '" + modId + "' has a bound instance");
                return (RbxBaseScript)script;
            }

            public RbxWorldPackagePayload Capture()
            {
                return RbxWorldPackageSerializer.Capture(Context());
            }

            public RbxWorldPackagePayload Capture(int formatVersion)
            {
                return RbxWorldPackageSerializer.Capture(Context(), formatVersion);
            }

            private RbxWorldPackageCaptureContext Context()
            {
                return new RbxWorldPackageCaptureContext(Registry, Roblox.Game, Roblox.PartSink,
                    new RbxWorldSettings
                    {
                        WorldId = WorldId,
                        MetersPerStud = 0.28f,
                        GravityStudsPerSecondSquared = 196.2d,
                        SignalBehavior = RbxWorldSettings.DeferredSignalBehavior
                    },
                    null, Sources, CapturedAtUtc);
            }

            public void Dispose()
            {
                Roblox.Dispose();
            }
        }

        internal sealed class KeyValueStore : ILuaModStore
        {
            private readonly Dictionary<(string ModId, string Key), string> _values = new();

            public string Get(string modId, string key)
            {
                return _values.TryGetValue((modId, key), out string value) ? value : "";
            }

            public void Set(string modId, string key, string value)
            {
                if (value == null)
                {
                    _values.Remove((modId, key));
                    return;
                }

                _values[(modId, key)] = value;
            }

            public void Clear(string modId)
            {
                List<(string ModId, string Key)> keys = new();
                foreach ((string ModId, string Key) key in _values.Keys)
                {
                    if (key.ModId == modId)
                    {
                        keys.Add(key);
                    }
                }

                foreach ((string ModId, string Key) key in keys)
                {
                    _values.Remove(key);
                }
            }
        }

        internal sealed class MemorySourceStore : ILuaModSourceStore
        {
            private readonly SortedDictionary<string, (string Source, LuaModManifest Manifest)> _entries =
                new(StringComparer.Ordinal);

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

                source = null;
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

        private sealed class SilentGameLogger : IGameLogger
        {
            public void LogDebug(GameLogFeature feature, string message, UnityEngine.Object context = null)
            {
            }

            public void LogInfo(GameLogFeature feature, string message, UnityEngine.Object context = null)
            {
            }

            public void LogWarning(GameLogFeature feature, string message, UnityEngine.Object context = null)
            {
            }

            public void LogError(GameLogFeature feature, string message, UnityEngine.Object context = null)
            {
            }
        }
    }
}
