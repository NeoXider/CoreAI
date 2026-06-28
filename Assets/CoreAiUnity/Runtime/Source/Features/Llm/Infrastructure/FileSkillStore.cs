using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using CoreAI.Ai;
using CoreAI.Infrastructure;
using CoreAI.Logging;
using Newtonsoft.Json;
using UnityEngine;

namespace CoreAI.Infrastructure.Llm
{
    /// <summary>
    /// File-backed Unity implementation of <see cref="ISkillStore"/>: the persistent store for
    /// agent-authored skills (name, description, instructions, and the allowlist of existing tool names),
    /// so a skill the model wrote survives a restart and reappears in its <c>read_skill</c> catalog.
    /// Modeled on <c>FileLuaModSourceStore</c>.
    /// <para>
    /// Each skill lives in its own file
    /// <c>persistentDataPath/CoreAI/Skills/&lt;sanitizedId&gt;.json</c>. Writes are serialized through a
    /// <see cref="SemaphoreSlim"/> gate and applied atomically (temp file then swap) so a crash mid-write
    /// cannot corrupt an existing skill.
    /// </para>
    /// </summary>
    public sealed class FileSkillStore : ISkillStore, IDisposable
    {
#if UNITY_WEBGL && !UNITY_EDITOR
        [DllImport("__Internal")]
        private static extern void CoreAi_PersistFsSync();
#endif

        private static void PersistFsForWebGl()
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            try { CoreAi_PersistFsSync(); } catch { /* best-effort flush */ }
#endif
        }

        private static readonly JsonSerializerSettings JsonSettings = new()
        {
            Formatting = Formatting.Indented
        };

        private static readonly IReadOnlyList<SkillRecord> Empty = new SkillRecord[0];

        private readonly string _dir;
        private readonly ILog _log;
        private readonly SemaphoreSlim _gate = new(1, 1);

        /// <summary>Creates a file-backed skill store under CoreAI persistent data.</summary>
        /// <param name="rootDirectory">
        /// Optional override for the storage directory; defaults to the CoreAI skills folder under
        /// <see cref="Application.persistentDataPath"/>.
        /// </param>
        /// <param name="log">Optional logger.</param>
        public FileSkillStore(string rootDirectory = null, ILog log = null)
        {
            _dir = !string.IsNullOrWhiteSpace(rootDirectory)
                ? rootDirectory.Trim()
                : Path.Combine(Application.persistentDataPath, CoreAiPersistentPaths.RootFolderName,
                    CoreAiPersistentPaths.Skills);
            _log = log;
        }

        /// <inheritdoc />
        public void Save(SkillRecord record)
        {
            if (record == null)
            {
                return;
            }

            string id = Normalize(record.Id);
            if (id.Length == 0)
            {
                return;
            }

            _gate.Wait();
            try
            {
                record.Id = id;
                if (!Directory.Exists(_dir))
                {
                    Directory.CreateDirectory(_dir);
                }

                string json = JsonConvert.SerializeObject(record, JsonSettings);
                AtomicWriteAllText(GetSkillPath(id), json, _log);
                PersistFsForWebGl();
            }
            catch (Exception ex)
            {
                _log?.Error($"[FileSkillStore] Save failed for {id}: {ex}");
            }
            finally
            {
                _gate.Release();
            }
        }

        /// <inheritdoc />
        public bool TryLoad(string id, out SkillRecord record)
        {
            record = null;
            string skillId = Normalize(id);
            if (skillId.Length == 0)
            {
                return false;
            }

            _gate.Wait();
            try
            {
                string path = GetSkillPath(skillId);
                if (!File.Exists(path))
                {
                    return false;
                }

                record = ReadRecord(path);
                return record != null;
            }
            catch (Exception ex)
            {
                _log?.Error($"[FileSkillStore] Load failed for {skillId}: {ex}");
                record = null;
                return false;
            }
            finally
            {
                _gate.Release();
            }
        }

        /// <inheritdoc />
        public IReadOnlyList<SkillRecord> List()
        {
            _gate.Wait();
            try
            {
                if (!Directory.Exists(_dir))
                {
                    return Empty;
                }

                List<SkillRecord> result = new();
                foreach (string path in Directory.GetFiles(_dir, "*.json"))
                {
                    SkillRecord record = ReadRecord(path);
                    if (record != null && !string.IsNullOrWhiteSpace(record.Id))
                    {
                        result.Add(record);
                    }
                }

                return result;
            }
            catch (Exception ex)
            {
                _log?.Error($"[FileSkillStore] List failed: {ex}");
                return Empty;
            }
            finally
            {
                _gate.Release();
            }
        }

        /// <inheritdoc />
        public void Delete(string id)
        {
            string skillId = Normalize(id);
            if (skillId.Length == 0)
            {
                return;
            }

            _gate.Wait();
            try
            {
                string path = GetSkillPath(skillId);
                if (File.Exists(path))
                {
                    File.Delete(path);
                    PersistFsForWebGl();
                }
            }
            catch (Exception ex)
            {
                _log?.Error($"[FileSkillStore] Delete failed for {skillId}: {ex}");
            }
            finally
            {
                _gate.Release();
            }
        }

        /// <summary>Releases the internal file-access semaphore.</summary>
        public void Dispose()
        {
            _gate.Dispose();
        }

        private SkillRecord ReadRecord(string path)
        {
            try
            {
                string json = File.ReadAllText(path);
                return JsonConvert.DeserializeObject<SkillRecord>(json, JsonSettings);
            }
            catch (Exception ex)
            {
                _log?.Error($"[FileSkillStore] Record read failed for {path}: {ex}");
                return null;
            }
        }

        private string GetSkillPath(string id)
        {
            return Path.Combine(_dir, SanitizedFileName(id) + ".json");
        }

        /// <summary>
        /// Maps a raw skill id to a unique file name. Invalid filename characters are replaced, and when
        /// the replacement changed anything a short hash of the raw id is appended so distinct ids cannot
        /// collide on the same file.
        /// </summary>
        private static string SanitizedFileName(string id)
        {
            string safe = string.Join("_", id.Split(Path.GetInvalidFileNameChars()));
            if (string.Equals(safe, id, StringComparison.Ordinal))
            {
                return safe;
            }

            uint hash = 2166136261u;
            foreach (char c in id)
            {
                hash = (hash ^ c) * 16777619u;
            }

            return $"{safe}_{hash:x8}";
        }

        private static void AtomicWriteAllText(string path, string contents, ILog log)
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
            catch (Exception ex)
            {
                log?.Error($"[FileSkillStore] Atomic write failed for {path}: {ex}");
                if (File.Exists(tmpPath))
                {
                    try
                    {
                        File.Delete(tmpPath);
                    }
                    catch (Exception cleanupEx)
                    {
                        log?.Error($"[FileSkillStore] Atomic write cleanup failed for {tmpPath}: {cleanupEx}");
                    }
                }

                throw;
            }
        }

        private static string Normalize(string value)
        {
            return (value ?? "").Trim();
        }
    }
}
