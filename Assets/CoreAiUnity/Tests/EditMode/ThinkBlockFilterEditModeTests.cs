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
    /// Как ПАНЕЛЬ снимает <c>&lt;think&gt;</c>-блоки: непотоковый путь —
    /// <see cref="CoreAiChatPanel.StripThinkBlocks"/>, которым чистится цельный ответ (непотоковый режим
    /// и симулированная реплика); потоковый путь — панель обязана в конце потока забрать у
    /// <see cref="ThinkBlockStreamFilter"/> удержанный хвост (<c>Flush</c>).
    /// <para>
    /// Раньше этот файл сторожил собственную копию regex'а и собственную копию потокового автомата
    /// («зеркало» <c>CoreAiChatPanel.FilterStreamChunk</c>): тесты были зелёными при ЛЮБОЙ реализации
    /// боевого кода. Теперь здесь только боевые методы; сам автомат фильтра покрыт в
    /// <c>ThinkBlockStreamFilterEditModeTests</c>.
    /// </para>
    /// </summary>
    public class ThinkBlockFilterEditModeTests
    {
        // ===================== Непотоковый путь: StripThinkBlocks =====================

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
        /// Ответ учителя Python по-русски: <c>&lt;</c> внутри — оператор сравнения, блок — по-русски.
        /// </summary>
        [Test]
        public void StripThinkBlocks_CyrillicAndComparisonOperator_KeepsVisibleText()
        {
            string input = "<think>сначала подумаю</think>Верно: 2 < 3 и 5 > 4.";
            Assert.AreEqual("Верно: 2 < 3 и 5 > 4.", CoreAiChatPanel.StripThinkBlocks(input));
        }

        // ===================== Потоковый путь: хвост в конце потока =====================

        /// <summary>
        /// Ответ учителя кончается на <c>&lt;</c> («оператор сравнения: &lt;»), и провайдер отдал этот символ
        /// отдельным последним чанком. Фильтр удерживает его на случай, что это начало тега, а следующего
        /// чанка не будет. Панель зва́ла у фильтра только обработку чанка и сброс, но не <c>Flush</c> —
        /// ученик читал ответ без последнего символа и в ленте, и в истории роли, и у обработчика ответа.
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
                "Полный ответ уходит в историю и обработчикам — последний символ обязан быть в нём.");

            List<Label> aiLabels = scroll.contentContainer.Query<Label>().Class("coreai-ai-message").ToList();
            Assert.AreEqual(1, aiLabels.Count);
            Assert.AreEqual("Оператор сравнения: <", aiLabels[0].text,
                "Удержанный хвост дописывается в тот же пузырь, а не теряется и не открывает новый.");
        }

        /// <summary>
        /// Тот же хвост, но настоящий тег: <c>&lt;</c> в конце одного чанка и <c>think&gt;…&lt;/think&gt;</c> в
        /// следующем. Хвост обязан ждать следующего чанка, а не уходить на экран — иначе рассуждение
        /// мелькнуло бы перед ответом.
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
            // WHY: EditMode не вызывает lifecycle-колбэки MonoBehaviour, поэтому «панель включена»
            // моделируется явно, не ослабляя боевой страж жизненного цикла.
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
