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

        [SetUp]
        public void SetUp()
        {
            _settings = UnityEngine.ScriptableObject.CreateInstance<CoreAISettingsAsset>();
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
            HubSettingsPage page = new(_settings, routingController: controller);
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
            HubSettingsPage page = new(_settings, routingController: controller);
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
            HubSettingsPage page = new(_settings);
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
            HubSettingsPage page = new(_settings);
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
            HubSettingsPage page = new(_settings, RuntimePlatform.WebGLPlayer);
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

            public event Action Changed;

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
                return Task.FromResult(new CoreAiRoutingUiResult(true));
            }

            public Task<CoreAiRoutingUiResult> RemoveEndpointAsync(
                string endpointId,
                CancellationToken cancellationToken = default)
            {
                Removed.Add(endpointId);
                _endpoints.RemoveAll(s => s.Descriptor.EndpointId == endpointId);
                Changed?.Invoke();
                return Task.FromResult(new CoreAiRoutingUiResult(true));
            }
        }
    }
}
