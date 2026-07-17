using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Ai;
using CoreAI.Infrastructure;
using CoreAI.Logging;
using Newtonsoft.Json;
using UnityEngine;

namespace CoreAI.Infrastructure.AiMemory
{
    /// <summary>
    /// File-backed Unity implementation of agent memory, chat history, and conversation transcripts.
    /// Data is stored below <see cref="Application.persistentDataPath"/> in the CoreAI folder so it
    /// survives scene reloads and player restarts.
    /// <para>
    /// WebGL player: same store under <see cref="CoreAILifetimeScope"/> (<b>v1.6.19+</b>); after writes calls
    /// <c>CoreAi_PersistFsSync</c> (<c>CoreAiPersistFs.jslib</c>) so IDBFS reaches IndexedDB on reload / tab close.
    /// The jslib runs <c>FS.syncfs</c> single-flight (<b>v1.7.2+</b>) so rapid successive writes do not overlap syncs.
    /// </para>
    /// </summary>
    public sealed class FileAgentMemoryStore : IAgentMemoryStore, IAtomicAgentMemoryStore,
        IConversationTranscriptStore, IDisposable
    {
#if UNITY_WEBGL && !UNITY_EDITOR
        [DllImport("__Internal")]
        private static extern void CoreAi_PersistFsSync();
#endif

        /// <summary>
        /// On WebGL pushes the in-memory IDBFS tree into IndexedDB so writes survive a reload.
        /// On other platforms this method compiles to a no-op.
        /// </summary>
        private static void PersistFsForWebGl()
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            try { CoreAi_PersistFsSync(); } catch { /* best-effort flush */ }
#endif
        }

        /// <summary>
        /// Process-wide mutation locks keyed by each role's persisted file path, one entry per distinct
        /// role id ever mutated. Entries are intentionally never evicted: a caller could already hold the
        /// <see cref="SemaphoreSlim"/> instance fetched from this dictionary while a concurrent
        /// eviction-then-<c>GetOrAdd</c> for the same key hands a second caller a fresh instance, which
        /// would silently break the mutual exclusion this lock exists for. In practice the key set is
        /// bounded by the number of distinct agent roles a host ever creates, which is small relative to
        /// process lifetime.
        /// </summary>
        private static readonly ConcurrentDictionary<string, SemaphoreSlim> MutationLocks =
            new(StringComparer.Ordinal);

        [Serializable]
        private sealed class Persisted
        {
            public string lastSystemPrompt;
            public string memory;
            public string chatHistoryJson; // WHY: Serialized chat history JSON payload for persistence.
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

        private readonly string _dir;
        private readonly Dictionary<string, List<ChatMessage>> _ephemeralHistory = new();
        private readonly Dictionary<string, List<ConversationEntry>> _transcripts = new();
        private readonly ILog _log;
        private readonly int _maxChatHistoryMessages;
        private readonly int _maxTranscriptEntries;

        /// <summary>
        /// Serializes all file and in-memory cache access for this store instance so the async
        /// thread-pool offloads cannot race the synchronous (main-thread) interface methods.
        /// SemaphoreSlim is not reentrant, so the gate is acquired only in public entry points;
        /// private *Core helpers assume the gate is already held.
        /// </summary>
        private readonly SemaphoreSlim _gate = new(1, 1);

        /// <summary>Creates a file-backed agent memory store under CoreAI persistent data.</summary>
        /// <param name="log">Optional logger.</param>
        /// <param name="rootDirectory">
        /// Optional override for the storage directory (used by tests); defaults to the CoreAI
        /// agent-memory folder under <see cref="Application.persistentDataPath"/>.
        /// </param>
        public FileAgentMemoryStore(ILog log = null, string rootDirectory = null,
            int maxChatHistoryMessages = 500, int maxTranscriptEntries = 2000)
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

        /// <inheritdoc />
        public bool TryLoad(string roleId, out AgentMemoryState state)
        {
            _gate.Wait();
            try
            {
                state = TryLoadCore(roleId);
                return state != null;
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
        public async Task<AgentMemoryState> TryLoadAsync(string roleId)
        {
            await _gate.WaitAsync();
            try
            {
                return await RunOffThread(() => TryLoadCore(roleId));
            }
            finally
            {
                _gate.Release();
            }
        }

        private AgentMemoryState TryLoadCore(string roleId)
        {
            try
            {
                string path = GetPath(roleId);
                if (!File.Exists(path))
                {
                    return null;
                }

                string json = File.ReadAllText(path);
                Persisted p = JsonUtility.FromJson<Persisted>(json);
                if (p == null)
                {
                    return null;
                }

                return new AgentMemoryState
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
            }
            catch (Exception ex)
            {
                _log?.Error($"[FileAgentMemoryStore] Failed to load memory for {roleId}: {ex}");
                return null;
            }
        }

        /// <inheritdoc />
        public void Save(string roleId, AgentMemoryState state)
        {
            SemaphoreSlim mutationGate = GetMutationGate(roleId);
            mutationGate.Wait();
            try
            {
                _gate.Wait();
                try
                {
                    SaveCore(roleId, state);
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
        /// Async variant of <see cref="Save"/> that performs the atomic file write on the thread pool.
        /// </summary>
        public async Task SaveAsync(string roleId, AgentMemoryState state)
        {
            SemaphoreSlim mutationGate = GetMutationGate(roleId);
            await mutationGate.WaitAsync();
            try
            {
                await _gate.WaitAsync();
                try
                {
                    await RunOffThread(() => SaveCore(roleId, state));
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

        /// <inheritdoc />
        public async Task<TResult> MutateAsync<TResult>(
            string roleId,
            Func<AgentMemoryState, TResult> mutator,
            CancellationToken cancellationToken = default)
        {
            if (mutator == null)
            {
                throw new ArgumentNullException(nameof(mutator));
            }

            SemaphoreSlim mutationGate = GetMutationGate(roleId);
            await mutationGate.WaitAsync(cancellationToken);
            try
            {
                await _gate.WaitAsync(cancellationToken);
                try
                {
                    return await RunOffThread(() =>
                    {
                        AgentMemoryState state = TryLoadCore(roleId) ?? new AgentMemoryState();
                        TResult result = mutator(state);
                        SaveCore(roleId, state);
                        return result;
                    });
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

        private void SaveCore(string roleId, AgentMemoryState state)
        {
            try
            {
                EnsureDir();
                string path = GetPath(roleId);
                Persisted p = new();

                if (File.Exists(path))
                {
                    string existingJson = File.ReadAllText(path);
                    p = JsonUtility.FromJson<Persisted>(existingJson) ?? new Persisted();
                }

                p.lastSystemPrompt = state.LastSystemPrompt;
                p.memory = state.Memory;
                p.systemPromptMemorySnapshot = state.SystemPromptMemorySnapshot;
                p.systemPromptMemoryVersion = state.SystemPromptMemoryVersion;
                p.maxMemoryVersions = state.MaxMemoryVersions;
                p.versionsJson = SerializeVersions(state.Versions);

                string newJson = JsonUtility.ToJson(p, true);
                AtomicWriteAllText(path, newJson);
                PersistFsForWebGl();
            }
            catch (Exception ex)
            {
                _log?.Error($"[FileAgentMemoryStore] Failed to save memory for {roleId}: {ex}");
            }
        }

        /// <inheritdoc />
        public void Clear(string roleId)
        {
            SemaphoreSlim mutationGate = GetMutationGate(roleId);
            mutationGate.Wait();
            try
            {
                _gate.Wait();
                try
                {
                    ClearCore(roleId);
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
        /// Async variant of <see cref="Clear"/> that performs the file rewrite on the thread pool.
        /// </summary>
        public async Task ClearAsync(string roleId)
        {
            SemaphoreSlim mutationGate = GetMutationGate(roleId);
            await mutationGate.WaitAsync();
            try
            {
                await _gate.WaitAsync();
                try
                {
                    await RunOffThread(() => ClearCore(roleId));
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

        private void ClearCore(string roleId)
        {
            try
            {
                string path = GetPath(roleId);
                if (File.Exists(path))
                {
                    string existingJson = File.ReadAllText(path);
                    Persisted p;
                    bool parseFailed = false;
                    try
                    {
                        p = JsonUtility.FromJson<Persisted>(existingJson);
                    }
                    catch (Exception ex)
                    {
                        _log?.Warn($"[FileAgentMemoryStore] Replacing corrupt memory for {roleId}: {ex.Message}");
                        p = null;
                    }

                    if (p == null)
                    {
                        _log?.Warn($"[FileAgentMemoryStore] Replacing unparseable memory for {roleId}.");
                        p = new Persisted();
                        parseFailed = true;
                    }

                    // WHY: the role file also carries persisted chat history/transcripts, so a memory
                    // clear must not destroy them. When nothing but memory lives in the file, the clear
                    // removes the role key entirely (the documented Clear contract; regression a883db00).
                    // A corrupt file is instead replaced with an explicit cleared marker so the next load
                    // does not resurrect garbage.
                    if (!parseFailed &&
                        !HasChatHistoryPayload(p.chatHistoryJson) &&
                        !HasTranscriptPayload(p.transcriptEntriesJson))
                    {
                        File.Delete(path);
                        PersistFsForWebGl();
                        return;
                    }

                    p.memory = "";
                    p.versionsJson = "";
                    p.systemPromptMemorySnapshot = "";
                    p.systemPromptMemoryVersion++;
                    AtomicWriteAllText(path, JsonUtility.ToJson(p, true));
                    PersistFsForWebGl();
                }
            }
            catch (Exception ex)
            {
                _log?.Error($"[FileAgentMemoryStore] Failed to clear memory for {roleId}: {ex}");
            }
        }

        /// <summary>True when the serialized chat-history wrapper actually carries messages.</summary>
        private static bool HasChatHistoryPayload(string serialized)
        {
            if (string.IsNullOrWhiteSpace(serialized))
            {
                return false;
            }

            try
            {
                ChatMessageArrayWrapper wrapper = JsonUtility.FromJson<ChatMessageArrayWrapper>(serialized);
                return wrapper.Items is { Length: > 0 };
            }
            catch
            {
                return true; // unknown format — err on preserving the file
            }
        }

        /// <summary>True when the serialized transcript list actually carries entries.</summary>
        private static bool HasTranscriptPayload(string serialized)
        {
            if (string.IsNullOrWhiteSpace(serialized))
            {
                return false;
            }

            try
            {
                List<ConversationEntry> entries =
                    JsonConvert.DeserializeObject<List<ConversationEntry>>(serialized, TranscriptJson);
                return entries is { Count: > 0 };
            }
            catch
            {
                return true; // unknown format — err on preserving the file
            }
        }

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
                    ClearChatHistoryCore(roleId);
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
        /// Async variant of <see cref="ClearChatHistory"/> that performs the file rewrite on the thread pool.
        /// </summary>
        public async Task ClearChatHistoryAsync(string roleId)
        {
            SemaphoreSlim mutationGate = GetMutationGate(roleId);
            await mutationGate.WaitAsync();
            try
            {
                await _gate.WaitAsync();
                try
                {
                    await RunOffThread(() => ClearChatHistoryCore(roleId));
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

        private void ClearChatHistoryCore(string roleId)
        {
            if (_ephemeralHistory.ContainsKey(roleId))
            {
                _ephemeralHistory.Remove(roleId);
            }

            if (_transcripts.ContainsKey(roleId))
            {
                _transcripts.Remove(roleId);
            }

            _loadedRoles.Remove(roleId); // WHY: re-sync _ephemeralHistory on next access after removing the list above

            // WHY: Wrap the following block with exception-safe behavior.
            try
            {
                string path = GetPath(roleId);
                if (File.Exists(path))
                {
                    string existingJson = File.ReadAllText(path);
                    Persisted p = JsonUtility.FromJson<Persisted>(existingJson);
                    if (p != null)
                    {
                        p.chatHistoryJson = "";
                        p.transcriptEntriesJson = "";
                        AtomicWriteAllText(path, JsonUtility.ToJson(p, true));
                        PersistFsForWebGl();
                    }
                }
            }
            catch (Exception ex)
            {
                _log?.Error($"[FileAgentMemoryStore] Failed to clear chat history for {roleId}: {ex}");
            }
        }

        private string GetPath(string roleId)
        {
            string combined = Path.Combine(_dir, $"{SanitizedFileStem(roleId)}.json");
            return EnsureWithinRoot(combined);
        }

        private SemaphoreSlim GetMutationGate(string roleId)
        {
            return MutationLocks.GetOrAdd(GetPath(roleId), _ => new SemaphoreSlim(1, 1));
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
        /// Releases the internal file-access semaphore.
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

            return JsonConvert.SerializeObject(versions, TranscriptJson);
        }

        /// <summary>
        /// Restores the memory version audit trail from its persisted JSON string. A null/blank or
        /// unparsable value yields null (no versions) rather than throwing, so a corrupt field cannot
        /// break loading the rest of the memory state.
        /// </summary>
        private static AgentMemoryVersionSnapshot[] DeserializeVersions(string versionsJson)
        {
            if (string.IsNullOrWhiteSpace(versionsJson))
            {
                return null;
            }

            try
            {
                return JsonConvert.DeserializeObject<AgentMemoryVersionSnapshot[]>(versionsJson, TranscriptJson);
            }
            catch
            {
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
            File.WriteAllText(tmpPath, contents);

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

        private readonly HashSet<string> _loadedRoles = new();

        private static readonly JsonSerializerSettings TranscriptJson = new() { Formatting = Formatting.Indented };

        #region Chat History Methods

        [Serializable]
        private struct ChatMessageArrayWrapper
        {
            public ChatMessage[] Items;
        }

        private void EnsureHistoryLoaded(string roleId)
        {
            if (_loadedRoles.Contains(roleId))
            {
                return;
            }

            _loadedRoles.Add(roleId);
            if (!_ephemeralHistory.ContainsKey(roleId))
            {
                _ephemeralHistory[roleId] = new List<ChatMessage>();
            }

            if (!_transcripts.ContainsKey(roleId))
            {
                _transcripts[roleId] = new List<ConversationEntry>();
            }

            try
            {
                string path = GetPath(roleId);
                if (!File.Exists(path))
                {
                    return;
                }

                string existingJson = File.ReadAllText(path);
                Persisted p = JsonUtility.FromJson<Persisted>(existingJson);
                if (p == null)
                {
                    return;
                }

                if (!string.IsNullOrEmpty(p.chatHistoryJson))
                {
                    ChatMessageArrayWrapper wrapper =
                        JsonUtility.FromJson<ChatMessageArrayWrapper>(p.chatHistoryJson);
                    if (wrapper.Items != null && wrapper.Items.Length > 0)
                    {
                        _ephemeralHistory[roleId].InsertRange(0, wrapper.Items);
                    }
                }

                if (!string.IsNullOrEmpty(p.transcriptEntriesJson))
                {
                    try
                    {
                        List<ConversationEntry> loaded =
                            JsonConvert.DeserializeObject<List<ConversationEntry>>(p.transcriptEntriesJson,
                                TranscriptJson);
                        if (loaded != null && loaded.Count > 0)
                        {
                            _transcripts[roleId].InsertRange(0, loaded);
                        }
                    }
                    catch (Exception tex)
                    {
                        _log?.Error($"[FileAgentMemoryStore] Transcript JSON for {roleId}: {tex}");
                    }
                }

                if (_transcripts[roleId].Count == 0 && _ephemeralHistory[roleId].Count > 0)
                {
                    MigrateTranscriptFromFlatHistory(roleId);
                }
            }
            catch (Exception ex)
            {
                _log?.Error(
                    $"[FileAgentMemoryStore] Failed to read chat history from disk for {roleId}: {ex}");
            }
        }

        private void MigrateTranscriptFromFlatHistory(string roleId)
        {
            foreach (ChatMessage m in _ephemeralHistory[roleId])
            {
                _transcripts[roleId].Add(new ConversationEntry
                {
                    Kind = MapSpeakerKind(m.Role),
                    Key = m.Role ?? "",
                    Content = m.Content ?? "",
                    Timestamp = m.Timestamp
                });
            }
        }

        private static ConversationEntryKind MapSpeakerKind(string role)
        {
            if (string.Equals(role, "assistant", StringComparison.OrdinalIgnoreCase))
            {
                return ConversationEntryKind.Assistant;
            }

            return ConversationEntryKind.User;
        }

        private void PersistRoleJsonFile(string roleId)
        {
            try
            {
                EnsureDir();
                string path = GetPath(roleId);
                Persisted p = new();
                if (File.Exists(path))
                {
                    string existingJson = File.ReadAllText(path);
                    p = JsonUtility.FromJson<Persisted>(existingJson) ?? new Persisted();
                }

                ChatMessageArrayWrapper wrapper = new() { Items = _ephemeralHistory[roleId].ToArray() };
                p.chatHistoryJson = JsonUtility.ToJson(wrapper);
                List<ConversationEntry> tlist = _transcripts.TryGetValue(roleId, out List<ConversationEntry> tl)
                    ? tl
                    : new List<ConversationEntry>();
                p.transcriptEntriesJson = JsonConvert.SerializeObject(tlist, TranscriptJson);

                string finalJson = JsonUtility.ToJson(p, true);
                AtomicWriteAllText(path, finalJson);
                PersistFsForWebGl();
            }
            catch (Exception ex)
            {
                _log?.Error($"[FileAgentMemoryStore] Failed to persist JSON for {roleId}: {ex}");
            }
        }

        /// <summary>
        /// Appends a chat message to the in-memory transcript and optionally persists it to disk.
        /// </summary>
        public void AppendChatMessage(string roleId, string role, string content, bool persistToDisk = true)
        {
            if (string.IsNullOrWhiteSpace(content))
            {
                return;
            }

            SemaphoreSlim mutationGate = GetMutationGate(roleId);
            mutationGate.Wait();
            try
            {
                _gate.Wait();
                try
                {
                    AppendChatMessageCore(roleId, role, content, persistToDisk);
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
        /// Async variant of <see cref="AppendChatMessage"/> that performs the load/persist file I/O
        /// on the thread pool.
        /// </summary>
        public async Task AppendChatMessageAsync(string roleId, string role, string content,
            bool persistToDisk = true)
        {
            if (string.IsNullOrWhiteSpace(content))
            {
                return;
            }

            SemaphoreSlim mutationGate = GetMutationGate(roleId);
            await mutationGate.WaitAsync();
            try
            {
                await _gate.WaitAsync();
                try
                {
                    await RunOffThread(() => AppendChatMessageCore(roleId, role, content, persistToDisk))
                        ;
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

        private void AppendChatMessageCore(string roleId, string role, string content, bool persistToDisk)
        {
            EnsureHistoryLoaded(roleId);

            ChatMessage newMsg = new()
            {
                Role = role,
                Content = content,
                Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            };

            _ephemeralHistory[roleId].Add(newMsg);

            _transcripts[roleId].Add(new ConversationEntry
            {
                Kind = MapSpeakerKind(role),
                Key = role ?? "",
                Content = content,
                Timestamp = newMsg.Timestamp
            });
            TrimToLatest(_ephemeralHistory[roleId], _maxChatHistoryMessages);
            TrimToLatest(_transcripts[roleId], _maxTranscriptEntries);

            if (persistToDisk)
            {
                PersistRoleJsonFile(roleId);
            }
        }

        /// <inheritdoc />
        public void AppendTranscriptEntry(string roleId, ConversationEntry entry, bool persistToDisk = true)
        {
            if (entry == null || string.IsNullOrWhiteSpace(roleId))
            {
                return;
            }

            SemaphoreSlim mutationGate = GetMutationGate(roleId);
            mutationGate.Wait();
            try
            {
                _gate.Wait();
                try
                {
                    AppendTranscriptEntryCore(roleId, entry, persistToDisk);
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
        /// Async variant of <see cref="AppendTranscriptEntry"/> that performs the load/persist file I/O
        /// on the thread pool.
        /// </summary>
        public async Task AppendTranscriptEntryAsync(string roleId, ConversationEntry entry,
            bool persistToDisk = true)
        {
            if (entry == null || string.IsNullOrWhiteSpace(roleId))
            {
                return;
            }

            SemaphoreSlim mutationGate = GetMutationGate(roleId);
            await mutationGate.WaitAsync();
            try
            {
                await _gate.WaitAsync();
                try
                {
                    await RunOffThread(() => AppendTranscriptEntryCore(roleId, entry, persistToDisk))
                        ;
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

        private void AppendTranscriptEntryCore(string roleId, ConversationEntry entry, bool persistToDisk)
        {
            EnsureHistoryLoaded(roleId);
            _transcripts[roleId].Add(entry);
            TrimToLatest(_transcripts[roleId], _maxTranscriptEntries);

            if (persistToDisk)
            {
                PersistRoleJsonFile(roleId);
            }
        }

        /// <inheritdoc />
        public IReadOnlyList<ConversationEntry> GetTranscriptEntries(string roleId, int maxEntries)
        {
            _gate.Wait();
            try
            {
                EnsureHistoryLoaded(roleId);
                if (!_transcripts.TryGetValue(roleId, out List<ConversationEntry> list) || list.Count == 0)
                {
                    return Array.Empty<ConversationEntry>();
                }

                int n = maxEntries > 0 ? Math.Min(maxEntries, list.Count) : list.Count;
                return list.Skip(list.Count - n).ToList();
            }
            finally
            {
                _gate.Release();
            }
        }

        private static void TrimToLatest<T>(List<T> list, int cap)
        {
            int removeCount = list.Count - cap;
            if (removeCount > 0)
            {
                list.RemoveRange(0, removeCount);
            }
        }

        /// <summary>
        /// Returns the latest chat messages for a role, optionally capped to the requested count.
        /// </summary>
        public ChatMessage[] GetChatHistory(string roleId, int maxMessages = 0)
        {
            _gate.Wait();
            try
            {
                EnsureHistoryLoaded(roleId);

                List<ChatMessage> list = _ephemeralHistory[roleId];
                if (maxMessages > 0 && list.Count > maxMessages)
                {
                    return list.Skip(list.Count - maxMessages).ToArray();
                }

                return list.ToArray();
            }
            finally
            {
                _gate.Release();
            }
        }

        #endregion
    }
}
