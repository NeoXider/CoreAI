using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.AgentMemory;
using CoreAI.Ai;
using CoreAI.Infrastructure.Llm;
using CoreAI.Infrastructure.Logging;
using CoreAI.Logging;
using Cysharp.Threading.Tasks;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using MEAI = Microsoft.Extensions.AI;

namespace CoreAI.Tests.PlayMode
{
    /// <summary>
    /// PlayMode parity tests for the unified tool-calling pipeline. Runs the full
    /// <see cref="MeaiLlmClient"/> streaming and non-streaming paths with a scripted
    /// inner client, asserting that:
    /// <list type="bullet">
    ///   <item>Text-shaped tool calls are executed; memory is updated; terminal chunk carries traces.</item>
    ///   <item>With live streaming and bound tools, raw tool JSON may appear in intermediate <see cref="LlmStreamChunk.Text"/> before extraction (same as production OpenRouter-style deltas); non-streaming content stays stripped.</item>
    ///   <item>The diagnostic <c>[ToolCall]</c> log line is emitted with status + args.</item>
    ///   <item>The final stream chunk carries <see cref="LlmStreamChunk.ExecutedToolCalls"/>.</item>
    /// </list>
    /// These complement the EditMode unit tests with a real Unity player frame so the
    /// async streaming machinery behaves the same way it does at runtime.
    /// </summary>
#if !COREAI_NO_LLM
    public sealed class ToolCallStreamingParityPlayModeTests
    {
        [TearDown]
        public void TearDown()
        {
            Log.Instance = NullLog.Instance;
        }

        [UnityTest]
        public IEnumerator Streaming_TextShapedToolCall_ExecutesAndStripsFromChunks()
        {
            return UniTask.ToCoroutine(async () =>
            {
                CoreAISettingsAsset settings = ScriptableObject.CreateInstance<CoreAISettingsAsset>();
                SpyLogger spy = new();
                Log.Instance = spy; // ToolExecutionPolicy writes [ToolCall] via Log.Instance
                InMemoryMemoryStore memStore = new();

                // Two-script: first stream emits text + JSON; second stream finishes with text.
                ScriptedStreamClient inner = new(
                    new[]
                    {
                        "Hi! ",
                        "{\"name\":\"memory\",\"arguments\":{\"action\":\"append\",\"content\":\"play-mode-streaming\"}}"
                    },
                    new[] { "Saved." });

                MeaiLlmClient client = new(inner, spy, settings, memStore);
                LlmCompletionRequest request = new()
                {
                    AgentRoleId = "Teacher",
                    SystemPrompt = "x",
                    UserPayload = "x",
                    TraceId = "play-stream-1",
                    Tools = new List<ILlmTool> { new MemoryLlmTool() }
                };

                string concatVisible = "";
                LlmStreamChunk lastChunk = null;
                await foreach (LlmStreamChunk chunk in client.CompleteStreamingAsync(request, CancellationToken.None))
                {
                    if (!string.IsNullOrEmpty(chunk.Text))
                    {
                        concatVisible += chunk.Text;
                    }

                    if (chunk.IsDone)
                    {
                        lastChunk = chunk;
                    }
                }

                // 1. Visible stream contains greeting and final text. With live streaming + bound tools,
                // raw tool-shaped JSON may pass through Text before TryExtractToolCallsFromText runs (parity with EditMode).
                StringAssert.Contains("Hi!", concatVisible);
                StringAssert.Contains("Saved.", concatVisible);
                Assert.GreaterOrEqual(inner.StreamCalls, 2, "Tool cycle should trigger a second stream iteration.");

                // 2. Memory tool actually executed (store has the new content).
                Assert.IsTrue(memStore.States.TryGetValue("Teacher", out AgentMemoryState state),
                    "Memory tool should have written the role state.");
                StringAssert.Contains("play-mode-streaming", state.Memory ?? "");

                // 3. Final chunk carries the tool-call diagnostic.
                Assert.IsNotNull(lastChunk, "Expected a terminal chunk.");
                Assert.IsNotNull(lastChunk!.ExecutedToolCalls);
                Assert.That(lastChunk.ExecutedToolCalls.Count, Is.GreaterThanOrEqualTo(1));
                Assert.That(lastChunk.ExecutedToolCalls[0].Name, Is.EqualTo("memory"));
                Assert.That(lastChunk.ExecutedToolCalls[0].Success, Is.True);

                // 4. Per-call diagnostic line exists in the log stream.
                string toolLine = spy.AllLines.FirstOrDefault(l => l.Contains("[ToolCall]"));
                Assert.IsNotNull(toolLine, $"Expected a [ToolCall] line. Lines:\n{string.Join("\n", spy.AllLines)}");
                StringAssert.Contains("traceId=play-stream-1", toolLine);
                StringAssert.Contains("tool=memory", toolLine);
                StringAssert.Contains("status=OK", toolLine);
            });
        }

        [UnityTest]
        public IEnumerator NonStreaming_TextShapedToolCall_ExecutesAndStripsFromContent()
        {
            return UniTask.ToCoroutine(async () =>
            {
                CoreAISettingsAsset settings = ScriptableObject.CreateInstance<CoreAISettingsAsset>();
                SpyLogger spy = new();
                Log.Instance = spy; // ToolExecutionPolicy writes [ToolCall] via Log.Instance
                InMemoryMemoryStore memStore = new();

                int iter = 0;
                ScriptedNonStreamClient inner = new(_ =>
                {
                    iter++;
                    return iter == 1
                        ? MakeTextResponse(
                            "Hi! {\"name\":\"memory\",\"arguments\":{\"action\":\"append\",\"content\":\"play-mode-sync\"}}")
                        : MakeTextResponse("Saved.");
                });

                MeaiLlmClient client = new(inner, spy, settings, memStore);
                LlmCompletionResult result = await client.CompleteAsync(new LlmCompletionRequest
                {
                    AgentRoleId = "Teacher",
                    SystemPrompt = "x",
                    UserPayload = "x",
                    TraceId = "play-sync-1",
                    Tools = new List<ILlmTool> { new MemoryLlmTool() }
                }, CancellationToken.None);

                Assert.IsTrue(result.Ok, "Non-streaming call should complete successfully.");
                StringAssert.DoesNotContain("\"name\":\"memory\"", result.Content ?? "",
                    "Non-streaming Content must not contain raw tool-call JSON.");
                StringAssert.Contains("Saved.", result.Content ?? "");

                Assert.IsTrue(memStore.States.TryGetValue("Teacher", out AgentMemoryState state));
                StringAssert.Contains("play-mode-sync", state.Memory ?? "");

                Assert.That(result.ExecutedToolCalls, Is.Not.Null);
                Assert.That(result.ExecutedToolCalls.Count, Is.GreaterThanOrEqualTo(1));
                Assert.That(result.ExecutedToolCalls.Any(t => t.Name == "memory" && t.Success), Is.True);

                string toolLine = spy.AllLines.FirstOrDefault(l => l.Contains("[ToolCall]"));
                Assert.IsNotNull(toolLine, "Expected a [ToolCall] line in non-streaming flow.");
                StringAssert.Contains("traceId=play-sync-1", toolLine);
            });
        }

        [UnityTest]
        public IEnumerator Streaming_FailedToolThenEmptyModelTurn_FeedsRetryInstructionAndRecovers()
        {
            return UniTask.ToCoroutine(async () =>
            {
                CoreAISettingsAsset settings = ScriptableObject.CreateInstance<CoreAISettingsAsset>();
                SpyLogger spy = new();
                Log.Instance = spy;

                CapturingScriptedStreamClient inner = new(
                    new[]
                    {
                        "{\"name\":\"manage_mods\",\"arguments\":{\"action\":\"load\",\"code\":\"broken\"}}"
                    },
                    new[] { " \n " },
                    new[]
                    {
                        "{\"name\":\"manage_mods\",\"arguments\":{\"action\":\"load\",\"code\":\"fixed\"}}"
                    },
                    new[] { "Recovered." });

                FlakyManageModsTool tool = new();
                MeaiLlmClient client = new(inner, spy, settings, new InMemoryMemoryStore());
                LlmCompletionRequest request = new()
                {
                    AgentRoleId = BuiltInAgentRoleIds.Programmer,
                    SystemPrompt = "x",
                    UserPayload = "make boss reward",
                    TraceId = "play-stream-retry-after-empty",
                    Tools = new List<ILlmTool> { tool }
                };

                string visible = "";
                LlmStreamChunk lastChunk = null;
                await foreach (LlmStreamChunk chunk in client.CompleteStreamingAsync(request, CancellationToken.None))
                {
                    if (!string.IsNullOrEmpty(chunk.Text))
                    {
                        visible += chunk.Text;
                    }

                    if (chunk.IsDone)
                    {
                        lastChunk = chunk;
                    }
                }

                Assert.AreEqual(2, tool.CallCount,
                    "The failed tool call should be retried after the empty model turn.");
                Assert.AreEqual(4, inner.StreamCalls, "Expected fail call, empty turn, corrected call, final prose.");
                Assert.IsTrue(inner.UserMessages.Any(m =>
                        m.Contains("attempt to index a function value") &&
                        m.Contains("retry with a corrected tool call")),
                    "The model should receive explicit retry feedback after returning whitespace.");
                StringAssert.Contains("Recovered.", visible);
                Assert.IsNotNull(lastChunk);
                Assert.That(lastChunk!.ExecutedToolCalls.Count, Is.GreaterThanOrEqualTo(2));
                Assert.IsTrue(lastChunk.ExecutedToolCalls.Any(t => t.Name == "manage_mods" && !t.Success));
                Assert.IsTrue(lastChunk.ExecutedToolCalls.Any(t => t.Name == "manage_mods" && t.Success));
            });
        }

        // ------------ Helpers ------------

        private static MEAI.ChatResponse MakeTextResponse(string text)
        {
            return new MEAI.ChatResponse(new MEAI.ChatMessage(MEAI.ChatRole.Assistant, text));
        }

        private sealed class ScriptedStreamClient : MEAI.IChatClient
        {
            private readonly Queue<string[]> _scripts;
            public int StreamCalls { get; private set; }

            public ScriptedStreamClient(params string[][] scripts)
            {
                _scripts = new Queue<string[]>(scripts);
            }

            public Task<MEAI.ChatResponse> GetResponseAsync(IEnumerable<MEAI.ChatMessage> chat,
                MEAI.ChatOptions o = null, CancellationToken ct = default)
            {
                return Task.FromResult(new MEAI.ChatResponse(new MEAI.ChatMessage(MEAI.ChatRole.Assistant, "")));
            }

            public async IAsyncEnumerable<MEAI.ChatResponseUpdate> GetStreamingResponseAsync(
                IEnumerable<MEAI.ChatMessage> chat, MEAI.ChatOptions o = null,
                [System.Runtime.CompilerServices.EnumeratorCancellation]
                CancellationToken ct = default)
            {
                StreamCalls++;
                if (_scripts.Count == 0)
                {
                    yield break;
                }

                foreach (string s in _scripts.Dequeue())
                {
                    ct.ThrowIfCancellationRequested();
                    yield return new MEAI.ChatResponseUpdate(MEAI.ChatRole.Assistant, s);
                    await Task.Yield();
                }
            }

            public object GetService(Type t, object key = null)
            {
                return null;
            }

            public void Dispose()
            {
            }
        }

        private sealed class ScriptedNonStreamClient : MEAI.IChatClient
        {
            private readonly Func<int, MEAI.ChatResponse> _fn;
            private int _i;

            public ScriptedNonStreamClient(Func<int, MEAI.ChatResponse> fn)
            {
                _fn = fn;
            }

            public Task<MEAI.ChatResponse> GetResponseAsync(IEnumerable<MEAI.ChatMessage> chat,
                MEAI.ChatOptions o = null, CancellationToken ct = default)
            {
                _i++;
                return Task.FromResult(_fn(_i));
            }

            public IAsyncEnumerable<MEAI.ChatResponseUpdate> GetStreamingResponseAsync(
                IEnumerable<MEAI.ChatMessage> chat, MEAI.ChatOptions o = null, CancellationToken ct = default)
            {
                throw new NotSupportedException();
            }

            public object GetService(Type t, object key = null)
            {
                return null;
            }

            public void Dispose()
            {
            }
        }

        private sealed class CapturingScriptedStreamClient : MEAI.IChatClient
        {
            private readonly Queue<string[]> _scripts;
            public readonly List<string> UserMessages = new();
            public int StreamCalls { get; private set; }

            public CapturingScriptedStreamClient(params string[][] scripts)
            {
                _scripts = new Queue<string[]>(scripts);
            }

            public Task<MEAI.ChatResponse> GetResponseAsync(IEnumerable<MEAI.ChatMessage> chat,
                MEAI.ChatOptions o = null, CancellationToken ct = default)
            {
                return Task.FromResult(new MEAI.ChatResponse(new MEAI.ChatMessage(MEAI.ChatRole.Assistant, "")));
            }

            public async IAsyncEnumerable<MEAI.ChatResponseUpdate> GetStreamingResponseAsync(
                IEnumerable<MEAI.ChatMessage> chat, MEAI.ChatOptions o = null,
                [System.Runtime.CompilerServices.EnumeratorCancellation]
                CancellationToken ct = default)
            {
                StreamCalls++;
                UserMessages.AddRange(chat.Where(m => m.Role == MEAI.ChatRole.User).Select(m => m.Text ?? ""));
                if (_scripts.Count == 0)
                {
                    yield break;
                }

                foreach (string s in _scripts.Dequeue())
                {
                    ct.ThrowIfCancellationRequested();
                    yield return new MEAI.ChatResponseUpdate(MEAI.ChatRole.Assistant, s);
                    await Task.Yield();
                }
            }

            public object GetService(Type t, object key = null)
            {
                return null;
            }

            public void Dispose()
            {
            }
        }

        private sealed class FlakyManageModsTool : ILlmTool, IAIFunctionLlmTool
        {
            public int CallCount { get; private set; }
            public string Name => "manage_mods";
            public string Description => "Test manage_mods tool.";
            public string ParametersSchema => "{}";
            public bool AllowDuplicates => false;

            public MEAI.AIFunction CreateAIFunction()
            {
                Func<string, string, string> fn = (action, code) =>
                {
                    CallCount++;
                    return code == "fixed"
                        ? "{\"success\":true,\"message\":\"loaded\"}"
                        : "{\"success\":false,\"message\":\"manage_mods 'load' failed: attempt to index a function value\"}";
                };
                return MEAI.AIFunctionFactory.Create(
                    fn,
                    Name,
                    Description);
            }
        }

        private sealed class InMemoryMemoryStore : IAgentMemoryStore
        {
            public readonly Dictionary<string, AgentMemoryState> States = new();

            public bool TryLoad(string roleId, out AgentMemoryState state)
            {
                return States.TryGetValue(roleId, out state);
            }

            public void Save(string roleId, AgentMemoryState state)
            {
                States[roleId] = state;
            }

            public void Clear(string roleId)
            {
                States.Remove(roleId);
            }

            public void ClearChatHistory(string roleId)
            {
            }

            public void AppendChatMessage(string roleId, string role, string content, bool persistToDisk = true)
            {
            }

            public ChatMessage[] GetChatHistory(string roleId, int maxMessages = 0)
            {
                return Array.Empty<ChatMessage>();
            }
        }

        private sealed class SpyLogger : IGameLogger, ILog
        {
            public readonly List<string> AllLines = new();

            // IGameLogger
            public void LogDebug(GameLogFeature f, string m, UnityEngine.Object c = null)
            {
                AllLines.Add(m);
            }

            public void LogInfo(GameLogFeature f, string m, UnityEngine.Object c = null)
            {
                AllLines.Add(m);
            }

            public void LogWarning(GameLogFeature f, string m, UnityEngine.Object c = null)
            {
                AllLines.Add(m);
            }

            public void LogError(GameLogFeature f, string m, UnityEngine.Object c = null)
            {
                AllLines.Add(m);
            }

            // ILog (used by ToolExecutionPolicy via Log.Instance)
            public void Debug(string message, string tag = null)
            {
                AllLines.Add(message);
            }

            public void Info(string message, string tag = null)
            {
                AllLines.Add(message);
            }

            public void Warn(string message, string tag = null)
            {
                AllLines.Add(message);
            }

            public void Error(string message, string tag = null)
            {
                AllLines.Add(message);
            }
        }
    }
#endif
}
