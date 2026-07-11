using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Ai;
using CoreAI.Chat;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UIElements;

namespace CoreAI.Tests.EditMode
{
    /// <summary>
    /// Pins the streaming-bubble behaviour across a tool round: assistant prose that streams
    /// before tools, then resumes after the tools, must be split into TWO distinct bubbles with
    /// the tool-call bubble(s) in between (claude/cursor behaviour) — the answer must not be
    /// appended back into the bubble opened before the tools (which would leave the tool calls
    /// below the answer, forcing the user to scroll up).
    /// </summary>
    [TestFixture]
    public sealed class CoreAiChatPanelToolRoundBubbleEditModeTests
    {
        [Test]
        public async Task Streaming_WithToolRound_SplitsProseIntoSeparateBubbles()
        {
            using PanelCtx ctx = NewPanel();
            ctx.Panel.SetRuntimeOptions(new CoreAiChatOptions
            {
                RoleId = "SmartChat",
                ShowToolCallsInChat = true
            });

            // Give the panel a real (panel-less) ScrollView so streaming bubbles are actually
            // created and inspectable. The scheduler is safe to enqueue against without a panel.
            ScrollView scroll = new();
            SetField(ctx.Panel, "MessageScroll", scroll);
            SetField(ctx.Panel, "ChatContainer", scroll);

            // Production mimics: when the tool round starts mid-stream, the executed tool
            // appends its own bubble. We do it synchronously off ToolRoundStarted so the
            // ordering is deterministic relative to the post-tool prose bubble.
            ctx.Panel.ToolRoundStarted += (_, _) =>
            {
                typeof(CoreAiChatPanel)
                    .GetMethod("AppendToolCallBubble", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(ctx.Panel, new object[] { "Tool call completed: my_tool" });
            };

            ctx.Panel.ChatService = new CoreAiChatService(
                new FakeStreamingOrchestrator(new[]
                {
                    new LlmStreamChunk { Text = "before tools" },
                    new LlmStreamChunk
                    {
                        BufferedStreamingNoToolBinding = true,
                        BufferedStreamingUseToolProgressHint = true
                    },
                    new LlmStreamChunk { Text = " after tools" },
                    new LlmStreamChunk { IsDone = true }
                }),
                settings: new StubSettings { EnableStreaming = true });

            string? response = await ctx.Panel.SubmitMessageFromExternalAsync(
                "hello",
                new CoreAiChatExternalSubmitOptions { AppendUserMessageToChat = false });

            // Full transcript is unchanged: both parts concatenated exactly once.
            Assert.AreEqual("before tools after tools", response);

            List<VisualElement> rows = scroll.contentContainer.Children().ToList();

            // AI-message labels live inside CoreAiChatMessageBubbleElement's content slot
            // (a grandchild of the row, not a direct child), so they must be found via a
            // recursive descendant query rather than r.Children() — matches the convention
            // in CoreAiChatMessageBubbleElementEditModeTests (bubble.Q<Label>(...)).
            List<Label> aiLabels = rows
                .SelectMany(r => r.Query<Label>().Class("coreai-ai-message").ToList())
                .ToList();
            List<Label> toolLabels = rows
                .SelectMany(r => r.Query<Label>().Class("coreai-tool-call-message").ToList())
                .ToList();

            Assert.AreEqual(2, aiLabels.Count,
                "A tool round must split the assistant prose into two bubbles.");
            Assert.AreEqual(1, toolLabels.Count, "One tool-call bubble expected.");

            int beforeIdx = rows.IndexOf(RowOf(rows, aiLabels[0]));
            int toolIdx = rows.IndexOf(RowOf(rows, toolLabels[0]));
            int afterIdx = rows.IndexOf(RowOf(rows, aiLabels[1]));

            Assert.Less(beforeIdx, toolIdx, "Pre-tool prose bubble must precede the tool bubble.");
            Assert.Less(toolIdx, afterIdx, "Tool bubble must precede the post-tool prose bubble.");
            Assert.AreEqual("before tools", aiLabels[0].text);
            Assert.AreEqual(" after tools", aiLabels[1].text);
        }

        [Test]
        public async Task Streaming_WithoutToolRound_KeepsSingleProseBubble()
        {
            using PanelCtx ctx = NewPanel();
            ctx.Panel.SetRuntimeOptions(new CoreAiChatOptions
            {
                RoleId = "SmartChat",
                ShowToolCallsInChat = true
            });

            ScrollView scroll = new();
            SetField(ctx.Panel, "MessageScroll", scroll);
            SetField(ctx.Panel, "ChatContainer", scroll);

            ctx.Panel.ChatService = new CoreAiChatService(
                new FakeStreamingOrchestrator(new[]
                {
                    new LlmStreamChunk { Text = "hello " },
                    new LlmStreamChunk { Text = "world" },
                    new LlmStreamChunk { IsDone = true }
                }),
                settings: new StubSettings { EnableStreaming = true });

            string? response = await ctx.Panel.SubmitMessageFromExternalAsync(
                "hi",
                new CoreAiChatExternalSubmitOptions { AppendUserMessageToChat = false });

            Assert.AreEqual("hello world", response);

            int aiBubbles = scroll.contentContainer
                .Query<Label>().Class("coreai-ai-message").ToList()
                .Count;

            Assert.AreEqual(1, aiBubbles,
                "Plain streaming with no tool round must stay in a single bubble.");
        }

        // ---------- helpers ----------

        /// <summary>
        /// AI-message labels are nested inside CoreAiChatMessageBubbleElement's content slot,
        /// so a label's direct .parent is not its row; walk up until we hit a known row.
        /// </summary>
        private static VisualElement RowOf(List<VisualElement> rows, VisualElement element)
        {
            for (VisualElement current = element; current != null; current = current.parent)
            {
                if (rows.Contains(current))
                {
                    return current;
                }
            }

            return null;
        }

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
            GameObject go = new("CoreAiChatPanel_ToolRoundBubble_Test");
            CoreAiChatPanel panel = go.AddComponent<CoreAiChatPanel>();
            return new PanelCtx(go, panel);
        }

        private static void SetField(CoreAiChatPanel panel, string fieldName, object? value)
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
            public float? LlmRequestTimeoutSecondsOverride { get; set; }
            public float LlmRequestTimeoutSeconds => LlmRequestTimeoutSecondsOverride ?? 15f;
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