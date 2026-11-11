using System;
using CoreAI.Ai;
using CoreAI.Decisions;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace CoreAI.Core.Tests.EditMode.Decisions
{
    /// <summary>
    /// The System One wire against fixtures written from the documented shapes: request body per dialect, URLs,
    /// and response parsing into renormalised, calibrated answers, with the failure twins.
    /// </summary>
    [TestFixture]
    public sealed class SystemOneWireEditModeTests
    {
        private const double Tolerance = 1e-6;

        [Test]
        public void Request_HasModelStateAndTypedQuestions()
        {
            JObject body = Build(DecisionFixtures.NpcRequest(), "jev-1.14", SystemOneDialect.TypeSafe());

            Assert.AreEqual("jev-1.14", (string)body["model"]);
            Assert.AreEqual("A goblin with 3 hp faces the player.", (string)body["state"]);
            JObject questions = (JObject)body["questions"];
            Assert.AreEqual(new[] { "hostile", "action", "danger" }, PropertyNames(questions));

            JObject hostile = (JObject)questions["hostile"];
            Assert.AreEqual("noul", (string)hostile["type"]);
            Assert.AreEqual("Is the player hostile?", (string)hostile["instructions"]);
            Assert.AreEqual("the player attacks", (string)hostile["criteria"]["true"]);
            Assert.AreEqual("the player trades", (string)hostile["criteria"]["false"]);

            JObject action = (JObject)questions["action"];
            Assert.AreEqual("choice", (string)action["type"]);
            Assert.AreEqual("Hit the player.", (string)action["criteria"]["attack"]);
            Assert.AreEqual(JTokenType.Null, action["criteria"]["flee"].Type, "an option without description is null");
            Assert.AreEqual(new[] { "attack", "flee", "trade" }, PropertyNames((JObject)action["criteria"]));

            JObject danger = (JObject)questions["danger"];
            Assert.AreEqual("score", (string)danger["type"]);
            Assert.AreEqual(new[] { "safe", "wary", "risky", "deadly" }, ((JArray)danger["criteria"]).ToObject<string[]>());

            Assert.IsNull(body["assets"]);
            Assert.IsNull(body["files"]);
            Assert.IsNull(body["images"]);
        }

        [Test]
        public void Request_WithoutModelOrDescriptions_OmitsThem()
        {
            DecisionRequest request = new("s", DecisionQuestion.YesNo("q", ""));

            JObject body = Build(request, "", SystemOneDialect.LlamaCpp());

            Assert.IsNull(body["model"], "llama.cpp ignores model; an empty one is not sent");
            Assert.IsNull(body["questions"]["q"]["criteria"]);
            Assert.IsNull(body["questions"]["q"]["instructions"]);
        }

        [Test]
        public void Request_AssetsDialect_KeysEveryImageAndReferencesItInTheState()
        {
            DecisionRequest request = WithImages(AiAttachment.Image(new byte[] { 1, 2 }, "image/png"),
                AiAttachment.Image(new byte[] { 3 }, "image/jpeg"));

            JObject body = Build(request, "m", SystemOneDialect.TypeSafe());

            JObject assets = (JObject)body["assets"];
            Assert.AreEqual("png", (string)assets["image_1"]["format"]);
            Assert.AreEqual("image", (string)assets["image_1"]["type"]);
            Assert.AreEqual(Convert.ToBase64String(new byte[] { 1, 2 }), (string)assets["image_1"]["data_base64"]);
            Assert.AreEqual("jpg", (string)assets["image_2"]["format"]);
            StringAssert.Contains("image_1", (string)body["state"]);
            StringAssert.Contains("image_2", (string)body["state"]);
        }

        [Test]
        public void Request_AssetsDialect_RefusesWebp_ButOllamaTakesIt()
        {
            DecisionRequest request = WithImages(AiAttachment.Image(new byte[] { 9 }, "image/webp"));

            JObject refused = SystemOneWire.BuildRequestBody(request, "m", SystemOneDialect.TypeSafe(),
                out DecisionValidationError error);
            JObject ollama = Build(request, "m", SystemOneDialect.Ollama());

            Assert.IsNull(refused);
            Assert.AreEqual(DecisionErrorReasons.UnsupportedImageFormat, error.Code);
            Assert.AreEqual(Convert.ToBase64String(new byte[] { 9 }), (string)ollama["images"][0],
                "Ollama takes raw base64, no data: prefix");
        }

        [Test]
        public void Request_LlamaCppDialect_SendsDataUrlFiles()
        {
            DecisionRequest request = WithImages(AiAttachment.Image(new byte[] { 1 }, "image/png"));

            JObject body = Build(request, "m", SystemOneDialect.LlamaCpp());

            Assert.AreEqual("data:image/png;base64," + Convert.ToBase64String(new byte[] { 1 }), (string)body["files"][0]);
            Assert.AreEqual("s", (string)body["state"], "data-URL files need no reference in the state");
        }

        [TestCase("http://127.0.0.1:8080/v1", "http://127.0.0.1:8080/v1/systemone")]
        [TestCase("http://127.0.0.1:8080/v1/", "http://127.0.0.1:8080/v1/systemone")]
        [TestCase("http://127.0.0.1:8080", "http://127.0.0.1:8080/v1/systemone")]
        [TestCase("https://api.typesafe.ai/v1", "https://api.typesafe.ai/v1/systemone")]
        [TestCase("https://openrouter.ai/api/v1", "https://openrouter.ai/api/v1/systemone")]
        public void Url_AppendsTheRouteLikeChat(string baseUrl, string expected)
        {
            Assert.AreEqual(expected, SystemOneWire.BuildUrl(baseUrl));
        }

        [TestCase("https://openrouter.ai/api/v1", "https://openrouter.ai/api/alpha/decisions")]
        [TestCase("https://openrouter.ai", "https://openrouter.ai/api/alpha/decisions")]
        public void Url_OpenRouterAlpha(string baseUrl, string expected)
        {
            Assert.AreEqual(expected, DecisionUrls.OpenRouterAlphaDecisions(baseUrl));
        }

        [TestCase("https://api.typesafe.ai/v1", "typesafe")]
        [TestCase("https://openrouter.ai/api/v1", "openrouter")]
        [TestCase("http://localhost:11434/v1", "ollama")]
        [TestCase("http://localhost:1234/v1", "lmstudio")]
        [TestCase("http://127.0.0.1:8080/v1", "llama.cpp")]
        public void Dialect_IsPickedFromTheBaseUrl(string baseUrl, string expected)
        {
            Assert.AreEqual(expected, SystemOneDialect.ForBaseUrl(baseUrl).Name);
        }

        [Test]
        public void Response_TypeSafeFixture_GivesRenormalisedCalibratedAnswers()
        {
            DecisionRequest request = DecisionFixtures.NpcRequest();
            request.ConsumerId = "npc-brain";

            DecisionResult result = SystemOneWire.ParseResponse(DecisionFixtures.TypeSafeResponse, request, "systemone");

            Assert.IsTrue(result.Ok, result.Error);
            Assert.IsTrue(result.Calibrated);
            Assert.AreEqual("jev-1.14-20261001", result.Model);
            Assert.AreEqual(3, result.Answers.Count);

            DecisionAnswer hostile = result.Find("hostile");
            Assert.IsTrue(hostile.IsYes);
            Assert.AreEqual(0.82, hostile.Probabilities[0], Tolerance);
            Assert.AreEqual(0.18, hostile.Probabilities[1], Tolerance);
            Assert.AreEqual(0.82, hostile.Confidence, Tolerance, "noul has no confidence field: max(p, 1-p)");

            DecisionAnswer action = result.Find("action");
            Assert.AreEqual("flee", action.ChosenOptionId);
            Assert.AreEqual(1, action.ChosenIndex);
            Assert.AreEqual(0.71, action.Confidence, Tolerance);
            Assert.AreEqual(0.79, action.ProbabilityOf("flee"), Tolerance);

            DecisionAnswer danger = result.Find("danger");
            Assert.AreEqual(3, danger.ChosenIndex, "the most likely level");
            Assert.AreEqual(2.4, danger.ExpectedScore.Value, Tolerance);

            foreach (DecisionAnswer answer in result.Answers)
            {
                Assert.AreEqual(1d, Sum(answer), Tolerance, answer.QuestionId);
                Assert.IsTrue(answer.Calibrated, answer.QuestionId);
            }

            Assert.AreEqual(174, result.Usage.PromptTokens);
            Assert.AreEqual(3, result.Usage.CompletionTokens);
            Assert.AreEqual(177, result.Usage.TotalTokens);
            Assert.AreEqual("npc-brain", result.Usage.RoleId);
            Assert.IsTrue(result.Usage.Success);
        }

        [Test]
        public void Response_OllamaFixture_RenormalisesAServerSumBelowOne()
        {
            DecisionRequest request = new("checkout returns 500",
                DecisionQuestion.Choice("label", "", "billing", "bug", "account"));

            DecisionResult result = SystemOneWire.ParseResponse(DecisionFixtures.OllamaResponse, request, "systemone");

            DecisionAnswer label = result.Find("label");
            Assert.AreEqual(1d, Sum(label), Tolerance);
            Assert.AreEqual(0.9781 / 0.9999, label.ProbabilityOf("bug"), Tolerance);
            Assert.AreEqual("bug", label.ChosenOptionId);
        }

        [Test]
        public void Response_LlamaCppFixture_ParsesAllThreeTypes()
        {
            DecisionRequest request = new("I was charged twice.",
                DecisionQuestion.Choice("route", "", "billing", "shipping", "technical"),
                DecisionQuestion.YesNo("refund", "Refund?"),
                DecisionQuestion.Score("urgency", "", new[] { "can wait", "this week", "today", "right now" }));

            DecisionResult result = SystemOneWire.ParseResponse(DecisionFixtures.LlamaCppResponse, request, "systemone");

            Assert.IsTrue(result.Ok, result.Error);
            Assert.AreEqual("billing", result.Find("route").ChosenOptionId);
            Assert.AreEqual(0.6328, result.Find("refund").ProbabilityOf("yes"), Tolerance);
            Assert.AreEqual(2, result.Find("urgency").ChosenIndex);
            Assert.AreEqual(1.7, result.Find("urgency").ExpectedScore.Value, Tolerance);
            Assert.AreEqual(0, result.Usage.CompletionTokens);
        }

        [Test]
        public void Response_NoulWithConfidence_UsesTheServerValue()
        {
            DecisionRequest request = new("s", DecisionQuestion.YesNo("q", "a"));

            DecisionResult result = SystemOneWire.ParseResponse(
                "{\"answers\":{\"q\":{\"type\":\"noul\",\"noul\":0.3,\"confidence\":0.55}}}", request, "openrouter");

            DecisionAnswer answer = result.Find("q");
            Assert.IsFalse(answer.IsYes);
            Assert.AreEqual(0.55, answer.Confidence, Tolerance);
        }

        [Test]
        public void Response_Refusal_IsPerQuestion_AndTheRestIsStillAnswered()
        {
            DecisionRequest request = new("s",
                DecisionQuestion.Choice("team", "", "account", "frontend", "payments"),
                DecisionQuestion.YesNo("unsafe", "a"));

            DecisionResult result = SystemOneWire.ParseResponse(DecisionFixtures.OpenRouterRefusalResponse, request,
                "openrouter");

            Assert.IsTrue(result.Ok);
            Assert.AreEqual("payments", result.Find("team").ChosenOptionId);
            Assert.IsTrue(result.Find("unsafe").Refused);
            Assert.AreEqual(-1, result.Find("unsafe").ChosenIndex);
            Assert.IsFalse(result.Calibrated, "a refused answer has no calibrated distribution");
            Assert.AreEqual(40, result.Usage.CacheReadTokens);
        }

        [Test]
        public void Response_MissingAnswer_IsEmptyResponse()
        {
            DecisionRequest request = new("s", DecisionQuestion.YesNo("q", "a"), DecisionQuestion.YesNo("other", "b"));

            DecisionResult result = SystemOneWire.ParseResponse(
                "{\"answers\":{\"q\":{\"type\":\"noul\",\"noul\":0.3}}}", request, "systemone");

            Assert.AreEqual(LlmErrorCode.EmptyResponse, result.ErrorCode);
            Assert.AreEqual(DecisionErrorReasons.MissingAnswer, result.ErrorReason);
            StringAssert.Contains("other", result.Error);
        }

        [Test]
        public void Response_TypeMismatch_IsMalformed()
        {
            DecisionRequest request = new("s", DecisionQuestion.YesNo("q", "a"));

            DecisionResult result = SystemOneWire.ParseResponse(
                "{\"answers\":{\"q\":{\"type\":\"choice\",\"choice\":\"yes\"}}}", request, "systemone");

            Assert.AreEqual(DecisionErrorReasons.MalformedResponse, result.ErrorReason);
        }

        [TestCase("not json")]
        [TestCase("[1,2]")]
        [TestCase("{\"model\":\"m\"}")]
        [TestCase("{\"answers\":{\"q\":{\"type\":\"noul\"}}}")]
        public void Response_WithoutUsableAnswers_IsMalformed(string body)
        {
            DecisionRequest request = new("s", DecisionQuestion.YesNo("q", "a"));

            DecisionResult result = SystemOneWire.ParseResponse(body, request, "systemone");

            Assert.IsFalse(result.Ok);
            Assert.AreEqual(DecisionErrorReasons.MalformedResponse, result.ErrorReason);
        }

        [Test]
        public void Response_ChoiceWithoutProbabilities_IsOneHotAndUncalibrated()
        {
            DecisionRequest request = new("s", DecisionQuestion.Choice("q", "", "a", "b"));

            DecisionResult result = SystemOneWire.ParseResponse(
                "{\"answers\":{\"q\":{\"type\":\"choice\",\"choice\":\"b\"}}}", request, "systemone");

            DecisionAnswer answer = result.Find("q");
            Assert.AreEqual(new[] { 0d, 1d }, answer.Probabilities);
            Assert.IsFalse(answer.Calibrated, "a distribution the server never sent is not calibrated");
        }

        [Test]
        public void Response_ProbabilitiesAsAlignedArray_AreAccepted()
        {
            DecisionRequest request = new("s", DecisionQuestion.Choice("q", "", "a", "b"));

            DecisionResult result = SystemOneWire.ParseResponse(
                "{\"answers\":{\"q\":{\"type\":\"choice\",\"probabilities\":[0.2,0.6]}}}", request, "systemone");

            DecisionAnswer answer = result.Find("q");
            Assert.AreEqual("b", answer.ChosenOptionId);
            Assert.AreEqual(0.75, answer.ProbabilityOf("b"), Tolerance);
            Assert.AreEqual(0.75, answer.Confidence, Tolerance, "without confidence the top probability stands in");
            Assert.IsTrue(answer.Calibrated);
        }

        private static JObject Build(DecisionRequest request, string model, SystemOneDialect dialect)
        {
            JObject body = SystemOneWire.BuildRequestBody(request, model, dialect, out DecisionValidationError error);
            Assert.IsNull(error, error?.Message);
            return body;
        }

        private static DecisionRequest WithImages(params AiAttachment[] images)
        {
            return new DecisionRequest("s", new[] { DecisionQuestion.YesNo("q", "a") }, images);
        }

        private static string[] PropertyNames(JObject obj)
        {
            System.Collections.Generic.List<string> names = new();
            foreach (JProperty property in obj.Properties())
            {
                names.Add(property.Name);
            }

            return names.ToArray();
        }

        private static double Sum(DecisionAnswer answer)
        {
            double sum = 0d;
            foreach (double p in answer.Probabilities)
            {
                sum += p;
            }

            return sum;
        }
    }
}
