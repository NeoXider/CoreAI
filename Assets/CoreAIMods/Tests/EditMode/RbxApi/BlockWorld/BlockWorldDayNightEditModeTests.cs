using System.Threading;
using CoreAI.Ai.LuaCs;
using CoreAI.ChatOverlay;
using CoreAI.Mods.Rbx.Datatypes;
using CoreAI.Mods.Rbx.Instances;
using CoreAI.Mods.Rbx.Instances.TextChat;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode.RbxApi.BlockWorld
{
    /// <summary>
    /// P6, the Block World's day-and-night mod (<c>blockworld_daynight</c>) on the real <c>Lighting</c> service: the
    /// clock advances <c>ClockTime</c> by the length of a day, a time set elsewhere is adopted, the ambient light is
    /// darker at midnight than at noon, the fog takes the day, dusk and night colours and is whole before the
    /// streamed land ends, and the time words of <c>/time</c> (through the <c>BlockWorldTime</c> attribute and the
    /// L key) set, pause and resume it, while a word it does not know changes nothing.
    /// </summary>
    [TestFixture]
    public sealed class BlockWorldDayNightEditModeTests
    {
        private const string Mod = "blockworld_daynight";

        private SynchronizationContext _savedContext;
        private BlockWorldTestWorld _world;

        [SetUp]
        public void SetUp()
        {
            _savedContext = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(null);
            _world = new BlockWorldTestWorld();
            _world.Load(Mod);
        }

        [TearDown]
        public void TearDown()
        {
            _world?.Dispose();
            _world = null;
            SynchronizationContext.SetSynchronizationContext(_savedContext);
        }

        private RbxLighting Lighting => (RbxLighting)_world.Roblox.Game.FindFirstChildOfClass("Lighting");

        private void SetTime(string words)
        {
            _world.SetAttribute("BlockWorldTime", words);
            _world.Frame();
        }

        private static float Luminance(RbxColor3 color)
        {
            return 0.2126f * color.R + 0.7152f * color.G + 0.0722f * color.B;
        }

        private static float Linear(float channel)
        {
            return channel <= 0.04045f ? channel / 12.92f : (float)System.Math.Pow((channel + 0.055f) / 1.055f, 2.4f);
        }

        /// <summary>
        /// Linear luminance of the sky's light as the host draws it (<c>OutdoorAmbient</c> after the host's twilight
        /// scale), which is what lights a face the sun does not reach.
        /// </summary>
        private float SkyLight()
        {
            RbxColor3 drawn = RbxLightingMapping.EffectiveOutdoorAmbient(Lighting.CaptureState());
            return 0.2126f * Linear(drawn.R) + 0.7152f * Linear(drawn.G) + 0.0722f * Linear(drawn.B);
        }

        [Test]
        public void P6_DayNight_AFreshWorld_StartsInTheMorning_AndAReloadKeepsTheTime()
        {
            Assert.AreEqual(10d, Lighting.ClockTime, 0.02d, "a fresh world starts at 10:00, a mid-morning sun");

            SetTime("dusk");
            _world.Stack.Runtime.ReloadMod(Mod, BlockWorldTestWorld.Source(Mod));
            _world.Frame();

            Assert.AreEqual(18.5d, Lighting.ClockTime, 0.02d, "a reloaded clock carries on from the time it had");
            _world.AssertHealthy(Mod);
        }

        [Test]
        public void P6_DayNight_TheClock_AdvancesAnHourEveryTwentySeconds()
        {
            SetTime("noon");

            _world.Frame(200, 0.1d);

            Assert.AreEqual(13d, Lighting.ClockTime, 0.02d, "a day lasts 480 s, so 20 s is one hour");
            _world.AssertHealthy(Mod);
        }

        [Test]
        public void P6_DayNight_ATimeSetElsewhere_IsAdopted_NotOverwritten()
        {
            _world.LoadSource("test_clock", "game:GetService('Lighting').ClockTime = 3");
            _world.Frame(100, 0.1d);

            Assert.AreEqual(3.5d, Lighting.ClockTime, 0.02d, "the clock carried on from 03:00");
        }

        [Test]
        public void P6_DayNight_Midnight_IsDarkerThanNoon_UnderTheOpenSky()
        {
            SetTime("noon");
            float noon = SkyLight();

            SetTime("midnight");

            Assert.AreEqual(0d, Lighting.ClockTime, 0.01d);
            Assert.Less(SkyLight(), noon * 0.3f, "the sky's light falls at night");
        }

        // WHY these bands: measured on the default look's painted grass, a sky light of these linear luminances draws
        // the ground at about 0.15-0.25 at dusk and 0.04-0.07 at midnight (BlockWorldScenePlayModeTests checks the crops).
        [Test]
        public void P6_DayNight_AtDusk_TheLandStaysReadable_AndFallsSmoothlyToAMoonlitNight()
        {
            SetTime("noon");
            float noon = SkyLight();
            float[] light = new float[7];
            string[] times = { "18:36", "18:45", "19:00", "19:15", "19:30", "20:00", "midnight" };
            for (int index = 0; index < times.Length; index++)
            {
                SetTime(times[index]);
                light[index] = SkyLight();
                TestContext.WriteLine(times[index] + ": sky light " + light[index].ToString("0.000") + " (noon "
                                      + noon.ToString("0.000") + "), drawn " + RbxLightingMapping.EffectiveOutdoorAmbient(
                                          Lighting.CaptureState()));
            }

            Assert.That(light[0], Is.InRange(noon * 0.7f, noon * 1.1f), "at 18:36, after sunset, the land is nearly as lit as by the noon sky");
            for (int index = 1; index < light.Length - 1; index++)
            {
                Assert.LessOrEqual(light[index + 1], light[index] + 0.02f, "the light falls from " + times[index] + " to "
                                                                           + times[index + 1] + " without a jump up");
            }

            Assert.That(light[6], Is.InRange(noon * 0.15f, noon * 0.3f), "midnight is a dim moonlit night, not black");
            RbxColor3 night = RbxLightingMapping.EffectiveOutdoorAmbient(Lighting.CaptureState());
            Assert.Greater(night.B, night.R, "and blue");
            Assert.Less(Luminance(Lighting.Ambient), 0.2f, "while a cave keeps only the low Ambient");
            _world.AssertHealthy(Mod);
        }

        // WHY these bounds: a step of the fog colour under one 8-bit level, and of the sky's light under 1 %, is below
        // what a screen shows; the sun turns every frame because at a low sun a shadow's tip moves a block for a tenth
        // of a degree.
        [TestCase("17:00")]
        [TestCase("4:30")]
        public void P6_DayNight_ADuskOrADawn_ChangesFrameByFrame_WithoutAVisibleStep(string from)
        {
            SetTime(from);
            RbxLightingState state = Lighting.CaptureState();
            float lastSky = SkyLight();
            RbxColor3 lastFog = Lighting.FogColor;
            RbxVector3 lastSun = state.SunDirection;
            double fogStep = 0d;
            double skyStep = 0d;
            int sunStill = 0;
            int frames = 60 * 60;
            for (int frame = 0; frame < frames; frame++)
            {
                _world.Frame();
                float sky = SkyLight();
                RbxColor3 fog = Lighting.FogColor;
                RbxVector3 sun = Lighting.CaptureState().SunDirection;
                fogStep = System.Math.Max(fogStep, 255d * System.Math.Max(System.Math.Abs(fog.R - lastFog.R),
                    System.Math.Max(System.Math.Abs(fog.G - lastFog.G), System.Math.Abs(fog.B - lastFog.B))));
                skyStep = System.Math.Max(skyStep, System.Math.Abs(sky - lastSky) / System.Math.Max(lastSky, 1e-4f));
                sunStill += sun == lastSun ? 1 : 0;
                lastSky = sky;
                lastFog = fog;
                lastSun = sun;
            }

            TestContext.WriteLine(from + " and the next three hours at 60 frames a second: the largest step of the fog "
                                  + fogStep.ToString("0.00") + " of 255, of the sky's light "
                                  + (skyStep * 100d).ToString("0.00") + " %, frames with the sun still " + sunStill
                                  + " of " + frames);
            Assert.Less(fogStep, 1d, "the fog's colour changes by less than one 8-bit level a frame");
            Assert.Less(skyStep, 0.01d, "the sky's light changes by less than 1 % a frame");
            Assert.AreEqual(0, sunStill, "the sun turns every frame");
            _world.AssertHealthy(Mod);
        }

        [Test]
        public void P6_DayNight_AtNoonAndAtMidnight_NothingButTheClockIsWritten_NegativeTwin()
        {
            Lighting.AttachSink(new IdleSink());
            foreach (string time in new[] { "noon", "midnight" })
            {
                SetTime(time);
                _world.Frame(5);
                int before = Lighting.SinkApplyCount;
                _world.Frame(60);

                // WHY a push is the measure: the host redraws the sun and the ambient once per real change of a
                // Lighting property, so a still sky must cost one push a frame, the clock's.
                TestContext.WriteLine(time + ": " + (Lighting.SinkApplyCount - before) + " pushes in 60 frames");
                Assert.LessOrEqual(Lighting.SinkApplyCount - before, 60, "at " + time + " only ClockTime moves");
            }

            _world.AssertHealthy(Mod);
        }

        private sealed class IdleSink : IRbxLightingSink
        {
            public void Apply(in RbxLightingState state)
            {
            }
        }

        [Test]
        public void P6_DayNight_WhereTheSkyDoesNotReach_ItIsAsDarkAtNoonAsAtMidnight()
        {
            SetTime("noon");
            float noonAmbient = Luminance(Lighting.Ambient);
            float noonOutdoor = Luminance(Lighting.OutdoorAmbient);
            SetTime("midnight");

            Assert.AreEqual(noonAmbient, Luminance(Lighting.Ambient), 1e-4f,
                "Lighting.Ambient, the light of a cave, does not follow the sun");
            Assert.Less(noonAmbient, 0.2f, "and is low: a torch matters there");
            Assert.Greater(noonOutdoor, noonAmbient * 4f, "while the sky's light by day is several times brighter");
        }

        [Test]
        public void P6_DayNight_TheFog_TakesTheDayDuskAndNightColours()
        {
            SetTime("noon");
            AssertColour(198, 220, 228, Lighting.FogColor, "the day's pale haze at noon");
            SetTime("18:45");
            AssertColour(52, 58, 92, Lighting.FogColor, "the dusky blue half way through the light's fall");
            SetTime("6:15");
            AssertColour(52, 58, 92, Lighting.FogColor, "and the same at dawn");
            SetTime("midnight");
            AssertColour(24, 32, 56, Lighting.FogColor, "the dark night sky at midnight");
            _world.AssertHealthy(Mod);
        }

        [Test]
        public void P6_DayNight_WithoutAStreamedWorld_TheFogDistancesAreLeftAlone_NegativeTwin()
        {
            Assert.AreEqual(RbxLighting.DefaultFogEnd, Lighting.FogEnd, "no BlockWorldViewStuds, no fog distance");
            Assert.AreEqual(RbxLighting.DefaultFogStart, Lighting.FogStart);
        }

        // WHY the radius rule restated here: the world streams the columns (dx, dz) from the player's column with
        // dx * dx + dz * dz <= r * r + 1, and the fog must be whole before the nearest column it leaves out, seen
        // from any point of the player's column (a corner is the worst), on a desktop (4) and in WebGL (3).
        [TestCase(4)]
        [TestCase(3)]
        public void P6_DayNight_TheFogIsWhole_BeforeTheStreamedLandEnds_OnEveryPlatform(int radius)
        {
            _world.Dispose();
            _world = new BlockWorldTestWorld();
            _world.SetAttribute("BlockWorldViewRadius", (double)radius);
            _world.Load("blockworld_world");
            _world.Load(Mod);
            _world.Frame();

            const float column = 64f;
            double nearestGap = double.MaxValue;
            for (int dx = -radius - 1; dx <= radius + 1; dx++)
            {
                for (int dz = -radius - 1; dz <= radius + 1; dz++)
                {
                    if (dx * dx + dz * dz > radius * radius + 1)
                    {
                        double gx = System.Math.Max(System.Math.Abs(dx) - 1, 0) * column;
                        double gz = System.Math.Max(System.Math.Abs(dz) - 1, 0) * column;
                        nearestGap = System.Math.Min(nearestGap, System.Math.Sqrt(gx * gx + gz * gz));
                    }
                }
            }

            TestContext.WriteLine("radius " + radius + ": land sure to reach " + nearestGap.ToString("0.0")
                                  + " studs, fog from " + Lighting.FogStart.ToString("0.0") + " to "
                                  + Lighting.FogEnd.ToString("0.0"));
            Assert.LessOrEqual(Lighting.FogEnd, nearestGap, "no player sees past the fog to the world's edge");
            Assert.Less(Lighting.FogEnd, radius * column, "and so it is whole inside the nominal stream radius");
            Assert.Greater(Lighting.FogEnd, nearestGap - 20d, "yet it does not hide land the world has made");
            Assert.That(Lighting.FogStart, Is.GreaterThan(0f).And.LessThan(Lighting.FogEnd));
            _world.AssertHealthy(Mod, "blockworld_world");
        }

        private static void AssertColour(int r, int g, int b, RbxColor3 actual, string what)
        {
            Assert.AreEqual(r / 255f, actual.R, 0.5f / 255f, what + " (red)");
            Assert.AreEqual(g / 255f, actual.G, 0.5f / 255f, what + " (green)");
            Assert.AreEqual(b / 255f, actual.B, 0.5f / 255f, what + " (blue)");
        }

        [TestCase("dawn", 6d)]
        [TestCase("dusk", 18.5d)]
        [TestCase("18:30", 18.5d)]
        [TestCase(" 7 ", 7d)]
        [TestCase("NOON", 12d)]
        public void P6_DayNight_TimeWords_SetTheClock(string words, double hours)
        {
            SetTime(words);

            Assert.AreEqual(hours, Lighting.ClockTime, 0.01d);
            StringAssert.StartsWith("Time set to", (string)_world.Attribute("BlockWorldNotice"));
            Assert.AreEqual("", _world.Attribute("BlockWorldTime"), "the request is consumed");
        }

        [TestCase("25:00")]
        [TestCase("teatime")]
        [TestCase("-3")]
        public void P6_DayNight_AWordItDoesNotKnow_ChangesNothing_NegativeTwin(string words)
        {
            SetTime("noon");

            SetTime(words);

            Assert.AreEqual(12d, Lighting.ClockTime, 0.01d);
            StringAssert.StartsWith("Unknown time", (string)_world.Attribute("BlockWorldNotice"));
        }

        [Test]
        public void P6_DayNight_StopAndGo_PauseAndResumeTheClock()
        {
            SetTime("noon");
            SetTime("stop");
            _world.Frame(100, 0.1d);
            Assert.AreEqual(12d, Lighting.ClockTime, 0.01d, "stopped");

            SetTime("go");
            _world.Frame(100, 0.1d);
            Assert.AreEqual(12.5d, Lighting.ClockTime, 0.03d, "running again");
        }

        [Test]
        public void P6_DayNight_TheLKey_CyclesNoonDuskMidnightDawn()
        {
            double[] expected = { 12d, 18.5d, 0d, 6d, 12d };
            foreach (double hours in expected)
            {
                _world.Tap(BlockWorldTestWorld.KeyL);
                Assert.AreEqual(hours, Lighting.ClockTime, 0.01d);
            }
        }

        // WHY rewritten: TextChatCommand works now, so the mod registers /time the Roblox way and its "press L"
        // fallback note (for a host without Lua chat commands) no longer prints.
        [Test]
        public void P6_DayNight_TheTimeCommand_IsATextChatCommand_ThatTheChatRuns()
        {
            RbxInstance service = _world.Roblox.Game.FindFirstChildOfClass("TextChatService");
            RbxTextChatCommand command = service.FindFirstChild("BlockWorldTime", true) as RbxTextChatCommand;
            Assert.IsNotNull(command, "the mod registered its command under TextChatService");
            Assert.AreEqual("/time", command.PrimaryAlias);
            Assert.IsFalse(_world.Logger.Contains("press L"), "no fallback note: " + _world.Logger.Describe());

            ChatCommandRegistry registry = new();
            _world.Roblox.AttachChatCommands(new ChatCommandRegistrySink(registry));
            Assert.IsTrue(registry.TryResolve("time", out IChatCommand time));
            ChatCommandParser.Classify("/time midnight", ChatCommandParser.DefaultPrefix,
                out ChatCommandInvocation invocation, out string _);
            time.Execute(new ChatCommandContext(invocation, registry, null, ChatCommandParser.DefaultPrefix));
            _world.Frame();

            Assert.AreEqual(0d, Lighting.ClockTime, 0.01d, "/time midnight from the chat set the clock");
            _world.AssertHealthy(Mod);
        }
    }
}
