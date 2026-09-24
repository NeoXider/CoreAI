using System.Collections.Generic;
using System.Reflection;
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
    /// conversions at the locked 0.28 scale. Also the binder's physics bookkeeping: only the
    /// Workspace subtree is active, CanCollide=false keeps a trigger collider that still reports
    /// contacts and answers rays, colliders resolve to parts through the parent chain in constant
    /// time per level, and a GameObject destroyed behind the binder's back counts as unbound.
    /// Nested parts materialize in their parent's unscaled child container (absolute Size, no
    /// dragging, no compound collider); a simulated part reads back its body's pose and partial
    /// writes keep it; destroyed parts keep a bounded last-known state in both sinks; and the sink
    /// boundary clamps Size and refuses non-finite spatial writes.
    /// </summary>
    [TestFixture]
    public sealed class InstanceGameObjectBinderEditModeTests
    {
        private const float Epsilon = 1e-4f;

        /// <summary>Where the ray tests put their target, away from anything an open editor scene
        /// is likely to hold at the origin.</summary>
        private static readonly RbxVector3 RayTargetPosition = new(500f, 0f, 0f);

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

        private RbxInstance CreateAnchoredPartAt(RbxVector3 position)
        {
            RbxInstance part = CreatePartInWorld();
            _binder.SetAnchored(part.Id, true);
            _binder.SetPosition(part.Id, position);
            return part;
        }

        /// <summary>Casts straight down through <paramref name="target"/>'s stored position and
        /// reports whether the nearest accepted hit is that part.</summary>
        private bool RayHits(UnityRbxPhysicsPort port, RbxInstance target, bool respectCanCollide)
        {
            // WHY: EditMode never steps physics, so transform writes reach the physics scene only
            // through an explicit sync.
            Physics.SyncTransforms();
            RbxVector3 above = _binder.GetPartPropertiesOrDefault(target.Id).Position
                               + new RbxVector3(0f, 20f, 0f);
            return port.TryRaycast(above, new RbxVector3(0f, -40f, 0f), respectCanCollide, null,
                       out RbxPhysicsRaycastHit hit)
                   && hit.Instance.Value == target.Id.Value;
        }

        private GameObject ShapeChildOf(RbxInstance part)
        {
            Transform child = BoundObject(part).transform.Find("Shape");
            Assert.IsNotNull(child, part.Name + " must have a Cylinder Shape child");
            return child.gameObject;
        }

        private List<string> RecordContacts()
        {
            List<string> contacts = new();
            _binder.ContactObserved += (first, second, began) =>
                contacts.Add(first.Value + "-" + second.Value + ":" + (began ? "began" : "ended"));
            return contacts;
        }

        private static string Contact(RbxInstance self, RbxInstance other, bool began)
        {
            return self.Id.Value + "-" + other.Id.Value + ":" + (began ? "began" : "ended");
        }

        /// <summary>Delivers a Unity trigger message to a relay the way the engine does, so the
        /// binder's bookkeeping can be checked without stepping physics.</summary>
        private static void DeliverTriggerMessage(RbxContactRelay relay, string message, Collider other)
        {
            Assert.IsNotNull(relay, "the part must carry a contact relay");
            MethodInfo handler = typeof(RbxContactRelay).GetMethod(message,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            Assert.IsNotNull(handler,
                "RbxContactRelay must handle Unity's " + message + " message: a CanCollide=false part " +
                "is a trigger, and Roblox still fires Touched for it");
            handler.Invoke(relay, new object[] { other });
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
        public void BeginHostTeardown_BlocksLateMaterializationAndHierarchyParking()
        {
            RbxInstance boundPart = CreatePartInWorld();
            GameObject boundPartGo = BoundObject(boundPart);
            Transform originalParent = boundPartGo.transform.parent;
            RbxInstance latePart = _registry.Create("Part");

            _binder.BeginHostTeardown();
            boundPart.Parent = null;
            latePart.Parent = _registry.WorldRoot;

            Assert.AreEqual(originalParent, boundPartGo.transform.parent,
                "host teardown must not re-parent existing backing objects");
            Assert.IsTrue(boundPartGo.activeSelf,
                "host teardown must not park existing objects while their host is being destroyed");
            Assert.IsFalse(_binder.TryGetBoundObject(latePart.Id, out _),
                "instances entering the world during host teardown must not materialize");

            boundPart.Destroy();
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
            float[] metersPerStudValues = { 0.28f, 1f };
            RbxPartShape[] shapes =
            {
                RbxPartShape.Block,
                RbxPartShape.Wedge,
                RbxPartShape.CornerWedge
            };
            string[] expectedMeshNames = { "Cube", "CoreAiWedge", "CoreAiCornerWedge" };

            for (int scaleIndex = 0; scaleIndex < metersPerStudValues.Length; scaleIndex++)
            {
                float metersPerStud = metersPerStudValues[scaleIndex];
                RbxSpace.ResetForTests(metersPerStud);
                GameObject root = new("AssetRuleRoot" + scaleIndex);
                RbxDataModel game = null;
                try
                {
                    InstanceGameObjectBinder binder = new(root.transform);
                    InstanceRegistry registry = new(null, binder);
                    game = DataModelBootstrap.CreateGame(registry);
                    for (int shapeIndex = 0; shapeIndex < shapes.Length; shapeIndex++)
                    {
                        RbxInstance part = registry.Create("Part");
                        part.Parent = registry.WorldRoot;
                        binder.SetShape(part.Id, shapes[shapeIndex]);
                        binder.SetSize(part.Id, new RbxVector3(4f, 1f, 2f));

                        Assert.IsTrue(binder.TryGetBoundObject(part.Id, out GameObject partGo));
                        Assert.AreEqual(expectedMeshNames[shapeIndex],
                            partGo.GetComponent<MeshFilter>().sharedMesh.name,
                            shapes[shapeIndex] + " must use its normalized unit mesh at both scales");
                        Vector3 scale = partGo.transform.localScale;
                        Assert.AreEqual(4f * metersPerStud, scale.x, Epsilon);
                        Assert.AreEqual(1f * metersPerStud, scale.y, Epsilon);
                        Assert.AreEqual(2f * metersPerStud, scale.z, Epsilon);
                    }
                }
                finally
                {
                    game?.Destroy();
                    Object.DestroyImmediate(root);
                }
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
        public void CanCollide_False_KeepsTheColliderEnabledAsATrigger()
        {
            RbxInstance part = CreatePartInWorld();
            Collider collider = BoundObject(part).GetComponent<Collider>();
            Assert.IsTrue(collider.enabled);
            Assert.IsFalse(collider.isTrigger, "a default part collides");

            _binder.SetCanCollide(part.Id, false);
            Assert.IsTrue(collider.enabled,
                "CanCollide=false must keep the collider: Roblox still fires Touched for the part and " +
                "rays still hit it");
            Assert.IsTrue(collider.isTrigger, "a non-colliding part lets bodies through as a trigger");

            _binder.SetCanCollide(part.Id, true);
            Assert.IsTrue(collider.enabled);
            Assert.IsFalse(collider.isTrigger, "CanCollide=true makes it solid again");
        }

        [Test]
        public void CanCollideFalsePart_IsHitByADefaultRay_AndSkippedWhenTheRayRespectsCanCollide()
        {
            RbxInstance ghost = CreateAnchoredPartAt(RayTargetPosition);
            _binder.SetCanCollide(ghost.Id, false);
            using UnityRbxPhysicsPort port = new(_binder);

            Assert.IsTrue(RayHits(port, ghost, respectCanCollide: false),
                "RaycastParams.RespectCanCollide defaults to false, so the query uses CanQuery (true) and " +
                "a CanCollide=false part is still hit");
            Assert.IsFalse(RayHits(port, ghost, respectCanCollide: true),
                "with RespectCanCollide the query uses CanCollide, so the non-colliding part is skipped");
        }

        [Test]
        public void Negative_SolidPart_IsHitWhetherOrNotTheRayRespectsCanCollide()
        {
            RbxInstance solid = CreateAnchoredPartAt(RayTargetPosition);
            using UnityRbxPhysicsPort port = new(_binder);

            Assert.IsTrue(RayHits(port, solid, respectCanCollide: false));
            Assert.IsTrue(RayHits(port, solid, respectCanCollide: true),
                "RespectCanCollide must only skip non-colliding parts");
        }

        [Test]
        public void CanCollideFalsePart_TriggerOverlap_IsReportedAsAContactThenItsEnd()
        {
            RbxInstance ghost = CreatePartInWorld();
            _binder.SetCanCollide(ghost.Id, false);
            RbxInstance mover = CreatePartInWorld();
            List<string> contacts = RecordContacts();
            RbxContactRelay relay = BoundObject(ghost).GetComponent<RbxContactRelay>();
            Collider moverCollider = BoundObject(mover).GetComponent<Collider>();

            DeliverTriggerMessage(relay, "OnTriggerEnter", moverCollider);
            DeliverTriggerMessage(relay, "OnTriggerExit", moverCollider);

            CollectionAssert.AreEqual(
                new[] { Contact(ghost, mover, true), Contact(ghost, mover, false) }, contacts,
                "the pickup / kill-zone idiom: a non-colliding part's overlap must become Touched and TouchEnded");
        }

        [Test]
        public void Negative_TriggerOverlapWithAnUnboundHostCollider_ReportsNothing()
        {
            RbxInstance ghost = CreatePartInWorld();
            _binder.SetCanCollide(ghost.Id, false);
            List<string> contacts = RecordContacts();
            GameObject hostVolume = GameObject.CreatePrimitive(PrimitiveType.Cube);
            try
            {
                DeliverTriggerMessage(BoundObject(ghost).GetComponent<RbxContactRelay>(), "OnTriggerEnter",
                    hostVolume.GetComponent<Collider>());

                Assert.IsEmpty(contacts, "a host object is not an instance, so there is no pair to report");
            }
            finally
            {
                Object.DestroyImmediate(hostVolume);
            }
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

        // ---- Live position read-back (reverse sync) ------------------------------------------

        [Test]
        public void GetLivePositionStuds_FollowsTheBackingTransform_WithNoLuaPositionAssignment()
        {
            // WHY this matters: a character motor or gravity moves the backing Rigidbody's
            // transform directly, the same way this test does, and never goes through
            // SetPosition/SetCFrame. GetLivePositionStuds must still see where the part actually
            // is, not the value frozen in the property store at spawn.
            RbxInstance part = CreatePartInWorld();
            GameObject partGo = BoundObject(part);

            partGo.transform.position = new Vector3(2.8f, 0.56f, 1.4f);

            RbxVector3 live = _binder.GetLivePositionStuds(part.Id);
            Assert.AreEqual(10f, live.X, Epsilon);
            Assert.AreEqual(2f, live.Y, Epsilon);
            Assert.AreEqual(-5f, live.Z, Epsilon);

            PartProperties read = _binder.GetPartPropertiesOrDefault(part.Id);
            Assert.AreEqual(10f, read.Position.X, Epsilon,
                "an unanchored part's property read follows its body as well, so Part.Position and " +
                "the live read agree instead of Part.Position staying frozen at the spawn value");
        }

        [Test]
        public void GetLivePositionStuds_FallsBackToStoredValue_ForAnUnmaterializedPart()
        {
            // An unmaterialized part (never parented into the world) has no live transform to
            // read, so the fallback must answer with whatever was pushed through the one-way sink.
            RbxInstance part = _registry.Create("Part");
            Assert.IsFalse(_binder.TryGetBoundObject(part.Id, out _),
                "precondition: this part must have no backing object yet");

            _binder.SetPosition(part.Id, new RbxVector3(7f, 8f, 9f));

            RbxVector3 live = _binder.GetLivePositionStuds(part.Id);
            Assert.AreEqual(7f, live.X, Epsilon);
            Assert.AreEqual(8f, live.Y, Epsilon);
            Assert.AreEqual(9f, live.Z, Epsilon);
        }

        [Test]
        public void D5_OnlyWorkspaceIsActive_AmongTheDataModelsChildren()
        {
            IReadOnlyList<RbxInstance> children = _game.GetChildren();
            Assert.Greater(children.Count, 1, "precondition: the bootstrap tree has services besides Workspace");
            foreach (RbxInstance child in children)
            {
                bool isWorkspace = ReferenceEquals(child, _registry.WorldRoot);
                Assert.AreEqual(isWorkspace, BoundObject(child).activeSelf,
                    child.Name + ": only Workspace and its descendants are the physical world");
            }
        }

        [Test]
        public void D5_PartUnderLightingOrDataModelRoot_IsNotActiveInHierarchy_AndNotHitByARay()
        {
            RbxInstance part = CreateAnchoredPartAt(RayTargetPosition);
            GameObject partGo = BoundObject(part);
            using UnityRbxPhysicsPort port = new(_binder);
            Assert.IsTrue(RayHits(port, part, respectCanCollide: false),
                "precondition: under Workspace the ray hits the part");

            part.Parent = _game.GetService("Lighting");
            Assert.IsFalse(partGo.activeInHierarchy,
                "Lighting is not the physical world: a Part stored there must not render or collide");
            Assert.IsFalse(RayHits(port, part, respectCanCollide: false),
                "a Part stored in Lighting must not answer workspace:Raycast");

            part.Parent = _game;
            Assert.IsFalse(partGo.activeSelf,
                "a direct child of the DataModel other than Workspace is inactive itself");
            Assert.IsFalse(partGo.activeInHierarchy);
            Assert.IsFalse(RayHits(port, part, respectCanCollide: false),
                "a Part parented straight to game must not answer workspace:Raycast");

            part.Parent = _registry.WorldRoot;
            Assert.IsTrue(partGo.activeInHierarchy, "back under Workspace the Part is physical again");
            Assert.IsTrue(RayHits(port, part, respectCanCollide: false));
        }

        [Test]
        public void D5_ContainerMovedBetweenTheDataModelRootAndWorkspace_RecomputesItsActiveFlag()
        {
            RbxInstance model = _registry.Create("Model");
            model.Parent = _game;
            RbxInstance part = _registry.Create("Part");
            part.Parent = model;
            GameObject modelGo = BoundObject(model);
            GameObject partGo = BoundObject(part);
            Assert.IsFalse(modelGo.activeSelf, "a Model parented straight to game is outside Workspace");
            Assert.IsFalse(partGo.activeInHierarchy);
            Assert.IsTrue(partGo.activeSelf, "only the top-level object below game carries the flag");

            model.Parent = _registry.WorldRoot;
            Assert.IsTrue(modelGo.activeSelf, "moving into Workspace must recompute the flag");
            Assert.IsTrue(partGo.activeInHierarchy, "the whole subtree becomes physical");

            model.Parent = _game;
            Assert.IsFalse(partGo.activeInHierarchy, "moving back out leaves the physical world again");
        }

        [Test]
        public void Negative_D5_DeeplyNestedWorkspaceContent_StaysActive()
        {
            RbxInstance folder = _registry.Create("Folder");
            folder.Parent = _registry.WorldRoot;
            RbxInstance model = _registry.Create("Model");
            model.Parent = folder;
            RbxInstance part = _registry.Create("Part");
            part.Parent = model;

            foreach (RbxInstance node in new[] { folder, model, part })
            {
                Assert.IsTrue(BoundObject(node).activeSelf, node.Name + " keeps its own flag on");
                Assert.IsTrue(BoundObject(node).activeInHierarchy, node.Name + " is Workspace content");
            }
        }

        [Test]
        public void Raycast_HitsCylinderPart()
        {
            RbxInstance cylinder = CreateAnchoredPartAt(RayTargetPosition);
            _binder.SetShape(cylinder.Id, RbxPartShape.Cylinder);
            _binder.SetSize(cylinder.Id, new RbxVector3(4f, 2f, 2f));
            using UnityRbxPhysicsPort port = new(_binder);

            Assert.IsTrue(RayHits(port, cylinder, respectCanCollide: false),
                "a Cylinder's collider lives on its Shape child, and the hit must still resolve to the part");
        }

        [Test]
        public void ContactWithACylindersShapeChild_ResolvesToTheCylinder()
        {
            RbxInstance cylinder = CreatePartInWorld();
            _binder.SetShape(cylinder.Id, RbxPartShape.Cylinder);
            RbxInstance ball = CreatePartInWorld();
            List<string> contacts = RecordContacts();

            DeliverTriggerMessage(BoundObject(ball).GetComponent<RbxContactRelay>(), "OnTriggerEnter",
                ShapeChildOf(cylinder).GetComponent<Collider>());

            CollectionAssert.AreEqual(new[] { Contact(ball, cylinder, true) }, contacts,
                "Touched must fire for a Cylinder even though its collider sits on a child");
        }

        [Test]
        public void ReverseLookup_FollowsShapeSwitches_AndStopsAtContainers()
        {
            RbxInstance model = _registry.Create("Model");
            model.Parent = _registry.WorldRoot;
            RbxInstance part = _registry.Create("Part");
            part.Parent = model;
            GameObject partGo = BoundObject(part);
            GameObject modelGo = BoundObject(model);

            Assert.IsTrue(_binder.TryGetInstanceId(partGo, out InstanceId byRoot));
            Assert.AreEqual(part.Id.Value, byRoot.Value);
            Assert.IsTrue(_binder.TryGetInstanceId(modelGo, out InstanceId byModel),
                "containers are bound objects too");
            Assert.AreEqual(model.Id.Value, byModel.Value);
            Assert.IsFalse(_binder.TryResolvePartInstanceId(modelGo, out _),
                "a Model is a container, never the part a collider belongs to");

            _binder.SetShape(part.Id, RbxPartShape.Cylinder);
            GameObject shapeChild = ShapeChildOf(part);
            Assert.IsFalse(_binder.TryGetInstanceId(shapeChild, out _),
                "the exact lookup only knows backing objects");
            Assert.IsTrue(_binder.TryResolvePartInstanceId(shapeChild, out InstanceId byShapeChild));
            Assert.AreEqual(part.Id.Value, byShapeChild.Value, "the Shape child resolves to its Cylinder");

            GameObject strayChild = new("HostObjectUnderAModel");
            try
            {
                strayChild.transform.SetParent(modelGo.transform, false);
                Assert.IsFalse(_binder.TryResolvePartInstanceId(strayChild, out _),
                    "the nearest bound ancestor is a container, so the collider belongs to no part");
            }
            finally
            {
                Object.DestroyImmediate(strayChild);
            }

            _binder.SetShape(part.Id, RbxPartShape.Block);
            Assert.IsTrue(_binder.TryGetInstanceId(partGo, out InstanceId afterSwitch),
                "a shape switch keeps the backing object, and with it the reverse entry");
            Assert.AreEqual(part.Id.Value, afterSwitch.Value);
            Assert.IsTrue(_binder.TryResolvePartInstanceId(partGo, out InstanceId blockRoot));
            Assert.AreEqual(part.Id.Value, blockRoot.Value);
        }

        [Test]
        public void ReverseLookup_CostDoesNotGrowWithTheBoundSet()
        {
            RbxInstance crowd = _registry.Create("Folder");
            crowd.Parent = _registry.WorldRoot;
            for (int index = 0; index < 2048; index++)
            {
                RbxInstance filler = _registry.Create("Folder");
                filler.Parent = crowd;
            }

            RbxInstance cylinder = CreatePartInWorld();
            _binder.SetShape(cylinder.Id, RbxPartShape.Cylinder);
            GameObject shapeChild = ShapeChildOf(cylinder);
            GameObject stranger = new("UnboundLookupProbe");
            try
            {
                Assert.Greater(_binder.BoundCount, 2048, "precondition: thousands of bound objects");
                long before = _binder.ReverseLookupProbeCount;
                int resolved = 0;
                for (int index = 0; index < 1000; index++)
                {
                    if (!_binder.TryGetInstanceId(stranger, out _)
                        && _binder.TryResolvePartInstanceId(shapeChild, out InstanceId id)
                        && id.Value == cylinder.Id.Value)
                    {
                        resolved++;
                    }
                }

                long probes = _binder.ReverseLookupProbeCount - before;
                Assert.AreEqual(1000, resolved, "every lookup must still answer correctly");
                Assert.LessOrEqual(probes, 3000,
                    "a miss is one probe and a Shape child two (itself, then its part); a lookup that " +
                    "scanned the " + _binder.BoundCount + " bound objects would cost millions per 1000 hits");
            }
            finally
            {
                Object.DestroyImmediate(stranger);
            }
        }

        [Test]
        public void Binder_ExternallyDestroyedGameObject_ReparentAndWritesDoNotThrow()
        {
            RbxInstance model = _registry.Create("Model");
            model.Parent = _registry.WorldRoot;
            RbxInstance part = CreatePartInWorld();
            GameObject partGo = BoundObject(part);

            // WHY: a host kill-plane script, a scene unload in progress or an editor deletion
            // destroys the backing without asking the registry.
            Object.DestroyImmediate(partGo);

            Assert.DoesNotThrow(() => part.Name = "KilledByHost", "rename");
            Assert.DoesNotThrow(() => _binder.SetPosition(part.Id, new RbxVector3(10f, 5f, -4f)),
                "transform write");
            Assert.DoesNotThrow(() => _binder.SetColor(part.Id, RbxColor3.FromRGB(255f, 0f, 0f)),
                "appearance write");
            Assert.DoesNotThrow(() => part.Parent = model, "re-parent inside the world");
            Assert.IsFalse(_binder.TryGetBoundObject(part.Id, out _),
                "a destroyed backing counts as unbound");
            Assert.DoesNotThrow(() => part.Parent = null, "leaving the world");
            Assert.DoesNotThrow(() => part.Parent = _registry.WorldRoot, "re-entering the world");

            GameObject rematerialized = BoundObject(part);
            Assert.IsFalse(ReferenceEquals(partGo, rematerialized), "re-entry builds a fresh backing");
            Assert.AreEqual("KilledByHost", rematerialized.name);
            Assert.AreEqual(2.8f, rematerialized.transform.position.x, Epsilon,
                "writes made while unbound are kept and applied on re-entry");
            Assert.IsTrue(rematerialized.activeInHierarchy);
            Assert.DoesNotThrow(() => part.Destroy(), "destroy");
        }

        [Test]
        public void NewChildOfAnExternallyDestroyedContainer_StillMaterializes()
        {
            RbxInstance worldModel = _registry.Create("Model");
            worldModel.Parent = _registry.WorldRoot;
            RbxInstance storedModel = _registry.Create("Model");
            storedModel.Parent = _game.GetService("Lighting");
            Object.DestroyImmediate(BoundObject(worldModel));
            Object.DestroyImmediate(BoundObject(storedModel));

            RbxInstance worldPart = _registry.Create("Part");
            worldPart.Parent = worldModel;
            RbxInstance storedPart = _registry.Create("Part");
            storedPart.Parent = storedModel;

            GameObject worldPartGo = BoundObject(worldPart);
            Assert.AreEqual(_root.transform, worldPartGo.transform.parent,
                "with its container's backing gone the part sits directly under the host");
            Assert.IsTrue(worldPartGo.activeInHierarchy, "it is still Workspace content, so it stays physical");
            Assert.IsFalse(BoundObject(storedPart).activeInHierarchy,
                "Lighting content stays out of the physical world even without its container's backing");
        }

        // ---- Nested parts: a part under a part is independent unless welded -------------------

        private RbxInstance CreateChildPart(RbxInstance parent, string name)
        {
            RbxInstance child = _registry.Create("Part");
            child.Name = name;
            child.Parent = parent;
            return child;
        }

        [Test]
        public void AssetRule_NestedPart_LossyScaleEqualsOwnSizeTimesMetersPerStud()
        {
            RbxInstance handle = CreatePartInWorld();
            handle.Name = "Handle";
            _binder.SetSize(handle.Id, new RbxVector3(4f, 1f, 2f));
            _binder.SetCFrame(handle.Id, RbxCFrame.Angles(0f, Mathf.PI / 4f, 0f));
            RbxInstance blade = CreateChildPart(handle, "Blade");
            _binder.SetSize(blade.Id, new RbxVector3(1f, 4f, 1f));

            Vector3 lossy = BoundObject(blade).transform.lossyScale;

            Assert.AreEqual(0.28f, lossy.x, Epsilon,
                "Roblox Size is absolute: a 1x4x1 Blade under a 4x1x2 Handle is 1x4x1 studs, not the product");
            Assert.AreEqual(1.12f, lossy.y, Epsilon);
            Assert.AreEqual(0.28f, lossy.z, Epsilon);
        }

        [Test]
        public void NestedPart_ParentMove_DoesNotMoveChildBackingObject()
        {
            RbxInstance handle = CreateAnchoredPartAt(new RbxVector3(0f, 10f, 0f));
            RbxInstance blade = CreateChildPart(handle, "Blade");
            _binder.SetAnchored(blade.Id, true);
            _binder.SetPosition(blade.Id, new RbxVector3(0f, 12f, 0f));
            GameObject bladeGo = BoundObject(blade);
            Vector3 before = bladeGo.transform.position;

            _binder.SetCFrame(handle.Id,
                RbxCFrame.FromPosition(40f, 10f, 0f) * RbxCFrame.Angles(0f, Mathf.PI / 2f, 0f));

            Assert.Less((bladeGo.transform.position - before).magnitude, Epsilon,
                "a part under a part is not welded to it: moving the parent must not drag the child");
            Vector3 readBack = RbxSpace.ToUnity(_binder.GetPartPropertiesOrDefault(blade.Id).Position);
            Assert.Less((bladeGo.transform.position - readBack).magnitude, Epsilon,
                "what renders is what Blade.Position reads");
        }

        [Test]
        public void NestedPart_UnderAnUnanchoredParent_IsNotACompoundColliderOfItsRigidbody()
        {
            RbxInstance handle = CreatePartInWorld();
            RbxInstance blade = CreateChildPart(handle, "Blade");
            _binder.SetAnchored(blade.Id, true);
            Assert.IsNotNull(BoundObject(handle).GetComponent<Rigidbody>(), "precondition: the parent is simulated");

            Collider bladeCollider = BoundObject(blade).GetComponent<Collider>();
            Physics.SyncTransforms();

            Assert.IsNull(bladeCollider.GetComponentInParent<Rigidbody>(),
                "an anchored child must not sit under the parent's Rigidbody, or Unity makes its collider " +
                "part of the parent's compound body (welded in all but name)");
            Assert.IsNull(bladeCollider.attachedRigidbody);
        }

        [Test]
        public void NestedPart_FollowsItsParentThroughReparentAndWorldMembership()
        {
            RbxInstance model = _registry.Create("Model");
            model.Parent = _registry.WorldRoot;
            RbxInstance handle = CreatePartInWorld();
            handle.Name = "Handle";
            RbxInstance blade = CreateChildPart(handle, "Blade");
            GameObject bladeGo = BoundObject(blade);

            handle.Parent = model;
            Assert.IsTrue(bladeGo.transform.IsChildOf(BoundObject(model).transform),
                "the child container moves with its part");
            Assert.IsTrue(bladeGo.activeInHierarchy);

            handle.Parent = _game.GetService("Lighting");
            Assert.IsFalse(bladeGo.activeInHierarchy, "a part stored in Lighting takes its children out of the world");

            handle.Parent = _game;
            Assert.IsFalse(bladeGo.activeInHierarchy, "so does a part parented straight to game");

            handle.Parent = null;
            Assert.IsFalse(bladeGo.activeInHierarchy);

            handle.Parent = _registry.WorldRoot;
            Assert.AreSame(bladeGo, BoundObject(blade), "re-entry reuses the parked child");
            Assert.IsTrue(bladeGo.activeInHierarchy, "back in Workspace the child is physical again");
            Assert.AreEqual("Workspace.Handle" + InstanceGameObjectBinder.ChildContainerSuffix + ".Blade",
                TransformPathBelowHost(bladeGo.transform));
        }

        [Test]
        public void NestedPart_ChildContainer_IsNamedAfterItsPart_AndIsNeverAPartOrAnAdoptableObject()
        {
            RbxInstance handle = CreatePartInWorld();
            handle.Name = "Handle";
            RbxInstance blade = CreateChildPart(handle, "Blade");
            GameObject container = BoundObject(blade).transform.parent.gameObject;

            Assert.AreEqual("Handle" + InstanceGameObjectBinder.ChildContainerSuffix, container.name);
            Assert.Less((container.transform.lossyScale - Vector3.one).magnitude, Epsilon,
                "the container carries no Size");
            Assert.IsFalse(_binder.TryGetInstanceId(container, out _));
            Assert.IsFalse(_binder.TryResolvePartInstanceId(container, out _),
                "a child container is not the part a collider belongs to");
            Assert.IsTrue(_binder.IsInsideBinderOwnedBacking(container),
                "a child container is binder-owned, so the world adapter must never adopt it as a prop");

            handle.Name = "Hilt";
            Assert.AreEqual("Hilt" + InstanceGameObjectBinder.ChildContainerSuffix, container.name);
        }

        [Test]
        public void NestedPart_DestroyingTheParent_ReleasesItsChildContainer()
        {
            RbxInstance handle = CreatePartInWorld();
            RbxInstance blade = CreateChildPart(handle, "Blade");
            GameObject bladeGo = BoundObject(blade);
            GameObject container = bladeGo.transform.parent.gameObject;

            handle.Destroy();

            Assert.IsTrue(bladeGo == null, "the child is destroyed with its parent");
            Assert.IsTrue(container == null, "and the container it lived in does not outlive the part");
        }

        // ---- Simulated parts read back their body's pose --------------------------------------

        [Test]
        public void UnanchoredPart_PositionReadFollowsBody()
        {
            RbxInstance part = CreatePartInWorld();
            _binder.SetPosition(part.Id, new RbxVector3(0f, 50f, 0f));
            GameObject partGo = BoundObject(part);

            // WHY: EditMode never steps physics; moving the body's transform is exactly what gravity
            // or a character motor does to it.
            partGo.transform.SetPositionAndRotation(RbxSpace.ToUnity(new RbxVector3(3f, 2f, -1f)),
                RbxSpace.ToUnity(RbxCFrame.Angles(0f, Mathf.PI / 2f, 0f)));

            PartProperties read = _binder.GetPartPropertiesOrDefault(part.Id);
            Assert.IsTrue(read.Position.FuzzyEq(new RbxVector3(3f, 2f, -1f), Epsilon),
                "Part.Position must read where the body is, not the spawn height 50: " + read.Position);
            Assert.AreEqual(-1f, read.CFrame.LookVector.X, Epsilon, "the orientation follows the body too");
            Assert.IsTrue(_binder.TryGetPartProperties(part.Id, out PartProperties tried));
            Assert.IsTrue(tried.Position.FuzzyEq(new RbxVector3(3f, 2f, -1f), Epsilon),
                "a world save captures the live pose as well");
        }

        [Test]
        public void UnanchoredPart_SizeWrite_DoesNotResetPosition()
        {
            RbxInstance part = CreatePartInWorld();
            _binder.SetPosition(part.Id, new RbxVector3(0f, 50f, 0f));
            GameObject partGo = BoundObject(part);
            Vector3 fallen = RbxSpace.ToUnity(new RbxVector3(0f, 2f, 0f));
            partGo.transform.position = fallen;

            _binder.SetSize(part.Id, new RbxVector3(2f, 2f, 2f));

            Assert.Less((partGo.transform.position - fallen).magnitude, Epsilon,
                "a Size write must not teleport a fallen part back to its last scripted pose");
            Assert.AreEqual(0.56f, partGo.transform.localScale.x, Epsilon, "the size itself still applies");
        }

        [Test]
        public void UnanchoredPart_ShapeColorAndAnchoredWrites_KeepTheBodysPose()
        {
            RbxInstance part = CreatePartInWorld();
            _binder.SetPosition(part.Id, new RbxVector3(0f, 50f, 0f));
            GameObject partGo = BoundObject(part);
            Vector3 fallen = RbxSpace.ToUnity(new RbxVector3(6f, 2f, 0f));
            partGo.transform.position = fallen;

            _binder.SetShape(part.Id, RbxPartShape.Ball);
            _binder.SetColor(part.Id, RbxColor3.FromRGB(255f, 0f, 0f));
            _binder.SetAnchored(part.Id, true);

            Assert.Less((partGo.transform.position - fallen).magnitude, Epsilon,
                "a whole-visual rebuild (Shape) starts from the body's pose, not the stored one");
            Assert.IsNull(partGo.GetComponent<Rigidbody>(), "precondition: now anchored");
            Assert.IsTrue(_binder.GetPartPropertiesOrDefault(part.Id).Position
                    .FuzzyEq(new RbxVector3(6f, 2f, 0f), Epsilon),
                "anchoring where it landed keeps reading where it landed, with no body left to follow");
        }

        [Test]
        public void Negative_UnanchoredPartAtRest_ReadsBackExactlyWhatWasWritten()
        {
            RbxInstance part = CreatePartInWorld();
            Assert.IsNotNull(BoundObject(part).GetComponent<Rigidbody>(), "precondition: simulated");
            RbxCFrame written = RbxCFrame.FromPosition(1.1f, 2.2f, -3.3f) * RbxCFrame.Angles(0.3f, 1.1f, -0.7f);

            _binder.SetCFrame(part.Id, written);

            CollectionAssert.AreEqual(written.GetComponents(),
                _binder.GetPartPropertiesOrDefault(part.Id).CFrame.GetComponents(),
                "a body physics has not moved reads back the scripted CFrame bit for bit, not a float round trip");
        }

        // ---- Destroyed parts keep their last-known state (bounded) -----------------------------

        [Test]
        public void Destroy_KeepsTheLastKnownPartState_ForDestructionHandlerReads()
        {
            RbxInstance part = CreateAnchoredPartAt(new RbxVector3(10f, 5f, -4f));
            _binder.SetSize(part.Id, new RbxVector3(3f, 7f, 2f));
            _binder.SetColor(part.Id, RbxColor3.FromRGB(255f, 0f, 0f));

            part.Destroy();

            PartProperties last = _binder.GetPartPropertiesOrDefault(part.Id);
            Assert.IsTrue(last.Position.FuzzyEq(new RbxVector3(10f, 5f, -4f), Epsilon),
                "Destroying/AncestryChanged handlers run after destruction and must read the last " +
                "position, not the identity default: " + last.Position);
            Assert.IsTrue(last.Size.FuzzyEq(new RbxVector3(3f, 7f, 2f), Epsilon));
            Assert.AreEqual(1f, last.Color.R, Epsilon);
            Assert.IsTrue(last.Anchored);
            Assert.IsTrue(_binder.TryGetPartProperties(part.Id, out _));
            Assert.AreEqual(1, _binder.RetainedDestroyedPartCount);
        }

        [Test]
        public void Destroy_SimulatedPart_RetainsWhereItsBodyWas()
        {
            RbxInstance part = CreatePartInWorld();
            _binder.SetPosition(part.Id, new RbxVector3(0f, 50f, 0f));
            BoundObject(part).transform.position = RbxSpace.ToUnity(new RbxVector3(8f, 2f, 0f));

            part.Destroy();

            Assert.IsTrue(_binder.GetPartPropertiesOrDefault(part.Id).Position
                    .FuzzyEq(new RbxVector3(8f, 2f, 0f), Epsilon),
                "an explosion spawned from a Destroying handler goes where the part fell, not where it spawned");
        }

        [Test]
        public void DestroyedPartState_IsBounded_OldestForgottenFirst()
        {
            int retention = InMemoryPartPropertySink.DestroyedPartRetention;
            List<RbxInstance> destroyed = new();
            for (int index = 0; index <= retention; index++)
            {
                // WHY never parented: a part outside the world has no GameObject, so the loop costs
                // no Unity objects while the sink path is the one every destroyed part takes.
                RbxInstance part = _registry.Create("Part");
                _binder.SetPosition(part.Id, new RbxVector3(index + 1f, 0f, 0f));
                part.Destroy();
                destroyed.Add(part);
            }

            Assert.AreEqual(retention, _binder.RetainedDestroyedPartCount,
                "a world that destroys parts forever holds at most the retention, never one bundle per part");
            Assert.IsFalse(_binder.TryGetPartProperties(destroyed[0].Id, out _),
                "the oldest destroyed part is forgotten first");
            Assert.AreEqual(0f, _binder.GetPartPropertiesOrDefault(destroyed[0].Id).Position.X, Epsilon);
            Assert.AreEqual(2f, _binder.GetPartPropertiesOrDefault(destroyed[1].Id).Position.X, Epsilon);
            Assert.AreEqual(retention + 1f,
                _binder.GetPartPropertiesOrDefault(destroyed[retention].Id).Position.X, Epsilon);
        }

        [Test]
        public void DestroyedPart_LateHostWrite_UpdatesTheRetainedCopy_NotTheLiveStore()
        {
            RbxInstance part = _registry.Create("Part");
            _binder.SetPosition(part.Id, new RbxVector3(1f, 0f, 0f));
            part.Destroy();

            _binder.SetColor(part.Id, RbxColor3.FromRGB(255f, 0f, 0f));

            Assert.AreEqual(1f, _binder.GetPartPropertiesOrDefault(part.Id).Color.R, Epsilon,
                "the destroyed part keeps the value written to it");
            Assert.AreEqual(1, _binder.RetainedDestroyedPartCount);
            for (int index = 0; index < InMemoryPartPropertySink.DestroyedPartRetention; index++)
            {
                RbxInstance filler = _registry.Create("Part");
                _binder.SetPosition(filler.Id, new RbxVector3(2f, 0f, 0f));
                filler.Destroy();
            }

            Assert.IsFalse(_binder.TryGetPartProperties(part.Id, out _),
                "the late write stayed in the bounded store and ages out with it; a write that re-entered " +
                "the live store would never be released again");
        }

        [Test]
        public void Negative_WholeBundlePush_MakesADestroyedIdALivePartAgain()
        {
            RbxInstance part = _registry.Create("Part");
            _binder.SetPosition(part.Id, new RbxVector3(1f, 0f, 0f));
            part.Destroy();
            PartProperties restored = PartProperties.CreateDefault();
            restored.Size = new RbxVector3(9f, 9f, 9f);

            _binder.SetPartProperties(part.Id, in restored);

            Assert.AreEqual(0, _binder.RetainedDestroyedPartCount,
                "a restore that reuses the id owns it from then on");
            Assert.AreEqual(9f, _binder.GetPartPropertiesOrDefault(part.Id).Size.X, Epsilon);
        }

        [Test]
        public void BothSinks_DestroyedPart_ReadTheSameLastKnownState_AndASecondNoticeChangesNothing()
        {
            InstanceRegistry headlessRegistry = new();
            InMemoryPartPropertySink headless = new(headlessRegistry);
            RbxInstance rendered = CreateAnchoredPartAt(new RbxVector3(4f, 5f, 6f));
            RbxInstance stored = headlessRegistry.Create("Part");
            headless.SetAnchored(stored.Id, true);
            headless.SetPosition(stored.Id, new RbxVector3(4f, 5f, 6f));

            rendered.Destroy();
            stored.Destroy();
            _binder.OnPartDestroyed(rendered.Id);
            headless.OnPartDestroyed(stored.Id);

            PartProperties fromBinder = _binder.GetPartPropertiesOrDefault(rendered.Id);
            PartProperties fromHeadless = headless.GetPartPropertiesOrDefault(stored.Id);
            Assert.IsTrue(fromBinder.Position.FuzzyEq(new RbxVector3(4f, 5f, 6f), Epsilon));
            Assert.IsTrue(fromHeadless.Position.FuzzyEq(fromBinder.Position, Epsilon),
                "a destruction handler reads the same answer whether or not the world renders");
            Assert.AreEqual(fromBinder.Anchored, fromHeadless.Anchored);
            Assert.AreEqual(1, _binder.RetainedDestroyedPartCount);
            Assert.AreEqual(1, headless.RetainedDestroyedPartCount);
        }

        // ---- Headless sink: destroyed parts are released (bounded) ------------------------------

        [Test]
        public void InMemorySink_DropsBundleOnDestroy()
        {
            InstanceRegistry registry = new();
            InMemoryPartPropertySink sink = new(registry);
            RbxInstance part = registry.Create("Part");
            sink.SetPosition(part.Id, new RbxVector3(4f, 5f, 6f));
            Assert.AreEqual(1, sink.LivePartCount, "precondition");

            part.Destroy();

            Assert.AreEqual(0, sink.LivePartCount,
                "a headless world must not keep every destroyed part's bundle for the session");
            Assert.AreEqual(1, sink.RetainedDestroyedPartCount);
            Assert.IsTrue(sink.GetPartPropertiesOrDefault(part.Id).Position
                    .FuzzyEq(new RbxVector3(4f, 5f, 6f), Epsilon),
                "destruction handlers still read the last value");
        }

        [Test]
        public void InMemorySink_DestroyedState_IsBounded_AndALateWriteDoesNotRegrowTheLiveStore()
        {
            InstanceRegistry registry = new();
            InMemoryPartPropertySink sink = new(registry);
            RbxInstance first = registry.Create("Part");
            sink.SetPosition(first.Id, new RbxVector3(1f, 0f, 0f));
            first.Destroy();
            sink.SetColor(first.Id, RbxColor3.FromRGB(0f, 0f, 255f));
            Assert.AreEqual(0, sink.LivePartCount, "a late write lands in the retained copy");
            Assert.AreEqual(1f, sink.GetPartPropertiesOrDefault(first.Id).Color.B, Epsilon);

            for (int index = 0; index < InMemoryPartPropertySink.DestroyedPartRetention; index++)
            {
                RbxInstance filler = registry.Create("Part");
                sink.SetPosition(filler.Id, new RbxVector3(2f, 0f, 0f));
                filler.Destroy();
            }

            Assert.AreEqual(0, sink.LivePartCount);
            Assert.AreEqual(InMemoryPartPropertySink.DestroyedPartRetention, sink.RetainedDestroyedPartCount);
            Assert.IsFalse(sink.TryGetPartProperties(first.Id, out _), "the oldest destroyed part is forgotten first");
        }

        [Test]
        public void Negative_InMemorySink_LiveParts_AreNeverRetiredByOtherDestroys()
        {
            InstanceRegistry registry = new();
            InMemoryPartPropertySink sink = new(registry);
            RbxInstance survivor = registry.Create("Part");
            sink.SetPosition(survivor.Id, new RbxVector3(7f, 0f, 0f));
            RbxInstance folder = registry.Create("Folder");

            folder.Destroy();

            Assert.AreEqual(1, sink.LivePartCount, "only the destroyed instance's state moves");
            Assert.AreEqual(0, sink.RetainedDestroyedPartCount, "a destroyed non-part has no part state to keep");
            Assert.AreEqual(7f, sink.GetPartPropertiesOrDefault(survivor.Id).Position.X, Epsilon);
        }

        // ---- Sink boundary: Roblox bounds for host-side writers ---------------------------------

        private static void AssertRefusedAsBadArgument(TestDelegate write, string member)
        {
            RbxError error = Assert.Throws<RbxError>(write, member + " with NaN/inf must be refused");
            Assert.AreEqual(RbxErrorCode.BadArgument, error.Code);
            StringAssert.Contains(member, error.Message);
        }

        [Test]
        public void SinkSizeWrite_ClampsToRobloxRange()
        {
            RbxInstance part = CreatePartInWorld();

            _binder.SetSize(part.Id, new RbxVector3(-2f, 0f, 1e9f));

            RbxVector3 size = _binder.GetPartPropertiesOrDefault(part.Id).Size;
            Assert.AreEqual(PartPropertyBounds.MinimumSizeStuds, size.X, "a negative axis rises to 0.001, never mirrors");
            Assert.AreEqual(PartPropertyBounds.MinimumSizeStuds, size.Y);
            Assert.AreEqual(PartPropertyBounds.MaximumSizeStuds, size.Z, "Roblox's largest axis is 2048 studs");
            Vector3 scale = BoundObject(part).transform.localScale;
            Assert.Greater(scale.x, 0f, "no mirrored mesh and no negative BoxCollider");
            Assert.AreEqual(RbxSpace.SizeToUnity(size).z, scale.z, Epsilon, "what renders is what reads");

            PartProperties bundle = PartProperties.CreateDefault();
            bundle.Size = new RbxVector3(5000f, 0.0001f, 3f);
            _binder.SetPartProperties(part.Id, in bundle);
            Assert.AreEqual(new RbxVector3(PartPropertyBounds.MaximumSizeStuds, PartPropertyBounds.MinimumSizeStuds, 3f),
                _binder.GetPartPropertiesOrDefault(part.Id).Size, "a whole-bundle push is held to the same range");
        }

        [Test]
        public void Negative_SinkSizeWrite_InsideTheRange_IsStoredExactly()
        {
            RbxInstance part = CreatePartInWorld();
            RbxVector3 edges = new(PartPropertyBounds.MinimumSizeStuds, PartPropertyBounds.MaximumSizeStuds, 0.5f);

            _binder.SetSize(part.Id, edges);

            Assert.AreEqual(edges, _binder.GetPartPropertiesOrDefault(part.Id).Size,
                "the bounds themselves and every value between them are kept unchanged");
        }

        [Test]
        public void SinkSpatialWrite_NonFinite_IsRefusedWithBadArgument_AndChangesNothing()
        {
            RbxInstance part = CreateAnchoredPartAt(new RbxVector3(1f, 2f, 3f));
            _binder.SetSize(part.Id, new RbxVector3(2f, 2f, 2f));
            GameObject partGo = BoundObject(part);
            Vector3 pose = partGo.transform.position;
            Vector3 scale = partGo.transform.localScale;
            PartProperties nonFiniteBundle = PartProperties.CreateDefault();
            nonFiniteBundle.CFrame = RbxCFrame.FromPosition(float.NaN, 0f, 0f);

            AssertRefusedAsBadArgument(
                () => _binder.SetPosition(part.Id, new RbxVector3(float.NaN, 0f, 0f)), "Position");
            AssertRefusedAsBadArgument(
                () => _binder.SetCFrame(part.Id, RbxCFrame.FromPosition(0f, float.PositiveInfinity, 0f)), "CFrame");
            AssertRefusedAsBadArgument(
                () => _binder.SetSize(part.Id, new RbxVector3(1f, float.PositiveInfinity, 1f)), "Size");
            AssertRefusedAsBadArgument(
                () => _binder.SetPartProperties(part.Id, in nonFiniteBundle), "CFrame");

            PartProperties read = _binder.GetPartPropertiesOrDefault(part.Id);
            Assert.IsTrue(read.Position.FuzzyEq(new RbxVector3(1f, 2f, 3f), Epsilon),
                "a refused write leaves the stored pose, so the read never disagrees with what renders");
            Assert.IsTrue(read.Size.FuzzyEq(new RbxVector3(2f, 2f, 2f), Epsilon));
            Assert.AreEqual(pose, partGo.transform.position);
            Assert.AreEqual(scale, partGo.transform.localScale);
        }

        [Test]
        public void InMemorySink_SizeWrite_ClampsFiniteAxesLikeTheBinder_AndKeepsNonFiniteForTheCapture()
        {
            InMemoryPartPropertySink sink = new();
            InstanceId id = _registry.Create("Part").Id;

            sink.SetSize(id, new RbxVector3(-2f, 0f, 1e9f));
            Assert.AreEqual(new RbxVector3(PartPropertyBounds.MinimumSizeStuds, PartPropertyBounds.MinimumSizeStuds,
                    PartPropertyBounds.MaximumSizeStuds), sink.GetPartPropertiesOrDefault(id).Size,
                "a headless world answers the same Size a rendered one does");

            sink.SetSize(id, new RbxVector3(1f, float.PositiveInfinity, 1f));
            Assert.IsTrue(float.IsPositiveInfinity(sink.GetPartPropertiesOrDefault(id).Size.Y),
                "nothing renders here, so a non-finite axis is kept for the world-package capture to " +
                "report with a diagnostic instead of vanishing silently");
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
