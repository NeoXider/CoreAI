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
        public void AppendChatMessage(string roleId, string role, string content)
        {
            try
            {
                Directory.CreateDirectory(_dir);
                string path = GetPath(roleId);

                // Загружаем существующую историю
                List<ChatMessage> history = new();
                if (File.Exists(path))
                {
                    string json = File.ReadAllText(path);
                    Persisted p = JsonUtility.FromJson<Persisted>(json);
                    if (p != null && !string.IsNullOrEmpty(p.chatHistoryJson))
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

                // Добавляем новое сообщение
                history.Add(new ChatMessage(role, content ?? ""));

                // Сохраняем
                Persisted persisted = new()
                {
                    lastSystemPrompt = "",
                    memory = "",
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
                string path = GetPath(roleId);
                if (!File.Exists(path))
                {
                    return System.Array.Empty<ChatMessage>();
                }

                string json = File.ReadAllText(path);
                Persisted p = JsonUtility.FromJson<Persisted>(json);
                if (p == null || string.IsNullOrEmpty(p.chatHistoryJson))
                {
                    return System.Array.Empty<ChatMessage>();
                }

                var wrapper = JsonUtility.FromJson<ChatMessageArrayWrapper>(p.chatHistoryJson);
                if (wrapper == null || wrapper.messages == null)
                {
                    return System.Array.Empty<ChatMessage>();
                }

                if (maxMessages <= 0)
                {
                    return wrapper.messages.ToArray();
                }

                // Возвращаем N последних сообщений
                int count = Math.Min(maxMessages, wrapper.messages.Count);
                return wrapper.messages.Skip(wrapper.messages.Count - count).ToArray();
            }
            catch (Exception)
            {
                return System.Array.Empty<ChatMessage>();
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
