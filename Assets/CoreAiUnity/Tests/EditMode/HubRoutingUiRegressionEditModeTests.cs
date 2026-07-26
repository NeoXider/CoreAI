using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Ai;
using CoreAI.Chat;
using CoreAI.Hub.UI;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode
{
    public sealed class HubRoutingUiRegressionEditModeTests
    {
        [Test]
        public void EndpointIdHelpers_DeriveSlugAndUniqueSuffix()
        {
            Assert.AreEqual("production-api-v2", LlmEndpointDescriptor.DeriveEndpointSlug(" Production API / v2 "));
            Assert.AreEqual("", LlmEndpointDescriptor.DeriveEndpointSlug("  "));
            Assert.AreEqual(
                "production-api-3",
                LlmEndpointDescriptor.EnsureUniqueEndpointId(
                    "production-api",
                    new[] { "production-api", "production-api-2" }));
        }

        [Test]
        public void HubValidation_UsesDescriptorValidation()
        {
            LlmEndpointDescriptor endpoint = new()
            {
                EndpointId = "local",
                DisplayName = "Local",
                Kind = LlmEndpointKind.LlmUnity,
                ContextWindowTokens = 128,
                Port = 70000,
                ParallelSlots = 0
            };

            string expected = string.Join(" ", endpoint.Validate());
            MethodInfo validate = typeof(HubSettingsPage).GetMethod(
                "ValidateEndpoint",
                BindingFlags.Static | BindingFlags.NonPublic);
            string actual = (string)validate.Invoke(null, new object[] { endpoint });

            Assert.AreEqual(expected, actual);
        }

        [Test]
        public async Task SaveEndpointAsync_ReturnsSavedAndReady()
        {
            LlmEndpointRegistryUiController controller = new(new FakeRegistry
            {
                Snapshot = Snapshot(LlmEndpointLifecycleState.Ready)
            });

            CoreAiRoutingUiResult result = await controller.SaveEndpointAsync(Descriptor(), null);

            Assert.IsTrue(result.Ok);
            Assert.AreEqual(CoreAiRoutingUiSaveStatus.SavedAndReady, result.SaveStatus);
            Assert.AreEqual("Saved and ready.", result.Message);
        }

        [Test]
        public async Task SaveEndpointAsync_ReturnsSavedActivationFailed()
        {
            LlmEndpointRegistryUiController controller = new(new FakeRegistry
            {
                Snapshot = Snapshot(LlmEndpointLifecycleState.Failed, "Probe timed out.")
            });

            CoreAiRoutingUiResult result = await controller.SaveEndpointAsync(Descriptor(), null);

            Assert.IsTrue(result.Ok);
            Assert.AreEqual(CoreAiRoutingUiSaveStatus.SavedActivationFailed, result.SaveStatus);
            Assert.AreEqual("Saved; activation failed: Probe timed out.", result.Message);
        }

        [Test]
        public async Task SaveEndpointAsync_ReturnsNotSaved()
        {
            LlmEndpointRegistryUiController controller = new(new FakeRegistry
            {
                SaveException = new InvalidOperationException("Store unavailable.")
            });

            CoreAiRoutingUiResult result = await controller.SaveEndpointAsync(Descriptor(), null);

            Assert.IsFalse(result.Ok);
            Assert.AreEqual(CoreAiRoutingUiSaveStatus.NotSaved, result.SaveStatus);
            Assert.AreEqual("Store unavailable.", result.Message);
        }

        private static LlmEndpointDescriptor Descriptor()
        {
            return new LlmEndpointDescriptor
            {
                EndpointId = "endpoint",
                DisplayName = "Endpoint",
                Kind = LlmEndpointKind.Offline,
                ContextWindowTokens = 1024,
                ParallelSlots = 1
            };
        }

        private static LlmEndpointSnapshot Snapshot(LlmEndpointLifecycleState state, string error = "")
        {
            return new LlmEndpointSnapshot
            {
                Descriptor = Descriptor(),
                State = state,
                Error = error
            };
        }

        private sealed class FakeRegistry : ILlmEndpointRegistry
        {
            public event Action Changed;
            public LlmEndpointSnapshot Snapshot { get; set; }
            public Exception SaveException { get; set; }

            public IReadOnlyList<LlmEndpointSnapshot> GetEndpoints()
            {
                return Array.Empty<LlmEndpointSnapshot>();
            }

            public IReadOnlyList<LlmRuntimeProfile> GetProfiles()
            {
                return Array.Empty<LlmRuntimeProfile>();
            }

            public Task<LlmEndpointSnapshot> AddOrUpdateEndpointAsync(
                LlmEndpointDescriptor descriptor,
                string sessionApiKey = null,
                CancellationToken cancellationToken = default)
            {
                if (SaveException != null)
                {
                    return Task.FromException<LlmEndpointSnapshot>(SaveException);
                }

                return Task.FromResult(Snapshot);
            }

            public Task<LlmEndpointSnapshot> SetEndpointActiveAsync(
                string endpointId,
                bool active,
                bool keepWarm = false,
                CancellationToken cancellationToken = default)
            {
                return Task.FromResult(Snapshot);
            }

            public Task<bool> RemoveEndpointAsync(
                string endpointId,
                LlmEndpointRemovalMode mode = LlmEndpointRemovalMode.Drain,
                string replacementEndpointId = null,
                CancellationToken cancellationToken = default)
            {
                return Task.FromResult(false);
            }

            public void AddOrUpdateProfile(LlmRuntimeProfile profile)
            {
            }

            public bool RemoveProfile(string profileId, string replacementProfileId = null)
            {
                return false;
            }

            public void AssignRoleProfile(string rolePattern, string profileId, int sortOrder = 0)
            {
                Changed?.Invoke();
            }

            public bool ClearRoleProfile(string rolePattern)
            {
                return false;
            }

            public string GetRoleProfile(string roleId)
            {
                return "";
            }
        }
    }
}
