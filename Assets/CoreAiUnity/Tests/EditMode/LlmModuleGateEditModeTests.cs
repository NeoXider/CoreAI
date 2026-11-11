using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Ai;
using CoreAI.Composition;
using CoreAI.Infrastructure.Llm;
using CoreAI.Infrastructure.Logging;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace CoreAI.Tests.EditMode
{
    /// <summary>
    /// Newcomer audit A2: a build without the <c>COREAI_LLM</c> define used to answer every request with the
    /// canned <see cref="StubLlmClient"/> (<c>Ok = true</c>), so a forgotten define made NPCs speak
    /// <c>ApplyWaveModifier</c> JSON. Now every mode that needs the module fails with
    /// <see cref="LlmErrorCode.NotConfigured"/> and a message naming the fix, Offline keeps the offline stub, and
    /// the composition logs one warning. The decisions take the module state as a parameter, so both branches
    /// run in this build; <see cref="ResolvedClient_FollowsTheCompiledModule"/> checks the configuration that is
    /// actually compiled (CI runs it with and without the define).
    /// </summary>
    [TestFixture]
    public sealed class LlmModuleGateEditModeTests
    {
        private readonly List<Object> _created = new();

        [TearDown]
        public void TearDown()
        {
            foreach (Object created in _created)
            {
                if (created != null)
                {
                    Object.DestroyImmediate(created);
                }
            }

            _created.Clear();
        }

        private CoreAISettingsAsset Settings(Action<CoreAISettingsAsset> configure)
        {
            CoreAISettingsAsset settings = ScriptableObject.CreateInstance<CoreAISettingsAsset>();
            _created.Add(settings);
            configure(settings);
            return settings;
        }

        private CoreAISettingsAsset HttpSettings()
        {
            return Settings(s => s.ConfigureClientOwnedApi("http://localhost:1234/v1", "", "test-model"));
        }

        private static Task<LlmCompletionResult> Ask(ILlmClient client, string role = BuiltInAgentRoleIds.SmartChat)
        {
            return client.CompleteAsync(new LlmCompletionRequest
            {
                AgentRoleId = role,
                SystemPrompt = "system",
                UserPayload = "Hello, blacksmith."
            }, CancellationToken.None);
        }

        [Test]
        public async Task WithoutModule_HttpMode_FailsNotConfigured_AndTheMessageNamesTheFix()
        {
            ILlmClient client = LlmPipelineInstaller.ResolveClientWithoutLlmModule(HttpSettings());

            Assert.IsInstanceOf<NotConfiguredLlmClient>(client);
            LlmCompletionResult result = await Ask(client, "Blacksmith");
            Assert.IsFalse(result.Ok, "A missing module must never answer Ok = true.");
            Assert.AreEqual(LlmErrorCode.NotConfigured, result.ErrorCode);
            StringAssert.Contains("COREAI_LLM", result.Error);
            StringAssert.Contains("Offline", result.Error, "The message names the deliberate alternative too.");
            Assert.IsEmpty(result.Content, "No canned reply reaches the caller.");
        }

        [Test]
        public async Task WithoutModule_OfflineMode_KeepsTheDeterministicOfflineStub()
        {
            ILlmClient client = LlmPipelineInstaller.ResolveClientWithoutLlmModule(Settings(s => s.ConfigureOffline()));

            Assert.IsInstanceOf<OfflineLlmClient>(client);
            Assert.IsTrue((await Ask(client)).Ok, "Offline chosen on purpose keeps working.");
        }

        [Test]
        public void WithoutModule_LocalModel_FailsNotConfigured()
        {
            ILlmClient client = LlmPipelineInstaller.ResolveClientWithoutLlmModule(
                Settings(s => s.ConfigureLlmUnity()));

            Assert.IsInstanceOf<NotConfiguredLlmClient>(client);
            Assert.AreEqual(LlmExecutionMode.LocalModel, ((NotConfiguredLlmClient)client).Mode);
        }

        [Test]
        public void WithoutModule_Auto_ServesOfflineWithoutAModel_AndFailsOnceAModelIsConfigured()
        {
            CoreAISettingsAsset noModel = Settings(s =>
                s.SetModelResolution(LlmExecutionMode.Auto, LlmBackendType.Auto, "", ""));
            CoreAISettingsAsset withModel = Settings(s =>
                s.SetModelResolution(LlmExecutionMode.Auto, LlmBackendType.Auto, "qwen3.5-4b", ""));

            Assert.IsInstanceOf<OfflineLlmClient>(LlmPipelineInstaller.ResolveClientWithoutLlmModule(noModel),
                "With nothing configured Auto ends at the offline client, with or without the module.");
            Assert.IsInstanceOf<NotConfiguredLlmClient>(LlmPipelineInstaller.ResolveClientWithoutLlmModule(withModel),
                "With a model Auto would call it when the module is present, so without it the request fails.");
        }

        [Test]
        public void CompositionWarning_IsWrittenOnce_OnlyWhenTheModuleIsMissing()
        {
            CapturingLogger missing = new();
            CapturingLogger compiled = new();
            CapturingLogger offline = new();

            Assert.IsTrue(LlmModuleGate.WarnWhenLlmModuleMissing(HttpSettings(), false, missing));
            Assert.IsFalse(LlmModuleGate.WarnWhenLlmModuleMissing(HttpSettings(), true, compiled));
            Assert.IsFalse(LlmModuleGate.WarnWhenLlmModuleMissing(Settings(s => s.ConfigureOffline()), false,
                offline));

            Assert.AreEqual(1, missing.Warnings.Count);
            StringAssert.Contains("COREAI_LLM", missing.Warnings[0]);
            StringAssert.Contains("NotConfigured", missing.Warnings[0]);
            Assert.IsEmpty(compiled.Warnings, "A build with the module logs nothing.");
            Assert.IsEmpty(offline.Warnings, "Offline chosen on purpose logs nothing.");
        }

        [Test]
        public void CompositionWarning_ForAutoWithoutAModel_SaysTheOfflineStubServes()
        {
            CapturingLogger logger = new();

            Assert.IsTrue(LlmModuleGate.WarnWhenLlmModuleMissing(
                Settings(s => s.SetModelResolution(LlmExecutionMode.Auto, LlmBackendType.Auto, "", "")), false,
                logger));

            StringAssert.Contains("offline stub", logger.Warnings[0]);
        }

        [Test]
        public void ResolvedClient_FollowsTheCompiledModule()
        {
            CoreAISettingsAsset settings = HttpSettings();
            settings.ConfigureFallbackBackend(true, "http://localhost:1235/v1", "fallback-model");

            ILlmClient primary = LlmPipelineInstaller.ResolveLlmClientForPlatform(
                settings, new CapturingLogger(), null, null, Application.platform);
            ILlmClient secondary = LlmPipelineInstaller.BuildSecondaryHttpClient(settings, null);

            bool compiled = LlmModuleGate.IsLlmModuleCompiled;
            Assert.AreEqual(!compiled, primary is NotConfiguredLlmClient,
                compiled
                    ? "With COREAI_LLM the HTTP mode builds a real client."
                    : "Without COREAI_LLM the HTTP mode must fail NotConfigured, not answer with a stub.");
            Assert.AreEqual(!compiled, secondary is NotConfiguredLlmClient,
                "Without the module the fallback backend fails the same way; a stub would answer a failover.");
            Assert.IsNotInstanceOf<StubLlmClient>(primary);
        }

        [Test]
        public async Task NotConfigured_NeverFailsOverToTheSecondary()
        {
            CountingClient secondary = new(new LlmCompletionResult { Ok = true, Content = "canned" });
            FallbackLlmClientDecorator chain = new(new NotConfiguredLlmClient(LlmExecutionMode.ClientOwnedApi),
                secondary);

            LlmCompletionResult result = await Ask(chain);

            Assert.AreEqual(LlmErrorCode.NotConfigured, result.ErrorCode);
            Assert.AreEqual(0, secondary.Calls, "A configuration fault is not a reason to switch backends.");
        }

        [Test]
        public async Task TransientPrimaryFailure_StillFailsOver()
        {
            CountingClient primary = new(new LlmCompletionResult
            {
                Ok = false, Error = "HTTP 503", ErrorCode = LlmErrorCode.BackendUnavailable
            });
            CountingClient secondary = new(new LlmCompletionResult { Ok = true, Content = "from secondary" });

            LlmCompletionResult result = await Ask(new FallbackLlmClientDecorator(primary, secondary));

            Assert.IsTrue(result.Ok);
            Assert.AreEqual(1, secondary.Calls);
        }

        private sealed class CountingClient : ILlmClient
        {
            private readonly LlmCompletionResult _result;

            public CountingClient(LlmCompletionResult result)
            {
                _result = result;
            }

            public int Calls { get; private set; }

            public Task<LlmCompletionResult> CompleteAsync(LlmCompletionRequest request,
                CancellationToken cancellationToken = default)
            {
                Calls++;
                return Task.FromResult(_result);
            }
        }

        private sealed class CapturingLogger : IGameLogger
        {
            public List<string> Warnings { get; } = new();

            public void LogDebug(GameLogFeature feature, string message, Object context = null)
            {
            }

            public void LogInfo(GameLogFeature feature, string message, Object context = null)
            {
            }

            public void LogWarning(GameLogFeature feature, string message, Object context = null)
            {
                Warnings.Add(message);
            }

            public void LogError(GameLogFeature feature, string message, Object context = null)
            {
            }
        }
    }
}
