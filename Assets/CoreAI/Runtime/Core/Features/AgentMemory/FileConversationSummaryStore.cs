using System;
using System.IO;
using CoreAI.Logging;
using Newtonsoft.Json;

namespace CoreAI.Ai
{
    /// <summary>
    /// Persists per-role conversation summaries under a host-provided directory (portable filesystem).
    /// </summary>
    public sealed class FileConversationSummaryStore : IConversationSummaryStore
    {
        private static readonly JsonSerializerSettings JsonSettings = new()
        {
            Formatting = Formatting.Indented
        };

        private readonly string _dir;
        private readonly ILog _log;

        private sealed class PersistedDto
        {
            public string Summary { get; set; } = "";
        }

        /// <summary>Creates store writing to <paramref name="rootDirectory"/>.</summary>
        /// <param name="rootDirectory">Directory path; created on first write.</param>
        /// <param name="log">Optional logger.</param>
        public FileConversationSummaryStore(string rootDirectory, ILog log = null)
        {
            if (string.IsNullOrWhiteSpace(rootDirectory))
            {
                throw new ArgumentException("Root directory is required.", nameof(rootDirectory));
            }

            _dir = rootDirectory.Trim();
            _log = log;
        }

        /// <inheritdoc />
        public string LoadSummary(string roleId)
        {
            if (string.IsNullOrWhiteSpace(roleId))
            {
                return "";
            }

            try
            {
                string path = GetPath(roleId);
                if (!File.Exists(path))
                {
                    return "";
                }

                string json = File.ReadAllText(path);
                PersistedDto dto = JsonConvert.DeserializeObject<PersistedDto>(json, JsonSettings);
                return dto?.Summary?.Trim() ?? "";
            }
            catch (Exception ex)
            {
                _log?.Error($"[FileConversationSummaryStore] Load failed for {roleId}: {ex.Message}");
                return "";
            }
        }

        /// <inheritdoc />
        public void SaveSummary(string roleId, string summary)
        {
            if (string.IsNullOrWhiteSpace(roleId))
            {
                return;
            }

            try
            {
                EnsureDir();
                PersistedDto dto = new() { Summary = summary ?? "" };
                string json = JsonConvert.SerializeObject(dto, JsonSettings);
                File.WriteAllText(GetPath(roleId), json);
            }
            catch (Exception ex)
            {
                _log?.Error($"[FileConversationSummaryStore] Save failed for {roleId}: {ex.Message}");
            }
        }

        /// <inheritdoc />
        public void ClearSummary(string roleId)
        {
            if (string.IsNullOrWhiteSpace(roleId))
            {
                return;
            }

            try
            {
                string path = GetPath(roleId);
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (Exception ex)
            {
                _log?.Error($"[FileConversationSummaryStore] Clear failed for {roleId}: {ex.Message}");
            }
        }

        private string GetPath(string roleId)
        {
            string safe = string.Join("_", roleId.Split(Path.GetInvalidFileNameChars()));
            return Path.Combine(_dir, $"{safe}.json");
        }

        private void EnsureDir()
        {
            if (!Directory.Exists(_dir))
            {
                Directory.CreateDirectory(_dir);
            }
        }
    }
}
