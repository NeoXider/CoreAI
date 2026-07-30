using System;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Ai;
using CoreAI.Infrastructure.Logging;
using CoreAI.Infrastructure.Llm;
using NUnit.Framework;
#if COREAI_HAS_LLMUNITY
using LLMUnity;
using UnityEngine;
#endif

namespace CoreAI.Tests.EditMode
{
    public sealed class LlmUnityActivationLogEditModeTests
    {
        private sealed class RecordingReadinessProbe : ILlmEndpointReadinessProbe
        {
            public LlmEndpointReadinessRequest Request { get; private set; }

            public Task<LlmEndpointReadinessResult> ProbeAsync(
                LlmEndpointReadinessRequest request,
                CancellationToken cancellationToken = default)
            {
                Request = request;
                return Task.FromResult(new LlmEndpointReadinessResult
                {
                    IsReady = true,
                    StatusCode = 200
                });
            }
        }

        [Test]
        public void NativeStartup_ReportsStableContextAndDuration()
        {
            LlmUnityActivationLogContext context = new(
                "local-main",
                "Local Main",
                @"D:\Models\qwen3.5-0.8b.gguf",
                "Qwen Agent",
                13333);

            string started = LlmUnityActivationLog.NativeStarted(context);
            string succeeded = LlmUnityActivationLog.NativeSucceeded(context, 16234);

            Assert.That(started, Is.EqualTo(
                "[CoreAI.LLMUnity] phase=native_startup status=started " +
                "endpointId=\"local-main\" endpoint=\"Local Main\" model=\"qwen3.5-0.8b.gguf\" " +
                "agent=\"Qwen Agent\" port=13333"));
            Assert.That(succeeded, Is.EqualTo(
                "[CoreAI.LLMUnity] phase=native_startup status=succeeded " +
                "endpointId=\"local-main\" endpoint=\"Local Main\" model=\"qwen3.5-0.8b.gguf\" " +
                "agent=\"Qwen Agent\" port=13333 durationMs=16234"));
        }

        [Test]
        public void ReadinessFailure_ReportsPhaseDurationAndSanitizedError()
        {
            LlmUnityActivationLogContext context = new(
                "local\nsecondary",
                "Local \"Secondary\"",
                "/models/secondary.gguf",
                "Secondary Agent",
                14444);

            string failed = LlmUnityActivationLog.ReadinessFailed(
                context,
                507,
                new InvalidOperationException("socket\nnot ready for /models/secondary.gguf"));

            Assert.That(failed, Is.EqualTo(
                "[CoreAI.LLMUnity] phase=http_readiness status=failed " +
                "endpointId=\"local secondary\" endpoint=\"Local 'Secondary'\" model=\"secondary.gguf\" " +
                "agent=\"Secondary Agent\" port=14444 durationMs=507 " +
                "errorType=\"InvalidOperationException\" error=\"socket not ready for secondary.gguf\""));
        }

        [TestCase(200, true)]
        [TestCase(204, true)]
        [TestCase(302, false)]
        [TestCase(400, true)]
        [TestCase(401, false)]
        [TestCase(403, false)]
        [TestCase(404, false)]
        [TestCase(429, true)]
        [TestCase(500, false)]
        public void LlmUnityReadiness_UsesOneStatusPolicyForRuntimeAndLegacyAutostart(
            long status,
            bool expected)
        {
            Assert.AreEqual(expected, LlmEndpointReadinessPolicy.IsHandlerReached(status));
        }

        [Test]
        public async Task HttpEndpointFactory_DelegatesPortableModelsThenCompletionsProbe()
        {
            CoreAISettingsAsset settings = ScriptableObject.CreateInstance<CoreAISettingsAsset>();
            RecordingReadinessProbe probe = new();
            try
            {
                LlmEndpointClientFactory factory = new(
                    settings,
                    GameLoggerUnscopedFallback.Instance,
                    readinessProbe: probe);

                LlmEndpointClientActivation activation = await factory.ActivateAsync(
                    new LlmEndpointDescriptor
                    {
                        EndpointId = "external",
                        Kind = LlmEndpointKind.HttpOpenAi,
                        BaseUrl = "https://example.test/v1",
                        Model = "test"
                    },
                    "session-key",
                    CancellationToken.None);

                Assert.NotNull(activation.Client);
                Assert.NotNull(probe.Request);
                Assert.AreEqual(
                    LlmEndpointReadinessMode.ModelsThenCompletions,
                    probe.Request.Mode);
                Assert.AreEqual("session-key", probe.Request.ApiKey);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(settings);
            }
        }

        // WHY: ResolveAgent itself is compiled out on WebGL and with LLM support off, so these reflection
        // tests must carry the same guard as the method — otherwise GetMethod returns null and they fail
        // for a missing method rather than a broken one, which is what happened on a WebGL build target.
#if COREAI_HAS_LLMUNITY && !UNITY_WEBGL && !COREAI_NO_LLM
        [Test]
        public void ResolveAgent_FindsExactNamedInactiveHostWithoutModelStartup()
        {
            GameObject first = new("Inactive Exact Agent");
            GameObject second = new("Inactive Other Agent");
            first.SetActive(false);
            second.SetActive(false);
            LLMAgent expected = first.AddComponent<LLMAgent>();
            second.AddComponent<LLMAgent>();
            try
            {
                MethodInfo resolve = typeof(LlmEndpointClientFactory).GetMethod(
                    "ResolveAgent",
                    BindingFlags.NonPublic | BindingFlags.Static);

                Assert.NotNull(resolve);
                Assert.AreSame(expected, resolve.Invoke(null, new object[] { "Inactive Exact Agent" }));
                Assert.IsNull(resolve.Invoke(null, new object[] { "inactive exact agent" }));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(first);
                UnityEngine.Object.DestroyImmediate(second);
            }
        }

        [Test]
        public void ResolveAgent_RejectsAmbiguousExactName()
        {
            GameObject first = new("Duplicate Native Agent");
            GameObject second = new("Duplicate Native Agent");
            first.SetActive(false);
            second.SetActive(false);
            first.AddComponent<LLMAgent>();
            second.AddComponent<LLMAgent>();
            try
            {
                MethodInfo resolve = typeof(LlmEndpointClientFactory).GetMethod(
                    "ResolveAgent",
                    BindingFlags.NonPublic | BindingFlags.Static);

                Assert.NotNull(resolve);
                Assert.IsNull(resolve.Invoke(null, new object[] { "Duplicate Native Agent" }));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(first);
                UnityEngine.Object.DestroyImmediate(second);
            }
        }

#endif

#if COREAI_HAS_LLMUNITY
        [Test]
        public void NativeActivationSource_WaitsForReadinessWithoutWarmupPrompt()
        {
            string source = File.ReadAllText(
                "Assets/CoreAiUnity/Runtime/Source/Features/Llm/Infrastructure/LlmEndpointClientFactory.cs");

            StringAssert.Contains("llm.WaitUntilReady()", source);
            StringAssert.DoesNotContain("agent.Warmup", source);
            StringAssert.Contains("FindObjectsInactive.Include", source);
        }
#endif

#if COREAI_HAS_LLMUNITY && !UNITY_WEBGL && !COREAI_NO_LLM
        [Test]
        public void NativeConfiguration_AppliesAndFingerprintsParallelSlotsAndContext()
        {
            GameObject host = new("Native Configuration Test");
            host.SetActive(false);
            try
            {
                LLM llm = host.AddComponent<LLM>();
                LlmEndpointDescriptor descriptor = new()
                {
                    EndpointId = "local",
                    Kind = LlmEndpointKind.LlmUnity,
                    Model = "",
                    Port = 14444,
                    GpuLayers = 12,
                    FlashAttention = true,
                    ParallelSlots = 3,
                    ContextWindowTokens = 8192
                };

                LlmEndpointClientFactory.ApplyNativeConfiguration(llm, descriptor, descriptor.Model);

                Assert.AreEqual(3, llm.parallelPrompts);
                Assert.AreEqual(8192, llm.contextSize);
                Assert.IsTrue(LlmEndpointClientFactory.NativeConfigurationMatches(
                    llm, descriptor, descriptor.Model));
                descriptor.ParallelSlots = 4;
                Assert.IsFalse(LlmEndpointClientFactory.NativeConfigurationMatches(
                    llm, descriptor, descriptor.Model));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(host);
            }
        }

        [Test]
        public async Task NativeActivationCoordinator_SerializesSameAgentAndCancelsQueuedWaiter()
        {
            GameObject host = new("Native Coordinator Test");
            host.SetActive(false);
            LLMAgent agent = host.AddComponent<LLMAgent>();
            try
            {
                await LlmUnityActivationCoordinator.WaitAsync(agent, CancellationToken.None);
                using CancellationTokenSource cancellation = new();
                Task queued = LlmUnityActivationCoordinator.WaitAsync(agent, cancellation.Token);
                Assert.IsFalse(queued.IsCompleted);

                cancellation.Cancel();

                OperationCanceledException caught = null;
                try
                {
                    await queued;
                }
                catch (OperationCanceledException ex)
                {
                    caught = ex;
                }

                Assert.IsNotNull(caught, "The canceled queued waiter must observe cancellation.");
            }
            finally
            {
                LlmUnityActivationCoordinator.Release(agent);
                UnityEngine.Object.DestroyImmediate(host);
            }
        }
#endif
    }
}
