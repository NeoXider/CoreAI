using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Chat;
using CoreAI.Hub.UI;
using CoreAI.Ai;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace CoreAI.Tests
{
    public sealed class CoreAiRoutingUiEditModeTests
    {
        private FakeController _controller;

        [SetUp]
        public void SetUp()
        {
            _controller = new FakeController();
            CoreAiRoutingUi.Controller = _controller;
        }

        [TearDown]
        public void TearDown()
        {
            CoreAiRoutingUi.Controller = null;
        }

        [Test]
        public void ChatApiSelector_IsHiddenUntilExpanded_AndUsesActiveAgentProfile()
        {
            GameObject gameObject = new("RoutingChatTest");
            try
            {
                CoreAiChatPanel panel = gameObject.AddComponent<CoreAiChatPanel>();
                VisualElement header = new();
                Label title = new("Chat");
                header.Add(title);
                SetField(panel, "HeaderTitle", title);

                panel.EnableApiSwitching();

                DropdownField dropdown = header.Q<DropdownField>(className: "coreai-chat-api-dropdown");
                Assert.IsNotNull(dropdown);
                Assert.AreEqual(DisplayStyle.None, dropdown.style.display.value);
                Assert.AreEqual("profile-a", panel.SelectedRoutingProfileId);

                Invoke(panel, "ToggleApiProfileSelector");

                Assert.IsTrue(panel.IsApiSelectorExpanded);
                Assert.AreEqual(DisplayStyle.Flex, dropdown.style.display.value);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(gameObject);
            }
        }

        [Test]
        public void ChatApiSelector_AutomaticClearsPersistedAgentOverride()
        {
            GameObject gameObject = new("RoutingChatAutomaticTest");
            try
            {
                CoreAiChatPanel panel = gameObject.AddComponent<CoreAiChatPanel>();
                VisualElement header = new();
                Label title = new("Chat");
                header.Add(title);
                SetField(panel, "HeaderTitle", title);

                panel.EnableApiSwitching();
                DropdownField dropdown = header.Q<DropdownField>(className: "coreai-chat-api-dropdown");
                string automaticLabel = dropdown.choices[0];
                using (ChangeEvent<string> evt = ChangeEvent<string>.GetPooled(dropdown.value, automaticLabel))
                {
                    dropdown.SetValueWithoutNotify(automaticLabel);
                    typeof(CoreAiChatPanel).GetMethod("OnApiProfileChanged",
                            BindingFlags.Instance | BindingFlags.NonPublic)
                        ?.Invoke(panel, new object[] { evt });
                }

                Assert.AreEqual(string.Empty, _controller.GetProfileForRole("SmartChat"));
                Assert.AreEqual(string.Empty, panel.SelectedRoutingProfileId);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(gameObject);
            }
        }

        [Test]
        public void ChatApiSelector_ZeroProfilesShowsAutomaticReadableState()
        {
            _controller.HasProfiles = false;
            GameObject gameObject = new("RoutingChatEmptyTest");
            try
            {
                CoreAiChatPanel panel = gameObject.AddComponent<CoreAiChatPanel>();
                VisualElement header = new();
                Label title = new("Chat");
                header.Add(title);
                SetField(panel, "HeaderTitle", title);

                panel.EnableApiSwitching();

                Assert.AreEqual(string.Empty, panel.SelectedRoutingProfileId);
                Button toggle = header.Q<Button>(className: "coreai-chat-api-toggle");
                Assert.AreEqual("API · Auto", toggle.text);
                Invoke(panel, "ToggleApiProfileSelector");
                Assert.That(header.Q<Label>(className: "coreai-chat-api-status").text,
                    Does.Contain("No API profiles"));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(gameObject);
            }
        }

        [Test]
        public void HubEndpointEditor_SendsSessionKeyWriteOnly_AndAssignsAgentProfile()
        {
            HubSettingsPage page = new(routingController: _controller);
            ScrollView root = (ScrollView)page.CreatePageContent();

            FindTextField(root, "Name").value = "Production API";
            FindTextField(root, "Endpoint ID").value = "production";
            FindTextField(root, "Base URL").value = "https://example.test/v1";
            FindTextField(root, "Model / GGUF").value = "model-1";
            FindTextField(root, "LLMUnity agent name").value = "QwenAgent";
            FindTextField(root, "Secret reference").value = "OPENAI_PROD";
            TextField sessionKey = FindTextField(root, "Session API key");
            sessionKey.value = "session-secret";

            InvokePage(page, "SaveEndpoint");

            Assert.IsNotNull(_controller.LastSavedEndpoint);
            Assert.AreEqual("production", _controller.LastSavedEndpoint.EndpointId);
            Assert.AreEqual("OPENAI_PROD", _controller.LastSavedEndpoint.SecretReference);
            Assert.AreEqual("QwenAgent", _controller.LastSavedEndpoint.UnityAgentName);
            Assert.AreEqual("session-secret", _controller.LastSessionKey);
            Assert.AreEqual(string.Empty, sessionKey.value);
            Assert.AreNotEqual("session-secret", _controller.LastSavedEndpoint.SecretReference);

            DropdownField agent = FindDropdown(root, "Agent");
            DropdownField profile = FindDropdown(root, "API profile");
            agent.value = "Programmer";
            profile.value = profile.choices[1];
            InvokePage(page, "AssignProfileToAgent");

            Assert.AreEqual("profile-a", _controller.GetProfileForRole("Programmer"));

            FindTextField(root, "Custom agent role").value = "QuestDesigner";
            InvokePage(page, "AssignProfileToAgent");
            Assert.AreEqual("profile-a", _controller.GetProfileForRole("QuestDesigner"));

            FindTextField(root, "Custom agent role").value = "";
            agent.value = "SmartChat";
            profile.value = profile.choices[0];
            InvokePage(page, "AssignProfileToAgent");
            Assert.AreEqual(string.Empty, _controller.GetProfileForRole("SmartChat"));
            page.OnDestroyed();
        }

        [Test]
        public void HubEndpointEditor_HidesSessionKeyClearOutsideHttpEndpoints()
        {
            HubSettingsPage page = new(routingController: _controller);
            ScrollView root = (ScrollView)page.CreatePageContent();
            DropdownField kind = FindDropdown(root, "Type");
            Button clearKey = FindButton(root, "Clear saved session key");

            Assert.AreEqual(DisplayStyle.Flex, clearKey.style.display.value);
            kind.SetValueWithoutNotify("LLMUnity");
            InvokePage(page, "RefreshEndpointEditorVisibility");
            Assert.AreEqual(DisplayStyle.None, clearKey.style.display.value);
            kind.SetValueWithoutNotify("Offline");
            InvokePage(page, "RefreshEndpointEditorVisibility");
            Assert.AreEqual(DisplayStyle.None, clearKey.style.display.value);
            kind.SetValueWithoutNotify("HTTP API");
            InvokePage(page, "RefreshEndpointEditorVisibility");
            Assert.AreEqual(DisplayStyle.Flex, clearKey.style.display.value);
            page.OnDestroyed();
        }

        [Test]
        public void HubEndpointEditor_BlankKeyOnEditMeansPreserve_AndIdIsLocked()
        {
            HubSettingsPage page = new(routingController: _controller);
            ScrollView root = (ScrollView)page.CreatePageContent();
            DropdownField picker = FindDropdown(root, "Edit endpoint");
            picker.value = picker.choices[1];
            InvokePage(page, "LoadSelectedEndpoint");

            TextField id = FindTextField(root, "Endpoint ID");
            Assert.IsFalse(id.enabledSelf);
            FindTextField(root, "Name").value = "API A edited";
            InvokePage(page, "SaveEndpoint");

            Assert.IsNull(_controller.LastSessionKey);
            Assert.AreEqual("endpoint-a", _controller.LastSavedEndpoint.EndpointId);
            page.OnDestroyed();
        }

        [Test]
        public void HubEndpointEditor_RemoveRequiresSecondConfirmation()
        {
            HubSettingsPage page = new(routingController: _controller);
            ScrollView root = (ScrollView)page.CreatePageContent();
            DropdownField picker = FindDropdown(root, "Edit endpoint");
            picker.value = picker.choices[1];
            InvokePage(page, "LoadSelectedEndpoint");

            InvokePage(page, "RemoveEndpoint");
            Assert.AreEqual(0, _controller.RemoveCalls);
            Assert.IsNotNull(FindButton(root, "Confirm remove"));

            InvokePage(page, "RemoveEndpoint");
            Assert.AreEqual(1, _controller.RemoveCalls);
            page.OnDestroyed();
        }

        [Test]
        public void HubEndpointEditor_InvalidHttpUrl_DoesNotCallRegistry()
        {
            HubSettingsPage page = new(routingController: _controller);
            ScrollView root = (ScrollView)page.CreatePageContent();
            FindTextField(root, "Name").value = "Broken API";
            FindTextField(root, "Endpoint ID").value = "broken";
            FindTextField(root, "Base URL").value = "not-a-url";

            InvokePage(page, "SaveEndpoint");

            Assert.IsNull(_controller.LastSavedEndpoint);
            page.OnDestroyed();
        }

        [Test]
        public void ShippedHubPrefab_EnablesBuiltInSettingsAndWiresChatAssets()
        {
            GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(
                "Assets/CoreAIHub/Runtime/CoreAiHub.prefab");
            Assert.IsNotNull(prefab);
            CoreAiHubDemo demo = prefab.GetComponent<CoreAiHubDemo>();
            Assert.IsNotNull(demo);
            Assert.IsTrue(GetField<bool>(demo, "registerBuiltInPages"));
            Assert.IsNotNull(GetField<VisualTreeAsset>(demo, "chatTemplate"));
            Assert.IsNotNull(GetField<StyleSheet>(demo, "chatStyleSheet"));
        }

        private static TextField FindTextField(VisualElement root, string label)
        {
            foreach (TextField field in root.Query<TextField>().ToList())
            {
                if (field.label == label)
                {
                    return field;
                }
            }

            Assert.Fail("Text field not found: " + label);
            return null;
        }

        private static DropdownField FindDropdown(VisualElement root, string label)
        {
            foreach (DropdownField field in root.Query<DropdownField>().ToList())
            {
                if (field.label == label)
                {
                    return field;
                }
            }

            Assert.Fail("Dropdown not found: " + label);
            return null;
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

            Assert.Fail("Button not found: " + text);
            return null;
        }

        private static void SetField(object target, string name, object value)
        {
            typeof(CoreAiChatPanel).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
                ?.SetValue(target, value);
        }

        private static T GetField<T>(object target, string name)
        {
            return (T)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
                ?.GetValue(target);
        }

        private static void Invoke(object target, string name)
        {
            typeof(CoreAiChatPanel).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)
                ?.Invoke(target, null);
        }

        private static void InvokePage(HubSettingsPage page, string name)
        {
            typeof(HubSettingsPage).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)
                ?.Invoke(page, null);
        }

        private sealed class FakeController : ICoreAiRoutingUiController
        {
            private readonly Dictionary<string, string> _roleProfiles = new(StringComparer.Ordinal)
            {
                ["SmartChat"] = "profile-a"
            };

            public event Action Changed;

            public LlmEndpointDescriptor LastSavedEndpoint { get; private set; }
            public string LastSessionKey { get; private set; }
            public bool HasProfiles { get; set; } = true;

            public IReadOnlyList<LlmEndpointSnapshot> GetEndpoints()
            {
                if (!HasProfiles)
                {
                    return Array.Empty<LlmEndpointSnapshot>();
                }

                return new[]
                {
                    new LlmEndpointSnapshot
                    {
                        Descriptor = new LlmEndpointDescriptor
                        {
                            EndpointId = "endpoint-a",
                            DisplayName = "API A",
                            Kind = LlmEndpointKind.HttpOpenAi,
                            BaseUrl = "https://example.test/v1",
                            Model = "model-a",
                            Active = true
                        },
                        State = LlmEndpointLifecycleState.Ready
                    }
                };
            }

            public IReadOnlyList<LlmRuntimeProfile> GetProfiles()
            {
                if (!HasProfiles)
                {
                    return Array.Empty<LlmRuntimeProfile>();
                }

                return new[]
                {
                    new LlmRuntimeProfile
                    {
                        ProfileId = "profile-a",
                        DisplayName = "API A",
                        EndpointId = "endpoint-a"
                    }
                };
            }

            public string GetProfileForRole(string roleId)
            {
                return _roleProfiles.TryGetValue(roleId ?? "", out string profileId) ? profileId : "";
            }

            public CoreAiRoutingUiResult AssignProfileToRole(string roleId, string profileId)
            {
                _roleProfiles[roleId] = profileId;
                Changed?.Invoke();
                return new CoreAiRoutingUiResult(true);
            }

            public Task<CoreAiRoutingUiResult> SaveEndpointAsync(
                LlmEndpointDescriptor endpoint,
                string sessionApiKey,
                CancellationToken cancellationToken = default)
            {
                LastSavedEndpoint = endpoint;
                LastSessionKey = sessionApiKey;
                return Task.FromResult(new CoreAiRoutingUiResult(true));
            }

            public Task<CoreAiRoutingUiResult> RemoveEndpointAsync(
                string endpointId,
                CancellationToken cancellationToken = default)
            {
                RemoveCalls++;
                return Task.FromResult(new CoreAiRoutingUiResult(true));
            }

            public int RemoveCalls { get; private set; }
        }
    }
}
