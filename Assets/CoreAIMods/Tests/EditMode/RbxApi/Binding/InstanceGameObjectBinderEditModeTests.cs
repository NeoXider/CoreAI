using System.Collections.Generic;
using CoreAI.Mods.Rbx.Binding;
using CoreAI.Mods.Rbx.Datatypes;
using CoreAI.Mods.Rbx.Spatial;
using CoreAI.Mods.Rbx.Instances;
using NUnit.Framework;
using UnityEngine;

namespace CoreAI.Tests.EditMode.RbxApi.Binding
{
    /// <summary>
    /// Unity materialization per D5/D3 (§5.1.8 items 2 and 11), mirroring the
    /// BackingBinderSeamEditModeTests contract with real GameObjects: hierarchy mirroring,
    /// deactivate-not-destroy, destroy cleanup, name sync, and the golden RbxSpace
    /// conversions at the locked 0.28 scale.
    /// </summary>
    [TestFixture]
    public sealed class InstanceGameObjectBinderEditModeTests
    {
        private const float Epsilon = 1e-4f;

        private GameObject _root;
        private InstanceGameObjectBinder _binder;
        private InstanceRegistry _registry;
        private RbxDataModel _game;

        [SetUp]
        public void SetUp()
        {
            RbxSpace.ResetForTests(0.28f);
            _root = new GameObject("BinderTestRoot");
            _binder = new InstanceGameObjectBinder(_root.transform);
            _registry = new InstanceRegistry(null, _binder);
            _game = DataModelBootstrap.CreateGame(_registry);
        }

        [TearDown]
        public void TearDown()
        {
            _game.Destroy();
            Object.DestroyImmediate(_root);
            RbxSpace.ResetForTests();
        }

        private RbxInstance CreatePartInWorld()
        {
            RbxInstance part = _registry.Create("Part");
            part.Parent = _registry.WorldRoot;
            return part;
        }

        private GameObject BoundObject(RbxInstance instance)
        {
            Assert.IsTrue(_binder.TryGetBoundObject(instance.Id, out GameObject gameObject),
                instance.Name + " should have a backing GameObject");
            return gameObject;
        }

        /// <summary>Dot-joined transform names from just below the host (game) GameObject down to
        /// <paramref name="leaf"/>, so it can be compared against RbxInstance.GetFullName.</summary>
        private string TransformPathBelowHost(Transform leaf)
        {
            List<string> names = new();
            for (Transform current = leaf;
                 current != null && current != _root.transform;
                 current = current.parent)
            {
                names.Add(current.name);
            }

            names.Reverse();
            return string.Join(".", names);
        }

        // ---- Materialization / hierarchy ----------------------------------------------------

        [Test]
        public void D5_FreshPart_HasNoGameObject()
        {
            RbxInstance part = _registry.Create("Part");
            Assert.IsFalse(_binder.TryGetBoundObject(part.Id, out _));
        }

        [Test]
        public void MaterializedTree_TransformHierarchyMirrorsTheRegistry()
        {
            RbxInstance model = _registry.Create("Model");
            RbxInstance part = _registry.Create("Part");
            part.Parent = model;
            model.Parent = _registry.WorldRoot;

            GameObject workspaceGo = BoundObject(_registry.WorldRoot);
            GameObject modelGo = BoundObject(model);
            GameObject partGo = BoundObject(part);

            Assert.AreEqual(_root.transform, workspaceGo.transform.parent);
            Assert.AreEqual(workspaceGo.transform, modelGo.transform.parent);
            Assert.AreEqual(modelGo.transform, partGo.transform.parent);
        }

        [Test]
        public void FolderAndModel_MaterializeAsEmptyTransforms()
        {
            RbxInstance folder = _registry.Create("Folder");
            folder.Parent = _registry.WorldRoot;

            GameObject folderGo = BoundObject(folder);
            Assert.IsTrue(folderGo.activeInHierarchy, "a Folder under Workspace is an active empty GO");
            Assert.IsNull(folderGo.GetComponent<Renderer>());
            Assert.IsNull(folderGo.GetComponent<Collider>());
        }

        [Test]
        public void ModelWithTwoParts_NestUnderModel_AndFullNameMatchesTransformPath()
        {
            RbxInstance model = _registry.Create("Model");
            model.Name = "Rig";
            model.Parent = _registry.WorldRoot;
            RbxInstance head = _registry.Create("Part");
            head.Name = "Head";
            head.Parent = model;
            RbxInstance torso = _registry.Create("Part");
            torso.Name = "Torso";
            torso.Parent = model;

            GameObject modelGo = BoundObject(model);
            Assert.AreEqual(BoundObject(_registry.WorldRoot).transform, modelGo.transform.parent);
            Assert.AreEqual(modelGo.transform, BoundObject(head).transform.parent);
            Assert.AreEqual(modelGo.transform, BoundObject(torso).transform.parent);

            // WHY: the Unity hierarchy mirrors the explorer, so GetFullName path segments equal the
            // transform path segments below the host (game) GameObject.
            Assert.AreEqual("Workspace.Rig.Head", head.GetFullName());
            Assert.AreEqual(head.GetFullName(), TransformPathBelowHost(BoundObject(head).transform));
        }

        [Test]
        public void ContainerRename_SyncsThroughTheSeam()
        {
            RbxInstance folder = _registry.Create("Folder");
            folder.Parent = _registry.WorldRoot;
            GameObject folderGo = BoundObject(folder);

            folder.Name = "Loot";
            Assert.AreEqual("Loot", folderGo.name, "containers rename through the same seam as parts");
        }

        [Test]
        public void ReparentPartWorkspaceToReplicatedStorage_MovesUnderInactiveParent_AndBack()
        {
            RbxInstance part = CreatePartInWorld();
            GameObject partGo = BoundObject(part);
            Assert.IsTrue(partGo.activeInHierarchy);

            RbxInstance storage = _game.GetService("ReplicatedStorage");
            GameObject storageGo = BoundObject(storage);
            Assert.IsFalse(storageGo.activeSelf, "storage services materialize inactive");

            part.Parent = storage;
            Assert.AreEqual(storageGo.transform, partGo.transform.parent, "the GO moves under the service");
            Assert.IsFalse(partGo.activeInHierarchy,
                "under an inactive service the Part leaves the physical world automatically");
            Assert.IsTrue(partGo.activeSelf,
                "only the parent is inactive — the Part's own active flag is untouched");

            part.Parent = _registry.WorldRoot;
            Assert.AreEqual(BoundObject(_registry.WorldRoot).transform, partGo.transform.parent);
            Assert.IsTrue(partGo.activeInHierarchy, "back in Workspace the Part is physical again");
        }

        [Test]
        public void DestroyModel_RemovesItsSubtree_HostSurvives()
        {
            RbxInstance model = _registry.Create("Model");
            model.Parent = _registry.WorldRoot;
            RbxInstance part = _registry.Create("Part");
            part.Parent = model;
            GameObject modelGo = BoundObject(model);
            GameObject partGo = BoundObject(part);

            model.Destroy();

            Assert.IsFalse(_binder.TryGetBoundObject(model.Id, out _));
            Assert.IsFalse(_binder.TryGetBoundObject(part.Id, out _));
            Assert.IsTrue(modelGo == null && partGo == null, "the Model subtree GameObjects are destroyed");
            Assert.IsTrue(_root != null, "the host (game) GameObject survives child teardown");
        }

        [Test]
        public void D5_Detach_DeactivatesAndKeepsTheSameGameObject()
        {
            RbxInstance part = CreatePartInWorld();
            GameObject partGo = BoundObject(part);

            part.Parent = null;
            Assert.IsFalse(partGo.activeSelf);
            Assert.IsTrue(partGo != null, "detach must deactivate, not destroy (D5)");

            part.Parent = _registry.WorldRoot;
            Assert.IsTrue(partGo.activeSelf);
            Assert.AreSame(partGo, BoundObject(part), "re-entry must reuse the parked object");
        }

        [Test]
        public void ReparentWithinWorld_MovesTheTransform()
        {
            RbxInstance model = _registry.Create("Model");
            model.Parent = _registry.WorldRoot;
            RbxInstance part = CreatePartInWorld();

            part.Parent = model;

            Assert.AreEqual(BoundObject(model).transform, BoundObject(part).transform.parent);
        }

        [Test]
        public void D6_Destroy_DestroysTheGameObject()
        {
            RbxInstance part = CreatePartInWorld();
            GameObject partGo = BoundObject(part);

            part.Destroy();

            Assert.IsFalse(_binder.TryGetBoundObject(part.Id, out _));
            Assert.IsTrue(partGo == null, "backing GameObject must be destroyed with the instance");
        }

        [Test]
        public void NameSync_AtMaterializationAndOnRename()
        {
            RbxInstance part = _registry.Create("Part");
            part.Name = "SpawnPad";
            part.Parent = _registry.WorldRoot;
            GameObject partGo = BoundObject(part);
            Assert.AreEqual("SpawnPad", partGo.name);

            part.Name = "LavaFloor";
            Assert.AreEqual("LavaFloor", partGo.name);
        }

        // ---- Golden conversions at 0.28 (D3, §5.1.8 item 11) --------------------------------

        [Test]
        public void PositionGolden_At028_ScalesAndMirrorsZ()
        {
            RbxInstance part = CreatePartInWorld();
            _binder.SetCFrame(part.Id, RbxCFrame.FromPosition(10f, 5f, -4f));

            Vector3 position = BoundObject(part).transform.position;
            Assert.AreEqual(2.8f, position.x, Epsilon);
            Assert.AreEqual(1.4f, position.y, Epsilon);
            Assert.AreEqual(1.12f, position.z, Epsilon, "mod-space z = -Unity z (D2)");
        }

        [Test]
        public void SizeGolden_StudCube4x1x2_Becomes_1p12_0p28_0p56Meters()
        {
            RbxInstance part = CreatePartInWorld();
            _binder.SetSize(part.Id, new RbxVector3(4f, 1f, 2f));

            Vector3 scale = BoundObject(part).transform.localScale;
            Assert.AreEqual(1.12f, scale.x, Epsilon);
            Assert.AreEqual(0.28f, scale.y, Epsilon);
            Assert.AreEqual(0.56f, scale.z, Epsilon);
        }

        [Test]
        public void RotationGolden_Yaw90_LookVectorMapsThroughRobloxSpace()
        {
            RbxInstance part = CreatePartInWorld();
            RbxCFrame cframe = RbxCFrame.Angles(0f, Mathf.PI / 2f, 0f);
            _binder.SetCFrame(part.Id, cframe);

            Vector3 forward = BoundObject(part).transform.forward;
            Vector3 expected = RbxSpace.DirectionToUnity(cframe.LookVector);
            Assert.AreEqual(expected.x, forward.x, Epsilon);
            Assert.AreEqual(expected.y, forward.y, Epsilon);
            Assert.AreEqual(expected.z, forward.z, Epsilon);
            Assert.AreEqual(-1f, forward.x, Epsilon, "Roblox yaw +90 looks down -X");
        }

        [Test]
        public void AssetRule_At1To1_OnlyTheConstantChanges()
        {
            // WHY: §5.1.8 item 11 — switching 0.28 <-> 1:1 touches zero assets; the same
            // binder code path must yield stud-numeric localScale at 1:1.
            RbxSpace.ResetForTests(1f);
            GameObject root = new("OneToOneRoot");
            try
            {
                InstanceGameObjectBinder binder = new(root.transform);
                InstanceRegistry registry = new(null, binder);
                RbxDataModel game = DataModelBootstrap.CreateGame(registry);
                RbxInstance part = registry.Create("Part");
                part.Parent = registry.WorldRoot;
                binder.SetSize(part.Id, new RbxVector3(4f, 1f, 2f));

                Assert.IsTrue(binder.TryGetBoundObject(part.Id, out GameObject partGo));
                Vector3 scale = partGo.transform.localScale;
                Assert.AreEqual(4f, scale.x, Epsilon);
                Assert.AreEqual(1f, scale.y, Epsilon);
                Assert.AreEqual(2f, scale.z, Epsilon);
                game.Destroy();
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
        }

        // ---- Part property flow -------------------------------------------------------------

        [Test]
        public void Color_PushesIntoTheMaterialPropertyBlock()
        {
            RbxInstance part = CreatePartInWorld();
            _binder.SetColor(part.Id, RbxColor3.FromRGB(255f, 128f, 0f));

            Renderer renderer = BoundObject(part).GetComponent<Renderer>();
            MaterialPropertyBlock block = new();
            renderer.GetPropertyBlock(block);
            Color color = block.GetColor("_Color");
            Assert.AreEqual(1f, color.r, Epsilon);
            Assert.AreEqual(128f / 255f, color.g, Epsilon);
            Assert.AreEqual(0f, color.b, Epsilon);
            Assert.AreEqual(1f, color.a, Epsilon);
        }

        [Test]
        public void Transparency_SetsAlphaAndHidesAtOne()
        {
            RbxInstance part = CreatePartInWorld();
            Renderer renderer = BoundObject(part).GetComponent<Renderer>();
            MaterialPropertyBlock block = new();

            _binder.SetTransparency(part.Id, 0.25f);
            renderer.GetPropertyBlock(block);
            Assert.AreEqual(0.75f, block.GetColor("_Color").a, Epsilon);
            Assert.IsTrue(renderer.enabled);

            _binder.SetTransparency(part.Id, 1f);
            Assert.IsFalse(renderer.enabled, "Transparency 1 = invisible (Roblox parity)");
        }

        [Test]
        public void Anchored_TogglesTheRigidbody()
        {
            RbxInstance part = CreatePartInWorld();
            GameObject partGo = BoundObject(part);

            Rigidbody body = partGo.GetComponent<Rigidbody>();
            Assert.IsNotNull(body, "default Anchored=false needs a Rigidbody");
            Assert.IsFalse(body.useGravity, "DEV-6: per-body gravity only, never Unity global");

            _binder.SetAnchored(part.Id, true);
            Assert.IsNull(partGo.GetComponent<Rigidbody>());

            _binder.SetAnchored(part.Id, false);
            Assert.IsNotNull(partGo.GetComponent<Rigidbody>());
        }

        [Test]
        public void CanCollide_TogglesTheCollider()
        {
            RbxInstance part = CreatePartInWorld();
            Collider collider = BoundObject(part).GetComponent<Collider>();
            Assert.IsTrue(collider.enabled);

            _binder.SetCanCollide(part.Id, false);
            Assert.IsFalse(collider.enabled);

            _binder.SetCanCollide(part.Id, true);
            Assert.IsTrue(collider.enabled);
        }

        [Test]
        public void Position_KeepsOrientation_RobloxPartSemantics()
        {
            RbxInstance part = CreatePartInWorld();
            RbxCFrame rotated = RbxCFrame.Angles(0f, Mathf.PI / 2f, 0f);
            _binder.SetCFrame(part.Id, rotated);

            _binder.SetPosition(part.Id, new RbxVector3(1f, 2f, 3f));

            PartProperties properties = _binder.GetPartPropertiesOrDefault(part.Id);
            Assert.AreEqual(1f, properties.CFrame.Position.X, Epsilon);
            Assert.AreEqual(2f, properties.CFrame.Position.Y, Epsilon);
            Assert.AreEqual(3f, properties.CFrame.Position.Z, Epsilon);
            Assert.AreEqual(-1f, properties.CFrame.LookVector.X, Epsilon,
                "setting Position must not touch the orientation");
        }

        [Test]
        public void PropertiesPushedBeforeMaterialization_ApplyWhenTheGameObjectAppears()
        {
            RbxInstance part = _registry.Create("Part");
            _binder.SetCFrame(part.Id, RbxCFrame.FromPosition(10f, 0f, 0f));
            _binder.SetSize(part.Id, new RbxVector3(2f, 2f, 2f));

            part.Parent = _registry.WorldRoot;

            GameObject partGo = BoundObject(part);
            Assert.AreEqual(2.8f, partGo.transform.position.x, Epsilon);
            Assert.AreEqual(0.56f, partGo.transform.localScale.x, Epsilon);
        }

        [Test]
        public void PartDefaults_MatchRobloxPart()
        {
            PartProperties defaults = PartProperties.CreateDefault();
            Assert.AreEqual(4f, defaults.Size.X);
            Assert.AreEqual(1f, defaults.Size.Y);
            Assert.AreEqual(2f, defaults.Size.Z);
            Assert.IsFalse(defaults.Anchored);
            Assert.IsTrue(defaults.CanCollide);
            Assert.AreEqual(0f, defaults.Transparency);
        }
    }
}
