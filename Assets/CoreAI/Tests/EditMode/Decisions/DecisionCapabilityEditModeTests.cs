using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Ai;
using CoreAI.Decisions;
using NUnit.Framework;

namespace CoreAI.Core.Tests.EditMode.Decisions
{
    /// <summary>
    /// The capability check: parsing <c>output_modalities</c> and <c>supported_parameters</c> from a model list,
    /// the probe over the GET seam, and the router refusing what the list refutes.
    /// </summary>
    [TestFixture]
    public sealed class DecisionCapabilityEditModeTests
    {
        [Test]
        public void LlamaCppModelList_AdvertisesDecisions()
        {
            IReadOnlyList<DecisionModelInfo> models = DecisionModelListWire.Parse(DecisionFixtures.LlamaCppModels);

            Assert.AreEqual(1, models.Count);
            Assert.AreEqual("d1-2B-Q4_K_M.gguf", models[0].Id);
            Assert.IsTrue(models[0].AdvertisesDecisions);
            Assert.IsFalse(models[0].SupportedParametersListed);
        }

        [Test]
        public void TextModel_DoesNotAdvertiseDecisions()
        {
            Assert.IsFalse(DecisionModelListWire.Parse(DecisionFixtures.LlamaCppTextModels)[0].AdvertisesDecisions);
        }

        [Test]
        public void OpenRouterModelList_ListsDecisionsAndLogprobs()
        {
            IReadOnlyList<DecisionModelInfo> models = DecisionModelListWire.Parse(DecisionFixtures.OpenRouterModels);

            Assert.IsTrue(DecisionModelListWire.Find(models, "typesafe/jev-1.14").AdvertisesDecisions);
            Assert.IsTrue(DecisionModelListWire.Find(models, "openai/gpt-4o-mini").AdvertisesLogprobs);
            Assert.IsFalse(DecisionModelListWire.Find(models, "some/chat").AdvertisesLogprobs);
            Assert.IsTrue(DecisionModelListWire.Find(models, "some/chat").SupportedParametersListed);
        }

        [Test]
        public void TopLevelOutputModalities_AndBareArrays_AreRead()
        {
            IReadOnlyList<DecisionModelInfo> models = DecisionModelListWire.Parse(
                "[{\"id\":\"m\",\"output_modalities\":[\"Decisions\"]}]");

            Assert.IsTrue(models[0].AdvertisesDecisions);
        }

        [TestCase("")]
        [TestCase("<html>proxy error</html>")]
        [TestCase("{\"data\":{}}")]
        public void MalformedModelList_IsEmpty(string body)
        {
            Assert.AreEqual(0, DecisionModelListWire.Parse(body).Count);
        }

        [Test]
        public void Find_SingleModelServer_AnswersForAnyId_ButNotAmongMany()
        {
            IReadOnlyList<DecisionModelInfo> single = DecisionModelListWire.Parse(DecisionFixtures.LlamaCppModels);
            IReadOnlyList<DecisionModelInfo> many = DecisionModelListWire.Parse(DecisionFixtures.OpenRouterModels);

            Assert.IsNotNull(DecisionModelListWire.Find(single, "d1-2B"));
            Assert.IsNull(DecisionModelListWire.Find(many, "unknown/model"));
        }

        [Test]
        public async Task Probe_ReadsTheModelList_WithTheResolvedKey()
        {
            FakeHttpGet http = new(200, DecisionFixtures.OpenRouterModels);
            ModelListDecisionCapabilityProbe probe = new(http, new FixedSecretProvider("OR", "or-key"));

            DecisionCapabilityReport report = await probe.ProbeAsync(
                DecisionFixtures.Endpoint("or", LlmDecisionProtocol.OpenRouter, "https://openrouter.ai/api/v1",
                    "typesafe/jev-1.14", "OR"), CancellationToken.None);

            Assert.AreEqual("https://openrouter.ai/api/v1/models", http.Urls[0]);
            Assert.AreEqual("Bearer or-key", http.Authorization[0]);
            Assert.IsTrue(report.Reachable);
            Assert.IsTrue(report.ModelListed);
            Assert.IsTrue(report.AdvertisesDecisions);
            Assert.IsFalse(report.Refutes(LlmDecisionProtocol.OpenRouter));
            Assert.IsTrue(report.Refutes(LlmDecisionProtocol.ChatLogprobs),
                "OpenRouter lists supported parameters and jev's do not include logprobs");
        }

        [Test]
        public void Report_ChatModelWithoutParameterList_IsNotRefutedForLogprobs()
        {
            DecisionCapabilityReport report = new()
            {
                Reachable = true, ModelListed = true, SupportedParametersListed = false
            };

            Assert.IsFalse(report.Refutes(LlmDecisionProtocol.ChatLogprobs), "llama.cpp does not list parameters");
            Assert.IsTrue(report.Refutes(LlmDecisionProtocol.SystemOne));
        }

        [Test]
        public async Task Probe_HttpError_IsUnreachable_AndRefutesNothing()
        {
            ModelListDecisionCapabilityProbe probe = new(new FakeHttpGet(404, "not found"));

            DecisionCapabilityReport report = await probe.ProbeAsync(
                DecisionFixtures.Endpoint("local", LlmDecisionProtocol.SystemOne), CancellationToken.None);

            Assert.IsFalse(report.Reachable);
            Assert.AreEqual(404, report.StatusCode);
            Assert.IsFalse(report.Refutes(LlmDecisionProtocol.SystemOne));
        }

        [Test]
        public async Task Probe_SocketFailure_IsReported_NotThrown()
        {
            ModelListDecisionCapabilityProbe probe = new(new FakeHttpGet(0, "") { Throw = new InvalidOperationException("refused") });

            DecisionCapabilityReport report = await probe.ProbeAsync(
                DecisionFixtures.Endpoint("local", LlmDecisionProtocol.SystemOne), CancellationToken.None);

            Assert.IsFalse(report.Reachable);
            StringAssert.Contains("InvalidOperationException", report.Error);
        }

        [Test]
        public async Task Probe_SilentServer_TimesOutOnTheClock()
        {
            ManualDecisionClock clock = new();
            FakeHttpGet http = new(200, "") { Hold = true };
            ModelListDecisionCapabilityProbe probe = new(http, null, clock);

            Task<DecisionCapabilityReport> pending = probe.ProbeAsync(
                DecisionFixtures.Endpoint("local", LlmDecisionProtocol.SystemOne), CancellationToken.None);
            clock.Advance(ModelListDecisionCapabilityProbe.DefaultTimeoutSeconds);
            DecisionCapabilityReport report = await pending;

            Assert.IsFalse(report.Reachable);
            StringAssert.Contains("did not answer", report.Error);
            Assert.IsTrue(http.Tokens[0].IsCancellationRequested);
        }

        [Test]
        public async Task Router_ProbeOfATextModel_RefusesTheSystemOneRoute()
        {
            DecisionRouter router = new(DecisionRouteTable.AllTo("local"), new StaticDecisionEndpointSource(new[]
            {
                DecisionFixtures.Endpoint("local", LlmDecisionProtocol.SystemOne)
            }));

            DecisionCapabilityReport report = await router.ProbeAsync(
                new ModelListDecisionCapabilityProbe(new FakeHttpGet(200, DecisionFixtures.LlamaCppTextModels)), "local");

            Assert.IsFalse(report.AdvertisesDecisions);
            Assert.AreEqual(DecisionErrorReasons.CapabilityNotAdvertised, router.Resolve("x").Reason);
        }

        [Test]
        public async Task Router_ProbeOfADecisionModel_KeepsTheRoute()
        {
            DecisionRouter router = new(DecisionRouteTable.AllTo("local"), new StaticDecisionEndpointSource(new[]
            {
                DecisionFixtures.Endpoint("local", LlmDecisionProtocol.SystemOne)
            }));

            await router.ProbeAsync(new ModelListDecisionCapabilityProbe(
                new FakeHttpGet(200, DecisionFixtures.LlamaCppModels)), "local");

            Assert.IsTrue(router.Resolve("x").Ok);
        }

        private sealed class FakeHttpGet : IDecisionHttpGet
        {
            private readonly int _status;
            private readonly string _body;

            public FakeHttpGet(int status, string body)
            {
                _status = status;
                _body = body;
            }

            public bool Hold { get; set; }

            public Exception Throw { get; set; }

            public List<string> Urls { get; } = new();

            public List<string> Authorization { get; } = new();

            public List<CancellationToken> Tokens { get; } = new();

            public Task<DecisionHttpResponse> GetAsync(string url, IReadOnlyList<KeyValuePair<string, string>> headers,
                int timeoutSeconds, CancellationToken cancellationToken)
            {
                Urls.Add(url);
                Tokens.Add(cancellationToken);
                string auth = null;
                foreach (KeyValuePair<string, string> header in headers)
                {
                    if (header.Key == "Authorization")
                    {
                        auth = header.Value;
                    }
                }

                Authorization.Add(auth);
                if (Throw != null)
                {
                    throw Throw;
                }

                if (Hold)
                {
                    TaskCompletionSource<DecisionHttpResponse> never = new();
                    cancellationToken.Register(() => never.TrySetCanceled(cancellationToken));
                    return never.Task;
                }

                return Task.FromResult(new DecisionHttpResponse { StatusCode = _status, BodyText = _body });
            }
        }
    }
}
