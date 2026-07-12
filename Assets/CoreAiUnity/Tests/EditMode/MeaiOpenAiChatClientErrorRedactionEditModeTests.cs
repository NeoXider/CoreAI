#if !COREAI_NO_LLM
using CoreAI.Ai;
using CoreAI.Infrastructure.Llm;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode
{
    /// <summary>
    /// Pins the log redaction of provider HTTP error bodies (<see cref="MeaiOpenAiChatClient.FormatHttpErrorForLog"/>):
    /// auth bodies are never logged and large bodies are truncated, while the full body still flows to
    /// retry/classification logic and the typed error unchanged.
    /// </summary>
    public sealed class MeaiOpenAiChatClientErrorRedactionEditModeTests
    {
        [Test]
        public void AuthError_401Body_IsBlanked_403Is_Not()
        {
            string body = "{\"error\":{\"message\":\"Invalid API key sk-secret-123\"}}";
            string log401 = MeaiOpenAiChatClient.FormatHttpErrorForLog(401, body);
            string log403 = MeaiOpenAiChatClient.FormatHttpErrorForLog(403, body);

            // 401 (invalid credentials) can echo the key — fully blanked.
            StringAssert.DoesNotContain("sk-secret-123", log401);
            StringAssert.Contains("redacted", log401);
            StringAssert.Contains("401", log401);
            // 403 (forbidden/permission/geo) is diagnostic, not a credential echo — kept (truncated), not blanked.
            StringAssert.DoesNotContain("redacted", log403);
            StringAssert.Contains("403", log403);
        }

        [Test]
        public void LargeBody_IsTruncated()
        {
            string body = new string('x', 4000);
            string log = MeaiOpenAiChatClient.FormatHttpErrorForLog(500, body);

            Assert.Less(log.Length, 1000, "large provider error body must be truncated in logs");
            StringAssert.Contains("chars]", log);
            StringAssert.Contains("500", log);
        }

        [Test]
        public void SmallNonAuthBody_IsLoggedVerbatim()
        {
            string log = MeaiOpenAiChatClient.FormatHttpErrorForLog(400, "bad request: missing model");
            StringAssert.Contains("bad request: missing model", log);
            StringAssert.Contains("400", log);
        }

        [Test]
        public void EmptyBody_LogsStatusOnly()
        {
            string log = MeaiOpenAiChatClient.FormatHttpErrorForLog(429, "");
            Assert.AreEqual("HTTP 429", log);
        }

        [Test]
        public void ThrownException_Message_DoesNotCarryRawNonJsonBody()
        {
            string body = "unexpected upstream failure " + new string('y', 3000);
            LlmClientException ex = MeaiOpenAiChatClient.BuildHttpExceptionForTests(500, body);

            Assert.Less(ex.Message.Length, 1000, "exception message must not carry the full untruncated body");
            StringAssert.DoesNotContain(new string('y', 3000), ex.Message);
            // WHY: the raw body is still available programmatically for retry-window parsing / diagnostics.
            StringAssert.Contains(body, ex.ProviderErrorBody);
        }

        [Test]
        public void ThrownException_Message_RedactsAuthBody()
        {
            string body = "sk-super-secret-key-leaked-in-401-body";
            LlmClientException ex = MeaiOpenAiChatClient.BuildHttpExceptionForTests(401, body);

            StringAssert.DoesNotContain("sk-super-secret-key-leaked-in-401-body", ex.Message);
        }

        [Test]
        public void ThrownException_Message_Redacts401AuthBody_EvenWhenJson_ButKeeps403()
        {
            // A 401 parsed provider error.message can echo the submitted key — it must NOT reach the exception
            // message (which is logged), even for a valid-JSON body.
            string body = "{\"error\":{\"message\":\"Incorrect API key provided: sk-secret-JSON-123.\"}}";
            LlmClientException ex401 = MeaiOpenAiChatClient.BuildHttpExceptionForTests(401, body);

            StringAssert.DoesNotContain("sk-secret-JSON-123", ex401.Message);
            // WHY: raw body still retained for diagnostics/retry-window parsing, just not in the message.
            StringAssert.Contains("sk-secret-JSON-123", ex401.ProviderErrorBody);

            // 403 is not a credential echo — its parsed provider message is preserved for diagnostics.
            string body403 = "{\"error\":{\"message\":\"Your region is not supported for this model.\"}}";
            LlmClientException ex403 = MeaiOpenAiChatClient.BuildHttpExceptionForTests(403, body403);
            StringAssert.Contains("region is not supported", ex403.Message);
        }

        [Test]
        public void ThrownException_Message_TruncatesHugeJsonProviderMessage()
        {
            string huge = new string('z', 3000);
            string body = "{\"error\":{\"message\":\"" + huge + "\"}}";
            LlmClientException ex = MeaiOpenAiChatClient.BuildHttpExceptionForTests(500, body);

            Assert.Less(ex.Message.Length, 1000, "a huge provider error.message must be truncated in the exception message");
            StringAssert.DoesNotContain(huge, ex.Message);
        }
    }
}
#endif
