using CoreAI;
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
            Toggle overrideTemp = FindToggle(root, "Override temperature");

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
        public void SettingsPage_ModelPicker_SelectingDiscoveredModel_WritesHttpModelField()
        {
            HubSettingsPage page = new(_settings);
            ScrollView root = (ScrollView)page.CreatePageContent();

            DropdownField mode = root.Q<DropdownField>();
            TextField model = root.Query<TextField>().AtIndex(2);
            mode.value = "HTTP API";

            DropdownField modelPicker = FindDropdown(root, "Discovered models");
            Assert.IsNotNull(modelPicker);

            CoreAiModelListResult result = new(true, new[] { "llama-3.1-8b", "qwen3.5-4b" }, "");
            InvokePrivateStatic(typeof(HubSettingsPage), "PopulateModelPicker", modelPicker, result);

            // The picker must not silently land on a real model without notifying (that was the bug:
            // the first fetched model used to be pre-selected via SetValueWithoutNotify, so it never
            // reached HTTP model unless the user reselected a different entry and back).
            Assert.AreEqual("llama-3.1-8b", modelPicker.choices[1]);
            Assert.AreNotEqual("llama-3.1-8b", modelPicker.value);
            Assert.AreEqual("old-model", model.value, "Populating the picker alone must not touch HTTP model.");

            // Picking the first real model — previously a silent no-op — must now reach HTTP model.
            // WHY: a detached DropdownField dispatches no ChangeEvent (event delivery needs an attached
            // panel), so drive the exact copy the picker's callback runs instead of setting picker.value.
            InvokePrivateStatic(typeof(HubSettingsPage), "CopyPickedModelInto", model, "llama-3.1-8b");
            Assert.AreEqual("llama-3.1-8b", model.value);

            // The picker must still refuse to copy a non-model prompt into HTTP model.
            InvokePrivateStatic(typeof(HubSettingsPage), "CopyPickedModelInto", model, modelPicker.choices[0]);
            Assert.AreEqual("llama-3.1-8b", model.value, "Selecting the prompt entry must not overwrite HTTP model.");
        }

        [Test]
        public void SettingsPage_RefreshFromStatus_SyncsModeWhenNotFocused()
        {
            HubSettingsPage page = new(_settings);
            ScrollView root = (ScrollView)page.CreatePageContent();
            DropdownField mode = root.Q<DropdownField>();

            Assert.AreEqual("HTTP API", mode.value);

            // Simulate a stale display (e.g. left over from a prior status) and confirm a normal,
            // unfocused refresh still re-syncs it from the persisted backend status.
            mode.SetValueWithoutNotify("Auto");
            InvokePrivate(page, "RefreshFromStatus");

            Assert.AreEqual("HTTP API", mode.value,
                "Without focus, RefreshFromStatus must still re-sync Mode from the persisted status.");
        }

        [Test]
        public void IsFocusWithin_ReturnsFalse_ForUnattachedElement()
        {
            VisualElement element = new();

            bool result = (bool)typeof(HubSettingsPage)
                .GetMethod("IsFocusWithin", BindingFlags.Static | BindingFlags.NonPublic)
                .Invoke(null, new object[] { element });

            Assert.IsFalse(result);
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

        // WHY: query by label, not root.Q<Toggle>() — the Advanced foldout's own expand toggle is a
        // Toggle too, so the first Toggle in the tree is no longer the Override temperature one.
        private static Toggle FindToggle(VisualElement root, string label)
        {
            foreach (Toggle toggle in root.Query<Toggle>().ToList())
            {
                if (toggle.label == label)
                {
                    return toggle;
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

        private static void InvokePrivateStatic(System.Type type, string method, params object[] args)
        {
            type.GetMethod(method, BindingFlags.Static | BindingFlags.NonPublic)
                .Invoke(null, args);
        }

        private static DropdownField FindDropdown(VisualElement root, string label)
        {
            foreach (DropdownField dropdown in root.Query<DropdownField>().ToList())
            {
                if (dropdown.label == label)
                {
                    return dropdown;
                }
            }

            return null;
        }
    }
}
