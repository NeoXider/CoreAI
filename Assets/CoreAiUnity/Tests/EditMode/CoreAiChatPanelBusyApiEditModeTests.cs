using CoreAI.Ai;
using CoreAI.Chat;
using NUnit.Framework;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.TestTools;

namespace CoreAI.Tests.EditMode
{
    /// <summary>
    /// Covers the v2.4.0 public busy contract on <see cref="CoreAiChatPanel"/>:
    /// <c>IsBusy</c>, <c>BusyStateChanged</c>, <c>CurrentTurnGeneration</c>, <c>ToolRoundStarted</c>
    /// and <c>ResetBusyStateWithoutCancellation</c>.
    /// External hosts (RedoSchool's submit-unlock gate) used to read the private flags via
    /// reflection - these tests pin the contract so we never silently regress it.
    /// </summary>
    [TestFixture]
    public sealed class CoreAiChatPanelBusyApiEditModeTests
    {
        [Test]
        public void IsBusy_ReflectsAnySetFlag()
        {
            using PanelCtx ctx = NewPanel();

            Assert.IsFalse(ctx.Panel.IsBusy, "fresh panel should be idle");

            foreach (string flag in new[] { "_isSending", "_isStreaming", "_isStopping", "_isClearing" })
            {
                SetFlag(ctx.Panel, flag, true);
                Assert.IsTrue(ctx.Panel.IsBusy, $"IsBusy must be true when {flag} is set");
                SetFlag(ctx.Panel, flag, false);
                Assert.IsFalse(ctx.Panel.IsBusy, $"IsBusy must be false after {flag} cleared");
            }
        }

        [Test]
        public void BusyStateChanged_FiresOnTransitionsOnly()
        {
            using PanelCtx ctx = NewPanel();
            List<bool> events = new();
            ctx.Panel.BusyStateChanged += b => events.Add(b);

            // Idle -> sending: one true.
            SetFlag(ctx.Panel, "_isSending", true);
            InvokeUpdateSendButton(ctx.Panel);
            // Add a second flag while already busy: must NOT fire again.
            SetFlag(ctx.Panel, "_isStreaming", true);
            InvokeUpdateSendButton(ctx.Panel);
            // Clear one flag, the other still set: still busy, no new event.
            SetFlag(ctx.Panel, "_isSending", false);
            InvokeUpdateSendButton(ctx.Panel);
            // Clear the last flag: one false.
            SetFlag(ctx.Panel, "_isStreaming", false);
            InvokeUpdateSendButton(ctx.Panel);

            CollectionAssert.AreEqual(new[] { true, false }, events);
        }

        [Test]
        public void ResetBusyStateWithoutCancellation_ClearsAllFlagsAndFiresChange()
        {
            using PanelCtx ctx = NewPanel();
            List<bool> events = new();
            ctx.Panel.BusyStateChanged += b => events.Add(b);

            SetFlag(ctx.Panel, "_isSending", true);
            SetFlag(ctx.Panel, "_isStreaming", true);
            SetFlag(ctx.Panel, "_isStopping", true);
            SetFlag(ctx.Panel, "_isClearing", true);
            InvokeUpdateSendButton(ctx.Panel); // publishes initial true
            Assert.IsTrue(ctx.Panel.IsBusy);

            ctx.Panel.ResetBusyStateWithoutCancellation();

            Assert.IsFalse(ctx.Panel.IsBusy);
            Assert.IsFalse(GetFlag(ctx.Panel, "_isSending"));
            Assert.IsFalse(GetFlag(ctx.Panel, "_isStreaming"));
            Assert.IsFalse(GetFlag(ctx.Panel, "_isStopping"));
            Assert.IsFalse(GetFlag(ctx.Panel, "_isClearing"));
            CollectionAssert.Contains(events, false);
        }

        [Test]
        public void CurrentTurnGeneration_StartsAtZero()
        {
            using PanelCtx ctx = NewPanel();
            Assert.AreEqual(0, ctx.Panel.CurrentTurnGeneration);
        }

        [Test]
        public async Task CurrentTurnGeneration_IncrementsOnEachTurn()
        {
            using PanelCtx ctx = NewPanel();
            ctx.Panel.ChatService = new CoreAiChatService(
                new FakeTurnOrchestrator("first", "second"),
                settings: new StubSettings { EnableStreaming = false });

            Assert.AreEqual(0, ctx.Panel.CurrentTurnGeneration);

            string? first = await ctx.Panel.SubmitMessageFromExternalAsync(
                "hello",
                new CoreAiChatExternalSubmitOptions { AppendUserMessageToChat = false });
            Assert.AreEqual(1, ctx.Panel.CurrentTurnGeneration);
            Assert.AreEqual("first", first);

            string? second = await ctx.Panel.SubmitMessageFromExternalAsync(
                "world",
                new CoreAiChatExternalSubmitOptions { AppendUserMessageToChat = false });
            Assert.AreEqual(2, ctx.Panel.CurrentTurnGeneration);
            Assert.AreEqual("second", second);
        }

        [Test]
        public async Task ToolRoundStarted_FiresWhenStreamingToolHintAppearsAfterVisibleText()
        {
            using PanelCtx ctx = NewPanel();

            ctx.Panel.SetRuntimeOptions(new CoreAiChatOptions
            {
                RoleId = "SmartChat",
                ShowToolCallsInChat = false
            });
            (int Iter, string Tool)? captured = null;
            ctx.Panel.ToolRoundStarted += (iter, toolName) => captured = (iter, toolName);

            typeof(CoreAiChatPanel)
                .GetMethod("TryRegisterToolCallChatDisplay", BindingFlags.Instance | BindingFlags.NonPublic)
                .Invoke(ctx.Panel, null);

            try
            {
                ctx.Panel.ChatService = new CoreAiChatService(
                    new FakeStreamingOrchestrator(
                        new[]
                        {
                            new LlmStreamChunk { Text = "A" },
                            new LlmStreamChunk
                            {
                                BufferedStreamingNoToolBinding = true,
                                BufferedStreamingUseToolProgressHint = true
                            },
                            new LlmStreamChunk { IsDone = true }
                        }),
                    settings: new StubSettings { EnableStreaming = true });

                // Any in-turn tool execution (even if chat bubbles are hidden) must be remembered for ToolRoundStarted.
                typeof(CoreAiChatPanel)
                    .GetMethod("OnToolExecutedChatDisplay", BindingFlags.Instance | BindingFlags.NonPublic)
                    .Invoke(ctx.Panel, new object[] { "SmartChat", "inventory_use", null, null });

                string? response = await ctx.Panel.SubmitMessageFromExternalAsync(
                    "hello",
                    new CoreAiChatExternalSubmitOptions { AppendUserMessageToChat = false });

                Assert.AreEqual(2, captured?.Iter);
                Assert.AreEqual("inventory_use", captured?.Tool);
                Assert.AreEqual("A", response);
            }
            finally
            {
                typeof(CoreAiChatPanel)
                    .GetMethod("TryUnregisterToolCallChatDisplay", BindingFlags.Instance | BindingFlags.NonPublic)
                    .Invoke(ctx.Panel, null);

                ctx.Panel.ClearRuntimeOptions();
            }
        }

        [Test]
        public async Task ToolRoundStarted_DoesNotFireForHintBeforeVisibleText()
        {
            using PanelCtx ctx = NewPanel();
            ctx.Panel.SetRuntimeOptions(new CoreAiChatOptions
            {
                RoleId = "SmartChat",
                ShowToolCallsInChat = false
            });
            (int Iter, string Tool)? captured = null;
            ctx.Panel.ToolRoundStarted += (iter, toolName) => captured = (iter, toolName);

            typeof(CoreAiChatPanel)
                .GetMethod("TryRegisterToolCallChatDisplay", BindingFlags.Instance | BindingFlags.NonPublic)
                .Invoke(ctx.Panel, null);

            try
            {
                ctx.Panel.ChatService = new CoreAiChatService(
                    new FakeTimedStreamingOrchestrator(
                        new[]
                        {
                            (new LlmStreamChunk
                            {
                                BufferedStreamingNoToolBinding = true,
                                BufferedStreamingUseToolProgressHint = true
                            }, 0),
                            (new LlmStreamChunk { Text = "A" }, 0),
                            (new LlmStreamChunk { IsDone = true }, 0)
                        }),
                    settings: new StubSettings { EnableStreaming = true });

                typeof(CoreAiChatPanel)
                    .GetMethod("OnToolExecutedChatDisplay", BindingFlags.Instance | BindingFlags.NonPublic)
                    .Invoke(ctx.Panel, new object[] { "SmartChat", "inventory_use", null, null });

                string? response = await ctx.Panel.SubmitMessageFromExternalAsync(
                    "hello",
                    new CoreAiChatExternalSubmitOptions { AppendUserMessageToChat = false });

                Assert.IsNull(captured);
                Assert.AreEqual("A", response);
            }
            finally
            {
                typeof(CoreAiChatPanel)
                    .GetMethod("TryUnregisterToolCallChatDisplay", BindingFlags.Instance | BindingFlags.NonPublic)
                    .Invoke(ctx.Panel, null);

                ctx.Panel.ClearRuntimeOptions();
            }
        }

        [Test]
        public async Task StreamGapWarn_LogsAfterLongPauseInStreaming()
        {
            using PanelCtx ctx = NewPanel();
            LogAssert.Expect(
                LogType.Warning,
                new System.Text.RegularExpressions.Regex(@".*\[CoreAiChatPanel\] Stream gap .* before chunk: .*"));

            ctx.Panel.ChatService = new CoreAiChatService(
                new FakeTimedStreamingOrchestrator(
                    new[]
                    {
                        (new LlmStreamChunk { Text = "A" }, 0),
                        (new LlmStreamChunk { Text = "B" }, 5500),
                        (new LlmStreamChunk { IsDone = true }, 0)
                    }),
                settings: new StubSettings { EnableStreaming = true });

            string? response = await ctx.Panel.SubmitMessageFromExternalAsync(
                "hello",
                new CoreAiChatExternalSubmitOptions { AppendUserMessageToChat = false });

            Assert.AreEqual("AB", response);
        }

        [Test]
        public void ToolRoundStarted_ManualInvocation_DeliversIterationAndToolName()
        {
            using PanelCtx ctx = NewPanel();
            (int Iter, string Tool)? captured = null;
            ctx.Panel.ToolRoundStarted += (iter, name) => captured = (iter, name);

            // Simulate the boundary path the streaming pump takes.
            typeof(CoreAiChatPanel)
                .GetMethod("RaiseToolRoundStarted", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(ctx.Panel, new object[] { 2, "advance_lesson" });

            Assert.IsTrue(captured.HasValue);
            Assert.AreEqual(2, captured!.Value.Iter);
            Assert.AreEqual("advance_lesson", captured.Value.Tool);
        }

        [Test]
        public void OnToolExecuted_TracksToolNameEvenWhenToolCallsHidden()
        {
            using PanelCtx ctx = NewPanel();
            ctx.Panel.SetRuntimeOptions(new CoreAiChatOptions
            {
                RoleId = "SmartChat",
                ShowToolCallsInChat = false
            });
            // Subscribe exactly as production code does: always subscribes now, even when tool bubbles are disabled.
            typeof(CoreAiChatPanel)
                .GetMethod("TryRegisterToolCallChatDisplay", BindingFlags.Instance | BindingFlags.NonPublic)
                .Invoke(ctx.Panel, null);

            try
            {
                typeof(CoreAiChatPanel)
                    .GetMethod("OnToolExecutedChatDisplay", BindingFlags.Instance | BindingFlags.NonPublic)
                    .Invoke(ctx.Panel, new object[] { "SmartChat", "inventory_use", null, null });

                Assert.AreEqual("inventory_use", GetStringField(ctx.Panel, "_lastToolNameInTurn"));
            }
            finally
            {
                typeof(CoreAiChatPanel)
                    .GetMethod("TryUnregisterToolCallChatDisplay", BindingFlags.Instance | BindingFlags.NonPublic)
                    .Invoke(ctx.Panel, null);

                ctx.Panel.ClearRuntimeOptions();
            }
        }

        [Test]
        public void OnToolExecuted_TracksToolNameForNullConfigDefaultRole()
        {
            using PanelCtx ctx = NewPanel();

            typeof(CoreAiChatPanel)
                .GetField("config", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(ctx.Panel, null);

            typeof(CoreAiChatPanel)
                .GetMethod("TryRegisterToolCallChatDisplay", BindingFlags.Instance | BindingFlags.NonPublic)
                .Invoke(ctx.Panel, null);

            try
            {
                typeof(CoreAiChatPanel)
                    .GetMethod("OnToolExecutedChatDisplay", BindingFlags.Instance | BindingFlags.NonPublic)
                    .Invoke(ctx.Panel, new object[] { "SmartChat", "advance_lesson", null, null });

                Assert.AreEqual("advance_lesson", GetStringField(ctx.Panel, "_lastToolNameInTurn"));
            }
            finally
            {
                typeof(CoreAiChatPanel)
                    .GetMethod("TryUnregisterToolCallChatDisplay", BindingFlags.Instance | BindingFlags.NonPublic)
                    .Invoke(ctx.Panel, null);
            }
        }

        [Test]
        public void OnToolExecuted_DoesNotTrackToolNameForRoleMismatch()
        {
            using PanelCtx ctx = NewPanel();
            ctx.Panel.SetRuntimeOptions(new CoreAiChatOptions
            {
                RoleId = "SmartChat",
                ShowToolCallsInChat = false
            });
            typeof(CoreAiChatPanel)
                .GetMethod("TryRegisterToolCallChatDisplay", BindingFlags.Instance | BindingFlags.NonPublic)
                .Invoke(ctx.Panel, null);

            try
            {
                typeof(CoreAiChatPanel)
                    .GetMethod("OnToolExecutedChatDisplay", BindingFlags.Instance | BindingFlags.NonPublic)
                    .Invoke(ctx.Panel, new object[] { "OtherRole", "inventory_use", null, null });

                Assert.IsNull(GetField<string>(ctx.Panel, "_lastToolNameInTurn"));
            }
            finally
            {
                typeof(CoreAiChatPanel)
                    .GetMethod("TryUnregisterToolCallChatDisplay", BindingFlags.Instance | BindingFlags.NonPublic)
                    .Invoke(ctx.Panel, null);

                ctx.Panel.ClearRuntimeOptions();
            }
        }

        [Test]
        public void ResetBusyStateWithoutCancellation_DoesNotCancelActiveRequest()
        {
            using PanelCtx ctx = NewPanel();
            CancellationTokenSource cts = new();
            SetField(ctx.Panel, "_activeRequestCts", cts);
            SetFlag(ctx.Panel, "_isSending", true);
            SetFlag(ctx.Panel, "_isStreaming", true);
            SetFlag(ctx.Panel, "_isStopping", true);
            SetFlag(ctx.Panel, "_isClearing", true);

            ctx.Panel.ResetBusyStateWithoutCancellation();

            Assert.IsFalse(cts.IsCancellationRequested);
            Assert.IsFalse(GetFlag(ctx.Panel, "_isSending"));
            Assert.IsFalse(GetFlag(ctx.Panel, "_isStreaming"));
            Assert.IsFalse(GetFlag(ctx.Panel, "_isStopping"));
            Assert.IsFalse(GetFlag(ctx.Panel, "_isClearing"));
        }

        [Test]
        public async Task ToolRoundStarted_DoesNotFireForVisibleHintWithoutToolProgressFlag()
        {
            using PanelCtx ctx = NewPanel();
            ctx.Panel.SetRuntimeOptions(new CoreAiChatOptions
            {
                RoleId = "SmartChat",
                ShowToolCallsInChat = false
            });
            (int Iter, string Tool)? captured = null;
            ctx.Panel.ToolRoundStarted += (iter, toolName) => captured = (iter, toolName);

            typeof(CoreAiChatPanel)
                .GetMethod("TryRegisterToolCallChatDisplay", BindingFlags.Instance | BindingFlags.NonPublic)
                .Invoke(ctx.Panel, null);

            try
            {
                ctx.Panel.ChatService = new CoreAiChatService(
                    new FakeStreamingOrchestrator(
                        new[]
                        {
                            new LlmStreamChunk { Text = "A" },
                            new LlmStreamChunk
                            {
                                BufferedStreamingNoToolBinding = true,
                                BufferedStreamingUseToolProgressHint = false
                            },
                            new LlmStreamChunk { Text = "B" },
                            new LlmStreamChunk { IsDone = true }
                        }),
                    settings: new StubSettings { EnableStreaming = true });

                // Streaming hint without `UseToolProgressHint` must not start a tool-round UI event.
                typeof(CoreAiChatPanel)
                    .GetMethod("OnToolExecutedChatDisplay", BindingFlags.Instance | BindingFlags.NonPublic)
                    .Invoke(ctx.Panel, new object[] { "SmartChat", "inventory_use", null, null });

                string? response = await ctx.Panel.SubmitMessageFromExternalAsync(
                    "hello",
                    new CoreAiChatExternalSubmitOptions { AppendUserMessageToChat = false });

                Assert.IsNull(captured);
                Assert.AreEqual("AB", response);
            }
            finally
            {
                typeof(CoreAiChatPanel)
                    .GetMethod("TryUnregisterToolCallChatDisplay", BindingFlags.Instance | BindingFlags.NonPublic)
                    .Invoke(ctx.Panel, null);

                ctx.Panel.ClearRuntimeOptions();
            }
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
            GameObject go = new("CoreAiChatPanel_BusyApi_Test");
            CoreAiChatPanel panel = go.AddComponent<CoreAiChatPanel>();
            return new PanelCtx(go, panel);
        }

        private static void SetFlag(CoreAiChatPanel panel, string fieldName, bool value)
        {
            typeof(CoreAiChatPanel)
                .GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(panel, value);
        }

        private static bool GetFlag(CoreAiChatPanel panel, string fieldName)
        {
            return GetField<bool>(panel, fieldName);
        }

        private static T GetField<T>(CoreAiChatPanel panel, string fieldName)
        {
            return (T)typeof(CoreAiChatPanel)
                .GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)
                .GetValue(panel);
        }

        private static string GetStringField(CoreAiChatPanel panel, string fieldName)
        {
            return GetField<string>(panel, fieldName);
        }

        private static void InvokeUpdateSendButton(CoreAiChatPanel panel)
        {
            // UpdateSendButtonVisualState() is the funnel that fires BusyStateChanged on transitions.
            // It's called automatically by every flag mutation in production code; the test reaches
            // it through reflection because we mutated flags directly via SetFlag.
            typeof(CoreAiChatPanel)
                .GetMethod("UpdateSendButtonVisualState", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(panel, null);
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

        private sealed class FakeTurnOrchestrator : IAiOrchestrationService
        {
            private readonly Queue<string> _responses;

            public FakeTurnOrchestrator(params string[] responses)
            {
                _responses = new Queue<string>(responses);
            }

            public Task<string> RunTaskAsync(AiTaskRequest request, CancellationToken ct = default)
            {
                return Task.FromResult(_responses.Count > 0 ? _responses.Dequeue() : string.Empty);
            }

            public async IAsyncEnumerable<LlmStreamChunk> RunStreamingAsync(
                AiTaskRequest request,
                [System.Runtime.CompilerServices.EnumeratorCancellation]
                CancellationToken ct = default)
            {
                while (_responses.Count > 0)
                {
                    ct.ThrowIfCancellationRequested();
                    yield return new LlmStreamChunk { Text = _responses.Dequeue() };
                    await Task.Yield();
                }

                yield return new LlmStreamChunk { IsDone = true };
            }

            public void CancelTasks(string scopeId)
            {
            }
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

        private sealed class FakeTimedStreamingOrchestrator : IAiOrchestrationService
        {
            private readonly Queue<(LlmStreamChunk Chunk, int DelayMs)> _chunks;

            public FakeTimedStreamingOrchestrator(IEnumerable<(LlmStreamChunk Chunk, int DelayMs)> chunks)
            {
                _chunks = new Queue<(LlmStreamChunk, int)>(chunks);
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
                    (LlmStreamChunk chunk, int delayMs) = _chunks.Dequeue();

                    if (delayMs > 0)
                    {
                        await Task.Delay(delayMs, ct);
                    }

                    ct.ThrowIfCancellationRequested();
                    yield return chunk;
                }
            }

            public void CancelTasks(string cancellationScope)
            {
            }
        }
    }
}