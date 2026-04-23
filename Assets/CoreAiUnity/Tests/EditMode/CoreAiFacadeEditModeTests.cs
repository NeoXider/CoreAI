using System;
using NUnit.Framework;
using CoreAI;

namespace CoreAI.Tests.EditMode
{
    /// <summary>
    /// EditMode-тесты для статического фасада <see cref="CoreAi"/>.
    /// Без CoreAILifetimeScope в сцене синглтон не может резолвнуть сервисы,
    /// поэтому тесты фиксируют контракт: IsReady=false, методы бросают понятное
    /// исключение, Invalidate безопасен.
    /// </summary>
    public sealed class CoreAiFacadeEditModeTests
    {
        [SetUp]
        public void ResetFacade()
        {
            CoreAi.Invalidate();
        }

        [Test]
        public void IsReady_WithoutLifetimeScope_ReturnsFalse()
        {
            Assert.IsFalse(CoreAi.IsReady, "Без CoreAILifetimeScope в сцене фасад не должен считаться готовым");
        }

        [Test]
        public void Invalidate_DoesNotThrow_WhenCalledMultipleTimes()
        {
            Assert.DoesNotThrow(() => CoreAi.Invalidate());
            Assert.DoesNotThrow(() => CoreAi.Invalidate());
            Assert.DoesNotThrow(() => CoreAi.Invalidate());
        }

        [Test]
        public void GetSettings_WithoutLifetimeScope_ReturnsNull()
        {
            ICoreAISettings settings = CoreAi.GetSettings();
            Assert.IsNull(settings,
                "Без scope GetSettings возвращает null (caller должен сам использовать CoreAISettings.Instance)");
        }

        [Test]
        public void GetChatService_WithoutLifetimeScope_ThrowsInvalidOperation()
        {
            InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
                () => CoreAi.GetChatService());
            StringAssert.Contains("CoreAILifetimeScope", ex.Message,
                "Исключение должно подсказывать, где искать проблему");
        }

        [Test]
        public void TryGetChatService_WithoutLifetimeScope_ReturnsFalse()
        {
            Assert.IsFalse(CoreAi.TryGetChatService(out _),
                "Без scope TryGet не бросает исключение и возвращает false");
        }

        [Test]
        public void GetOrchestrator_WithoutLifetimeScope_ThrowsInvalidOperation()
        {
            InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
                () => CoreAi.GetOrchestrator());
            StringAssert.Contains("IAiOrchestrationService", ex.Message,
                "Исключение должно объяснять, что не зарегистрирован оркестратор");
        }

        [Test]
        public void TryGetOrchestrator_WithoutLifetimeScope_ReturnsFalse()
        {
            Assert.IsFalse(CoreAi.TryGetOrchestrator(out _));
        }
    }
}
