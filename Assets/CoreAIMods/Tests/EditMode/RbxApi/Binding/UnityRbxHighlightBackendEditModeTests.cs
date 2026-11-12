using System.Collections.Generic;
using CoreAI.Mods.Rbx.Binding;
using CoreAI.Mods.Rbx.Datatypes;
using CoreAI.Mods.Rbx.Instances;
using CoreAI.Mods.Rbx.Instances.Adornments;
using CoreAI.Mods.Rbx.Spatial;
using NUnit.Framework;
using UnityEngine;

namespace CoreAI.Tests.EditMode.RbxApi.Binding
{
    /// <summary>
    /// The Unity drawing of <c>Highlight</c>: the outline mode draws a fill and an outline over each covered part's own
    /// mesh at the part's pose, the wireframe fallback (forced here, chosen on its own where the outline shader is not
    /// supported) draws twelve edges of each part's box in the outline colour and no fill, and neither creates an object
    /// or a collider. The outline shader sits under a Resources folder, so every player build carries it.
    /// </summary>
    [TestFixture]
    public sealed class UnityRbxHighlightBackendEditModeTests
    {
        private GameObject _root;
        private InstanceGameObjectBinder _binder;
        private InstanceRegistry _registry;
        private RbxDataModel _game;
        private RbxInstance _part;

        [SetUp]
        public void SetUp()
        {
            RbxSpace.ResetForTests();
            _root = new GameObject("HighlightBackendTestRoot");
            _binder = new InstanceGameObjectBinder(_root.transform);
            _registry = new InstanceRegistry(null, _binder, worldInstanceAdapter: new WorldInstanceAdapter(_binder));
            _game = DataModelBootstrap.CreateGame(_registry);
            _part = _registry.Create("Part");
            _part.Parent = _registry.WorldRoot;
            _binder.SetSize(_part.Id, new RbxVector3(4f, 2f, 6f));
            _binder.SetPosition(_part.Id, new RbxVector3(10f, 3f, -5f));
        }

        [TearDown]
        public void TearDown()
        {
            _game.Destroy();
            Object.DestroyImmediate(_root);
            RbxSpace.ResetForTests();
        }

        private RbxHighlightRenderState State(RbxHighlightDepthMode depth = RbxHighlightDepthMode.AlwaysOnTop)
        {
            return new RbxHighlightRenderState(new[] { _part.Id }, depth, new RbxColor3(1f, 0f, 0f), 0.5f,
                new RbxColor3(1f, 1f, 0f), 0.25f);
        }

        [Test]
        public void TheOutlineShader_IsInAResourcesFolder()
        {
            Assert.IsNotNull(Shader.Find(UnityRbxHighlightBackend.ShaderName),
                "Shader.Find resolves it, so a player build includes it");
        }

        [Test]
        public void OutlineMode_DrawsAFillAndAnOutlineOverThePartsOwnMesh()
        {
            Shader shader = Shader.Find(UnityRbxHighlightBackend.ShaderName);
            if (shader == null || !shader.isSupported)
            {
                Assert.Ignore("the outline shader is not supported by this graphics device; the wireframe test covers it");
            }

            using UnityRbxHighlightBackend backend = new(_binder);
            Assert.AreEqual(RbxHighlightDrawMode.Outline, backend.Mode);
            backend.Apply(new InstanceId(900), State());
            backend.Draw(submit: false);

            Assert.IsTrue(_binder.TryGetPartRenderer(_part.Id, out Renderer renderer));
            IReadOnlyList<RbxHighlightDraw> frame = backend.LastFrame;
            Assert.AreEqual(2, frame.Count);
            Assert.IsFalse(frame[0].IsOutline);
            Assert.IsTrue(frame[1].IsOutline);
            Assert.AreEqual(renderer.localToWorldMatrix, frame[0].Matrix, "drawn at the part's own pose");
            Assert.AreEqual(new Color(1f, 0f, 0f, 0.5f), frame[0].Color);
            Assert.AreEqual(new Color(1f, 1f, 0f, 0.75f), frame[1].Color);
            Assert.AreEqual(0, _root.GetComponentsInChildren<Collider>(true).Length - 1,
                "only the part's own collider exists; the highlight adds none");
        }

        [Test]
        public void WireframeFallback_DrawsTwelveEdgesOfThePartsBox_InTheOutlineColour_AndNoFill()
        {
            int objectsBefore = _root.GetComponentsInChildren<Transform>(true).Length;
            using UnityRbxHighlightBackend backend = new(_binder, forceWireframe: true);
            Assert.AreEqual(RbxHighlightDrawMode.Wireframe, backend.Mode);
            backend.Apply(new InstanceId(901), State(RbxHighlightDepthMode.Occluded));
            backend.Draw(submit: false);

            IReadOnlyList<RbxHighlightDraw> frame = backend.LastFrame;
            Assert.AreEqual(12, frame.Count);
            Vector3 centre = RbxSpace.ToUnityPose(RbxCFrame.FromPosition(10f, 3f, -5f)).Item1;
            Vector3 half = RbxSpace.SizeToUnity(new RbxVector3(4f, 2f, 6f)) * 0.5f;
            int onCorners = 0;
            for (int index = 0; index < frame.Count; index++)
            {
                Assert.IsTrue(frame[index].IsOutline, "the fallback draws no fill");
                Assert.AreEqual(new Color(1f, 1f, 0f, 0.75f), frame[index].Color);
                Vector3 offset = (Vector3)frame[index].Matrix.GetColumn(3) - centre;
                int onFaces = 0;
                onFaces += Mathf.Abs(Mathf.Abs(offset.x) - half.x) < 1e-3f ? 1 : 0;
                onFaces += Mathf.Abs(Mathf.Abs(offset.y) - half.y) < 1e-3f ? 1 : 0;
                onFaces += Mathf.Abs(Mathf.Abs(offset.z) - half.z) < 1e-3f ? 1 : 0;
                onCorners += onFaces == 2 ? 1 : 0;
            }

            Assert.AreEqual(12, onCorners, "every edge runs along the box where two of its faces meet");
            Assert.AreEqual(objectsBefore, _root.GetComponentsInChildren<Transform>(true).Length,
                "the fallback creates no object under the world");
        }

        [Test]
        public void Negative_AReleasedHighlight_DrawsNothing()
        {
            using UnityRbxHighlightBackend backend = new(_binder, forceWireframe: true);
            InstanceId id = new(902);
            backend.Apply(id, State());
            backend.Release(id);
            backend.Draw(submit: false);

            Assert.AreEqual(0, backend.Count);
            Assert.AreEqual(0, backend.LastFrame.Count);
        }
    }
}
