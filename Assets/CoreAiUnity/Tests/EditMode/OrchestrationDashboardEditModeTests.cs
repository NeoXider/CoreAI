using CoreAI.Diagnostics;
using NUnit.Framework;
using UnityEngine;

namespace CoreAI.Tests.EditMode
{
    /// <summary>
    /// Smoke-проверка <see cref="OrchestrationDashboard"/> на совместимость с обеими input-системами.
    /// Полноценный keyboard-trigger потребовал бы PlayMode + симуляции <see cref="UnityEngine.Input"/>
    /// (или <c>InputTestFixture</c>), что для дашборда метрик избыточно. Здесь ловим самое ценное:
    /// при текущей конфигурации <c>Active Input Handling</c> компонент создаётся, инициализируется и
    /// прокачивает <c>Update</c>-цикл без исключений (включая ветку, где legacy <c>Input</c>
    /// отключён, а <c>UnityEngine.InputSystem</c> подцеплён через <c>versionDefines</c>).
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
            var update = typeof(OrchestrationDashboard).GetMethod(
                "Update",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);

            Assert.IsNotNull(update, "OrchestrationDashboard must declare a private Update method.");

            Assert.DoesNotThrow(() => update.Invoke(_dashboard, null),
                "Update must tolerate any Active Input Handling configuration without throwing.");
        }
    }
}
