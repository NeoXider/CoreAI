using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Security.Cryptography;
using System.Text;
using CoreAI.Ai;
using CoreAI.Infrastructure;
using CoreAI.Logging;
using Newtonsoft.Json;
using UnityEngine;

namespace CoreAI.Infrastructure.Llm
{
    /// <summary>
    /// Atomic skill storage. All instances serialize writes and publication through the same canonical
    /// directory gate, including discovery and migration of legacy filenames. Existing unreadable records
    /// are never treated as missing during mutation. Logical ids are case-insensitive on every platform.
    /// WebGL Sync queues browser persistence; synchronous success is not an IndexedDB completion receipt.
    /// </summary>
    public sealed class FileSkillStore : ISkillStore, ICommittedSkillStore, IDisposable
    {
        private static readonly JsonSerializerSettings JsonSettings = new() { Formatting = Formatting.Indented };
        private static readonly StringComparer PathComparer = Path.DirectorySeparatorChar == '\\'
            ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        private static readonly StringComparison PathComparison = Path.DirectorySeparatorChar == '\\'
            ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        // WHY: never evict a live path gate; a waiter may still hold its instance.
        private static readonly ConcurrentDictionary<string, SemaphoreSlim> MutationLocks = new(PathComparer);
        private readonly string _dir;
        private readonly ILog _log;
        private readonly SemaphoreSlim _gate;
        private bool _disposed;

        public FileSkillStore(string rootDirectory = null, ILog log = null)
        {
            _dir = Path.GetFullPath(!string.IsNullOrWhiteSpace(rootDirectory)
                ? rootDirectory.Trim()
                : Path.Combine(Application.persistentDataPath, CoreAiPersistentPaths.RootFolderName,
                    CoreAiPersistentPaths.Skills));
            if (_dir.Length > Path.GetPathRoot(_dir).Length)
                _dir = _dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            _gate = MutationLocks.GetOrAdd(_dir, _ => new SemaphoreSlim(1, 1));
            _log = log;
        }

        public void Save(SkillRecord record)
        {
            ThrowIfDisposed();
            if (record == null || string.IsNullOrWhiteSpace(record.Id)) return;
            Mutate(record.Id, _ => SkillStoreMutation<bool>.SaveRecord(record, true));
        }

        public void Delete(string id)
        {
            ThrowIfDisposed();
            if (string.IsNullOrWhiteSpace(id)) return;
            Mutate(id, current => current == null
                ? SkillStoreMutation<bool>.NoChange(false)
                : SkillStoreMutation<bool>.DeleteRecord(true));
        }

        public TResult Mutate<TResult>(string id, Func<SkillRecord, SkillStoreMutation<TResult>> mutator)
        {
            return MutateAndPublish(id, mutator, null);
        }

        public TResult MutateAndPublish<TResult>(string id, Func<SkillRecord, SkillStoreMutation<TResult>> mutator,
            Action<TResult> publish)
        {
            ThrowIfDisposed();
            if (mutator == null) throw new ArgumentNullException(nameof(mutator));
            string skillId = Normalize(id);
            if (skillId.Length == 0) throw new ArgumentException("Skill id must not be empty.", nameof(id));
            _ = GetSkillPath(skillId);
            Enter(_gate);
            try
            {
                SkillRecord current = FindRecord(skillId, out string existingPath);
                string stableId = current?.Id ?? skillId;
                string path = GetSkillPath(stableId);
                _ = ReadRecordStrict(path, stableId);
                if (current != null) MigrateRecord(existingPath, path);
                SkillStoreMutation<TResult> mutation = SkillStoreCallbackContext.Run(() => mutator(current))
                    ?? throw new InvalidOperationException("Skill store mutator returned null.");
                if (mutation.Delete)
                {
                    if (current != null)
                    {
                        File.Delete(path);
                        CoreAiWebGlPersistence.Sync();
                    }
                }
                else if (mutation.Save)
                {
                    if (mutation.Record == null) throw new InvalidOperationException("Save mutation requires a record.");
                    if (!string.Equals(Normalize(mutation.Record.Id), skillId, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("A skill mutation cannot write a different key.");
                    _ = ReadRecordStrict(path, stableId);
                    SaveCore(stableId, mutation.Record, path);
                }
                // WHY: a later commit must not publish ahead of this one; callback stays inside the key gate.
                SkillStoreCallbackContext.Publish(skillId, mutation.Result, publish);
                return mutation.Result;
            }
            catch (Exception ex)
            {
                string stage = ex is SkillStorePublicationException ? "Publication failed after commit" : "Mutation failed";
                _log?.Error($"[FileSkillStore] {stage} for {skillId}: {ex}");
                throw;
            }
            finally
            {
                _gate.Release();
            }
        }

        private void SaveCore(string stableId, SkillRecord record, string path)
        {
            SkillRecord snapshot = new(stableId, record.Description, record.Instructions, record.ToolNames,
                record.Version, record.Sections);
            if (snapshot.Version < 0) throw new InvalidDataException("Skill version must not be negative.");
            Directory.CreateDirectory(_dir);
            AtomicWriteAllText(path, JsonConvert.SerializeObject(snapshot, JsonSettings));
            CoreAiWebGlPersistence.Sync();
        }

        public bool TryLoad(string id, out SkillRecord record)
        {
            ThrowIfDisposed();
            record = null;
            string skillId = Normalize(id);
            if (skillId.Length == 0) return false;
            _ = GetSkillPath(skillId);
            Enter(_gate);
            try
            {
                record = FindRecord(skillId, out string existingPath);
                if (record != null)
                {
                    string path = GetSkillPath(record.Id);
                    _ = ReadRecordStrict(path, record.Id);
                    MigrateRecord(existingPath, path);
                }
                return record != null;
            }
            catch (Exception ex)
            {
                record = null;
                _log?.Error($"[FileSkillStore] Load failed for {skillId}: {ex}");
                return false;
            }
            finally
            {
                _gate.Release();
            }
        }

        public IReadOnlyList<SkillRecord> List()
        {
            ThrowIfDisposed();
            Enter(_gate);
            try
            {
                Dictionary<string, SkillRecord> unique = new(StringComparer.OrdinalIgnoreCase);
                HashSet<string> ambiguous = new(StringComparer.OrdinalIgnoreCase);
                foreach (string path in GetRecordPaths())
                {
                    try
                    {
                        SkillRecord record = ReadRecordStrict(path, null);
                        if (record == null) continue;
                        ValidateRecordPath(path, record.Id);
                        string key = Normalize(record.Id);
                        if (unique.ContainsKey(key)) ambiguous.Add(key);
                        else unique.Add(key, record);
                    }
                    catch (Exception ex)
                    {
                        _log?.Error($"[FileSkillStore] Record read failed for {path}: {ex}");
                    }
                }
                foreach (string key in ambiguous)
                {
                    unique.Remove(key);
                    _log?.Error($"[FileSkillStore] Ambiguous skill '{key}': preserve files and resolve the duplicate before writing.");
                }
                List<SkillRecord> records = new(unique.Values);
                records.Sort((left, right) => StringComparer.OrdinalIgnoreCase.Compare(left.Id, right.Id));
                return records.AsReadOnly();
            }
            finally { _gate.Release(); }
        }

        private string[] GetRecordPaths()
        {
            try { return Directory.GetFiles(_dir, "*.json"); }
            catch (DirectoryNotFoundException) { return Array.Empty<string>(); }
        }

        private SkillRecord FindRecord(string id, out string existingPath)
        {
            SkillRecord found = null;
            existingPath = null;
            // WHY: legacy filenames encode the old case and platform-specific sanitization. Discover by
            // validated stored identity before migration; an unreadable record may be this same skill.
            foreach (string path in GetRecordPaths())
            {
                SkillRecord record = ReadRecordStrict(path, null);
                if (record == null) continue;
                ValidateRecordPath(path, record.Id);
                if (!string.Equals(Normalize(record.Id), id, StringComparison.OrdinalIgnoreCase)) continue;
                if (found != null)
                    throw new InvalidDataException($"Ambiguous skill '{id}': multiple records exist; all files preserved.");
                found = record;
                existingPath = path;
            }
            return found;
        }

        private void ValidateRecordPath(string path, string id)
        {
            string filename = Path.GetFileName(path);
            if (PathComparer.Equals(GetSkillPath(id), Path.GetFullPath(path)) ||
                string.Equals(filename, HashedFileName(id, true) + ".json", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(filename, LegacyFileName(id, true) + ".json", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(filename, LegacyFileName(id, false) + ".json", StringComparison.OrdinalIgnoreCase)) return;
            throw new InvalidDataException("Existing skill filename does not match its identity; preserved without changes.");
        }

        private static void MigrateRecord(string existingPath, string canonicalPath)
        {
            if (PathComparer.Equals(Path.GetFullPath(existingPath), canonicalPath)) return;
            // WHY: one same-directory rename preserves the complete old bytes if the later content write fails.
            // File.Move refuses an occupied destination; migration never chooses a winning duplicate.
            File.Move(existingPath, canonicalPath);
            CoreAiWebGlPersistence.Sync();
        }
        private static SkillRecord ReadRecordStrict(string path, string expectedId)
        {
            string json;
            try
            {
                json = File.ReadAllText(path);
            }
            catch (FileNotFoundException)
            {
                return null;
            }
            catch (DirectoryNotFoundException)
            {
                return null;
            }
            SkillRecord record;
            try
            {
                record = JsonConvert.DeserializeObject<SkillRecord>(json, JsonSettings);
            }
            catch (JsonException ex)
            {
                throw new InvalidDataException("Existing skill record is not readable JSON; preserved without changes.", ex);
            }
            if (record == null || string.IsNullOrWhiteSpace(record.Id) || record.Version < 0 ||
                (expectedId != null && !string.Equals(Normalize(record.Id), expectedId, StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidDataException("Existing skill record has invalid identity or version; preserved without changes.");
            }
            return record;
        }

        private string GetSkillPath(string id)
        {
            string path = Path.GetFullPath(Path.Combine(_dir, CanonicalFileName(id) + ".json"));
            string prefix = _dir.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal)
                ? _dir : _dir + Path.DirectorySeparatorChar;
            if (!path.StartsWith(prefix, PathComparison)) throw new InvalidOperationException("Skill path escapes its store root.");
            return path;
        }

        private static string CanonicalFileName(string id)
        {
            return HashedFileName(id, false);
        }

        private static string HashedFileName(string id, bool legacyCaseFold)
        {
            using SHA256 hash = SHA256.Create();
            // WHY: replacement fallback maps different invalid surrogate ids onto the same file key.
            string source = Normalize(id);
            if (legacyCaseFold) source = source.ToUpperInvariant();
            byte[] bytes = hash.ComputeHash(new UTF8Encoding(false, true).GetBytes(source));
            // WHY: ToUpperInvariant is not OrdinalIgnoreCase identity (for example, long-s and ASCII S).
            // Preserve the original stored id; aliases resolve it through FindRecord instead of renaming it.
            StringBuilder name = new(legacyCaseFold ? "skill-" : "skill-v2-");
            foreach (byte value in bytes) name.Append(value.ToString("x2"));
            return name.ToString();
        }

        private static string LegacyFileName(string id, bool windows)
        {
            StringBuilder safe = new();
            bool changed = false;
            foreach (char c in id)
            {
                bool invalid = c == '\0' || c == '/' || (windows && (c < 32 || "<>:\"\\|?*".IndexOf(c) >= 0));
                safe.Append(invalid ? '_' : c);
                changed |= invalid;
            }
            if (!changed) return safe.ToString();
            uint hash = 2166136261u;
            foreach (char c in id) hash = unchecked((hash ^ c) * 16777619u);
            return $"{safe}_{hash:x8}";
        }
        private void AtomicWriteAllText(string path, string contents)
        {
            string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(temporary, contents);
                if (File.Exists(path)) File.Replace(temporary, path, null);
                else File.Move(temporary, path);
            }
            finally
            {
                try
                {
                    if (File.Exists(temporary)) File.Delete(temporary);
                }
                catch (Exception ex)
                {
                    _log?.Error($"[FileSkillStore] Temporary file cleanup failed: {ex}");
                }
            }
        }

        private static void Enter(SemaphoreSlim gate)
        {
            SkillStoreCallbackContext.ThrowIfActive();
#if UNITY_WEBGL && !UNITY_EDITOR
            if (!gate.Wait(0)) throw new InvalidOperationException("Skill store is busy; retry after the current operation.");
#else
            gate.Wait();
#endif
        }

        private void ThrowIfDisposed()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(FileSkillStore));
        }

        public void Dispose() => _disposed = true;
        private static string Normalize(string value) => (value ?? "").Trim();
    }
}
