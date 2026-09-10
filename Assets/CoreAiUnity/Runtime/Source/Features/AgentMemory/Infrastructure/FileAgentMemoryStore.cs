using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Ai;
using CoreAI.Infrastructure;
using CoreAI.Logging;
using Cysharp.Threading.Tasks;
using Newtonsoft.Json;
using UnityEngine;

namespace CoreAI.Infrastructure.AiMemory
{
    /// <summary>
    /// What <see cref="FileAgentMemoryStore"/> cut from one role's conversation, and how much, once that
    /// conversation hit its cap. The role key is not passed along: it may be a student's scoped key, and
    /// this event goes to the host's log.
    /// </summary>
    public readonly struct AgentHistoryTrimmedEventArgs
    {
        internal AgentHistoryTrimmedEventArgs(int droppedChatMessages, int droppedTranscriptEntries,
            int retainedChatMessages, int retainedTranscriptEntries, int maxChatHistoryMessages,
            int maxTranscriptEntries)
        {
            DroppedChatMessages = droppedChatMessages;
            DroppedTranscriptEntries = droppedTranscriptEntries;
            RetainedChatMessages = retainedChatMessages;
            RetainedTranscriptEntries = retainedTranscriptEntries;
            MaxChatHistoryMessages = maxChatHistoryMessages;
            MaxTranscriptEntries = maxTranscriptEntries;
        }

        /// <summary>How many of the oldest chat messages this trim removed from the history.</summary>
        public int DroppedChatMessages { get; }

        /// <summary>How many transcript rows (chat messages included) this trim removed.</summary>
        public int DroppedTranscriptEntries { get; }

        /// <summary>How many chat messages are left.</summary>
        public int RetainedChatMessages { get; }

        /// <summary>How many transcript rows are left.</summary>
        public int RetainedTranscriptEntries { get; }

        /// <summary>The chat message cap in force.</summary>
        public int MaxChatHistoryMessages { get; }

        /// <summary>The transcript row cap in force.</summary>
        public int MaxTranscriptEntries { get; }
    }

    /// <summary>
    /// File-backed Unity implementation of agent memory, chat history, and conversation transcripts.
    /// Data is stored below <see cref="Application.persistentDataPath"/> in the CoreAI folder so it
    /// survives scene reloads and player restarts.
    /// <para>
    /// <b>On-disk layout (v2).</b> Two files per role: <c>&lt;stem&gt;.json</c> is the memory document
    /// (text, versions, system-prompt snapshot; rewritten in full only when memory is mutated), and
    /// <c>&lt;stem&gt;.history.jsonl</c> is the conversation, one JSON line per record. A chat message and
    /// its transcript row are ONE record carrying the <c>m</c> flag, not two copies of the same text.
    /// Adding a message appends a single line at the end instead of read-parse-serialize-replace over the
    /// entire file. A file in the old format (the conversation living inside the memory document in the
    /// <c>chatHistoryJson</c> / <c>transcriptEntriesJson</c> fields) is migrated the first time the role's
    /// conversation is touched.
    /// </para>
    /// <para>
    /// <b>Caps.</b> The conversation is trimmed down to <c>maxChatHistoryMessages</c> chat messages and
    /// <c>maxTranscriptEntries</c> transcript rows. Trimming is visible: a <see cref="HistoryTrimmed"/>
    /// event on every trim, plus a log line (the first time for a role, and from then on at every file
    /// compaction). The file is compacted not on every message, but once <see cref="CompactionSlack"/>
    /// already-trimmed lines have piled up at its head; on read the surplus head lines are discarded, so
    /// readers never see more than the cap.
    /// </para>
    /// <para>
    /// <b>Honest writes.</b> The store tells "no file" (<see cref="AgentMemoryLoadStatus.NotFound"/>) apart
    /// from "the file is there but unreadable" (<see cref="AgentMemoryLoadStatus.Failed"/>): an atomic
    /// mutation over an unreadable document throws <see cref="AgentMemoryLoadException"/> and does NOT
    /// overwrite it with an empty one. Writing the memory document does not depend on reading the old file,
    /// so a corrupt document is healed by the very first <see cref="Save"/> or <see cref="Clear"/>; a
    /// truncated line in the conversation file is skipped with a warning and erased at the next compaction.
    /// <c>persistToDisk=false</c> means "in this process only": such a record NEVER reaches the disk, not
    /// even during compaction.
    /// </para>
    /// <para>
    /// <b>WebGL.</b> <c>persistentDataPath</c> is the tab's in-memory filesystem; the ENGINE carries it
    /// into IndexedDB, provided the page passes <c>config.autoSyncPersistentDataPath = true</c> to
    /// <c>createUnityInstance()</c>. Both the synchronous and the asynchronous methods therefore get the
    /// same immediate durability answer from <see cref="CoreAiWebGlPersistence"/>: the async ones
    /// (<see cref="SaveAsync"/>, <see cref="MutateAsync{TResult}"/>, <see cref="AppendChatMessageAsync"/>
    /// and the rest) throw <see cref="IOException"/> when the page has no durable storage armed, and the
    /// synchronous ones log it. Nothing here waits for a browser callback, because the browser has none
    /// to give; <see cref="FlushAsync"/> is kept as the explicit "is my data covered?" query.
    /// </para>
    /// <para>
    /// <b><c>ConfigureAwait(false)</c> is REQUIRED on the gate awaits in this file</b> (see the WHY on
    /// <see cref="_gate"/>) and forbidden everywhere else in it. That is not an exception carved out of
    /// the project-wide rule for convenience: it is the one place the rule's premise does not hold, and
    /// the deadlock the flag prevents reproduces in the editor, not only in a browser.
    /// Historical note, so the next reader does not repeat the diagnosis: these occurrences were removed
    /// on 2026-09-09 while chasing a <c>memory action=write</c> that hung until the 30 s tool timeout.
    /// A rebuilt player proved they were NOT the cause — the cause was the durability confirmation
    /// itself, an <c>FS.syncfs</c> callback Unity 6.3 never delivers — so removing them bought nothing
    /// and cost the deadlock guard. They are back, and the guard's allowlist carries the reason.
    /// </para>
    /// </summary>
    public sealed class FileAgentMemoryStore : IAgentMemoryStore, IAgentMemoryLoadDiagnostics,
        IAtomicAgentMemoryStore, IConversationTranscriptStore, IDisposable
    {
        /// <summary>Default chat message cap.</summary>
        public const int DefaultMaxChatHistoryMessages = 500;

        /// <summary>Default transcript row cap.</summary>
        public const int DefaultMaxTranscriptEntries = 2000;

        /// <summary>
        /// Process-wide mutation locks keyed by each role's persisted memory file path, one entry per
        /// distinct role id ever mutated. Entries are intentionally never evicted: a caller could already
        /// hold the <see cref="SemaphoreSlim"/> instance fetched from this dictionary while a concurrent
        /// eviction-then-<c>GetOrAdd</c> for the same key hands a second caller a fresh instance, which
        /// would silently break the mutual exclusion this lock exists for. In practice the key set is
        /// bounded by the number of distinct agent roles a host ever creates, which is small relative to
        /// process lifetime.
        /// </summary>
        private static readonly ConcurrentDictionary<string, SemaphoreSlim> MutationLocks =
            new(StringComparer.Ordinal);
        private static readonly ConcurrentDictionary<string, long> HistoryRevisions =
            new(StringComparer.Ordinal);
        private static readonly ConcurrentDictionary<string, long> HistoryClearRevisions =
            new(StringComparer.Ordinal);
        private static readonly ConcurrentDictionary<string, long> HistoryOrders =
            new(StringComparer.Ordinal);

        /// <summary>The role's on-disk memory document (JsonUtility; the field names are the file contract).</summary>
        [Serializable]
        private sealed class Persisted
        {
            public string lastSystemPrompt;
            public string memory;

            // WHY: v1-format fields - the conversation used to live inside the memory document. They are
            // read only to migrate it into <stem>.history.jsonl and are always empty afterwards; never
            // write to them.
            public string chatHistoryJson;
            public string transcriptEntriesJson;

            // WHY: Memory versioning + stable-prefix snapshot. Persisted so the documented rollback feature
            // (ListVersions/Revert) and the system-prompt tail-update prompt-cache optimization survive a
            // reload; the orchestrator re-reads state from disk every request, so dropping these silently
            // disables both. Versions are stored as a JSON string (JsonConvert) since JsonUtility cannot
            // round-trip an array of reference-typed objects through a string field reliably.
            public string versionsJson;
            public string systemPromptMemorySnapshot;
            public int systemPromptMemoryVersion;
            public int maxMemoryVersions;
        }

        /// <summary>One line of the conversation file. The short names are the file format, not a style choice.</summary>
        private sealed class HistoryLine
        {
            [JsonProperty("k")] public int Kind;
            [JsonProperty("r")] public string Key;
            [JsonProperty("c")] public string Content;
            [JsonProperty("id", NullValueHandling = NullValueHandling.Ignore)] public string CallId;
            [JsonProperty("t")] public long Timestamp;
            // Additive ordering metadata: legacy rows default to zero and retain their file order.
            // Unlike Unix seconds, this distinguishes concurrent turns and session-only rows.
            [JsonProperty("o", DefaultValueHandling = DefaultValueHandling.Ignore)] public long Order;

            /// <summary>The row is at the same time a message of the flat chat history.</summary>
            [JsonProperty("m")] public bool IsChatMessage;

            /// <summary>
            /// In this process only (<c>persistToDisk=false</c>): the row lives in memory and is never
            /// written to the file. Not serialized - by definition no such row exists on disk.
            /// </summary>
            [JsonIgnore] public bool Persisted = true;
            [JsonIgnore] public bool PendingWrite;

            /// <summary>
            /// The row is inside the chat window (the last <c>maxChatHistoryMessages</c> messages).
            /// Recomputed on every load, hence not serialized.
            /// </summary>
            [JsonIgnore] public bool InChatView;

            /// <summary>The row is inside the transcript window (the last <c>maxTranscriptEntries</c> rows).</summary>
            [JsonIgnore] public bool InTranscriptView;
        }

        /// <summary>One role's loaded conversation plus the bookkeeping of what its file holds.</summary>
        private sealed class RoleHistory
        {
            public readonly List<HistoryLine> Lines = new();

            /// <summary>How many chat messages are currently inside the chat window.</summary>
            public int ChatCount;

            /// <summary>How many rows are currently inside the transcript window.</summary>
            public int TranscriptCount;

            /// <summary>How many rows in <see cref="Lines"/> are meant for the disk.</summary>
            public int PersistedCount;

            /// <summary>How many rows physically sit in the file, already-trimmed and unreadable ones included.</summary>
            public int FileLineCount;

            /// <summary>
            /// The file must be rewritten from memory on the next write: it holds unreadable lines, or an
            /// append failed and the file's contents diverge from memory.
            /// </summary>
            public bool NeedsRewrite;

            /// <summary>
            /// The file exists, but reading it failed (I/O, not parsing). Its contents are unknown, so it
            /// must not be rewritten from memory - that would erase what we never saw. Appending is safe.
            /// </summary>
            public bool LoadFailed;

            /// <summary>The role's first trim has already been noted in the log.</summary>
            public bool TrimLogged;
            public long Revision;
        }

        private static readonly JsonSerializerSettings HistoryJson = new()
        {
            Formatting = Formatting.None,
            NullValueHandling = NullValueHandling.Ignore
        };

        private static readonly JsonSerializerSettings CompactJson = new() { Formatting = Formatting.None };

        private readonly string _dir;
        private readonly Dictionary<string, RoleHistory> _histories = new(StringComparer.Ordinal);
        private readonly ILog _log;
        private readonly int _maxChatHistoryMessages;
        private readonly int _maxTranscriptEntries;

        /// <summary>
        /// Serializes all file and in-memory cache access for this store instance so the async
        /// thread-pool offloads cannot race the synchronous (main-thread) interface methods.
        /// SemaphoreSlim is not reentrant, so the gate is acquired only in public entry points;
        /// private *Core helpers assume the gate is already held. Nothing that truly yields the thread may
        /// be awaited under this lock: on WebGL the synchronous methods take it with a blocking
        /// <c>Wait()</c>, and waiting for a browser callback under the lock would stall the single thread.
        /// <para>
        /// WHY every <c>await</c> that acquires or releases <see cref="_gate"/> or a mutation gate (see
        /// <see cref="GetMutationGate"/>) uses <c>.ConfigureAwait(false)</c>: without it, a continuation
        /// that resumes off the calling thread (real <c>Task.Run</c> in <see cref="RunOffThread(Action)"/>
        /// off WebGL) is posted back to whatever <see cref="SynchronizationContext"/> was captured when
        /// the async method was entered. If that call came from Unity's main thread, the posted
        /// continuation — which is what releases <see cref="_gate"/> / the mutation gate — sits in the
        /// main-thread queue. A synchronous <c>Save</c>/<c>TryLoad</c>/<c>GetTranscriptEntries</c> call
        /// on that same main thread blocks on the same gate via <c>Wait()</c> and never returns to pump
        /// that queue: permanent deadlock, no exception. This is NOT the WebGL-pool hazard
        /// <c>WebGlUnsafeAsyncPrimitivesEditModeTests</c> normally bans <c>ConfigureAwait(false)</c> for
        /// (see its allowlist entry for this file): on WebGL, <see cref="RunOffThread(Action)"/> runs the
        /// action inline and returns an already-completed task, and the gate-acquire → work → gate-release
        /// chain never yields before both gates are released (nothing else runs on WebGL's single
        /// cooperative thread to contend for them mid-chain), so every await in that chain is already
        /// complete when awaited there — <c>ConfigureAwait(false)</c> never causes a continuation to be
        /// scheduled at all, let alone onto a thread pool WebGL doesn't have.
        /// </para>
        /// </summary>
        private readonly SemaphoreSlim _gate = new(1, 1);

        /// <summary>Creates a file-backed agent memory store under CoreAI persistent data.</summary>
        /// <param name="log">Optional logger.</param>
        /// <param name="rootDirectory">
        /// Optional override for the storage directory (used by tests); defaults to the CoreAI
        /// agent-memory folder under <see cref="Application.persistentDataPath"/>.
        /// </param>
        /// <param name="maxChatHistoryMessages">Per-role chat message cap.</param>
        /// <param name="maxTranscriptEntries">Per-role transcript row cap.</param>
        public FileAgentMemoryStore(ILog log = null, string rootDirectory = null,
            int maxChatHistoryMessages = DefaultMaxChatHistoryMessages,
            int maxTranscriptEntries = DefaultMaxTranscriptEntries)
        {
            _dir = !string.IsNullOrWhiteSpace(rootDirectory)
                ? rootDirectory.Trim()
                : Path.Combine(Application.persistentDataPath, CoreAiPersistentPaths.RootFolderName,
                    CoreAiPersistentPaths.AgentMemory);
            _log = log;
            _maxChatHistoryMessages = Math.Max(1, maxChatHistoryMessages);
            _maxTranscriptEntries = Math.Max(1, maxTranscriptEntries);
        }

        /// <summary>
        /// Raised after every conversation trim, already outside the store's internal locks, so a handler
        /// is free to call back into the store. It complements the log line rather than replacing it.
        /// </summary>
        public event Action<AgentHistoryTrimmedEventArgs> HistoryTrimmed;

        /// <summary>The chat message cap in force.</summary>
        public int MaxChatHistoryMessages => _maxChatHistoryMessages;

        /// <summary>The transcript row cap in force.</summary>
        public int MaxTranscriptEntries => _maxTranscriptEntries;

        /// <summary>
        /// How many already-trimmed lines may pile up at the head of the conversation file before it is
        /// compacted. Lower means more full rewrites, higher means more junk on disk; readers never see
        /// that junk.
        /// </summary>
        internal int CompactionSlack => Math.Max(16, _maxTranscriptEntries / 8);

        /// <summary>
        /// Runs file I/O on the thread pool so large reads/writes do not stall the Unity main thread.
        /// On WebGL (no threads) the work runs inline instead.
        /// </summary>
        private static Task RunOffThread(Action action)
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            action();
            return Task.CompletedTask;
#else
            return Task.Run(action);
#endif
        }

        /// <inheritdoc cref="RunOffThread(Action)"/>
        private static Task<T> RunOffThread<T>(Func<T> func)
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            return Task.FromResult(func());
#else
            return Task.Run(func);
#endif
        }

        /// <summary>
        /// Reports whether everything written so far is covered by durable storage. Completes
        /// immediately on every platform. <c>false</c> means a browser page without Unity's automatic
        /// <c>persistentDataPath</c> persistence: those writes will not survive a reload.
        /// </summary>
        public Task<bool> FlushAsync(CancellationToken cancellationToken = default)
        {
            return CoreAiWebGlPersistence.SyncAsync(cancellationToken).AsTask();
        }

        /// <summary>
        /// Durability check for the async paths. Called AFTER the locks are released so a synchronous
        /// caller on the single WebGL thread is never blocked behind it.
        /// </summary>
        private static async Task ConfirmDurableAsync(CancellationToken cancellationToken)
        {
            bool durable = await CoreAiWebGlPersistence.SyncAsync(cancellationToken);
            if (!durable)
            {
                throw new IOException(
                    "[FileAgentMemoryStore] The write reached the in-memory filesystem, but this browser page " +
                    "has no durable storage armed, so it will not survive a reload.");
            }
        }

        #region Memory document

        /// <inheritdoc />
        public bool TryLoad(string roleId, out AgentMemoryState state)
        {
            return TryLoadDetailed(roleId, out state) == AgentMemoryLoadStatus.Loaded;
        }

        /// <inheritdoc />
        public AgentMemoryLoadStatus TryLoadDetailed(string roleId, out AgentMemoryState state)
        {
            _gate.Wait();
            try
            {
                return TryLoadDetailedCore(roleId, out state);
            }
            finally
            {
                _gate.Release();
            }
        }

        /// <summary>
        /// Async variant of <see cref="TryLoad"/> that performs the file read on the thread pool.
        /// Returns the loaded state, or <c>null</c> when no memory exists for the role.
        /// </summary>
        /// <exception cref="AgentMemoryLoadException">The document exists, but reading it failed.</exception>
        public async Task<AgentMemoryState> TryLoadAsync(string roleId)
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                return await RunOffThread(() =>
                {
                    AgentMemoryLoadStatus status = TryLoadDetailedCore(roleId, out AgentMemoryState state);
                    if (status == AgentMemoryLoadStatus.Failed)
                    {
                        throw new AgentMemoryLoadException(roleId);
                    }

                    return state;
                }).ConfigureAwait(false);
            }
            finally
            {
                _gate.Release();
            }
        }

        private AgentMemoryLoadStatus TryLoadDetailedCore(string roleId, out AgentMemoryState state)
        {
            state = null;
            string path = GetMemoryPath(roleId);
            if (!File.Exists(path))
            {
                return AgentMemoryLoadStatus.NotFound;
            }

            try
            {
                Persisted p = ReadPersisted(path);
                state = new AgentMemoryState
                {
                    LastSystemPrompt = p.lastSystemPrompt ?? "",
                    Memory = p.memory ?? "",
                    SystemPromptMemorySnapshot = p.systemPromptMemorySnapshot ?? "",
                    SystemPromptMemoryVersion = p.systemPromptMemoryVersion,
                    MaxMemoryVersions = p.maxMemoryVersions > 0
                        ? p.maxMemoryVersions
                        : AgentMemoryState.DefaultMaxMemoryVersions,
                    Versions = DeserializeVersions(p.versionsJson)
                };
                return AgentMemoryLoadStatus.Loaded;
            }
            catch (Exception ex)
            {
                // WHY: the file IS there, but what is inside it is unknown. This is not "no memory": a
                // caller that takes emptiness for the truth and saves would erase the document and every
                // version it holds.
                LogStorageFailure("load memory", ex);
                return AgentMemoryLoadStatus.Failed;
            }
        }

        /// <summary>Reads the memory document; any defect in the file is an exception, not an empty document.</summary>
        private static Persisted ReadPersisted(string path)
        {
            string json = File.ReadAllText(path);
            Persisted p = JsonUtility.FromJson<Persisted>(json);
            if (p == null)
            {
                throw new InvalidDataException("Memory document is empty or is not a JSON object.");
            }

            return p;
        }

        /// <inheritdoc />
        /// <remarks>
        /// Best-effort, as its <c>void</c> contract implies: a write failure goes to the log and no exception
        /// is propagated - the orchestrator calls this method for the prompt cache in the middle of a turn,
        /// and a disk failure must not bring that turn down. Callers who need the write's outcome use
        /// <see cref="SaveAsync"/> or <see cref="MutateAsync{TResult}"/>.
        /// </remarks>
        public void Save(string roleId, AgentMemoryState state)
        {
            SemaphoreSlim mutationGate = GetMutationGate(roleId);
            mutationGate.Wait();
            try
            {
                _gate.Wait();
                try
                {
                    SaveCore(roleId, state, true);
                }
                catch (Exception ex)
                {
                    LogStorageFailure("save memory", ex);
                }
                finally
                {
                    _gate.Release();
                }
            }
            finally
            {
                mutationGate.Release();
            }
        }

        /// <summary>
        /// Async variant of <see cref="Save"/> that performs the atomic file write on the thread pool and
        /// returns only after the write is durable (on WebGL — confirmed by the browser).
        /// </summary>
        /// <exception cref="IOException">The write failed or was not confirmed.</exception>
        public async Task SaveAsync(string roleId, AgentMemoryState state, CancellationToken cancellationToken = default)
        {
            SemaphoreSlim mutationGate = GetMutationGate(roleId);
            await mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    await RunOffThread(() => SaveCore(roleId, state, false)).ConfigureAwait(false);
                }
                finally
                {
                    _gate.Release();
                }
            }
            finally
            {
                mutationGate.Release();
            }

            await ConfirmDurableAsync(cancellationToken);
        }

        /// <inheritdoc />
        /// <remarks>
        /// Honours the contract literally: the mutation's result is on disk (on WebGL - confirmed by the
        /// browser) BEFORE the method returns that result. Any failure is an exception, not a quiet log line.
        /// </remarks>
        /// <exception cref="AgentMemoryLoadException">
        /// The role's document exists but is unreadable: the mutator does not run and the document is left
        /// untouched.
        /// </exception>
        /// <exception cref="IOException">The write failed or was not confirmed.</exception>
        public async Task<TResult> MutateAsync<TResult>(
            string roleId,
            Func<AgentMemoryState, TResult> mutator,
            CancellationToken cancellationToken = default)
        {
            if (mutator == null)
            {
                throw new ArgumentNullException(nameof(mutator));
            }

            TResult result;
            SemaphoreSlim mutationGate = GetMutationGate(roleId);
            await mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    result = await RunOffThread(() =>
                    {
                        AgentMemoryLoadStatus status = TryLoadDetailedCore(roleId, out AgentMemoryState state);
                        if (status == AgentMemoryLoadStatus.Failed)
                        {
                            // WHY: this used to read `TryLoadCore(roleId) ?? new AgentMemoryState()`: a
                            // transient read failure turned into a mutation of an EMPTY document, and then
                            // a successful write replaced the real memory and all of its versions with it.
                            throw new AgentMemoryLoadException(roleId);
                        }

                        state ??= new AgentMemoryState();
                        TResult mutated = mutator(state);
                        SaveCore(roleId, state, false);
                        return mutated;
                    }).ConfigureAwait(false);
                }
                finally
                {
                    _gate.Release();
                }
            }
            finally
            {
                mutationGate.Release();
            }

            await ConfirmDurableAsync(cancellationToken);
            return result;
        }

        /// <summary>
        /// Writes the memory document out of <paramref name="state"/>. The old file is NOT read: the whole
        /// content of the document lives in the state, so a corrupt file is simply overwritten. Exceptions
        /// propagate - whether to swallow them is up to the caller and its own contract.
        /// </summary>
        private void SaveCore(string roleId, AgentMemoryState state, bool queueFlush)
        {
            state ??= new AgentMemoryState();
            // WHY: a v1-format file may still carry the conversation in the document's fields. Move it out
            // into its own file BEFORE rewriting the document - otherwise Save would erase the student's
            // history along with those fields.
            EnsureHistoryLoaded(roleId);
            EnsureDir();
            Persisted p = new()
            {
                lastSystemPrompt = state.LastSystemPrompt,
                memory = state.Memory,
                systemPromptMemorySnapshot = state.SystemPromptMemorySnapshot,
                systemPromptMemoryVersion = state.SystemPromptMemoryVersion,
                maxMemoryVersions = state.MaxMemoryVersions,
                versionsJson = SerializeVersions(state.Versions)
            };
            AtomicWriteAllText(GetMemoryPath(roleId), JsonUtility.ToJson(p));
            if (queueFlush)
            {
                QueueFlush();
            }
        }

        /// <inheritdoc />
        /// <remarks>
        /// Deletes the role's memory document (text, versions, prompt snapshot); the conversation lives in
        /// its own file and is left untouched. An unreadable document is deleted too: this is the only
        /// explicit way to give a role working memory back after its file was corrupted, and afterwards
        /// <see cref="TryLoadDetailed"/> honestly answers <see cref="AgentMemoryLoadStatus.NotFound"/>.
        /// </remarks>
        public void Clear(string roleId)
        {
            SemaphoreSlim mutationGate = GetMutationGate(roleId);
            mutationGate.Wait();
            try
            {
                _gate.Wait();
                try
                {
                    ClearCore(roleId, true);
                }
                catch (Exception ex)
                {
                    LogStorageFailure("clear memory", ex);
                }
                finally
                {
                    _gate.Release();
                }
            }
            finally
            {
                mutationGate.Release();
            }
        }

        /// <summary>
        /// Async variant of <see cref="Clear"/> that performs the file removal on the thread pool and
        /// returns after it is durable.
        /// </summary>
        /// <exception cref="IOException">The removal failed or was not confirmed.</exception>
        public async Task ClearAsync(string roleId, CancellationToken cancellationToken = default)
        {
            SemaphoreSlim mutationGate = GetMutationGate(roleId);
            await mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    await RunOffThread(() => ClearCore(roleId, false)).ConfigureAwait(false);
                }
                finally
                {
                    _gate.Release();
                }
            }
            finally
            {
                mutationGate.Release();
            }

            await ConfirmDurableAsync(cancellationToken);
        }

        private void ClearCore(string roleId, bool queueFlush)
        {
            // WHY: first move the conversation out of the v1 file (if it lives there), and only then delete
            // the document.
            EnsureHistoryLoaded(roleId);
            string path = GetMemoryPath(roleId);
            if (!File.Exists(path))
            {
                return;
            }

            if (TryLoadDetailedCore(roleId, out _) == AgentMemoryLoadStatus.Failed)
            {
                _log?.Warn("[FileAgentMemoryStore] Removing an unreadable memory document on clear.");
            }

            File.Delete(path);
            if (queueFlush)
            {
                QueueFlush();
            }
        }

        #endregion

        #region Conversation history

        /// <inheritdoc />
        public void ClearChatHistory(string roleId)
        {
            SemaphoreSlim mutationGate = GetMutationGate(roleId);
            mutationGate.Wait();
            try
            {
                _gate.Wait();
                try
                {
                    ClearChatHistoryCore(roleId, true);
                }
                catch (Exception ex)
                {
                    LogStorageFailure("clear chat history", ex);
                }
                finally
                {
                    _gate.Release();
                }
            }
            finally
            {
                mutationGate.Release();
            }
        }

        /// <summary>
        /// Async variant of <see cref="ClearChatHistory"/> that performs the file removal on the thread
        /// pool and returns after it is durable.
        /// </summary>
        /// <exception cref="IOException">The removal failed or was not confirmed.</exception>
        public async Task ClearChatHistoryAsync(string roleId, CancellationToken cancellationToken = default)
        {
            SemaphoreSlim mutationGate = GetMutationGate(roleId);
            await mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    await RunOffThread(() => ClearChatHistoryCore(roleId, false)).ConfigureAwait(false);
                }
                finally
                {
                    _gate.Release();
                }
            }
            finally
            {
                mutationGate.Release();
            }

            await ConfirmDurableAsync(cancellationToken);
        }

        private void ClearChatHistoryCore(string roleId, bool queueFlush)
        {
            // WHY: the v1 migration must happen before the delete - otherwise the conversation would stay
            // in the memory document's fields and "resurrect" itself on the next access.
            EnsureHistoryLoaded(roleId);
            // A previous migration may have written history but failed to remove the legacy fields.
            // Remove those fields before deleting history, or the next store would resurrect it.
            string memoryPath = GetMemoryPath(roleId);
            if (File.Exists(memoryPath))
            {
                Persisted legacy = ReadPersisted(memoryPath);
                if (!string.IsNullOrEmpty(legacy.chatHistoryJson) ||
                    !string.IsNullOrEmpty(legacy.transcriptEntriesJson))
                {
                    legacy.chatHistoryJson = null;
                    legacy.transcriptEntriesJson = null;
                    AtomicWriteAllText(memoryPath, JsonUtility.ToJson(legacy));
                }
            }

            string path = GetHistoryPath(roleId);
            if (!File.Exists(path))
            {
                _histories.Remove(roleId);
                HistoryClearRevisions[GetHistoryPath(roleId)] = AdvanceHistoryRevision(roleId);
                return;
            }

            // WHY: the in-memory copy is dropped only after the file delete succeeded. The other way round
            // (forget first, delete second) turned a disk failure into a "successful" reset: reads returned
            // nothing while the file stayed put, and the history "resurrected" itself on the next load.
            File.Delete(path);
            _histories.Remove(roleId);
            HistoryClearRevisions[GetHistoryPath(roleId)] = AdvanceHistoryRevision(roleId);
            if (queueFlush)
            {
                QueueFlush();
            }
        }

        /// <summary>
        /// Appends a chat message to the role's conversation. With <paramref name="persistToDisk"/> =
        /// <c>false</c> the message lives in this process only and never reaches the disk.
        /// </summary>
        /// <remarks>Best-effort, as its <c>void</c> contract implies: a write failure goes to the log and
        /// the file is rewritten from memory on the next successful write. For a confirmed write use
        /// <see cref="AppendChatMessageAsync"/>.</remarks>
        public void AppendChatMessage(string roleId, string role, string content, bool persistToDisk = true)
        {
            if (string.IsNullOrWhiteSpace(content))
            {
                return;
            }

            // WHY: same as AppendTranscriptEntry: an empty role id would yield the shared stem
            // ".history.jsonl", where unrelated callers would mix together, and a null would break the call
            // with an exception out of SanitizedFileStem.
            if (string.IsNullOrWhiteSpace(roleId))
            {
                return;
            }

            AgentHistoryTrimmedEventArgs? trimmed = null;
            SemaphoreSlim mutationGate = GetMutationGate(roleId);
            mutationGate.Wait();
            try
            {
                _gate.Wait();
                try
                {
                    trimmed = AppendLineCore(roleId, ChatLine(role, content), persistToDisk, true, true);
                }
                finally
                {
                    _gate.Release();
                }
            }
            finally
            {
                mutationGate.Release();
            }

            RaiseTrimmed(trimmed);
        }

        /// <summary>
        /// Async variant of <see cref="AppendChatMessage"/> that performs the file I/O on the thread pool
        /// and, when <paramref name="persistToDisk"/> is set, returns only after the write is durable.
        /// </summary>
        /// <exception cref="IOException">The write failed or was not confirmed.</exception>
        public async Task AppendChatMessageAsync(string roleId, string role, string content,
            bool persistToDisk = true, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(content))
            {
                return;
            }

            // WHY: same as AppendChatMessage: an empty role id routes to nobody's file.
            if (string.IsNullOrWhiteSpace(roleId))
            {
                return;
            }

            await AppendLineAsync(roleId, ChatLine(role, content), persistToDisk, cancellationToken);
        }

        /// <inheritdoc />
        /// <remarks>Best-effort, as its <c>void</c> contract implies, like <see cref="AppendChatMessage"/>.</remarks>
        public void AppendTranscriptEntry(string roleId, ConversationEntry entry, bool persistToDisk = true)
        {
            if (entry == null || string.IsNullOrWhiteSpace(roleId))
            {
                return;
            }

            AgentHistoryTrimmedEventArgs? trimmed = null;
            SemaphoreSlim mutationGate = GetMutationGate(roleId);
            mutationGate.Wait();
            try
            {
                _gate.Wait();
                try
                {
                    trimmed = AppendLineCore(roleId, TranscriptLine(entry), persistToDisk, true, true);
                }
                finally
                {
                    _gate.Release();
                }
            }
            finally
            {
                mutationGate.Release();
            }

            RaiseTrimmed(trimmed);
        }

        /// <summary>
        /// Async variant of <see cref="AppendTranscriptEntry"/> that performs the file I/O on the thread
        /// pool and, when <paramref name="persistToDisk"/> is set, returns only after the write is durable.
        /// </summary>
        /// <exception cref="IOException">The write failed or was not confirmed.</exception>
        public async Task AppendTranscriptEntryAsync(string roleId, ConversationEntry entry,
            bool persistToDisk = true, CancellationToken cancellationToken = default)
        {
            if (entry == null || string.IsNullOrWhiteSpace(roleId))
            {
                return;
            }

            await AppendLineAsync(roleId, TranscriptLine(entry), persistToDisk, cancellationToken);
        }

        private async Task AppendLineAsync(string roleId, HistoryLine line, bool persistToDisk,
            CancellationToken cancellationToken)
        {
            SynchronizationContext callbackContext = SynchronizationContext.Current;
            AgentHistoryTrimmedEventArgs? trimmed;
            SemaphoreSlim mutationGate = GetMutationGate(roleId);
            await mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    trimmed = await RunOffThread(() => AppendLineCore(roleId, line, persistToDisk, false, false))
                        .ConfigureAwait(false);
                }
                finally
                {
                    _gate.Release();
                }
            }
            finally
            {
                mutationGate.Release();
            }

            await RaiseTrimmedAsync(trimmed, callbackContext);
            if (persistToDisk)
            {
                await ConfirmDurableAsync(cancellationToken);
            }
        }

        private static HistoryLine ChatLine(string role, string content)
        {
            // WHY: the timestamp unit is the same one the ChatMessage constructor and InMemoryAgentMemoryStore
            // use (Unix seconds); the file store used to write milliseconds, so the unit depended on which
            // implementation you got.
            ChatMessage message = new(role ?? "", content);
            return new HistoryLine
            {
                Kind = (int)MapSpeakerKind(message.Role),
                Key = message.Role,
                Content = message.Content,
                Timestamp = message.Timestamp,
                IsChatMessage = true
            };
        }

        private static HistoryLine TranscriptLine(ConversationEntry entry)
        {
            return new HistoryLine
            {
                Kind = (int)entry.Kind,
                Key = entry.Key ?? "",
                Content = entry.Content ?? "",
                CallId = string.IsNullOrEmpty(entry.CallId) ? null : entry.CallId,
                Timestamp = entry.Timestamp,
                IsChatMessage = false
            };
        }

        /// <summary>
        /// Adds a row to the role's in-memory history and, when that row is meant for the disk, to the file
        /// as well. Returns a description of the trim (if one happened) so that the caller raises the event
        /// outside the locks.
        /// </summary>
        /// <param name="swallowWriteFailure">
        /// The synchronous contract: a write failure goes to the log and to
        /// <see cref="RoleHistory.NeedsRewrite"/>. The async contract propagates the exception instead.
        /// </param>
        private AgentHistoryTrimmedEventArgs? AppendLineCore(string roleId, HistoryLine line, bool persistToDisk,
            bool queueFlush, bool swallowWriteFailure)
        {
            RoleHistory previous = EnsureHistoryLoaded(roleId);
            // Trimming mutates line flags as well as counts. Work on a private candidate so a failed
            // confirmed append leaves both the previous cache and the durable view unchanged.
            RoleHistory history = CopyHistory(previous);
            long lastOrder = 0;
            foreach (HistoryLine retained in history.Lines)
            {
                lastOrder = Math.Max(lastOrder, retained.Order);
            }
            line.Order = HistoryOrders.AddOrUpdate(GetHistoryPath(roleId), lastOrder + 1,
                (_, current) => Math.Max(current, lastOrder) + 1);
            line.Persisted = persistToDisk;
            Track(history, line);

            AgentHistoryTrimmedEventArgs? trimmed = TrimRetained(history);
            // WHY: a role's first trim goes to the log right away, without waiting for a file compaction:
            // otherwise the first lost turns (including those of a role whose history lives in the process
            // only) disappear silently.
            if (!persistToDisk)
            {
                if (trimmed.HasValue && !history.TrimLogged)
                {
                    LogTrim(history, trimmed.Value);
                }
                _histories[roleId] = history;
                return trimmed;
            }

            try
            {
                bool compactNow = !history.LoadFailed &&
                                  (history.NeedsRewrite ||
                                   history.FileLineCount - history.PersistedCount >= CompactionSlack);
                if (compactNow)
                {
                    RewriteHistoryFile(roleId, history);
                }
                else
                {
                    AppendHistoryLine(roleId, line);
                    history.FileLineCount++;
                }

                if (trimmed.HasValue && (compactNow || !history.TrimLogged))
                {
                    LogTrim(history, trimmed.Value);
                }

                foreach (HistoryLine retained in history.Lines)
                {
                    retained.PendingWrite = false;
                }
                history.Revision = AdvanceHistoryRevision(roleId);
                _histories[roleId] = history;

                if (queueFlush)
                {
                    QueueFlush();
                }
            }
            catch (Exception ex) when (swallowWriteFailure)
            {
                // WHY: the row is already in memory and marked as meant for the disk; the next successful
                // write rewrites the whole file from memory and puts the row back on disk.
                history.NeedsRewrite = true;
                line.PendingWrite = true;
                _histories[roleId] = history;
                if (trimmed.HasValue && !history.TrimLogged)
                {
                    LogTrim(history, trimmed.Value);
                }
                LogStorageFailure("append conversation history", ex);
            }

            return trimmed;
        }

        private static RoleHistory CopyHistory(RoleHistory source)
        {
            RoleHistory copy = new()
            {
                ChatCount = source.ChatCount,
                TranscriptCount = source.TranscriptCount,
                PersistedCount = source.PersistedCount,
                FileLineCount = source.FileLineCount,
                NeedsRewrite = source.NeedsRewrite,
                LoadFailed = source.LoadFailed,
                TrimLogged = source.TrimLogged,
                Revision = source.Revision
            };
            foreach (HistoryLine line in source.Lines)
            {
                copy.Lines.Add(new HistoryLine
                {
                    Kind = line.Kind, Key = line.Key, Content = line.Content, CallId = line.CallId,
                    Timestamp = line.Timestamp, Order = line.Order, IsChatMessage = line.IsChatMessage,
                    Persisted = line.Persisted, PendingWrite = line.PendingWrite,
                    InChatView = line.InChatView, InTranscriptView = line.InTranscriptView
                });
            }
            return copy;
        }

        /// <summary>Puts a new (or just-loaded) row into both windows and updates the counters.</summary>
        private static void Track(RoleHistory history, HistoryLine line)
        {
            line.InChatView = line.IsChatMessage;
            line.InTranscriptView = true;
            history.Lines.Add(line);
            history.TranscriptCount++;
            if (line.IsChatMessage)
            {
                history.ChatCount++;
            }

            if (line.Persisted)
            {
                history.PersistedCount++;
            }
        }

        /// <summary>
        /// Maintains two windows over one list: the last <see cref="MaxChatHistoryMessages"/> chat messages
        /// and the last <see cref="MaxTranscriptEntries"/> transcript rows. Falling out of a window is what
        /// the reader sees, and that is exactly what counts as a trim and reaches the event; a row is
        /// physically removed only once neither window needs it any more. A chat message older than the
        /// transcript window stays alive until it falls out of the chat window - precisely how the two
        /// independent copies used to live.
        /// </summary>
        private AgentHistoryTrimmedEventArgs? TrimRetained(RoleHistory history)
        {
            List<HistoryLine> lines = history.Lines;
            int droppedChat = 0;
            for (int i = 0; i < lines.Count && history.ChatCount > _maxChatHistoryMessages; i++)
            {
                if (!lines[i].InChatView)
                {
                    continue;
                }

                lines[i].InChatView = false;
                history.ChatCount--;
                droppedChat++;
            }

            int droppedTranscript = 0;
            for (int i = 0; i < lines.Count && history.TranscriptCount > _maxTranscriptEntries; i++)
            {
                if (!lines[i].InTranscriptView)
                {
                    continue;
                }

                lines[i].InTranscriptView = false;
                history.TranscriptCount--;
                droppedTranscript++;
            }

            if (droppedChat == 0 && droppedTranscript == 0)
            {
                return null;
            }

            int write = 0;
            for (int i = 0; i < lines.Count; i++)
            {
                HistoryLine line = lines[i];
                if (line.InChatView || line.InTranscriptView)
                {
                    lines[write++] = line;
                    continue;
                }

                if (line.Persisted)
                {
                    history.PersistedCount--;
                }
            }

            lines.RemoveRange(write, lines.Count - write);
            return new AgentHistoryTrimmedEventArgs(
                droppedChat,
                droppedTranscript,
                history.ChatCount,
                history.TranscriptCount,
                _maxChatHistoryMessages,
                _maxTranscriptEntries);
        }

        private void LogTrim(RoleHistory history, AgentHistoryTrimmedEventArgs trimmed)
        {
            history.TrimLogged = true;
            _log?.Warn(
                "[FileAgentMemoryStore] Conversation history of one role reached its cap: " +
                $"{trimmed.DroppedChatMessages} chat message(s) / {trimmed.DroppedTranscriptEntries} transcript " +
                $"row(s) dropped (caps: {_maxChatHistoryMessages} chat messages, {_maxTranscriptEntries} transcript " +
                "rows). Older turns are gone from this role's history; raise the caps on CoreAILifetimeScope " +
                "(SetConversationHistoryCaps) if the host must keep them.");
        }

        private void RaiseTrimmed(AgentHistoryTrimmedEventArgs? trimmed)
        {
            if (trimmed.HasValue)
            {
                HistoryTrimmed?.Invoke(trimmed.Value);
            }
        }

        private Task RaiseTrimmedAsync(AgentHistoryTrimmedEventArgs? trimmed, SynchronizationContext callbackContext)
        {
            // WHY: Disk IO and lock release must not wait for Unity's main thread: a synchronous
            // read there could be waiting for these locks. Only host callbacks return to that context,
            // after the locks are free. WebGL executes IO inline and retains its original context.
            if (!trimmed.HasValue || callbackContext == null ||
                ReferenceEquals(callbackContext, SynchronizationContext.Current))
            {
                RaiseTrimmed(trimmed);
                return Task.CompletedTask;
            }

            // WHY: no RunContinuationsAsynchronously — it forces the continuation onto the thread pool,
            // which does not exist in WebGL and hangs forever. Inlining is safe here: TrySetResult runs
            // inside the Post callback above, after both gates are already released (see the WHY above).
            TaskCompletionSource<bool> completion = new();
            callbackContext.Post(_ =>
            {
                try
                {
                    RaiseTrimmed(trimmed);
                    completion.TrySetResult(true);
                }
                catch (Exception error)
                {
                    completion.TrySetException(error);
                }
            }, null);
            return completion.Task;
        }

        /// <inheritdoc />
        public IReadOnlyList<ConversationEntry> GetTranscriptEntries(string roleId, int maxEntries)
        {
            SemaphoreSlim mutationGate = GetMutationGate(roleId);
            mutationGate.Wait();
            _gate.Wait();
            try
            {
                RoleHistory history = EnsureHistoryLoaded(roleId);
                List<HistoryLine> lines = history.Lines;
                // WHY: rows outside the transcript window are always older than rows inside it (rows leave
                // the window from the head and the order never changes), so the window is the tail of the
                // list, TranscriptCount rows long.
                int take = maxEntries > 0 ? Math.Min(maxEntries, history.TranscriptCount) : history.TranscriptCount;
                if (take == 0)
                {
                    return Array.Empty<ConversationEntry>();
                }

                List<ConversationEntry> result = new(take);
                for (int i = lines.Count - take; i < lines.Count; i++)
                {
                    HistoryLine line = lines[i];
                    result.Add(new ConversationEntry
                    {
                        Kind = (ConversationEntryKind)line.Kind,
                        Key = line.Key ?? "",
                        Content = line.Content ?? "",
                        CallId = line.CallId ?? "",
                        Timestamp = line.Timestamp
                    });
                }

                return result;
            }
            finally
            {
                _gate.Release();
                mutationGate.Release();
            }
        }

        /// <summary>
        /// Returns the latest chat messages for a role, optionally capped to the requested count.
        /// </summary>
        public ChatMessage[] GetChatHistory(string roleId, int maxMessages = 0)
        {
            SemaphoreSlim mutationGate = GetMutationGate(roleId);
            mutationGate.Wait();
            _gate.Wait();
            try
            {
                RoleHistory history = EnsureHistoryLoaded(roleId);
                int take = maxMessages > 0 ? Math.Min(maxMessages, history.ChatCount) : history.ChatCount;
                if (take == 0)
                {
                    return Array.Empty<ChatMessage>();
                }

                ChatMessage[] result = new ChatMessage[take];
                int write = take - 1;
                for (int i = history.Lines.Count - 1; i >= 0 && write >= 0; i--)
                {
                    HistoryLine line = history.Lines[i];
                    if (!line.InChatView)
                    {
                        continue;
                    }

                    result[write--] = new ChatMessage
                    {
                        Role = line.Key,
                        Content = line.Content,
                        Timestamp = line.Timestamp
                    };
                }

                return result;
            }
            finally
            {
                _gate.Release();
                mutationGate.Release();
            }
        }

        /// <summary>
        /// Loads the role's conversation on first access: from <c>.history.jsonl</c>, or, when that file
        /// does not exist, by migrating it out of the v1-format memory document's fields.
        /// </summary>
        private RoleHistory EnsureHistoryLoaded(string roleId)
        {
            long revision = HistoryRevisions.GetOrAdd(GetHistoryPath(roleId), 0);
            if (_histories.TryGetValue(roleId, out RoleHistory cached) && cached.Revision == revision)
            {
                return cached;
            }

            RoleHistory history = new() { Revision = revision, TrimLogged = cached?.TrimLogged ?? false };
            string historyPath = GetHistoryPath(roleId);
            if (File.Exists(historyPath))
            {
                LoadHistoryFile(history, historyPath);
            }
            else
            {
                MigrateLegacyConversation(roleId, history);
            }

            if (history.LoadFailed && cached != null)
            {
                // A peer changed the file but it is temporarily unreadable. Retain our recoverable
                // cache and retry on the next access; never overwrite an unknown disk snapshot.
                throw new IOException("The current conversation history could not be reloaded.");
            }

            long clearedAt = HistoryClearRevisions.GetOrAdd(GetHistoryPath(roleId), 0);
            if (cached != null && cached.Revision >= clearedAt)
            {
                foreach (HistoryLine local in cached.Lines)
                {
                    if (!local.Persisted || local.PendingWrite)
                    {
                        Track(history, local);
                        // Insert the local row at its original position relative to peer commits.
                        // Moving only this row preserves the order of legacy rows with order zero.
                        int index = history.Lines.Count - 1;
                        while (index > 0 && history.Lines[index - 1].Order > local.Order)
                        {
                            history.Lines[index] = history.Lines[index - 1];
                            index--;
                        }
                        history.Lines[index] = local;
                        history.NeedsRewrite |= local.PendingWrite;
                    }
                }
            }

            TrimRetained(history);
            _histories[roleId] = history;
            return history;
        }

        private void LoadHistoryFile(RoleHistory history, string path)
        {
            string[] rawLines;
            try
            {
                rawLines = File.ReadAllLines(path);
            }
            catch (Exception ex)
            {
                // WHY: the file's contents are unknown - it must not be rewritten from memory (see LoadFailed).
                history.LoadFailed = true;
                LogStorageFailure("read conversation history", ex);
                return;
            }

            int unreadable = 0;
            foreach (string raw in rawLines)
            {
                if (string.IsNullOrWhiteSpace(raw))
                {
                    continue;
                }

                history.FileLineCount++;
                HistoryLine line = null;
                try
                {
                    line = JsonConvert.DeserializeObject<HistoryLine>(raw, HistoryJson);
                }
                catch (JsonException)
                {
                    line = null;
                }

                if (line == null)
                {
                    unreadable++;
                    continue;
                }

                line.Persisted = true;
                Track(history, line);
            }

            if (unreadable > 0)
            {
                // WHY: a truncated line is the trace of an interrupted syncfs or a partial write. It is
                // skipped, the rest is read, and the file is rewritten from scratch on the next write.
                history.NeedsRewrite = true;
                _log?.Warn(
                    $"[FileAgentMemoryStore] Skipped {unreadable} unreadable line(s) in one role's conversation " +
                    "history; the file will be rewritten on the next write.");
            }
        }

        /// <summary>
        /// The v1 format stored the conversation inside the memory document twice: the flat chat in
        /// <c>chatHistoryJson</c> (JsonUtility) and the transcript in <c>transcriptEntriesJson</c>
        /// (Newtonsoft). This merges them into a single list of rows: a transcript entry matching the next
        /// chat message by role, text and timestamp IS that same message (one row carrying the chat flag);
        /// everything else becomes a transcript-only row. Chat without a transcript (the oldest format)
        /// becomes chat rows.
        /// </summary>
        private void MigrateLegacyConversation(string roleId, RoleHistory history)
        {
            string memoryPath = GetMemoryPath(roleId);
            if (!File.Exists(memoryPath))
            {
                return;
            }

            Persisted p;
            try
            {
                p = ReadPersisted(memoryPath);
            }
            catch (Exception ex)
            {
                // WHY: the v1 document does not read - the conversation inside it is just as unreachable as
                // the memory. Leave the document alone: TryLoadDetailed reports its state (Failed).
                LogStorageFailure("migrate legacy conversation", ex);
                return;
            }

            if (string.IsNullOrEmpty(p.chatHistoryJson) && string.IsNullOrEmpty(p.transcriptEntriesJson))
            {
                return;
            }

            List<ChatMessage> chat = new();
            if (!string.IsNullOrEmpty(p.chatHistoryJson))
            {
                try
                {
                    ChatMessageArrayWrapper wrapper = JsonUtility.FromJson<ChatMessageArrayWrapper>(p.chatHistoryJson);
                    if (wrapper.Items != null)
                    {
                        chat.AddRange(wrapper.Items);
                    }
                }
                catch (Exception ex)
                {
                    LogStorageFailure("parse legacy chat history", ex);
                }
            }

            List<ConversationEntry> transcript = new();
            if (!string.IsNullOrEmpty(p.transcriptEntriesJson))
            {
                try
                {
                    List<ConversationEntry> loaded =
                        JsonConvert.DeserializeObject<List<ConversationEntry>>(p.transcriptEntriesJson);
                    if (loaded != null)
                    {
                        transcript.AddRange(loaded);
                    }
                }
                catch (Exception ex)
                {
                    LogStorageFailure("parse legacy transcript", ex);
                }
            }

            int chatCursor = 0;
            foreach (ConversationEntry entry in transcript)
            {
                if (entry == null)
                {
                    continue;
                }

                bool isChat = chatCursor < chat.Count && SameMessage(chat[chatCursor], entry);
                if (isChat)
                {
                    chatCursor++;
                }

                Track(history, new HistoryLine
                {
                    Kind = (int)entry.Kind,
                    Key = entry.Key ?? "",
                    Content = entry.Content ?? "",
                    CallId = string.IsNullOrEmpty(entry.CallId) ? null : entry.CallId,
                    Timestamp = NormalizeToUnixSeconds(entry.Timestamp),
                    IsChatMessage = isChat
                });
            }

            for (; chatCursor < chat.Count; chatCursor++)
            {
                ChatMessage message = chat[chatCursor];
                Track(history, new HistoryLine
                {
                    Kind = (int)MapSpeakerKind(message.Role),
                    Key = message.Role ?? "",
                    Content = message.Content ?? "",
                    Timestamp = NormalizeToUnixSeconds(message.Timestamp),
                    IsChatMessage = true
                });
            }

            TrimRetained(history);
            RewriteHistoryFile(roleId, history);

            // WHY: the conversation now lives in its own file; the memory document is rewritten without the
            // v1 fields so that the migration does not repeat itself and Save does not carry them further.
            p.chatHistoryJson = null;
            p.transcriptEntriesJson = null;
            AtomicWriteAllText(memoryPath, JsonUtility.ToJson(p));
            QueueFlush();
            _log?.Info("[FileAgentMemoryStore] Migrated one role's conversation from the v1 memory document " +
                       "into its own history file.");
        }

        private static bool SameMessage(ChatMessage message, ConversationEntry entry)
        {
            return string.Equals(message.Role ?? "", entry.Key ?? "", StringComparison.Ordinal) &&
                   string.Equals(message.Content ?? "", entry.Content ?? "", StringComparison.Ordinal) &&
                   NormalizeToUnixSeconds(message.Timestamp) == NormalizeToUnixSeconds(entry.Timestamp);
        }

        /// <summary>
        /// The v1 format wrote milliseconds into chat timestamps. Unix seconds will not pass 10^11 for
        /// another three thousand years, while milliseconds crossed it back in 1973 - the boundary is
        /// unambiguous.
        /// </summary>
        private static long NormalizeToUnixSeconds(long timestamp)
        {
            return timestamp > 100_000_000_000L ? timestamp / 1000L : timestamp;
        }

        private static ConversationEntryKind MapSpeakerKind(string role)
        {
            if (string.Equals(role, "assistant", StringComparison.OrdinalIgnoreCase))
            {
                return ConversationEntryKind.Assistant;
            }

            return ConversationEntryKind.User;
        }

        [Serializable]
        private struct ChatMessageArrayWrapper
        {
            public ChatMessage[] Items;
        }

        private void AppendHistoryLine(string roleId, HistoryLine line)
        {
            EnsureDir();
            byte[] bytes = new UTF8Encoding(false).GetBytes(JsonConvert.SerializeObject(line, HistoryJson) + "\n");
            using FileStream file = new(GetHistoryPath(roleId), FileMode.OpenOrCreate, FileAccess.Write, FileShare.Read);
            long originalLength = file.Length;
            try
            {
                file.Position = originalLength;
                file.Write(bytes, 0, bytes.Length);
                file.Flush();
            }
            catch
            {
                // A failed write may have appended a partial JSON record. Restore the last durable
                // boundary before allowing a retry, otherwise a later append could duplicate it.
                file.SetLength(originalLength);
                throw;
            }
        }

        /// <summary>Rewrites the conversation file from memory: only the rows meant for the disk.</summary>
        private void RewriteHistoryFile(string roleId, RoleHistory history)
        {
            EnsureDir();
            StringBuilder sb = new(history.PersistedCount * 128);
            foreach (HistoryLine line in history.Lines)
            {
                if (!line.Persisted)
                {
                    continue;
                }

                sb.Append(JsonConvert.SerializeObject(line, HistoryJson)).Append('\n');
            }

            AtomicWriteAllText(GetHistoryPath(roleId), sb.ToString());
            history.FileLineCount = history.PersistedCount;
            history.NeedsRewrite = false;
        }

        #endregion

        #region Paths, files, logging

        private string GetMemoryPath(string roleId)
        {
            return EnsureWithinRoot(Path.Combine(_dir, $"{SanitizedFileStem(roleId)}.json"));
        }

        private string GetHistoryPath(string roleId)
        {
            return EnsureWithinRoot(Path.Combine(_dir, $"{SanitizedFileStem(roleId)}.history.jsonl"));
        }

        private SemaphoreSlim GetMutationGate(string roleId)
        {
            return MutationLocks.GetOrAdd(GetMemoryPath(roleId), _ => new SemaphoreSlim(1, 1));
        }

        private long AdvanceHistoryRevision(string roleId)
        {
            return HistoryRevisions.AddOrUpdate(GetHistoryPath(roleId), 1, (_, current) => current + 1);
        }

        /// <summary>
        /// Guards against path traversal: <see cref="Path.GetInvalidFileNameChars"/> does NOT strip
        /// <c>..</c>, so a role id like <c>..</c> or <c>../x</c> could resolve outside <see cref="_dir"/>
        /// and read/write/delete arbitrary files. Reject any combined path that escapes the store root.
        /// </summary>
        private string EnsureWithinRoot(string combinedPath)
        {
            string rootFull = Path.GetFullPath(_dir);
            string rootPrefix = rootFull.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal)
                ? rootFull
                : rootFull + Path.DirectorySeparatorChar;
            string fullPath = Path.GetFullPath(combinedPath);
            if (!fullPath.StartsWith(rootPrefix, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "[FileAgentMemoryStore] Role id resolves outside the store root; rejected to prevent path traversal.");
            }

            return fullPath;
        }

        /// <summary>
        /// Releases the internal file-access semaphore. The store has no deferred writes: everything that
        /// was meant for the disk is already there (on WebGL - at least queued for the flush).
        /// </summary>
        public void Dispose()
        {
            _gate.Dispose();
        }

        /// <summary>
        /// Maps a raw role id to a unique file stem. Invalid filename characters are replaced, and
        /// when the replacement changed anything a short hash of the raw id is appended so distinct
        /// ids like "A/B" and "A_B" cannot collide on the same file.
        /// </summary>
        private static string SanitizedFileStem(string roleId)
        {
            string safe = string.Join("_", roleId.Split(Path.GetInvalidFileNameChars()));
            if (string.Equals(safe, roleId, StringComparison.Ordinal))
            {
                return safe;
            }

            uint hash = 2166136261u;
            foreach (char c in roleId)
            {
                hash = (hash ^ c) * 16777619u;
            }

            return $"{safe}_{hash:x8}";
        }

        private void EnsureDir()
        {
            if (!Directory.Exists(_dir))
            {
                Directory.CreateDirectory(_dir);
            }
        }

        /// <summary>
        /// Serializes the memory version audit trail to a JSON string for storage in the
        /// JsonUtility-backed <see cref="Persisted"/> DTO. Returns "" when there are no versions so the
        /// persisted file stays compact.
        /// </summary>
        private static string SerializeVersions(AgentMemoryVersionSnapshot[] versions)
        {
            if (versions == null || versions.Length == 0)
            {
                return "";
            }

            return JsonConvert.SerializeObject(versions, CompactJson);
        }

        /// <summary>
        /// Restores the memory version audit trail from its persisted JSON string. An unreadable version
        /// trail does not break reading the document (the memory text matters more), but it does not stay
        /// silent either: it goes to the log, because the next write of the document will no longer keep it.
        /// </summary>
        private AgentMemoryVersionSnapshot[] DeserializeVersions(string versionsJson)
        {
            if (string.IsNullOrWhiteSpace(versionsJson))
            {
                return null;
            }

            try
            {
                return JsonConvert.DeserializeObject<AgentMemoryVersionSnapshot[]>(versionsJson);
            }
            catch (Exception ex)
            {
                LogStorageFailure("parse memory versions", ex, true);
                return null;
            }
        }

        /// <summary>
        /// Writes <paramref name="contents"/> to <paramref name="path"/> atomically by writing to a temp
        /// file first and then swapping it into place, so a crash mid-write cannot corrupt the existing file.
        /// </summary>
        private static void AtomicWriteAllText(string path, string contents)
        {
            string dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            string tmpPath = path + ".tmp";
            File.WriteAllText(tmpPath, contents, Encoding.UTF8);

            try
            {
                if (File.Exists(path))
                {
                    File.Replace(tmpPath, path, null);
                }
                else
                {
                    File.Move(tmpPath, path);
                }
            }
            catch
            {
                if (File.Exists(tmpPath))
                {
                    try
                    {
                        File.Delete(tmpPath);
                    }
                    catch
                    {
                        /* best-effort cleanup */
                    }
                }

                throw;
            }
        }

        /// <summary>
        /// Checks the durability answer after a synchronous write. A <c>void</c> interface method
        /// cannot fail the caller, so a page without durable storage is reported to the log; the async
        /// entry points throw for the same condition.
        /// </summary>
        private void QueueFlush()
        {
            if (!CoreAiWebGlPersistence.Sync())
            {
                _log?.Warn("[FileAgentMemoryStore] This browser page has no durable storage armed; the last " +
                           "write will not survive a reload.");
            }
        }

        private void LogStorageFailure(string operation, Exception ex, bool warning = false)
        {
            // WHY: roleId may be a scoped persistence key and exception messages commonly repeat the
            // filesystem path. Report only the operation and exception kind so logs disclose neither.
            string message = $"[FileAgentMemoryStore] Failed to {operation} ({ex.GetType().Name}).";
            if (warning)
            {
                _log?.Warn(message);
            }
            else
            {
                _log?.Error(message);
            }
        }

        #endregion
    }
}
