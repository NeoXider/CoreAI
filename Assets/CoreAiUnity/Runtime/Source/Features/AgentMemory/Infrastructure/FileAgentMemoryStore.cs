using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
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
    public sealed class FileAgentMemoryStore : IAgentMemoryStore, IConversationTranscriptStore
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

        [Serializable]
        private sealed class Persisted
        {
            public string lastSystemPrompt;
            public string memory;
            public string chatHistoryJson; // Serialized chat history JSON payload for persistence.
            public string transcriptEntriesJson;
        }

        private readonly string _dir;
        private readonly Dictionary<string, List<ChatMessage>> _ephemeralHistory = new();
        private readonly Dictionary<string, List<ConversationEntry>> _transcripts = new();
        private readonly ILog _log;

        /// <summary>Creates a file-backed agent memory store under CoreAI persistent data.</summary>
        public FileAgentMemoryStore(ILog log = null)
        {
            _dir = Path.Combine(Application.persistentDataPath, CoreAiPersistentPaths.RootFolderName,
                CoreAiPersistentPaths.AgentMemory);
            _log = log;
        }

        /// <inheritdoc />
        public bool TryLoad(string roleId, out AgentMemoryState state)
        {
            state = null;
            try
            {
                string path = GetPath(roleId);
                if (!File.Exists(path))
                {
                    return false;
                }

                string json = File.ReadAllText(path);
                Persisted p = JsonUtility.FromJson<Persisted>(json);
                if (p == null)
                {
                    return false;
                }

                state = new AgentMemoryState
                {
                    LastSystemPrompt = p.lastSystemPrompt ?? "",
                    Memory = p.memory ?? ""
                };
                return true;
            }
            catch (Exception ex)
            {
                _log?.Error($"[FileAgentMemoryStore] Failed to load memory for {roleId}: {ex.Message}");
                return false;
            }
        }

        /// <inheritdoc />
        public void Save(string roleId, AgentMemoryState state)
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

                string newJson = JsonUtility.ToJson(p, true);
                File.WriteAllText(path, newJson);
                PersistFsForWebGl();
            }
            catch (Exception ex)
            {
                _log?.Error($"[FileAgentMemoryStore] Failed to save memory for {roleId}: {ex.Message}");
            }
        }

        /// <inheritdoc />
        public void Clear(string roleId)
        {
            try
            {
                string path = GetPath(roleId);
                if (File.Exists(path))
                {
                    string existingJson = File.ReadAllText(path);
                    Persisted p = JsonUtility.FromJson<Persisted>(existingJson);
                    if (p != null)
                    {
                        p.memory = "";
                        p.lastSystemPrompt =
                            ""; // Clear previous system prompt cache entry to avoid leaking across sessions.
                        File.WriteAllText(path, JsonUtility.ToJson(p, true));
                        PersistFsForWebGl();
                    }
                }
            }
            catch (Exception ex)
            {
                _log?.Error($"[FileAgentMemoryStore] Failed to clear memory for {roleId}: {ex.Message}");
            }
        }

        /// <inheritdoc />
        public void ClearChatHistory(string roleId)
        {
            if (_ephemeralHistory.ContainsKey(roleId))
            {
                _ephemeralHistory.Remove(roleId);
            }

            if (_transcripts.ContainsKey(roleId))
            {
                _transcripts.Remove(roleId);
            }

            _loadedRoles.Remove(roleId); // re-sync _ephemeralHistory on next access after removing the list above

            // Wrap the following block with exception-safe behavior.
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
                        File.WriteAllText(path, JsonUtility.ToJson(p, true));
                        PersistFsForWebGl();
                    }
                }
            }
            catch (Exception ex)
            {
                _log?.Error($"[FileAgentMemoryStore] Failed to clear chat history for {roleId}: {ex.Message}");
            }
        }

        private string GetPath(string roleId)
        {
            // Resolve and cache required local values.
            string safeName = string.Join("_", roleId.Split(Path.GetInvalidFileNameChars()));
            return Path.Combine(_dir, $"{safeName}.json");
        }

        private void EnsureDir()
        {
            if (!Directory.Exists(_dir))
            {
                Directory.CreateDirectory(_dir);
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
                        _log?.Error($"[FileAgentMemoryStore] Transcript JSON for {roleId}: {tex.Message}");
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
                    $"[FileAgentMemoryStore] Failed to read chat history from disk for {roleId}: {ex.Message}");
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
                File.WriteAllText(path, finalJson);
                PersistFsForWebGl();
            }
            catch (Exception ex)
            {
                _log?.Error($"[FileAgentMemoryStore] Failed to persist JSON for {roleId}: {ex.Message}");
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

            EnsureHistoryLoaded(roleId);
            _transcripts[roleId].Add(entry);

            if (persistToDisk)
            {
                PersistRoleJsonFile(roleId);
            }
        }

        /// <inheritdoc />
        public IReadOnlyList<ConversationEntry> GetTranscriptEntries(string roleId, int maxEntries)
        {
            EnsureHistoryLoaded(roleId);
            if (!_transcripts.TryGetValue(roleId, out List<ConversationEntry> list) || list.Count == 0)
            {
                return Array.Empty<ConversationEntry>();
            }

            int n = maxEntries > 0 ? Math.Min(maxEntries, list.Count) : list.Count;
            return list.Skip(list.Count - n).ToList();
        }

        /// <summary>
        /// Returns the latest chat messages for a role, optionally capped to the requested count.
        /// </summary>
        public ChatMessage[] GetChatHistory(string roleId, int maxMessages = 0)
        {
            EnsureHistoryLoaded(roleId);

            List<ChatMessage> list = _ephemeralHistory[roleId];
            if (maxMessages > 0 && list.Count > maxMessages)
            {
                return list.Skip(list.Count - maxMessages).ToArray();
            }

            return list.ToArray();
        }

        #endregion
    }
}