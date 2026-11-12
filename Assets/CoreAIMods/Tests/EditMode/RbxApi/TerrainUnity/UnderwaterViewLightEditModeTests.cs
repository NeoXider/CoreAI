using System.Collections.Generic;
using CoreAI.Mods.Rbx.Binding;
using CoreAI.Mods.Rbx.Binding.Terrain;
using CoreAI.Mods.Rbx.Spatial;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Rendering;

namespace CoreAI.Tests.EditMode.RbxApi.TerrainUnity
{
    /// <summary>
    /// Unity EditMode: the underwater fog is as bright as the light that reaches the water. In open water at noon it is
    /// the full daylight colour, at midnight it is darker, in a flooded cave (no sky seen) it is near black at noon and at
    /// midnight alike, and leaving the water gives the world's fog back. The light is the Block World's: its noon and
    /// night sky ambient, its cave ambient and a sun of brightness 2 high in the sky.
    /// </summary>
    [TestFixture]
    public sealed class UnderwaterViewLightEditModeTests
    {
        private static readonly int OccludedAmbientId = Shader.PropertyToID(UnityRbxLightingSink.OccludedAmbientProperty);
        private static readonly Color NoonSky = new Color32(195, 196, 204, 255);
        private static readonly Color NightSky = new Color32(80, 100, 150, 255);
        private static readonly Color CaveAmbient = new Color32(22, 24, 34, 255);

        private readonly List<Object> _owned = new();
        private AmbientMode _savedMode;
        private Color _savedSky;
        private Light _savedSun;
        private Color _savedOccluded;
        private bool _savedFog;
        private Color _savedFogColor;
        private float _savedFogEnd;
        private Light _sun;

        [SetUp]
        public void SaveLighting()
        {
            RbxSpace.Configure(RbxSpace.DefaultMetersPerStud);
            _savedMode = RenderSettings.ambientMode;
            _savedSky = RenderSettings.ambientSkyColor;
            _savedSun = RenderSettings.sun;
            _savedOccluded = Shader.GetGlobalColor(OccludedAmbientId);
            _savedFog = RenderSettings.fog;
            _savedFogColor = RenderSettings.fogColor;
            _savedFogEnd = RenderSettings.fogEndDistance;
            GameObject sunObject = new("Underwater test sun");
            _owned.Add(sunObject);
            _sun = sunObject.AddComponent<Light>();
            _sun.type = LightType.Directional;
            _sun.color = Color.white;
            sunObject.transform.rotation = Quaternion.Euler(70f, 30f, 0f);
            RenderSettings.ambientMode = AmbientMode.Trilight;
            Shader.SetGlobalColor(OccludedAmbientId, CaveAmbient.linear);
        }

        [TearDown]
        public void RestoreLighting()
        {
            foreach (Object owned in _owned)
            {
                Object.DestroyImmediate(owned);
            }

            _owned.Clear();
            RenderSettings.ambientMode = _savedMode;
            RenderSettings.ambientSkyColor = _savedSky;
            RenderSettings.sun = _savedSun;
            Shader.SetGlobalColor(OccludedAmbientId, _savedOccluded);
            RenderSettings.fog = _savedFog;
            RenderSettings.fogColor = _savedFogColor;
            RenderSettings.fogEndDistance = _savedFogEnd;
        }

        [Test]
        public void TheFog_FollowsTheLight_OnTheWater_AndInAFloodedCaveItIsDarkAtNoonAndMidnight()
        {
            Color noonOpen = FogUnderWater(noon: true, skyExposure: 1f, out float noonOpenLevel);
            Color midnightOpen = FogUnderWater(noon: false, skyExposure: 1f, out float midnightOpenLevel);
            Color noonCave = FogUnderWater(noon: true, skyExposure: 0f, out float noonCaveLevel);
            Color midnightCave = FogUnderWater(noon: false, skyExposure: 0f, out float midnightCaveLevel);
            TestContext.WriteLine("light levels: open water noon " + noonOpenLevel + ", midnight " + midnightOpenLevel
                                  + "; flooded cave noon " + noonCaveLevel + ", midnight " + midnightCaveLevel
                                  + "; fog colours " + noonOpen + " " + midnightOpen + " " + noonCave + " " + midnightCave);

            Assert.AreEqual(RbxUnderwaterView.FogColor, noonOpen, "open water at noon keeps the daylight colour");
            Assert.Less(Brightness(midnightOpen), Brightness(noonOpen) * 0.6f, "open water at midnight is darker");
            Assert.Greater(Brightness(midnightOpen), Brightness(noonOpen) * 0.05f, "and still readable in the moonlight");
            Assert.Less(Brightness(noonCave), Brightness(noonOpen) * 0.2f, "a flooded cave is dark at noon");
            Assert.AreEqual(Brightness(noonCave), Brightness(midnightCave), 1e-4f, "as dark as at midnight");
            Assert.Greater(midnightOpen.b + midnightOpen.g, midnightOpen.r * 2f, "darker water is still blue-green");
        }

        [Test]
        public void LeavingTheWater_GivesTheWorldsFogBack()
        {
            RenderSettings.fog = true;
            RenderSettings.fogColor = new Color(0.8f, 0.6f, 0.4f);
            RenderSettings.fogEndDistance = 50f;
            RbxUnderwaterView view = new();
            AllWater water = new();
            view.Update(Vector3.zero, water, (x, y, z) => 0f);
            Assert.IsTrue(view.IsUnderwater);
            Assert.AreEqual(RbxUnderwaterView.FogColorAt(view.LightLevel), RenderSettings.fogColor);

            water.Wet = false;
            view.Update(Vector3.zero, water, (x, y, z) => 0f);
            Assert.IsFalse(view.IsUnderwater);
            Assert.AreEqual(new Color(0.8f, 0.6f, 0.4f), RenderSettings.fogColor, "the world's fog is back");
            Assert.AreEqual(50f, RenderSettings.fogEndDistance, 1e-4f);
            view.Dispose();
        }

        private Color FogUnderWater(bool noon, float skyExposure, out float level)
        {
            RenderSettings.ambientSkyColor = noon ? NoonSky : NightSky;
            _sun.intensity = 2f;
            _sun.enabled = noon;
            RenderSettings.sun = _sun;
            RbxUnderwaterView view = new();
            try
            {
                view.Update(Vector3.zero, new AllWater(), (x, y, z) => skyExposure);
                Assert.IsTrue(view.IsUnderwater, "the eye is in a water cell");
                level = view.LightLevel;
                return RenderSettings.fogColor;
            }
            finally
            {
                view.Dispose();
            }
        }

        private static float Brightness(Color gamma)
        {
            Color linear = gamma.linear;
            return 0.2126f * linear.r + 0.7152f * linear.g + 0.0722f * linear.b;
        }

        private sealed class AllWater : IRbxTerrainWaterCells
        {
            public bool Wet = true;

            public bool IsWaterCell(int x, int y, int z)
            {
                return Wet;
            }
        }
    }
}
