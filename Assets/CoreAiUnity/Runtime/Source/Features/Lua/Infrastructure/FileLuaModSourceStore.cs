#if COREAI_HAS_MOONSHARP && !COREAI_NO_LUA
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using CoreAI.Ai;
using CoreAI.Logging;
using Newtonsoft.Json;
using UnityEngine;

namespace CoreAI.Infrastructure.Lua
{
    /// <summary>
    /// File-backed Unity implementation of <see cref="ILuaModSourceStore"/>: the persistent package
    /// store for Lua mods (the source plus its <see cref="LuaModManifest"/>), so mods survive a
    /// restart and can be shared between hosts. Distinct from <see cref="FileLuaModStore"/>, which
    /// persists the per-mod runtime key/value scratch space backing <c>store_set</c>/<c>store_get</c>.
    /// <para>
    /// Each mod lives in its own folder
    /// <c>persistentDataPath/CoreAI/Mods/&lt;sanitizedId&gt;/{manifest.json, main.lua}</c>. Writes are
    /// serialized through a <see cref="SemaphoreSlim"/> gate and applied atomically (temp file then
    /// swap) so a crash mid-write cannot corrupt an existing package.
    /// </para>
    /// </summary>
    public sealed class FileLuaModSourceStore : ILuaModSourceStore, IDisposable
    {
#if UNITY_WEBGL && !UNITY_EDITOR
        [DllImport("__Internal")]
        private static extern void CoreAi_PersistFsSync();
#endif

        /// <summary>
        /// On WebGL pushes the in-memory IDBFS tree into IndexedDB so writes survive a tab reload (Unity
        /// only auto-syncs on Application.Quit, which a reload does not invoke). No-op on other platforms.
        /// Without this, persisted mod packages are lost on WebGL despite the durability the class promises.
        /// </summary>
        private static void PersistFsForWebGl()
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            try { CoreAi_PersistFsSync(); } catch { /* best-effort flush */ }
#endif
        }

        private const string ManifestFileName = "manifest.json";
        private const string SourceFileName = "main.lua";

        private static readonly JsonSerializerSettings JsonSettings = new()
        {
            Formatting = Formatting.Indented
        };

        private static readonly IReadOnlyList<LuaModManifest> Empty = new LuaModManifest[0];

        private readonly string _dir;
        private readonly ILog _log;

        /// <summary>
        /// Serializes file access for this store instance so load-modify-save operations cannot race.
        /// SemaphoreSlim is not reentrant, so the gate is acquired only in public entry points.
        /// </summary>
        private readonly SemaphoreSlim _gate = new(1, 1);

        /// <summary>Creates a file-backed Lua mod package store under CoreAI persistent data.</summary>
        /// <param name="rootDirectory">
        /// Optional override for the storage directory; defaults to the CoreAI mod package folder under
        /// <see cref="Application.persistentDataPath"/>.
        /// </param>
        /// <param name="log">Optional logger.</param>
        public FileLuaModSourceStore(string rootDirectory = null, ILog log = null)
        {
            _dir = !string.IsNullOrWhiteSpace(rootDirectory)
                ? rootDirectory.Trim()
                : Path.Combine(Application.persistentDataPath, CoreAiPersistentPaths.RootFolderName,
                    CoreAiPersistentPaths.ModPackages);
            _log = log;
        }

        /// <inheritdoc />
        public void Save(string id, string source, LuaModManifest manifest)
        {
            string modId = Normalize(id);
            if (modId.Length == 0)
            {
                return;
            }

            _gate.Wait();
            try
            {
                SaveCore(modId, source ?? "", manifest);
            }
            finally
            {
                _gate.Release();
            }
        }

        private void SaveCore(string modId, string source, LuaModManifest manifest)
        {
            try
            {
                LuaModManifest toWrite = manifest ?? new LuaModManifest();
                // The folder name is derived from the id, so always store the canonical id in the
                // manifest regardless of what the caller passed.
                toWrite.Id = modId;

                string modDir = GetModDir(modId);
                if (!Directory.Exists(modDir))
                {
                    Directory.CreateDirectory(modDir);
                }

                string manifestJson = JsonConvert.SerializeObject(toWrite, JsonSettings);
                AtomicWriteAllText(Path.Combine(modDir, ManifestFileName), manifestJson, _log);
                AtomicWriteAllText(Path.Combine(modDir, SourceFileName), source, _log);
                PersistFsForWebGl();
            }
            catch (Exception ex)
            {
                _log?.Error($"[FileLuaModSourceStore] Save failed for {modId}: {ex}");
            }
        }

        /// <inheritdoc />
        public bool TryLoad(string id, out string source, out LuaModManifest manifest)
        {
            source = "";
            manifest = null;

            string modId = Normalize(id);
            if (modId.Length == 0)
            {
                return false;
            }

            _gate.Wait();
            try
            {
                string modDir = GetModDir(modId);
                string manifestPath = Path.Combine(modDir, ManifestFileName);
                string sourcePath = Path.Combine(modDir, SourceFileName);
                if (!File.Exists(manifestPath) || !File.Exists(sourcePath))
                {
                    return false;
                }

                LuaModManifest loaded = ReadManifest(manifestPath);
                if (loaded == null)
                {
                    return false;
                }

                source = File.ReadAllText(sourcePath);
                manifest = loaded;
                return true;
            }
            catch (Exception ex)
            {
                _log?.Error($"[FileLuaModSourceStore] Load failed for {modId}: {ex}");
                source = "";
                manifest = null;
                return false;
            }
            finally
            {
                _gate.Release();
            }
        }

        /// <inheritdoc />
        public IReadOnlyList<LuaModManifest> List()
        {
            _gate.Wait();
            try
            {
                if (!Directory.Exists(_dir))
                {
                    return Empty;
                }

                List<LuaModManifest> result = new();
                foreach (string modDir in Directory.GetDirectories(_dir))
                {
                    string manifestPath = Path.Combine(modDir, ManifestFileName);
                    if (!File.Exists(manifestPath))
                    {
                        continue;
                    }

                    LuaModManifest manifest = ReadManifest(manifestPath);
                    if (manifest != null)
                    {
                        result.Add(manifest);
                    }
                }

                return result;
            }
            catch (Exception ex)
            {
                _log?.Error($"[FileLuaModSourceStore] List failed: {ex}");
                return Empty;
            }
            finally
            {
                _gate.Release();
            }
        }

        /// <inheritdoc />
        public void SetActive(string id, bool active)
        {
            string modId = Normalize(id);
            if (modId.Length == 0)
            {
                return;
            }

            _gate.Wait();
            try
            {
                string manifestPath = Path.Combine(GetModDir(modId), ManifestFileName);
                if (!File.Exists(manifestPath))
                {
                    return;
                }

                LuaModManifest manifest = ReadManifest(manifestPath);
                if (manifest == null)
                {
                    return;
                }

                manifest.Active = active;
                string manifestJson = JsonConvert.SerializeObject(manifest, JsonSettings);
                AtomicWriteAllText(manifestPath, manifestJson, _log);
                PersistFsForWebGl();
            }
            catch (Exception ex)
            {
                _log?.Error($"[FileLuaModSourceStore] SetActive failed for {modId}: {ex}");
            }
            finally
            {
                _gate.Release();
            }
        }

        /// <inheritdoc />
        public void Delete(string id)
        {
            string modId = Normalize(id);
            if (modId.Length == 0)
            {
                return;
            }

            _gate.Wait();
            try
            {
                string modDir = GetModDir(modId);
                if (Directory.Exists(modDir))
                {
                    Directory.Delete(modDir, recursive: true);
                    PersistFsForWebGl();
                }
            }
            catch (Exception ex)
            {
                _log?.Error($"[FileLuaModSourceStore] Delete failed for {modId}: {ex}");
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

        private LuaModManifest ReadManifest(string manifestPath)
        {
            try
            {
                string json = File.ReadAllText(manifestPath);
                return JsonConvert.DeserializeObject<LuaModManifest>(json, JsonSettings);
            }
            catch (Exception ex)
            {
                _log?.Error($"[FileLuaModSourceStore] Manifest read failed for {manifestPath}: {ex}");
                return null;
            }
        }

        private string GetModDir(string modId)
        {
            string combined = Path.Combine(_dir, SanitizedFolderName(modId));
            return EnsureWithinRoot(combined);
        }

        /// <summary>
        /// Guards against path traversal: <see cref="Path.GetInvalidFileNameChars"/> does NOT strip
        /// <c>..</c>, so an id like <c>..</c> or <c>../x</c> could resolve outside <see cref="_dir"/> and
        /// read/write/delete arbitrary files (a recursive <see cref="Directory.Delete(string,bool)"/> on the
        /// escaped folder is especially destructive). Reject any combined path that escapes the store root.
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
                    "[FileLuaModSourceStore] Mod id resolves outside the store root; rejected to prevent path traversal.");
            }

            return fullPath;
        }

        /// <summary>
        /// Maps a raw mod id to a unique folder name. Invalid filename characters are replaced, and
        /// when the replacement changed anything a short hash of the raw id is appended so distinct
        /// ids like "A/B" and "A_B" cannot collide on the same folder.
        /// </summary>
        private static string SanitizedFolderName(string modId)
        {
            string safe = string.Join("_", modId.Split(Path.GetInvalidFileNameChars()));
            if (string.Equals(safe, modId, StringComparison.Ordinal))
            {
                return safe;
            }

            uint hash = 2166136261u;
            foreach (char c in modId)
            {
                hash = (hash ^ c) * 16777619u;
            }

            return $"{safe}_{hash:x8}";
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
                log?.Error($"[FileLuaModSourceStore] Atomic write failed for {path}: {ex}");
                if (File.Exists(tmpPath))
                {
                    try
                    {
                        File.Delete(tmpPath);
                    }
                    catch (Exception cleanupEx)
                    {
                        log?.Error($"[FileLuaModSourceStore] Atomic write cleanup failed for {tmpPath}: {cleanupEx}");
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
#endif
