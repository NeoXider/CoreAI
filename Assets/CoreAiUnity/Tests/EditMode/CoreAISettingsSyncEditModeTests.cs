using NUnit.Framework;
using UnityEngine;
using CoreAI.Composition;
using VContainer;
using CoreAI.Infrastructure.Llm;

namespace CoreAI.Tests.EditMode
{
    /// <summary>
    /// EditMode тесты для статической синхронизации настроек.
    /// Закрывает "CoreAISettings статическая синхронизация" пробел (секция 5.2).
    /// </summary>
    public sealed class CoreAISettingsSyncEditModeTests
    {
        [Test]
        public void Configure_ShouldSyncAssetToStaticSettings()
        {
            // 1. Arrange: создаём мок-настройки
            var settings = ScriptableObject.CreateInstance<CoreAISettingsAsset>();
            var type = typeof(CoreAISettingsAsset);
            var bf = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
            type.GetField("maxLuaRepairRetries", bf).SetValue(settings, 99);
            type.GetField("maxToolCallRetries", bf).SetValue(settings, 77);
            type.GetField("enableMeaiDebugLogging", bf).SetValue(settings, true);
            type.GetField("contextWindowTokens", bf).SetValue(settings, 12345);
            type.GetField("universalSystemPromptPrefix", bf).SetValue(settings, "TEST PREFIX");
            type.GetField("temperature", bf).SetValue(settings, 0.99f);
            type.GetField("logToolCalls", bf).SetValue(settings, false);
            type.GetField("logToolCallArguments", bf).SetValue(settings, false);
            type.GetField("logToolCallResults", bf).SetValue(settings, false);
            type.GetField("logMeaiToolCallingSteps", bf).SetValue(settings, false);

            // 2. Arrange: создаём Scope
            var go = new GameObject("TestScope");
            var scope = go.AddComponent<CoreAILifetimeScope>();
            typeof(CoreAILifetimeScope).GetField("coreAiSettings", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).SetValue(scope, settings);

            var builder = new ContainerBuilder();
            var configureMethod = typeof(CoreAILifetimeScope).GetMethod("Configure", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            
            // 3. Act: вызываем Configure
            configureMethod.Invoke(scope, new object[] { builder });
            
            // 4. Assert: статическая структура должна обновиться
            Assert.AreEqual(99, CoreAI.CoreAISettings.MaxLuaRepairRetries);
            Assert.AreEqual(77, CoreAI.CoreAISettings.MaxToolCallRetries);
            Assert.AreEqual(true, CoreAI.CoreAISettings.EnableMeaiDebugLogging);
            Assert.AreEqual(12345, CoreAI.CoreAISettings.ContextWindowTokens);
            Assert.AreEqual("TEST PREFIX", CoreAI.CoreAISettings.UniversalSystemPromptPrefix);
            Assert.AreEqual(0.99f, CoreAI.CoreAISettings.Temperature);
            Assert.AreEqual(false, CoreAI.CoreAISettings.LogToolCalls);
            Assert.AreEqual(false, CoreAI.CoreAISettings.LogToolCallArguments);
            Assert.AreEqual(false, CoreAI.CoreAISettings.LogToolCallResults);
            Assert.AreEqual(false, CoreAI.CoreAISettings.LogMeaiToolCallingSteps);
            
            // Cleanup
            Object.DestroyImmediate(go);
            Object.DestroyImmediate(settings);
        }
    }
}
