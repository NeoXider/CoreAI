using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Ai;
using CoreAI.Ai.Logging;
using CoreAI.Audit;
using CoreAI.Features.Audit;
using NUnit.Framework;
using UnityEngine;
#if COREAI_LLM
using CoreAI.Infrastructure.Llm;
using CoreAI.Infrastructure.Logging;
using MEAI = Microsoft.Extensions.AI;
#endif

namespace CoreAI.Tests.EditMode
{
    /// <summary>Protects read-triggered persistence receipts and valid Unicode through real diagnostic sinks.</summary>
    public sealed class PrReviewPersistenceEditModeTests
    {
        private string _root;
        private readonly List<AuditLogWriter> _writers = new List<AuditLogWriter>();

        [SetUp]
        public void SetUp()
        {
            _root = Path.Combine(Application.temporaryCachePath, "PrReviewPersistence_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
        }

        [TearDown]
        public void TearDown()
        {
            foreach (AuditLogWriter writer in _writers)
            {
                writer.SimulateWriteFailureForTesting = null;
                writer.Dispose();
            }
            _writers.Clear();
            if (Directory.Exists(_root)) Directory.Delete(_root, true);
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task MEM001_ReadMigrationWaitsForReceiptAndRetainsRefusedGeneration(bool transaction)
        {
            const string role = "teacher";
            const string original = "{\"Summary\":\"legacy fact\"}";
            string legacy = Path.Combine(_root, role + ".json");
            string canonical = Path.Combine(_root, AgentMemoryFileKey.FileStem(role) + ".json");
            File.WriteAllText(legacy, original);
            int pendingBefore = FileConversationSummaryStore.PendingConfirmationCount;
            int confirmations = 0;
            bool recovery = false;
            TaskCompletionSource<bool> entered = new TaskCompletionSource<bool>();
            TaskCompletionSource<bool> receipt = new TaskCompletionSource<bool>();
            FileConversationSummaryStore store = new FileConversationSummaryStore(_root,
                afterWriteAsync: token =>
                {
                    Assert.IsFalse(token.CanBeCanceled, "An already committed migration must confirm independently of caller cancellation.");
                    confirmations++;
                    entered.TrySetResult(true);
                    return recovery ? Task.FromResult(true) : receipt.Task;
                });
            Task<string> operation = ReadSummary(store, role, transaction);
            try
            {
                Task first = await Task.WhenAny(entered.Task, operation);
                Assert.AreSame(entered.Task, first, "A read that copies and renames storage must enter the supplied receipt hook.");
                Assert.IsFalse(operation.IsCompleted, "Migration cannot report success while its receipt remains pending.");
                Assert.IsTrue(File.Exists(canonical));
                Assert.IsFalse(File.Exists(legacy));
                Assert.AreEqual(original, File.ReadAllText(canonical));
                Assert.AreEqual(original, File.ReadAllText(legacy + ".legacy"));
                receipt.TrySetResult(false);
                Exception refusal = await Capture(operation);
                Assert.IsInstanceOf<IOException>(refusal, "A refused receipt must remain an observable failed read/open.");
                Assert.AreEqual(1, confirmations);
                Assert.AreEqual(pendingBefore + 1, FileConversationSummaryStore.PendingConfirmationCount);
                store.Dispose();

                recovery = true;
                using FileConversationSummaryStore peer = new FileConversationSummaryStore(_root);
                Assert.AreEqual("legacy fact", await ReadSummary(peer, role, transaction));
                Assert.GreaterOrEqual(confirmations, 2, "A new peer must confirm the retained mutation rather than forget it.");
                Assert.AreEqual(pendingBefore, FileConversationSummaryStore.PendingConfirmationCount);
                Assert.AreEqual(original, File.ReadAllText(legacy + ".legacy"));
                Assert.AreEqual(original, File.ReadAllText(canonical));
            }
            finally
            {
                recovery = true;
                receipt.TrySetResult(true);
                await Capture(operation);
                store.Dispose();
                using FileConversationSummaryStore cleanup = new FileConversationSummaryStore(_root);
                await cleanup.LoadSummaryAsync(role);
            }
        }

        [Test]
        public async Task MEM001_CanonicalReadDoesNotInventAWriteReceipt()
        {
            const string role = "teacher";
            string canonical = Path.Combine(_root, AgentMemoryFileKey.FileStem(role) + ".json");
            File.WriteAllText(canonical, "{\"RoleId\":\"teacher\",\"Summary\":\"already canonical\"}");
            int calls = 0;
            using FileConversationSummaryStore store = new FileConversationSummaryStore(_root,
                afterWriteAsync: token => { calls++; return Task.FromResult(false); });
            Assert.AreEqual("already canonical", await store.LoadSummaryAsync(role));
            Assert.AreEqual(0, calls, "A read without a mutation needs no persistence acknowledgement.");
            Assert.AreEqual(1, Directory.EnumerateFiles(_root).Count());
        }

        private static async Task<string> ReadSummary(FileConversationSummaryStore store, string role, bool transaction)
        {
            if (!transaction) return await store.LoadSummaryAsync(role);
            using ConversationSummaryTransaction snapshot = await store.OpenSummaryTransactionAsync(role);
            return snapshot.Summary;
        }

#if COREAI_LLM
        [TestCase(false)]
        [TestCase(true)]
        public async Task AUD001_ValidPostMarshalToolSnapshotDoesNotPoisonFlushOrDisposal(bool disposeFlush)
        {
            CoreAI.Composition.GlobalMessagePipeMinimalBootstrap.EnsureInitializedForLlmDiagnostics();
            AuditLogWriter writer = NewWriter(_root);
            writer.SimulateWriteFailureForTesting = () => true;
            RecordingForwarder audit = new RecordingForwarder(writer);
            string fullResult = new string('a', 1999) + char.ConvertFromUtf32(0x1F600) + "z";
            Assert.IsTrue(IsWellFormedUnicode(fullResult));
            CoreAISettingsOptions settings = new CoreAISettingsOptions
            {
                LogToolCallResults = true, LogToolCalls = false, LogToolCallArguments = false,
                LogMeaiToolCallingSteps = false, MaxToolResultChars = 8000, DefaultToolTimeoutMs = 0
            };
            int invocations = 0;
            DelegateLlmTool tool = new DelegateLlmTool("boundary_result", "Return valid Unicode.",
                (Func<string>)(() => { invocations++; return fullResult; }));
            MeaiLlmClient client = new MeaiLlmClient(new OneToolChatClient(),
                GameLoggerUnscopedFallback.Instance, settings, true);
            writer.Record(Healthy("before"));
            using (ToolCallAuditInterceptor interceptor = new ToolCallAuditInterceptor(audit))
            {
                LlmCompletionResult completion = await client.CompleteAsync(new LlmCompletionRequest
                {
                    ActorId = "actor-a", AgentRoleId = "builder", TraceId = "unicode-tool",
                    UserPayload = "Produce the boundary result.", Tools = new[] { tool }
                });
                Assert.IsTrue(completion.Ok, completion.Error);
            }
            Assert.AreEqual(1, invocations);
            Assert.AreEqual(1, audit.Entries.Count);
            Assert.AreEqual("unicode-tool", audit.Entries.Single().TraceId);
            Assert.AreEqual("actor-a", audit.Entries.Single().Actor);
            Assert.IsNotEmpty(audit.Entries.Single().ResultDetail,
                "The policy's post-marshalling diagnostic must actually enter the interceptor.");
            writer.Record(Healthy("after"));
            writer.FlushForTesting();
            Assert.IsFalse(File.Exists(writer.FilePath), "The controlled pre-write refusal must leave the batch pending.");
            writer.SimulateWriteFailureForTesting = null;
            if (disposeFlush)
            {
                writer.Dispose();
                AuditLogWriter peer = NewWriter(_root);
                peer.Record(Healthy("next"));
                peer.FlushForTesting();
            }
            else
            {
                writer.FlushForTesting();
                writer.FlushForTesting();
                writer.Record(Healthy("next"));
                writer.FlushForTesting();
            }

            List<AuditEntry> persisted = AuditLogVerifier.ReadAll(writer.FilePath);
            CollectionAssert.AreEqual(new[] { "before", "unicode-tool", "after", "next" },
                persisted.Select(entry => entry.TraceId).ToArray(),
                "A normal tool diagnostic cannot repeatedly hold healthy accepted entries behind its batch.");
            Assert.AreEqual(4, persisted.Select(entry => entry.TraceId).Distinct().Count());
            Assert.AreEqual(0, writer.DroppedCount);
            Assert.IsTrue(IsWellFormedUnicode(persisted.Single(entry => entry.TraceId == "unicode-tool").ResultDetail));
            AuditVerifyResult verified = AuditLogVerifier.Verify(writer.FilePath);
            Assert.IsTrue(verified.Ok, verified.Error);
            Assert.AreEqual(4, verified.LineCount);
        }

        private sealed class RecordingForwarder : IAuditLog
        {
            private readonly IAuditLog _target;
            internal readonly List<AuditEntry> Entries = new List<AuditEntry>();
            internal RecordingForwarder(IAuditLog target) { _target = target; }
            public void Record(AuditEntry entry) { Entries.Add(entry); _target.Record(entry); }
        }

        private sealed class OneToolChatClient : MEAI.IChatClient
        {
            private int _calls;
            public Task<MEAI.ChatResponse> GetResponseAsync(IEnumerable<MEAI.ChatMessage> messages,
                MEAI.ChatOptions options = null, CancellationToken cancellationToken = default)
            {
                _calls++;
                MEAI.ChatMessage response = _calls == 1
                    ? new MEAI.ChatMessage(MEAI.ChatRole.Assistant, new List<MEAI.AIContent>
                        { new MEAI.FunctionCallContent("boundary-call", "boundary_result", new Dictionary<string, object>()) })
                    : new MEAI.ChatMessage(MEAI.ChatRole.Assistant, "done");
                return Task.FromResult(new MEAI.ChatResponse(response));
            }
            public IAsyncEnumerable<MEAI.ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<MEAI.ChatMessage> messages,
                MEAI.ChatOptions options = null, CancellationToken cancellationToken = default) =>
                throw new NotSupportedException("This fixture drives the production buffered native tool loop.");
            public object GetService(Type type, object key = null) => null;
            public void Dispose() { }
        }
#endif

        [TestCase("split")]
        [TestCase("pair")]
        [TestCase("ascii")]
        public async Task AUD002_LogClippingPreservesUnicodeInMessageScriptAndToolSurface(string boundary)
        {
            string prefix = boundary == "split" ? "a" + char.ConvertFromUtf32(0x1F600)
                : boundary == "pair" ? char.ConvertFromUtf32(0x1F600) : "ab";
            string original = prefix + new string('x', 20);
            LuaLogService logs = new LuaLogService(maximumMessageCharacters: 16,
                maximumRetainedCharacters: 1024, mirrorErrorsToUnityConsole: false);
            logs.Append(new LuaLogEntry { ModId = "m", Message = original, ScriptName = original,
                Level = LuaLogLevel.Print });
            IReadOnlyList<LuaLogEntry> returned = logs.Query(new LuaLogQuery { ModId = "m", MaxCount = 10 });
            Assert.AreEqual(1, returned.Count);
            LuaLogEntry entry = returned[0];
            Assert.AreEqual("m", entry.ModId);
            Assert.IsTrue(IsWellFormedUnicode(entry.Message), "Clipping a valid message must not create a lone surrogate.");
            Assert.IsTrue(IsWellFormedUnicode(entry.ScriptName), "Script-name clipping has the same Unicode contract.");
            Assert.LessOrEqual(entry.Message.Length, 16);
            Assert.LessOrEqual(entry.ScriptName.Length, 16);
            StringAssert.EndsWith("...[truncated]", entry.Message);
            StringAssert.EndsWith("...[truncated]", entry.ScriptName);
            if (boundary == "split") Assert.AreEqual("a...[truncated]", entry.Message);
            else Assert.AreEqual(prefix + "...[truncated]", entry.Message);
            string snapshot = entry.Message;
            entry.Message = "caller mutation";
            Assert.AreEqual(snapshot, logs.Query(new LuaLogQuery { ModId = "m", MaxCount = 10 })[0].Message);
            UTF8Encoding strict = new UTF8Encoding(false, true);
            string formatted = LuaLogFormatter.ToPromptText(logs.Query(new LuaLogQuery { ModId = "m", MaxCount = 10 }), 6000);
            Assert.DoesNotThrow(() => strict.GetBytes(formatted));
            string json = await new GetModLogsLlmTool(logs).ExecuteAsync(mod_id: "m");
            Assert.DoesNotThrow(() => strict.GetBytes(json));
            Assert.LessOrEqual(logs.RetainedCharacters, 1024);
        }

        private AuditLogWriter NewWriter(string folder)
        {
            AuditLogWriter writer = new AuditLogWriter(folder,
                new AuditLogWriterOptions { DiagnosticSink = (level, message) => { } });
            _writers.Add(writer);
            return writer;
        }

        private static AuditEntry Healthy(string trace) => AuditEntry.ForToolCall(0, trace, "healthy-actor",
            "model", "prompt", "healthy", "{}", "allowed", "ok", "healthy detail", 0);

        private static async Task<Exception> Capture(Task task)
        {
            try { await task; return null; }
            catch (Exception exception) { return exception; }
        }

        private static bool IsWellFormedUnicode(string text)
        {
            for (int index = 0; index < text.Length; index++)
            {
                if (char.IsLowSurrogate(text[index])) return false;
                if (!char.IsHighSurrogate(text[index])) continue;
                if (index + 1 >= text.Length || !char.IsLowSurrogate(text[++index])) return false;
            }
            return true;
        }
    }
}
