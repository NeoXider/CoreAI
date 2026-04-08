using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CoreAI.Ai;
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

        /// <summary>Каталог памяти: CoreAI/AgentMemory под persistentDataPath.</summary>
        public FileAgentMemoryStore()
        {
            _dir = Path.Combine(Application.persistentDataPath, "CoreAI", "AgentMemory");
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
            catch (Exception)
            {
                return false;
            }
        }

        /// <inheritdoc />
        public void Save(string roleId, AgentMemoryState state)
        {
            try
            {
                Directory.CreateDirectory(_dir);

                // Загружаем существующий файл чтобы сохранить chatHistory
                string path = GetPath(roleId);
                string existingChatJson = "";
                if (File.Exists(path))
                {
                    string json = File.ReadAllText(path);
                    Persisted old = JsonUtility.FromJson<Persisted>(json);
                    if (old != null)
                    {
                        existingChatJson = old.chatHistoryJson ?? "";
                    }
                }

                Persisted p = new()
                {
                    lastSystemPrompt = state?.LastSystemPrompt ?? "",
                    memory = state?.Memory ?? "",
                    chatHistoryJson = existingChatJson
                };
                string newJson = JsonUtility.ToJson(p, true);
                File.WriteAllText(path, newJson);
            }
            catch (Exception)
            {
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
                    File.Delete(path);
                }
            }
            catch (Exception)
            {
            }
        }

        /// <inheritdoc />
        public void ClearChatHistory(string roleId)
        {
            _ephemeralHistory.Remove(roleId);
            try
            {
                string path = GetPath(roleId);
                if (File.Exists(path))
                {
                    string json = File.ReadAllText(path);
                    Persisted p = JsonUtility.FromJson<Persisted>(json);
                    if (p != null)
                    {
                        p.chatHistoryJson = "";
                        File.WriteAllText(path, JsonUtility.ToJson(p, true));
                    }
                }
            }
            catch (Exception)
            {
            }
        }

        /// <inheritdoc />
        public void AppendChatMessage(string roleId, string role, string content, bool persistToDisk = true)
        {
            try
            {
                Directory.CreateDirectory(_dir);
                string path = GetPath(roleId);

                // Загружаем существующую историю и стейт
                List<ChatMessage> history = new();
                string existingMemory = "";
                string existingPrompt = "";

                if (File.Exists(path))
                {
                    string json = File.ReadAllText(path);
                    Persisted p = JsonUtility.FromJson<Persisted>(json);
                    if (p != null)
                    {
                        existingMemory = p.memory ?? "";
                        existingPrompt = p.lastSystemPrompt ?? "";

                        if (!string.IsNullOrEmpty(p.chatHistoryJson))
                        {
                            try
                            {
                                history = JsonUtility.FromJson<ChatMessageArrayWrapper>(p.chatHistoryJson)?.messages
                                          ?? new List<ChatMessage>();
                            }
                            catch
                            {
                                history = new List<ChatMessage>();
                            }
                        }
                    }
                }
                
                // Fallback to ephemeral if disk hasn't persisted it but we have it
                if (history.Count == 0 && _ephemeralHistory.TryGetValue(roleId, out List<ChatMessage> ephemeralStr))
                {
                    history = new List<ChatMessage>(ephemeralStr);
                }

                // Добавляем новое сообщение
                history.Add(new ChatMessage(role, content ?? ""));
                _ephemeralHistory[roleId] = history;

                if (!persistToDisk)
                {
                    return;
                }

                // Сохраняем
                Persisted persisted = new()
                {
                    lastSystemPrompt = existingPrompt,
                    memory = existingMemory,
                    chatHistoryJson = JsonUtility.ToJson(new ChatMessageArrayWrapper { messages = history })
                };
                File.WriteAllText(path, JsonUtility.ToJson(persisted, true));
            }
            catch (Exception)
            {
            }
        }

        /// <inheritdoc />
        public ChatMessage[] GetChatHistory(string roleId, int maxMessages = 0)
        {
            try
            {
                List<ChatMessage> resultHistory = new();
                string path = GetPath(roleId);
                
                if (File.Exists(path))
                {
                    string json = File.ReadAllText(path);
                    Persisted p = JsonUtility.FromJson<Persisted>(json);
                    if (p != null && !string.IsNullOrEmpty(p.chatHistoryJson))
                    {
                        ChatMessageArrayWrapper wrapper = JsonUtility.FromJson<ChatMessageArrayWrapper>(p.chatHistoryJson);
                        if (wrapper != null && wrapper.messages != null)
                        {
                            resultHistory = wrapper.messages;
                        }
                    }
                }
                
                if (resultHistory.Count == 0 && _ephemeralHistory.TryGetValue(roleId, out List<ChatMessage> emph))
                {
                    resultHistory = new List<ChatMessage>(emph);
                }

                if (resultHistory.Count == 0)
                {
                    return Array.Empty<ChatMessage>();
                }

                if (maxMessages <= 0)
                {
                    return resultHistory.ToArray();
                }

                // Возвращаем N последних сообщений
                int count = Math.Min(maxMessages, resultHistory.Count);
                return resultHistory.Skip(resultHistory.Count - count).ToArray();
            }
            catch (Exception)
            {
                return Array.Empty<ChatMessage>();
            }
        }

        private string GetPath(string roleId)
        {
            string safe = string.IsNullOrWhiteSpace(roleId) ? "Creator" : roleId.Trim();
            foreach (char c in Path.GetInvalidFileNameChars())
            {
                safe = safe.Replace(c.ToString(), "_");
            }

            return Path.Combine(_dir, safe + ".json");
        }

        // Хелпер для сериализации List<ChatMessage> через JsonUtility
        [Serializable]
        private sealed class ChatMessageArrayWrapper
        {
            public List<ChatMessage> messages = new();
        }
    }
}