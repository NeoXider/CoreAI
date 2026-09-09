using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Logging;
using Newtonsoft.Json;

namespace CoreAI.Ai
{
    /// <summary>
    /// Persists per-role conversation summaries under a host-provided directory (portable filesystem).
    /// <para>
    /// Стор портативный и сам не знает, что на WebGL каталог <c>persistentDataPath</c> — это MEMFS в памяти
    /// вкладки, а в IndexedDB его доводит отдельный вызов <c>FS.syncfs</c>. Поэтому после каждой записи и
    /// удаления он зовёт хук долговечности, куда Unity-хост передаёт <c>CoreAiWebGlPersistence.Sync</c>
    /// (синхронный путь) или <c>SyncAsync</c> (асинхронный путь); на остальных платформах хук не нужен:
    /// там запись долговечна с момента возврата из <see cref="File"/>.
    /// </para>
    /// <para>
    /// Честность: неудачные save/clear бросают исключение, а не возвращают молчаливый успех; строгое
    /// асинхронное чтение пробрасывает битое или нечитаемое хранилище вместо тихой перезаписи, отсутствие
    /// файла остаётся пустой строкой. Отмена вызывающего до начала ввода-вывода предотвращает мутацию;
    /// подтверждение долговечности после коммита всегда ожидается без отмены вызывающего и сообщает
    /// реальный исход. Колбэк долговечности обязан сам ограничивать время завершения: стор не ставит
    /// поверх него параллельный таймаут (на проде им владеет <c>CoreAiWebGlPersistence.SyncAsync</c>).
    /// </para>
    /// <para>
    /// Синхронный путь выполняет блокирующий дисковый ввод-вывод на вызывающем потоке и fail-fast бросает
    /// <see cref="InvalidOperationException"/>, когда целевой файл занят активной асинхронной операцией, —
    /// вместо блокировки главного потока Unity. Файловые гейты общие на процесс и на целевой путь (паттерн
    /// <c>FileAgentMemoryStore.MutationLocks</c>): они намеренно никогда не выселяются и не освобождаются,
    /// поэтому <see cref="Dispose"/> лишь запрещает новые вызовы, а принятые операции спокойно завершаются.
    /// </para>
    /// </summary>
    public sealed class FileConversationSummaryStore : IConversationSummaryStore, IAsyncConversationSummaryStore, IDisposable
    {
        private static readonly JsonSerializerSettings JsonSettings = new()
        {
            Formatting = Formatting.None
        };

        /// <summary>
        /// Process-wide mutation gates keyed by each summary file path. Entries are intentionally never
        /// evicted or disposed: a caller could already hold the instance fetched from this dictionary
        /// while an eviction hands a second caller a fresh one, silently breaking the mutual exclusion.
        /// The key set is bounded by the number of distinct roles a host ever creates.
        /// </summary>
        private static readonly ConcurrentDictionary<string, SemaphoreSlim> MutationGates =
            new(Path.DirectorySeparatorChar == '\\' ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

        private readonly string _dir;
        private readonly ILog _log;
        private readonly Func<bool> _afterWrite;
        private readonly Func<CancellationToken, Task<bool>> _afterWriteAsync;

        private readonly object _lifeLock = new();
        private int _activeCount;
        private bool _disposed;

        private sealed class PersistedDto
        {
            public string Summary { get; set; } = "";
        }

        /// <summary>Creates store writing to <paramref name="rootDirectory"/>.</summary>
        /// <param name="rootDirectory">Directory path; created on first write.</param>
        /// <param name="log">Optional logger.</param>
        /// <param name="afterWrite">
        /// Хук долговечности: вызывается после каждой успешной записи и удаления файла. Возвращает
        /// <c>false</c>, если довести запись до долговечного хранилища не удалось даже поставить в очередь —
        /// save/clear бросают честный <see cref="IOException"/>: запись в VFS состоялась, но долговечность
        /// не подтверждена. <c>null</c> — платформа долговечна сама по себе.
        /// </param>
        /// <param name="afterWriteAsync">
        /// Асинхронный хук долговечности для async-пути. Вызывается с <see cref="CancellationToken.None"/>
        /// после зафиксированной мутации, вне файловых гейтов, на исходном контексте вызывающего; обязан
        /// сам ограничивать время завершения. <c>false</c> или исключение сообщают реальный исход через
        /// <see cref="IOException"/>. <c>null</c> — используется синхронный хук.
        /// </param>
        public FileConversationSummaryStore(
            string rootDirectory,
            ILog log = null,
            Func<bool> afterWrite = null,
            Func<CancellationToken, Task<bool>> afterWriteAsync = null)
        {
            if (string.IsNullOrWhiteSpace(rootDirectory))
            {
                throw new ArgumentException("Root directory is required.", nameof(rootDirectory));
            }

            _dir = Path.GetFullPath(rootDirectory.Trim());
            _log = log;
            _afterWrite = afterWrite;
            _afterWriteAsync = afterWriteAsync;
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

            EnterOperation();
            try
            {
                SemaphoreSlim gate = ForPath(GetPath(roleId));
                if (!gate.Wait(0))
                {
                    throw new InvalidOperationException(
                        "[FileConversationSummaryStore] Target file is busy with an active async operation; " +
                        "await the async API instead of blocking the calling (Unity main) thread on sync I/O.");
                }

                try
                {
                    return LoadSummaryCore(roleId);
                }
                finally
                {
                    gate.Release();
                }
            }
            finally
            {
                ExitOperation();
            }
        }

        /// <summary>
        /// Async variant of <see cref="LoadSummary"/> that performs the file read off the calling thread.
        /// </summary>
        public async Task<string> LoadSummaryAsync(string roleId, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(roleId))
            {
                return "";
            }

            ThrowIfDisposed();
            cancellationToken.ThrowIfCancellationRequested();
            EnterOperation();
            try
            {
                // WHY: awaited with default capture so the logging below runs on the original entry
                // context; the disk helper itself uses ConfigureAwait(false) internally.
                string summary = await ReadCommittedAsync(roleId, cancellationToken);
                return summary;
            }
            catch (Exception ex) when (!(ex is OperationCanceledException))
            {
                LogStorageFailure("Load", ex);
                throw;
            }
            finally
            {
                ExitOperation();
            }
        }

        private async Task<string> ReadCommittedAsync(string roleId, CancellationToken cancellationToken)
        {
            SemaphoreSlim gate = ForPath(GetPath(roleId));
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                string result = await RunOffThread(() =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    return ReadSummaryCoreStrict(roleId);
                }).ConfigureAwait(false);
                return result;
            }
            finally
            {
                gate.Release();
            }
        }

        private string LoadSummaryCore(string roleId)
        {
            try
            {
                return ReadSummaryCoreStrict(roleId);
            }
            catch (FileNotFoundException)
            {
                return "";
            }
            catch (DirectoryNotFoundException)
            {
                return "";
            }
            catch (Exception ex)
            {
                LogStorageFailure("Load", ex);
                return "";
            }
        }

        /// <summary>
        /// Reads the committed summary; missing file stays empty, corrupt or unreadable storage throws
        /// instead of silently overwriting the existing data.
        /// </summary>
        private string ReadSummaryCoreStrict(string roleId)
        {
            string path = GetPath(roleId);
            ValidateExistingParent(path);
            string json;
            try { json = File.ReadAllText(path); }
            catch (FileNotFoundException) { return ""; }
            catch (DirectoryNotFoundException) { return ""; }
            if (string.IsNullOrWhiteSpace(json))
            {
                throw new InvalidDataException("[FileConversationSummaryStore] Summary file is empty.");
            }

            PersistedDto dto;
            try
            {
                dto = JsonConvert.DeserializeObject<PersistedDto>(json, JsonSettings);
            }
            catch (JsonException ex)
            {
                throw new InvalidDataException("[FileConversationSummaryStore] Summary file is corrupt.", ex);
            }

            if (dto == null)
            {
                throw new InvalidDataException("[FileConversationSummaryStore] Summary file is corrupt.");
            }

            return dto.Summary?.Trim() ?? "";
        }

        /// <inheritdoc />
        public void SaveSummary(string roleId, string summary)
        {
            if (string.IsNullOrWhiteSpace(roleId))
            {
                return;
            }

            EnterOperation();
            try
            {
                RequireSynchronousConfirmation();
                SemaphoreSlim gate = ForPath(GetPath(roleId));
                if (!gate.Wait(0))
                {
                    throw new InvalidOperationException(
                        "[FileConversationSummaryStore] Target file is busy with an active async operation; " +
                        "await the async API instead of blocking the calling (Unity main) thread on sync I/O.");
                }

                try
                {
                    WriteSummaryCore(GetPath(roleId), summary ?? "");
                }
                finally
                {
                    gate.Release();
                }

                ConfirmDurabilitySync();
            }
            finally
            {
                ExitOperation();
            }
        }

        /// <summary>
        /// Async variant of <see cref="SaveSummary"/> that performs the atomic file write off the calling
        /// thread and awaits host durability confirmation on the original entry context.
        /// </summary>
        public async Task SaveSummaryAsync(string roleId, string summary, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(roleId))
            {
                return;
            }

            ThrowIfDisposed();
            cancellationToken.ThrowIfCancellationRequested();
            EnterOperation();
            try
            {
                // WHY: awaited with default capture so durability confirmation and logging below run on
                // the original entry context outside the file gate; the helper uses ConfigureAwait(false).
                await CommitSaveAsync(GetPath(roleId), summary ?? "", cancellationToken);
                await ConfirmDurabilityAsync();
            }
            catch (Exception ex) when (!(ex is OperationCanceledException))
            {
                LogStorageFailure("Save", ex);
                throw;
            }
            finally
            {
                ExitOperation();
            }
        }

        private async Task CommitSaveAsync(string path, string summary, CancellationToken cancellationToken)
        {
            SemaphoreSlim gate = ForPath(path);
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                await RunOffThread(() =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    PersistedDto dto = new() { Summary = summary };
                    string json = JsonConvert.SerializeObject(dto, JsonSettings);
                    AtomicWriteAllText(path, json, cancellationToken);
                }).ConfigureAwait(false);
            }
            finally
            {
                gate.Release();
            }
        }

        private void WriteSummaryCore(string path, string summary)
        {
            PersistedDto dto = new() { Summary = summary };
            string json = JsonConvert.SerializeObject(dto, JsonSettings);
            try
            {
                // WHY the directory is created inside this try: failing to create it IS a write
                // failure, and outside the try it was the one storage error that reached the caller
                // with nothing in the log — the same failure through the file itself was logged.
                EnsureDir();
                AtomicWriteAllText(path, json);
            }
            catch (Exception ex)
            {
                LogStorageFailure("Save", ex);
                throw;
            }
        }

        private void ConfirmDurabilitySync()
        {
            bool confirmed;
            try { confirmed = _afterWrite == null || _afterWrite(); }
            catch (Exception ex) { throw UnconfirmedDurability(ex); }
            if (!confirmed)
            {
                IOException failure = new(
                    "[FileConversationSummaryStore] VFS write committed but durability was not confirmed.");
                LogStorageFailure("Save", failure);
                throw failure;
            }
        }

        private async Task ConfirmDurabilityAsync()
        {
            bool confirmed;
            try
            {
                // WHY: committed mutation is already durable in-process; the host confirmation must be
                // awaited uncancelled so a caller cancelling after commit still learns the real outcome.
                confirmed = _afterWriteAsync != null
                    ? await _afterWriteAsync(CancellationToken.None)
                    : _afterWrite == null || _afterWrite();
            }
            catch (Exception ex) { throw UnconfirmedDurability(ex); }

            if (!confirmed)
            {
                throw new IOException(
                    "[FileConversationSummaryStore] VFS write committed but durability was not confirmed.");
            }
        }

        private static IOException UnconfirmedDurability(Exception cause) => new(
            "[FileConversationSummaryStore] VFS write committed but durability was not confirmed.", cause);

        private void RequireSynchronousConfirmation()
        {
            if (_afterWrite == null && _afterWriteAsync != null)
                throw new InvalidOperationException("Only async durability confirmation is configured; use the async summary API.");
        }

        /// <inheritdoc />
        public void ClearSummary(string roleId)
        {
            if (string.IsNullOrWhiteSpace(roleId))
            {
                return;
            }

            EnterOperation();
            try
            {
                RequireSynchronousConfirmation();
                string path = GetPath(roleId);
                SemaphoreSlim gate = ForPath(path);
                if (!gate.Wait(0))
                {
                    throw new InvalidOperationException(
                        "[FileConversationSummaryStore] Target file is busy with an active async operation; " +
                        "await the async API instead of blocking the calling (Unity main) thread on sync I/O.");
                }

                bool mutated;
                try
                {
                    mutated = DeleteSummaryCore(path);
                }
                finally
                {
                    gate.Release();
                }

                // WHY: A retry after unconfirmed deletion must flush again even when the VFS file is already absent.
                ConfirmDurabilitySync();
            }
            finally
            {
                ExitOperation();
            }
        }

        /// <summary>
        /// Async variant of <see cref="ClearSummary"/> that performs the file delete off the calling
        /// thread and awaits host durability confirmation on the original entry context.
        /// </summary>
        public async Task ClearSummaryAsync(string roleId, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(roleId))
            {
                return;
            }

            ThrowIfDisposed();
            cancellationToken.ThrowIfCancellationRequested();
            EnterOperation();
            try
            {
                // WHY: awaited with default capture so durability confirmation and logging below run on
                // the original entry context outside the file gate; the helper uses ConfigureAwait(false).
                await CommitClearAsync(GetPath(roleId), cancellationToken);
                await ConfirmDurabilityAsync();
            }
            catch (Exception ex) when (!(ex is OperationCanceledException))
            {
                LogStorageFailure("Clear", ex);
                throw;
            }
            finally
            {
                ExitOperation();
            }
        }

        private async Task<bool> CommitClearAsync(string path, CancellationToken cancellationToken)
        {
            SemaphoreSlim gate = ForPath(path);
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                bool result = await RunOffThread(() =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    return TryDeleteFile(path);
                }).ConfigureAwait(false);
                return result;
            }
            finally
            {
                gate.Release();
            }
        }

        private bool DeleteSummaryCore(string path)
        {
            try
            {
                return TryDeleteFile(path);
            }
            catch (Exception ex)
            {
                LogStorageFailure("Clear", ex);
                throw;
            }
        }

        private static bool TryDeleteFile(string path)
        {
            ValidateExistingParent(path);
            try { _ = File.GetAttributes(path); }
            catch (FileNotFoundException) { return false; }
            catch (DirectoryNotFoundException) { return false; }
            File.Delete(path);
            return true;
        }

        /// <summary>Distinguishes an absent directory from an unreadable path or a file used as a directory.</summary>
        private static void ValidateExistingParent(string path)
        {
            string parent = Path.GetDirectoryName(path);
            while (!string.IsNullOrEmpty(parent))
            {
                try
                {
                    FileAttributes attributes = File.GetAttributes(parent);
                    if ((attributes & FileAttributes.Directory) == 0)
                        throw new IOException("Summary storage parent is not a directory.");
                    return;
                }
                catch (FileNotFoundException) { }
                catch (DirectoryNotFoundException) { }
                parent = Path.GetDirectoryName(parent);
            }
        }

        private void LogStorageFailure(string operation, Exception ex)
        {
            // WHY: roleId may be a scoped persistence key and exception messages commonly repeat the
            // filesystem path. Keep diagnostics actionable without disclosing either identity data or keys.
            _log?.Error($"[FileConversationSummaryStore] {operation} failed ({ex.GetType().Name}).");
        }

        /// <summary>
        /// Rejects new operations; accepted operations finish safely. Shared per-path gates are retained
        /// for the process lifetime and are never disposed, so disposal can never race in-flight waits.
        /// </summary>
        public void Dispose()
        {
            lock (_lifeLock)
            {
                _disposed = true;
            }
        }

        private void ThrowIfDisposed()
        {
            lock (_lifeLock)
            {
                if (_disposed)
                {
                    throw new ObjectDisposedException(nameof(FileConversationSummaryStore));
                }
            }
        }

        private void EnterOperation()
        {
            lock (_lifeLock)
            {
                if (_disposed)
                {
                    throw new ObjectDisposedException(nameof(FileConversationSummaryStore));
                }

                _activeCount++;
            }
        }

        private void ExitOperation()
        {
            lock (_lifeLock)
            {
                _activeCount--;
            }
        }

        private static SemaphoreSlim ForPath(string path)
        {
            return MutationGates.GetOrAdd(path, _ => new SemaphoreSlim(1, 1));
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
        /// Writes <paramref name="contents"/> to <paramref name="path"/> atomically by writing to a unique
        /// temp file first and then swapping it into place, so a crash mid-write cannot corrupt the existing
        /// file and concurrent writers from distinct store instances cannot share one temp path. The final
        /// file name and JSON shape are unchanged. Callers hold the per-path gate, so the
        /// exists/replace-versus-move decision cannot race another in-process writer for the same target.
        /// </summary>
        private static void AtomicWriteAllText(string path, string contents, CancellationToken cancellationToken = default)
        {
            string dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            string tmpPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(tmpPath, contents);
                cancellationToken.ThrowIfCancellationRequested();
                if (File.Exists(path))
                {
                    File.Replace(tmpPath, path, null);
                }
                else
                {
                    File.Move(tmpPath, path);
                }
            }
            finally
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
            }
        }
    }
}
