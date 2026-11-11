using System;
using CoreAI.Look;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode
{
    /// <summary>
    /// Engine-free contract of <see cref="ProceduralSkyHaze"/>: the ground colour it returns makes the procedural sky
    /// below the horizon equal the haze above it at any sun position, the view further down stays close to the haze,
    /// the old fixed ground colour does not (twin: the brown floor), the haze is pale blue at noon, warmer at dusk and
    /// dark after sunset, and the result is finite for any sun. Engine-free like the class it tests: it must never
    /// touch UnityEngine.
    /// </summary>
    public sealed class ProceduralSkyHazeEditModeTests
    {
        private const float TintR = 0.52f;
        private const float TintG = 0.58f;
        private const float TintB = 0.66f;
        private const float Thickness = 0.9f;

        [TestCase(70f)]
        [TestCase(40f)]
        [TestCase(15f)]
        [TestCase(4f)]
        public void TheGroundColour_ContinuesTheHazeBelowTheHorizon(float sunDegrees)
        {
            Sun(sunDegrees, out float x, out float y, out float z);
            ProceduralSkyHaze.HorizonHaze(x, y, z, TintR, TintG, TintB, Thickness, out float hazeR, out float hazeG,
                out float hazeB);
            ProceduralSkyHaze.GroundColorMatchingHaze(x, y, z, TintR, TintG, TintB, Thickness, out float groundR,
                out float groundG, out float groundB);
            Below(x, y, z, ProceduralSkyHaze.GroundElevation, groundR, groundG, groundB, out float r, out float g,
                out float b);
            Assert.AreEqual(hazeR, r, hazeR * 0.03f + 0.002f, "red just below the horizon is the haze's");
            Assert.AreEqual(hazeG, g, hazeG * 0.03f + 0.002f, "green just below the horizon is the haze's");
            Assert.AreEqual(hazeB, b, hazeB * 0.03f + 0.002f, "blue just below the horizon is the haze's");
            Below(x, y, z, -0.3f, groundR, groundG, groundB, out float deepR, out float deepG, out float deepB);
            Assert.AreEqual(hazeG, deepG, hazeG * 0.12f + 0.002f, "seventeen degrees down it is still about the haze");
            Assert.AreEqual(hazeB, deepB, hazeB * 0.12f + 0.002f);
            Assert.AreEqual(hazeR, deepR, hazeR * 0.12f + 0.002f);
        }

        [Test]
        public void TheOldFixedGroundColour_DrawsADarkBrownFloorUnderTheHaze_NegativeTwin()
        {
            Sun(40f, out float x, out float y, out float z);
            ProceduralSkyHaze.HorizonHaze(x, y, z, TintR, TintG, TintB, Thickness, out float hazeR, out float hazeG,
                out float hazeB);
            // sRGB (0.38, 0.36, 0.34), the profile's ground colour, in linear.
            Below(x, y, z, ProceduralSkyHaze.GroundElevation, 0.1195f, 0.107f, 0.0953f, out float r, out float g,
                out float b);
            Assert.Less(g, hazeG * 0.6f, "the fixed ground colour is far darker than the haze");
            Assert.Greater(r / Math.Max(b, 1e-4f), hazeR / hazeB * 1.3f, "and warmer: a brown floor under a blue haze");
        }

        [Test]
        public void TheHaze_IsPaleBlueAtNoon_WarmerAtDusk_AndDarkAtNight()
        {
            Sun(60f, out float x, out float y, out float z);
            ProceduralSkyHaze.HorizonHaze(x, y, z, TintR, TintG, TintB, Thickness, out float noonR, out float noonG,
                out float noonB);
            Assert.GreaterOrEqual(noonB, noonR, "the noon haze is bluish");
            Assert.Greater(noonG, 0.3f, "and bright");
            Sun(2f, out x, out y, out z);
            ProceduralSkyHaze.HorizonHaze(x, y, z, TintR, TintG, TintB, Thickness, out float duskR, out float duskG,
                out float duskB);
            Assert.Greater(duskR / duskB, noonR / noonB * 1.2f, "the dusk haze is warmer");
            Sun(-25f, out x, out y, out z);
            ProceduralSkyHaze.HorizonHaze(x, y, z, TintR, TintG, TintB, Thickness, out float nightR, out float nightG,
                out float nightB);
            Assert.Less(nightG, noonG * 0.1f, "long after sunset the horizon is dark");
        }

        [Test]
        public void AnySun_GivesAFiniteGroundColourWithinItsLimit()
        {
            for (float degrees = -90f; degrees <= 90f; degrees += 7.5f)
            {
                Sun(degrees, out float x, out float y, out float z);
                ProceduralSkyHaze.GroundColorMatchingHaze(x, y, z, TintR, TintG, TintB, Thickness, out float r,
                    out float g, out float b);
                foreach (float channel in new[] { r, g, b })
                {
                    Assert.IsFalse(float.IsNaN(channel) || float.IsInfinity(channel), degrees + " degrees");
                    Assert.GreaterOrEqual(channel, 0f, degrees + " degrees");
                    Assert.LessOrEqual(channel, ProceduralSkyHaze.MaxGroundChannel, degrees + " degrees");
                }
            }

            ProceduralSkyHaze.GroundColorMatchingHaze(0f, 0f, 0f, TintR, TintG, TintB, Thickness, out float zeroR,
                out float _, out float _);
            Assert.IsFalse(float.IsNaN(zeroR), "a zero sun direction is read as straight up, not NaN");
        }

        private static void Sun(float degrees, out float x, out float y, out float z)
        {
            double radians = degrees * Math.PI / 180.0;
            x = (float)Math.Cos(radians) * 0.96f;
            y = (float)Math.Sin(radians);
            z = (float)Math.Cos(radians) * 0.28f;
        }

        private static void Below(float sunX, float sunY, float sunZ, float elevation, float groundR, float groundG,
            float groundB, out float red, out float green, out float blue)
        {
            red = 0f;
            green = 0f;
            blue = 0f;
            for (int index = 0; index < 8; index++)
            {
                double angle = index * Math.PI / 4.0;
                float horizontal = (float)Math.Sqrt(1f - elevation * elevation);
                ProceduralSkyHaze.SkyColor((float)Math.Cos(angle) * horizontal, elevation,
                    (float)Math.Sin(angle) * horizontal, sunX, sunY, sunZ, TintR, TintG, TintB, Thickness, groundR,
                    groundG, groundB, out float r, out float g, out float b);
                red += r / 8f;
                green += g / 8f;
                blue += b / 8f;
            }
        }
    }
}
