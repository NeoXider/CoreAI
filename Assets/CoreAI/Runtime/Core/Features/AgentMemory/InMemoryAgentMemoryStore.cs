using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace CoreAI.Ai
{
    /// <summary>
    /// Process-lifetime backing store for agent memory, flat chat history, and structured transcripts.
    /// It preserves the same contracts as the Unity file store without performing any filesystem I/O.
    /// </summary>
    public sealed class InMemoryAgentMemoryStore : IAgentMemoryStore, IAgentMemoryLoadDiagnostics,
        IAtomicAgentMemoryStore, IConversationTranscriptStore
    {
        private readonly object _gate = new();
        private readonly int _maxChatHistoryMessages;
        private readonly int _maxTranscriptEntries;
        private readonly Dictionary<string, AgentMemoryState> _states = new(StringComparer.Ordinal);
        private readonly Dictionary<string, List<ChatMessage>> _chat = new(StringComparer.Ordinal);
        private readonly Dictionary<string, List<ConversationEntry>> _transcripts = new(StringComparer.Ordinal);

        /// <summary>Creates an in-memory store with bounded chat and transcript histories.</summary>
        public InMemoryAgentMemoryStore(int maxChatHistoryMessages = 500, int maxTranscriptEntries = 2000)
        {
            _maxChatHistoryMessages = Math.Max(1, maxChatHistoryMessages);
            _maxTranscriptEntries = Math.Max(1, maxTranscriptEntries);
        }

        /// <inheritdoc />
        public bool TryLoad(string roleId, out AgentMemoryState state)
        {
            return TryLoadDetailed(roleId, out state) == AgentMemoryLoadStatus.Loaded;
        }

        /// <inheritdoc />
        public AgentMemoryLoadStatus TryLoadDetailed(string roleId, out AgentMemoryState state)
        {
            string key = Key(roleId);
            lock (_gate)
            {
                if (!_states.TryGetValue(key, out AgentMemoryState stored))
                {
                    state = null;
                    return AgentMemoryLoadStatus.NotFound;
                }

                state = Clone(stored);
                return AgentMemoryLoadStatus.Loaded;
            }
        }

        /// <inheritdoc />
        public void Save(string roleId, AgentMemoryState state)
        {
            lock (_gate)
            {
                _states[Key(roleId)] = Clone(state ?? new AgentMemoryState());
            }
        }

        /// <inheritdoc />
        public void Clear(string roleId)
        {
            lock (_gate)
            {
                // WHY: match FileAgentMemoryStore: clearing memory does not erase conversation history.
                _states.Remove(Key(roleId));
            }
        }

        /// <inheritdoc />
        public void ClearChatHistory(string roleId)
        {
            string key = Key(roleId);
            lock (_gate)
            {
                _chat.Remove(key);
                _transcripts.Remove(key);
            }
        }

        /// <inheritdoc />
        public void AppendChatMessage(string roleId, string role, string content, bool persistToDisk = true)
        {
            if (string.IsNullOrWhiteSpace(content))
            {
                return;
            }

            string key = Key(roleId);
            lock (_gate)
            {
                if (!_chat.TryGetValue(key, out List<ChatMessage> history))
                {
                    history = new List<ChatMessage>();
                    _chat[key] = history;
                }

                history.Add(new ChatMessage(role ?? "", content));
                TrimToLatest(history, _maxChatHistoryMessages);
            }
        }

        /// <inheritdoc />
        public ChatMessage[] GetChatHistory(string roleId, int maxMessages = 0)
        {
            lock (_gate)
            {
                if (!_chat.TryGetValue(Key(roleId), out List<ChatMessage> history) || history.Count == 0)
                {
                    return Array.Empty<ChatMessage>();
                }

                int count = maxMessages > 0 ? Math.Min(maxMessages, history.Count) : history.Count;
                ChatMessage[] result = new ChatMessage[count];
                history.CopyTo(history.Count - count, result, 0, count);
                return result;
            }
        }

        /// <inheritdoc />
        public void AppendTranscriptEntry(string roleId, ConversationEntry entry, bool persistToDisk = true)
        {
            if (entry == null || string.IsNullOrWhiteSpace(roleId))
            {
                return;
            }

            string key = Key(roleId);
            lock (_gate)
            {
                if (!_transcripts.TryGetValue(key, out List<ConversationEntry> transcript))
                {
                    transcript = new List<ConversationEntry>();
                    _transcripts[key] = transcript;
                }

                transcript.Add(Clone(entry));
                TrimToLatest(transcript, _maxTranscriptEntries);
            }
        }

        /// <inheritdoc />
        public IReadOnlyList<ConversationEntry> GetTranscriptEntries(string roleId, int maxEntries)
        {
            lock (_gate)
            {
                if (!_transcripts.TryGetValue(Key(roleId), out List<ConversationEntry> transcript) ||
                    transcript.Count == 0)
                {
                    return Array.Empty<ConversationEntry>();
                }

                int count = maxEntries > 0 ? Math.Min(maxEntries, transcript.Count) : transcript.Count;
                List<ConversationEntry> result = new(count);
                for (int i = transcript.Count - count; i < transcript.Count; i++)
                {
                    result.Add(Clone(transcript[i]));
                }

                return result;
            }
        }

        /// <inheritdoc />
        public Task<TResult> MutateAsync<TResult>(string roleId, Func<AgentMemoryState, TResult> mutator,
            CancellationToken cancellationToken = default)
        {
            if (mutator == null)
            {
                throw new ArgumentNullException(nameof(mutator));
            }

            cancellationToken.ThrowIfCancellationRequested();
            string key = Key(roleId);
            lock (_gate)
            {
                AgentMemoryState state = _states.TryGetValue(key, out AgentMemoryState stored)
                    ? Clone(stored)
                    : new AgentMemoryState();
                TResult result = mutator(state);
                _states[key] = Clone(state);
                return Task.FromResult(result);
            }
        }

        private static string Key(string roleId)
        {
            return roleId ?? "";
        }

        private static void TrimToLatest<T>(List<T> values, int cap)
        {
            int remove = values.Count - cap;
            if (remove > 0)
            {
                values.RemoveRange(0, remove);
            }
        }

        private static AgentMemoryState Clone(AgentMemoryState state)
        {
            AgentMemoryVersionSnapshot[] versions = null;
            if (state.Versions != null)
            {
                versions = new AgentMemoryVersionSnapshot[state.Versions.Length];
                for (int i = 0; i < state.Versions.Length; i++)
                {
                    AgentMemoryVersionSnapshot version = state.Versions[i];
                    versions[i] = version == null
                        ? null
                        : new AgentMemoryVersionSnapshot
                        {
                            Version = version.Version,
                            Timestamp = version.Timestamp,
                            Action = version.Action,
                            ContentAfter = version.ContentAfter,
                            Note = version.Note
                        };
                }
            }

            return new AgentMemoryState
            {
                LastSystemPrompt = state.LastSystemPrompt,
                Memory = state.Memory,
                SystemPromptMemorySnapshot = state.SystemPromptMemorySnapshot,
                SystemPromptMemoryVersion = state.SystemPromptMemoryVersion,
                MaxMemoryVersions = state.MaxMemoryVersions,
                Versions = versions
            };
        }

        private static ConversationEntry Clone(ConversationEntry entry)
        {
            return new ConversationEntry
            {
                Kind = entry.Kind,
                Key = entry.Key,
                Content = entry.Content,
                CallId = entry.CallId,
                Timestamp = entry.Timestamp
            };
        }
    }
}
