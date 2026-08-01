using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Logging;
using Newtonsoft.Json;

namespace CoreAI.Ai
{
    /// <summary>
    /// Persists per-role conversation summaries under a host-provided directory (portable filesystem).
    /// </summary>
    public sealed class FileConversationSummaryStore : IConversationSummaryStore, IDisposable
    {
        private static readonly JsonSerializerSettings JsonSettings = new()
        {
            Formatting = Formatting.Indented
        };

        private readonly string _dir;
        private readonly ILog _log;

        /// <summary>
        /// Serializes file access for this store instance so the async thread-pool offloads cannot
        /// race the synchronous interface methods. Acquired only in public entry points (not reentrant).
        /// </summary>
        private readonly SemaphoreSlim _gate = new(1, 1);

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

        /// <summary>
        /// Runs file I/O on the thread pool so it does not stall the caller's (Unity main) thread.
        /// On WebGL (no threads) the work runs inline instead.
        /// </summary>
        private static Task RunOffThread(Action action)
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            action();
            return Task.CompletedTask;
#else
            return Task.Run(action);
#endif
        }

        /// <inheritdoc cref="RunOffThread(Action)"/>
        private static Task<T> RunOffThread<T>(Func<T> func)
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            return Task.FromResult(func());
#else
            return Task.Run(func);
#endif
        }

        /// <inheritdoc />
        public string LoadSummary(string roleId)
        {
            if (string.IsNullOrWhiteSpace(roleId))
            {
                return "";
            }

            _gate.Wait();
            try
            {
                return LoadSummaryCore(roleId);
            }
            finally
            {
                _gate.Release();
            }
        }

        /// <summary>
        /// Async variant of <see cref="LoadSummary"/> that performs the file read on the thread pool.
        /// </summary>
        public async Task<string> LoadSummaryAsync(string roleId)
        {
            if (string.IsNullOrWhiteSpace(roleId))
            {
                return "";
            }

            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                return await RunOffThread(() => LoadSummaryCore(roleId)).ConfigureAwait(false);
            }
            finally
            {
                _gate.Release();
            }
        }

        private string LoadSummaryCore(string roleId)
        {
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
                LogStorageFailure("Load", ex);
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

            _gate.Wait();
            try
            {
                SaveSummaryCore(roleId, summary);
            }
            finally
            {
                _gate.Release();
            }
        }

        /// <summary>
        /// Async variant of <see cref="SaveSummary"/> that performs the atomic file write on the thread pool.
        /// </summary>
        public async Task SaveSummaryAsync(string roleId, string summary)
        {
            if (string.IsNullOrWhiteSpace(roleId))
            {
                return;
            }

            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                await RunOffThread(() => SaveSummaryCore(roleId, summary)).ConfigureAwait(false);
            }
            finally
            {
                _gate.Release();
            }
        }

        private void SaveSummaryCore(string roleId, string summary)
        {
            try
            {
                EnsureDir();
                PersistedDto dto = new() { Summary = summary ?? "" };
                string json = JsonConvert.SerializeObject(dto, JsonSettings);
                AtomicWriteAllText(GetPath(roleId), json);
            }
            catch (Exception ex)
            {
                LogStorageFailure("Save", ex);
            }
        }

        /// <inheritdoc />
        public void ClearSummary(string roleId)
        {
            if (string.IsNullOrWhiteSpace(roleId))
            {
                return;
            }

            _gate.Wait();
            try
            {
                ClearSummaryCore(roleId);
            }
            finally
            {
                _gate.Release();
            }
        }

        /// <summary>
        /// Async variant of <see cref="ClearSummary"/> that performs the file delete on the thread pool.
        /// </summary>
        public async Task ClearSummaryAsync(string roleId)
        {
            if (string.IsNullOrWhiteSpace(roleId))
            {
                return;
            }

            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                await RunOffThread(() => ClearSummaryCore(roleId)).ConfigureAwait(false);
            }
            finally
            {
                _gate.Release();
            }
        }

        private void ClearSummaryCore(string roleId)
        {
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
                LogStorageFailure("Clear", ex);
            }
        }

        private void LogStorageFailure(string operation, Exception ex)
        {
            // WHY: roleId may be a scoped persistence key and exception messages commonly repeat the
            // filesystem path. Keep diagnostics actionable without disclosing either identity data or keys.
            _log?.Error($"[FileConversationSummaryStore] {operation} failed ({ex.GetType().Name}).");
        }

        /// <summary>Releases the internal file-access semaphore.</summary>
        public void Dispose()
        {
            _gate.Dispose();
        }

        private string GetPath(string roleId)
        {
            return Path.Combine(_dir, $"{SanitizedFileStem(roleId)}.json");
        }

        /// <summary>
        /// Maps a raw role id to a unique file stem. Invalid filename characters are replaced, and
        /// when the replacement changed anything a short hash of the raw id is appended so distinct
        /// ids like "A/B" and "A_B" cannot collide on the same file.
        /// </summary>
        internal static string SanitizedFileStem(string roleId)
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
        private static void AtomicWriteAllText(string path, string contents)
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
            catch
            {
                if (File.Exists(tmpPath))
                {
                    try
                    {
                        File.Delete(tmpPath);
                    }
                    catch
                    {
                        /* best-effort cleanup */
                    }
                }

                throw;
            }
        }
    }
}
