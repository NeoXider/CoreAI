#if !COREAI_NO_LLM
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Ai;
using CoreAI.Audit;
using CoreAI.Authority;
using CoreAI.Config;
using CoreAI.Crafting;
using CoreAI.Infrastructure.Llm;
using CoreAI.Messaging;
using CoreAI.Session;
using Microsoft.Extensions.AI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

// WHY: Microsoft.Extensions.AI also exports a ChatMessage type, so the agent-memory contract implemented
// by the stubs below must be pinned to the CoreAI one.
using ChatMessage = CoreAI.Ai.ChatMessage;

namespace CoreAI.Core.Tests.EditMode
{
    /// <summary>
    /// Regression tests for audited correctness findings in <c>CoreAI.Core</c>: a library timeout reported
    /// as user cancellation, a silently successful empty stream, role memory destroyed by a transient read
    /// failure, a tool timeout swallowed by <c>call_skill_tool</c>, <c>game_config</c> claiming success
    /// after a failed save, a forged audit-chain restart passing verification, a live trace list handed to
    /// concurrent readers, settings lost through <see cref="CoreAISettingsOptions"/>, and a case-sensitive
    /// JSON schema range gate.
    /// </summary>
    public sealed class CoreAuditFindingsEditModeTests
    {
        // --- Finding 2: Timeout(Logging(inner)) must surface a library timeout as Timeout ---

        [Test]
        public async Task TimeoutOverLoggingDecorator_LibraryTimeout_SurfacesTimeoutNotCancelled()
        {
            StallingStreamingClient inner = new();
            LoggingLlmClientDecorator logging = new(inner, null);
            TimeoutLlmClientDecorator sut = new(logging, () => 0.05f);

            List<LlmStreamChunk> chunks = new();
            await foreach (LlmStreamChunk chunk in sut.CompleteStreamingAsync(new LlmCompletionRequest()))
            {
                chunks.Add(chunk);
            }

            Assert.AreEqual(1, chunks.Count, "Expected exactly one terminal chunk.");
            Assert.IsTrue(chunks[0].IsDone);
            Assert.AreEqual(
                LlmErrorCode.Timeout, chunks[0].ErrorCode,
                "The logging decorator swallowed the OperationCanceledException and emitted ErrorCode.None, " +
                "so the timeout decorator could not recognise its own linked-token timeout and the user " +
                "saw 'cancelled' as if they had pressed Stop.");
        }

        [Test]
        public async Task LoggingDecorator_InnerStreamFault_TagsTheTerminalChunkWithATypedCode()
        {
            ThrowingStreamingClient inner = new(
                new LlmClientException("nope", LlmErrorCode.BackendUnavailable));
            LoggingLlmClientDecorator sut = new(inner, null);

            List<LlmStreamChunk> chunks = new();
            await foreach (LlmStreamChunk chunk in sut.CompleteStreamingAsync(new LlmCompletionRequest()))
            {
                chunks.Add(chunk);
            }

            Assert.AreEqual(1, chunks.Count);
            Assert.AreEqual(LlmErrorCode.BackendUnavailable, chunks[0].ErrorCode);
        }

        [Test]
        public async Task TimeoutOverLoggingDecorator_CallerStop_StaysCancelled()
        {
            StallingStreamingClient inner = new();
            LoggingLlmClientDecorator logging = new(inner, null);
            TimeoutLlmClientDecorator sut = new(logging, () => 30f);

            using CancellationTokenSource caller = new();
            caller.CancelAfter(TimeSpan.FromMilliseconds(50));

            List<LlmStreamChunk> chunks = new();
            await foreach (LlmStreamChunk chunk in sut.CompleteStreamingAsync(
                               new LlmCompletionRequest(), caller.Token))
            {
                chunks.Add(chunk);
            }

            Assert.AreEqual(1, chunks.Count);
            Assert.AreEqual(
                LlmErrorCode.Cancelled, chunks[0].ErrorCode,
                "A genuine caller stop must never be relabelled as a timeout.");
        }

        // --- Finding 3: an empty streamed turn is a failure, not a silent success ---

        [Test]
        public async Task Orchestrator_StreamWithNoContentAndNoError_ReportsEmptyResponse()
        {
            RecordingTraceSink traceSink = new();
            RecordingMetrics metrics = new();
            AiOrchestrator sut = BuildOrchestrator(new SilentStreamingClient(), traceSink, metrics);

            List<LlmStreamChunk> chunks = new();
            await foreach (LlmStreamChunk chunk in sut.RunStreamingAsync(new AiTaskRequest { Hint = "hi" }))
            {
                chunks.Add(chunk);
            }

            Assert.AreEqual(1, chunks.Count, "Expected one terminal chunk describing the empty answer.");
            Assert.IsTrue(chunks[0].IsDone);
            Assert.AreEqual(LlmErrorCode.EmptyResponse, chunks[0].ErrorCode);
            Assert.AreEqual(
                1, traceSink.Traces.Count,
                "An empty turn used to vanish from the trace sink entirely.");
            Assert.AreEqual("empty response", traceSink.Traces[0].Error);
            Assert.AreEqual(
                1, metrics.Completions.Count);
            Assert.IsFalse(
                metrics.Completions[0],
                "An empty answer was counted as a successful completion in the metrics.");
        }

        [Test]
        public async Task Orchestrator_StreamWithContent_StillCountsAsSuccess()
        {
            RecordingTraceSink traceSink = new();
            RecordingMetrics metrics = new();
            AiOrchestrator sut = BuildOrchestrator(new TextStreamingClient("hello"), traceSink, metrics);

            List<LlmStreamChunk> chunks = new();
            await foreach (LlmStreamChunk chunk in sut.RunStreamingAsync(new AiTaskRequest { Hint = "hi" }))
            {
                chunks.Add(chunk);
            }

            Assert.IsTrue(chunks.Exists(c => c.Text == "hello"));
            Assert.AreEqual(1, metrics.Completions.Count);
            Assert.IsTrue(metrics.Completions[0]);
        }

        // --- Finding 4: a transient read failure must not wipe role memory ---

        [Test]
        public void MutateAsync_LoadFailure_AbortsWithoutSaving()
        {
            UnreadableMemoryStore store = new();

            AgentMemoryLoadException thrown = Assert.ThrowsAsync<AgentMemoryLoadException>(
                async () => await store.MutateAsync("creator", state =>
                {
                    state.Memory = "brand new";
                    return true;
                }));

            Assert.AreEqual("creator", thrown.RoleId);
            Assert.AreEqual(
                0, store.SaveCount,
                "A failed load used to start from an empty state and save it unconditionally, " +
                "destroying the memory document and its whole version history.");
        }

        [Test]
        public async Task MutateAsync_MissingDocument_StillStartsFromAnEmptyState()
        {
            UnreadableMemoryStore store = new() { Status = AgentMemoryLoadStatus.NotFound };

            bool ran = await store.MutateAsync("creator", state =>
            {
                state.Memory = "first fact";
                return true;
            });

            Assert.IsTrue(ran);
            Assert.AreEqual(1, store.SaveCount, "A first write must not be blocked by the load guard.");
            Assert.AreEqual("first fact", store.Saved?.Memory);
        }

        [Test]
        public void ScopedDecorator_ForwardsLoadDiagnostics()
        {
            UnreadableMemoryStore inner = new();
            ScopedAgentMemoryStoreDecorator sut = new(inner, null);

            AgentMemoryLoadStatus status = sut.TryLoadDetailed("creator", out AgentMemoryState state);

            Assert.AreEqual(AgentMemoryLoadStatus.Failed, status);
            Assert.IsNull(state);
        }

        // --- Finding 5: call_skill_tool must not swallow cancellation ---

        [Test]
        public void CallSkillTool_ToolCancellation_PropagatesInsteadOfBecomingAFailedResult()
        {
            SkillSet skill = new("timing", "d", "i", new CancellingSkillTool());
            ILlmTool tool = CallSkillToolLlmTool.Create(new List<SkillSet> { skill });
            AIFunction function = ((IAIFunctionLlmTool)tool).CreateAIFunction();

            AIFunctionArguments args = new()
            {
                ["tool_name"] = "slow_tool",
                ["arguments_json"] = "{}"
            };

            Assert.CatchAsync<OperationCanceledException>(
                async () => await function.InvokeAsync(args),
                "A per-tool timeout was collapsed into a plain {\"success\":false}, so ToolExecutionPolicy " +
                "never ran its timeout path and grew the consecutive-error counter instead.");
        }

        // --- Finding 6: game_config update must not claim success on a failed save ---

        [Test]
        public async Task GameConfigUpdate_StoreRejectsTheWrite_ReportsFailure()
        {
            GameConfigPolicy policy = new();
            policy.SetKnownKeys(new[] { "difficulty" });
            policy.GrantFullAccess("creator");

            GameConfigTool sut = new(new NullGameConfigStore(), policy, "creator", QuietSettings());

            string json = await sut.ExecuteAsync("update", "{\"difficulty\":3}");
            JObject result = JObject.Parse(json);

            Assert.IsFalse(
                (bool)result["Success"],
                "The result of TrySave was discarded, so the null fallback store reported 'Config updated' " +
                "while nothing was persisted.");
            Assert.IsNotEmpty((string)result["Error"]);
        }

        [Test]
        public async Task GameConfigUpdate_PolicyRejectsThePayload_DoesNotReachTheStore()
        {
            RecordingConfigStore store = new();
            RejectingGameConfigPolicy policy = new();
            policy.SetKnownKeys(new[] { "difficulty" });
            policy.GrantFullAccess("creator");

            GameConfigTool sut = new(store, policy, "creator", QuietSettings());

            string json = await sut.ExecuteAsync("update", "{\"difficulty\":9999}");
            JObject result = JObject.Parse(json);

            Assert.IsFalse((bool)result["Success"]);
            Assert.AreEqual("difficulty out of range", (string)result["Error"]);
            Assert.AreEqual(
                0, store.SaveCount,
                "Rejected JSON was still written to the store, bypassing policy validation.");
        }

        [Test]
        public async Task GameConfigUpdate_StoreAcceptsTheWrite_ReportsSuccess()
        {
            RecordingConfigStore store = new();
            GameConfigPolicy policy = new();
            policy.SetKnownKeys(new[] { "difficulty" });
            policy.GrantFullAccess("creator");

            GameConfigTool sut = new(store, policy, "creator", QuietSettings());

            string json = await sut.ExecuteAsync("update", "{\"difficulty\":3}");
            JObject result = JObject.Parse(json);

            Assert.IsTrue((bool)result["Success"], (string)result["Error"]);
            Assert.AreEqual(1, store.SaveCount);
        }

        // --- Finding 7: a mid-file ChainReset must break verification ---

        [Test]
        public void AuditVerify_ForgedMidFileChainReset_IsRejected()
        {
            string path = Path.Combine(Path.GetTempPath(), $"coreai_audit_{Guid.NewGuid():N}.jsonl");
            try
            {
                List<string> lines = new();
                string prev = "";
                for (int i = 0; i < 2; i++)
                {
                    prev = AppendChainedLine(lines, prev, AuditEntryKind.ToolCall, i + 1, "creator");
                }

                // The forgery: truncate after any entry, then restart the chain from genesis with a
                // hand-made ChainReset line plus an arbitrary tail.
                string resetPrev = AppendChainedLine(lines, "", AuditEntryKind.ChainReset, 3, "system");
                AppendChainedLine(lines, resetPrev, AuditEntryKind.ToolCall, 4, "attacker");

                File.WriteAllLines(path, lines);

                AuditVerifyResult result = AuditLogVerifier.Verify(path);

                Assert.IsFalse(
                    result.Ok,
                    "A ChainReset re-seeded prevHash from the line's own self-declared Kind, so anyone could " +
                    "truncate the journal and append a forged tail behind a fake restart marker.");
                Assert.AreEqual(1, result.ChainResetCount);
                StringAssert.Contains("ChainReset", result.Error);
            }
            finally
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
        }

        [Test]
        public void AuditVerify_ChainResetOnTheFirstLine_StillVerifies()
        {
            string path = Path.Combine(Path.GetTempPath(), $"coreai_audit_{Guid.NewGuid():N}.jsonl");
            try
            {
                List<string> lines = new();
                string prev = AppendChainedLine(lines, "", AuditEntryKind.ChainReset, 1, "system");
                AppendChainedLine(lines, prev, AuditEntryKind.ToolCall, 2, "creator");

                File.WriteAllLines(path, lines);

                AuditVerifyResult result = AuditLogVerifier.Verify(path);

                Assert.IsTrue(result.Ok, result.Error);
                Assert.AreEqual(2, result.LineCount);
            }
            finally
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
        }

        // --- Finding 8: ExecutedTraces must be a snapshot ---

        [Test]
        public void ExecutedTraces_ConcurrentAppend_DoesNotInvalidateTheSnapshot()
        {
            ToolExecutionPolicy policy = new(null, new CoreAISettingsOptions(), null, true, "creator");
            for (int i = 0; i < 64; i++)
            {
                policy.RecordSyntheticTrace($"tool{i}", true, 1, "native");
            }

            // WHY: the append budget is BOUNDED. An unbounded writer loop grows the trace list without
            // limit while the reader copies it every round, which exhausts memory and takes the editor
            // down with a native crash inside Array.Copy — a harness failure that says nothing about the
            // property under test. A few thousand interleaved appends already lose the race reliably if
            // the snapshot is not taken under the append lock.
            const int Appends = 4000;
            using CancellationTokenSource stop = new();
            Task writer = Task.Run(() =>
            {
                for (int n = 0; n < Appends && !stop.IsCancellationRequested; n++)
                {
                    policy.RecordSyntheticTrace($"late{n}", true, 1, "native");
                }
            });

            try
            {
                for (int round = 0; round < 400; round++)
                {
                    IReadOnlyList<LlmToolCallTrace> snapshot = policy.ExecutedTraces;
                    int seen = 0;
                    foreach (LlmToolCallTrace _ in snapshot)
                    {
                        seen++;
                    }

                    Assert.AreEqual(
                        snapshot.Count, seen,
                        "ExecutedTraces returned the live list, so a worker appending during enumeration " +
                        "threw 'Collection was modified' far from the cause.");
                }
            }
            finally
            {
                stop.Cancel();
                writer.Wait(TimeSpan.FromSeconds(5));
            }
        }

        // --- Finding 9: every setting must survive a CoreAISettingsOptions round-trip ---

        [Test]
        public void CoreAISettingsOptions_From_CopiesEveryInterfaceProperty()
        {
            CoreAISettingsOptions source = new();
            MutateEveryProperty(source);

            CoreAISettingsOptions copy = CoreAISettingsOptions.From(source);

            List<string> lost = new();
            foreach (PropertyInfo prop in typeof(CoreAISettingsOptions)
                         .GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (!prop.CanRead || !prop.CanWrite)
                {
                    continue;
                }

                object expected = prop.GetValue(source);
                object actual = prop.GetValue(copy);
                if (!Equals(expected, actual))
                {
                    lost.Add($"{prop.Name}: expected '{expected}' but From() produced '{actual}'");
                }
            }

            Assert.IsEmpty(lost, string.Join(Environment.NewLine, lost));
        }

        [Test]
        public void CoreAISettingsOptions_DeclaresEveryInterfaceProperty()
        {
            List<string> missing = new();
            foreach (PropertyInfo contract in typeof(ICoreAISettings)
                         .GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                PropertyInfo own = typeof(CoreAISettingsOptions).GetProperty(
                    contract.Name,
                    BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);
                if (own == null)
                {
                    missing.Add(contract.Name);
                }
            }

            Assert.IsEmpty(
                missing,
                "These properties fall through to the DEFAULT INTERFACE implementation, so From() cannot " +
                "copy them and a host's configured value is silently replaced: " + string.Join(", ", missing));
        }

        [Test]
        public void CoreAISettingsOptions_Defaults_MatchTheInterfaceContract()
        {
            CoreAISettingsOptions sut = new();

            Assert.AreEqual(
                ICoreAISettings.DefaultConversationRolledSummaryMaxTokens,
                sut.ConversationRolledSummaryMaxTokens,
                "0 is the EXPLICIT 'unlimited' opt-out, so the rolling summary was never trimmed.");
            StringAssert.Contains(
                "NEVER output JSON in your text response",
                sut.UniversalSystemPromptPrefix,
                "The CRITICAL RULES block was replaced by a paraphrase, so agents lost the rule that keeps " +
                "them on the function-calling channel.");
            Assert.IsTrue(sut.AllowWorldPrimitives);
        }

        // --- Finding 10: schema ranges must apply regardless of type casing ---

        [Test]
        public void JsonSchemaValidator_UppercaseNumericType_StillEnforcesTheRange()
        {
            JsonSchemaValidator sut = new JsonSchemaValidator("stats")
                .AddField("hp", "Number", true, 0, 100);

            JsonValidationResult result = sut.Validate("{\"hp\":9999}");

            Assert.IsFalse(
                result.IsValid,
                "The range gate compared the raw type string while CheckType lower-cased it, so a field " +
                "declared as 'Number' skipped Min/Max entirely.");
        }

        [Test]
        public void JsonSchemaValidator_UppercaseNumericType_AcceptsAnInRangeValue()
        {
            JsonSchemaValidator sut = new JsonSchemaValidator("stats")
                .AddField("hp", "Integer", true, 0, 100);

            Assert.IsTrue(sut.Validate("{\"hp\":42}").IsValid);
        }

        // --- helpers ---

        private static ICoreAISettings QuietSettings()
        {
            return new CoreAISettingsOptions
            {
                LogToolCalls = false,
                LogToolCallArguments = false,
                LogToolCallResults = false
            };
        }

        /// <summary>
        /// Appends one correctly chained journal line, mirroring how the writer builds it: the canonical
        /// preimage is the entry with <c>hash</c> still empty.
        /// </summary>
        private static string AppendChainedLine(
            List<string> lines, string prevHash, AuditEntryKind kind, long seq, string actor)
        {
            AuditEntry preimageEntry = new(
                seq, kind, "trace", actor, "m", "ph", "t", "{}", "allowed", "ok", "", 1,
                "", "", prevHash, "", "", DateTime.UtcNow);
            string preimage = JsonConvert.SerializeObject(preimageEntry);
            string hash = AuditHash.Chain(prevHash, preimage);
            lines.Add(JsonConvert.SerializeObject(preimageEntry.WithHash(hash)));
            return hash;
        }

        private static void MutateEveryProperty(CoreAISettingsOptions target)
        {
            int seed = 3;
            foreach (PropertyInfo prop in typeof(CoreAISettingsOptions)
                         .GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (!prop.CanRead || !prop.CanWrite)
                {
                    continue;
                }

                if (prop.PropertyType == typeof(bool))
                {
                    prop.SetValue(target, !(bool)prop.GetValue(target));
                }
                else if (prop.PropertyType == typeof(int))
                {
                    prop.SetValue(target, (int)prop.GetValue(target) + seed++);
                }
                else if (prop.PropertyType == typeof(float))
                {
                    prop.SetValue(target, (float)prop.GetValue(target) + seed++);
                }
                else if (prop.PropertyType == typeof(string))
                {
                    prop.SetValue(target, $"probe-{prop.Name}");
                }
            }
        }

        private static AiOrchestrator BuildOrchestrator(
            ILlmClient llm,
            IAgentTurnTraceSink traceSink,
            IAiOrchestrationMetrics metrics)
        {
            AiPromptComposer composer = new(
                new StubSystemPrompts(),
                new StubUserTemplates(),
                null);

            return new AiOrchestrator(
                new SoloAuthorityHost(),
                llm,
                new NoOpCommandSink(),
                new SessionTelemetryCollector(),
                composer,
                new NullAgentMemoryStore(),
                null,
                null,
                metrics,
                new CoreAISettingsOptions(),
                traceSink: traceSink);
        }

        private sealed class StubSystemPrompts : IAgentSystemPromptProvider
        {
            public bool TryGetSystemPrompt(string roleId, out string systemPrompt)
            {
                systemPrompt = "system";
                return true;
            }
        }

        private sealed class StubUserTemplates : IAgentUserPromptTemplateProvider
        {
            public bool TryGetUserTemplate(string roleId, out string template)
            {
                template = "{goal}";
                return true;
            }
        }

        private sealed class NoOpCommandSink : IAiGameCommandSink
        {
            public void Publish(ApplyAiGameCommand command)
            {
            }
        }

        private sealed class RecordingTraceSink : IAgentTurnTraceSink
        {
            public List<AgentTurnTrace> Traces { get; } = new();

            public void Record(AgentTurnTrace trace)
            {
                Traces.Add(trace);
            }
        }

        private sealed class RecordingMetrics : IAiOrchestrationMetrics
        {
            public List<bool> Completions { get; } = new();

            public void RecordLlmCompletion(string roleId, string traceId, bool ok, double wallMs)
            {
                Completions.Add(ok);
            }

            public void RecordStructuredRetry(string roleId, string traceId, string reason)
            {
            }

            public void RecordCommandPublished(string roleId, string traceId)
            {
            }
        }

        private sealed class SilentStreamingClient : ILlmClient
        {
            public Task<LlmCompletionResult> CompleteAsync(
                LlmCompletionRequest request, CancellationToken cancellationToken = default)
            {
                return Task.FromResult(new LlmCompletionResult { Ok = true, Content = "" });
            }

            public async IAsyncEnumerable<LlmStreamChunk> CompleteStreamingAsync(
                LlmCompletionRequest request,
                [EnumeratorCancellation]
                CancellationToken cancellationToken = default)
            {
                await Task.Yield();
                yield break;
            }
        }

        private sealed class TextStreamingClient : ILlmClient
        {
            private readonly string _text;

            public TextStreamingClient(string text)
            {
                _text = text;
            }

            public Task<LlmCompletionResult> CompleteAsync(
                LlmCompletionRequest request, CancellationToken cancellationToken = default)
            {
                return Task.FromResult(new LlmCompletionResult { Ok = true, Content = _text });
            }

            public async IAsyncEnumerable<LlmStreamChunk> CompleteStreamingAsync(
                LlmCompletionRequest request,
                [EnumeratorCancellation]
                CancellationToken cancellationToken = default)
            {
                await Task.Yield();
                yield return new LlmStreamChunk { Text = _text };
                yield return new LlmStreamChunk { IsDone = true };
            }
        }

        private sealed class StallingStreamingClient : ILlmClient
        {
            public Task<LlmCompletionResult> CompleteAsync(
                LlmCompletionRequest request, CancellationToken cancellationToken = default)
            {
                return Task.FromResult(new LlmCompletionResult { Ok = true, Content = "" });
            }

            public async IAsyncEnumerable<LlmStreamChunk> CompleteStreamingAsync(
                LlmCompletionRequest request,
                [EnumeratorCancellation]
                CancellationToken cancellationToken = default)
            {
                await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken);
                yield return new LlmStreamChunk { IsDone = true, Text = "never reached" };
            }
        }

        private sealed class ThrowingStreamingClient : ILlmClient
        {
            private readonly Exception _fault;

            public ThrowingStreamingClient(Exception fault)
            {
                _fault = fault;
            }

            public Task<LlmCompletionResult> CompleteAsync(
                LlmCompletionRequest request, CancellationToken cancellationToken = default)
            {
                return Task.FromResult(new LlmCompletionResult { Ok = true, Content = "" });
            }

            public async IAsyncEnumerable<LlmStreamChunk> CompleteStreamingAsync(
                LlmCompletionRequest request,
                [EnumeratorCancellation]
                CancellationToken cancellationToken = default)
            {
                await Task.Yield();
                if (_fault != null)
                {
                    throw _fault;
                }

                yield return new LlmStreamChunk { IsDone = true };
            }
        }

        private sealed class UnreadableMemoryStore : IAgentMemoryStore, IAgentMemoryLoadDiagnostics
        {
            public AgentMemoryLoadStatus Status { get; set; } = AgentMemoryLoadStatus.Failed;
            public int SaveCount { get; private set; }
            public AgentMemoryState Saved { get; private set; }

            public AgentMemoryLoadStatus TryLoadDetailed(string roleId, out AgentMemoryState state)
            {
                state = null;
                return Status;
            }

            public bool TryLoad(string roleId, out AgentMemoryState state)
            {
                state = null;
                return false;
            }

            public void Save(string roleId, AgentMemoryState state)
            {
                SaveCount++;
                Saved = state;
            }

            public void Clear(string roleId)
            {
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

        private sealed class CancellingSkillTool : LlmToolBase, IJsonInvocableLlmTool
        {
            public override string Name => "slow_tool";
            public override string Description => "Never returns before its deadline.";

            public async Task<object> InvokeJsonAsync(
                string argumentsJson, CancellationToken cancellationToken = default)
            {
                using CancellationTokenSource fired = new();
                fired.Cancel();
                await Task.Delay(TimeSpan.FromSeconds(30), fired.Token);
                return "unreachable";
            }
        }

        private sealed class RejectingGameConfigPolicy : GameConfigPolicy
        {
            public override bool TryApplyChanges(
                string roleId, string json, out string[] appliedKeys, out string error)
            {
                appliedKeys = Array.Empty<string>();
                error = "difficulty out of range";
                return false;
            }
        }

        private sealed class RecordingConfigStore : IGameConfigStore
        {
            public int SaveCount { get; private set; }

            public bool TryLoad(string key, out string json)
            {
                json = "{}";
                return true;
            }

            public bool TrySave(string key, string json)
            {
                SaveCount++;
                return true;
            }

            public string[] GetKnownKeys()
            {
                return new[] { "difficulty" };
            }
        }
    }
}
#endif
