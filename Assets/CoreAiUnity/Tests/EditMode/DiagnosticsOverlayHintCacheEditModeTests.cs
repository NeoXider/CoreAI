using CoreAI.Diagnostics;
using NUnit.Framework;
using UnityEngine;

namespace CoreAI.Tests.EditMode
{
    /// <summary>
    /// The IMGUI diagnostics windows repaint several times per frame while shown; their hotkey footer
    /// used to be re-interpolated (boxing the <see cref="KeyCode"/>) on every repaint. It must be built
    /// once per hotkey value.
    /// </summary>
    [Category("Diagnostics")]
    public sealed class DiagnosticsOverlayHintCacheEditModeTests
    {
        private GameObject _go;

        [SetUp]
        public void SetUp()
        {
            _go = new GameObject("DiagnosticsOverlayHintCacheEditModeTests");
        }

        [TearDown]
        public void TearDown()
        {
            Object.DestroyImmediate(_go);
        }

        [Test]
        public void TokenBudgetOverlay_HintIsReused_UntilTheHotkeyChanges()
        {
            CoreAiTokenBudgetOverlay overlay = _go.AddComponent<CoreAiTokenBudgetOverlay>();

            string first = overlay.ToggleHintText;
            string second = overlay.ToggleHintText;
            Assert.IsTrue(ReferenceEquals(first, second), "Same hotkey, same string instance.");
            StringAssert.Contains("[F10]", first);

            overlay.ToggleKey = KeyCode.F5;
            string changed = overlay.ToggleHintText;
            StringAssert.Contains("[F5]", changed);
            Assert.IsTrue(ReferenceEquals(changed, overlay.ToggleHintText));
        }

        [Test]
        public void OrchestrationDashboard_HintIsReused()
        {
            OrchestrationDashboard dashboard = _go.AddComponent<OrchestrationDashboard>();

            string first = dashboard.ToggleHintText;
            Assert.IsTrue(ReferenceEquals(first, dashboard.ToggleHintText));
            StringAssert.Contains("[F9]", first);
        }
    }
}
