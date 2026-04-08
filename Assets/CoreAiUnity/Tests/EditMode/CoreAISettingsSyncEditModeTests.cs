using System;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using CoreAI.Composition;
using VContainer;
using CoreAI.Infrastructure.Llm;
using Object = UnityEngine.Object;

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
            CoreAISettingsAsset settings = ScriptableObject.CreateInstance<CoreAISettingsAsset>();
            Type type = typeof(CoreAISettingsAsset);
            BindingFlags bf = BindingFlags.NonPublic | BindingFlags.Instance;
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
            GameObject go = new("TestScope");
            CoreAILifetimeScope scope = go.AddComponent<CoreAILifetimeScope>();
            typeof(CoreAILifetimeScope)
                .GetField("coreAiSettings",
                    BindingFlags.NonPublic | BindingFlags.Instance)
                .SetValue(scope, settings);

            ContainerBuilder builder = new();
            MethodInfo configureMethod = typeof(CoreAILifetimeScope).GetMethod("Configure",
                BindingFlags.NonPublic | BindingFlags.Instance);

            // 3. Act: вызываем Configure
            configureMethod.Invoke(scope, new object[] { builder });

            // 4. Assert: статическая структура должна обновиться
            Assert.AreEqual(99, CoreAISettings.MaxLuaRepairRetries);
            Assert.AreEqual(77, CoreAISettings.MaxToolCallRetries);
            Assert.AreEqual(true, CoreAISettings.EnableMeaiDebugLogging);
            Assert.AreEqual(12345, CoreAISettings.ContextWindowTokens);
            Assert.AreEqual("TEST PREFIX", CoreAISettings.UniversalSystemPromptPrefix);
            Assert.AreEqual(0.99f, CoreAISettings.Temperature);
            Assert.AreEqual(false, CoreAISettings.LogToolCalls);
            Assert.AreEqual(false, CoreAISettings.LogToolCallArguments);
            Assert.AreEqual(false, CoreAISettings.LogToolCallResults);
            Assert.AreEqual(false, CoreAISettings.LogMeaiToolCallingSteps);

            // Cleanup
            Object.DestroyImmediate(go);
            Object.DestroyImmediate(settings);
        }
    }
}