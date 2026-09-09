using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Ai;
using CoreAI.Authority;
using CoreAI.Infrastructure.AiMemory;
using CoreAI.Logging;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode
{
    /// <summary>
    /// The decorator maps role ids to scoped keys. The encoding must be injective: distinct scope tuples
    /// must never collide on the same key, or one user/session would read another's durable memory.
    /// </summary>
    public sealed class ScopedAgentMemoryStoreDecoratorEditModeTests
    {
        private sealed class KeyCapturingStore : IAgentMemoryStore
        {
            public readonly List<string> SavedKeys = new();

            public bool TryLoad(string roleId, out AgentMemoryState state)
            {
                state = null;
                return false;
            }

            public void Save(string roleId, AgentMemoryState state)
            {
                SavedKeys.Add(roleId);
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

        private sealed class CapturingLog : ILog
        {
            public readonly List<string> Messages = new();

            public void Debug(string message, string tag = null)
            {
                Messages.Add(message ?? "");
            }

            public void Info(string message, string tag = null)
            {
                Messages.Add(message ?? "");
            }

            public void Warn(string message, string tag = null)
            {
                Messages.Add(message ?? "");
            }

            public void Error(string message, string tag = null)
            {
                Messages.Add(message ?? "");
            }
        }

        private sealed class FixedScopeProvider : IAgentMemoryScopeProvider
        {
            private readonly AgentMemoryScope _scope;

            public FixedScopeProvider(AgentMemoryScope scope)
            {
                _scope = scope;
            }

            public AgentMemoryScope GetScope(string roleId)
            {
                return _scope;
            }
        }

        private sealed class MutableScopeProvider : IAgentMemoryScopeProvider
        {
            public string UserId { get; set; } = "";

            public AgentMemoryScope GetScope(string roleId)
            {
                return new AgentMemoryScope("redoschool", UserId, "", "");
            }
        }

        private sealed class InMemoryCapabilityStore : IAgentMemoryStore, IConversationTranscriptStore
        {
            private readonly Dictionary<string, AgentMemoryState> _states = new(StringComparer.Ordinal);
            private readonly Dictionary<string, List<ChatMessage>> _history = new(StringComparer.Ordinal);

            private readonly Dictionary<string, List<ConversationEntry>> _transcripts =
                new(StringComparer.Ordinal);

            public bool TryLoad(string roleId, out AgentMemoryState state)
            {
                return _states.TryGetValue(roleId ?? "", out state);
            }

            public void Save(string roleId, AgentMemoryState state)
            {
                _states[roleId ?? ""] = state;
            }

            public void Clear(string roleId)
            {
                _states.Remove(roleId ?? "");
            }

            public void ClearChatHistory(string roleId)
            {
                _history.Remove(roleId ?? "");
            }

            public void AppendChatMessage(string roleId, string role, string content, bool persistToDisk = true)
            {
                string key = roleId ?? "";
                if (!_history.TryGetValue(key, out List<ChatMessage> messages))
                {
                    messages = new List<ChatMessage>();
                    _history[key] = messages;
                }

                messages.Add(new ChatMessage(role, content));
            }

            public ChatMessage[] GetChatHistory(string roleId, int maxMessages = 0)
            {
                return _history.TryGetValue(roleId ?? "", out List<ChatMessage> messages)
                    ? messages.ToArray()
                    : Array.Empty<ChatMessage>();
            }

            public void AppendTranscriptEntry(string roleId, ConversationEntry entry, bool persistToDisk = true)
            {
                string key = roleId ?? "";
                if (!_transcripts.TryGetValue(key, out List<ConversationEntry> entries))
                {
                    entries = new List<ConversationEntry>();
                    _transcripts[key] = entries;
                }

                entries.Add(entry);
            }

            public IReadOnlyList<ConversationEntry> GetTranscriptEntries(string roleId, int maxEntries)
            {
                return _transcripts.TryGetValue(roleId ?? "", out List<ConversationEntry> entries)
                    ? entries.ToArray()
                    : Array.Empty<ConversationEntry>();
            }
        }

        private sealed class KeyCapturingConversationStore : IConversationTranscriptStore, IConversationSummaryStore
        {
            public string TranscriptKey { get; private set; } = "";
            public string SummaryKey { get; private set; } = "";

            public void AppendTranscriptEntry(string roleId, ConversationEntry entry, bool persistToDisk = true)
            {
                TranscriptKey = roleId;
            }

            public IReadOnlyList<ConversationEntry> GetTranscriptEntries(string roleId, int maxEntries)
            {
                TranscriptKey = roleId;
                return Array.Empty<ConversationEntry>();
            }

            public string LoadSummary(string roleId)
            {
                SummaryKey = roleId;
                return "";
            }

            public void SaveSummary(string roleId, string summary)
            {
                SummaryKey = roleId;
            }

            public void ClearSummary(string roleId)
            {
                SummaryKey = roleId;
            }
        }

        private static string ScopedKeyFor(AgentMemoryScope scope, string roleId)
        {
            KeyCapturingStore store = new();
            ScopedAgentMemoryStoreDecorator decorator = new(store, new FixedScopeProvider(scope));
            decorator.Save(roleId, new AgentMemoryState());
            return store.SavedKeys[0];
        }

        [Test]
        public void ScopedKeys_AmbiguousUnderscorePlacement_DoNotCollide()
        {
            // Without an injective encoding these two distinct tuples both produced "...a___b...".
            string keyA = ScopedKeyFor(new AgentMemoryScope("", "a_", "b", ""), "role");
            string keyB = ScopedKeyFor(new AgentMemoryScope("", "a", "_b", ""), "role");

            Assert.AreNotEqual(keyA, keyB,
                "Distinct user/session tuples must map to distinct memory keys (isolation).");
        }

        [Test]
        public void EmptyScope_PreservesBareRoleKey()
        {
            string key = ScopedKeyFor(AgentMemoryScope.Empty, "merchant");
            Assert.AreEqual("merchant", key,
                "An empty scope must preserve the bare role id (unchanged default behavior).");
        }

        [Test]
        public void DistinctUsers_ProduceDistinctKeys()
        {
            string keyUser1 = ScopedKeyFor(new AgentMemoryScope("t", "user1", "s", ""), "role");
            string keyUser2 = ScopedKeyFor(new AgentMemoryScope("t", "user2", "s", ""), "role");
            Assert.AreNotEqual(keyUser1, keyUser2);
        }

        [Test]
        public void ScopedKeys_DifferentLossyValuesWithSameSanitizedText_DoNotCollide()
        {
            string dotted = ScopedKeyFor(new AgentMemoryScope("", "a.b", "", ""), "role");
            string slashed = ScopedKeyFor(new AgentMemoryScope("", "a/b", "", ""), "role");

            Assert.AreNotEqual(dotted, slashed);
        }

        [Test]
        public void ScopedKeys_PaddedAndUnpaddedLossyValues_MapToSameKey()
        {
            string padded = ScopedKeyFor(new AgentMemoryScope("", " user@x ", "", ""), "role");
            string unpadded = ScopedKeyFor(new AgentMemoryScope("", "user@x", "", ""), "role");

            Assert.AreEqual(unpadded, padded);
        }

        [Test]
        public void ScopedKeys_AreOpaqueFullSha256Values()
        {
            string key = ScopedKeyFor(new AgentMemoryScope("t", "user1", "s", ""), "role");

            StringAssert.IsMatch("^scope-v1-[0-9a-f]{64}$", key);
            Assert.That(key, Does.Not.Contain("user1"));
            Assert.That(key, Does.Not.Contain("role"));
        }

        [Test]
        public void ScopedKeys_LowercaseGuid_IsNotPersistedInPlaintext()
        {
            const string userId = "01234567-89ab-cdef-0123-456789abcdef";
            string key = ScopedKeyFor(
                new AgentMemoryScope("", userId, "", ""),
                "role");

            StringAssert.IsMatch("^scope-v1-[0-9a-f]{64}$", key);
            Assert.That(key, Does.Not.Contain(userId));
        }

        [Test]
        public void ScopedKeys_LossyValueAndLiteralHashedValue_DoNotCollide()
        {
            string victim = ScopedKeyFor(new AgentMemoryScope("", "userX!", "", ""), "role");
            string attacker = ScopedKeyFor(new AgentMemoryScope("", "userX_-c4a7037cc0ec", "", ""), "role");

            Assert.AreNotEqual(victim, attacker);
        }

        [Test]
        public void ScopedKeys_CaseOnlyUserIds_ProduceDifferentLowercaseFileSafeKeys()
        {
            string upper = ScopedKeyFor(new AgentMemoryScope("tenant", "Student", "session", ""), "role");
            string lower = ScopedKeyFor(new AgentMemoryScope("tenant", "student", "session", ""), "role");

            Assert.AreNotEqual(upper, lower);
            Assert.AreEqual(upper.ToLowerInvariant(), upper);
            Assert.AreEqual(lower.ToLowerInvariant(), lower);
        }

        [Test]
        public void FilePersistence_CaseOnlyUsers_CreateDistinctOpaqueFiles()
        {
            string directory = CreateTemporaryDirectory();
            try
            {
                MutableScopeProvider provider = new();
                using FileAgentMemoryStore fileStore = new(rootDirectory: directory);
                ScopedAgentMemoryStoreDecorator scoped = new(fileStore, provider);

                provider.UserId = "Student";
                scoped.Save("Teacher", new AgentMemoryState { Memory = "upper" });
                provider.UserId = "student";
                scoped.Save("Teacher", new AgentMemoryState { Memory = "lower" });

                string[] names = Directory.GetFiles(directory, "*.json")
                    .Select(Path.GetFileName)
                    .OrderBy(value => value, StringComparer.Ordinal)
                    .ToArray();
                Assert.AreEqual(2, names.Length,
                    "Case-only identities must not share one filename on case-insensitive filesystems.");
                foreach (string name in names)
                {
                    StringAssert.IsMatch("^scope-v1-[0-9a-f]{64}\\.json$", name);
                }

                provider.UserId = "Student";
                Assert.IsTrue(scoped.TryLoad("Teacher", out AgentMemoryState upper));
                provider.UserId = "student";
                Assert.IsTrue(scoped.TryLoad("Teacher", out AgentMemoryState lower));
                Assert.AreEqual("upper", upper.Memory);
                Assert.AreEqual("lower", lower.Memory);
            }
            finally
            {
                Directory.Delete(directory, true);
            }
        }

        [Test]
        public void ScopedPersistence_DoesNotAutomaticallyClaimLegacyBareRoleData()
        {
            InMemoryCapabilityStore backing = new();
            backing.Save("Teacher", new AgentMemoryState { Memory = "legacy-shared" });
            ScopedAgentMemoryStoreDecorator scoped = new(backing, new FixedScopeProvider(
                new AgentMemoryScope("tenant", "student", "session", "")));

            Assert.IsFalse(scoped.TryLoad("Teacher", out _),
                "Legacy bare-role data needs an explicit host migration into a chosen scope.");
        }

        [Test]
        public void FileStores_ForcedWriteErrors_DoNotLogScopedKeyOrPii()
        {
            const string sentinel = "SENTINEL_STUDENT_PII_447188";
            string directory = CreateTemporaryDirectory();
            try
            {
                string blockingPath = Path.Combine(directory, "not-a-directory");
                File.WriteAllText(blockingPath, "block directory creation");
                CapturingLog log = new();
                FixedScopeProvider provider = new(new AgentMemoryScope("tenant", sentinel, "session", "topic"));

                using (FileAgentMemoryStore memory = new(log, blockingPath))
                {
                    new ScopedAgentMemoryStoreDecorator(memory, provider)
                        .Save("Teacher", new AgentMemoryState { Memory = "value" });
                }

                using (FileConversationSummaryStore summaries = new(blockingPath, log))
                {
                    // WHY a throw is expected here and not from the memory store: the summary store's
                    // contract is log-then-rethrow, so its caller decides whether a lost summary is
                    // fatal. What this test is about is the LOG — that it names a failure and carries
                    // neither the learner's id nor the derived scope key.
                    Assert.Catch<Exception>(() =>
                        new ScopedConversationSummaryStoreDecorator(summaries, provider)
                            .SaveSummary("Teacher", "summary"));
                }

                string joined = string.Join("\n", log.Messages);
                Assert.That(joined, Does.Contain("failed").IgnoreCase);
                Assert.That(joined, Does.Not.Contain(sentinel));
                Assert.That(joined, Does.Not.Contain("scope-v1-"));
            }
            finally
            {
                // WHY: without this, an assertion failure above skips cleanup and leaks the blocking
                // file, so a machine with one aborted run dies in setup on every run after.
                if (Directory.Exists(directory))
                {
                    Directory.Delete(directory, true);
                }
            }
        }

        [Test]
        public void TranscriptEntries_DistinctUsersWithSameRole_AreIsolated()
        {
            MutableScopeProvider provider = new();
            IConversationTranscriptStore transcript = new ScopedConversationTranscriptStoreDecorator(
                new InMemoryCapabilityStore(), provider);

            provider.UserId = "student-a";
            transcript.AppendTranscriptEntry("Teacher", new ConversationEntry { Content = "a" }, false);
            provider.UserId = "student-b";
            Assert.AreEqual(0, transcript.GetTranscriptEntries("Teacher", 0).Count);
            transcript.AppendTranscriptEntry("Teacher", new ConversationEntry { Content = "b" }, false);

            provider.UserId = "student-a";
            Assert.AreEqual("a", transcript.GetTranscriptEntries("Teacher", 0)[0].Content);
            provider.UserId = "student-b";
            Assert.AreEqual("b", transcript.GetTranscriptEntries("Teacher", 0)[0].Content);
        }

        [Test]
        public async Task AtomicMutation_SerializesSameScopedKey_AndKeepsScopesIndependent()
        {
            MutableScopeProvider provider = new();
            ScopedAgentMemoryStoreDecorator decorator = new(new InMemoryCapabilityStore(), provider);
            Assert.IsInstanceOf<IAtomicAgentMemoryStore>(decorator);
            IAtomicAgentMemoryStore atomic = decorator;

            provider.UserId = "student-a";
            await IncrementConcurrently(atomic, "Teacher", 24);
            provider.UserId = "student-b";
            await IncrementConcurrently(atomic, "Teacher", 11);

            provider.UserId = "student-a";
            Assert.IsTrue(decorator.TryLoad("Teacher", out AgentMemoryState studentA));
            provider.UserId = "student-b";
            Assert.IsTrue(decorator.TryLoad("Teacher", out AgentMemoryState studentB));
            Assert.AreEqual("24", studentA.Memory);
            Assert.AreEqual("11", studentB.Memory);
        }

        [Test]
        public void ConversationSummary_DistinctUsersWithSameRole_AreIsolated()
        {
            MutableScopeProvider provider = new();
            InMemoryConversationSummaryStore backing = new();
            IConversationSummaryStore summaries = new ScopedConversationSummaryStoreDecorator(backing, provider);

            provider.UserId = "student-a";
            summaries.SaveSummary("Teacher", "summary-a");
            provider.UserId = "student-b";
            Assert.AreEqual("", summaries.LoadSummary("Teacher"));
            summaries.SaveSummary("Teacher", "summary-b");

            provider.UserId = "student-a";
            Assert.AreEqual("summary-a", summaries.LoadSummary("Teacher"));
            provider.UserId = "student-b";
            Assert.AreEqual("summary-b", summaries.LoadSummary("Teacher"));
        }

        [Test]
        public void ConversationSummary_EmptyScope_PreservesLegacyRoleKey()
        {
            InMemoryConversationSummaryStore backing = new();
            IConversationSummaryStore summaries = new ScopedConversationSummaryStoreDecorator(
                backing, new DefaultAgentMemoryScopeProvider());

            summaries.SaveSummary("Teacher", "legacy-summary");

            Assert.AreEqual("legacy-summary", backing.LoadSummary("Teacher"));
        }

        /// <summary>
        /// The anonymous local default actor ("local", empty scope) is what every host gets for free.
        /// It names nobody, so it must never redirect memory away from the scope the host declared:
        /// when it did, writes made inside a turn went to the bare-role file while reads outside the turn
        /// used the learner's scoped file, and the lesson silently vanished from that learner's history.
        /// </summary>
        [Test]
        public void AmbientDefaultLocalActor_DoesNotOverrideHostDeclaredScope()
        {
            AgentMemoryScope hostScope = new("redoschool", "student-42", "lesson-1", "");
            FixedScopeProvider provider = new(hostScope);
            KeyCapturingStore store = new();
            ScopedAgentMemoryStoreDecorator decorator = new(store, provider);

            decorator.Save("Teacher", new AgentMemoryState());
            using (AgentMemoryScopeExecutionContext.Push(DefaultLocalActorContext("Teacher")))
            {
                decorator.Save("Teacher", new AgentMemoryState());
            }

            Assert.AreEqual(store.SavedKeys[0], store.SavedKeys[1],
                "Inside a turn the anonymous local actor must resolve the same key as outside it.");
            Assert.AreNotEqual("Teacher", store.SavedKeys[1],
                "A host-declared scope must survive the anonymous local actor default.");
        }

        [Test]
        public void AmbientDefaultLocalActor_WritesInsideTurn_StayInTheLearnerHistory()
        {
            FixedScopeProvider provider = new(new AgentMemoryScope("redoschool", "student-42", "lesson-1", ""));
            InMemoryCapabilityStore backing = new();
            ScopedAgentMemoryStoreDecorator decorator = new(backing, provider);

            using (AgentMemoryScopeExecutionContext.Push(DefaultLocalActorContext("Teacher")))
            {
                decorator.AppendChatMessage("Teacher", "assistant", "lesson turn", false);
            }

            ChatMessage[] history = decorator.GetChatHistory("Teacher");
            Assert.AreEqual(1, history.Length,
                "A turn written under the anonymous local actor must be readable from the learner scope.");
            Assert.AreEqual("lesson turn", history[0].Content);
        }

        [Test]
        public void AmbientNamedActor_StillOwnsItsDurableKey_RegardlessOfHostScope()
        {
            FixedScopeProvider provider = new(new AgentMemoryScope("redoschool", "student-42", "lesson-1", ""));
            KeyCapturingStore store = new();
            ScopedAgentMemoryStoreDecorator decorator = new(store, provider);
            LocalActorIdentityProvider named = new(
                "durable-actor",
                "connection-1",
                "",
                ActorGrantSet.None,
                AgentMemoryScope.Empty);

            using (AgentMemoryScopeExecutionContext.Push(named.GetActorContext("Teacher")))
            {
                decorator.Save("Teacher", new AgentMemoryState());
            }

            StringAssert.IsMatch("^actor-v1-[0-9a-f]{64}$", store.SavedKeys[0]);
        }

        [Test]
        public void AmbientDefaultLocalActor_CarryingItsOwnScope_UsesThatScope()
        {
            AgentMemoryScope turnScope = new("redoschool", "student-7", "lesson-9", "");
            FixedScopeProvider provider = new(new AgentMemoryScope("redoschool", "other-student", "", ""));
            KeyCapturingStore store = new();
            ScopedAgentMemoryStoreDecorator decorator = new(store, provider);
            LocalActorIdentityProvider local = new(
                LocalActorIdentityProvider.DefaultActorId,
                "connection-1",
                "",
                ActorGrantSet.None,
                turnScope);

            using (AgentMemoryScopeExecutionContext.Push(local.GetActorContext("Teacher")))
            {
                decorator.Save("Teacher", new AgentMemoryState());
            }

            Assert.AreEqual(ScopedKeyFor(turnScope, "Teacher"), store.SavedKeys[0],
                "A scope captured for the turn must win over the ambient host provider.");
        }

        [Test]
        public void AmbientDefaultLocalActor_WithoutAnyScope_KeepsLegacyBareRoleKey()
        {
            KeyCapturingStore store = new();
            ScopedAgentMemoryStoreDecorator decorator = new(store, new DefaultAgentMemoryScopeProvider());

            using (AgentMemoryScopeExecutionContext.Push(DefaultLocalActorContext("Teacher")))
            {
                decorator.Save("Teacher", new AgentMemoryState());
            }

            Assert.AreEqual("Teacher", store.SavedKeys[0],
                "With no scope anywhere the legacy bare-role file must still be the target.");
        }

        private static ActorContext DefaultLocalActorContext(string roleId)
        {
            LocalActorIdentityProvider provider = new(
                LocalActorIdentityProvider.DefaultActorId,
                "connection-1",
                "",
                ActorGrantSet.None,
                AgentMemoryScope.Empty);
            return provider.GetActorContext(roleId);
        }

        [Test]
        public void MemoryTranscriptAndSummary_UseSameCanonicalScopedKey()
        {
            AgentMemoryScope scope = new("tenant", "student/a", "lesson-1", "topic");
            FixedScopeProvider provider = new(scope);
            string memoryKey = ScopedKeyFor(scope, "Teacher");
            KeyCapturingConversationStore backing = new();
            IConversationTranscriptStore transcript = new ScopedConversationTranscriptStoreDecorator(backing, provider);
            IConversationSummaryStore summary = new ScopedConversationSummaryStoreDecorator(backing, provider);

            transcript.AppendTranscriptEntry("Teacher", new ConversationEntry(), false);
            summary.SaveSummary("Teacher", "summary");

            Assert.AreEqual(memoryKey, backing.TranscriptKey);
            Assert.AreEqual(memoryKey, backing.SummaryKey);
        }

        private static async Task IncrementConcurrently(
            IAtomicAgentMemoryStore atomic,
            string roleId,
            int count)
        {
            Task<int>[] mutations = Enumerable.Range(0, count)
                .Select(_ => atomic.MutateAsync(roleId, state =>
                {
                    int current = int.TryParse(state.Memory, out int value) ? value : 0;
                    Thread.SpinWait(4000);
                    int next = current + 1;
                    state.Memory = next.ToString();
                    return next;
                }))
                .ToArray();
            await Task.WhenAll(mutations);
        }

        private static string CreateTemporaryDirectory()
        {
            string path = Path.Combine(Path.GetTempPath(), "CoreAI-ScopedPersistence-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return path;
        }
    }

    /// <summary>Regression coverage for role-key normalization in <see cref="AgentMemoryPolicy"/>.</summary>
    public sealed class AgentMemoryPolicyEditModeTests
    {
        [Test]
        public void AddToolForRole_PaddedRoleId_AddsToTrimmedRole()
        {
            AgentMemoryPolicy policy = new();
            AgentMemory.MemoryLlmTool tool = new();

            policy.AddToolForRole(" trader ", tool);

            CollectionAssert.Contains(policy.GetToolsForRole("trader"), tool);
        }

        [Test]
        public void SetToolsForRole_PaddedRoleId_ReassertsRegisteredSkillMetaTools()
        {
            AgentMemoryPolicy policy = new();
            policy.AddSkillForRole(" trader ", SkillSet.FromTextContent("trade", "Trade", "instructions"));

            policy.SetToolsForRole(" trader ", Array.Empty<ILlmTool>());

            string[] names = policy.GetToolsForRole("trader").Select(tool => tool.Name).ToArray();
            CollectionAssert.Contains(names, "read_skill");
            CollectionAssert.Contains(names, "call_skill_tool");
        }
    }
}
