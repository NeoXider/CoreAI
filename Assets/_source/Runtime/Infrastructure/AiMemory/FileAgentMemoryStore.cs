using System;
using System.IO;
using CoreAI.Ai;
using UnityEngine;

namespace CoreAI.Infrastructure.AiMemory
{
    public sealed class FileAgentMemoryStore : IAgentMemoryStore
    {
        [Serializable]
        private sealed class Persisted
        {
            public string lastSystemPrompt;
            public string memory;
        }

        private readonly string _dir;

        public FileAgentMemoryStore()
        {
            _dir = Path.Combine(Application.persistentDataPath, "CoreAI", "AgentMemory");
        }

        public bool TryLoad(string roleId, out AgentMemoryState state)
        {
            state = null;
            try
            {
                var path = GetPath(roleId);
                if (!File.Exists(path))
                    return false;
                var json = File.ReadAllText(path);
                var p = JsonUtility.FromJson<Persisted>(json);
                if (p == null)
                    return false;
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

        public void Save(string roleId, AgentMemoryState state)
        {
            try
            {
                Directory.CreateDirectory(_dir);
                var p = new Persisted
                {
                    lastSystemPrompt = state?.LastSystemPrompt ?? "",
                    memory = state?.Memory ?? ""
                };
                var json = JsonUtility.ToJson(p, true);
                File.WriteAllText(GetPath(roleId), json);
            }
            catch (Exception)
            {
            }
        }

        public void Clear(string roleId)
        {
            try
            {
                var path = GetPath(roleId);
                if (File.Exists(path))
                    File.Delete(path);
            }
            catch (Exception)
            {
            }
        }

        private string GetPath(string roleId)
        {
            var safe = string.IsNullOrWhiteSpace(roleId) ? "Creator" : roleId.Trim();
            foreach (var c in Path.GetInvalidFileNameChars())
                safe = safe.Replace(c.ToString(), "_");
            return Path.Combine(_dir, safe + ".json");
        }
    }
}

