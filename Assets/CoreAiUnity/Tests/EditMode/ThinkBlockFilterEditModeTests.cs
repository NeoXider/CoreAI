using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Ai;
using CoreAI.Authority;
using CoreAI.Chat;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.UIElements;

namespace CoreAI.Tests.EditMode
{
    /// <summary>
    /// How the PANEL strips <c>&lt;think&gt;</c> blocks: the non-streaming path is
    /// <see cref="CoreAiChatPanel.StripThinkBlocks"/>, which cleans a whole answer (non-streaming mode and a
    /// simulated reply); on the streaming path the panel must collect the held tail from
    /// <see cref="ThinkBlockStreamFilter"/> at the end of the stream (<c>Flush</c>).
    /// <para>
    /// This file used to guard its own copy of the regex and its own copy of the streaming state machine
    /// (a "mirror" of <c>CoreAiChatPanel.FilterStreamChunk</c>): the tests stayed green for ANY production
    /// implementation. Now only production methods are called here; the filter's own state machine is covered in
    /// <c>ThinkBlockStreamFilterEditModeTests</c>.
    /// </para>
    /// </summary>
    public class ThinkBlockFilterEditModeTests
    {
        // ===================== Non-streaming path: StripThinkBlocks =====================

        [Test]
        public void StripThinkBlocks_RemovesSimpleBlock()
        {
            string input = "<think>reasoning here</think>Visible text.";
            string result = CoreAiChatPanel.StripThinkBlocks(input);
            Assert.AreEqual("Visible text.", result);
        }

        [Test]
        public void StripThinkBlocks_RemovesMultilineBlock()
        {
            string input = "<think>\nLine 1\nLine 2\n</think>\nVisible.";
            string result = CoreAiChatPanel.StripThinkBlocks(input);
            Assert.AreEqual("Visible.", result);
        }

        [Test]
        public void StripThinkBlocks_RemovesMultipleBlocks()
        {
            string input = "<think>a</think>Hello <think>b</think>World";
            string result = CoreAiChatPanel.StripThinkBlocks(input);
            Assert.AreEqual("Hello World", result);
        }

        [Test]
        public void StripThinkBlocks_CaseInsensitive()
        {
            string input = "<THINK>hidden</THINK>Visible";
            string result = CoreAiChatPanel.StripThinkBlocks(input);
            Assert.AreEqual("Visible", result);
        }

        [Test]
        public void StripThinkBlocks_NoThinkBlock_Unchanged()
        {
            string input = "Just normal text.";
            string result = CoreAiChatPanel.StripThinkBlocks(input);
            Assert.AreEqual("Just normal text.", result);
        }

        [Test]
        public void StripThinkBlocks_EmptyInput()
        {
            Assert.AreEqual("", CoreAiChatPanel.StripThinkBlocks(""));
            Assert.IsNull(CoreAiChatPanel.StripThinkBlocks(null));
        }

        /// <summary>
        /// A Python teacher's answer in Russian: the <c>&lt;</c> inside it is a comparison operator, and the block is in Russian.
        /// </summary>
        [Test]
        public void StripThinkBlocks_CyrillicAndComparisonOperator_KeepsVisibleText()
        {
            string input = "<think>сначала подумаю</think>Верно: 2 < 3 и 5 > 4.";
            Assert.AreEqual("Верно: 2 < 3 и 5 > 4.", CoreAiChatPanel.StripThinkBlocks(input));
        }

        // ===================== Streaming path: the tail at the end of the stream =====================

        /// <summary>
        /// The teacher's answer ends with <c>&lt;</c> ("comparison operator: &lt;"), and the provider delivered that
        /// character as a separate last chunk. The filter holds it back in case it starts a tag, and the next chunk
        /// never comes. The panel asked the filter only for chunk processing and reset, never for <c>Flush</c>, so
        /// the learner read the answer without its last character, in the feed, in the role history and in the response handler.
        /// </summary>
        [Test]
        public async Task Streaming_AnswerEndsWithLoneLessThan_LastCharacterReachesBubbleAndResponse()
        {
            using PanelCtx ctx = NewPanel();
            ctx.Panel.SetRuntimeOptions(new CoreAiChatOptions { RoleId = "SmartChat" });

            ScrollView scroll = new();
            SetField(ctx.Panel, "MessageScroll", scroll);
            SetField(ctx.Panel, "ChatContainer", scroll);

            ctx.Panel.ChatService = new CoreAiChatService(
                new FakeStreamingOrchestrator(new[]
                {
                    new LlmStreamChunk { Text = "Оператор сравнения: " },
                    new LlmStreamChunk { Text = "<" },
                    new LlmStreamChunk { IsDone = true }
                }),
                settings: new StubSettings { EnableStreaming = true });

            string response = await ctx.Panel.SubmitMessageFromExternalAsync(
                "какой оператор?",
                new CoreAiChatExternalSubmitOptions { AppendUserMessageToChat = false });

            Assert.AreEqual("Оператор сравнения: <", response,
                "The full answer goes into the history and to the handlers, so the last character has to be inside it.");

            List<Label> aiLabels = scroll.contentContainer.Query<Label>().Class("coreai-ai-message").ToList();
            Assert.AreEqual(1, aiLabels.Count);
            Assert.AreEqual("Оператор сравнения: <", aiLabels[0].text,
                "The held tail is appended to the same bubble; it is neither lost nor given a new one.");
        }

        /// <summary>
        /// The same tail, but a real tag this time: <c>&lt;</c> at the end of one chunk and <c>think&gt;...&lt;/think&gt;</c>
        /// in the next. The tail has to wait for the next chunk instead of going to the screen, otherwise the
        /// reasoning would flash up before the answer.
        /// </summary>
        [Test]
        public async Task Streaming_LessThanThatBecomesThinkTag_HidesReasoningKeepsAnswer()
        {
            using PanelCtx ctx = NewPanel();
            ctx.Panel.SetRuntimeOptions(new CoreAiChatOptions { RoleId = "SmartChat" });

            ScrollView scroll = new();
            SetField(ctx.Panel, "MessageScroll", scroll);
            SetField(ctx.Panel, "ChatContainer", scroll);

            ctx.Panel.ChatService = new CoreAiChatService(
                new FakeStreamingOrchestrator(new[]
                {
                    new LlmStreamChunk { Text = "Привет!<" },
                    new LlmStreamChunk { Text = "think>думаю</think>Ответ: 2 < 3" },
                    new LlmStreamChunk { IsDone = true }
                }),
                settings: new StubSettings { EnableStreaming = true });

            string response = await ctx.Panel.SubmitMessageFromExternalAsync(
                "привет",
                new CoreAiChatExternalSubmitOptions { AppendUserMessageToChat = false });

            Assert.AreEqual("Привет!Ответ: 2 < 3", response);
            List<Label> aiLabels = scroll.contentContainer.Query<Label>().Class("coreai-ai-message").ToList();
            Assert.AreEqual(1, aiLabels.Count);
            Assert.AreEqual("Привет!Ответ: 2 < 3", aiLabels[0].text);
        }

        // ---------- helpers ----------

        private readonly struct PanelCtx : System.IDisposable
        {
            public readonly GameObject Go;
            public readonly CoreAiChatPanel Panel;

            public PanelCtx(GameObject go, CoreAiChatPanel panel)
            {
                Go = go;
                Panel = panel;
            }

            public void Dispose()
            {
                Object.DestroyImmediate(Go);
            }
        }

        private static PanelCtx NewPanel()
        {
            GameObject go = new("CoreAiChatPanel_ThinkBlockFilter_Test");
            CoreAiChatPanel panel = go.AddComponent<CoreAiChatPanel>();
            panel.SetActorIdentityProvider(new LocalActorIdentityProvider("think-block-filter-panel-test"));
            // WHY: EditMode does not fire MonoBehaviour lifecycle callbacks, so "the panel is enabled" is modelled
            // explicitly, without weakening the production lifecycle guard.
            SetField(panel, "_lifecycleActive", true);
            return new PanelCtx(go, panel);
        }

        private static void SetField(CoreAiChatPanel panel, string fieldName, object value)
        {
            typeof(CoreAiChatPanel)
                .GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(panel, value);
        }

        private sealed class StubSettings : ICoreAISettings
        {
            public string UniversalSystemPromptPrefix { get; set; } = string.Empty;
            public float Temperature { get; set; } = 0.3f;
            public int ContextWindowTokens => 8192;
            public int MaxLuaRepairRetries => 3;
            public int MaxToolCallRetries => 3;
            public bool AllowDuplicateToolCalls => false;
            public bool EnableHttpDebugLogging => false;
            public bool LogMeaiToolCallingSteps => false;
            public bool EnableMeaiDebugLogging => false;
            public float LlmRequestTimeoutSeconds => 15f;
            public int MaxLlmRequestRetries => 2;
            public bool LogTokenUsage => false;
            public bool LogLlmLatency => false;
            public bool LogLlmConnectionErrors => false;
            public bool LogToolCalls => false;
            public bool LogToolCallArguments => false;
            public bool LogToolCallResults => false;
            public bool EnableStreaming { get; set; } = true;
        }

        private sealed class FakeStreamingOrchestrator : IAiOrchestrationService
        {
            private readonly Queue<LlmStreamChunk> _chunks;

            public FakeStreamingOrchestrator(IEnumerable<LlmStreamChunk> chunks)
            {
                _chunks = new Queue<LlmStreamChunk>(chunks);
            }

            public Task<string> RunTaskAsync(AiTaskRequest request, CancellationToken ct = default)
            {
                return Task.FromResult(string.Empty);
            }

            public async IAsyncEnumerable<LlmStreamChunk> RunStreamingAsync(
                AiTaskRequest request,
                [System.Runtime.CompilerServices.EnumeratorCancellation]
                CancellationToken ct = default)
            {
                while (_chunks.Count > 0)
                {
                    ct.ThrowIfCancellationRequested();
                    yield return _chunks.Dequeue();
                    await Task.Yield();
                }
            }

            public void CancelTasks(string cancellationScope)
            {
            }
        }
    }
}
