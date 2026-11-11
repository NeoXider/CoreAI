#if COREAI_LLM
using System.Threading.Tasks;
using CoreAI.Ai;
using CoreAI.Decisions;
using CoreAI.Infrastructure.Llm;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace CoreAI.Core.Tests.EditMode.Decisions
{
    /// <summary>The OpenRouter backend: both routes behind one option, its extras, refusals and errors.</summary>
    [TestFixture]
    public sealed class OpenRouterDecisionClientEditModeTests
    {
        private static DecisionRequest TeamRequest()
        {
            return new DecisionRequest("My card was charged twice.",
                DecisionQuestion.Choice("team", "Which team should own this ticket?", "account", "frontend", "payments"),
                DecisionQuestion.YesNo("unsafe", "Is the message unsafe?"));
        }

        private static LlmEndpointDescriptor Endpoint()
        {
            return DecisionFixtures.Endpoint("or", LlmDecisionProtocol.OpenRouter, "https://openrouter.ai/api/v1",
                "typesafe/jev-1.14", "OPENROUTER_API_KEY");
        }

        [Test]
        public async Task DefaultRoute_IsSystemOne_WithOpenRouterHeaders()
        {
            RecordingTransport transport = new RecordingTransport().Enqueue(200, DecisionFixtures.OpenRouterRefusalResponse);
            OpenRouterDecisionClient client = new(Endpoint(), transport,
                new FixedSecretProvider("OPENROUTER_API_KEY", "or-key-1"));

            DecisionResult result = await client.DecideAsync(TeamRequest());

            Assert.IsTrue(result.Ok, result.Error);
            Assert.AreEqual("https://openrouter.ai/api/v1/systemone", transport.Requests[0].Url);
            Assert.AreEqual("Bearer or-key-1", transport.Header(0, "Authorization"));
            Assert.AreEqual(OpenAiHttpConstants.HttpRefererUnityUrl,
                transport.Header(0, OpenAiHttpConstants.HttpRefererHeaderName));
            Assert.AreEqual("CoreAI", transport.Header(0, "X-Title"));
            Assert.AreEqual("typesafe/jev-1.14", (string)JObject.Parse(transport.Requests[0].JsonBody)["model"]);
        }

        [Test]
        public async Task AlphaRoute_PostsToTheAlphaDecisionsEndpoint()
        {
            RecordingTransport transport = new RecordingTransport().Enqueue(200, DecisionFixtures.OpenRouterRefusalResponse);
            OpenRouterDecisionClient client = new(Endpoint(), transport, null,
                new OpenRouterDecisionOptions { Route = OpenRouterDecisionRoute.AlphaDecisions });

            await client.DecideAsync(TeamRequest());

            Assert.AreEqual("https://openrouter.ai/api/alpha/decisions", transport.Requests[0].Url);
        }

        [Test]
        public async Task Options_AddSessionUserAndProvider()
        {
            RecordingTransport transport = new RecordingTransport().Enqueue(200, DecisionFixtures.OpenRouterRefusalResponse);
            OpenRouterDecisionClient client = new(Endpoint(), transport, null, new OpenRouterDecisionOptions
            {
                SessionId = "match-7", User = "player-1", ProviderJson = "{\"order\":[\"TypeSafe\"]}"
            });

            await client.DecideAsync(TeamRequest());

            JObject body = JObject.Parse(transport.Requests[0].JsonBody);
            Assert.AreEqual("match-7", (string)body["session_id"]);
            Assert.AreEqual("player-1", (string)body["user"]);
            Assert.AreEqual("TypeSafe", (string)body["provider"]["order"][0]);
        }

        [Test]
        public async Task NoOptions_SendNoExtras()
        {
            RecordingTransport transport = new RecordingTransport().Enqueue(200, DecisionFixtures.OpenRouterRefusalResponse);
            OpenRouterDecisionClient client = new(Endpoint(), transport);

            await client.DecideAsync(TeamRequest());

            JObject body = JObject.Parse(transport.Requests[0].JsonBody);
            Assert.IsNull(body["session_id"]);
            Assert.IsNull(body["user"]);
            Assert.IsNull(body["provider"]);
        }

        [Test]
        public async Task InvalidProviderJson_IsRefusedBeforeSending()
        {
            RecordingTransport transport = new();
            OpenRouterDecisionClient client = new(Endpoint(), transport, null,
                new OpenRouterDecisionOptions { ProviderJson = "[1]" });

            DecisionResult result = await client.DecideAsync(TeamRequest());

            Assert.AreEqual(LlmErrorCode.InvalidRequest, result.ErrorCode);
            Assert.AreEqual(0, transport.Requests.Count);
        }

        [Test]
        public async Task Refusal_IsReported_PerQuestion()
        {
            RecordingTransport transport = new RecordingTransport().Enqueue(200, DecisionFixtures.OpenRouterRefusalResponse);
            OpenRouterDecisionClient client = new(Endpoint(), transport);

            DecisionResult result = await client.DecideAsync(TeamRequest());

            Assert.AreEqual("payments", result.Find("team").ChosenOptionId);
            Assert.IsTrue(result.Find("unsafe").Refused);
            Assert.AreEqual("typesafe/jev-1.13-20260917", result.Model);
        }

        [Test]
        public async Task InsufficientCredits_IsPaymentRequired()
        {
            RecordingTransport transport = new RecordingTransport().Enqueue(402, DecisionFixtures.OpenRouterCredits402);
            OpenRouterDecisionClient client = new(Endpoint(), transport);

            DecisionResult result = await client.DecideAsync(TeamRequest());

            Assert.AreEqual(LlmErrorCode.PaymentRequired, result.ErrorCode);
            Assert.AreEqual(OpenRouterDecisionClient.BackendName, result.Backend);
        }
    }
}
#endif
