using System.Reflection;
using CoreAI.Diagnostics;
using NUnit.Framework;
using UnityEngine;

namespace CoreAI.Tests.EditMode
{
    /// <summary>
    /// Smoke coverage for <see cref="OrchestrationDashboard"/> compatibility with both input stacks.
    /// Full keyboard simulation belongs in PlayMode; this test verifies construction,
    /// initialization, and the update loop under the active input configuration.
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
        public void Component_CanBeInstantiated_OnAnyActiveInputHandler()
        {
            Assert.IsNotNull(_dashboard,
                "OrchestrationDashboard must AddComponent successfully regardless of Active Input Handling.");
        }

        [Test]
        public void Update_DoesNotThrow_WhenNoMetricsAttached()
        {
            // Reflection — Update — приватный метод. Ранее вызов Input.GetKeyDown() выбрасывал
            // InvalidOperationException, когда legacy Input Manager отключён в проекте,
            // и Update крашил каждый кадр. Smoke-тест ловит регресс: метод вызывается
            // безопасно при любом значении ENABLE_LEGACY_INPUT_MANAGER / ENABLE_INPUT_SYSTEM.
            MethodInfo update = typeof(OrchestrationDashboard).GetMethod(
                "Update",
                BindingFlags.Instance | BindingFlags.NonPublic);

            Assert.IsNotNull(update, "OrchestrationDashboard must declare a private Update method.");

            Assert.DoesNotThrow(() => update.Invoke(_dashboard, null),
                "Update must tolerate any Active Input Handling configuration without throwing.");
        }
    }
}