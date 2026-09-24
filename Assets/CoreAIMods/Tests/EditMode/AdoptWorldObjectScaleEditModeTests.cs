using System.Collections.Generic;
using CoreAI.Logging;
using CoreAI.Mods.Rbx.Binding;
using CoreAI.Mods.Rbx.Datatypes;
using CoreAI.Mods.Rbx.Instances;
using CoreAI.Mods.Rbx.Spatial;
using NUnit.Framework;
using UnityEngine;

namespace CoreAI.Tests.EditMode
{
    /// <summary>
    /// AdoptWorldObject reads initial Part state through the inverse RbxSpace boundary, so the
    /// adopted Size must match the part the user actually sees — including under a Size-scaled
    /// ancestor, where localScale omits the parent factor. After adoption the host object stays the
    /// host's: script writes move it, but never rescale it, strip its mesh or collider, replace its
    /// material or add and remove its Rigidbody, and world gravity never reaches it (DEV-6).
    /// </summary>
    public sealed class AdoptWorldObjectScaleEditModeTests
    {
        private const float Epsilon = 1e-4f;

        private readonly List<GameObject> _created = new();
        private GameObject _root;
        private InstanceGameObjectBinder _binder;
        private InstanceRegistry _registry;
        private RbxDataModel _game;

        [SetUp]
        public void SetUp()
        {
            // WHY: A non-1:1 scale is the only regime where a metres/studs leak is observable.
            RbxSpace.ResetForTests(0.5f);
            _root = new GameObject("AdoptScaleTestRoot");
            _created.Add(_root);
            _binder = new InstanceGameObjectBinder(_root.transform);
            _registry = new InstanceRegistry(null, _binder);
            _game = DataModelBootstrap.CreateGame(_registry);
        }

        [TearDown]
        public void TearDown()
        {
            _game.Destroy();
            for (int i = 0; i < _created.Count; i++)
            {
                if (_created[i] != null)
                {
                    Object.DestroyImmediate(_created[i]);
                }
            }

            _created.Clear();
            RbxSpace.ResetForTests();
        }

        [Test]
        public void AdoptWorldObject_WithoutScaledAncestor_ReportsLocalSize()
        {
            GameObject go = new("AdoptProbe");
            _created.Add(go);
            go.transform.localScale = new Vector3(1f, 2f, 3f);
            RbxInstance part = _registry.Create("Part");

            _binder.AdoptWorldObject(part.Id, go);

            RbxVector3 size = _binder.GetPartPropertiesOrDefault(part.Id).Size;
            RbxVector3 expected = RbxSpace.SizeFromUnity(new Vector3(1f, 2f, 3f));
            Assert.AreEqual(expected.X, size.X, Epsilon);
            Assert.AreEqual(expected.Y, size.Y, Epsilon);
            Assert.AreEqual(expected.Z, size.Z, Epsilon);
        }

        [Test]
        public void AdoptWorldObject_UnderScaledAncestor_ReportsWorldSeenSize()
        {
            GameObject ancestor = new("AdoptAncestor");
            _created.Add(ancestor);
            ancestor.transform.localScale = new Vector3(2f, 2f, 2f);
            GameObject go = new("AdoptNestedProbe");
            _created.Add(go);
            go.transform.SetParent(ancestor.transform, false);
            go.transform.localScale = Vector3.one;
            RbxInstance part = _registry.Create("Part");

            _binder.AdoptWorldObject(part.Id, go);

            // WHY: the user sees a 2 m cube (1 m local under a 2x ancestor); at 0.5 m/stud that
            // is 4 studs, not the 2 studs localScale alone would report.
            RbxVector3 size = _binder.GetPartPropertiesOrDefault(part.Id).Size;
            Assert.AreEqual(4f, size.X, Epsilon);
            Assert.AreEqual(4f, size.Y, Epsilon);
            Assert.AreEqual(4f, size.Z, Epsilon);
        }

        private GameObject CreateHostCube(string name)
        {
            GameObject go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = name;
            _created.Add(go);
            return go;
        }

        [Test]
        public void AdoptedHostObject_PositionWrite_PreservesLossyScale()
        {
            GameObject ancestor = new("AdoptScaledAncestor");
            _created.Add(ancestor);
            ancestor.transform.localScale = new Vector3(2f, 2f, 2f);
            GameObject go = CreateHostCube("AdoptPositionProbe");
            go.transform.SetParent(ancestor.transform, false);
            RbxInstance part = _registry.Create("Part");
            _binder.AdoptWorldObject(part.Id, go);

            _binder.SetPosition(part.Id, new RbxVector3(4f, 6f, -8f));

            Vector3 expected = RbxSpace.ToUnity(new RbxVector3(4f, 6f, -8f));
            Assert.Less((go.transform.position - expected).magnitude, Epsilon, "the pose write moves the prop");
            Assert.Less((go.transform.lossyScale - new Vector3(2f, 2f, 2f)).magnitude, Epsilon,
                "a position write must not write the adopted Size back as localScale and double the prop");
            Assert.Less((go.transform.localScale - Vector3.one).magnitude, Epsilon);
        }

        [Test]
        public void AdoptedHostObject_ShapeAnchoredAndAppearanceWrites_AreRefused_HostComponentsSurvive()
        {
            WarningLog log = new();
            _binder.SetLog(log);
            GameObject go = CreateHostCube("AdoptRefusalProbe");
            Rigidbody body = go.AddComponent<Rigidbody>();
            BoxCollider box = go.GetComponent<BoxCollider>();
            MeshFilter filter = go.GetComponent<MeshFilter>();
            Mesh hostMesh = filter.sharedMesh;
            MeshRenderer renderer = go.GetComponent<MeshRenderer>();
            Material hostMaterial = renderer.sharedMaterial;
            Vector3 hostScale = go.transform.localScale;
            RbxInstance part = _registry.Create("Part");
            _binder.AdoptWorldObject(part.Id, go);

            _binder.SetShape(part.Id, RbxPartShape.Ball);
            _binder.SetAnchored(part.Id, true);
            _binder.SetColor(part.Id, RbxColor3.FromRGB(255f, 0f, 0f));
            _binder.SetTransparency(part.Id, 1f);
            _binder.SetMaterialVariant(part.Id, "HostProbeVariant");
            _binder.SetCanCollide(part.Id, false);
            _binder.SetSize(part.Id, new RbxVector3(10f, 10f, 10f));

            Assert.IsTrue(box != null && box == go.GetComponent<BoxCollider>(),
                "a Shape write must not strip the host's collider");
            Assert.IsNull(go.GetComponent<SphereCollider>(), "nor build a binder collider on the host object");
            Assert.IsTrue(filter != null && hostMesh == filter.sharedMesh,
                "nor swap the host's mesh");
            Assert.IsTrue(body != null && body == go.GetComponent<Rigidbody>(),
                "Anchored=true must not destroy the host's own Rigidbody");
            Assert.IsTrue(renderer != null && hostMaterial == renderer.sharedMaterial,
                "an appearance write must not replace the host's material");
            Assert.IsTrue(renderer.enabled, "Transparency must not hide the host's renderer");
            Assert.IsTrue(box.enabled && !box.isTrigger, "CanCollide must not change the host's collider");
            Assert.Less((go.transform.localScale - hostScale).magnitude, Epsilon,
                "a Size write must not rescale the host object");

            PartProperties stored = _binder.GetPartPropertiesOrDefault(part.Id);
            Assert.AreEqual(RbxPartShape.Ball, stored.Shape, "the instance still reads back what was written");
            Assert.IsTrue(stored.Anchored);
            Assert.AreEqual(1, log.Warnings.Count,
                "the refusal is reported once per adopted object, not once per write");
            StringAssert.Contains("AdoptRefusalProbe", log.Warnings[0]);
        }

        [Test]
        public void AdoptedHostObject_ShapeWrite_DoesNotSnapAPropTheHostMovedBack()
        {
            GameObject go = CreateHostCube("AdoptSnapProbe");
            go.transform.position = new Vector3(1f, 2f, 3f);
            RbxInstance part = _registry.Create("Part");
            _binder.AdoptWorldObject(part.Id, go);
            Vector3 movedByHost = new(-4f, 5f, 6f);
            go.transform.position = movedByHost;

            _binder.SetShape(part.Id, RbxPartShape.Ball);

            Assert.Less((go.transform.position - movedByHost).magnitude, Epsilon,
                "a non-pose write must not re-apply the pose stored at adoption time");
        }

        [Test]
        public void AdoptedHostObject_RigidbodyIsNotCollectedForWorldGravity()
        {
            GameObject go = new("AdoptGravityProbe");
            _created.Add(go);
            Rigidbody hostBody = go.AddComponent<Rigidbody>();
            RbxInstance adopted = _registry.Create("Part");
            _binder.AdoptWorldObject(adopted.Id, go);
            RbxInstance owned = _registry.Create("Part");
            owned.Parent = _registry.WorldRoot;
            Assert.IsTrue(_binder.TryGetBoundObject(owned.Id, out GameObject ownedGo));

            List<Rigidbody> bodies = new();
            _binder.CollectSimulatedBodies(bodies);

            CollectionAssert.DoesNotContain(bodies, hostBody,
                "DEV-6: the world's gravity must never reach a host-owned body");
            CollectionAssert.Contains(bodies, ownedGo.GetComponent<Rigidbody>(),
                "an unanchored part the binder built is still simulated");
        }

        [Test]
        public void AdoptedHostObject_Destroy_RemovesOnlyTheRelay_AndForgetsTheObject()
        {
            GameObject go = CreateHostCube("AdoptReleaseProbe");
            RbxInstance part = _registry.Create("Part");
            _binder.AdoptWorldObject(part.Id, go);
            Assert.IsNotNull(go.GetComponent<RbxContactRelay>(), "precondition: adoption adds a contact relay");

            part.Destroy();

            Assert.IsTrue(go != null, "the host object is the host's and survives its wrapper");
            Assert.IsNull(go.GetComponent<RbxContactRelay>(),
                "the relay is the one thing the binder added, so it leaves with the wrapper");
            Assert.IsNotNull(go.GetComponent<BoxCollider>());
            Assert.IsNotNull(go.GetComponent<MeshRenderer>());
            Assert.IsFalse(_binder.TryGetInstanceId(go, out _), "the reverse map forgets the released object");
            Assert.IsFalse(_binder.TryResolvePartInstanceId(go, out _));
        }

        [Test]
        public void AdoptWorldObject_TriggerCollider_ReadsAsCanCollideFalse()
        {
            GameObject go = CreateHostCube("AdoptTriggerProbe");
            go.GetComponent<BoxCollider>().isTrigger = true;
            RbxInstance part = _registry.Create("Part");

            _binder.AdoptWorldObject(part.Id, go);

            Assert.IsFalse(_binder.GetPartPropertiesOrDefault(part.Id).CanCollide,
                "a host trigger volume lets bodies through, which is what CanCollide=false means");
        }

        [Test]
        public void WorldNameLookup_RefusesTheBindersOwnGameObjects()
        {
            RbxInstance cylinder = _registry.Create("Part");
            cylinder.Parent = _registry.WorldRoot;
            _binder.SetShape(cylinder.Id, RbxPartShape.Cylinder);
            Assert.IsTrue(_binder.TryGetBoundObject(cylinder.Id, out GameObject cylinderGo));
            Transform shapeChild = cylinderGo.transform.Find("Shape");
            Assert.IsNotNull(shapeChild, "precondition: a Cylinder builds a Shape child");
            // WHY a unique name: the lookup walks the whole open scene, so a plain "Shape" could match
            // an unrelated object first and prove nothing.
            shapeChild.name = "BinderOwnedShapeProbe_" + System.Guid.NewGuid().ToString("N");
            int countBefore = _registry.Count;
            WorldInstanceAdapter adapter = new(_binder);

            bool wrapped = adapter.TryWrap(_registry, shapeChild.name, out RbxInstance instance);

            Assert.IsFalse(wrapped, "a binder-built mesh child is part of the Cylinder, not a world object");
            Assert.IsNull(instance);
            Assert.AreEqual(countBefore, _registry.Count, "no registry record is created for it");
            Assert.AreSame(cylinderGo.transform, shapeChild.parent, "the Shape child stays with its Cylinder");
            Assert.IsTrue(_binder.IsInsideBinderOwnedBacking(shapeChild.gameObject));
            Assert.Throws<System.InvalidOperationException>(
                () => _binder.AdoptWorldObject(_registry.Create("Part").Id, shapeChild.gameObject),
                "direct adoption of binder internals is refused as well");
        }

        [Test]
        public void Negative_WorldNameLookup_StillAdoptsAHostSceneObject()
        {
            GameObject go = CreateHostCube("AdoptLookupProbe_" + System.Guid.NewGuid().ToString("N"));
            WorldInstanceAdapter adapter = new(_binder);

            Assert.IsFalse(_binder.IsInsideBinderOwnedBacking(go));
            Assert.IsTrue(adapter.TryWrap(_registry, go.name, out RbxInstance instance),
                "a host object at the scene root is exactly what the lookup exists to adopt");
            Assert.IsTrue(_binder.TryGetBoundObject(instance.Id, out GameObject backing));
            Assert.AreSame(go, backing, "adopted, not duplicated");
        }

        /// <summary>Keeps the warnings the binder reports; everything else is dropped.</summary>
        private sealed class WarningLog : ILog
        {
            public List<string> Warnings { get; } = new();

            public void Debug(string message, string tag = null)
            {
            }

            public void Info(string message, string tag = null)
            {
            }

            public void Warn(string message, string tag = null)
            {
                Warnings.Add(message ?? string.Empty);
            }

            public void Error(string message, string tag = null)
            {
            }
        }
    }
}
