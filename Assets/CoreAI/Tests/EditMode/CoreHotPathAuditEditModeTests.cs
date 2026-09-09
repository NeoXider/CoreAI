#if COREAI_LLM
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Ai;
using CoreAI.Audit;
using CoreAI.Infrastructure.Llm;
using Microsoft.Extensions.AI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using ChatMessage = CoreAI.Ai.ChatMessage;

namespace CoreAI.Core.Tests.EditMode
{
    /// <summary>
    /// Guards for the core hot-path audit. Each test is written from the defect it pins:
    /// per-chunk heap traffic in the streaming timeout decorator and the orchestration queue, a
    /// terminal chunk dropped by the queue reader, skill bindings rebuilt on every call of a live
    /// catalog, an audit prompt hash that never covered the conversation, unbounded audit-context
    /// growth in hosts without the audit interceptor, a thrown exception per plain-text tool result,
    /// and digests recomputed for the same input on every memory-store call.
    /// </summary>
    public sealed class CoreHotPathAuditEditModeTests
    {
        // ------------------------------------------------------------------------------------
        // Streaming timeout decorator
        // ------------------------------------------------------------------------------------

        /// <summary>
        /// Budget for what a stage may add on top of the inner stream for one chunk that arrives
        /// asynchronously: two posted continuations (the inner source hands the stage its callback, the
        /// stage hands the resumed loop to the host context; each post is one small tuple).
        /// Measured on this harness (4000 chunks, .NET 8): the inner stream alone costs 184 B/chunk; the
        /// former timeout wait (AsTask + async helper + WhenAny) added 512 B/chunk and the former queue
        /// park (TaskCompletionSource + async take) added 289 B/chunk; the current code adds 128 and 96.
        /// </summary>
        private const int AllowedBytesPerChunkOverInner = 192;

        [Test]
        public void TimeoutDecorator_AsynchronousChunks_AddNoHeapTrafficPerChunk()
        {
#if UNITY_5_3_OR_NEWER
            Assert.Ignore("Allocation accounting is asserted on the portable .NET leg of the suite.");
#else
            const int chunks = 4000;
            SingleThreadPump pump = new();

            long innerPerChunk = MeasureBytesPerChunk(
                pump, () => DrainAsync(new YieldingChunkClient { ChunkCount = chunks }), chunks);
            long decoratedPerChunk = MeasureBytesPerChunk(
                pump,
                () => DrainAsync(new TimeoutLlmClientDecorator(
                    new YieldingChunkClient { ChunkCount = chunks }, () => 30f, new NeverElapsingDelay())),
                chunks);

            TestContext.WriteLine(
                $"inner stream: {innerPerChunk} B/chunk; through TimeoutLlmClientDecorator: {decoratedPerChunk} B/chunk");
            Assert.LessOrEqual(
                decoratedPerChunk - innerPerChunk, AllowedBytesPerChunkOverInner,
                "The timeout decorator must not allocate per streamed chunk: every token a learner reads " +
                "goes through this wait, and on WebGL each object here is one more stop-the-world collection.");
#endif
        }

        /// <summary>
        /// The one-hop bias: when the deadline and a cooperative inner answer land in the same instant,
        /// the answer that already exists is delivered instead of a timeout. The pump makes the order
        /// deterministic: the signal wins the race, the move completes before the loop resumes.
        /// </summary>
        [Test]
        public void TimeoutDecorator_MoveFinishingRightAfterTheDeadline_IsDeliveredBeforeTheTimeout()
        {
            ManualMoveClient inner = new();
            PendingDelayMarshaler clock = new();
            TimeoutLlmClientDecorator sut = new(inner, () => 60f, clock);
            SingleThreadPump pump = new();
            SynchronizationContext previous = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(pump);
            try
            {
                IAsyncEnumerator<LlmStreamChunk> stream = sut.CompleteStreamingAsync(Req()).GetAsyncEnumerator();
                Task<bool> first = stream.MoveNextAsync().AsTask();
                pump.DrainUntil(() => inner.PendingMoves == 1);
                Assert.IsFalse(first.IsCompleted, "the first move is pending, so the loop is parked on the race");

                clock.ReleaseAll();
                pump.DrainUntil(() => inner.Token.IsCancellationRequested);
                Assert.IsFalse(first.IsCompleted, "the deadline fired but the parked loop has not resumed yet");

                // The inner answers in the same instant the deadline fires: its move completes BEFORE the
                // loop gets to observe the lost race.
                inner.CompletePendingMove(true, new LlmStreamChunk { Text = "late" });
                pump.DrainUntil(() => first.IsCompleted);

                Assert.IsTrue(first.Result);
                Assert.AreEqual("late", stream.Current.Text,
                    "an answer that already exists must win over reporting a timeout");

                Task<bool> second = stream.MoveNextAsync().AsTask();
                pump.DrainUntil(() => second.IsCompleted);
                Assert.IsTrue(second.Result);
                Assert.AreEqual(LlmErrorCode.Timeout, stream.Current.ErrorCode,
                    "with the deadline already passed, the next stall is the library timeout");

                Task<bool> third = stream.MoveNextAsync().AsTask();
                pump.DrainUntil(() => third.IsCompleted);
                Assert.IsFalse(third.Result);

                Task disposal = stream.DisposeAsync().AsTask();
                pump.DrainUntil(() => disposal.IsCompleted);
                Assert.AreEqual(0, inner.DisposeCount, "an abandoned move keeps the inner enumerator undisposed");
                inner.CompletePendingMove(false, null);
                pump.DrainUntil(() => inner.DisposeCount == 1);
            }
            finally
            {
                SynchronizationContext.SetSynchronizationContext(previous);
            }
        }

        // ------------------------------------------------------------------------------------
        // Orchestration queue
        // ------------------------------------------------------------------------------------

        [Test]
        public void QueuedOrchestrator_ParkedReader_AddsNoHeapTrafficPerChunk()
        {
#if UNITY_5_3_OR_NEWER
            Assert.Ignore("Allocation accounting is asserted on the portable .NET leg of the suite.");
#else
            const int chunks = 4000;
            SingleThreadPump pump = new();
            YieldingOrchestrator inner = new() { ChunkCount = chunks };
            using QueuedAiOrchestrator queue = new(inner, new AiOrchestrationQueueOptions { MaxConcurrent = 1 });

            long innerPerChunk = MeasureBytesPerChunk(pump, () => DrainAsync(inner), chunks);
            long queuedPerChunk = MeasureBytesPerChunk(pump, () => DrainAsync(queue), chunks);

            TestContext.WriteLine(
                $"inner orchestrator: {innerPerChunk} B/chunk; through QueuedAiOrchestrator: {queuedPerChunk} B/chunk");
            Assert.LessOrEqual(
                queuedPerChunk - innerPerChunk, AllowedBytesPerChunkOverInner,
                "Parking the reader between chunks must not allocate: the reader drains faster than the " +
                "model speaks, so nearly every chunk parks it.");
#endif
        }

        /// <summary>
        /// The reader used to check "queue empty" and then "completed" as two separate reads: a producer
        /// writing its terminal chunk and completing between them made the reader stop with the chunk
        /// still queued. The scripted source reproduces exactly that interleaving - the write and the
        /// completion land while the reader is between its two looks - so the outcome does not depend on
        /// thread scheduling: the terminal chunk must still be delivered, and without a park.
        /// </summary>
        [Test]
        public async Task QueueReader_CompletionObservedRightAfterAnEmptyLook_StillDeliversTheLastChunk()
        {
            ScriptedChunkSource source = new();

            List<LlmStreamChunk> chunks = new();
            await foreach (LlmStreamChunk chunk in QueuedAiOrchestrator.ReadStreamingQueue(source))
            {
                chunks.Add(chunk);
            }

            Assert.AreEqual(1, chunks.Count,
                "the terminal chunk written just before completion was dropped by the queue reader");
            Assert.IsTrue(chunks[0].IsDone);
            Assert.AreEqual(0, source.Parks, "once completion is observed the reader must not park");
        }

        // ------------------------------------------------------------------------------------
        // Skills on a live catalog
        // ------------------------------------------------------------------------------------

        [Test]
        public void CallSkillTool_LiveCatalog_BuildsToolBindingsOncePerCatalogVersion()
        {
            CountingTool tool = new("count_me");
            MutableSkillCatalog catalog = new();
            catalog.AddOrReplace(new SkillSet("Counting", "counts", tool));
            ILlmTool callSkillTool = CallSkillToolLlmTool.Create(catalog);
            int afterConstruction = tool.FunctionsCreated;

            ISkillSetMetaLlmTool meta = (ISkillSetMetaLlmTool)callSkillTool;
            IResolvedLlmToolCallProvider provider = (IResolvedLlmToolCallProvider)callSkillTool;
            for (int i = 0; i < 25; i++)
            {
                Assert.IsTrue(meta.ContainsSkillTool("count_me"));
                Assert.IsTrue(provider.TryResolveInvocation(Args("count_me", "{\"input\":\"x\"}"), out _, out string error),
                    error);
            }

            Assert.AreEqual(afterConstruction + 1, tool.FunctionsCreated,
                "the live tool map is built once and reused while the catalog version stands still; it used " +
                "to be rebuilt - every skill tool's MEAI function re-created by reflection - on every call");

            catalog.AddOrReplace(new SkillSet("Other", "other", new CountingTool("other_tool")));
            Assert.IsTrue(meta.ContainsSkillTool("other_tool"), "a catalog change is visible on the next call");
            int afterChange = tool.FunctionsCreated;
            for (int i = 0; i < 25; i++)
            {
                Assert.IsTrue(meta.ContainsSkillTool("count_me"));
            }

            Assert.AreEqual(afterChange, tool.FunctionsCreated, "no rebuild without a catalog change");
        }

        [Test]
        public void ReadSkill_LiveCatalog_IndexesOncePerCatalogVersion_AndCachesItsSchema()
        {
            CountingTool tool = new("count_me");
            MutableSkillCatalog catalog = new();
            catalog.AddOrReplace(new SkillSet("Counting", "counts", tool));
            ILlmTool readSkill = ReadSkillLlmTool.Create(catalog);
            int afterConstruction = tool.FunctionsCreated;

            ISkillSetMetaLlmTool meta = (ISkillSetMetaLlmTool)readSkill;
            for (int i = 0; i < 25; i++)
            {
                Assert.IsTrue(meta.ContainsSkillTool("count_me"));
            }

            Assert.AreEqual(afterConstruction + 1, tool.FunctionsCreated,
                "the live skill index is built once per catalog version");

            string schema = readSkill.ParametersSchema;
            Assert.AreSame(schema, readSkill.ParametersSchema,
                "read_skill's schema is derived by reflection from a fixed delegate; it is read several times " +
                "per request and must be computed once");
            StringAssert.Contains("skill_name", schema);
        }

        [Test]
        public async Task ResolvedSkillCall_KeepsItsParsedArguments_ForSignatureAndInvocation()
        {
            RecordingJsonTool tool = new("echo_json");
            MutableSkillCatalog catalog = new();
            catalog.AddOrReplace(new SkillSet("Echo", "echoes", tool));
            IResolvedLlmToolCallProvider provider =
                (IResolvedLlmToolCallProvider)CallSkillToolLlmTool.Create(catalog);
            const string json = "{ \"b\" : [1, 2], \"a\": {\"k\": \"v\"}, \"n\": 3, \"s\": \"text\" }";

            Assert.IsTrue(provider.TryResolveInvocation(Args("echo_json", json),
                out ResolvedLlmToolInvocation invocation, out string error), error);

            IDictionary<string, object> arguments = invocation.Arguments;
            Assert.AreEqual("[1,2]", arguments["b"], "nested tokens normalize to compact JSON strings");
            Assert.AreEqual("{\"k\":\"v\"}", arguments["a"]);
            Assert.AreEqual(3L, arguments["n"]);
            Assert.AreEqual("text", arguments["s"]);
            CollectionAssert.AreEquivalent(arguments.Keys, invocation.Arguments.Keys,
                "the argument view can be taken more than once");

            await invocation.InvokeAsync(CancellationToken.None);
            Assert.AreEqual(JObject.Parse(json).ToString(Formatting.None), tool.LastArgumentsJson,
                "a JSON-invocable tool still receives the compact form of exactly what the model sent");
        }

        [Test]
        public void MutableSkillCatalog_VersionMovesOnlyWhenTheCatalogChanges()
        {
            MutableSkillCatalog catalog = new();
            Assert.AreEqual(0, catalog.Version);

            catalog.AddOrReplace(null);
            Assert.AreEqual(0, catalog.Version, "a rejected add is not a change");

            catalog.AddOrReplace(new SkillSet("A", "a"));
            Assert.AreEqual(1, catalog.Version);

            catalog.AddOrReplace(new SkillSet("a", "replaced"));
            Assert.AreEqual(2, catalog.Version, "replacing under the same (case-insensitive) name is a change");

            Assert.IsFalse(catalog.Remove("missing"));
            Assert.AreEqual(2, catalog.Version, "removing nothing is not a change");

            Assert.IsTrue(catalog.Remove("A"));
            Assert.AreEqual(3, catalog.Version);
        }

        // ------------------------------------------------------------------------------------
        // Audit prompt hash and audit context
        // ------------------------------------------------------------------------------------

        private static IEnumerable<TestCaseData> PartsCases()
        {
            yield return new TestCaseData(new object[] { new[] { "hello", "\n", "world" } }).SetName("Parts_PlainAscii");
            yield return new TestCaseData(new object[] { new[] { "система: учитель", "\n", "ученик: привет" } })
                .SetName("Parts_Cyrillic");
            yield return new TestCaseData(new object[] { new[] { "a\uD83D", "\uDE00b" } })
                .SetName("Parts_SurrogatePairSplitAcrossParts");
            yield return new TestCaseData(new object[] { new[] { new string('x', 1023) + "😀y" } })
                .SetName("Parts_SurrogatePairStraddlingTheEncodingChunk");
            yield return new TestCaseData(new object[] { new[] { "tail\uD83D" } }).SetName("Parts_LoneHighSurrogateAtEnd");
            yield return new TestCaseData(new object[] { new[] { "\uDE00head", "x" } }).SetName("Parts_LoneLowSurrogateAtStart");
            yield return new TestCaseData(new object[] { new[] { null, "", "only", null } }).SetName("Parts_NullAndEmptyParts");
            yield return new TestCaseData(new object[] { new[] { new string('я', 5000), "\n", new string('z', 3000) } })
                .SetName("Parts_MultiChunkParts");
        }

        [TestCaseSource(nameof(PartsCases))]
        public void AuditHash_ComputeParts_MatchesTheDigestOfTheJoinedString(string[] parts)
        {
            string joined = string.Concat(parts);
            Assert.AreEqual(AuditHash.Compute(joined), AuditHash.ComputeParts(parts),
                "hashing part by part must give the digest of the joined string, including surrogate pairs " +
                "split across parts or encoding chunks");
        }

        [Test]
        public void AuditHash_ComputeParts_EmptyInput_MatchesTheSingleStringForm()
        {
            Assert.AreEqual("", AuditHash.ComputeParts(null));
            Assert.AreEqual("", AuditHash.ComputeParts(new[] { "", null }));
        }

        [Test]
        public void PromptHash_CoversTheConversationHistory()
        {
            List<Microsoft.Extensions.AI.ChatMessage> history = new()
            {
                new(ChatRole.User, "first question"),
                new(ChatRole.Assistant, "first answer")
            };
            string hash = AiOrchestrator.ComputePromptHash("system", "user", history);

            Assert.AreEqual(
                AuditHash.Compute("system\nuser\nfirst question\nfirst answer"), hash,
                "the fingerprint is system, user and every history message, newline-separated");

            history[1] = new Microsoft.Extensions.AI.ChatMessage(ChatRole.Assistant, "a different answer");
            Assert.AreNotEqual(hash, AiOrchestrator.ComputePromptHash("system", "user", history),
                "a different conversation must produce a different prompt hash - it used to hash the " +
                "history list's type name, so every turn with the same system and user text collided");

            Assert.AreEqual(AuditHash.Compute("system\nuser\n"),
                AiOrchestrator.ComputePromptHash("system", "user", null),
                "no history contributes nothing");
        }

        [Test]
        public void AuditContext_WithoutCleanup_StaysBounded()
        {
            string prefix = "hot-path-audit-" + Guid.NewGuid().ToString("N") + "-";
            int total = AuditContext.MaxTrackedTraces * 3;
            for (int i = 0; i < total; i++)
            {
                AuditContext.SetPromptHash(prefix + i, "hash-" + i);
            }

            Assert.LessOrEqual(AuditContext.TrackedTraceCount, AuditContext.MaxTrackedTraces,
                "hosts without the audit interceptor never call Cleanup; retention must not grow with the " +
                "number of requests");
            Assert.AreEqual("hash-" + (total - 1), AuditContext.GetPromptHash(prefix + (total - 1)),
                "the newest trace is the one still in flight and must survive");
            Assert.AreEqual("", AuditContext.GetPromptHash(prefix + 0), "the oldest trace is the one dropped");
        }

        // ------------------------------------------------------------------------------------
        // Tool trace message extraction
        // ------------------------------------------------------------------------------------

        [Test]
        public void ExtractToolTraceMessage_PlainTextResult_DoesNotThrowInternally()
        {
            int thrown = 0;
            EventHandler<FirstChanceExceptionEventArgs> counter = (_, _) => Interlocked.Increment(ref thrown);
            AppDomain.CurrentDomain.FirstChanceException += counter;
            string plain;
            string bracketed;
            try
            {
                plain = AiOrchestrator.ExtractToolTraceMessage("card shown, waiting for the student");
                bracketed = AiOrchestrator.ExtractToolTraceMessage("[1, 2, 3]");
            }
            finally
            {
                AppDomain.CurrentDomain.FirstChanceException -= counter;
            }

            Assert.AreEqual("card shown, waiting for the student", plain);
            Assert.AreEqual("[1, 2, 3]", bracketed);
            Assert.AreEqual(0, thrown,
                "a plain-text tool result is the common case and must not cost a thrown-and-caught exception");

            Assert.AreEqual("boom", AiOrchestrator.ExtractToolTraceMessage("  {\"success\":false,\"message\":\"boom\"}  "));
            Assert.AreEqual("bad", AiOrchestrator.ExtractToolTraceMessage("{\"Error\":\"bad\"}"));
            Assert.AreEqual("{\"other\":1}", AiOrchestrator.ExtractToolTraceMessage("{\"other\":1}"),
                "an object without a message field falls through to the raw text exactly as before");
        }

        // ------------------------------------------------------------------------------------
        // Logging decorator prompt budget
        // ------------------------------------------------------------------------------------

        [Test]
        public void LoggingDecorator_ToolsWordCount_MatchesTheJoinedCatalogItReplaced()
        {
            List<ILlmTool> tools = new()
            {
                new FakeTool("spawn_quiz", "Show a quiz\tcard to the\nstudent", "{\"type\":\"object\", \"properties\": {}}"),
                new FakeTool("nameonly", null, null),
                new FakeTool("  spaced  ", "   ", " {} "),
                null,
                new FakeTool("wait", "Pause for N seconds", "{\"type\":\"object\"}")
            };

            StringBuilder blob = new();
            foreach (ILlmTool t in tools)
            {
                if (t == null)
                {
                    continue;
                }

                blob.Append(t.Name).Append(' ').Append(t.Description).Append(' ').Append(t.ParametersSchema).Append(' ');
            }

            Assert.AreEqual(LoggingLlmClientDecorator.CountWords(blob.ToString()),
                LoggingLlmClientDecorator.CountToolsCatalogWords(tools),
                "counting per field must equal counting the space-joined catalog it replaced");
            Assert.AreEqual(0, LoggingLlmClientDecorator.CountToolsCatalogWords(null));
        }

        // ------------------------------------------------------------------------------------
        // Memoized digests
        // ------------------------------------------------------------------------------------

        [Test]
        public void ScopedMemoryKey_IsMemoized_AndStillTheExactDigest()
        {
            AgentMemoryScope scope = new("tenant", "user", "session", "topic");
            string first = AgentMemoryScopeKey.Resolve(scope, "Teacher");
            string second = AgentMemoryScopeKey.Resolve(scope, "Teacher");

            Assert.AreSame(first, second, "the same tuple must not be hashed again on every store call");
            Assert.AreEqual(AgentMemoryScopeKey.ScopedKeyPrefix + Sha256Hex("6:tenant;4:user;7:session;5:topic;7:Teacher;"),
                first, "memoization must not change the persisted key encoding");
        }

        [Test]
        public void FoldMarker_SharedHasherOverload_MatchesThePublicDigest()
        {
            ChatMessage message = new("assistant", "  the answer is 42\n");
            string expected = Sha256Hex("assistant\nthe answer is 42").Substring(0, 12);
            using SHA256 sha = SHA256.Create();

            Assert.AreEqual(expected, ConversationFoldMarker.HashMessage(message));
            Assert.AreEqual(expected, ConversationFoldMarker.HashMessage(sha, message));
            Assert.AreEqual(expected, ConversationFoldMarker.HashMessage(sha, message), "the hasher is reusable");
        }

        [Test]
        public void CanonicalSchema_IsMemoized_AndStillKeySorted()
        {
            const string schema = "{\"b\":1,\"a\":{\"d\":2,\"c\":3}}";
            string first = AiToolContractPromptFormatter.CanonicalizeSchemaOrRaw(schema);
            string second = AiToolContractPromptFormatter.CanonicalizeSchemaOrRaw(schema);

            Assert.AreEqual("{\"a\":{\"c\":3,\"d\":2},\"b\":1}", first);
            Assert.AreSame(first, second, "the same schema text must not be parsed again on every request");
            Assert.AreEqual("not json", AiToolContractPromptFormatter.CanonicalizeSchemaOrRaw("not json"));
        }

        // ------------------------------------------------------------------------------------
        // Helpers
        // ------------------------------------------------------------------------------------

        private static LlmCompletionRequest Req()
        {
            return new LlmCompletionRequest { AgentRoleId = "Test", UserPayload = "hi" };
        }

        private static Dictionary<string, object> Args(string toolName, string argumentsJson)
        {
            return new Dictionary<string, object>
            {
                ["tool_name"] = toolName,
                ["arguments_json"] = argumentsJson
            };
        }

        private static string Sha256Hex(string value)
        {
            using SHA256 sha = SHA256.Create();
            byte[] digest = sha.ComputeHash(Encoding.UTF8.GetBytes(value));
            StringBuilder sb = new(digest.Length * 2);
            foreach (byte b in digest)
            {
                sb.Append(b.ToString("x2"));
            }

            return sb.ToString();
        }

        private static async Task<int> DrainAsync(ILlmClient client)
        {
            int count = 0;
            await foreach (LlmStreamChunk _ in client.CompleteStreamingAsync(Req()))
            {
                count++;
            }

            return count;
        }

        private static async Task<int> DrainAsync(IAiOrchestrationService orchestrator)
        {
            int count = 0;
            await foreach (LlmStreamChunk _ in orchestrator.RunStreamingAsync(new AiTaskRequest()))
            {
                count++;
            }

            return count;
        }

        private static long MeasureBytesPerChunk(SingleThreadPump pump, Func<Task<int>> drain, int chunks)
        {
            // Two warm-up passes settle JIT tiering and buffer growth; the third pass is the measurement.
            pump.Run(drain);
            pump.Run(drain);
            long before = GC.GetAllocatedBytesForCurrentThread();
            int delivered = pump.Run(drain);
            long after = GC.GetAllocatedBytesForCurrentThread();
            Assert.GreaterOrEqual(delivered, chunks, "every chunk must be delivered");
            return (after - before) / chunks;
        }

        /// <summary>
        /// Runs asynchronous work entirely on the calling thread by acting as its SynchronizationContext:
        /// every posted continuation is executed here, in order, so a per-thread allocation counter sees the
        /// whole pipeline and interleavings are deterministic - the same single-threaded shape a WebGL
        /// player has.
        /// </summary>
        private sealed class SingleThreadPump : SynchronizationContext
        {
            private readonly Queue<(SendOrPostCallback Callback, object State)> _queue = new(1024);
            private readonly object _gate = new();

            public override void Post(SendOrPostCallback d, object state)
            {
                lock (_gate)
                {
                    _queue.Enqueue((d, state));
                }
            }

            public override void Send(SendOrPostCallback d, object state)
            {
                d(state);
            }

            public T Run<T>(Func<Task<T>> body)
            {
                SynchronizationContext previous = Current;
                SetSynchronizationContext(this);
                try
                {
                    Task<T> task = body();
                    DrainUntil(() => task.IsCompleted);
                    return task.GetAwaiter().GetResult();
                }
                finally
                {
                    SetSynchronizationContext(previous);
                }
            }

            public void DrainUntil(Func<bool> condition)
            {
                DateTime deadline = DateTime.UtcNow.AddSeconds(20);
                while (!condition())
                {
                    if (!TryRunOne())
                    {
                        if (DateTime.UtcNow > deadline)
                        {
                            Assert.Fail("The pumped work did not reach the expected state within 20 s.");
                        }

                        Thread.Yield();
                    }
                }
            }

            private bool TryRunOne()
            {
                (SendOrPostCallback Callback, object State) item;
                lock (_gate)
                {
                    if (_queue.Count == 0)
                    {
                        return false;
                    }

                    item = _queue.Dequeue();
                }

                item.Callback(item.State);
                return true;
            }
        }

        /// <summary>Delay that never elapses on its own: the deadline is armed but never reached.</summary>
        private sealed class NeverElapsingDelay : ILlmAsyncMarshaler
        {
            public Task<T> InvokeAsync<T>(Func<Task<T>> factory, CancellationToken cancellationToken)
            {
                return factory();
            }

            public Task DelayAsync(int milliseconds, CancellationToken cancellationToken)
            {
                TaskCompletionSource<bool> tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
                cancellationToken.Register(() => tcs.TrySetCanceled(cancellationToken));
                return tcs.Task;
            }
        }

        /// <summary>Delay that elapses only when the test says so.</summary>
        private sealed class PendingDelayMarshaler : ILlmAsyncMarshaler
        {
            private readonly List<TaskCompletionSource<bool>> _pending = new();

            public Task<T> InvokeAsync<T>(Func<Task<T>> factory, CancellationToken cancellationToken)
            {
                return factory();
            }

            public Task DelayAsync(int milliseconds, CancellationToken cancellationToken)
            {
                TaskCompletionSource<bool> tcs = new();
                cancellationToken.Register(() => tcs.TrySetCanceled(cancellationToken));
                lock (_pending)
                {
                    _pending.Add(tcs);
                }

                return tcs.Task;
            }

            public void ReleaseAll()
            {
                lock (_pending)
                {
                    foreach (TaskCompletionSource<bool> tcs in _pending)
                    {
                        tcs.TrySetResult(true);
                    }
                }
            }
        }

        /// <summary>Every chunk arrives asynchronously (one yield), as chunks do on a real network stream.</summary>
        private sealed class YieldingChunkClient : ILlmClient
        {
            public int ChunkCount;

            public Task<LlmCompletionResult> CompleteAsync(
                LlmCompletionRequest request, CancellationToken cancellationToken = default)
            {
                return Task.FromResult(new LlmCompletionResult { Ok = true, Content = "ok" });
            }

            public async IAsyncEnumerable<LlmStreamChunk> CompleteStreamingAsync(
                LlmCompletionRequest request,
                [EnumeratorCancellation] CancellationToken cancellationToken = default)
            {
                for (int i = 0; i < ChunkCount; i++)
                {
                    await Task.Yield();
                    yield return new LlmStreamChunk { Text = "t" };
                }

                yield return new LlmStreamChunk { IsDone = true };
            }
        }

        /// <summary>Stream whose moves complete only when the test completes them; ignores its token.</summary>
        private sealed class ManualMoveClient : ILlmClient, IAsyncEnumerable<LlmStreamChunk>,
            IAsyncEnumerator<LlmStreamChunk>
        {
            private TaskCompletionSource<bool> _move;
            public CancellationToken Token;
            public int PendingMoves;
            public int DisposeCount;

            public LlmStreamChunk Current { get; private set; }

            public Task<LlmCompletionResult> CompleteAsync(
                LlmCompletionRequest request, CancellationToken cancellationToken = default)
            {
                throw new NotSupportedException("streaming-only stub");
            }

            public IAsyncEnumerable<LlmStreamChunk> CompleteStreamingAsync(
                LlmCompletionRequest request, CancellationToken cancellationToken = default)
            {
                Token = cancellationToken;
                return this;
            }

            public IAsyncEnumerator<LlmStreamChunk> GetAsyncEnumerator(CancellationToken cancellationToken = default)
            {
                return this;
            }

            public ValueTask<bool> MoveNextAsync()
            {
                _move = new TaskCompletionSource<bool>();
                PendingMoves++;
                return new ValueTask<bool>(_move.Task);
            }

            public void CompletePendingMove(bool hasNext, LlmStreamChunk chunk)
            {
                Current = chunk;
                _move.TrySetResult(hasNext);
            }

            public ValueTask DisposeAsync()
            {
                DisposeCount++;
                return default;
            }
        }

        private sealed class YieldingOrchestrator : IAiOrchestrationService
        {
            public int ChunkCount;

            public Task<string> RunTaskAsync(AiTaskRequest task, CancellationToken cancellationToken = default)
            {
                return Task.FromResult("done");
            }

            public async IAsyncEnumerable<LlmStreamChunk> RunStreamingAsync(
                AiTaskRequest task,
                [EnumeratorCancellation] CancellationToken cancellationToken = default)
            {
                for (int i = 0; i < ChunkCount; i++)
                {
                    await Task.Yield();
                    yield return new LlmStreamChunk { Text = "t" };
                }

                yield return new LlmStreamChunk { IsDone = true };
            }

            public void CancelTasks(string cancellationScope)
            {
            }
        }

        /// <summary>
        /// Queue source scripted for the lost-terminal-chunk interleaving: the first look finds nothing;
        /// the producer's write and completion happen while the reader consults the completion flag.
        /// </summary>
        private sealed class ScriptedChunkSource : QueuedAiOrchestrator.IStreamingChunkSource
        {
            private readonly Queue<LlmStreamChunk> _items = new();
            private bool _completed;
            private bool _producerRan;
            public int Parks;

            public bool IsCompleted
            {
                get
                {
                    if (!_producerRan)
                    {
                        _producerRan = true;
                        _items.Enqueue(new LlmStreamChunk { IsDone = true, Text = "done" });
                        _completed = true;
                    }

                    return _completed;
                }
            }

            public bool TryTake(out LlmStreamChunk chunk)
            {
                if (_items.Count > 0)
                {
                    chunk = _items.Dequeue();
                    return true;
                }

                chunk = null;
                return false;
            }

            public ValueTask<bool> WaitForSignalAsync()
            {
                Parks++;
                return new ValueTask<bool>(true);
            }
        }

        private sealed class CountingTool : LlmToolBase, IAIFunctionLlmTool
        {
            private readonly string _name;
            public int FunctionsCreated;

            public CountingTool(string name)
            {
                _name = name;
            }

            public override string Name => _name;
            public override string Description => "Counts how many MEAI functions were created for it.";

            public AIFunction CreateAIFunction()
            {
                FunctionsCreated++;
                // WHY the named delegate instead of an inline lambda: Unity 6000.3 compiles this assembly
                // with an older C# language version, where a lambda has no natural type and cannot convert
                // to `Delegate`. The portable net8.0 harness accepts it, so this file compiled there and
                // failed only inside the editor — taking the WHOLE CoreAI.Core.Tests assembly with it,
                // including the MEAI version-floor guard this release is built around, and leaving an
                // EditMode run that looked green because those tests were never discovered. Every other
                // AIFunctionFactory.Create call site in the repository already states its delegate type.
                Func<string, string> body = input => "ok:" + input;
                return AIFunctionFactory.Create(
                    body,
                    new AIFunctionFactoryOptions { Name = _name, Description = Description });
            }
        }

        private sealed class RecordingJsonTool : LlmToolBase, IJsonInvocableLlmTool
        {
            private readonly string _name;
            public string LastArgumentsJson;

            public RecordingJsonTool(string name)
            {
                _name = name;
            }

            public override string Name => _name;
            public override string Description => "Records the JSON it is invoked with.";

            public Task<object> InvokeJsonAsync(string argumentsJson, CancellationToken cancellationToken = default)
            {
                LastArgumentsJson = argumentsJson;
                return Task.FromResult<object>("ok");
            }
        }

        private sealed class FakeTool : LlmToolBase
        {
            private readonly string _name;
            private readonly string _description;
            private readonly string _schema;

            public FakeTool(string name, string description, string schema)
            {
                _name = name;
                _description = description;
                _schema = schema;
            }

            public override string Name => _name;
            public override string Description => _description;
            public override string ParametersSchema => _schema;
        }
    }
}
#endif
