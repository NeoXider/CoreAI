using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Ai;
using CoreAI.Chat;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UIElements;

namespace CoreAI.Tests.PlayMode
{
    public sealed class CoreAiChatRoutingPlayModeTests
    {
        [UnityTest]
        public IEnumerator AdvancedApiSelector_DefaultsCollapsed_AndRoutesNextRequest()
        {
            FakeController controller = new();
            CoreAiRoutingUi.Controller = controller;
            GameObject gameObject = new("CoreAiChatRoutingPlayModeTest");
            gameObject.SetActive(false);
            try
            {
                PanelHarness panel = gameObject.AddComponent<PanelHarness>();
                VisualElement header = new();
                Label title = new("Chat");
                header.Add(title);
                panel.InitializeRoutingHeader(title);

                yield return null;

                DropdownField selector = header.Q<DropdownField>(className: "coreai-chat-api-dropdown");
                Assert.IsNotNull(selector);
                Assert.AreEqual(DisplayStyle.None, selector.style.display.value);

                AiTaskRequest request = panel.CreateRequest("hello", "SmartChat");
                Assert.AreEqual("profile-fast", request.RoutingProfileId);
                Assert.AreEqual("SmartChat", request.RoleId);
            }
            finally
            {
                CoreAiRoutingUi.Controller = null;
                UnityEngine.Object.Destroy(gameObject);
            }
        }

        private sealed class PanelHarness : CoreAiChatPanel
        {
            public void InitializeRoutingHeader(Label title)
            {
                HeaderTitle = title;
                EnableApiSwitching();
            }

            public AiTaskRequest CreateRequest(string text, string roleId)
            {
                return BuildAiTaskRequest(text, roleId);
            }
        }

        private sealed class FakeController : ICoreAiRoutingUiController
        {
            public event Action Changed;

            public IReadOnlyList<LlmEndpointSnapshot> GetEndpoints()
            {
                return Array.Empty<LlmEndpointSnapshot>();
            }

            public IReadOnlyList<LlmRuntimeProfile> GetProfiles()
            {
                return new[]
                {
                    new LlmRuntimeProfile
                    {
                        ProfileId = "profile-fast",
                        DisplayName = "Fast API",
                        EndpointId = "fast"
                    }
                };
            }

            public string GetProfileForRole(string roleId)
            {
                return "profile-fast";
            }

            public CoreAiRoutingUiResult AssignProfileToRole(string roleId, string profileId)
            {
                Changed?.Invoke();
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
                return Task.FromResult(new CoreAiRoutingUiResult(true));
            }
        }
    }
}
