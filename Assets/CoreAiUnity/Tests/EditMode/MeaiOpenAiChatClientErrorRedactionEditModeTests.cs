#if !COREAI_NO_LLM
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
        public void AuthError_BodyIsRedacted_NotLogged()
        {
            string body = "{\"error\":{\"message\":\"Invalid API key sk-secret-123\"}}";
            string log401 = MeaiOpenAiChatClient.FormatHttpErrorForLog(401, body);
            string log403 = MeaiOpenAiChatClient.FormatHttpErrorForLog(403, body);

            StringAssert.DoesNotContain("sk-secret-123", log401);
            StringAssert.DoesNotContain("sk-secret-123", log403);
            StringAssert.Contains("redacted", log401);
            StringAssert.Contains("401", log401);
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
    }
}
#endif
