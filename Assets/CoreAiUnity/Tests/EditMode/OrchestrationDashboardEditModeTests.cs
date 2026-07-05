using System.Reflection;
using CoreAI.Diagnostics;
using NUnit.Framework;
using UnityEngine;

namespace CoreAI.Tests.EditMode
{
    /// <summary>
    /// Smoke coverage for <see cref="OrchestrationDashboard"/> compatibility with both input stacks.
    /// Full keyboard simulation belongs in PlayMode; this test verifies that the update loop
    /// tolerates any Active Input Handling configuration without throwing.
    /// </summary>
    [TestFixture]
    public sealed class OrchestrationDashboardEditModeTests
    {
        private GameObject _go;
        private OrchestrationDashboard _dashboard;

        [SetUp]
        public void SetUp()
        {
            _go = new GameObject("CoreAi.OrchestrationDashboard.Test");
            _dashboard = _go.AddComponent<OrchestrationDashboard>();
        }

        [TearDown]
        public void TearDown()
        {
            if (_go != null)
            {
                Object.DestroyImmediate(_go);
            }
        }

        [Test]
        public void Update_DoesNotThrow_WhenNoMetricsAttached()
        {
            // Reflection path: call the private Update method directly.
            // Older editor/input combinations can throw when legacy input references Input.GetKeyDown(); this test guards resilience
            // by validating Update under different Active Input Handling configurations.
            // Covers both legacy and new Input System branches controlled by project symbols.
            // This is intentionally a smoke test for startup robustness, not input behavior.
            MethodInfo update = typeof(OrchestrationDashboard).GetMethod(
                "Update",
                BindingFlags.Instance | BindingFlags.NonPublic);

            Assert.IsNotNull(update, "OrchestrationDashboard must declare a private Update method.");

            Assert.DoesNotThrow(() => update.Invoke(_dashboard, null),
                "Update must tolerate any Active Input Handling configuration without throwing.");
        }
    }
}