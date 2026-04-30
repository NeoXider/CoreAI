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
    ///   <item>Text-shaped tool-call JSON is executed and stripped from visible chunks.</item>
    ///   <item>The diagnostic <c>[ToolCall]</c> log line is emitted with status + args.</item>
    ///   <item>The final stream chunk carries <see cref="LlmStreamChunk.ExecutedToolCalls"/>.</item>
    /// </list>
    /// These complement the EditMode unit tests with a real Unity player frame so the
    /// async streaming machinery behaves the same way it does at runtime.
    /// </summary>
#if !COREAI_NO_LLM
    public sealed class ToolCallStreamingParityPlayModeTests
    {
        [UnityTest]
        public IEnumerator Streaming_TextShapedToolCall_ExecutesAndStripsFromChunks() => UniTask.ToCoroutine(async () =>
        {
            var settings = ScriptableObject.CreateInstance<CoreAISettingsAsset>();
            var spy = new SpyLogger();
            var memStore = new InMemoryMemoryStore();

            // Two-script: first stream emits text + JSON; second stream finishes with text.
            var inner = new ScriptedStreamClient(
                new[] { "Hi! ", "{\"name\":\"memory\",\"arguments\":{\"action\":\"append\",\"content\":\"play-mode-streaming\"}}" },
                new[] { "Saved." });

            var client = new MeaiLlmClient(inner, spy, settings, memStore);
            var request = new LlmCompletionRequest
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
                if (!string.IsNullOrEmpty(chunk.Text)) concatVisible += chunk.Text;
                if (chunk.IsDone) lastChunk = chunk;
            }

            // 1. JSON is gone from the visible stream the user sees.
            StringAssert.DoesNotContain("\"name\":\"memory\"", concatVisible,
                "Streaming chunks must never contain raw tool-call JSON.");
            StringAssert.Contains("Hi!", concatVisible);
            StringAssert.Contains("Saved.", concatVisible);

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

        [UnityTest]
        public IEnumerator NonStreaming_TextShapedToolCall_ExecutesAndStripsFromContent() => UniTask.ToCoroutine(async () =>
        {
            var settings = ScriptableObject.CreateInstance<CoreAISettingsAsset>();
            var spy = new SpyLogger();
            var memStore = new InMemoryMemoryStore();

            int iter = 0;
            var inner = new ScriptedNonStreamClient(_ =>
            {
                iter++;
                return iter == 1
                    ? MakeTextResponse("Hi! {\"name\":\"memory\",\"arguments\":{\"action\":\"append\",\"content\":\"play-mode-sync\"}}")
                    : MakeTextResponse("Saved.");
            });

            var client = new MeaiLlmClient(inner, spy, settings, memStore);
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

        // ------------ Helpers ------------

        private static MEAI.ChatResponse MakeTextResponse(string text)
            => new(new MEAI.ChatMessage(MEAI.ChatRole.Assistant, text));

        private sealed class ScriptedStreamClient : MEAI.IChatClient
        {
            private readonly Queue<string[]> _scripts;
            public ScriptedStreamClient(params string[][] scripts) { _scripts = new Queue<string[]>(scripts); }

            public Task<MEAI.ChatResponse> GetResponseAsync(IEnumerable<MEAI.ChatMessage> chat, MEAI.ChatOptions o = null, CancellationToken ct = default)
                => Task.FromResult(new MEAI.ChatResponse(new MEAI.ChatMessage(MEAI.ChatRole.Assistant, "")));

            public async IAsyncEnumerable<MEAI.ChatResponseUpdate> GetStreamingResponseAsync(
                IEnumerable<MEAI.ChatMessage> chat, MEAI.ChatOptions o = null,
                [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
            {
                if (_scripts.Count == 0) yield break;
                foreach (string s in _scripts.Dequeue())
                {
                    ct.ThrowIfCancellationRequested();
                    yield return new MEAI.ChatResponseUpdate(MEAI.ChatRole.Assistant, s);
                    await Task.Yield();
                }
            }

            public object GetService(Type t, object key = null) => null;
            public void Dispose() { }
        }

        private sealed class ScriptedNonStreamClient : MEAI.IChatClient
        {
            private readonly Func<int, MEAI.ChatResponse> _fn;
            private int _i;
            public ScriptedNonStreamClient(Func<int, MEAI.ChatResponse> fn) { _fn = fn; }

            public Task<MEAI.ChatResponse> GetResponseAsync(IEnumerable<MEAI.ChatMessage> chat, MEAI.ChatOptions o = null, CancellationToken ct = default)
            {
                _i++;
                return Task.FromResult(_fn(_i));
            }

            public IAsyncEnumerable<MEAI.ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<MEAI.ChatMessage> chat, MEAI.ChatOptions o = null, CancellationToken ct = default)
                => throw new NotSupportedException();

            public object GetService(Type t, object key = null) => null;
            public void Dispose() { }
        }

        private sealed class InMemoryMemoryStore : IAgentMemoryStore
        {
            public readonly Dictionary<string, AgentMemoryState> States = new();
            public bool TryLoad(string roleId, out AgentMemoryState state) => States.TryGetValue(roleId, out state);
            public void Save(string roleId, AgentMemoryState state) => States[roleId] = state;
            public void Clear(string roleId) => States.Remove(roleId);
            public void ClearChatHistory(string roleId) { }
            public void AppendChatMessage(string roleId, string role, string content, bool persistToDisk = true) { }
            public ChatMessage[] GetChatHistory(string roleId, int maxMessages = 0) => Array.Empty<ChatMessage>();
        }

        private sealed class SpyLogger : IGameLogger
        {
            public readonly List<string> AllLines = new();
            public void LogDebug(GameLogFeature f, string m, UnityEngine.Object c = null) => AllLines.Add(m);
            public void LogInfo(GameLogFeature f, string m, UnityEngine.Object c = null) => AllLines.Add(m);
            public void LogWarning(GameLogFeature f, string m, UnityEngine.Object c = null) => AllLines.Add(m);
            public void LogError(GameLogFeature f, string m, UnityEngine.Object c = null) => AllLines.Add(m);
        }
    }
#endif
}
