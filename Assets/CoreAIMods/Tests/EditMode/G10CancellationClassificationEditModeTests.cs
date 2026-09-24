using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Ai;
using CoreAI.Diagnostics.G10;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode
{
    /// <summary>
    /// Pins the boundary where a cancellation must not be counted as a provider failure.
    /// <para>
    /// WHY: the 2026-09-01 G10 real-provider run reported 23 provider failures that LM Studio's own
    /// server log contradicted — it had logged no error at all. They were client disconnects:
    /// `MeaiLlmClient` turns an <see cref="OperationCanceledException"/> into an unsuccessful
    /// <see cref="LlmCompletionResult"/> carrying <see cref="LlmErrorCode.Cancelled"/> rather than
    /// letting the exception escape, and the measured client counted every unsuccessful result as a
    /// provider failure. The capacity verdict of an entire acceptance run rested on that counter, so
    /// it is pinned here: a miscounted cancellation reads as "the backend is broken" when the truth
    /// is "we cancelled it ourselves".
    /// </para>
    /// </summary>
    public sealed class G10CancellationClassificationEditModeTests
    {
        private const string TraceId = "trace-g10-classification";

        private static async Task<G10ProviderObservation> ObserveAsync(LlmCompletionResult inner)
        {
            G10ProviderProbe probe = new();
            G10MeasuredLlmClient client = new(new StubLlmClient(inner), probe);
            try
            {
                await client.CompleteAsync(
                    new LlmCompletionRequest { TraceId = TraceId },
                    CancellationToken.None);
            }
            catch (OperationCanceledException)
            {
                // Expected for the cancelled case: the measured client re-raises so the harness's own
                // cancellation bookkeeping sees it.
            }

            Assert.IsTrue(probe.TryGet(TraceId, out G10ProviderObservation observation),
                "the probe must record an observation for every invocation");
            return observation;
        }

        [Test]
        public async Task ResultCarryingCancelledErrorCode_IsCountedAsCancelled_NotAProviderFailure()
        {
            G10ProviderObservation observation = await ObserveAsync(new LlmCompletionResult
            {
                Ok = false,
                ErrorCode = LlmErrorCode.Cancelled,
                Error = "The operation was canceled."
            });

            Assert.IsTrue(observation.Cancelled,
                "a result carrying ErrorCode.Cancelled is our own cancellation, not a backend failure");
            Assert.IsFalse(observation.Succeeded);
        }

        [Test]
        public async Task GenuineProviderError_IsStillCountedAsAFailure()
        {
            // The negative twin: the fix must not make every failure disappear into "cancelled".
            G10ProviderObservation observation = await ObserveAsync(new LlmCompletionResult
            {
                Ok = false,
                ErrorCode = LlmErrorCode.BackendUnavailable,
                Error = "connection refused"
            });

            Assert.IsFalse(observation.Cancelled,
                "a real backend error must not be laundered into a cancellation");
            Assert.IsFalse(observation.Succeeded);
        }

        [Test]
        public async Task SuccessfulCompletion_IsCountedAsServed()
        {
            G10ProviderObservation observation = await ObserveAsync(new LlmCompletionResult
            {
                Ok = true,
                Content = "hello"
            });

            Assert.IsTrue(observation.Succeeded);
            Assert.IsFalse(observation.Cancelled);
        }

        [Test]
        public async Task OkResultWithEmptyContent_IsNotCountedAsServed()
        {
            // WHY: "served" means the actor got an answer. An empty body is not an answer, and
            // counting it would inflate the served fraction the gate is judged on.
            G10ProviderObservation observation = await ObserveAsync(new LlmCompletionResult
            {
                Ok = true,
                Content = "   "
            });

            Assert.IsFalse(observation.Succeeded,
                "an empty completion must not inflate the served fraction");
            Assert.IsFalse(observation.Cancelled);
        }

#if !COREAI_LLM
        [Test]
        public void RealProviderWithoutLlmModule_RefusesInsteadOfMeasuringAStub()
        {
            // WHY: without COREAI_LLM the HTTP provider is stripped. A composition that quietly
            // substituted the scripted stub would publish stub latencies as a real-provider capacity
            // measurement, so RealProvider mode must refuse and name the missing module.
            G10MeasurementConfiguration configuration = new()
            {
                Provider = new G10ProviderConfiguration
                {
                    ProviderMode = G10ProviderMode.RealProvider,
                    Endpoint = "http://127.0.0.1:1/v1",
                    ModelId = "g10-no-llm-regression",
                    ContextCapTokens = 4096,
                    OutputCapTokens = 256,
                    BackendConcurrency = 1,
                    OrchestratorConcurrency = 1,
                    RequestTimeoutSeconds = 30
                }
            };
            Assert.That(configuration.Validate(), Is.Empty,
                "the configuration must be valid, so the refusal can only come from the missing LLM module");

            InvalidOperationException error = Assert.Throws<InvalidOperationException>(
                () => G10MeasurementComposition.Compose(configuration),
                "RealProvider mode must refuse in a build without COREAI_LLM, not fall back to a stub provider");

            StringAssert.Contains("RealProvider", error.Message);
            StringAssert.Contains("COREAI_LLM", error.Message);
        }
#endif

        private sealed class StubLlmClient : ILlmClient
        {
            private readonly LlmCompletionResult _result;

            public StubLlmClient(LlmCompletionResult result)
            {
                _result = result;
            }

            public bool SupportsNativeToolCalling => false;

            public bool SupportsNativeToolCallingForRole(string agentRoleId)
            {
                return false;
            }

            public bool SupportsNativeToolCallingForRole(string agentRoleId, string routingProfileId)
            {
                return false;
            }

            public int? ResolveContextWindowTokensForRole(string agentRoleId, string routingProfileId)
            {
                return null;
            }

            public void SetTools(IReadOnlyList<ILlmTool> tools)
            {
            }

            public Task<LlmCompletionResult> CompleteAsync(
                LlmCompletionRequest request,
                CancellationToken cancellationToken = default)
            {
                return Task.FromResult(_result);
            }
        }
    }
}
