using System;
using System.Diagnostics;
using CoreAI.RuntimeUi;
using NUnit.Framework;
using UnityEngine.TestTools.Constraints;
using Is = UnityEngine.TestTools.Constraints.Is;

namespace CoreAI.Tests.EditMode.RuntimeUi
{
    /// <summary>
    /// The P18 budgets in the editor: a 200-element screen builds (parse, factory, cascade, inline styles)
    /// in 10 ms or less, and an idle screen allocates nothing per frame - the host has no per-frame work,
    /// and the panel's own style and layout passes stay allocation-free while nothing changes. The
    /// P19 harness re-measures both in built players.
    /// </summary>
    [TestFixture]
    public sealed class RuntimeUiBudgetEditModeTests
    {
        [Test]
        public void TwoHundredElementScreen_BuildsWithinTenMilliseconds()
        {
            string uxml = RuntimeUiFixtureTexts.TwoHundredElementUxml();
            using RuntimeUiTestPanel panel = new();
            for (int i = 0; i < 3; i++)
            {
                BuildAndDestroy(panel, uxml, out _);
            }

            double[] samples = new double[7];
            for (int i = 0; i < samples.Length; i++)
            {
                BuildAndDestroy(panel, uxml, out samples[i]);
            }

            Array.Sort(samples);
            double median = samples[samples.Length / 2];
            TestContext.WriteLine("Runtime UI 200-element screen build in the editor: median " +
                                  median.ToString("0.000") + " ms (min " + samples[0].ToString("0.000") +
                                  ", max " + samples[samples.Length - 1].ToString("0.000") + ")");
            UnityEngine.Debug.Log("[RuntimeUi budget] 200-element build median " + median.ToString("0.000") + " ms");
            Assert.LessOrEqual(median, 10.0, "P18 DoD: a screen of 200 elements builds in 10 ms or less.");
        }

        [Test]
        public void IdleScreen_AllocatesNothingPerFrame()
        {
            using RuntimeUiTestPanel panel = new();
            RuntimeUiScreen screen = panel.Create("hud", RuntimeUiFixtureTexts.TwoHundredElementUxml(),
                RuntimeUiFixtureTexts.HotbarUss);
            Assert.AreEqual(200, screen.ElementCount);
            Action frame = panel.PanelPasses;
            for (int i = 0; i < 5; i++)
            {
                frame();
            }

            int styleVersion = screen.StyleVersion;
            Assert.That(() =>
            {
                for (int i = 0; i < 60; i++)
                {
                    frame();
                }
            }, Is.Not.AllocatingGCMemory(), "60 idle frames of a 200-element screen must not allocate.");
            Assert.AreEqual(styleVersion, screen.StyleVersion, "Nothing restyles while idle.");
        }

        private static void BuildAndDestroy(RuntimeUiTestPanel panel, string uxml, out double milliseconds)
        {
            Stopwatch watch = Stopwatch.StartNew();
            RuntimeUiResult result = panel.Host.CreateScreen("budget", uxml, RuntimeUiFixtureTexts.HotbarUss);
            watch.Stop();
            milliseconds = watch.Elapsed.TotalMilliseconds;
            Assert.IsTrue(result.Success, result.ToString());
            Assert.AreEqual(200, result.Screen.ElementCount);
            Assert.AreEqual(0, result.Diagnostics.Count, string.Join("\n", result.Diagnostics));
            panel.Host.DestroyScreen("budget");
        }
    }
}
