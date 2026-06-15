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
    /// EditMode coverage for <see cref="CoreAISettings"/> static proxy delegation
    /// to the DI-registered <see cref="ICoreAISettings"/> instance.
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
            // Arrange test settings.
            CoreAISettingsAsset settings = ScriptableObject.CreateInstance<CoreAISettingsAsset>();
            settings.ApplyOptions(new CoreAISettingsOptions
            {
                MaxLuaRepairRetries = 99,
                MaxToolCallRetries = 77,
                EnableMeaiDebugLogging = true,
                ContextWindowTokens = 12345,
                UniversalSystemPromptPrefix = "TEST PREFIX",
                Temperature = 0.99f,
                OverrideTemperature = true,
                LogToolCalls = false,
                LogToolCallArguments = false,
                LogToolCallResults = false,
                LogMeaiToolCallingSteps = false
            });

            // Act: set Instance.
            CoreAISettings.Instance = settings;

            // Assert: static properties delegate to Instance.
            Assert.AreEqual(99, CoreAISettings.MaxLuaRepairRetries);
            Assert.AreEqual(77, CoreAISettings.MaxToolCallRetries);
            Assert.AreEqual(true, CoreAISettings.EnableMeaiDebugLogging);
            Assert.AreEqual(12345, CoreAISettings.ContextWindowTokens);
            Assert.AreEqual("TEST PREFIX", CoreAISettings.UniversalSystemPromptPrefix);
            Assert.AreEqual(0.99f, CoreAISettings.Temperature);
            Assert.IsTrue(CoreAISettings.OverrideTemperature);
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
            // Arrange settings with MaxLuaRepairRetries = 99.
            CoreAISettingsAsset settings = ScriptableObject.CreateInstance<CoreAISettingsAsset>();
            settings.ApplyOptions(new CoreAISettingsOptions
            {
                MaxLuaRepairRetries = 99
            });
            CoreAISettings.Instance = settings;

            // Act: local override.
            CoreAISettings.MaxLuaRepairRetries = 5;

            // Assert: override wins.
            Assert.AreEqual(5, CoreAISettings.MaxLuaRepairRetries);

            // Cleanup
            Object.DestroyImmediate(settings);
        }

        [Test]
        public void ResetOverrides_RestoresInstanceDelegation()
        {
            CoreAISettingsAsset settings = ScriptableObject.CreateInstance<CoreAISettingsAsset>();
            settings.ApplyOptions(new CoreAISettingsOptions
            {
                MaxLuaRepairRetries = 42
            });
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
            // Without Instance, defaults are used.
            Assert.AreEqual(3, CoreAISettings.MaxLuaRepairRetries);
            Assert.AreEqual(0.1f, CoreAISettings.Temperature);
            Assert.IsFalse(CoreAISettings.OverrideTemperature);
            Assert.AreEqual(false, CoreAISettings.EnableMeaiDebugLogging);
            Assert.AreEqual(CoreAISettings.DefaultContextWindowTokens, CoreAISettings.ContextWindowTokens);
        }

        [Test]
        public void Configure_SetsInstance_OnLifetimeScope()
        {
            // Arrange
            CoreAISettingsAsset settings = ScriptableObject.CreateInstance<CoreAISettingsAsset>();
            settings.ApplyOptions(new CoreAISettingsOptions
            {
                MaxLuaRepairRetries = 99,
                Temperature = 0.99f
            });

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

            // Instance should be assigned.
            Assert.AreSame(settings, CoreAISettings.Instance);
            Assert.AreEqual(99, CoreAISettings.MaxLuaRepairRetries);
            Assert.AreEqual(0.99f, CoreAISettings.Temperature);

            // Cleanup
            Object.DestroyImmediate(go);
            Object.DestroyImmediate(settings);
        }
    }
}
