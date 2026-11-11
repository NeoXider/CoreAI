using CoreAI.Mods.Rbx.Binding;
using CoreAI.Mods.Rbx.Datatypes;
using CoreAI.Mods.Rbx.Instances;
using CoreAI.Mods.Rbx.Instances.Adornments;
using NUnit.Framework;
using UnityEngine;

namespace CoreAI.Tests.EditMode.RbxApi.Binding
{
    /// <summary>
    /// The Unity drawing of <c>SelectionBox</c>: twelve thin edges and a face box without a single collider, so no ray,
    /// click or camera pull-in stops on it; faces only below SurfaceTransparency 1; released boxes leave no object.
    /// </summary>
    [TestFixture]
    public sealed class UnityRbxSelectionBoxBackendEditModeTests
    {
        private UnityRbxSelectionBoxBackend _backend;

        [SetUp]
        public void SetUp()
        {
            _backend = new UnityRbxSelectionBoxBackend();
        }

        [TearDown]
        public void TearDown()
        {
            _backend.Dispose();
        }

        private static RbxSelectionBoxRenderState State(float surfaceTransparency)
        {
            return new RbxSelectionBoxRenderState(RbxCFrame.FromPosition(0f, 2f, 0f), new RbxVector3(4f, 4f, 4f),
                new RbxColor3(1f, 1f, 0f), 0f, 0.15f, RbxGuiBase3d.DefaultColor, surfaceTransparency);
        }

        [Test]
        public void ADrawnBox_HasTwelveEdgesAndAFaceBox_AndNoCollider()
        {
            InstanceId id = new(7);
            _backend.Apply(id, State(1f));

            GameObject drawn = _backend.GetDrawn(id);
            Assert.IsNotNull(drawn);
            Assert.AreEqual(13, drawn.GetComponentsInChildren<MeshRenderer>(true).Length);
            Assert.AreEqual(0, drawn.GetComponentsInChildren<Collider>(true).Length, "never hit by a ray");
            Assert.IsFalse(drawn.transform.Find("Surface").GetComponent<MeshRenderer>().enabled,
                "SurfaceTransparency 1 draws no faces");
            Assert.IsTrue(drawn.transform.Find("Edge 0").GetComponent<MeshRenderer>().enabled);
        }

        [Test]
        public void Negative_ReleasingABox_LeavesNoObject_AndFacesShowBelowOne()
        {
            InstanceId id = new(8);
            _backend.Apply(id, State(0.5f));
            GameObject drawn = _backend.GetDrawn(id);
            Assert.IsTrue(drawn.transform.Find("Surface").GetComponent<MeshRenderer>().enabled,
                "twin: SurfaceTransparency 0.5 draws the faces");

            _backend.Release(id);

            Assert.IsNull(_backend.GetDrawn(id));
            Assert.AreEqual(0, _backend.Count);
            Assert.IsTrue(drawn == null, "the drawn object is destroyed");
        }
    }
}
