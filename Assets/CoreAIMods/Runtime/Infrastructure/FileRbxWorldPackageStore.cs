using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using CoreAI.Infrastructure;
using Cysharp.Threading.Tasks;
using UnityEngine;

namespace CoreAI.Mods.WorldPackages
{
    /// <summary>Result of a durable package write or pre-mutation backup attempt.</summary>
    public sealed class RbxWorldPackageWriteResult
    {
        internal RbxWorldPackageWriteResult(bool success, string path, string error)
        {
            Success = success;
            Path = path ?? "";
            Error = error ?? "";
        }

        public bool Success { get; }

        public string Path { get; }

        public string Error { get; }
    }

    /// <summary>
    /// Async storage boundary for create-once manual slots and trigger-labelled autosave packages.
    /// Implementations may use IDBFS, native files, cloud storage, or another non-blocking backend.
    /// </summary>
    public interface IRbxWorldPackageStore
    {
        UniTask<RbxWorldPackageWriteResult> CreateManualAsync(
            string slot,
            RbxWorldPackagePayload payload,
            CancellationToken cancellationToken = default);

        UniTask<RbxWorldPackageWriteResult> CreateAutoAsync(
            string trigger,
            RbxWorldPackagePayload payload,
            CancellationToken cancellationToken = default);

        UniTask<RbxWorldPackagePayload> LoadManualAsync(
            string slot,
            CancellationToken cancellationToken = default);

        UniTask<RbxWorldPackagePayload> LoadAutoAsync(
            string fileName,
            CancellationToken cancellationToken = default);

        IReadOnlyList<string> ListManualSlots();

        IReadOnlyList<string> ListAutoFiles();

        IReadOnlyList<RbxAutoSaveInfo> ListAutoSaves();
    }

    /// <summary>Filesystem seam used to verify volatile-versus-durable world-package behavior.</summary>
    public interface IRbxWorldPackageFileSystem
    {
        bool DirectoryExists(string path);

        void CreateDirectory(string path);

        bool FileExists(string path);

        long GetFileLength(string path);

        UniTask WriteAllBytesCreateNewAsync(
            string path,
            byte[] bytes,
            CancellationToken cancellationToken);

        UniTask<byte[]> ReadAllBytesAsync(string path, CancellationToken cancellationToken);

        void MoveCreateNew(string sourcePath, string destinationPath);

        void DeleteFile(string path);

        IReadOnlyList<string> GetFiles(string directory, string extension);
    }

    internal sealed class SystemRbxWorldPackageFileSystem : IRbxWorldPackageFileSystem
    {
        private const int IoChunkBytes = 64 * 1024;

        public bool DirectoryExists(string path)
        {
            return Directory.Exists(path);
        }

        public void CreateDirectory(string path)
        {
            Directory.CreateDirectory(path);
        }

        public bool FileExists(string path)
        {
            return File.Exists(path);
        }

        public long GetFileLength(string path)
        {
            return new FileInfo(path).Length;
        }

        public async UniTask WriteAllBytesCreateNewAsync(
            string path,
            byte[] bytes,
            CancellationToken cancellationToken)
        {
            using FileStream stream = new(
                path,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None);
            int offset = 0;
            while (offset < bytes.Length)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int count = Math.Min(IoChunkBytes, bytes.Length - offset);
                stream.Write(bytes, offset, count);
                offset += count;
                if (offset < bytes.Length)
                {
                    await UniTask.Yield(PlayerLoopTiming.Update, cancellationToken);
                }
            }

            stream.Flush();
        }

        public async UniTask<byte[]> ReadAllBytesAsync(
            string path,
            CancellationToken cancellationToken)
        {
            long length = GetFileLength(path);
            if (length > int.MaxValue)
            {
                throw new IOException("World package is too large to read into memory.");
            }

            byte[] bytes = new byte[(int)length];
            using FileStream stream = new(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read);
            int offset = 0;
            while (offset < bytes.Length)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int count = stream.Read(bytes, offset, Math.Min(IoChunkBytes, bytes.Length - offset));
                if (count == 0)
                {
                    throw new EndOfStreamException("World package ended before its declared length.");
                }

                offset += count;
                if (offset < bytes.Length)
                {
                    await UniTask.Yield(PlayerLoopTiming.Update, cancellationToken);
                }
            }

            return bytes;
        }

        public void MoveCreateNew(string sourcePath, string destinationPath)
        {
            File.Move(sourcePath, destinationPath);
        }

        public void DeleteFile(string path)
        {
            File.Delete(path);
        }

        public IReadOnlyList<string> GetFiles(string directory, string extension)
        {
            return Directory.GetFiles(directory, "*" + extension);
        }
    }

    /// <summary>
    /// Native/IDBFS implementation. File operations are isolated behind the async store contract;
    /// every mutation requests the shared WebGL persistence sync before reporting success.
    /// </summary>
    public sealed class FileRbxWorldPackageStore : IRbxWorldPackageStore
    {
        private sealed class RotatedFile
        {
            public RotatedFile(string path, byte[] bytes)
            {
                Path = path;
                Bytes = bytes;
            }

            public string Path { get; }

            public byte[] Bytes { get; }
        }

        public const int DefaultAutoBackupCapacity = 10;
        public const int MaximumWebGlSafePackageBytes = 4 * 1024 * 1024;
        public const int MaximumWebGlSafeInstances = 4096;
        public const int MaximumWebGlSafeCollectionItems = 32768;
        public const int MaximumWebGlSafeTextCharacters = 2 * 1024 * 1024;

        private const string Extension = ".world";
        private const int MaximumNameLength = 64;

        private readonly string _manualDirectory;
        private readonly string _autoDirectory;
        private readonly int _autoBackupCapacity;
        private readonly Func<CancellationToken, UniTask<bool>> _persistenceSyncAsync;
        private readonly Func<DateTime> _utcNow;
        private readonly IRbxWorldPackageFileSystem _fileSystem;
        private readonly SemaphoreSlim _mutationGate = new(1, 1);

        public FileRbxWorldPackageStore(
            string rootDirectory = null,
            int autoBackupCapacity = DefaultAutoBackupCapacity,
            Func<CancellationToken, UniTask<bool>> persistenceSyncAsync = null,
            Func<DateTime> utcNow = null,
            IRbxWorldPackageFileSystem fileSystem = null)
        {
            if (autoBackupCapacity <= 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(autoBackupCapacity),
                    autoBackupCapacity,
                    "Auto backup capacity must be positive.");
            }

            string resolvedRoot = string.IsNullOrWhiteSpace(rootDirectory)
                ? Path.Combine(
                    Application.persistentDataPath,
                    CoreAiPersistentPaths.RootFolderName,
                    "Saves")
                : Path.GetFullPath(rootDirectory);
            _manualDirectory = Path.Combine(resolvedRoot, "Manual");
            _autoDirectory = Path.Combine(resolvedRoot, "Auto");
            _autoBackupCapacity = autoBackupCapacity;
            _persistenceSyncAsync = persistenceSyncAsync ?? RequestPersistenceCompletionAsync;
            _utcNow = utcNow ?? (() => DateTime.UtcNow);
            _fileSystem = fileSystem ?? new SystemRbxWorldPackageFileSystem();
        }

        public async UniTask<RbxWorldPackageWriteResult> CreateManualAsync(
            string slot,
            RbxWorldPackagePayload payload,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await _mutationGate.WaitAsync(cancellationToken);
            try
            {
                string safeSlot = ValidateName(slot, "manual slot");
                string path = Path.Combine(_manualDirectory, safeSlot + Extension);
                if (_fileSystem.FileExists(path))
                {
                    return new RbxWorldPackageWriteResult(
                        false,
                        path,
                        "Manual slot '" + safeSlot + "' already exists and cannot be overwritten.");
                }

                return await WriteCreateOnceAsync(path, payload, false, cancellationToken);
            }
            finally
            {
                _mutationGate.Release();
            }
        }

        public async UniTask<RbxWorldPackageWriteResult> CreateAutoAsync(
            string trigger,
            RbxWorldPackagePayload payload,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await _mutationGate.WaitAsync(cancellationToken);
            try
            {
                string safeTrigger = SanitizeTrigger(trigger);
                string timestamp = NormalizeUtc(_utcNow()).ToString(
                    "yyyyMMdd'T'HHmmssfff'Z'", System.Globalization.CultureInfo.InvariantCulture);
                _fileSystem.CreateDirectory(_autoDirectory);
                string path = AllocateUniqueAutoPath(timestamp, safeTrigger);
                return await WriteCreateOnceAsync(path, payload, true, cancellationToken);
            }
            finally
            {
                _mutationGate.Release();
            }
        }

        public UniTask<RbxWorldPackagePayload> LoadManualAsync(
            string slot,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string safeSlot = ValidateName(slot, "manual slot");
            string path = Path.Combine(_manualDirectory, safeSlot + Extension);
            return ReadValidatedAsync(path, cancellationToken);
        }

        public UniTask<RbxWorldPackagePayload> LoadAutoAsync(
            string fileName,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string safeName = ValidateAutoFileName(fileName);
            string path = Path.Combine(_autoDirectory, safeName);
            return ReadValidatedAsync(path, cancellationToken);
        }

        public IReadOnlyList<string> ListManualSlots()
        {
            if (!_fileSystem.DirectoryExists(_manualDirectory))
            {
                return Array.Empty<string>();
            }

            IReadOnlyList<string> paths = _fileSystem.GetFiles(_manualDirectory, Extension);
            List<string> slots = new(paths.Count);
            foreach (string path in paths)
            {
                slots.Add(Path.GetFileNameWithoutExtension(path));
            }

            slots.Sort(StringComparer.Ordinal);
            return slots;
        }

        public IReadOnlyList<string> ListAutoFiles()
        {
            if (!_fileSystem.DirectoryExists(_autoDirectory))
            {
                return Array.Empty<string>();
            }

            IReadOnlyList<string> paths = _fileSystem.GetFiles(_autoDirectory, Extension);
            List<string> names = new(paths.Count);
            foreach (string path in paths)
            {
                names.Add(Path.GetFileName(path));
            }

            names.Sort(StringComparer.Ordinal);
            return names;
        }

        public IReadOnlyList<RbxAutoSaveInfo> ListAutoSaves()
        {
            if (!_fileSystem.DirectoryExists(_autoDirectory))
            {
                return Array.Empty<RbxAutoSaveInfo>();
            }

            IReadOnlyList<string> paths = _fileSystem.GetFiles(_autoDirectory, Extension);
            List<RbxAutoSaveInfo> infos = new(paths.Count);
            foreach (string path in paths)
            {
                string fileName = Path.GetFileName(path);
                long size = 0L;
                try
                {
                    size = _fileSystem.GetFileLength(path);
                }
                catch
                {
                }

                string trigger = ParseTrigger(fileName);
                DateTime timestamp = ParseTimestamp(fileName);
                infos.Add(new RbxAutoSaveInfo(fileName, trigger, timestamp, size));
            }

            infos.Sort((a, b) => string.CompareOrdinal(a.FileName, b.FileName));
            return infos;
        }

        private static string ParseTrigger(string fileName)
        {
            string nameWithoutExt = Path.GetFileNameWithoutExtension(fileName);
            string[] parts = nameWithoutExt.Split('-');
            if (parts.Length < 3)
            {
                return "";
            }

            return string.Join("-", parts, 2, parts.Length - 2);
        }

        private static DateTime ParseTimestamp(string fileName)
        {
            string nameWithoutExt = Path.GetFileNameWithoutExtension(fileName);
            int firstDash = nameWithoutExt.IndexOf('-');
            if (firstDash <= 0)
            {
                return default;
            }

            string timestampText = nameWithoutExt.Substring(0, firstDash);
            if (DateTime.TryParseExact(
                    timestampText,
                    "yyyyMMdd'T'HHmmssfff'Z'",
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AssumeUniversal
                    | System.Globalization.DateTimeStyles.AdjustToUniversal,
                    out DateTime result))
            {
                return result;
            }

            return default;
        }

        private async UniTask<RbxWorldPackageWriteResult> WriteCreateOnceAsync(
            string path,
            RbxWorldPackagePayload payload,
            bool rotateAutoRing,
            CancellationToken cancellationToken)
        {
            string temporaryPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            bool installed = false;
            List<RotatedFile> rotatedFiles = new();
            try
            {
                string directory = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(directory))
                {
                    _fileSystem.CreateDirectory(directory);
                }

                byte[] bytes = await EncodeForStoreAsync(payload, cancellationToken);
                await _fileSystem.WriteAllBytesCreateNewAsync(
                    temporaryPath,
                    bytes,
                    cancellationToken);

                _fileSystem.MoveCreateNew(temporaryPath, path);
                installed = true;

                cancellationToken.ThrowIfCancellationRequested();
                bool persistenceCompleted = await _persistenceSyncAsync(cancellationToken);
                if (!persistenceCompleted)
                {
                    _fileSystem.DeleteFile(path);
                    installed = false;
                    bool cleanupCompleted = await ConfirmPersistenceWithoutCancellationAsync();
                    return new RbxWorldPackageWriteResult(
                        false,
                        path,
                        "The package reached the filesystem, but durable persistence was not confirmed."
                        + (cleanupCompleted
                            ? " The failed create was durably removed."
                            : " Cleanup durability was also not confirmed."));
                }
                if (rotateAutoRing)
                {
                    await RotateAutoRingAsync(path, rotatedFiles, cancellationToken);
                    if (rotatedFiles.Count > 0)
                    {
                        bool rotationCompleted = await _persistenceSyncAsync(cancellationToken);
                        if (!rotationCompleted)
                        {
                            bool localRollbackCompleted = TryDeleteFile(path);
                            installed = false;
                            localRollbackCompleted = await TryRestoreRotatedFilesAsync(rotatedFiles)
                                                     && localRollbackCompleted;
                            bool rollbackSyncCompleted =
                                await ConfirmPersistenceWithoutCancellationAsync();
                            bool rollbackCompleted = localRollbackCompleted && rollbackSyncCompleted;
                            return new RbxWorldPackageWriteResult(
                                false,
                                path,
                                "Durable ring rotation was not confirmed."
                                + (rollbackCompleted
                                    ? " The new autosave was durably removed, the exact prior ring "
                                      + "was restored, and rollback durability was confirmed."
                                    : " Exact prior-ring rollback durability was not confirmed."));
                        }
                    }
                }

                return new RbxWorldPackageWriteResult(true, path, "");
            }
            catch (Exception ex)
            {
                string recovery = "";
                bool recoveryRequired = false;
                bool localRecoveryCompleted = true;
                if (rotatedFiles.Count > 0)
                {
                    localRecoveryCompleted = await TryRestoreRotatedFilesAsync(rotatedFiles);
                    recoveryRequired = true;
                }

                if (installed && _fileSystem.FileExists(path))
                {
                    localRecoveryCompleted = TryDeleteFile(path) && localRecoveryCompleted;
                    recoveryRequired = true;
                }

                if (recoveryRequired)
                {
                    bool recoverySyncCompleted =
                        await ConfirmPersistenceWithoutCancellationAsync();
                    bool recoveryCompleted = localRecoveryCompleted && recoverySyncCompleted;
                    recovery = recoveryCompleted
                        ? " Exact pre-call state durability was restored."
                        : " Exact pre-call state durability was not confirmed.";
                }

                return new RbxWorldPackageWriteResult(false, path, ex.Message + recovery);
            }
            finally
            {
                try
                {
                    if (_fileSystem.FileExists(temporaryPath))
                    {
                        _fileSystem.DeleteFile(temporaryPath);
                    }
                }
                catch (Exception)
                {
                }
            }
        }

        private async UniTask<RbxWorldPackagePayload> ReadValidatedAsync(
            string path,
            CancellationToken cancellationToken)
        {
            if (!_fileSystem.FileExists(path))
            {
                throw new FileNotFoundException("World package was not found.", path);
            }

            long length = _fileSystem.GetFileLength(path);
            if (length > RbxWorldPackageSerializer.MaximumPackageBytes)
            {
                throw new RbxWorldPackageException(
                    "World package is " + length + " bytes; format version 1 limit is "
                    + RbxWorldPackageSerializer.MaximumPackageBytes + " bytes.");
            }
#if UNITY_WEBGL && !UNITY_EDITOR
            if (length > MaximumWebGlSafePackageBytes)
            {
                throw new RbxWorldPackageException(
                    "WebGL world-package load refuses " + length
                    + " bytes until the JSON/ZIP decoder is incrementally chunked; safe limit is "
                    + MaximumWebGlSafePackageBytes + " bytes.");
            }
#endif

            byte[] bytes = await _fileSystem.ReadAllBytesAsync(path, cancellationToken);
            await UniTask.Yield(PlayerLoopTiming.Update, cancellationToken);
            return RbxWorldPackageSerializer.ReadPackage(bytes);
        }

        private static async UniTask<byte[]> EncodeForStoreAsync(
            RbxWorldPackagePayload payload,
            CancellationToken cancellationToken)
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            ValidateWebGlWorkBudget(payload);
            await UniTask.Yield(PlayerLoopTiming.Update, cancellationToken);
#endif
            byte[] bytes = RbxWorldPackageSerializer.WritePackage(payload);
#if UNITY_WEBGL && !UNITY_EDITOR
            if (bytes.Length > MaximumWebGlSafePackageBytes)
            {
                throw new RbxWorldPackageException(
                    "WebGL world-package write produced " + bytes.Length
                    + " bytes; the bounded non-blocking path limit is "
                    + MaximumWebGlSafePackageBytes + " bytes.");
            }

            await UniTask.Yield(PlayerLoopTiming.Update, cancellationToken);
#endif
            return bytes;
        }

#if UNITY_WEBGL && !UNITY_EDITOR
        private static void ValidateWebGlWorkBudget(RbxWorldPackagePayload payload)
        {
            if (payload?.Tree?.Instances == null || payload.Mods == null)
            {
                throw new RbxWorldPackageException("WebGL world-package payload is incomplete.");
            }

            if (payload.Tree.Instances.Count > MaximumWebGlSafeInstances)
            {
                throw new RbxWorldPackageException(
                    "WebGL world-package write refuses " + payload.Tree.Instances.Count
                    + " instances until JSON/ZIP encoding is incrementally chunked; safe limit is "
                    + MaximumWebGlSafeInstances + ".");
            }

            long textCharacters = 0L;
            int collectionItems = payload.Mods.Count;
            AddTextLength(ref textCharacters, payload.Settings?.WorldId);
            AddTextLength(ref textCharacters, payload.Settings?.SignalBehavior);
            foreach (InstanceSnapshot node in payload.Tree.Instances)
            {
                collectionItems += node.Attributes?.Count ?? 0;
                collectionItems += node.Tags?.Count ?? 0;
                AddTextLength(ref textCharacters, node.ClassName);
                AddTextLength(ref textCharacters, node.Name);
                AddTextLength(ref textCharacters, node.OwnerModId);
                AddTextLength(ref textCharacters, node.OriginTag);
                AddTextLength(ref textCharacters, node.OwnerActorId);
                AddTextLength(ref textCharacters, node.Model?.StoredWorldPivot);
                AddTextLength(ref textCharacters, node.ClickDetector?.MaxActivationDistance);
                if (node.Attributes != null)
                {
                    foreach (AttributeSnapshot attribute in node.Attributes)
                    {
                        AddTextLength(ref textCharacters, attribute?.Name);
                        AddTextLength(ref textCharacters, attribute?.StringValue);
                    }
                }

                if (node.Tags != null)
                {
                    foreach (string tag in node.Tags)
                    {
                        AddTextLength(ref textCharacters, tag);
                    }
                }
            }

            foreach (RbxWorldModSource mod in payload.Mods)
            {
                AddTextLength(ref textCharacters, mod?.Source);
                if (mod?.Manifest != null)
                {
                    AddTextLength(ref textCharacters, mod.Manifest.Id);
                    AddTextLength(ref textCharacters, mod.Manifest.Name);
                    AddTextLength(ref textCharacters, mod.Manifest.Description);
                    AddTextLength(ref textCharacters, mod.Manifest.Version);
                    AddTextLength(ref textCharacters, mod.Manifest.Category);
                    AddTextLength(ref textCharacters, mod.Manifest.Tags);
                    AddTextLength(ref textCharacters, mod.Manifest.Origin);
                    AddTextLength(ref textCharacters, mod.Manifest.SeededVersion);
                    AddTextLength(ref textCharacters, mod.Manifest.SeededHash);
                    AddTextLength(ref textCharacters, mod.Manifest.Author);
                    AddTextLength(ref textCharacters, mod.Manifest.OwnerActorId);
                    AddTextLength(ref textCharacters, mod.Manifest.Capabilities);
                    AddTextLength(ref textCharacters, mod.Manifest.Entry);
                }
            }

            foreach (KeyValuePair<InstanceId, PartProperties> part in payload.Parts)
            {
                AddTextLength(ref textCharacters, part.Value.Material.Name);
            }

            if (collectionItems > MaximumWebGlSafeCollectionItems
                || textCharacters > MaximumWebGlSafeTextCharacters)
            {
                throw new RbxWorldPackageException(
                    "WebGL world-package write exceeds the bounded non-blocking JSON/ZIP work budget.");
            }
        }

        private static void AddTextLength(ref long total, string value)
        {
            if (value != null)
            {
                total += value.Length;
            }
        }
#endif

        private static UniTask<bool> RequestPersistenceCompletionAsync(
            CancellationToken cancellationToken)
        {
            return CoreAiWebGlPersistence.SyncAsync(cancellationToken);
        }

        private async UniTask RotateAutoRingAsync(
            string retainedPath,
            List<RotatedFile> rollbackJournal,
            CancellationToken cancellationToken)
        {
            IReadOnlyList<string> discovered = _fileSystem.GetFiles(_autoDirectory, Extension);
            string retainedFullPath = Path.GetFullPath(retainedPath);
            List<string> paths = new(discovered.Count);
            foreach (string discoveredPath in discovered)
            {
                // WHY: a host clock that moved backwards sorts the just-confirmed autosave before
                // the ring; it must never be its own rotation victim, or the caller proceeds with a
                // success result that names a deleted backup.
                if (!string.Equals(
                        Path.GetFullPath(discoveredPath),
                        retainedFullPath,
                        StringComparison.Ordinal))
                {
                    paths.Add(discoveredPath);
                }
            }

            paths.Sort(StringComparer.Ordinal);
            int removeCount = paths.Count - (_autoBackupCapacity - 1);
            for (int index = 0; index < removeCount; index++)
            {
                string path = paths[index];
                byte[] bytes = await _fileSystem.ReadAllBytesAsync(path, cancellationToken);
                rollbackJournal.Add(new RotatedFile(path, bytes));
                _fileSystem.DeleteFile(path);
            }
        }

        private async UniTask RestoreRotatedFilesAsync(IReadOnlyList<RotatedFile> rotatedFiles)
        {
            foreach (RotatedFile rotatedFile in rotatedFiles)
            {
                if (!_fileSystem.FileExists(rotatedFile.Path))
                {
                    await _fileSystem.WriteAllBytesCreateNewAsync(
                        rotatedFile.Path,
                        rotatedFile.Bytes,
                        CancellationToken.None);
                }
            }
        }

        private async UniTask<bool> TryRestoreRotatedFilesAsync(
            IReadOnlyList<RotatedFile> rotatedFiles)
        {
            try
            {
                await RestoreRotatedFilesAsync(rotatedFiles);
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private bool TryDeleteFile(string path)
        {
            try
            {
                _fileSystem.DeleteFile(path);
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private async UniTask<bool> ConfirmPersistenceWithoutCancellationAsync()
        {
            try
            {
                return await _persistenceSyncAsync(CancellationToken.None);
            }
            catch (Exception)
            {
                return false;
            }
        }

        private string AllocateUniqueAutoPath(string timestamp, string trigger)
        {
            string prefix = timestamp + "-";
            IReadOnlyList<string> existingPaths = _fileSystem.GetFiles(_autoDirectory, Extension);
            int sequence = 0;
            foreach (string existingPath in existingPaths)
            {
                string existingName = Path.GetFileNameWithoutExtension(existingPath);
                if (!existingName.StartsWith(prefix, StringComparison.Ordinal))
                {
                    continue;
                }
                int sequenceEnd = existingName.IndexOf('-', prefix.Length);
                if (sequenceEnd <= prefix.Length)
                {
                    continue;
                }

                string sequenceText = existingName.Substring(
                    prefix.Length, sequenceEnd - prefix.Length);
                if (int.TryParse(
                        sequenceText,
                        System.Globalization.NumberStyles.None,
                        System.Globalization.CultureInfo.InvariantCulture,
                        out int existingSequence)
                    && existingSequence >= sequence)
                {
                    sequence = checked(existingSequence + 1);
                }
            }

            string stem = prefix + sequence.ToString(
                "D4", System.Globalization.CultureInfo.InvariantCulture) + "-" + trigger;
            return Path.Combine(_autoDirectory, stem + Extension);
        }

        private static string ValidateAutoFileName(string fileName)
        {
            if (string.IsNullOrWhiteSpace(fileName)
                || !string.Equals(fileName, Path.GetFileName(fileName), StringComparison.Ordinal)
                || !fileName.EndsWith(Extension, StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException(
                    "Auto package name must be one .world file name without a path.",
                    nameof(fileName));
            }

            return fileName;
        }

        private static readonly HashSet<string> ReservedDeviceNames = new(StringComparer.OrdinalIgnoreCase)
        {
            "CON", "PRN", "AUX", "NUL",
            "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
            "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
        };

        private static string ValidateName(string value, string field)
        {
            string trimmed = value?.Trim();
            if (string.IsNullOrEmpty(trimmed) || trimmed.Length > MaximumNameLength)
            {
                throw new ArgumentException(
                    field + " must contain 1-" + MaximumNameLength + " characters.",
                    nameof(value));
            }

            string baseName = trimmed;
            int dotIndex = trimmed.IndexOf('.');
            if (dotIndex >= 0)
            {
                baseName = trimmed.Substring(0, dotIndex);
            }

            if (ReservedDeviceNames.Contains(baseName))
            {
                throw new ArgumentException(
                    field + " '" + trimmed + "' is a reserved device name.",
                    nameof(value));
            }

            for (int index = 0; index < trimmed.Length; index++)
            {
                char character = trimmed[index];
                bool allowed = char.IsLetterOrDigit(character)
                               || character == '-'
                               || character == '_';
                if (!allowed)
                {
                    throw new ArgumentException(
                        field + " may contain only letters, digits, '-' and '_'.",
                        nameof(value));
                }
            }

            return trimmed;
        }

        private static string SanitizeTrigger(string trigger)
        {
            string source = string.IsNullOrWhiteSpace(trigger) ? "mutation" : trigger.Trim();
            StringBuilder builder = new(Math.Min(source.Length, MaximumNameLength));
            for (int index = 0; index < source.Length && builder.Length < MaximumNameLength; index++)
            {
                char character = source[index];
                builder.Append(char.IsLetterOrDigit(character) || character == '-' || character == '_'
                    ? character
                    : '_');
            }

            return builder.Length == 0 ? "mutation" : builder.ToString();
        }

        private static DateTime NormalizeUtc(DateTime value)
        {
            if (value.Kind == DateTimeKind.Utc)
            {
                return value;
            }

            return value.Kind == DateTimeKind.Local
                ? value.ToUniversalTime()
                : DateTime.SpecifyKind(value, DateTimeKind.Utc);
        }
    }
}
