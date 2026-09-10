using CoreAI.Messaging;
using CoreAI.Presentation.AiDashboard;
using NUnit.Framework;
using UnityEngine;

namespace CoreAI.Tests.EditMode
{
    /// <summary>
    /// <see cref="AiDashboardPresenter"/> draws its overlay on every IMGUI repaint - several per frame -
    /// but its content changes only when a command is routed or a permission flag flips. The text must be
    /// reused between those events and rebuilt on each of them.
    /// </summary>
    [Category("Diagnostics")]
    public sealed class AiDashboardPresenterOverlayTextEditModeTests
    {
        private GameObject _go;
        private AiDashboardPresenter _presenter;

        [SetUp]
        public void SetUp()
        {
            _go = new GameObject("AiDashboardPresenterOverlayTextEditModeTests");
            _presenter = _go.AddComponent<AiDashboardPresenter>();
        }

        [TearDown]
        public void TearDown()
        {
            Object.DestroyImmediate(_go);
        }

        [Test]
        public void RepeatedRepaints_WithoutChanges_ReuseTheSameString()
        {
            string first = _presenter.BuildOverlayText();
            string second = _presenter.BuildOverlayText();

            Assert.IsTrue(ReferenceEquals(first, second),
                "Nothing changed between two repaints, so the overlay text must not be rebuilt.");
        }

        [Test]
        public void RoutedCommand_RebuildsTheTextOnce_ThenReusesIt()
        {
            string before = _presenter.BuildOverlayText();

            _presenter.OnAiCommand(new ApplyAiGameCommand
            {
                CommandTypeId = "spawn",
                SourceTag = "creator",
                JsonPayload = "{\"kind\":\"cube\"}"
            });

            string after = _presenter.BuildOverlayText();
            Assert.IsFalse(ReferenceEquals(before, after), "A routed command must produce a fresh overlay text.");
            StringAssert.Contains("spawn [creator]: {\"kind\":\"cube\"}", after);
            Assert.IsTrue(ReferenceEquals(after, _presenter.BuildOverlayText()),
                "After the rebuild the text must be reused again until the next change.");
        }

        [Test]
        public void PermissionFlagFlip_OnTheSameSnapshot_IsStillReflected()
        {
            // The old code read the live flags on every repaint; caching must not freeze them.
            AiPermissionsOptions permissions = new() { AllowCreator = true, AllowAnalyzer = true, AllowCoreMechanic = true };
            _presenter.SetRuntimePermissions(permissions);

            string allowed = _presenter.BuildOverlayText();
            StringAssert.Contains("C=True", allowed);

            permissions.AllowCreator = false;
            string denied = _presenter.BuildOverlayText();

            StringAssert.Contains("C=False", denied);
            Assert.IsFalse(ReferenceEquals(allowed, denied));
        }

        [Test]
        public void SwappingThePermissionSource_RebuildsTheText()
        {
            _presenter.SetRuntimePermissions(new AiPermissionsOptions());
            string first = _presenter.BuildOverlayText();

            _presenter.SetRuntimePermissions(new AiPermissionsOptions { AllowAnalyzer = false });
            string second = _presenter.BuildOverlayText();

            StringAssert.Contains("A=True", first);
            StringAssert.Contains("A=False", second);
        }
    }
}
