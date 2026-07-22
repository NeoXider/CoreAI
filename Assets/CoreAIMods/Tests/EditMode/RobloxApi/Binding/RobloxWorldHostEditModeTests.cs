using CoreAI.Mods.Roblox.Binding;
using CoreAI.Mods.Roblox.Spatial;
using CoreAI.RobloxApi.Instances;
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
