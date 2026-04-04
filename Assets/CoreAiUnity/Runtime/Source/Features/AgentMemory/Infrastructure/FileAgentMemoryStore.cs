using System;
using System.IO;
using CoreAI.Ai;
using UnityEngine;

namespace CoreAI.Infrastructure.AiMemory
{
    /// <summary>
    /// Персистентная память агентов в <see cref="Application.persistentDataPath"/> (JSON на роль).
    /// </summary>
    public sealed class FileAgentMemoryStore : IAgentMemoryStore
    {
        [Serializable]
        private sealed class Persisted
        {
            public string lastSystemPrompt;
            public string memory;
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
                Persisted p = new()
                {
                    lastSystemPrompt = state?.LastSystemPrompt ?? "",
                    memory = state?.Memory ?? ""
                };
                string json = JsonUtility.ToJson(p, true);
                File.WriteAllText(GetPath(roleId), json);
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

        private string GetPath(string roleId)
        {
            string safe = string.IsNullOrWhiteSpace(roleId) ? "Creator" : roleId.Trim();
            foreach (char c in Path.GetInvalidFileNameChars())
            {
                safe = safe.Replace(c.ToString(), "_");
            }

            return Path.Combine(_dir, safe + ".json");
        }
    }
}