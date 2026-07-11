using CoreAI.Ai;
using CoreAI.Hub.UI;
using CoreAI.Infrastructure.Llm;
using NUnit.Framework;
using System.Reflection;
using UnityEngine.UIElements;

namespace CoreAI.Tests
{
    public sealed class HubSettingsStatisticsPageEditModeTests
    {
        private CoreAISettingsAsset _settings;

        [SetUp]
        public void SetUp()
        {
            _settings = UnityEngine.ScriptableObject.CreateInstance<CoreAISettingsAsset>();
            _settings.ConfigureClientOwnedApi("http://old/v1", "existing-key", "old-model");
            CoreAISettingsAsset.SetInstance(_settings);
        }

        [TearDown]
        public void TearDown()
        {
            CoreAISettingsAsset.ResetInstance();
            if (_settings != null)
            {
                UnityEngine.Object.DestroyImmediate(_settings);
            }
        }

        [Test]
        public void SettingsPage_BuildsEditableAiBackendSurface()
        {
            HubSettingsPage page = new(_settings);

            ScrollView root = page.CreatePageContent() as ScrollView;

            Assert.IsNotNull(root);
            Assert.IsNotNull(root.Q<DropdownField>());
            Assert.IsNotNull(root.Q<TextField>());
            Assert.IsNotNull(FindButton(root, "Apply"));
            Assert.IsNotNull(FindButton(root, "Test backend"));
            Assert.AreEqual("AI Settings", page.DisplayName);
        }

        [Test]
        public void SettingsPage_ApplyHttp_KeepsExistingKeyWhenFieldIsBlank()
        {
            HubSettingsPage page = new(_settings);
            ScrollView root = (ScrollView)page.CreatePageContent();

            DropdownField mode = root.Q<DropdownField>();
            TextField baseUrl = root.Query<TextField>().AtIndex(0);
            TextField apiKey = root.Query<TextField>().AtIndex(1);
            TextField model = root.Query<TextField>().AtIndex(2);

            mode.value = "HTTP API";
            baseUrl.value = "http://new/v1";
            apiKey.value = "";
            model.value = "new-model";

            InvokePrivate(page, "Apply");

            Assert.AreEqual(LlmExecutionMode.ClientOwnedApi, _settings.ExecutionMode);
            Assert.AreEqual("http://new/v1", _settings.ApiBaseUrl);
            Assert.AreEqual("existing-key", _settings.ApiKey);
            Assert.AreEqual("new-model", _settings.ModelName);
        }

        [Test]
        public void SettingsPage_ApplyHttp_WhenOverrideTemperatureOff_UsesModelDefault()
        {
            HubSettingsPage page = new(_settings);
            ScrollView root = (ScrollView)page.CreatePageContent();

            DropdownField mode = root.Q<DropdownField>();
            TextField baseUrl = root.Query<TextField>().AtIndex(0);
            TextField apiKey = root.Query<TextField>().AtIndex(1);
            TextField model = root.Query<TextField>().AtIndex(2);
            Toggle overrideTemp = root.Q<Toggle>();

            mode.value = "HTTP API";
            baseUrl.value = "http://new/v1";
            apiKey.value = "new-key";
            model.value = "new-model";
            overrideTemp.value = false;

            InvokePrivate(page, "Apply");

            Assert.AreEqual(LlmExecutionMode.ClientOwnedApi, _settings.ExecutionMode);
            Assert.IsFalse(_settings.OverrideTemperature,
                "With override off, the backend should use its own default sampling temperature.");
        }

        [Test]
        public void StatisticsPage_ShowsDerivedDiagnosticsAndReset()
        {
            InMemoryAiOrchestrationMetrics metrics = new();
            metrics.RecordLlmCompletion("writer", "t1", true, 100);
            metrics.RecordLlmCompletion("writer", "t2", false, 300);
            metrics.RecordStructuredRetry("writer", "t2", "invalid-json");
            metrics.RecordCommandPublished("writer", "t2");
            HubStatisticsPage page = new(metrics, _settings);

            ScrollView root = page.CreatePageContent() as ScrollView;

            Assert.IsNotNull(root);
            Assert.IsNotNull(FindButton(root, "Reset counters"));

            InvokePrivate(page, "ResetMetrics");

            Assert.AreEqual(0, metrics.TotalCompletions);
            Assert.AreEqual(0, metrics.CommandsPublished);
        }

        private static Button FindButton(VisualElement root, string text)
        {
            foreach (Button button in root.Query<Button>().ToList())
            {
                if (button.text == text)
                {
                    return button;
                }
            }

            return null;
        }

        private static void InvokePrivate(object target, string method)
        {
            target.GetType()
                .GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)
                .Invoke(target, null);
        }
    }
}