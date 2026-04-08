using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CoreAI.Ai;
using CoreAI.Logging;
using UnityEngine;

namespace CoreAI.Infrastructure.AiMemory
{
    /// <summary>
    /// Персистентная память агентов в <see cref="Application.persistentDataPath"/> (JSON на роль).
    /// Поддерживает 2 типа памяти:
    /// 1) MemoryTool — явная память через function call (memory поле)
    /// 2) ChatHistory — полная история диалога (chatHistory поле)
    /// </summary>
    public sealed class FileAgentMemoryStore : IAgentMemoryStore
    {
        [Serializable]
        private sealed class Persisted
        {
            public string lastSystemPrompt;
            public string memory;
            public string chatHistoryJson; // JSON массив ChatMessage[]
        }

        private readonly string _dir;
        private readonly Dictionary<string, List<ChatMessage>> _ephemeralHistory = new();
        private readonly ILog _log;

        /// <summary>Каталог памяти: CoreAI/AgentMemory под persistentDataPath.</summary>
        public FileAgentMemoryStore(ILog log = null)
        {
            _dir = Path.Combine(Application.persistentDataPath, "CoreAI", "AgentMemory");
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
                        p.lastSystemPrompt = ""; // Очищаем промпт
                        File.WriteAllText(path, JsonUtility.ToJson(p, true));
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
            // Очищаем из оперативной памяти
            if (_ephemeralHistory.ContainsKey(roleId))
            {
                _ephemeralHistory.Remove(roleId);
            }

            // Очищаем с диска
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
                        File.WriteAllText(path, JsonUtility.ToJson(p, true));
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
            // Упрощенная санитизация имени файла
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

            try
            {
                string path = GetPath(roleId);
                if (File.Exists(path))
                {
                    string existingJson = File.ReadAllText(path);
                    Persisted p = JsonUtility.FromJson<Persisted>(existingJson);

                    if (p != null && !string.IsNullOrEmpty(p.chatHistoryJson))
                    {
                        ChatMessageArrayWrapper wrapper =
                            JsonUtility.FromJson<ChatMessageArrayWrapper>(p.chatHistoryJson);
                        if (wrapper.Items != null)
                        {
                            // Вставляем дисковую историю в начало
                            _ephemeralHistory[roleId].InsertRange(0, wrapper.Items);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _log?.Error(
                    $"[FileAgentMemoryStore] Failed to read chat history from disk for {roleId}: {ex.Message}");
            }
        }

        /// <summary>
        /// Добавляет сообщение в историю.
        /// persistToDisk=false используется для промежуточных tool call (сохраняется только в оперативку).
        /// persistToDisk=true используется для финальных ответов (синхронизируется на диск).
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
                // Используем миллисекунды для гарантии правильной сортировки при быстром добавлении
                Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            };

            _ephemeralHistory[roleId].Add(newMsg);

            if (persistToDisk)
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

                    // Сохраняем ВЕСЬ массив актуальной истории на диск
                    ChatMessageArrayWrapper newWrapper = new() { Items = _ephemeralHistory[roleId].ToArray() };
                    p.chatHistoryJson = JsonUtility.ToJson(newWrapper);

                    string newJson = JsonUtility.ToJson(p, true);
                    File.WriteAllText(path, newJson);
                }
                catch (Exception ex)
                {
                    _log?.Error(
                        $"[FileAgentMemoryStore] Failed to append chat history to disk for {roleId}: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// Возвращает объединенную историю (с диска + из оперативки), отсортированную по времени.
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