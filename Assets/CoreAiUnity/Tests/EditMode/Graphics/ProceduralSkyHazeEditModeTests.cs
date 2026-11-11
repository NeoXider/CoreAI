using System;
using CoreAI.Look;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode
{
    /// <summary>
    /// Engine-free contract of <see cref="ProceduralSkyHaze"/>: the ground colour it returns makes the procedural sky
    /// below the horizon equal the haze above it at any sun position, the view further down stays close to the haze,
    /// the old fixed ground colour does not (twin: the brown floor), the haze is pale blue at noon, warmer at dusk and
    /// dark after sunset, and the result is finite for any sun; the twilight sky is blue from late afternoon to
    /// nightfall where the authored one turns olive (twin), and keeps the authored sky by day. Engine-free like the
    /// class it tests: it must never touch UnityEngine.
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

        // WHY these suns: Lighting's sun at the default latitude at 17:30, 18:00, 18:36 and 19:15.
        [TestCase(5.6f)]
        [TestCase(0f)]
        [TestCase(-6.7f)]
        [TestCase(-13.9f)]
        public void TheTwilightSky_IsBlueNeverOlive_FromLateAfternoonToNightfall(float sunDegrees)
        {
            Sun(sunDegrees, out float x, out float y, out float z);
            ProceduralSkyHaze.TwilightSky(y, TintR, TintG, TintB, Thickness, out float r, out float g, out float b,
                out float thickness);
            Assert.LessOrEqual(GreenOverBlue(x, y, z, r, g, b, thickness), 0f,
                "no ray above the horizon band has more green than blue");
        }

        [Test]
        public void TheAuthoredSky_IsOliveAtDusk_NegativeTwin()
        {
            Sun(-6.7f, out float x, out float y, out float z);
            Assert.Greater(GreenOverBlue(x, y, z, TintR, TintG, TintB, Thickness), 0f,
                "without the twilight sky the sky over the horizon band is olive at 18:36");
        }

        [Test]
        public void TheTwilightSky_KeepsTheAuthoredSkyByDay_AndNeverThickensTheAtmosphere()
        {
            ProceduralSkyHaze.TwilightSky(ProceduralSkyHaze.TwilightFrom + 0.01f, TintR, TintG, TintB, Thickness,
                out float dayR, out float dayG, out float dayB, out float dayThickness);
            Assert.AreEqual(TintR, dayR, 1e-6f);
            Assert.AreEqual(TintG, dayG, 1e-6f);
            Assert.AreEqual(TintB, dayB, 1e-6f);
            Assert.AreEqual(Thickness, dayThickness, 1e-6f, "the day sky is the authored one");

            ProceduralSkyHaze.TwilightSky(ProceduralSkyHaze.TwilightFull, TintR, TintG, TintB, Thickness,
                out float duskR, out float duskG, out float duskB, out float duskThickness);
            Assert.AreEqual(ProceduralSkyHaze.TwilightTintR, duskR, 1e-6f);
            Assert.AreEqual(ProceduralSkyHaze.TwilightTintG, duskG, 1e-6f);
            Assert.AreEqual(ProceduralSkyHaze.TwilightTintB, duskB, 1e-6f);
            Assert.AreEqual(ProceduralSkyHaze.TwilightThickness, duskThickness, 1e-6f, "twilight just under the horizon");

            ProceduralSkyHaze.TwilightSky(-0.5f, TintR, TintG, TintB, 0.1f, out float _, out float _, out float _,
                out float thin);
            Assert.AreEqual(0.1f, thin, 1e-6f, "an atmosphere authored thinner than the night one is kept");
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

        /// <summary>
        /// The largest green-minus-blue (linear) over rays from just above the horizon dome's band (ten degrees) to the
        /// zenith, all round.
        /// </summary>
        private static float GreenOverBlue(float sunX, float sunY, float sunZ, float tintR, float tintG, float tintB,
            float thickness)
        {
            float worst = float.MinValue;
            for (float degrees = 10f; degrees <= 85f; degrees += 15f)
            {
                double elevation = degrees * Math.PI / 180.0;
                for (int index = 0; index < 16; index++)
                {
                    double angle = index * Math.PI / 8.0;
                    ProceduralSkyHaze.SkyColor((float)(Math.Cos(elevation) * Math.Cos(angle)), (float)Math.Sin(elevation),
                        (float)(Math.Cos(elevation) * Math.Sin(angle)), sunX, sunY, sunZ, tintR, tintG, tintB, thickness,
                        0f, 0f, 0f, out float _, out float g, out float b);
                    worst = Math.Max(worst, g - b);
                }
            }

            return worst;
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
