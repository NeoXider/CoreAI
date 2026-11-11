using System.Threading;
using CoreAI.Mods.Rbx.Datatypes;
using CoreAI.Mods.Rbx.Instances;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode.RbxApi.BlockWorld
{
    /// <summary>
    /// P6, the Block World's day-and-night mod (<c>blockworld_daynight</c>) on the real <c>Lighting</c> service: the
    /// clock advances <c>ClockTime</c> by the length of a day, a time set elsewhere is adopted, the ambient light is
    /// darker at midnight than at noon, and the time words of <c>/time</c> (through the <c>BlockWorldTime</c>
    /// attribute and the L key) set, pause and resume it, while a word it does not know changes nothing.
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

        [Test]
        public void P6_DayNight_AFreshWorld_StartsInTheMorning_AndAReloadKeepsTheTime()
        {
            Assert.AreEqual(8.5d, Lighting.ClockTime, 0.02d, "a fresh world starts at 08:30, a low morning sun");

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
        public void P6_DayNight_Midnight_IsDarkerThanNoon()
        {
            SetTime("noon");
            float noonOutdoor = Luminance(Lighting.OutdoorAmbient);
            float noonAmbient = Luminance(Lighting.Ambient);

            SetTime("midnight");

            Assert.AreEqual(0d, Lighting.ClockTime, 0.01d);
            Assert.Less(Luminance(Lighting.OutdoorAmbient), noonOutdoor * 0.4f, "the outdoor light falls at night");
            Assert.Less(Luminance(Lighting.Ambient), noonAmbient * 0.4f);
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

        [Test]
        public void P6_DayNight_WithoutLuaChatCommands_TheModSaysHowToSetTheTime()
        {
            Assert.IsTrue(_world.Logger.Contains("press L"), "the host has no TextChatService: " + _world.Logger.Describe());
            _world.AssertHealthy(Mod);
        }
    }
}
