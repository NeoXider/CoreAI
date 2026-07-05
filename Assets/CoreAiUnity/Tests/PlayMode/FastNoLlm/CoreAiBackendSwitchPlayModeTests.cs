using System.Collections;
using System.Reflection;
using System.Threading.Tasks;
using CoreAI;
using CoreAI.Ai;
using CoreAI.Composition;
using CoreAI.Infrastructure.Llm;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace CoreAI.Tests.PlayMode
{
    /// <summary>
    /// End-to-end runtime backend switching against a REAL CoreAILifetimeScope (no live LLM needed):
    /// the switch must hot-swap the routed client so the very next orchestrated request uses the new
    /// backend — verified by flipping to the Offline backend with a distinctive custom response.
    /// </summary>
    public sealed class CoreAiBackendSwitchPlayModeTests
    {
        private const string OfflineMarker = "offline-switch-marker-42";

        private CoreAISettingsAsset _previousInstance;
        private CoreAISettingsAsset _settings;
        private GameObject _scopeGo;

        [UnitySetUp]
        public IEnumerator SetUp()
        {
            _previousInstance = CoreAISettingsAsset.Instance;

            _settings = ScriptableObject.CreateInstance<CoreAISettingsAsset>();
            // Start OFFLINE with a default (non-custom) response so the switch below is observable.
            _settings.ConfigureOffline();
            CoreAISettingsAsset.SetInstance(_settings);

            _scopeGo = new GameObject("CoreAiBackendSwitchTestScope");
            _scopeGo.SetActive(false);
            CoreAILifetimeScope scope = _scopeGo.AddComponent<CoreAILifetimeScope>();
            typeof(CoreAILifetimeScope)
                .GetField("coreAiSettings", BindingFlags.NonPublic | BindingFlags.Instance)
                ?.SetValue(scope, _settings);
            _scopeGo.SetActive(true);

            CoreAi.Invalidate();
            yield return null; // let Awake build the container
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            if (_scopeGo != null)
            {
                Object.Destroy(_scopeGo);
            }

            CoreAISettingsAsset.SetInstance(_previousInstance);
            CoreAi.Invalidate();
            if (_settings != null)
            {
                Object.Destroy(_settings);
            }

            yield return null;
        }

        [UnityTest]
        public IEnumerator Status_WithLiveScope_IsLive()
        {
            CoreAiBackendStatus status = CoreAiBackend.Status;
            Assert.IsTrue(status.IsLive, "A built CoreAILifetimeScope must make the backend status live.");
            Assert.AreEqual(LlmExecutionMode.Offline, status.Mode);
            yield break;
        }

        [UnityTest]
        public IEnumerator SwitchToOfflineCustomResponse_NextRequestUsesIt()
        {
            bool live = CoreAiBackend.ApplyOffline(true, OfflineMarker, "*");
            Assert.IsTrue(live, "With a live scope the switch must hot-swap the active client.");

            Task<string> ask = CoreAi.OrchestrateAsync(new AiTaskRequest
            {
                RoleId = BuiltInAgentRoleIds.SmartChat,
                Hint = "ping"
            });
            yield return WaitTask(ask, 30f);

            Assert.IsNotNull(ask.Result);
            StringAssert.Contains(OfflineMarker, ask.Result,
                "The request AFTER the switch must be served by the swapped offline client.");
        }

        [UnityTest]
        public IEnumerator SwitchToUnreachableHttp_VerifyFails_ThenOfflineRecovers()
        {
            // 1) Switch to an unreachable HTTP endpoint: probe must fail with an error, not hang.
            bool live = CoreAiBackend.ApplyHttpApi(
                "http://127.0.0.1:59999/v1", "test-key", "test-model", timeoutSeconds: 3);
            Assert.IsTrue(live);

            Task<CoreAiBackendHealth> verify = CoreAiBackend.VerifyAsync(8);
            yield return WaitTask(verify, 30f);

            Assert.IsFalse(verify.Result.Ok, "Unreachable endpoint must fail the health probe.");
            Assert.IsNotEmpty(verify.Result.Error);

            // 2) Switch back to offline: the same session recovers without any rebuild.
            Assert.IsTrue(CoreAiBackend.ApplyOffline(true, OfflineMarker, "*"));

            Task<string> ask = CoreAi.OrchestrateAsync(new AiTaskRequest
            {
                RoleId = BuiltInAgentRoleIds.SmartChat,
                Hint = "ping"
            });
            yield return WaitTask(ask, 30f);

            StringAssert.Contains(OfflineMarker, ask.Result,
                "After switching back to offline the request must succeed again.");
        }

        [UnityTest]
        public IEnumerator OnBackendChanged_FiresWithLiveStatus()
        {
            CoreAiBackendStatus? observed = null;

            void Handler(CoreAiBackendStatus s)
            {
                observed = s;
            }

            CoreAiBackend.OnBackendChanged += Handler;
            try
            {
                CoreAiBackend.ApplyOffline();
            }
            finally
            {
                CoreAiBackend.OnBackendChanged -= Handler;
            }

            Assert.IsTrue(observed.HasValue, "OnBackendChanged must fire on a live switch.");
            Assert.IsTrue(observed.Value.IsLive);
            Assert.AreEqual(LlmExecutionMode.Offline, observed.Value.Mode);
            yield break;
        }

        private static IEnumerator WaitTask(Task task, float timeoutSeconds)
        {
            float deadline = Time.realtimeSinceStartup + timeoutSeconds;
            while (!task.IsCompleted && Time.realtimeSinceStartup < deadline)
            {
                yield return null;
            }

            if (!task.IsCompleted)
            {
                Assert.Fail($"Task did not complete within {timeoutSeconds}s.");
            }

            if (task.IsFaulted)
            {
                Assert.Fail($"Task faulted: {task.Exception?.GetBaseException().Message}");
            }
        }
    }
}