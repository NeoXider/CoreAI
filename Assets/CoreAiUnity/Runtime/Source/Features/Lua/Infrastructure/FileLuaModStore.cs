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

namespace CoreAI.Infrastructure.Lua
{
    /// <summary>
    /// File-backed Unity implementation of persistent per-mod Lua key/value storage.
    /// </summary>
    public sealed class FileLuaModStore : ILuaModStore, IDisposable
    {
#if UNITY_WEBGL && !UNITY_EDITOR
        [DllImport("__Internal")]
        private static extern void CoreAi_PersistFsSync();
#endif

        /// <summary>
        /// On WebGL pushes the in-memory IDBFS tree into IndexedDB so writes survive a tab reload. No-op on
        /// other platforms. Without this, store_set values are lost on WebGL reload.
        /// </summary>
        private static void PersistFsForWebGl()
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            try { CoreAi_PersistFsSync(); } catch { /* best-effort flush */ }
#endif
        }

        private const int MaxKeysPerMod = 256;
        private const int MaxValueBytes = 64 * 1024;

        private static readonly JsonSerializerSettings JsonSettings = new()
        {
            Formatting = Formatting.Indented
        };

        private readonly string _dir;
        private readonly ILog _log;

        /// <summary>
        /// Serializes file access for this store instance so load-modify-save operations cannot race.
        /// SemaphoreSlim is not reentrant, so the gate is acquired only in public entry points.
        /// </summary>
        private readonly SemaphoreSlim _gate = new(1, 1);

        /// <summary>Creates a file-backed Lua mod store under CoreAI persistent data.</summary>
        /// <param name="rootDirectory">
        /// Optional override for the storage directory; defaults to the CoreAI Lua mod folder under
        /// <see cref="Application.persistentDataPath"/>.
        /// </param>
        /// <param name="log">Optional logger.</param>
        public FileLuaModStore(string rootDirectory = null, ILog log = null)
        {
            _dir = !string.IsNullOrWhiteSpace(rootDirectory)
                ? rootDirectory.Trim()
                : Path.Combine(Application.persistentDataPath, CoreAiPersistentPaths.RootFolderName,
                    CoreAiPersistentPaths.LuaMods);
            _log = log;
        }

        /// <inheritdoc />
        public string Get(string modId, string key)
        {
            if (string.IsNullOrWhiteSpace(modId) || string.IsNullOrWhiteSpace(key))
            {
                return "";
            }

            _gate.Wait();
            try
            {
                Dictionary<string, string> values = LoadModCore(modId);
                return values.TryGetValue(key, out string value) ? value ?? "" : "";
            }
            finally
            {
                _gate.Release();
            }
        }

        /// <inheritdoc />
        public void Set(string modId, string key, string value)
        {
            if (string.IsNullOrWhiteSpace(modId) || string.IsNullOrWhiteSpace(key))
            {
                return;
            }

            if (value != null && System.Text.Encoding.UTF8.GetByteCount(value) > MaxValueBytes)
            {
                throw new InvalidOperationException(
                    $"Lua mod store value exceeds {MaxValueBytes} bytes for mod '{modId}', key '{key}'.");
            }

            _gate.Wait();
            try
            {
                SetCore(modId, key, value);
            }
            finally
            {
                _gate.Release();
            }
        }

        private void SetCore(string modId, string key, string value)
        {
            Dictionary<string, string> values = LoadModCore(modId);
            if (value == null)
            {
                if (values.Remove(key))
                {
                    SaveModCore(modId, values);
                }

                return;
            }

            if (!values.ContainsKey(key) && values.Count >= MaxKeysPerMod)
            {
                throw new InvalidOperationException(
                    $"Lua mod store exceeds {MaxKeysPerMod} keys for mod '{modId}'.");
            }

            values[key] = value;
            SaveModCore(modId, values);
        }

        /// <inheritdoc />
        public void Clear(string modId)
        {
            if (string.IsNullOrWhiteSpace(modId))
            {
                return;
            }

            _gate.Wait();
            try
            {
                ClearCore(modId);
            }
            finally
            {
                _gate.Release();
            }
        }

        private Dictionary<string, string> LoadModCore(string modId)
        {
            try
            {
                string path = GetPath(modId);
                if (!File.Exists(path))
                {
                    return new Dictionary<string, string>();
                }

                string json = File.ReadAllText(path);
                return JsonConvert.DeserializeObject<Dictionary<string, string>>(json, JsonSettings)
                       ?? new Dictionary<string, string>();
            }
            catch (Exception ex)
            {
                _log?.Error($"[FileLuaModStore] Load failed for {modId}: {ex}");
                return new Dictionary<string, string>();
            }
        }

        private void SaveModCore(string modId, Dictionary<string, string> values)
        {
            try
            {
                EnsureDir();
                string json = JsonConvert.SerializeObject(values, JsonSettings);
                AtomicWriteAllText(GetPath(modId), json, _log);
                PersistFsForWebGl();
            }
            catch (Exception ex)
            {
                _log?.Error($"[FileLuaModStore] Save failed for {modId}: {ex}");
            }
        }

        private void ClearCore(string modId)
        {
            try
            {
                string path = GetPath(modId);
                if (File.Exists(path))
                {
                    File.Delete(path);
                    PersistFsForWebGl();
                }
            }
            catch (Exception ex)
            {
                _log?.Error($"[FileLuaModStore] Clear failed for {modId}: {ex}");
            }
        }

        /// <summary>Releases the internal file-access semaphore.</summary>
        public void Dispose()
        {
            _gate.Dispose();
        }

        private string GetPath(string modId)
        {
            return Path.Combine(_dir, $"{SanitizedFileStem(modId)}.json");
        }

        /// <summary>
        /// Maps a raw role id to a unique file stem. Invalid filename characters are replaced, and
        /// when the replacement changed anything a short hash of the raw id is appended so distinct
        /// ids like "A/B" and "A_B" cannot collide on the same file.
        /// </summary>
        private static string SanitizedFileStem(string roleId)
        {
            string safe = string.Join("_", roleId.Split(Path.GetInvalidFileNameChars()));
            if (string.Equals(safe, roleId, StringComparison.Ordinal))
            {
                return safe;
            }

            uint hash = 2166136261u;
            foreach (char c in roleId)
            {
                hash = (hash ^ c) * 16777619u;
            }

            return $"{safe}_{hash:x8}";
        }

        private void EnsureDir()
        {
            if (!Directory.Exists(_dir))
            {
                Directory.CreateDirectory(_dir);
            }
        }

        /// <summary>
        /// Writes <paramref name="contents"/> to <paramref name="path"/> atomically by writing to a temp
        /// file first and then swapping it into place, so a crash mid-write cannot corrupt the existing file.
        /// </summary>
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
                log?.Error($"[FileLuaModStore] Atomic write failed for {path}: {ex}");
                if (File.Exists(tmpPath))
                {
                    try
                    {
                        File.Delete(tmpPath);
                    }
                    catch (Exception cleanupEx)
                    {
                        log?.Error($"[FileLuaModStore] Atomic write cleanup failed for {tmpPath}: {cleanupEx}");
                    }
                }

                throw;
            }
        }
    }
}