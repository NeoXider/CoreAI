using System;
using System.Collections.Generic;
using System.Threading;
using CoreAI.Ai;
using CoreAI.Ai.LuaCs;
using CoreAI.Infrastructure.Lua;
using CoreAI.Mods.Rbx.Binding;
using CoreAI.Mods.Rbx.Instances;
using CoreAI.Mods.WorldPackages;
using Newtonsoft.Json;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode
{
    /// <summary>
    /// MVP4 step A2 (dev-docs/MVP4_SCRIPT_CONTEXTS_DESIGN.md, SC-5 and SC-6): the <c>@coreai</c> keys
    /// <c>context:</c> and <c>parent:</c>, the manifest's <c>Context</c> and the one rule every manifest writer
    /// derives it by. No runtime reads the context yet.
    /// </summary>
    [TestFixture]
    public sealed class Mvp4ScriptManifestEditModeTests
    {
        /// <summary>The manifest of a mod without a context, as every release up to 7.56.0 serialized it.</summary>
        private const string PreContextManifestJson =
            "{\"Id\":\"plain\",\"Name\":\"Plain\",\"Description\":\"\",\"Version\":\"1.0.0\",\"Category\":\"Tools\","
            + "\"Tags\":\"\",\"Origin\":\"\",\"SeededVersion\":\"\",\"SeededHash\":\"\",\"Author\":\"\","
            + "\"OwnerActorId\":\"\",\"Capabilities\":\"Read\",\"Active\":true,\"UpdateAvailable\":false,"
            + "\"Entry\":\"main.lua\"}";

        private SynchronizationContext _savedContext;

        /// <summary>The runtime cases run a mod runtime, whose sync-over-async guard must not post to a blocked thread.</summary>
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

        [Test]
        public void LuaModHeader_ParsesContextAndParentKeys()
        {
            LuaModHeader block = LuaModHeader.Parse(
                "--[[@coreai\nid: door\nContext: Client\nparent: Workspace/Castle/Gate\n]]\nprint(1)", "fallback");
            LuaModHeader lines = LuaModHeader.Parse(
                "-- @coreai id: door\n-- @coreai context: shared\n-- @coreai PARENT: @game/ReplicatedStorage/Lib\nprint(1)",
                "fallback");

            Assert.AreEqual("Client", block.Context, "the header keeps the text as written");
            Assert.AreEqual("Workspace/Castle/Gate", block.Parent);
            Assert.AreEqual("shared", lines.Context);
            Assert.AreEqual("@game/ReplicatedStorage/Lib", lines.Parent);
        }

        [Test]
        public void LuaModHeader_AbsentKeysAreEmpty()
        {
            LuaModHeader header = LuaModHeader.Parse("--[[@coreai\nid: door\nname: Door\n]]\nprint(1)", "fallback");
            LuaModHeader none = LuaModHeader.Parse("print(1)", "fallback");

            Assert.AreEqual("", header.Context);
            Assert.AreEqual("", header.Parent);
            Assert.AreEqual("", none.Context);
            Assert.AreEqual("", none.Parent);
        }

        /// <summary>A manifest without a context serializes exactly as every release before the field did.</summary>
        [Test]
        public void LuaModManifest_EmptyContext_IsOmittedAndByteIdentical()
        {
            LuaModManifest manifest = new()
            {
                Id = "plain", Name = "Plain", Version = "1.0.0", Category = "Tools", Capabilities = "Read", Active = true
            };

            Assert.AreEqual(PreContextManifestJson, JsonConvert.SerializeObject(manifest));
            Assert.AreEqual("", JsonConvert.DeserializeObject<LuaModManifest>(PreContextManifestJson).Context);
        }

        [Test]
        public void LuaModManifest_DeclaredContext_RoundTrips()
        {
            LuaModManifest manifest = new() { Id = "door", Context = "client" };

            string json = JsonConvert.SerializeObject(manifest);

            StringAssert.Contains("\"Context\":\"client\"", json);
            Assert.AreEqual("client", JsonConvert.DeserializeObject<LuaModManifest>(json).Context);
        }

        /// <summary>The header's valid context wins in canonical spelling; otherwise the stored one; otherwise legacy.</summary>
        [Test]
        public void ResolveContext_HeaderWins_ThenTheStoredContext_ThenLegacy()
        {
            LuaModManifest stored = new() { Id = "door", Context = "server" };

            Assert.AreEqual("client", LuaModManifest.ResolveContext(Header("CLIENT"), stored));
            Assert.AreEqual("server", LuaModManifest.ResolveContext(Header(null), stored));
            Assert.AreEqual("", LuaModManifest.ResolveContext(Header(null), null));
            Assert.AreEqual("shared", LuaModManifest.ResolveContext(Header(" shared "), null));
        }

        /// <summary>Twin: a value that is not a context declares nothing here; the stored context stays (the load refuses it, step C1).</summary>
        [Test]
        public void ResolveContext_ValueThatIsNotAContext_KeepsTheStoredOne()
        {
            LuaModManifest stored = new() { Id = "door", Context = "server" };

            Assert.AreEqual("server", LuaModManifest.ResolveContext(Header("clinet"), stored));
            Assert.AreEqual("", LuaModManifest.ResolveContext(Header("plugin"), null));
        }

        /// <summary>The runtime, the bundled seeder and the world package all carry the declared context.</summary>
        [Test]
        public void ManifestWriters_RuntimeSeederAndPackageCarryContext()
        {
            MemoryStore runtimeStore = new();
            LuaCsModRuntime runtime = new(sourceStore: runtimeStore);
            runtime.LoadMod("door", Source("door", "client"), LuaCapabilities.Read);
            Assert.AreEqual("client", runtimeStore.ManifestOf("door").Context, "LuaCsModRuntime.BuildManifest");

            MemoryStore seededStore = new();
            new BundledModSeeder(seededStore, new IBundledModSource[] { new OneMod("lib", Source("lib", "shared", "1.0.0")) })
                .Seed();
            Assert.AreEqual("shared", seededStore.ManifestOf("lib").Context, "BundledModSeeder.BuildManifest");

            MemoryStore packagedStore = new();
            packagedStore.Save("door", Source("door", "server"), new LuaModManifest { Id = "door", Name = "door", Context = "server" });
            RbxWorldPackagePayload read = RbxWorldPackageSerializer.ReadPackage(RbxWorldPackageSerializer.WritePackage(Capture(packagedStore)));
            Assert.AreEqual("server", read.Mods[0].Manifest.Context, "RbxWorldPackageSerializer.CloneManifest");
        }

        /// <summary>Twin: a header without a context keeps the one the store holds, in the runtime and the seeder.</summary>
        [Test]
        public void ManifestWriters_HeaderWithoutContext_KeepsTheStoredOne()
        {
            MemoryStore runtimeStore = new();
            LuaCsModRuntime runtime = new(sourceStore: runtimeStore);
            runtime.LoadMod("door", Source("door", "client"), LuaCapabilities.Read);
            LuaCsModRuntime restarted = new(sourceStore: runtimeStore);
            restarted.LoadMod("door", Source("door", null), LuaCapabilities.Read);
            Assert.AreEqual("client", runtimeStore.ManifestOf("door").Context);

            MemoryStore seededStore = new();
            new BundledModSeeder(seededStore, new IBundledModSource[] { new OneMod("lib", Source("lib", "shared", "1.0.0")) })
                .Seed();
            new BundledModSeeder(seededStore, new IBundledModSource[] { new OneMod("lib", Source("lib", null, "1.1.0")) })
                .Seed();
            Assert.AreEqual("1.1.0", seededStore.ManifestOf("lib").SeededVersion, "precondition: the update ran");
            Assert.AreEqual("shared", seededStore.ManifestOf("lib").Context);
        }

        private static LuaModHeader Header(string context)
        {
            return LuaModHeader.Parse(
                "--[[@coreai\nid: door\n" + (context == null ? "" : "context: " + context + "\n") + "]]\nprint(1)", "door");
        }

        private static string Source(string id, string context, string version = "1.0.0")
        {
            return "--[[@coreai\nid: " + id + "\nname: " + id + "\nversion: " + version + "\ncapabilities: Read\n"
                   + (context == null ? "" : "context: " + context + "\n") + "]]\nlocal n = 1\n";
        }

        private static RbxWorldPackagePayload Capture(ILuaModSourceStore store)
        {
            InstanceRegistry registry = new(worldId: "mvp4-manifest");
            RbxDataModel game = DataModelBootstrap.CreateGame(registry);
            return RbxWorldPackageSerializer.Capture(new RbxWorldPackageCaptureContext(
                registry, game, new InMemoryPartPropertySink(registry),
                new RbxWorldSettings { WorldId = "mvp4-manifest" }, modSourceStore: store,
                capturedAtUtc: new DateTime(2026, 11, 11, 16, 0, 0, DateTimeKind.Utc)));
        }

        private sealed class OneMod : IBundledModSource
        {
            private readonly BundledMod[] _mods;

            public OneMod(string id, string source)
            {
                _mods = new[] { new BundledMod(id, source, LuaModHeader.Parse(source, id).Version) };
            }

            public string Origin => "resources";

            public IReadOnlyList<BundledMod> Load()
            {
                return _mods;
            }
        }

        private sealed class MemoryStore : ILuaModSourceStore
        {
            private readonly Dictionary<string, (string Source, LuaModManifest Manifest)> _entries = new(StringComparer.Ordinal);

            public LuaModManifest ManifestOf(string id)
            {
                return _entries[id].Manifest;
            }

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
