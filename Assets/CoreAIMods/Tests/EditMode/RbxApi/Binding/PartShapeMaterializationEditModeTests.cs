using CoreAI.Mods.Rbx.Binding;
using CoreAI.Mods.Rbx.Datatypes;
using CoreAI.Mods.Rbx.Spatial;
using CoreAI.Mods.Rbx.Instances;
using NUnit.Framework;
using UnityEngine;

namespace CoreAI.Tests.EditMode.RbxApi.Binding
{
    /// <summary>
    /// Part.Shape materialization (MVP1 shape slice): Ball maps to Unity's unit sphere and
    /// Block to the unit cube directly on the part GameObject; Cylinder corrects Unity's
    /// 2-unit-tall Y-axis mesh onto Roblox's X-axis stud-per-unit cylinder via a rotated,
    /// height-halved mesh child; Wedge and CornerWedge use custom normalized meshes on the root.
    /// Shape switches rebuild the visual while keeping the GameObject identity.
    /// </summary>
    [TestFixture]
    public sealed class PartShapeMaterializationEditModeTests
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
            _root = new GameObject("ShapeTestRoot");
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

        [Test]
        public void DefaultShape_IsBlock_CubeMeshOnTheRoot()
        {
            GameObject partGo = BoundObject(CreatePartInWorld());

            Assert.AreEqual("Cube", partGo.GetComponent<MeshFilter>().sharedMesh.name);
            Assert.IsNotNull(partGo.GetComponent<BoxCollider>());
            Assert.IsNull(partGo.transform.Find("Shape"), "Block needs no mesh child");
        }

        [Test]
        public void Ball_MaterializesUnitSphere_ScaledBySizeTimesMetersPerStud()
        {
            RbxInstance part = CreatePartInWorld();
            _binder.SetShape(part.Id, RbxPartShape.Ball);
            _binder.SetSize(part.Id, new RbxVector3(6f, 6f, 6f));

            GameObject partGo = BoundObject(part);
            Assert.AreEqual("Sphere", partGo.GetComponent<MeshFilter>().sharedMesh.name);
            Assert.IsNotNull(partGo.GetComponent<SphereCollider>());
            Assert.IsNull(partGo.GetComponent<BoxCollider>());
            // WHY: Unity's sphere mesh is 1 unit in diameter, so a 6-stud Ball at 0.28 m/stud
            // is localScale 1.68 on every axis — same Size-driven path as the cube.
            Vector3 scale = partGo.transform.localScale;
            Assert.AreEqual(1.68f, scale.x, Epsilon);
            Assert.AreEqual(1.68f, scale.y, Epsilon);
            Assert.AreEqual(1.68f, scale.z, Epsilon);
        }

        [Test]
        public void Cylinder_MeshChildRotatedOntoLocalX_WithHalvedHeight()
        {
            RbxInstance part = CreatePartInWorld();
            _binder.SetShape(part.Id, RbxPartShape.Cylinder);
            _binder.SetSize(part.Id, new RbxVector3(4f, 1f, 1f));

            GameObject partGo = BoundObject(part);
            Assert.IsNull(partGo.GetComponent<MeshFilter>(), "the cylinder mesh lives on a child");

            Transform child = partGo.transform.Find("Shape");
            Assert.IsNotNull(child, "Cylinder must materialize a Shape mesh child");
            Assert.AreEqual("Cylinder", child.GetComponent<MeshFilter>().sharedMesh.name);

            // WHY: Unity's cylinder is 2 units tall along local Y; the child maps mesh Y onto
            // the part's local X (Roblox's circular axis) and halves the height so 1 root unit
            // spans 1 stud of length.
            Vector3 meshAxisInPartSpace = child.localRotation * Vector3.up;
            Assert.AreEqual(1f, Mathf.Abs(meshAxisInPartSpace.x), Epsilon);
            Assert.AreEqual(0f, meshAxisInPartSpace.y, Epsilon);
            Assert.AreEqual(0f, meshAxisInPartSpace.z, Epsilon);
            Assert.AreEqual(1f, child.localScale.x, Epsilon);
            Assert.AreEqual(0.5f, child.localScale.y, Epsilon);
            Assert.AreEqual(1f, child.localScale.z, Epsilon);

            // Root carries Size * MetersPerStud like every shape: 4x1x1 studs -> 1.12x0.28x0.28.
            Vector3 scale = partGo.transform.localScale;
            Assert.AreEqual(1.12f, scale.x, Epsilon);
            Assert.AreEqual(0.28f, scale.y, Epsilon);
            Assert.AreEqual(0.28f, scale.z, Epsilon);

            // With an identity CFrame the circular axis points along Unity world X.
            Vector3 worldAxis = child.TransformDirection(Vector3.up);
            Assert.AreEqual(1f, Mathf.Abs(worldAxis.x), Epsilon);
            Assert.AreEqual(0f, worldAxis.y, Epsilon);
            Assert.AreEqual(0f, worldAxis.z, Epsilon);
        }

        [Test]
        public void Cylinder_AppearanceAndCanCollide_ReachTheMeshChild()
        {
            RbxInstance part = CreatePartInWorld();
            _binder.SetShape(part.Id, RbxPartShape.Cylinder);
            _binder.SetColor(part.Id, RbxColor3.FromRGB(255f, 0f, 0f));

            Transform child = BoundObject(part).transform.Find("Shape");
            Renderer renderer = child.GetComponent<Renderer>();
            MaterialPropertyBlock block = new();
            renderer.GetPropertyBlock(block);
            Assert.AreEqual(1f, block.GetColor("_Color").r, Epsilon);

            _binder.SetCanCollide(part.Id, false);
            Assert.IsFalse(child.GetComponent<Collider>().enabled);
        }

        [Test]
        public void Wedge_MaterializesCustomRampMesh_OnTheRootScaledBySize()
        {
            RbxInstance part = CreatePartInWorld();
            _binder.SetShape(part.Id, RbxPartShape.Wedge);
            _binder.SetSize(part.Id, new RbxVector3(2f, 4f, 6f));

            GameObject partGo = BoundObject(part);
            Mesh mesh = partGo.GetComponent<MeshFilter>().sharedMesh;
            Assert.AreEqual("CoreAiWedge", mesh.name, "Wedge uses its own normalized ramp mesh");
            Assert.IsNull(partGo.transform.Find("Shape"), "the wedge mesh lives on the root");

            // WHY: a right triangular prism authored 1 unit = 1 stud spans +-0.5 on every axis, so
            // the bounds are a unit cube and the Size-driven localScale carries the dimensions.
            Assert.AreEqual(1f, mesh.bounds.size.x, Epsilon);
            Assert.AreEqual(1f, mesh.bounds.size.y, Epsilon);
            Assert.AreEqual(1f, mesh.bounds.size.z, Epsilon);

            MeshCollider collider = partGo.GetComponent<MeshCollider>();
            Assert.IsNotNull(collider, "Wedge collides through a convex mesh collider");
            Assert.IsTrue(collider.convex);
            Assert.IsNull(partGo.GetComponent<BoxCollider>(), "no leftover box collider");

            Vector3 scale = partGo.transform.localScale;
            Assert.AreEqual(0.56f, scale.x, Epsilon);
            Assert.AreEqual(1.12f, scale.y, Epsilon);
            Assert.AreEqual(1.68f, scale.z, Epsilon);
        }

        [Test]
        public void Wedge_AppearanceAndCanCollide_ReachTheRoot()
        {
            RbxInstance part = CreatePartInWorld();
            _binder.SetShape(part.Id, RbxPartShape.Wedge);
            _binder.SetColor(part.Id, RbxColor3.FromRGB(0f, 0f, 255f));

            GameObject partGo = BoundObject(part);
            MaterialPropertyBlock block = new();
            partGo.GetComponent<Renderer>().GetPropertyBlock(block);
            Assert.AreEqual(1f, block.GetColor("_Color").b, Epsilon);

            _binder.SetCanCollide(part.Id, false);
            Assert.IsFalse(partGo.GetComponent<MeshCollider>().enabled);
        }

        [Test]
        public void CornerWedge_MaterializesCustomTwoSlopeMesh_WithExactConvexCollider()
        {
            RbxInstance corner = CreatePartInWorld();
            _binder.SetShape(corner.Id, RbxPartShape.CornerWedge);
            _binder.SetSize(corner.Id, new RbxVector3(2f, 4f, 6f));

            GameObject cornerGo = BoundObject(corner);
            Mesh mesh = cornerGo.GetComponent<MeshFilter>().sharedMesh;
            Assert.AreEqual("CoreAiCornerWedge", mesh.name);
            Assert.AreEqual(1f, mesh.bounds.size.x, Epsilon);
            Assert.AreEqual(1f, mesh.bounds.size.y, Epsilon);
            Assert.AreEqual(1f, mesh.bounds.size.z, Epsilon);
            Assert.AreEqual(18, mesh.triangles.Length,
                "a corner wedge has a quad base and four triangular faces, not a cube shell");

            Vector3 frontSlope = new Vector3(0f, 1f, 1f).normalized;
            Vector3 rightSlope = new Vector3(1f, 1f, 0f).normalized;
            bool hasFrontSlope = false;
            bool hasRightSlope = false;
            Vector3[] normals = mesh.normals;
            for (int i = 0; i < normals.Length; i++)
            {
                hasFrontSlope |= Vector3.Dot(normals[i], frontSlope) > 0.999f;
                hasRightSlope |= Vector3.Dot(normals[i], rightSlope) > 0.999f;
            }

            Assert.IsTrue(hasFrontSlope, "one face must slope toward local +Z");
            Assert.IsTrue(hasRightSlope, "one face must slope toward local +X");
            MeshCollider collider = cornerGo.GetComponent<MeshCollider>();
            Assert.IsNotNull(collider);
            Assert.IsTrue(collider.convex);
            Assert.AreSame(mesh, collider.sharedMesh);
            Assert.IsNull(cornerGo.GetComponent<BoxCollider>(), "CornerWedge must not collide as a block");

            Vector3 scale = cornerGo.transform.localScale;
            Assert.AreEqual(0.56f, scale.x, Epsilon);
            Assert.AreEqual(1.12f, scale.y, Epsilon);
            Assert.AreEqual(1.68f, scale.z, Epsilon);
        }

        [Test]
        public void CylinderAppearance_TargetsOwnVisual_NotANestedChildPart()
        {
            // WHY: a Cylinder keeps its visual on a "Shape" child; an unbounded GetComponentInChildren
            // would hit a nested child part's renderer instead. The cached visual ref must resolve
            // the part's OWN Shape child, so recoloring the parent never touches the child part.
            RbxInstance parent = CreatePartInWorld();
            RbxInstance child = _registry.Create("Part");
            child.Parent = parent;
            _binder.SetShape(parent.Id, RbxPartShape.Cylinder);
            // WHY: give the child a distinct known color so a bleed from the parent recolor is
            // unmistakable (a fresh part's default is medium grey, not zero).
            _binder.SetColor(child.Id, RbxColor3.FromRGB(0f, 255f, 0f));

            _binder.SetColor(parent.Id, RbxColor3.FromRGB(255f, 0f, 0f));

            Transform shapeChild = BoundObject(parent).transform.Find("Shape");
            MaterialPropertyBlock parentBlock = new();
            shapeChild.GetComponent<Renderer>().GetPropertyBlock(parentBlock);
            Assert.AreEqual(1f, parentBlock.GetColor("_Color").r, Epsilon,
                "the parent cylinder's own visual receives the color");

            MaterialPropertyBlock childBlock = new();
            BoundObject(child).GetComponent<Renderer>().GetPropertyBlock(childBlock);
            Color childColor = childBlock.GetColor("_Color");
            Assert.AreEqual(0f, childColor.r, Epsilon,
                "the nested child part must not pick up the parent's red");
            Assert.AreEqual(1f, childColor.g, Epsilon, "the child keeps its own green");
        }

        [Test]
        public void ShapeSwitch_RecachesVisualRefs_SoColorAndCollideHitTheNewVisual()
        {
            // WHY: after a shape switch the binder strips the old visual and builds a new one; the
            // cached Renderer/Collider must point at the NEW components, not the stripped ones.
            RbxInstance part = CreatePartInWorld();
            _binder.SetColor(part.Id, RbxColor3.FromRGB(255f, 0f, 0f));
            _binder.SetShape(part.Id, RbxPartShape.Ball);

            _binder.SetColor(part.Id, RbxColor3.FromRGB(0f, 255f, 0f));
            _binder.SetCanCollide(part.Id, false);

            GameObject partGo = BoundObject(part);
            MaterialPropertyBlock block = new();
            partGo.GetComponent<Renderer>().GetPropertyBlock(block);
            Assert.AreEqual(1f, block.GetColor("_Color").g, Epsilon,
                "color lands on the rebuilt sphere renderer");
            Assert.IsFalse(partGo.GetComponent<SphereCollider>().enabled,
                "CanCollide toggles the rebuilt sphere collider, not a stale box collider");
        }

        [Test]
        public void ShapeSwitch_DoesNotTouchAUserChildNamed_Shape()
        {
            // WHY: the binder must identify its Cylinder mesh child by an owned reference, NOT by the
            // name "Shape" — a mod can legally name one of its own child parts "Shape", and a name
            // lookup would destroy the user's object on a shape switch and cache its visual as the part's.
            RbxInstance parent = CreatePartInWorld();
            RbxInstance userChild = _registry.Create("Part");
            userChild.Name = "Shape";
            userChild.Parent = parent;
            _binder.SetColor(userChild.Id, RbxColor3.FromRGB(0f, 255f, 0f));

            _binder.SetShape(parent.Id, RbxPartShape.Cylinder);
            _binder.SetShape(parent.Id, RbxPartShape.Ball);
            _binder.SetColor(parent.Id, RbxColor3.FromRGB(255f, 0f, 0f));

            GameObject childGo = BoundObject(userChild);
            Assert.IsTrue(childGo != null, "the user's child named 'Shape' must survive shape switches");
            MaterialPropertyBlock childBlock = new();
            childGo.GetComponent<Renderer>().GetPropertyBlock(childBlock);
            Assert.AreEqual(0f, childBlock.GetColor("_Color").r, Epsilon,
                "the user child keeps its own green — the parent recolor targets the parent's own visual");
            Assert.AreEqual(1f, childBlock.GetColor("_Color").g, Epsilon);
        }

        [Test]
        public void TransformWrite_AfterShapeAndColor_KeepsAppearance()
        {
            // WHY: split apply — a CFrame/Size write must not wipe the previously applied color,
            // and a color write must not reset the transform.
            RbxInstance part = CreatePartInWorld();
            _binder.SetColor(part.Id, RbxColor3.FromRGB(0f, 0f, 255f));
            _binder.SetSize(part.Id, new RbxVector3(2f, 2f, 2f));

            GameObject partGo = BoundObject(part);
            MaterialPropertyBlock block = new();
            partGo.GetComponent<Renderer>().GetPropertyBlock(block);
            Assert.AreEqual(1f, block.GetColor("_Color").b, Epsilon,
                "a Size write leaves the color intact");
            Assert.AreEqual(0.56f, partGo.transform.localScale.x, Epsilon);
        }

        [Test]
        public void ShapeSwitch_RebuildsTheVisual_KeepingGameObjectAndAppearance()
        {
            RbxInstance part = CreatePartInWorld();
            GameObject partGo = BoundObject(part);
            _binder.SetColor(part.Id, RbxColor3.FromRGB(0f, 255f, 0f));
            Assert.AreEqual("Cube", partGo.GetComponent<MeshFilter>().sharedMesh.name);

            _binder.SetShape(part.Id, RbxPartShape.Ball);

            Assert.AreSame(partGo, BoundObject(part), "a Shape switch must keep the GameObject");
            Assert.AreEqual("Sphere", partGo.GetComponent<MeshFilter>().sharedMesh.name);
            Assert.IsNotNull(partGo.GetComponent<SphereCollider>());
            Assert.IsNull(partGo.GetComponent<BoxCollider>(), "the old collider is swapped out");

            MaterialPropertyBlock block = new();
            partGo.GetComponent<Renderer>().GetPropertyBlock(block);
            Assert.AreEqual(1f, block.GetColor("_Color").g, Epsilon,
                "appearance must be re-applied onto the rebuilt visual");

            _binder.SetShape(part.Id, RbxPartShape.Cylinder);
            Assert.IsNull(partGo.GetComponent<MeshFilter>());
            Assert.IsNotNull(partGo.transform.Find("Shape"));
        }
    }
}
