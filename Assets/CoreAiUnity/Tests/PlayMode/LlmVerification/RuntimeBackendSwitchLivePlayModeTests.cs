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
    /// LIVE runtime backend switch: the scope boots in Offline mode, then
    /// <see cref="CoreAiBackend.ApplyHttpApi"/> retargets it at the configured local/CI
    /// OpenAI-compatible server and the very next request must round-trip through the real model.
    /// Uses the same env/file/asset resolution as the rest of the live suite.
    /// </summary>
    public sealed class RuntimeBackendSwitchLivePlayModeTests
    {
        private CoreAISettingsAsset _previousInstance;
        private CoreAISettingsAsset _settings;
        private GameObject _scopeGo;

        [UnitySetUp]
        public IEnumerator SetUp()
        {
            _previousInstance = CoreAISettingsAsset.Instance;
            _settings = ScriptableObject.CreateInstance<CoreAISettingsAsset>();
            _settings.ConfigureOffline();
            CoreAISettingsAsset.SetInstance(_settings);

            _scopeGo = new GameObject("RuntimeBackendSwitchLiveScope");
            _scopeGo.SetActive(false);
            CoreAILifetimeScope scope = _scopeGo.AddComponent<CoreAILifetimeScope>();
            typeof(CoreAILifetimeScope)
                .GetField("coreAiSettings", BindingFlags.NonPublic | BindingFlags.Instance)
                ?.SetValue(scope, _settings);
            _scopeGo.SetActive(true);

            CoreAi.Invalidate();
            yield return null;
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
        [Timeout(300000)]
        public IEnumerator OfflineScope_SwitchedToLiveHttp_AnswersThroughRealModel()
        {
            PlayModeOpenAiTestConfig.ResolvedConfig config = PlayModeOpenAiTestConfig.Resolve(null);
            if (!config.IsComplete)
            {
                Assert.Ignore(PlayModeOpenAiTestConfig.BuildIgnoreReason(config));
            }

            // Boot state: offline stub answers.
            Assert.AreEqual(LlmExecutionMode.Offline, CoreAiBackend.Status.Mode);

            // Runtime switch to the live server (no scene reload, no container rebuild).
            bool live = CoreAiBackend.ApplyHttpApi(config.BaseUrl, config.ApiKey, config.Model,
                timeoutSeconds: 120);
            Assert.IsTrue(live, "Switch must hot-swap the active client.");
            Assert.AreEqual(LlmExecutionMode.ClientOwnedApi, CoreAiBackend.Status.Mode);

            // Health probe round-trips through the real model.
            Task<CoreAiBackendHealth> verify = CoreAiBackend.VerifyAsync(120);
            yield return WaitTask(verify, 180f);
            Assert.IsTrue(verify.Result.Ok,
                $"Health probe must pass against the live backend. Error: {verify.Result.Error}");
            Assert.Greater(verify.Result.LatencyMs, 0);
            Debug.Log($"[RuntimeBackendSwitch] Live probe OK in {verify.Result.LatencyMs:F0} ms " +
                      $"({verify.Result.Model})");

            // A real orchestrated request through the swapped backend.
            Task<string> ask = CoreAi.OrchestrateAsync(new AiTaskRequest
            {
                RoleId = BuiltInAgentRoleIds.SmartChat,
                Hint = "Reply with the single word: pong",
                MaxOutputTokens = 512
            });
            yield return WaitTask(ask, 180f);

            Assert.IsFalse(string.IsNullOrWhiteSpace(ask.Result),
                "The live model must produce a non-empty answer after the runtime switch.");
            Debug.Log($"[RuntimeBackendSwitch] Live answer: {ask.Result}");
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
