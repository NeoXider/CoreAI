using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Ai;
using CoreAI.Chat;
using CoreAI.Hub.UI;
using CoreAI.Infrastructure.Llm;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.UIElements;

namespace CoreAI.Tests.EditMode
{
    public sealed class HubSettingsRegressionEditModeTests
    {
        private CoreAISettingsAsset _settings;
        private readonly List<HubSettingsPage> _pages = new List<HubSettingsPage>();

        [SetUp]
        public void SetUp()
        {
            _settings = UnityEngine.ScriptableObject.CreateInstance<CoreAISettingsAsset>();
            CoreAISettingsAsset.SetInstance(_settings);
        }

        [TearDown]
        public void TearDown()
        {
            foreach (HubSettingsPage page in _pages)
            {
                page.OnDestroyed();
            }

            _pages.Clear();
            CoreAISettingsAsset.ResetInstance();
            if (_settings != null)
            {
                UnityEngine.Object.DestroyImmediate(_settings);
            }
        }

        [Test]
        public void DecideRemoveAction_CommitsOnlyForTheArmedEndpoint()
        {
            Assert.AreEqual(HubSettingsPage.RemoveAction.Arm, HubSettingsPage.DecideRemoveAction("", "a"));
            Assert.AreEqual(HubSettingsPage.RemoveAction.Commit, HubSettingsPage.DecideRemoveAction("a", "a"));
            Assert.AreEqual(HubSettingsPage.RemoveAction.Arm, HubSettingsPage.DecideRemoveAction("a", "b"));
            Assert.AreEqual(HubSettingsPage.RemoveAction.Arm, HubSettingsPage.DecideRemoveAction("a", ""));
            Assert.AreEqual(HubSettingsPage.RemoveAction.Arm, HubSettingsPage.DecideRemoveAction("", ""));
            Assert.AreEqual(HubSettingsPage.RemoveAction.Arm, HubSettingsPage.DecideRemoveAction(null, null));
        }

        [Test]
        public void EndpointList_Rebuild_KeepsTheArmedRowLabelledConfirm()
        {
            FakeRoutingController controller = new();
            controller.Add("alpha", "Alpha");
            controller.Add("beta", "Beta");
            HubSettingsPage page = Own(new HubSettingsPage(_settings, routingController: controller));
            page.CreatePageContent();

            Button armed = RemoveButton(page, "alpha");
            Invoke(page, "RemoveEndpointRow", "alpha", "Alpha", armed);
            Assert.AreEqual("Confirm?", armed.text);

            // A routing Changed notification rebuilds the whole list mid-confirm. The pending id must not
            // survive behind a button labelled "Remove" — that turned the next single click into an
            // unconfirmed delete.
            Invoke(page, "RefreshEndpointManagement");

            Button rebuilt = RemoveButton(page, "alpha");
            Assert.AreNotSame(armed, rebuilt, "The row button is recreated by the rebuild.");
            Assert.AreEqual("Confirm?", rebuilt.text);
            Assert.AreEqual("Remove", RemoveButton(page, "beta").text);
            Assert.AreSame(rebuilt, PendingRemoveButton(page),
                "The pending button must track the live row, not the detached one.");
        }

        [Test]
        public void EndpointList_RemovingTheArmedEndpoint_ClearsThePendingState()
        {
            FakeRoutingController controller = new();
            controller.Add("alpha", "Alpha");
            HubSettingsPage page = Own(new HubSettingsPage(_settings, routingController: controller));
            page.CreatePageContent();

            Button remove = RemoveButton(page, "alpha");
            Invoke(page, "RemoveEndpointRow", "alpha", "Alpha", remove);
            Invoke(page, "RemoveEndpointRow", "alpha", "Alpha", RemoveButton(page, "alpha"));

            CollectionAssert.AreEqual(new[] { "alpha" }, controller.Removed);
            Assert.IsNull(PendingRemoveButton(page));
            Assert.AreEqual("", ArmedEndpointId(page));
        }

        [Test]
        public void ModeOption_RoundTripsForEveryExecutionMode()
        {
            foreach (LlmExecutionMode mode in Enum.GetValues(typeof(LlmExecutionMode)))
            {
                Assert.AreEqual(
                    mode,
                    HubSettingsPage.ResolveSelectedMode(HubSettingsPage.ModeToOption(mode), mode),
                    "Leaving the Mode dropdown untouched must never rewrite the live execution mode.");
            }
        }

        [Test]
        public void ModeOption_StillHonoursAnExplicitDropdownChange()
        {
            Assert.AreEqual(
                LlmExecutionMode.Offline,
                HubSettingsPage.ResolveSelectedMode(
                    HubSettingsPage.ModeToOption(LlmExecutionMode.Offline), LlmExecutionMode.ClientLimited));
            Assert.AreEqual(
                LlmExecutionMode.ClientOwnedApi,
                HubSettingsPage.ResolveSelectedMode(
                    HubSettingsPage.ModeToOption(LlmExecutionMode.ClientOwnedApi), LlmExecutionMode.Offline));
            Assert.AreEqual(
                LlmExecutionMode.LocalModel,
                HubSettingsPage.ResolveSelectedMode(
                    HubSettingsPage.ModeToOption(LlmExecutionMode.LocalModel), LlmExecutionMode.ServerManagedApi));
        }

        [Test]
        public void VisionOption_RoundTripsForEveryVisionMode()
        {
            foreach (VisionSupportMode mode in Enum.GetValues(typeof(VisionSupportMode)))
            {
                Assert.AreEqual(
                    mode,
                    HubSettingsPage.OptionToVisionMode(HubSettingsPage.VisionModeToOption(mode)));
            }
        }

        [Test]
        public void Apply_KeepsClientLimitedMode_WhenTheUserDidNotChangeIt()
        {
            _settings.ConfigureClientLimited("http://old/v1", "key", "old-model", 5, 1000);
            HubSettingsPage page = Own(new HubSettingsPage(_settings));
            ScrollView root = (ScrollView)page.CreatePageContent();
            root.Query<TextField>().AtIndex(0).value = "http://new/v1";
            root.Query<TextField>().AtIndex(2).value = "new-model";

            Invoke(page, "Apply");

            Assert.AreEqual(LlmExecutionMode.ClientLimited, _settings.ExecutionMode,
                "Applying an unrelated field must not downgrade ClientLimited to ClientOwnedApi.");
            Assert.AreEqual("http://new/v1", _settings.ApiBaseUrl);
            Assert.AreEqual("new-model", _settings.ModelName);
        }

        [Test]
        public void Apply_KeepsServerManagedMode_WhenTheUserDidNotChangeIt()
        {
            _settings.ConfigureServerManagedApi("http://proxy/v1", "old-model", "token");
            HubSettingsPage page = Own(new HubSettingsPage(_settings));
            ScrollView root = (ScrollView)page.CreatePageContent();
            root.Query<TextField>().AtIndex(2).value = "new-model";

            Invoke(page, "Apply");

            Assert.AreEqual(LlmExecutionMode.ServerManagedApi, _settings.ExecutionMode);
            Assert.AreEqual("new-model", _settings.ModelName);
        }

        [Test]
        public void BrowserSettings_DisablesLocalModelAndShowsActionableLimitation()
        {
            _settings.ConfigureLlmUnity(ggufPath: "browser.gguf");
            HubSettingsPage page = Own(new HubSettingsPage(_settings, RuntimePlatform.WebGLPlayer));
            ScrollView root = (ScrollView)page.CreatePageContent();

            DropdownField mode = root.Q<DropdownField>();
            CollectionAssert.DoesNotContain(mode.choices, HubSettingsPage.ModeToOption(LlmExecutionMode.LocalModel));
            CollectionAssert.DoesNotContain(
                HubSettingsPage.EndpointKindOptionsForPlatform(RuntimePlatform.WebGLPlayer),
                "LLMUnity");
            CollectionAssert.Contains(
                HubSettingsPage.ModeOptionsForPlatform(RuntimePlatform.WindowsPlayer),
                HubSettingsPage.ModeToOption(LlmExecutionMode.LocalModel));

            Button apply = FindButton(root, "Apply");
            Assert.IsNotNull(apply);
            Assert.IsFalse(apply.enabledSelf, "A persisted local mode must not be actionable in the browser.");
            Assert.IsTrue(ContainsLabel(root, LocalModelPlatformSupport.BrowserUnavailableMessage),
                "The page must explain the browser limitation instead of reporting only 'no live scope'.");

            Invoke(page, "Apply");
            Assert.IsTrue(ContainsLabel(root, LocalModelPlatformSupport.BrowserUnavailableMessage));
        }

        [TestCase(LlmEndpointKind.HttpOpenAi)]
        [TestCase(LlmEndpointKind.LlmUnity)]
        public void EndpointEditor_RoundTripsRequestSettingsAndPreservesHiddenFields(LlmEndpointKind kind)
        {
            FakeRoutingController controller = new();
            controller.Add("configured", "Configured");
            LlmEndpointDescriptor original = controller.GetEndpoints()[0].Descriptor;
            original.Kind = kind;
            original.Model = "model-alias";
            original.LocalModelPath = kind == LlmEndpointKind.LlmUnity ? "model.gguf" : "";
            original.ReasoningEffort = LlmReasoningEffort.High;
            original.ReasoningDialect = LlmReasoningDialect.OpenRouter;
            original.Temperature = 0.75f;
            original.OverrideMaxTokens = true;
            original.MaxTokens = 2048;
            original.ExtraBodyJson = "{\"custom_option\":true}";
            original.ThinkingBudgetTokens = 512;
            HubSettingsPage page = Own(new HubSettingsPage(_settings, RuntimePlatform.WindowsPlayer, controller));
            page.CreatePageContent();

            Invoke(page, "LoadEndpointById", "configured");
            LlmEndpointDescriptor edited = ReadEndpoint(page);

            Assert.AreEqual(LlmReasoningEffort.High, edited.ReasoningEffort);
            Assert.AreEqual(LlmReasoningDialect.OpenRouter, edited.ReasoningDialect);
            Assert.AreEqual(0.75f, edited.Temperature);
            Assert.IsTrue(edited.OverrideMaxTokens);
            Assert.AreEqual(2048, edited.MaxTokens);
            Assert.AreEqual(original.ExtraBodyJson, edited.ExtraBodyJson);
            Assert.AreEqual(512, edited.ThinkingBudgetTokens);
            Assert.AreEqual("model-alias", edited.Model);
            Assert.AreEqual(original.LocalModelPath, edited.LocalModelPath);
            Assert.AreNotSame(original, edited);
        }

        [Test]
        public void EndpointEditor_ChangesRequestSettingsWithoutMutatingTheStoredDescriptor()
        {
            FakeRoutingController controller = new();
            controller.Add("configured", "Configured");
            LlmEndpointDescriptor original = controller.GetEndpoints()[0].Descriptor;
            original.Kind = LlmEndpointKind.HttpOpenAi;
            original.Model = "model";
            HubSettingsPage page = Own(new HubSettingsPage(_settings, routingController: controller));
            page.CreatePageContent();
            Invoke(page, "LoadEndpointById", "configured");

            ((DropdownField)Field(page, "_endpointReasoningEffort").GetValue(page)).value = "Low";
            ((DropdownField)Field(page, "_endpointReasoningDialect").GetValue(page)).value = "OpenAi";
            ((Toggle)Field(page, "_endpointOverrideTemperature").GetValue(page)).value = true;
            ((FloatField)Field(page, "_endpointTemperature").GetValue(page)).value = 0f;
            ((Toggle)Field(page, "_endpointOverrideMaxTokens").GetValue(page)).value = true;
            ((IntegerField)Field(page, "_endpointMaxTokens").GetValue(page)).value = 0;
            LlmEndpointDescriptor edited = ReadEndpoint(page);

            Assert.AreEqual(LlmReasoningEffort.Low, edited.ReasoningEffort);
            Assert.AreEqual(LlmReasoningDialect.OpenAi, edited.ReasoningDialect);
            Assert.AreEqual(0f, edited.Temperature);
            Assert.IsTrue(edited.OverrideMaxTokens, "An explicit zero cap must preserve the override flag.");
            Assert.AreEqual(0, edited.MaxTokens);
            Assert.AreEqual(LlmReasoningEffort.Default, original.ReasoningEffort);
            Assert.AreEqual(LlmReasoningDialect.Auto, original.ReasoningDialect);
            Assert.IsNull(original.Temperature);
            Assert.IsFalse(original.OverrideMaxTokens);
        }

        [Test]
        public void EndpointEditor_DisablingOverridesRestoresNullableDefaults()
        {
            FakeRoutingController controller = new();
            controller.Add("configured", "Configured");
            LlmEndpointDescriptor original = controller.GetEndpoints()[0].Descriptor;
            original.Kind = LlmEndpointKind.HttpOpenAi;
            original.Model = "model";
            original.Temperature = 0.6f;
            original.MaxTokens = 1000;
            HubSettingsPage page = Own(new HubSettingsPage(_settings, routingController: controller));
            EndpointEditorHostWindow window = ScriptableObject.CreateInstance<EndpointEditorHostWindow>();
            try
            {
                // WHY: change callbacks require an attached panel; detached value setters only change stored values.
                window.Show();
                window.rootVisualElement.Add((VisualElement)page.CreatePageContent());
                Invoke(page, "LoadEndpointById", "configured");

                Toggle overrideTokens = (Toggle)Field(page, "_endpointOverrideMaxTokens").GetValue(page);
                Assert.IsTrue(overrideTokens.value, "Legacy positive caps must remain enabled when opened.");
                ((Toggle)Field(page, "_endpointOverrideTemperature").GetValue(page)).value = false;
                overrideTokens.value = false;
                LlmEndpointDescriptor edited = ReadEndpoint(page);

                Assert.IsNull(edited.Temperature);
                Assert.IsFalse(edited.OverrideMaxTokens);
                Assert.AreEqual(0, edited.MaxTokens);
                Assert.IsFalse(((FloatField)Field(page, "_endpointTemperature").GetValue(page)).enabledSelf);
                Assert.IsFalse(((IntegerField)Field(page, "_endpointMaxTokens").GetValue(page)).enabledSelf);

                Invoke(page, "ClearEndpointEditor");
                ((TextField)Field(page, "_endpointName").GetValue(page)).value = "New endpoint";
                LlmEndpointDescriptor fresh = ReadEndpoint(page);
                Assert.AreEqual(LlmReasoningEffort.Default, fresh.ReasoningEffort);
                Assert.AreEqual(LlmReasoningDialect.Auto, fresh.ReasoningDialect);
                Assert.IsNull(fresh.Temperature);
                Assert.IsFalse(fresh.OverrideMaxTokens);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(window);
            }
        }

        [Test]
        public void EndpointSave_NotSavedPreservesTheEditedDescriptorAndRetryKey()
        {
            WithoutSynchronizationContext(() =>
            {
                FakeRoutingController controller = new();
                controller.Add("alpha", "Alpha");
                controller.SaveHandler = (_, __, ___) => Task.FromResult(
                    new CoreAiRoutingUiResult(false, "store refused"));
                HubSettingsPage page = Own(new HubSettingsPage(_settings, routingController: controller));
                page.CreatePageContent();
                try
                {
                    Invoke(page, "LoadEndpointById", "alpha");
                    Text(page, "_endpointName").value = "Unsaved name";
                    Text(page, "_endpointModel").value = "unsaved-model";
                    Text(page, "_endpointSessionKey").value = "retry-key";

                    Invoke(page, "SaveEndpoint");

                    Assert.AreEqual("Unsaved name", Text(page, "_endpointName").value);
                    Assert.AreEqual("unsaved-model", Text(page, "_endpointModel").value);
                    Assert.AreEqual("retry-key", Text(page, "_endpointSessionKey").value);
                    Assert.AreEqual("alpha", Field(page, "_editingEndpointId").GetValue(page));
                    Assert.AreEqual("store refused", ((Label)Field(page, "_endpointOperationStatus").GetValue(page)).text);
                    Assert.IsTrue(((Button)Field(page, "_endpointSaveButton").GetValue(page)).enabledSelf);
                }
                finally
                {
                    page.OnDestroyed();
                }
            });
        }

        [Test]
        public void EndpointSave_OldCompletionCannotOverwriteAnotherDraftOrReleaseItsBusyState()
        {
            WithoutSynchronizationContext(() =>
            {
                FakeRoutingController controller = new();
                controller.Add("alpha", "Alpha");
                controller.Add("beta", "Beta");
                TaskCompletionSource<CoreAiRoutingUiResult> first = new();
                TaskCompletionSource<CoreAiRoutingUiResult> second = new();
                Queue<Task<CoreAiRoutingUiResult>> completions = new();
                completions.Enqueue(first.Task);
                completions.Enqueue(second.Task);
                controller.SaveHandler = (_, __, ___) => completions.Dequeue();
                HubSettingsPage page = Own(new HubSettingsPage(_settings, routingController: controller));
                page.CreatePageContent();
                try
                {
                    Invoke(page, "LoadEndpointById", "alpha");
                    Invoke(page, "SaveEndpoint");
                    Invoke(page, "LoadEndpointById", "beta");
                    Text(page, "_endpointName").value = "Beta draft";
                    Text(page, "_endpointSessionKey").value = "beta-key";
                    Invoke(page, "SaveEndpoint");
                    Assert.IsTrue(controller.SaveTokens[0].IsCancellationRequested);

                    first.SetResult(new CoreAiRoutingUiResult(true, "Old success"));

                    Assert.AreEqual("beta", Field(page, "_editingEndpointId").GetValue(page));
                    Assert.AreEqual("Beta draft", Text(page, "_endpointName").value);
                    Assert.AreEqual("beta-key", Text(page, "_endpointSessionKey").value);
                    Assert.IsFalse(((Button)Field(page, "_endpointSaveButton").GetValue(page)).enabledSelf,
                        "An older completion cannot enable Save while the current operation is still running.");
                    second.SetResult(new CoreAiRoutingUiResult(false, "New refusal"));
                    Assert.AreEqual("beta-key", Text(page, "_endpointSessionKey").value);
                    Assert.IsTrue(((Button)Field(page, "_endpointSaveButton").GetValue(page)).enabledSelf);
                }
                finally
                {
                    page.OnDestroyed();
                }
            });
        }

        [Test]
        public void EndpointSave_ControllerReplacementFencesTheOldResultAndCancelsItsToken()
        {
            WithoutSynchronizationContext(() =>
            {
                FakeRoutingController firstController = new();
                firstController.Add("same", "Old");
                TaskCompletionSource<CoreAiRoutingUiResult> completion = new();
                firstController.SaveHandler = (_, __, ___) => completion.Task;
                FakeRoutingController replacement = new();
                replacement.Add("same", "Replacement");
                HubSettingsPage page = Own(new HubSettingsPage(_settings, routingController: firstController));
                page.CreatePageContent();
                try
                {
                    Invoke(page, "LoadEndpointById", "same");
                    Invoke(page, "SaveEndpoint");
                    Invoke(page, "AttachRoutingController", replacement);
                    Invoke(page, "LoadEndpointById", "same");
                    Text(page, "_endpointName").value = "Replacement draft";
                    Text(page, "_endpointSessionKey").value = "replacement-key";
                    completion.SetResult(new CoreAiRoutingUiResult(true));

                    Assert.IsTrue(firstController.SaveTokens[0].IsCancellationRequested);
                    Assert.AreEqual("Replacement draft", Text(page, "_endpointName").value);
                    Assert.AreEqual("replacement-key", Text(page, "_endpointSessionKey").value);
                    Assert.AreEqual(0, firstController.Subscribers);
                    Assert.AreEqual(1, replacement.Subscribers);
                }
                finally
                {
                    page.OnDestroyed();
                }

                Assert.AreEqual(0, replacement.Subscribers);
            });
        }

        [TestCase(false)]
        [TestCase(true)]
        public void EndpointSave_FoldoutToggleDuringPendingSave_KeepsReceiptAndClearsAcceptedKey(bool toggleFoldout)
        {
            WithoutSynchronizationContext(() =>
            {
                FakeRoutingController controller = new();
                controller.Add("alpha", "Alpha");
                TaskCompletionSource<CoreAiRoutingUiResult> completion = new();
                controller.SaveHandler = (_, __, ___) => completion.Task;
                HubSettingsPage page = Own(new HubSettingsPage(_settings, routingController: controller));
                EndpointEditorHostWindow window = ScriptableObject.CreateInstance<EndpointEditorHostWindow>();
                try
                {
                    window.Show();
                    window.rootVisualElement.Add((VisualElement)page.CreatePageContent());
                    Invoke(page, "LoadEndpointById", "alpha");
                    Text(page, "_endpointSessionKey").value = "accepted-key";

                    Invoke(page, "SaveEndpoint");

                    if (toggleFoldout)
                    {
                        Foldout advanced = (Foldout)Field(page, "_endpointAdvanced").GetValue(page);
                        advanced.value = !advanced.value;
                    }

                    completion.SetResult(new CoreAiRoutingUiResult(true));

                    Label status = (Label)Field(page, "_endpointOperationStatus").GetValue(page);
                    Assert.IsTrue(status.text.Contains("saved", StringComparison.OrdinalIgnoreCase) ||
                        status.text.Contains("ready", StringComparison.OrdinalIgnoreCase) ||
                        status.text.Contains("Endpoint", StringComparison.OrdinalIgnoreCase),
                        "Successful completion should settle the operation status. Actual: " + status.text);
                    Assert.AreEqual("", Text(page, "_endpointSessionKey").value,
                        "An accepted session key must be cleared after a successful save.");
                    Assert.IsTrue(((Button)Field(page, "_endpointSaveButton").GetValue(page)).enabledSelf,
                        "Save must be re-enabled once the async operation ends.");
                }
                finally
                {
                    UnityEngine.Object.DestroyImmediate(window);
                    page.OnDestroyed();
                }
            });
        }

        [TestCase(false)]
        [TestCase(true)]
        public void EndpointSave_RealDescriptorEditDuringPendingSave_StillInvalidatesReceipt(bool editBoolean)
        {
            WithoutSynchronizationContext(() =>
            {
                FakeRoutingController controller = new();
                controller.Add("alpha", "Alpha");
                TaskCompletionSource<CoreAiRoutingUiResult> completion = new();
                controller.SaveHandler = (_, __, ___) => completion.Task;
                HubSettingsPage page = Own(new HubSettingsPage(_settings, routingController: controller));
                EndpointEditorHostWindow window = ScriptableObject.CreateInstance<EndpointEditorHostWindow>();
                try
                {
                    window.Show();
                    window.rootVisualElement.Add((VisualElement)page.CreatePageContent());
                    Invoke(page, "LoadEndpointById", "alpha");
                    Text(page, "_endpointSessionKey").value = "accepted-key";

                    Invoke(page, "SaveEndpoint");

                    Toggle active = (Toggle)Field(page, "_endpointActive").GetValue(page);
                    bool originalActive = active.value;
                    if (editBoolean) { active.value = !originalActive; }
                    else { Text(page, "_endpointName").value = "edited-during-wait"; }

                    completion.SetResult(new CoreAiRoutingUiResult(true));

                    Assert.AreEqual(editBoolean ? "Alpha" : "edited-during-wait", Text(page, "_endpointName").value,
                        "A real descriptor edit must keep the draft intact.");
                    Assert.AreEqual(editBoolean ? !originalActive : originalActive, active.value);
                    Assert.AreEqual("accepted-key", Text(page, "_endpointSessionKey").value,
                        "An invalidated receipt must not clear the accepted key.");
                }
                finally
                {
                    UnityEngine.Object.DestroyImmediate(window);
                    page.OnDestroyed();
                }
            });
        }

        [Test]
        public void EndpointSave_DestroyedViewCancelsAndIgnoresALateSuccessfulCompletion()
        {
            WithoutSynchronizationContext(() =>
            {
                FakeRoutingController controller = new();
                controller.Add("alpha", "Alpha");
                TaskCompletionSource<CoreAiRoutingUiResult> completion = new();
                controller.SaveHandler = (_, __, ___) => completion.Task;
                HubSettingsPage page = Own(new HubSettingsPage(_settings, routingController: controller));
                page.CreatePageContent();
                Invoke(page, "LoadEndpointById", "alpha");
                Text(page, "_endpointSessionKey").value = "private draft";
                Invoke(page, "SaveEndpoint");

                page.OnDestroyed();
                completion.SetResult(new CoreAiRoutingUiResult(true));

                Assert.IsTrue(controller.SaveTokens[0].IsCancellationRequested);
                Assert.AreEqual("private draft", Text(page, "_endpointSessionKey").value);
                Assert.AreEqual(0, controller.Subscribers);
                Assert.IsFalse(((Button)Field(page, "_endpointSaveButton").GetValue(page)).enabledSelf);
            });
        }

        [Test]
        public void EndpointControllerChange_QueuedBeforeDestructionCannotReattachTheView()
        {
            ICoreAiRoutingUiController previousController = CoreAiRoutingUi.Controller;
            SynchronizationContext previousContext = SynchronizationContext.Current;
            RecordingContext context = new();
            FakeRoutingController first = new();
            FakeRoutingController replacement = new();
            HubSettingsPage page = null;
            try
            {
                CoreAiRoutingUi.Controller = first;
                SynchronizationContext.SetSynchronizationContext(context);
                page = Own(new HubSettingsPage(_settings));
                page.CreatePageContent();
                Assert.AreEqual(1, first.Subscribers, first.DescribeSubscribers());
                SynchronizationContext.SetSynchronizationContext(null);
                CoreAiRoutingUi.Controller = replacement;
                Assert.AreEqual(1, context.Pending.Count);

                page.OnDestroyed();
                Assert.AreEqual(0, first.Subscribers);
                Assert.AreEqual(0, replacement.Subscribers);
                foreach (Action callback in context.Pending)
                {
                    callback();
                }

                Assert.AreEqual(0, first.Subscribers);
                Assert.AreEqual(0, replacement.Subscribers,
                    "A queued global-controller event must not revive a destroyed page subscription.");
            }
            finally
            {
                page?.OnDestroyed();
                CoreAiRoutingUi.Controller = previousController;
                SynchronizationContext.SetSynchronizationContext(previousContext);
            }
        }

        private static TextField Text(HubSettingsPage page, string name)
        {
            return (TextField)Field(page, name).GetValue(page);
        }

        private HubSettingsPage Own(HubSettingsPage page)
        {
            _pages.Add(page);
            return page;
        }

        private static void WithoutSynchronizationContext(Action action)
        {
            SynchronizationContext previous = SynchronizationContext.Current;
            try
            {
                SynchronizationContext.SetSynchronizationContext(null);
                action();
            }
            finally
            {
                SynchronizationContext.SetSynchronizationContext(previous);
            }
        }

        private sealed class RecordingContext : SynchronizationContext
        {
            public List<Action> Pending { get; } = new();
            public override void Post(SendOrPostCallback callback, object state)
            {
                Pending.Add(() => callback(state));
            }
        }

        private sealed class EndpointEditorHostWindow : UnityEditor.EditorWindow
        {
        }

        private static LlmEndpointDescriptor ReadEndpoint(HubSettingsPage page)
        {
            MethodInfo read = typeof(HubSettingsPage).GetMethod(
                "ReadEndpointEditor", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(read);
            return (LlmEndpointDescriptor)read.Invoke(page, null);
        }

        private static Button RemoveButton(HubSettingsPage page, string endpointId)
        {
            VisualElement list = (VisualElement)Field(page, "_endpointListContainer").GetValue(page);
            foreach (VisualElement row in list.Children())
            {
                Label label = row.Q<Label>();
                if (label != null &&
                    label.text.ToLowerInvariant().Contains(endpointId.ToLowerInvariant()))
                {
                    List<Button> buttons = row.Query<Button>().ToList();
                    return buttons[buttons.Count - 1];
                }
            }

            return null;
        }

        private static Button FindButton(VisualElement root, string text)
        {
            foreach (Button button in root.Query<Button>().ToList())
            {
                if (string.Equals(button.text, text, StringComparison.Ordinal))
                {
                    return button;
                }
            }

            return null;
        }

        private static bool ContainsLabel(VisualElement root, string text)
        {
            foreach (Label label in root.Query<Label>().ToList())
            {
                if (string.Equals(label.text, text, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }

        private static Button PendingRemoveButton(HubSettingsPage page)
        {
            return (Button)Field(page, "_pendingRemoveButton").GetValue(page);
        }

        private static string ArmedEndpointId(HubSettingsPage page)
        {
            return (string)Field(page, "_removeConfirmEndpointId").GetValue(page);
        }

        private static FieldInfo Field(object target, string name)
        {
            return target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
        }

        private static void Invoke(object target, string method, params object[] args)
        {
            MethodInfo info = target.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(info, "Missing method " + method);
            info.Invoke(target, args.Length == 0 ? null : args);
        }

        private sealed class FakeRoutingController : ICoreAiRoutingUiController
        {
            private readonly List<LlmEndpointSnapshot> _endpoints = new();
            private Action _changed;

            public event Action Changed { add => _changed += value; remove => _changed -= value; }
            public int Subscribers => _changed?.GetInvocationList().Length ?? 0;

            public string DescribeSubscribers()
            {
                List<string> owners = new();
                foreach (Delegate subscriber in _changed?.GetInvocationList() ?? Array.Empty<Delegate>())
                {
                    object target = subscriber.Target;
                    UnityEngine.Object unityTarget = target as UnityEngine.Object;
                    string state = ReferenceEquals(unityTarget, null)
                        ? "" : ", UnityDestroyed=" + (unityTarget == null);
                    owners.Add(subscriber.Method.DeclaringType?.FullName + "." + subscriber.Method.Name
                        + " target=" + (target?.GetType().FullName ?? "<static>") + state);
                }
                return string.Join(Environment.NewLine, owners);
            }

            public Func<LlmEndpointDescriptor, string, CancellationToken, Task<CoreAiRoutingUiResult>> SaveHandler;
            public List<CancellationToken> SaveTokens { get; } = new();

            public List<string> Removed { get; } = new();

            public void Add(string endpointId, string displayName)
            {
                _endpoints.Add(new LlmEndpointSnapshot
                {
                    Descriptor = new LlmEndpointDescriptor
                    {
                        EndpointId = endpointId,
                        DisplayName = displayName,
                        Kind = LlmEndpointKind.Offline,
                        ContextWindowTokens = 1024,
                        ParallelSlots = 1
                    },
                    State = LlmEndpointLifecycleState.Ready
                });
            }

            public IReadOnlyList<LlmEndpointSnapshot> GetEndpoints()
            {
                return _endpoints;
            }

            public IReadOnlyList<LlmRuntimeProfile> GetProfiles()
            {
                return Array.Empty<LlmRuntimeProfile>();
            }

            public string GetProfileForRole(string roleId)
            {
                return "";
            }

            public CoreAiRoutingUiResult AssignProfileToRole(string roleId, string profileId)
            {
                return new CoreAiRoutingUiResult(true);
            }

            public Task<CoreAiRoutingUiResult> SaveEndpointAsync(
                LlmEndpointDescriptor endpoint,
                string sessionApiKey,
                CancellationToken cancellationToken = default)
            {
                SaveTokens.Add(cancellationToken);
                return SaveHandler == null ? Task.FromResult(new CoreAiRoutingUiResult(true))
                    : SaveHandler(endpoint, sessionApiKey, cancellationToken);
            }

            public Task<CoreAiRoutingUiResult> RemoveEndpointAsync(
                string endpointId,
                CancellationToken cancellationToken = default)
            {
                Removed.Add(endpointId);
                _endpoints.RemoveAll(s => s.Descriptor.EndpointId == endpointId);
                _changed?.Invoke();
                return Task.FromResult(new CoreAiRoutingUiResult(true));
            }
        }
    }
}
