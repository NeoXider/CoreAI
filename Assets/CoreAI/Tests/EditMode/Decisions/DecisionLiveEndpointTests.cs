#if COREAI_LLM
using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Ai;
using CoreAI.Decisions;
using CoreAI.Infrastructure.Llm;
using NUnit.Framework;

namespace CoreAI.Core.Tests.EditMode.Decisions
{
    /// <summary>
    /// Talks to a real decision endpoint. Explicit: never part of a suite run; it is skipped unless
    /// <c>COREAI_DECISIONS_BASE_URL</c> is set. Variables: <c>COREAI_DECISIONS_BASE_URL</c> (for example
    /// <c>http://127.0.0.1:8080/v1</c>), <c>COREAI_DECISIONS_MODEL</c> (optional for llama-server),
    /// <c>COREAI_DECISIONS_PROTOCOL</c> (<c>SystemOne</c> default, <c>OpenRouter</c> or <c>ChatLogprobs</c>) and
    /// <c>COREAI_DECISIONS_SECRET_REF</c>, the NAME of the environment variable that holds the key, resolved through
    /// the secret-reference path like every endpoint.
    /// </summary>
    [TestFixture]
    [Explicit("Needs a live decision endpoint; see the class summary.")]
    [Category("LiveDecisions")]
    public sealed class DecisionLiveEndpointTests
    {
        private const double Tolerance = 1e-6;

        [Test]
        public async Task LiveEndpoint_AnswersTheFixtureSet_WithNormalisedProbabilities()
        {
            LlmEndpointDescriptor endpoint = EndpointFromEnvironment();
            IDecisionClient client = new DecisionClientFactory(new HttpClientOpenAiTransport(),
                new EnvironmentDecisionSecretProvider()).Create(endpoint);
            DecisionRequest request = DecisionFixtures.NpcRequest();
            using CancellationTokenSource cancellation = new();

            Stopwatch watch = Stopwatch.StartNew();
            DecisionResult result = await client.DecideAsync(request, cancellation.Token);
            watch.Stop();

            TestContext.WriteLine($"backend={result.Backend} model={result.Model} latency={watch.ElapsedMilliseconds} ms " +
                                  $"calibrated={result.Calibrated} usage={result.Usage.PromptTokens}/{result.Usage.CompletionTokens}");
            Assert.IsTrue(result.Ok, $"{result.ErrorCode}/{result.ErrorReason}: {result.Error}");
            foreach (DecisionAnswer answer in result.Answers)
            {
                double sum = 0d;
                foreach (double p in answer.Probabilities)
                {
                    sum += p;
                }

                TestContext.WriteLine($"{answer.QuestionId}: {answer.ChosenOptionId} confidence={answer.Confidence:0.000}");
                Assert.AreEqual(1d, sum, Tolerance, answer.QuestionId);
                Assert.That(answer.Confidence, Is.InRange(0d, 1d));
                Assert.GreaterOrEqual(answer.ChosenIndex, 0, answer.QuestionId + " was refused");
            }

            Assert.AreEqual(endpoint.DecisionProtocol != LlmDecisionProtocol.ChatLogprobs, result.Calibrated);
        }

        [Test]
        public async Task LiveEndpoint_ModelList_AdvertisesTheProtocol()
        {
            LlmEndpointDescriptor endpoint = EndpointFromEnvironment();
            using HttpClientDecisionHttpGet http = new();
            ModelListDecisionCapabilityProbe probe = new(http, new EnvironmentDecisionSecretProvider());

            DecisionCapabilityReport report = await probe.ProbeAsync(endpoint, CancellationToken.None);

            TestContext.WriteLine($"reachable={report.Reachable} status={report.StatusCode} listed={report.ModelListed} " +
                                  $"decisions={report.AdvertisesDecisions} logprobs={report.AdvertisesLogprobs} {report.Error}");
            Assert.IsTrue(report.Reachable, report.Error);
            Assert.IsFalse(report.Refutes(endpoint.DecisionProtocol), report.Error);
        }

        private static LlmEndpointDescriptor EndpointFromEnvironment()
        {
            string baseUrl = Environment.GetEnvironmentVariable("COREAI_DECISIONS_BASE_URL") ?? "";
            if (baseUrl.Trim().Length == 0)
            {
                Assert.Ignore("COREAI_DECISIONS_BASE_URL is not set.");
            }

            string protocolText = Environment.GetEnvironmentVariable("COREAI_DECISIONS_PROTOCOL") ?? "";
            LlmDecisionProtocol protocol = Enum.TryParse(protocolText.Trim(), true, out LlmDecisionProtocol parsed) &&
                                           parsed != LlmDecisionProtocol.None
                ? parsed
                : LlmDecisionProtocol.SystemOne;
            return new LlmEndpointDescriptor
            {
                EndpointId = "live",
                Kind = LlmEndpointKind.HttpOpenAi,
                BaseUrl = baseUrl.Trim(),
                Model = (Environment.GetEnvironmentVariable("COREAI_DECISIONS_MODEL") ?? "").Trim(),
                SecretReference = (Environment.GetEnvironmentVariable("COREAI_DECISIONS_SECRET_REF") ?? "").Trim(),
                DecisionProtocol = protocol,
                RequestTimeoutSeconds = 60
            };
        }
    }
}
#endif
