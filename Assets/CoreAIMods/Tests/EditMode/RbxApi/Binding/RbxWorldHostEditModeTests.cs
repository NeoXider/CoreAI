using CoreAI.Mods.Rbx.Binding;
using CoreAI.Mods.Rbx.Spatial;
using CoreAI.Mods.Rbx.Instances;
using NUnit.Framework;
using UnityEngine;

namespace CoreAI.Tests.EditMode.RbxApi.Binding
{
    /// <summary>Scene entry point wiring: one host = one registry + binder + game tree,
    /// no statics (ARCHITECTURE_RULES.md §2).</summary>
    [TestFixture]
    public sealed class RbxWorldHostEditModeTests
    {
        private GameObject _hostGo;
        private RbxWorldHost _host;

        [SetUp]
        public void SetUp()
        {
            RbxSpace.ResetForTests();
            _hostGo = new GameObject("RbxWorldHost");
            _host = _hostGo.AddComponent<RbxWorldHost>();
            _host.Initialize();
        }

        [TearDown]
        public void TearDown()
        {
            if (_hostGo != null)
            {
                Object.DestroyImmediate(_hostGo);
            }

            RbxSpace.ResetForTests();
        }

        [Test]
        public void Initialize_BuildsTheGameTreeUnderTheHostTransform()
        {
            Assert.IsTrue(_host.IsInitialized);
            Assert.IsNotNull(_host.Registry);
            Assert.IsNotNull(_host.Game);
            Assert.IsNotNull(_host.Registry.WorldRoot, "workspace must be the world root (D5)");

            Assert.IsTrue(_host.Binder.TryGetBoundObject(
                _host.Registry.WorldRoot.Id, out GameObject workspaceGo));
            Assert.AreEqual(_hostGo.transform, workspaceGo.transform.parent);
        }

        [Test]
        public void Bootstrap_MirrorsExplorerUnderHost_StorageServicesInactive()
        {
            // WHY: the host GameObject represents game (DataModel); every service nests under it,
            // only Workspace active (Roblox: only Workspace content is physical), so Lighting and the
            // storage services stay inactive and their content neither renders nor collides.
            Assert.IsTrue(_host.Binder.TryGetBoundObject(_host.Game.Id, out GameObject gameGo));
            Assert.AreSame(_hostGo, gameGo, "the DataModel binds to the host GameObject itself");

            AssertServiceActive("Workspace", true);
            AssertServiceActive("Lighting", false);
            AssertServiceActive("ReplicatedStorage", false);
            AssertServiceActive("ServerStorage", false);
            AssertServiceActive("ServerScriptService", false);
            AssertServiceActive("StarterPlayer", false);
        }

        private void AssertServiceActive(string serviceName, bool expectedActiveSelf)
        {
            RbxInstance service = _host.Game.GetService(serviceName);
            Assert.IsTrue(_host.Binder.TryGetBoundObject(service.Id, out GameObject serviceGo),
                serviceName + " must materialize");
            Assert.AreEqual(_hostGo.transform, serviceGo.transform.parent,
                serviceName + " nests under the host (game)");
            Assert.AreEqual(expectedActiveSelf, serviceGo.activeSelf,
                serviceName + " active state must mirror Roblox physical-world membership");
        }

        [Test]
        public void Initialize_IsIdempotent()
        {
            InstanceRegistry registry = _host.Registry;
            _host.Initialize();
            Assert.AreSame(registry, _host.Registry);
        }

        [Test]
        public void PartsCreatedThroughTheHostRegistry_Materialize()
        {
            RbxInstance part = _host.Registry.Create("Part");
            part.Parent = _host.Registry.WorldRoot;

            Assert.IsTrue(_host.Binder.TryGetBoundObject(part.Id, out GameObject partGo));
            Assert.IsNotNull(partGo.GetComponent<MeshRenderer>());
        }

        [Test]
        public void DestroyingTheHost_TearsDownTheWorld()
        {
            RbxInstance part = _host.Registry.Create("Part");
            part.Parent = _host.Registry.WorldRoot;
            RbxInstance camera = _host.Registry.WorldRoot.FindFirstChildOfClass("Camera");
            Assert.IsNotNull(camera, "the canonical Camera must exist before teardown");
            Assert.IsTrue(_host.Binder.TryGetBoundObject(camera.Id, out GameObject cameraGo),
                "the canonical Camera must already be materialized before teardown");

            Object.DestroyImmediate(_hostGo);
            _hostGo = null;

            Assert.IsTrue(part.IsDestroyed, "host teardown must destroy world instances");
            Assert.IsTrue(camera.IsDestroyed, "host teardown must destroy the canonical Camera instance");
            Assert.IsTrue(cameraGo == null,
                "Unity must destroy the existing Camera backing object with the host");
        }
    }

    /// <summary>
    /// A scene object the host never bound is wrapped on its first world-name lookup: every registry
    /// key resolves to that one record, and a meter-authored position reads back in studs.
    /// </summary>
    /// <remarks>
    /// WHY its own fixture: each test builds its own host next to its own scene object, so the host
    /// the fixture above creates in SetUp would only put a second world in the scene.
    /// </remarks>
    [TestFixture]
    public sealed class RbxWorldHostLazyWorldWrapEditModeTests
    {
        private const float Epsilon = 1e-4f;

        [TearDown]
        public void RestoreDefaultScale()
        {
            RbxSpace.ResetForTests();
        }

        [Test]
        public void Lookup_ByWorldName_LazilyWrapsHostObject_AndEveryKeyResolvesTheSameRecord()
        {
            RbxSpace.ResetForTests(0.28f);
            GameObject hostObject = new("LazyWorldIdentityHost");
            GameObject worldObject = new("LazyWorldSpawnPad");
            worldObject.transform.position = new Vector3(0f, 1.8f, 0f);
            worldObject.SetActive(false);
            RbxWorldHost host = hostObject.AddComponent<RbxWorldHost>();
            try
            {
                host.Initialize();
                int countBeforeLookup = host.Registry.Count;

                Assert.IsTrue(host.Registry.TryGetByWorldName(worldObject.name, out RbxInstance byName),
                    "a scene object must be wrapped on its first world-name lookup without pre-binding");
                Assert.AreEqual(countBeforeLookup + 1, host.Registry.Count,
                    "the first lookup creates exactly one host-owned registry record");
                Assert.AreEqual("Part", byName.ClassName);
                Assert.AreSame(host.Registry.WorldRoot, byName.Parent);
                Assert.IsTrue(host.Registry.TryGetRecord(byName.Id, out InstanceRecord record));
                Assert.AreEqual(worldObject.name, record.WorldName);
                Assert.IsNull(record.OwnerModId);
                Assert.IsTrue(host.Binder.TryGetBoundObject(byName.Id, out GameObject backingObject));
                Assert.AreSame(worldObject, backingObject, "the wrapper must adopt, not duplicate, the host object");
                Assert.IsFalse(backingObject.activeSelf, "adoption must preserve host-owned activation state");

                PartProperties properties = host.Binder.GetPartPropertiesOrDefault(byName.Id);
                Assert.AreEqual(1.8f / 0.28f, properties.Position.Y, 1e-3f);

                Assert.IsTrue(host.Registry.TryGet(byName.Id, out RbxInstance byId));
                Assert.AreSame(byName, byId);
                host.Registry.BindNetId(byName.Id, 42u);
                Assert.IsTrue(host.Registry.TryGetByNetId(42u, out RbxInstance byNet));
                Assert.AreSame(byName, byNet);

                Assert.IsTrue(host.Registry.TryGetByWorldName(worldObject.name, out RbxInstance secondLookup));
                Assert.AreSame(byName, secondLookup);
                Assert.AreEqual(countBeforeLookup + 1, host.Registry.Count,
                    "later lookups must reuse the same lazy record");
            }
            finally
            {
                Object.DestroyImmediate(hostObject);
                Object.DestroyImmediate(worldObject);
                RbxSpace.ResetForTests();
            }
        }

        [Test]
        public void D3_MeterAuthoredHostObjectReadsAsStuds()
        {
            RbxSpace.ResetForTests(0.28f);
            GameObject hostObject = new("RbxSpaceGoldenWorldHost");
            GameObject worldObject = new("RbxSpaceGoldenMeterObject");
            worldObject.transform.position = new Vector3(0f, 1.8f, 0f);
            RbxWorldHost host = hostObject.AddComponent<RbxWorldHost>();
            try
            {
                host.Initialize();

                Assert.IsTrue(host.Registry.TryGetByWorldName(worldObject.name, out RbxInstance wrapped),
                    "the golden must traverse the real lazy host-world wrapper path");
                PartProperties properties = host.Binder.GetPartPropertiesOrDefault(wrapped.Id);
                Assert.AreEqual(1.8f / 0.28f, properties.Position.Y, 1e-3f,
                    "a meter-authored object at y=1.8 m reads about 6.43 studs");
                Assert.AreEqual(0f, properties.Position.X, Epsilon);
                Assert.AreEqual(0f, properties.Position.Z, Epsilon);
            }
            finally
            {
                Object.DestroyImmediate(hostObject);
                Object.DestroyImmediate(worldObject);
            }
        }
    }
}
