using CoreAI.Ai;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode
{
    /// <summary>
    /// A learner read the engine's own bookkeeping inside the teacher's reply:
    /// <c>## Tool Results</c> with the raw <c>spawn_quiz</c> envelope under it. Two guarantees keep that
    /// out of a child's chat: tool results reach the model as machine records rather than as chat markdown
    /// it can imitate, and a word-for-word repetition of a result is removed from the visible stream.
    /// </summary>
    public sealed class ToolResultPromptProjectionEditModeTests
    {
        private const string SpawnQuizPayload =
            "{\"success\":true,\"tool\":\"spawn_quiz\",\"status\":\"card_shown_waiting_for_student\"}";

        [Test]
        public void ToolHistory_ReachesTheModelWithoutTheMarkdownRegisterItCanImitate()
        {
            string durable = "## Tool Results\n- spawn_quiz: ok " + SpawnQuizPayload;

            string projected = ToolResultPromptProjection.ForPrompt("tool", durable);

            StringAssert.DoesNotContain("## Tool Results", projected,
                "The heading is the shape the model copied back into its own reply.");
            StringAssert.DoesNotContain("- spawn_quiz", projected,
                "A markdown bullet is chat formatting; a tool result is a machine record.");
            StringAssert.Contains("tool_result name=spawn_quiz status=ok", projected);
            StringAssert.Contains(SpawnQuizPayload, projected,
                "The payload itself is what the model needs; only the register changes.");
        }

        [Test]
        public void FailedEntry_KeepsItsStatusAndReason()
        {
            string projected = ToolResultPromptProjection.ForPrompt(
                "tool",
                "## Tool Results\n- write_memory: FAILED permission denied");

            StringAssert.Contains("tool_result name=write_memory status=failed result=permission denied",
                projected);
        }

        [Test]
        public void IndentedToolPayloadThatLooksLikeAnEntry_IsLeftAlone()
        {
            // The Full policy indents raw tool output, and that output may itself be markdown or YAML.
            // Rewriting it would corrupt the tool's own text, which is the thing the model must read.
            string durable = "## Tool Results\n- read_file: ok\n  Detail:\n  - inner: ok not an entry";

            string projected = ToolResultPromptProjection.ForPrompt("tool", durable);

            StringAssert.Contains("tool_result name=read_file status=ok", projected);
            StringAssert.Contains("  - inner: ok not an entry", projected);
        }

        [Test]
        public void NonToolRoles_ArePassedThroughUnchanged()
        {
            const string assistant = "## Tool Results\nthis is the model's own text";

            Assert.AreEqual(assistant, ToolResultPromptProjection.ForPrompt("assistant", assistant));
            Assert.AreEqual(assistant, ToolResultPromptProjection.ForPrompt("user", assistant));
        }

        [Test]
        public void ToolMessageThisEngineDidNotWrite_IsNotMangled()
        {
            const string foreign = "host-written observation with no entries at all";

            Assert.AreEqual(foreign, ToolResultPromptProjection.ForPrompt("tool", foreign));
        }
    }

    /// <summary>
    /// The result-side twin of <c>LlmToolCallTextExtractor.StripForDisplay</c>. Every guarantee here is
    /// about IDENTITY: only the exact strings the engine handed the model are removed, and they are
    /// removed while the text is still streaming rather than after it is on screen.
    /// </summary>
    public sealed class ToolResultEchoStreamFilterEditModeTests
    {
        private const string Payload =
            "{\"success\":true,\"tool\":\"spawn_quiz\",\"status\":\"card_shown_waiting_for_student\"}";

        private static LlmToolCallTrace[] SpawnQuizTraces()
        {
            return new[] { new LlmToolCallTrace("spawn_quiz", true, 5d, "native", Payload) };
        }

        [Test]
        public void VerbatimRepetitionOfAToolResult_NeverReachesTheReader()
        {
            ToolResultEchoStreamFilter filter = new();
            filter.RegisterToolResults(SpawnQuizTraces());

            string visible = filter.ProcessChunk("Вот вопрос. " + Payload + " Отвечай на карточке.") +
                             filter.Flush();

            Assert.AreEqual("Вот вопрос.  Отвечай на карточке.", visible);
            Assert.IsTrue(filter.StrippedEcho);
        }

        [Test]
        public void RepetitionSplitAcrossChunks_IsStillRemoved()
        {
            // The whole point of a stream filter: a chunk boundary inside the payload must not let it
            // through, because by then it is already drawn in the chat.
            ToolResultEchoStreamFilter filter = new();
            filter.RegisterToolResults(SpawnQuizTraces());
            int split = Payload.Length / 2;

            string visible = filter.ProcessChunk("Готово. " + Payload.Substring(0, split));
            visible += filter.ProcessChunk(Payload.Substring(split) + " Дальше?");
            visible += filter.Flush();

            Assert.AreEqual("Готово.  Дальше?", visible);
            Assert.IsTrue(filter.StrippedEcho);
        }

        [Test]
        public void TextThatOnlyMentionsTheTool_IsUntouched()
        {
            // Guarding by keyword ("spawn_quiz", "Tool Results", "{") would eat the teacher's own words
            // and nobody could reproduce why a sentence disappeared.
            ToolResultEchoStreamFilter filter = new();
            filter.RegisterToolResults(SpawnQuizTraces());
            const string reply = "Я показал тебе карточку через spawn_quiz — успех, статус ожидания ответа.";

            string visible = filter.ProcessChunk(reply) + filter.Flush();

            Assert.AreEqual(reply, visible);
            Assert.IsFalse(filter.StrippedEcho);
        }

        [Test]
        public void ShortResults_AreNeverStripped()
        {
            // "ok"/"3"/"true" are ordinary words a teacher says; identity is not enough to call them ours.
            ToolResultEchoStreamFilter filter = new();
            filter.RegisterToolResults(new[] { new LlmToolCallTrace("add", true, 1d, "native", "4") });

            string visible = filter.ProcessChunk("Ответ 4, проверь на карточке.") + filter.Flush();

            Assert.AreEqual("Ответ 4, проверь на карточке.", visible);
            Assert.IsFalse(filter.HasNeedles);
        }

        [Test]
        public void HeldTailThatNeverCompletes_IsReleased_NotLost()
        {
            ToolResultEchoStreamFilter filter = new();
            filter.RegisterToolResults(SpawnQuizTraces());

            string visible = filter.ProcessChunk("Смотри: " + Payload.Substring(0, 30));
            visible += filter.Flush();

            Assert.AreEqual("Смотри: " + Payload.Substring(0, 30), visible,
                "Text held while a repetition might complete must come out, not disappear.");
            Assert.IsFalse(filter.StrippedEcho);
        }

        [Test]
        public void CrLfResult_RepeatedWithBareLf_IsStillRecognized()
        {
            string detail = "line one of the tool output\r\nline two of the tool output";
            ToolResultEchoStreamFilter filter = new();
            filter.RegisterToolResults(new[] { new LlmToolCallTrace("read", true, 1d, "native", detail) });

            string visible = filter.ProcessChunk("Итог: " + detail.Replace("\r\n", "\n")) + filter.Flush();

            Assert.AreEqual("Итог: ", visible);
        }

        [Test]
        public void OneShot_WithoutAMatch_ReturnsTheContentUnchanged()
        {
            const string content = "Обычный ответ учителя без повторов.";

            Assert.AreEqual(
                content,
                ToolResultEchoStreamFilter.StripEchoedToolResults(content, SpawnQuizTraces()));
        }

        [Test]
        public void OneShot_RemovesTheRepetitionFromTheFinishedTurn()
        {
            string content = "Вопрос показан.\n" + Payload;

            string stripped = ToolResultEchoStreamFilter.StripEchoedToolResults(content, SpawnQuizTraces());

            StringAssert.DoesNotContain("card_shown_waiting_for_student", stripped);
            StringAssert.Contains("Вопрос показан.", stripped);
        }
    }
}
