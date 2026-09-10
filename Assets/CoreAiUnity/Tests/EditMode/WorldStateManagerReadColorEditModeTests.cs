using CoreAI.Infrastructure.World;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools.Constraints;
using Is = UnityEngine.TestTools.Constraints.Is;

namespace CoreAI.Tests.EditMode
{
    /// <summary>
    /// <see cref="WorldStateManager.Save"/> reads every tracked object's colour override once a minute
    /// (auto-save) and on every quit. The read used to allocate a fresh <see cref="MaterialPropertyBlock"/>
    /// - a native-backed object - per object per save; now the caller hands in one scratch block and the
    /// read allocates nothing. The result must be indistinguishable from the fresh-block version.
    /// </summary>
    [Category("World")]
    public sealed class WorldStateManagerReadColorEditModeTests
    {
        private GameObject _cube;
        private GameObject _empty;

        [SetUp]
        public void SetUp()
        {
            _cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
            _cube.name = "WorldStateManagerReadColorEditModeTests.Cube";
            _empty = new GameObject("WorldStateManagerReadColorEditModeTests.Empty");
        }

        [TearDown]
        public void TearDown()
        {
            Object.DestroyImmediate(_cube);
            Object.DestroyImmediate(_empty);
        }

        [Test]
        public void NoRenderer_ReportsNoColor()
        {
            Color color = WorldStateManager.ReadColor(_empty, new MaterialPropertyBlock());

            Assert.Less(color.r, 0f, "An object without a renderer has no colour override to persist.");
        }

        [Test]
        public void RendererWithOverride_ReturnsTheOverride()
        {
            Renderer renderer = _cube.GetComponent<Renderer>();
            MaterialPropertyBlock authored = new();
            authored.SetColor("_Color", new Color(0.25f, 0.5f, 0.75f, 1f));
            renderer.SetPropertyBlock(authored);

            Color color = WorldStateManager.ReadColor(_cube, new MaterialPropertyBlock());

            // WHY per-channel with a tolerance and not Assert.AreEqual(Color, Color): the colour makes
            // a round trip through the shader property block as float32, so the value that comes back
            // is bit-inexact. Equality on the struct then fails with a message that prints the two
            // sides IDENTICALLY - "RGBA(0.250, 0.500, 0.750, 1.000)" against itself - which is a
            // uniquely unhelpful way to spend twenty minutes.
            Assert.AreEqual(0.25f, color.r, 1e-4f, "red channel");
            Assert.AreEqual(0.5f, color.g, 1e-4f, "green channel");
            Assert.AreEqual(0.75f, color.b, 1e-4f, "blue channel");
            Assert.AreEqual(1f, color.a, 1e-4f, "alpha channel");
        }

        [Test]
        public void ReusedScratch_DoesNotLeakThePreviousObjectsColour()
        {
            // A scratch block that still holds the previous object's override would report that colour
            // for an object that has none - the fresh-block shape never could.
            Renderer renderer = _cube.GetComponent<Renderer>();
            MaterialPropertyBlock authored = new();
            authored.SetColor("_Color", Color.red);
            renderer.SetPropertyBlock(authored);

            GameObject plainCube = GameObject.CreatePrimitive(PrimitiveType.Cube);
            try
            {
                MaterialPropertyBlock scratch = new();
                Color first = WorldStateManager.ReadColor(_cube, scratch);
                Color second = WorldStateManager.ReadColor(plainCube, scratch);

                Assert.AreEqual(Color.red, first);
                Assert.Less(second.r, 0f, "The scratch block must be cleared before reading the next renderer.");
            }
            finally
            {
                Object.DestroyImmediate(plainCube);
            }
        }

        [Test]
        public void ReadColor_WithScratchBlock_DoesNotAllocateGcMemory()
        {
            Renderer renderer = _cube.GetComponent<Renderer>();
            MaterialPropertyBlock authored = new();
            authored.SetColor("_BaseColor", Color.green);
            renderer.SetPropertyBlock(authored);

            MaterialPropertyBlock scratch = new();
            WorldStateManager.ReadColor(_cube, scratch); // warm-up

            Assert.That(() =>
            {
                for (int i = 0; i < 200; i++)
                {
                    WorldStateManager.ReadColor(_cube, scratch);
                }
            }, Is.Not.AllocatingGCMemory(), "One save pass reads every world object; the read must not allocate per object.");
        }
    }
}
