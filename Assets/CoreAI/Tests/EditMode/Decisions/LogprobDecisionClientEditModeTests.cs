#if COREAI_LLM
using System.Threading.Tasks;
using CoreAI.Ai;
using CoreAI.Decisions;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace CoreAI.Core.Tests.EditMode.Decisions
{
    /// <summary>
    /// The logprob fallback over an OpenAI-compatible chat model: the forced single-token label, the softmax over
    /// label tokens, renormalisation, the uncalibrated flag, and the failures a chat server can produce.
    /// </summary>
    [TestFixture]
    public sealed class LogprobDecisionClientEditModeTests
    {
        private const double Tolerance = 1e-6;

        private static DecisionRequest ActionRequest()
        {
            return new DecisionRequest("A goblin faces the player.",
                DecisionQuestion.Choice("action", "What should the goblin do?", new[]
                {
                    new DecisionOption("attack", "Hit the player."), new DecisionOption("flee"), new DecisionOption("trade")
                }));
        }

        private static LogprobDecisionClient Client(RecordingTransport transport, LogprobDecisionOptions options = null)
        {
            return new LogprobDecisionClient(
                DecisionFixtures.Endpoint("chat", LlmDecisionProtocol.ChatLogprobs, "https://api.openai.com/v1",
                    "gpt-4o-mini"), transport, null, options);
        }

        [Test]
        public async Task Request_AsksForLogprobs_AndForcesTheLabelWithAJsonSchemaEnum()
        {
            RecordingTransport transport = new RecordingTransport().Enqueue(200, DecisionFixtures.ChatLogprobsResponse);

            await Client(transport).DecideAsync(ActionRequest());

            Assert.AreEqual("https://api.openai.com/v1/chat/completions", transport.Requests[0].Url);
            JObject body = JObject.Parse(transport.Requests[0].JsonBody);
            Assert.AreEqual(true, (bool)body["logprobs"]);
            Assert.AreEqual(20, (int)body["top_logprobs"]);
            Assert.AreEqual(0, (int)body["temperature"]);
            Assert.AreEqual(false, (bool)body["stream"]);
            Assert.AreEqual("json_schema", (string)body["response_format"]["type"]);
            JToken schemaEnum = body["response_format"]["json_schema"]["schema"]["properties"]["answer"]["enum"];
            Assert.AreEqual(new[] { "A", "B", "C" }, schemaEnum.ToObject<string[]>());
            string prompt = (string)body["messages"][1]["content"];
            StringAssert.Contains("A = attack: Hit the player.", prompt);
            StringAssert.Contains("B = flee", prompt);
            Assert.IsNull(body["grammar"]);
        }

        [Test]
        public async Task GrammarForcing_SendsGbnf_AndNoResponseFormat()
        {
            RecordingTransport transport = new RecordingTransport().Enqueue(200, DecisionFixtures.ChatLogprobsResponse);

            await Client(transport, new LogprobDecisionOptions { Forcing = LogprobLabelForcing.Grammar })
                .DecideAsync(ActionRequest());

            JObject body = JObject.Parse(transport.Requests[0].JsonBody);
            Assert.AreEqual("root ::= \"A\" | \"B\" | \"C\"", (string)body["grammar"]);
            Assert.IsNull(body["response_format"]);
        }

        [Test]
        public async Task PromptOnlyForcing_SendsNeitherSchemaNorGrammar()
        {
            RecordingTransport transport = new RecordingTransport().Enqueue(200, DecisionFixtures.ChatLogprobsResponse);

            await Client(transport, new LogprobDecisionOptions { Forcing = LogprobLabelForcing.PromptOnly })
                .DecideAsync(ActionRequest());

            JObject body = JObject.Parse(transport.Requests[0].JsonBody);
            Assert.IsNull(body["grammar"]);
            Assert.IsNull(body["response_format"]);
        }

        [Test]
        public async Task Answer_IsASoftmaxOverLabelTokens_RenormalisedAndUncalibrated()
        {
            RecordingTransport transport = new RecordingTransport().Enqueue(200, DecisionFixtures.ChatLogprobsResponse);

            DecisionResult result = await Client(transport).DecideAsync(ActionRequest());

            Assert.IsTrue(result.Ok, result.Error);
            DecisionAnswer answer = result.Find("action");
            Assert.AreEqual("flee", answer.ChosenOptionId);
            Assert.AreEqual(0.25 / 0.92, answer.ProbabilityOf("attack"), Tolerance);
            Assert.AreEqual(0.62 / 0.92, answer.ProbabilityOf("flee"), Tolerance, "B and b are one label");
            Assert.AreEqual(0.05 / 0.92, answer.ProbabilityOf("trade"), Tolerance);
            Assert.AreEqual(1d, answer.Probabilities[0] + answer.Probabilities[1] + answer.Probabilities[2], Tolerance);
            Assert.AreEqual(0.62 / 0.92, answer.Confidence, Tolerance);
            Assert.IsFalse(answer.Calibrated);
            Assert.IsFalse(result.Calibrated);
            Assert.AreEqual(LogprobDecisionClient.BackendName, result.Backend);
            Assert.AreEqual(96, result.Usage.PromptTokens);
        }

        [Test]
        public async Task YesNo_MergesCaseAndSpacingVariants()
        {
            RecordingTransport transport = new RecordingTransport().Enqueue(200, DecisionFixtures.ChatLogprobsYesNoResponse);

            DecisionResult result = await Client(transport).DecideAsync(
                new DecisionRequest("s", DecisionQuestion.YesNo("hostile", "Is the player hostile?")));

            DecisionAnswer answer = result.Find("hostile");
            Assert.IsTrue(answer.IsYes);
            Assert.AreEqual(0.7 / 0.9, answer.ProbabilityOf("yes"), Tolerance);
            Assert.AreEqual(0.2 / 0.9, answer.ProbabilityOf("no"), Tolerance);
        }

        [Test]
        public async Task Score_HasDigitLabels_AndAnExpectedLevel()
        {
            RecordingTransport transport = new RecordingTransport().Enqueue(200, DecisionFixtures.ChatLogprobsScoreResponse);

            DecisionResult result = await Client(transport).DecideAsync(new DecisionRequest("s",
                DecisionQuestion.Score("danger", "How dangerous?", new[] { "safe", "risky", "deadly" })));

            DecisionAnswer answer = result.Find("danger");
            Assert.AreEqual(2, answer.ChosenIndex);
            Assert.AreEqual(0.3 + 2 * 0.5, answer.ExpectedScore.Value, Tolerance);
            JObject body = JObject.Parse(transport.Requests[0].JsonBody);
            StringAssert.Contains("0 = safe", (string)body["messages"][1]["content"]);
        }

        [Test]
        public async Task EveryQuestion_IsOneCall_AndUsageIsSummed()
        {
            RecordingTransport transport = new RecordingTransport()
                .Enqueue(200, DecisionFixtures.ChatLogprobsYesNoResponse)
                .Enqueue(200, DecisionFixtures.ChatLogprobsResponse);
            DecisionRequest request = new("s", DecisionQuestion.YesNo("hostile", "a"),
                DecisionQuestion.Choice("action", "b", "attack", "flee", "trade"));

            DecisionResult result = await Client(transport).DecideAsync(request);

            Assert.IsTrue(result.Ok, result.Error);
            Assert.AreEqual(2, transport.Requests.Count);
            Assert.AreEqual(146, result.Usage.PromptTokens);
            Assert.AreEqual(6, result.Usage.CompletionTokens);
            Assert.AreEqual(152, result.Usage.TotalTokens);
        }

        [Test]
        public async Task ServerWithoutLogprobs_IsANamedPermanentFailure()
        {
            RecordingTransport transport = new RecordingTransport().Enqueue(200, DecisionFixtures.ChatWithoutLogprobsResponse);

            DecisionResult result = await Client(transport).DecideAsync(ActionRequest());

            Assert.AreEqual(LlmErrorCode.PermanentProviderError, result.ErrorCode);
            Assert.AreEqual(DecisionErrorReasons.LogprobsMissing, result.ErrorReason);
        }

        [Test]
        public async Task AnswerWithoutALabelToken_IsEmptyResponse()
        {
            RecordingTransport transport = new RecordingTransport().Enqueue(200, DecisionFixtures.ChatWithoutLabelResponse);

            DecisionResult result = await Client(transport).DecideAsync(ActionRequest());

            Assert.AreEqual(LlmErrorCode.EmptyResponse, result.ErrorCode);
            Assert.AreEqual(DecisionErrorReasons.NoLabelToken, result.ErrorReason);
        }

        [Test]
        public async Task MoreOptionsThanTopLogprobs_IsRefusedBeforeSending()
        {
            RecordingTransport transport = new();

            DecisionResult result = await Client(transport).DecideAsync(new DecisionRequest("s",
                DecisionQuestion.Choice("q", "pick", DecisionRequestValidationEditModeTests.Options(21))));

            Assert.AreEqual(LlmErrorCode.InvalidRequest, result.ErrorCode);
            Assert.AreEqual(DecisionErrorReasons.TooManyOptionsForBackend, result.ErrorReason);
            Assert.AreEqual(0, transport.Requests.Count);
        }

        [Test]
        public async Task TwentyOptions_FitTheDefaultTopLogprobs()
        {
            RecordingTransport transport = new RecordingTransport().Enqueue(200, DecisionFixtures.ChatLogprobsResponse);

            DecisionResult result = await Client(transport).DecideAsync(new DecisionRequest("s",
                DecisionQuestion.Choice("q", "pick", DecisionRequestValidationEditModeTests.Options(20))));

            Assert.AreEqual(1, transport.Requests.Count);
            Assert.IsTrue(result.Ok, result.Error);
            Assert.AreEqual(20, result.Find("q").Probabilities.Count);
        }

        [Test]
        public void ScanLabels_ReadsTopLogprobsAsAMap()
        {
            string json = "{\"choices\":[{\"logprobs\":{\"content\":[{\"token\":\"A\",\"logprob\":-0.1," +
                          "\"top_logprobs\":{\"A\":-0.1,\"B\":-2.5}}]}}]}";

            LogprobLabelScan scan = LogprobChatWire.ScanLabels(json, new[] { "A", "B" });

            Assert.IsTrue(scan.Ok);
            Assert.AreEqual(-0.1, scan.Logprobs[0], Tolerance);
            Assert.AreEqual(-2.5, scan.Logprobs[1], Tolerance);
        }
    }
}
#endif
