#if COREAI_LLM
using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Ai;
using CoreAI.Decisions;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace CoreAI.Core.Tests.EditMode.Decisions
{
    /// <summary>
    /// The System One HTTP backend over a recording transport: what it posts, how it treats secrets, and how every
    /// failure becomes a typed result.
    /// </summary>
    [TestFixture]
    public sealed class SystemOneHttpDecisionClientEditModeTests
    {
        [Test]
        public async Task Decide_PostsTheRequestToSystemOne_AndParsesACalibratedResult()
        {
            RecordingTransport transport = new RecordingTransport().Enqueue(200, DecisionFixtures.TypeSafeResponse);
            SystemOneHttpDecisionClient client = new(
                DecisionFixtures.Endpoint("local", LlmDecisionProtocol.SystemOne), transport);
            DecisionRequest request = DecisionFixtures.NpcRequest();
            request.ConsumerId = "npc-brain";

            DecisionResult result = await client.DecideAsync(request);

            Assert.IsTrue(result.Ok, result.Error);
            Assert.IsTrue(result.Calibrated);
            Assert.AreEqual(SystemOneHttpDecisionClient.BackendName, result.Backend);
            Assert.AreEqual(1, transport.Requests.Count);
            Assert.AreEqual("http://127.0.0.1:8080/v1/systemone", transport.Requests[0].Url);
            Assert.IsFalse(transport.Requests[0].AcceptEventStream, "decisions never stream");
            JObject body = JObject.Parse(transport.Requests[0].JsonBody);
            Assert.AreEqual("d1-2B", (string)body["model"]);
            Assert.AreEqual(3, ((JObject)body["questions"]).Count, "all questions travel in one request");
            Assert.AreEqual("local", result.Usage.RoutingProfileId);
            Assert.AreEqual("npc-brain", result.Usage.RoleId);
        }

        [Test]
        public async Task SecretReference_IsResolvedPerCall_AndSentAsBearer()
        {
            RecordingTransport transport = new RecordingTransport().Enqueue(200, DecisionFixtures.TypeSafeResponse);
            SystemOneHttpDecisionClient client = new(
                DecisionFixtures.Endpoint("ts", LlmDecisionProtocol.SystemOne, "https://api.typesafe.ai/v1", "jev",
                    "TYPESAFE_KEY"),
                transport, new FixedSecretProvider("TYPESAFE_KEY", "ts-secret-123"));

            await client.DecideAsync(DecisionFixtures.NpcRequest());

            Assert.AreEqual("Bearer ts-secret-123", transport.Header(0, "Authorization"));
            StringAssert.DoesNotContain("ts-secret-123", transport.Requests[0].JsonBody);
        }

        [Test]
        public async Task NoSecretReference_SendsNoAuthorization()
        {
            RecordingTransport transport = new RecordingTransport().Enqueue(200, DecisionFixtures.TypeSafeResponse);
            SystemOneHttpDecisionClient client = new(
                DecisionFixtures.Endpoint("local", LlmDecisionProtocol.SystemOne), transport,
                new FixedSecretProvider("OTHER", "x-secret"));

            await client.DecideAsync(DecisionFixtures.NpcRequest());

            Assert.IsNull(transport.Header(0, "Authorization"));
        }

        [Test]
        public async Task InvalidRequest_IsNeverSent()
        {
            RecordingTransport transport = new();
            SystemOneHttpDecisionClient client = new(
                DecisionFixtures.Endpoint("local", LlmDecisionProtocol.SystemOne), transport);
            DecisionRequest request = new("s", DecisionQuestion.Choice("q", "pick",
                DecisionRequestValidationEditModeTests.Options(256)));

            DecisionResult result = await client.DecideAsync(request);

            Assert.AreEqual(LlmErrorCode.InvalidRequest, result.ErrorCode);
            Assert.AreEqual(DecisionErrorReasons.TooManyOptions, result.ErrorReason);
            Assert.AreEqual(0, transport.Requests.Count);
        }

        [Test]
        public async Task Http501_IsNotADecisionModel_AndTheSecretStaysOut()
        {
            RecordingTransport transport = new RecordingTransport().Enqueue(501, DecisionFixtures.LlamaCppNotDecision501);
            SystemOneHttpDecisionClient client = new(
                DecisionFixtures.Endpoint("local", LlmDecisionProtocol.SystemOne, secretReference: "K"), transport,
                new FixedSecretProvider("K", "local-secret-42"));

            DecisionResult result = await client.DecideAsync(DecisionFixtures.NpcRequest());

            Assert.AreEqual(LlmErrorCode.PermanentProviderError, result.ErrorCode);
            Assert.AreEqual(DecisionErrorReasons.NotADecisionModel, result.ErrorReason);
            StringAssert.DoesNotContain("local-secret-42", result.Error);
        }

        [Test]
        public async Task TransportException_IsBackendUnavailable()
        {
            RecordingTransport transport = new() { Throw = new HttpRequestException("connection refused") };
            SystemOneHttpDecisionClient client = new(
                DecisionFixtures.Endpoint("local", LlmDecisionProtocol.SystemOne), transport);

            DecisionResult result = await client.DecideAsync(DecisionFixtures.NpcRequest());

            Assert.AreEqual(LlmErrorCode.BackendUnavailable, result.ErrorCode);
            Assert.AreEqual(DecisionErrorReasons.TransportFailed, result.ErrorReason);
        }

        [Test]
        public async Task TransportTimeout_IsATimeout_NotACancellation()
        {
            RecordingTransport transport = new() { Throw = new TaskCanceledException("transport deadline") };
            SystemOneHttpDecisionClient client = new(
                DecisionFixtures.Endpoint("local", LlmDecisionProtocol.SystemOne), transport);

            DecisionResult result = await client.DecideAsync(DecisionFixtures.NpcRequest());

            Assert.AreEqual(LlmErrorCode.Timeout, result.ErrorCode);
        }

        [Test]
        public async Task CallerCancellation_Surfaces_AsOperationCanceled()
        {
            using CancellationTokenSource cancellation = new();
            cancellation.Cancel();
            RecordingTransport transport = new RecordingTransport().Enqueue(200, DecisionFixtures.TypeSafeResponse);
            SystemOneHttpDecisionClient client = new(
                DecisionFixtures.Endpoint("local", LlmDecisionProtocol.SystemOne), transport);

            await DecisionAsserts.AwaitThrows<OperationCanceledException>(() =>
                client.DecideAsync(DecisionFixtures.NpcRequest(), cancellation.Token));
            Assert.AreEqual(0, transport.Requests.Count);
        }

        [Test]
        public async Task CallerCancellation_DuringTheTransport_IsRethrown_NotMappedToATimeout()
        {
            using CancellationTokenSource cancellation = new();
            RecordingTransport transport = new()
            {
                OnPost = () =>
                {
                    cancellation.Cancel();
                    throw new OperationCanceledException(cancellation.Token);
                }
            };
            SystemOneHttpDecisionClient client = new(
                DecisionFixtures.Endpoint("local", LlmDecisionProtocol.SystemOne), transport);

            await DecisionAsserts.AwaitThrows<OperationCanceledException>(() =>
                client.DecideAsync(DecisionFixtures.NpcRequest(), cancellation.Token));
            Assert.AreEqual(1, transport.Requests.Count);
        }

        [Test]
        public async Task TypeSafeDialect_WithImages_SendsTheOptInHeader()
        {
            RecordingTransport transport = new RecordingTransport().Enqueue(200,
                "{\"answers\":{\"q\":{\"type\":\"noul\",\"noul\":0.9}}}");
            SystemOneHttpDecisionClient client = new(
                DecisionFixtures.Endpoint("ts", LlmDecisionProtocol.SystemOne, "https://api.typesafe.ai/v1"), transport);

            await client.DecideAsync(new DecisionRequest("s", new[] { DecisionQuestion.YesNo("q", "a") },
                new[] { CoreAI.Ai.AiAttachment.Image(new byte[] { 1 }, "image/png") }));

            Assert.AreEqual("true", transport.Header(0, SystemOneWire.ImageOptInHeader));
            Assert.IsNotNull(JObject.Parse(transport.Requests[0].JsonBody)["assets"]);
        }

        [Test]
        public async Task LlamaCppDialect_WithImages_SendsNoOptInHeader()
        {
            RecordingTransport transport = new RecordingTransport().Enqueue(200,
                "{\"answers\":{\"q\":{\"type\":\"noul\",\"noul\":0.9}}}");
            SystemOneHttpDecisionClient client = new(
                DecisionFixtures.Endpoint("local", LlmDecisionProtocol.SystemOne), transport);

            await client.DecideAsync(new DecisionRequest("s", new[] { DecisionQuestion.YesNo("q", "a") },
                new[] { CoreAI.Ai.AiAttachment.Image(new byte[] { 1 }, "image/png") }));

            Assert.IsNull(transport.Header(0, SystemOneWire.ImageOptInHeader));
            Assert.IsNotNull(JObject.Parse(transport.Requests[0].JsonBody)["files"]);
        }
    }
}
#endif
