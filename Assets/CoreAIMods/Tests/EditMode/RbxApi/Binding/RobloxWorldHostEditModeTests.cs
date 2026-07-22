using CoreAI.Mods.Rbx.Binding;
using CoreAI.Mods.Rbx.Spatial;
using CoreAI.Mods.Rbx.Instances;
using NUnit.Framework;
using UnityEngine;

namespace CoreAI.Tests.EditMode.RobloxApi.Binding
{
    /// <summary>Scene entry point wiring: one host = one registry + binder + game tree,
    /// no statics (ARCHITECTURE_RULES.md §2).</summary>
    [TestFixture]
    public sealed class RobloxWorldHostEditModeTests
    {
        private GameObject _hostGo;
        private RobloxWorldHost _host;

        [SetUp]
        public void SetUp()
        {
            RobloxSpace.ResetForTests();
            _hostGo = new GameObject("RobloxWorldHost");
            _host = _hostGo.AddComponent<RobloxWorldHost>();
            _host.Initialize();
        }

        [TearDown]
        public void TearDown()
        {
            if (_hostGo != null)
            {
                Object.DestroyImmediate(_hostGo);
            }

            RobloxSpace.ResetForTests();
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
            // Workspace/Lighting active, storage services inactive so their content is not physical.
            Assert.IsTrue(_host.Binder.TryGetBoundObject(_host.Game.Id, out GameObject gameGo));
            Assert.AreSame(_hostGo, gameGo, "the DataModel binds to the host GameObject itself");

            AssertServiceActive("Workspace", true);
            AssertServiceActive("Lighting", true);
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

            Object.DestroyImmediate(_hostGo);
            _hostGo = null;

            Assert.IsTrue(part.IsDestroyed, "host teardown must destroy world instances");
        }
    }
}
