using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
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
    public sealed class FileSkillStore : ISkillStore, IAtomicSkillStore, IDisposable
    {
        private static readonly JsonSerializerSettings JsonSettings = new()
        {
            Formatting = Formatting.Indented
        };

        private static readonly IReadOnlyList<SkillRecord> Empty = new SkillRecord[0];

        /// <summary>
        /// Process-wide mutation locks keyed by each skill's file path, one entry per distinct skill id
        /// ever mutated (not per active/loaded skill), so cardinality tracks the lifetime skill count
        /// rather than the current catalog size. Entries are intentionally never evicted: a caller could
        /// already hold the <see cref="SemaphoreSlim"/> instance fetched from this dictionary while a
        /// concurrent eviction-then-<c>GetOrAdd</c> for the same key hands a second caller a fresh
        /// instance, which would silently break the mutual exclusion this lock exists for. In practice the
        /// key set is bounded by the number of distinct skills an agent has authored on this install, which
        /// is small relative to process lifetime.
        /// </summary>
        private static readonly ConcurrentDictionary<string, SemaphoreSlim> MutationLocks =
            new(StringComparer.Ordinal);

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
                SaveCore(id, record);
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
        public TResult Mutate<TResult>(string id, Func<SkillRecord, SkillStoreMutation<TResult>> mutator)
        {
            if (mutator == null)
            {
                throw new ArgumentNullException(nameof(mutator));
            }

            string skillId = Normalize(id);
            if (skillId.Length == 0)
            {
                return mutator(null).Result;
            }

            string path = GetSkillPath(skillId);
            SemaphoreSlim mutationGate = MutationLocks.GetOrAdd(path, _ => new SemaphoreSlim(1, 1));
            mutationGate.Wait();
            try
            {
                _gate.Wait();
                try
                {
                    SkillRecord current = File.Exists(path) ? ReadRecord(path) : null;
                    SkillStoreMutation<TResult> mutation = mutator(current);
                    if (mutation == null)
                    {
                        throw new InvalidOperationException("Skill store mutator returned null.");
                    }

                    if (mutation.Delete)
                    {
                        if (File.Exists(path))
                        {
                            File.Delete(path);
                            CoreAiWebGlPersistence.Sync();
                        }
                    }
                    else if (mutation.Save && mutation.Record != null)
                    {
                        SaveCore(skillId, mutation.Record);
                    }

                    return mutation.Result;
                }
                finally
                {
                    _gate.Release();
                }
            }
            catch (Exception ex)
            {
                _log?.Error($"[FileSkillStore] Mutate failed for {skillId}: {ex}");
                throw;
            }
            finally
            {
                mutationGate.Release();
            }
        }

        private void SaveCore(string id, SkillRecord record)
        {
            record.Id = id;
            if (!Directory.Exists(_dir))
            {
                Directory.CreateDirectory(_dir);
            }

            string json = JsonConvert.SerializeObject(record, JsonSettings);
            AtomicWriteAllText(GetSkillPath(id), json, _log);
            CoreAiWebGlPersistence.Sync();
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
                    CoreAiWebGlPersistence.Sync();
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
            string combined = Path.Combine(_dir, SanitizedFileName(id) + ".json");
            return EnsureWithinRoot(combined);
        }

        /// <summary>
        /// Guards against path traversal: <see cref="Path.GetInvalidFileNameChars"/> does NOT strip
        /// <c>..</c>, so an id like <c>..</c> or <c>../x</c> could resolve outside <see cref="_dir"/> and
        /// read/write/delete arbitrary files. Reject any combined path that escapes the store root.
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
                    "[FileSkillStore] Skill id resolves outside the store root; rejected to prevent path traversal.");
            }

            return fullPath;
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
