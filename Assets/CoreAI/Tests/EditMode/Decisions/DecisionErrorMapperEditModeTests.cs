using CoreAI.Ai;
using CoreAI.Decisions;
using NUnit.Framework;

namespace CoreAI.Core.Tests.EditMode.Decisions
{
    /// <summary>Protocol errors onto <see cref="LlmErrorCode"/>, named reasons, and credential-free messages.</summary>
    [TestFixture]
    public sealed class DecisionErrorMapperEditModeTests
    {
        [TestCase(400, LlmErrorCode.InvalidRequest)]
        [TestCase(401, LlmErrorCode.AuthExpired)]
        [TestCase(402, LlmErrorCode.PaymentRequired)]
        [TestCase(403, LlmErrorCode.AuthExpired)]
        [TestCase(404, LlmErrorCode.PermanentProviderError)]
        [TestCase(408, LlmErrorCode.Timeout)]
        [TestCase(413, LlmErrorCode.ContextLengthExceeded)]
        [TestCase(422, LlmErrorCode.InvalidRequest)]
        [TestCase(429, LlmErrorCode.RateLimited)]
        [TestCase(500, LlmErrorCode.BackendUnavailable)]
        [TestCase(501, LlmErrorCode.PermanentProviderError)]
        [TestCase(502, LlmErrorCode.BackendUnavailable)]
        [TestCase(503, LlmErrorCode.BackendUnavailable)]
        [TestCase(524, LlmErrorCode.BackendUnavailable)]
        [TestCase(529, LlmErrorCode.BackendUnavailable)]
        [TestCase(0, LlmErrorCode.BackendUnavailable)]
        public void Status_MapsOntoTheTaxonomy(int status, LlmErrorCode expected)
        {
            Assert.AreEqual(expected, DecisionErrorMapper.MapStatus(status, ""));
        }

        [Test]
        public void ContextOverflowText_On400_IsContextLengthExceeded()
        {
            Assert.AreEqual(LlmErrorCode.ContextLengthExceeded, DecisionErrorMapper.MapStatus(400,
                "{\"error\":{\"type\":\"exceed_context_size_error\",\"message\":\"request exceeds the available context size\"}}"));
        }

        [Test]
        public void LlamaCpp501_IsNotADecisionModel()
        {
            DecisionResult result = DecisionErrorMapper.FromHttpFailure(501, DecisionFixtures.LlamaCppNotDecision501,
                "systemone");

            Assert.AreEqual(LlmErrorCode.PermanentProviderError, result.ErrorCode);
            Assert.AreEqual(DecisionErrorReasons.NotADecisionModel, result.ErrorReason);
            Assert.AreEqual(501, result.HttpStatus);
            StringAssert.Contains("not a decision model", result.Error);
        }

        [Test]
        public void TooManyOptionsText_On400_IsNamed()
        {
            Assert.AreEqual(DecisionErrorReasons.TooManyOptions,
                DecisionErrorMapper.ReasonFor(400, DecisionFixtures.TypeSafeTooManyOptions400));
        }

        [Test]
        public void UnsupportedTypeText_On422_IsNamed()
        {
            Assert.AreEqual(DecisionErrorReasons.UnsupportedQuestionType,
                DecisionErrorMapper.ReasonFor(422, "{\"detail\":[{\"msg\":\"Input tag 'box' is not supported\",\"type\":\"union_tag_invalid\"}]}"));
        }

        [Test]
        public void Plain400_IsAGenericHttpError()
        {
            Assert.AreEqual(DecisionErrorReasons.HttpError, DecisionErrorMapper.ReasonFor(400, "{\"detail\":\"bad state\"}"));
        }

        [Test]
        public void TypeSafeValidationDetail_BecomesAReadableMessage()
        {
            Assert.AreEqual("body.questions.action.criteria: Field required (missing)",
                DecisionErrorMapper.ExtractMessage(DecisionFixtures.TypeSafeValidation422));
        }

        [Test]
        public void OpenRouterError_MessageIsExtracted_And402IsPaymentRequired()
        {
            DecisionResult result = DecisionErrorMapper.FromHttpFailure(402, DecisionFixtures.OpenRouterCredits402,
                "openrouter");

            Assert.AreEqual(LlmErrorCode.PaymentRequired, result.ErrorCode);
            StringAssert.Contains("Insufficient credits", result.Error);
        }

        [Test]
        public void AuthErrorBody_IsNeverCopied()
        {
            DecisionResult result = DecisionErrorMapper.FromHttpFailure(401,
                "{\"error\":{\"message\":\"Invalid key sk-live-ABCDEF123456\"}}", "openrouter", "sk-live-ABCDEF123456");

            StringAssert.DoesNotContain("sk-live", result.Error);
            StringAssert.Contains("redacted", result.Error);
        }

        [Test]
        public void EchoedSecret_InAnyBody_IsRedacted()
        {
            DecisionResult result = DecisionErrorMapper.FromHttpFailure(400,
                "{\"detail\":\"header Bearer sk-test-9876543210 is malformed\"}", "systemone", "sk-test-9876543210");

            StringAssert.DoesNotContain("sk-test-9876543210", result.Error);
            StringAssert.Contains("[redacted]", result.Error);
        }

        [Test]
        public void LongBody_IsTruncated()
        {
            string message = DecisionErrorMapper.ExtractMessage(new string('x', 2000));

            Assert.LessOrEqual(message.Length, 303);
        }
    }
}
