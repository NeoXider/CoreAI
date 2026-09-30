using System;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEngine;

namespace CoreAI.Tests.EditMode.RbxApi.Unity
{
    /// <summary>
    /// CPU ports of the textured shader's tangent frame, face unrolling, curved-surface weights and
    /// flat-face test, driven by the constants read from the shader source.
    /// </summary>
    /// <remarks>
    /// WHY ports instead of string pins: the defects these guard against were numeric. A 1e-6
    /// tangent-frame floor left every normal map at a few percent of its strength, a zero weight
    /// cutoff sampled three projections on a quarter of every ball, and a size term in the top-face
    /// UV broke the grid between floor plates of different sizes. Each of those passes a pin on the
    /// function name and fails a number. Only the constants are read from the source, so a reformat
    /// does not break the guard, while reverting a constant does.
    /// </remarks>
    [TestFixture]
    public sealed class RbxTexturedShaderMathEditModeTests
    {
        // WHY: 40-degree vertical field of view over 675 pixels, the seam sheet's camera; one pixel
        // then covers about 1.08 mm per metre of distance.
        private const float PixelPerMetre = 0.00108f;
        private const float UvScale = 1f / (10f * 0.28f);

        private static string _texturedSource;
        private static string _proceduralSource;

        [OneTimeSetUp]
        public void ReadSources()
        {
            string root = Path.Combine(Application.dataPath, "CoreAIMods", "Runtime", "RbxApi", "Unity",
                "Resources", "CoreAIRbxMaterials");
            _texturedSource = File.ReadAllText(Path.Combine(root, "RbxTexturedSurface.shader"));
            _proceduralSource = File.ReadAllText(Path.Combine(root, "RbxProceduralCommon.hlsl"));
        }

        [TestCase(0.25f)]
        [TestCase(2f)]
        [TestCase(10f)]
        [TestCase(60f)]
        [TestCase(300f)]
        public void TangentFrame_KeepsTheMappedTiltAtEveryDistance(float distance)
        {
            float floor = FrameFloor();
            float pixel = distance * PixelPerMetre;
            Vector3 normal = Vector3.forward;
            Vector3 normalTs = new(Mathf.Sin(30f * Mathf.Deg2Rad), 0f, Mathf.Cos(30f * Mathf.Deg2Rad));
            foreach (float obliqueness in new[] { 1f, 3f })
            {
                // A face seen square-on, then one foreshortened threefold along screen Y.
                Vector3 dpdx = new(pixel, 0f, 0f);
                Vector3 dpdy = new(0f, pixel * obliqueness, 0f);
                Vector2 duvdx = new(dpdx.x * UvScale, dpdx.y * UvScale);
                Vector2 duvdy = new(dpdy.x * UvScale, dpdy.y * UvScale);

                Vector3 mapped = NormalFromDerivatives(normal, dpdx, dpdy, duvdx, duvdy, normalTs, floor);

                float tilt = Vector3.Angle(normal, mapped);
                Assert.That(tilt, Is.EqualTo(30f).Within(1f),
                    "a 30-degree map normal must stay 30 degrees at " + distance + " m (floor " +
                    floor.ToString(CultureInfo.InvariantCulture) + ")");
            }
        }

        [Test]
        public void ProceduralHeightGradient_IsNotWeakenedUpClose()
        {
            float floor = ReadFloat(_proceduralSource,
                @"max\(\s*abs\(\s*determinant\s*\)\s*,\s*([-+0-9.eE]+)\s*\)");
            // WHY: the determinant is the squared pixel footprint. At 25 cm (0.27 mm pixels) the
            // old 1e-5 floor divided every procedural bump by 130.
            float pixel = 0.25f * PixelPerMetre;
            float determinant = pixel * pixel;
            float retained = determinant / Mathf.Max(determinant, floor);
            Assert.That(retained, Is.GreaterThan(0.999f));
        }

        [Test]
        public void FaceUv_ShowsEveryBlockAndWedgeFaceUprightAndUnmirrored()
        {
            Vector2 half = new(0.9f, 0.55f);
            Vector3 slope = new Vector3(0f, 1f, 1f).normalized;
            (Vector3 Normal, Vector3 Up, string Name)[] faces =
            {
                (Vector3.right, Vector3.up, "+X"), (Vector3.left, Vector3.up, "-X"),
                (Vector3.forward, Vector3.up, "+Z"), (Vector3.back, Vector3.up, "-Z"),
                (Vector3.up, Vector3.back, "top"), (Vector3.down, Vector3.forward, "bottom"),
                (slope, (Vector3.up - slope * slope.y).normalized, "wedge slope")
            };
            foreach ((Vector3 normal, Vector3 up, string name) in faces)
            {
                // Unity's camera: right = up x forward, with forward looking into the face.
                Vector3 right = Vector3.Cross(up, -normal);
                Vector3 point = normal * 0.3f + right * 0.05f + up * 0.05f;
                Vector2 origin = FaceUv(point, half, normal);
                Vector2 towardRight = FaceUv(point + right * 0.01f, half, normal) - origin;
                Vector2 towardUp = FaceUv(point + up * 0.01f, half, normal) - origin;

                Assert.That(towardRight.x, Is.GreaterThan(0.0025f), name + ": U must grow to the right");
                Assert.That(Mathf.Abs(towardRight.y), Is.LessThan(1e-5f), name + ": V must not follow U");
                Assert.That(towardUp.y, Is.GreaterThan(0.0025f), name + ": V must grow upward");
                Assert.That(Mathf.Abs(towardUp.x), Is.LessThan(1e-5f), name + ": U must not follow V");
                Assert.That(towardRight.magnitude, Is.EqualTo(0.01f * UvScale).Within(1e-5f),
                    name + ": the face frame must not stretch the map");
            }
        }

        [TestCase(1f, 1f)]
        [TestCase(3.7f, 0.4f)]
        [TestCase(0.25f, 11f)]
        public void FaceUv_ContinuesAcrossThreeVerticalEdges(float halfX, float halfZ)
        {
            Vector2 half = new(halfX, halfZ);
            (Vector3 First, Vector3 Second, Vector3 Corner)[] edges =
            {
                (Vector3.right, Vector3.forward, new Vector3(halfX, 0f, halfZ)),
                (Vector3.forward, Vector3.left, new Vector3(-halfX, 0f, halfZ)),
                (Vector3.left, Vector3.back, new Vector3(-halfX, 0f, -halfZ))
            };
            foreach ((Vector3 first, Vector3 second, Vector3 corner) in edges)
            {
                foreach (float height in new[] { -0.4f, 0f, 1.3f })
                {
                    Vector3 point = corner + Vector3.up * height;
                    Vector2 a = FaceUv(point, half, first);
                    Vector2 b = FaceUv(point, half, second);
                    Assert.That(WrappedDistance(a.x - b.x), Is.LessThan(1e-4f),
                        first + "/" + second + " edge: U jumps by " + (a.x - b.x));
                    Assert.That(Mathf.Abs(a.y - b.y), Is.LessThan(1e-5f));
                }
            }
        }

        [Test]
        public void FaceUv_TopAndBottomDependOnlyOnThePositionNotThePartSize()
        {
            Vector3 point = new(0.7f, 0.5f, -0.2f);
            foreach (Vector3 normal in new[] { Vector3.up, Vector3.down })
            {
                Vector2 small = FaceUv(point, new Vector2(1f, 1f), normal);
                Vector2 large = FaceUv(point, new Vector2(40f, 3f), normal);
                Assert.AreEqual(small.x, large.x, 1e-6f, "top/bottom U picked up a size term");
                Assert.AreEqual(small.y, large.y, 1e-6f, "top/bottom V picked up a size term");
            }
        }

        [Test]
        public void FaceUv_FoldsTheEdgeOffsetSoHugePartsKeepSmallUvs()
        {
            Vector2 half = new(280f, 280f);
            foreach (Vector3 normal in new[] { Vector3.right, Vector3.forward, Vector3.left, Vector3.back })
            {
                Vector2 centre = FaceUv(normal * 280f, half, normal);
                Assert.That(Mathf.Abs(centre.x), Is.LessThan(1f),
                    normal + ": the edge offset was not folded into one tile");
            }
        }

        [Test]
        public void CurvedWeights_BlendAboutElevenDegreesAndRarelyTakeThreeProjections()
        {
            float blendFloor = ReadConstant(_texturedSource, "RBX_CURVED_BLEND_FLOOR");
            float cutoff = ReadConstant(_texturedSource, "RBX_CURVED_WEIGHT_CUTOFF");
            const int samples = 20000;
            int three = 0;
            for (int i = 0; i < samples; i++)
            {
                // Fibonacci sphere: evenly spread directions without randomness.
                float y = 1f - 2f * (i + 0.5f) / samples;
                float radius = Mathf.Sqrt(1f - y * y);
                float angle = i * 2.39996323f;
                Vector3 direction = new(Mathf.Cos(angle) * radius, y, Mathf.Sin(angle) * radius);
                Vector3 weights = CurvedWeights(direction, blendFloor, cutoff);
                Assert.That(weights.x + weights.y + weights.z, Is.EqualTo(1f).Within(1e-4f));
                if (weights.x > 0f && weights.y > 0f && weights.z > 0f)
                {
                    three++;
                }
            }

            Assert.That(three / (float)samples, Is.LessThan(0.08f),
                "three projections must stay confined to the triple-axis corners");

            float lowest = 90f;
            float highest = 0f;
            for (float degrees = 0f; degrees <= 90f; degrees += 0.05f)
            {
                float radians = degrees * Mathf.Deg2Rad;
                Vector3 weights = CurvedWeights(new Vector3(Mathf.Cos(radians), 0f, Mathf.Sin(radians)),
                    blendFloor, cutoff);
                if (weights.x > 0.05f && weights.z > 0.05f)
                {
                    lowest = Mathf.Min(lowest, degrees);
                    highest = Mathf.Max(highest, degrees);
                }
            }

            Assert.That((highest - lowest) * 0.5f, Is.InRange(9f, 13f),
                "the documented blend band is about +-11 degrees around 45");
        }

        [Test]
        public void FlatFaceTest_KeepsBallsCurvedAtAnyResolutionAndFlatFacesFlat()
        {
            float radius = ReadConstant(_texturedSource, "RBX_FLAT_FACE_RADIUS");
            float rounding = ReadConstant(_texturedSource, "RBX_FLAT_FACE_ROUNDING");
            Vector3 direction = new Vector3(0.3f, 0.8f, 0.52f).normalized;
            foreach (float footprint in new[] { 0.0001f, 0.001f, 0.01f, 0.1f, 1f })
            {
                Vector3 positionChange = direction * footprint * 2f;
                foreach (float ballRadius in new[] { 0.5f, 4f, 100f })
                {
                    Vector3 normalChange = positionChange / ballRadius;
                    Assert.IsFalse(IsFlat(normalChange, positionChange, radius, rounding),
                        "a ball of radius " + ballRadius + " m read as flat at " + footprint + " m pixels");
                }

                // The largest Roblox ball (2048 studs) is curved from millimetre pixels up.
                if (footprint >= 0.001f)
                {
                    Assert.IsFalse(IsFlat(positionChange / 287f, positionChange, radius, rounding),
                        "a 2048-stud ball read as flat at " + footprint + " m pixels");
                }

                Vector3 roundingNoise = new(1e-7f, 1e-7f, 1e-7f);
                Assert.IsTrue(IsFlat(roundingNoise, positionChange, radius, rounding),
                    "a flat face read as curved at " + footprint + " m pixels");
            }
        }

        private static float FrameFloor()
        {
            return ReadFloat(_texturedSource,
                @"dot\(\s*bitangentWS\s*,\s*bitangentWS\s*\)\s*\)\s*,\s*([-+0-9.eE]+)\s*\)");
        }

        private static float ReadConstant(string source, string name)
        {
            return ReadFloat(source, @"static\s+const\s+(?:float|half)\s+" + name +
                                     @"\s*=\s*([-+0-9.eE]+)h?\s*;");
        }

        private static float ReadFloat(string source, string pattern)
        {
            Match match = Regex.Match(source, pattern);
            Assert.IsTrue(match.Success, "shader source no longer matches " + pattern);
            return float.Parse(match.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture);
        }

        private static float WrappedDistance(float value)
        {
            return Mathf.Abs(value - Mathf.Round(value));
        }

        /// <summary>Port of RbxNormalFromDerivatives.</summary>
        private static Vector3 NormalFromDerivatives(Vector3 normal, Vector3 dpdx, Vector3 dpdy,
            Vector2 duvdx, Vector2 duvdy, Vector3 normalTs, float floor)
        {
            Vector3 perpendicularX = Vector3.Cross(dpdy, normal);
            Vector3 perpendicularY = Vector3.Cross(normal, dpdx);
            Vector3 tangent = perpendicularX * duvdx.x + perpendicularY * duvdy.x;
            Vector3 bitangent = perpendicularX * duvdx.y + perpendicularY * duvdy.y;
            float inverseScale = 1f / Mathf.Sqrt(Mathf.Max(Mathf.Max(
                Vector3.Dot(tangent, tangent), Vector3.Dot(bitangent, bitangent)), floor));
            tangent *= inverseScale;
            bitangent *= inverseScale;
            return (tangent * normalTs.x + bitangent * normalTs.y + normal * normalTs.z).normalized;
        }

        /// <summary>Port of RbxFaceUv, with the texture scale applied.</summary>
        private static Vector2 FaceUv(Vector3 position, Vector2 halfExtentXZ, Vector3 faceNormal)
        {
            float horizontalLength = new Vector2(faceNormal.x, faceNormal.z).magnitude;
            Vector3 uAxis = new(-1f, 0f, 0f);
            Vector3 vAxis = new(0f, 0f, faceNormal.y >= 0f ? -1f : 1f);
            float uOffset = 0f;
            if (horizontalLength > 0.001f)
            {
                uAxis = new Vector3(-faceNormal.z, 0f, faceNormal.x) / horizontalLength;
                vAxis = (Vector3.up - faceNormal * faceNormal.y) / horizontalLength;
                float hx = halfExtentXZ.x;
                float hz = halfExtentXZ.y;
                if (Mathf.Abs(faceNormal.x) > Mathf.Abs(faceNormal.z))
                {
                    uOffset = faceNormal.x > 0f ? hz : 2f * hx + 3f * hz;
                }
                else
                {
                    uOffset = faceNormal.z > 0f ? hx + 2f * hz : 3f * hx + 4f * hz;
                }
            }

            float scaledOffset = uOffset * UvScale;
            float edgeOffset = scaledOffset - Mathf.Floor(scaledOffset);
            return new Vector2(Vector3.Dot(position, uAxis) * UvScale + edgeOffset,
                Vector3.Dot(position, vAxis) * UvScale);
        }

        /// <summary>Port of RbxCurvedAxisWeights.</summary>
        private static Vector3 CurvedWeights(Vector3 normal, float blendFloor, float cutoff)
        {
            Vector3 weights = new(Mathf.Max(Mathf.Abs(normal.x) - blendFloor, 0f),
                Mathf.Max(Mathf.Abs(normal.y) - blendFloor, 0f),
                Mathf.Max(Mathf.Abs(normal.z) - blendFloor, 0f));
            weights = Vector3.Scale(weights, weights);
            weights = Vector3.Scale(weights, weights);
            weights /= Mathf.Max(weights.x + weights.y + weights.z, 0.00001f);
            weights = new Vector3(weights.x >= cutoff ? weights.x : 0f,
                weights.y >= cutoff ? weights.y : 0f, weights.z >= cutoff ? weights.z : 0f);
            return weights / Mathf.Max(weights.x + weights.y + weights.z, 0.00001f);
        }

        /// <summary>Port of RbxIsFlatFace, with fwidth values passed in.</summary>
        private static bool IsFlat(Vector3 normalChange, Vector3 positionChange, float radius,
            float rounding)
        {
            float normalSum = Mathf.Abs(normalChange.x) + Mathf.Abs(normalChange.y) +
                              Mathf.Abs(normalChange.z);
            float positionSum = Mathf.Abs(positionChange.x) + Mathf.Abs(positionChange.y) +
                                Mathf.Abs(positionChange.z);
            return normalSum < rounding + positionSum / radius;
        }
    }
}
