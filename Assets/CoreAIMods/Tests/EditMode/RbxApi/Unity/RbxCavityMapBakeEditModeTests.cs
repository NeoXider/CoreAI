using System;
using CoreAI.Editor.RbxMaterials;
using NUnit.Framework;
using UnityEngine;

namespace CoreAI.Tests.EditMode.RbxApi.Unity
{
    /// <summary>
    /// The cavity bake turns a normal map into an occlusion map that is dark in grooves, white on
    /// open surface, tileable, and indifferent to the normal map's green-channel convention.
    /// </summary>
    [TestFixture]
    public sealed class RbxCavityMapBakeEditModeTests
    {
        private const int Size = 64;

        [Test]
        public void FlatNormalMap_BakesPureWhite()
        {
            Color32[] flat = new Color32[Size * Size];
            for (int i = 0; i < flat.Length; i++)
            {
                flat[i] = new Color32(128, 128, 255, 255);
            }

            byte[] occlusion = RbxCavityMapBake.Bake(flat, Size, Size, true, out int width,
                out int height);

            Assert.AreEqual(Size / 2, width);
            Assert.AreEqual(Size / 2, height);
            foreach (byte value in occlusion)
            {
                Assert.GreaterOrEqual(value, 254);
            }
        }

        [Test]
        public void Groove_IsDarkerThanTheOpenSurfaceAroundIt()
        {
            // A horizontal trough 8 texels wide and 3 texels deep, rows 28-35 of 64.
            byte[] occlusion = RbxCavityMapBake.Bake(GrooveNormals(true), Size, Size, true,
                out int width, out _);

            float groove = RowMean(occlusion, width, 16);
            float open = RowMean(occlusion, width, 2);
            Assert.That(groove, Is.LessThan(open - 60f),
                "groove " + groove + " must be clearly darker than the open surface " + open);
            Assert.That(open, Is.GreaterThan(230f), "open surface far from the groove stays white");
        }

        [Test]
        public void DirectXNormalMap_BakesTheSameMapAsItsOpenGlTwin()
        {
            byte[] openGl = RbxCavityMapBake.Bake(GrooveNormals(true), Size, Size, true, out _, out _);
            byte[] directX = RbxCavityMapBake.Bake(GrooveNormals(false), Size, Size, false, out _,
                out _);

            for (int i = 0; i < openGl.Length; i++)
            {
                Assert.That(Math.Abs(openGl[i] - directX[i]), Is.LessThanOrEqualTo(1), "texel " + i);
            }
        }

        [Test]
        public void NonPowerOfTwoMap_IsRejected()
        {
            Assert.Throws<ArgumentException>(() =>
                RbxCavityMapBake.Bake(new Color32[48 * 48], 48, 48, true, out _, out _));
        }

        private static float RowMean(byte[] occlusion, int width, int row)
        {
            float sum = 0f;
            for (int x = 0; x < width; x++)
            {
                sum += occlusion[row * width + x];
            }

            return sum / width;
        }

        /// <summary>Normals of a smooth horizontal trough; rows run bottom-up.</summary>
        private static Color32[] GrooveNormals(bool openGl)
        {
            Color32[] pixels = new Color32[Size * Size];
            for (int y = 0; y < Size; y++)
            {
                // Height: 0 on the plateau, -3 inside rows 28-35, with 2-texel ramps.
                float slope = 0f;
                if (y >= 26 && y < 28)
                {
                    slope = -1.5f;
                }
                else if (y >= 36 && y < 38)
                {
                    slope = 1.5f;
                }

                Vector3 normal = new Vector3(0f, -slope, 1f).normalized;
                float green = openGl ? normal.y : -normal.y;
                for (int x = 0; x < Size; x++)
                {
                    pixels[y * Size + x] = new Color32((byte)Mathf.RoundToInt((normal.x * 0.5f + 0.5f) * 255f),
                        (byte)Mathf.RoundToInt((green * 0.5f + 0.5f) * 255f),
                        (byte)Mathf.RoundToInt((normal.z * 0.5f + 0.5f) * 255f), 255);
                }
            }

            return pixels;
        }
    }
}
