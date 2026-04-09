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
    /// EditMode тесты для CoreAISettings static proxy → ICoreAISettings delegation.
    /// Верифицирует, что после установки CoreAISettings.Instance все свойства
    /// делегируются в DI-зарегистрированный экземпляр.
    /// </summary>
    public sealed class CoreAISettingsSyncEditModeTests
    {
        [SetUp]
        public void SetUp()
        {
            CoreAISettings.ResetOverrides();
            CoreAISettings.Instance = null;
        }

        [TearDown]
        public void TearDown()
        {
            CoreAISettings.ResetOverrides();
            CoreAISettings.Instance = null;
        }

        [Test]
        public void Instance_Delegation_ReadsFromAsset()
        {
            // Arrange: создаём мок-настройки
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

            // Act: устанавливаем Instance
            CoreAISettings.Instance = settings;

            // Assert: статические свойства делегируются в Instance
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
            Object.DestroyImmediate(settings);
        }

        [Test]
        public void Override_TakesPrecedenceOverInstance()
        {
            // Arrange: настройки с MaxLuaRepairRetries = 99
            CoreAISettingsAsset settings = ScriptableObject.CreateInstance<CoreAISettingsAsset>();
            typeof(CoreAISettingsAsset)
                .GetField("maxLuaRepairRetries", BindingFlags.NonPublic | BindingFlags.Instance)
                .SetValue(settings, 99);
            CoreAISettings.Instance = settings;

            // Act: локальный override
            CoreAISettings.MaxLuaRepairRetries = 5;

            // Assert: override побеждает
            Assert.AreEqual(5, CoreAISettings.MaxLuaRepairRetries);

            // Cleanup
            Object.DestroyImmediate(settings);
        }

        [Test]
        public void ResetOverrides_RestoresInstanceDelegation()
        {
            CoreAISettingsAsset settings = ScriptableObject.CreateInstance<CoreAISettingsAsset>();
            typeof(CoreAISettingsAsset)
                .GetField("maxLuaRepairRetries", BindingFlags.NonPublic | BindingFlags.Instance)
                .SetValue(settings, 42);
            CoreAISettings.Instance = settings;

            // Set override
            CoreAISettings.MaxLuaRepairRetries = 999;
            Assert.AreEqual(999, CoreAISettings.MaxLuaRepairRetries);

            // Reset
            CoreAISettings.ResetOverrides();
            Assert.AreEqual(42, CoreAISettings.MaxLuaRepairRetries);

            Object.DestroyImmediate(settings);
        }

        [Test]
        public void NoInstance_UsesDefaults()
        {
            // Без Instance — значения по умолчанию
            Assert.AreEqual(3, CoreAISettings.MaxLuaRepairRetries);
            Assert.AreEqual(0.1f, CoreAISettings.Temperature);
            Assert.AreEqual(false, CoreAISettings.EnableMeaiDebugLogging);
            Assert.AreEqual(8192, CoreAISettings.ContextWindowTokens);
        }

        [Test]
        public void Configure_SetsInstance_OnLifetimeScope()
        {
            // Arrange
            CoreAISettingsAsset settings = ScriptableObject.CreateInstance<CoreAISettingsAsset>();
            Type type = typeof(CoreAISettingsAsset);
            BindingFlags bf = BindingFlags.NonPublic | BindingFlags.Instance;
            type.GetField("maxLuaRepairRetries", bf).SetValue(settings, 99);
            type.GetField("temperature", bf).SetValue(settings, 0.99f);

            GameObject go = new("TestScope");
            CoreAILifetimeScope scope = go.AddComponent<CoreAILifetimeScope>();
            typeof(CoreAILifetimeScope)
                .GetField("coreAiSettings",
                    BindingFlags.NonPublic | BindingFlags.Instance)
                .SetValue(scope, settings);

            ContainerBuilder builder = new();
            MethodInfo configureMethod = typeof(CoreAILifetimeScope).GetMethod("Configure",
                BindingFlags.NonPublic | BindingFlags.Instance);

            // Act
            configureMethod.Invoke(scope, new object[] { builder });

            // Assert: Instance должен быть установлен
            Assert.AreSame(settings, CoreAISettings.Instance);
            Assert.AreEqual(99, CoreAISettings.MaxLuaRepairRetries);
            Assert.AreEqual(0.99f, CoreAISettings.Temperature);

            // Cleanup
            Object.DestroyImmediate(go);
            Object.DestroyImmediate(settings);
        }
    }
}