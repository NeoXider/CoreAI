using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using CoreAI.Infrastructure;
using CoreAI.Infrastructure.Lua;
using CoreAI.Mods.Rbx.Binding;
using CoreAI.Mods.Rbx.Instances;
using Cysharp.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace CoreAI.Mods.WorldPackages
{
    /// <summary>Result of a durable package write or pre-mutation backup attempt.</summary>
    public sealed class RbxWorldPackageWriteResult
    {
        internal RbxWorldPackageWriteResult(bool success, string path, string error)
            : this(success, path, error, false)
        {
        }

        internal RbxWorldPackageWriteResult(
            bool success,
            string path,
            string error,
            bool packageCannotBeEncoded)
        {
            Success = success;
            Path = path ?? "";
            Error = error ?? "";
            PackageCannotBeEncoded = !success && packageCannotBeEncoded;
        }

        public bool Success { get; }

        /// <summary>
        /// True when the write failed because the payload itself cannot be encoded (a world-package
        /// format limit, a platform encoding budget, or text the encoder refuses), so retrying the
        /// same world can never succeed. False for a success and for every durability or I/O failure.
        /// </summary>
        public bool PackageCannotBeEncoded { get; }

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

    /// <summary>What the durable startup selection names.</summary>
    public enum RbxWorldStartupSelectionKind
    {
        /// <summary>No selection was ever recorded; the default world opens on start.</summary>
        None,

        /// <summary>The player chose the default world for the next start.</summary>
        Default,

        /// <summary>A copy of a player-confirmed package opens on start.</summary>
        Package,

        /// <summary>The newest entry cannot be read; the default world opens and the entry is kept.</summary>
        Invalid
    }

    /// <summary>
    /// The current durable startup selection. <see cref="Payload"/> is set only by a full read of a
    /// <see cref="RbxWorldStartupSelectionKind.Package"/> entry; the metadata read leaves it null and
    /// does not validate the package.
    /// </summary>
    public sealed class RbxWorldStartupSelection
    {
        internal static readonly RbxWorldStartupSelection NoneSelected = new(
            RbxWorldStartupSelectionKind.None, 0, null, "", null, "", "", "");

        internal RbxWorldStartupSelection(
            RbxWorldStartupSelectionKind kind,
            int sequence,
            RbxWorldPackagePayload payload,
            string worldId,
            DateTime? selectedAtUtc,
            string sourceKind,
            string sourceName,
            string error)
        {
            Kind = kind;
            Sequence = sequence;
            Payload = payload;
            WorldId = worldId ?? "";
            SelectedAtUtc = selectedAtUtc;
            SourceKind = sourceKind ?? "";
            SourceName = sourceName ?? "";
            Error = error ?? "";
        }

        public RbxWorldStartupSelectionKind Kind { get; }

        /// <summary>Create-once sequence number of the entry; 0 when nothing is selected.</summary>
        public int Sequence { get; }

        public RbxWorldPackagePayload Payload { get; }

        /// <summary>World id of the selected package; empty when unknown.</summary>
        public string WorldId { get; }

        /// <summary>When the player confirmed the selection; null when the metadata is unavailable.</summary>
        public DateTime? SelectedAtUtc { get; }

        /// <summary><c>manual</c> or <c>autosave</c>: where the confirmed package came from.</summary>
        public string SourceKind { get; }

        /// <summary>The manual slot or autosave file name the confirmed package came from.</summary>
        public string SourceName { get; }

        public string Error { get; }
    }

    /// <summary>
    /// Durable record of the world that opens on the next start: a create-once copy of a package the
    /// player confirmed (kept current by the session after each gated change to that world), or a
    /// marker that asks for the default world. Only trusted host code writes it (a player
    /// confirmation, the session's refresh of the confirmed world, or the Hub reset); no AI tool can
    /// choose what it names.
    /// </summary>
    public interface IRbxWorldStartupStore
    {
        /// <summary>Records an exact copy of <paramref name="payload"/> as the newest startup selection.</summary>
        UniTask<RbxWorldPackageWriteResult> SelectStartupAsync(
            RbxWorldPackagePayload payload,
            string sourceKind,
            string sourceName,
            CancellationToken cancellationToken = default);

        /// <summary>Records that the next start opens the default world.</summary>
        UniTask<RbxWorldPackageWriteResult> ClearStartupAsync(
            CancellationToken cancellationToken = default);

        /// <summary>Reads and validates the newest entry, including its package.</summary>
        UniTask<RbxWorldStartupSelection> ReadStartupAsync(
            CancellationToken cancellationToken = default);

        /// <summary>Reads only the newest entry's kind and informational metadata.</summary>
        UniTask<RbxWorldStartupSelection> ReadStartupInfoAsync(
            CancellationToken cancellationToken = default);
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
    /// <para>
    /// The startup area <c>Startup/[Stores/&lt;storeId&gt;/]</c> sits next to <c>Manual</c> and
    /// <c>Auto</c>. It holds create-once entries <c>&lt;N&gt;.world</c> (a copy of a confirmed
    /// package) and <c>&lt;N&gt;.default</c> (boot the default world), plus an informational
    /// <c>&lt;N&gt;.json</c> never needed to boot; the highest N among the entries is the selection.
    /// </para>
    /// </summary>
    public sealed class FileRbxWorldPackageStore : IRbxWorldPackageStore, IRbxWorldStartupStore
    {
        private sealed class StartupEntry
        {
            public StartupEntry(int sequence, string path, bool isDefault)
            {
                Sequence = sequence;
                Path = path;
                IsDefault = isDefault;
            }

            public int Sequence { get; }

            public string Path { get; }

            public bool IsDefault { get; }
        }

        private sealed class StartupMetadata
        {
            public static readonly StartupMetadata Unavailable = new("", null, "", "");

            public StartupMetadata(
                string worldId,
                DateTime? selectedAtUtc,
                string sourceKind,
                string sourceName)
            {
                WorldId = worldId ?? "";
                SelectedAtUtc = selectedAtUtc;
                SourceKind = sourceKind ?? "";
                SourceName = sourceName ?? "";
            }

            public string WorldId { get; }

            public DateTime? SelectedAtUtc { get; }

            public string SourceKind { get; }

            public string SourceName { get; }
        }

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

        /// <summary>Default number of manual slots one store keeps; a further create-once save is refused.</summary>
        public const int DefaultMaximumManualSlots = 64;

        /// <summary>Default total bytes of all manual slots together; a save that would pass it is refused.</summary>
        public const long DefaultMaximumManualSlotBytes = 256L * 1024L * 1024L;

        public const int MaximumWebGlSafePackageBytes = 4 * 1024 * 1024;
        public const int MaximumWebGlSafeInstances = 4096;
        public const int MaximumWebGlSafeCollectionItems = 32768;
        public const int MaximumWebGlSafeTextCharacters = 2 * 1024 * 1024;

        private const string Extension = RbxWorldPackageNames.Extension;
        private const int MaximumNameLength = RbxWorldPackageNames.MaximumNameLength;
        private const string StartupDefaultExtension = ".default";
        private const string StartupMetadataExtension = ".json";
        private const int StartupSequenceDigits = 10;
        private const int MaximumStartupMetadataBytes = 64 * 1024;
        private const string TemporaryExtension = ".tmp";
        private const int TemporaryGuidLength = 32;

        private static readonly string[] StartupEntryExtensions =
        {
            Extension,
            StartupDefaultExtension,
            StartupMetadataExtension
        };

        private readonly string _manualDirectory;
        private readonly string _autoDirectory;
        private readonly string _startupDirectory;
        private readonly int _autoBackupCapacity;
        private readonly int _maximumManualSlots;
        private readonly long _maximumManualSlotBytes;
        private readonly Func<CancellationToken, UniTask<bool>> _persistenceSyncAsync;
        private readonly Func<DateTime> _utcNow;
        private readonly IRbxWorldPackageFileSystem _fileSystem;
        private readonly SemaphoreSlim _mutationGate = new(1, 1);

        /// <param name="startupNamespace">
        /// Composition namespace of the startup area (production passes the mods store id), so a world
        /// selected in one composition never opens in another with a different Lua tier. Empty keeps the
        /// shared <c>Startup</c> directory. Manual slots and autosaves are not namespaced.
        /// </param>
        /// <param name="maximumManualSlots">How many manual slots may exist; a further save is refused.</param>
        /// <param name="maximumManualSlotBytes">
        /// Total bytes all manual slots may take together; a save that would pass it is refused.
        /// </param>
        public FileRbxWorldPackageStore(
            string rootDirectory = null,
            int autoBackupCapacity = DefaultAutoBackupCapacity,
            Func<CancellationToken, UniTask<bool>> persistenceSyncAsync = null,
            Func<DateTime> utcNow = null,
            IRbxWorldPackageFileSystem fileSystem = null,
            string startupNamespace = null,
            int maximumManualSlots = DefaultMaximumManualSlots,
            long maximumManualSlotBytes = DefaultMaximumManualSlotBytes)
        {
            if (autoBackupCapacity <= 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(autoBackupCapacity),
                    autoBackupCapacity,
                    "Auto backup capacity must be positive.");
            }

            if (maximumManualSlots <= 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(maximumManualSlots),
                    maximumManualSlots,
                    "The manual slot limit must be positive.");
            }

            if (maximumManualSlotBytes <= 0L)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(maximumManualSlotBytes),
                    maximumManualSlotBytes,
                    "The manual slot byte limit must be positive.");
            }

            string resolvedRoot = string.IsNullOrWhiteSpace(rootDirectory)
                ? Path.Combine(
                    Application.persistentDataPath,
                    CoreAiPersistentPaths.RootFolderName,
                    "Saves")
                : Path.GetFullPath(rootDirectory);
            _manualDirectory = Path.Combine(resolvedRoot, "Manual");
            _autoDirectory = Path.Combine(resolvedRoot, "Auto");
            _startupDirectory = LuaModStoreId.ApplyTo(
                Path.Combine(resolvedRoot, "Startup"),
                startupNamespace);
            _autoBackupCapacity = autoBackupCapacity;
            _maximumManualSlots = maximumManualSlots;
            _maximumManualSlotBytes = maximumManualSlotBytes;
            _persistenceSyncAsync = persistenceSyncAsync ?? RequestPersistenceCompletionAsync;
            _utcNow = utcNow ?? (() => DateTime.UtcNow);
            _fileSystem = fileSystem ?? new SystemRbxWorldPackageFileSystem();
            SweepCrashLeftTemporaryFiles();
        }

        /// <summary>
        /// The durability hook this store awaits after every write: the injected delegate, or the
        /// production default that asks <see cref="CoreAiWebGlPersistence.SyncAsync"/>. Read-only; tests
        /// use it to pin that default, which no EditMode run can observe through behaviour.
        /// </summary>
        internal Func<CancellationToken, UniTask<bool>> PersistenceSyncForTests => _persistenceSyncAsync;

        /// <summary>The resolved startup directory, namespace included; read-only, for composition tests.</summary>
        internal string StartupDirectoryForTests => _startupDirectory;

        public async UniTask<RbxWorldPackageWriteResult> SelectStartupAsync(
            RbxWorldPackagePayload payload,
            string sourceKind,
            string sourceName,
            CancellationToken cancellationToken = default)
        {
            if (payload == null)
            {
                throw new ArgumentNullException(nameof(payload));
            }

            cancellationToken.ThrowIfCancellationRequested();
            await _mutationGate.WaitAsync(cancellationToken);
            try
            {
                int sequence;
                try
                {
                    _fileSystem.CreateDirectory(_startupDirectory);
                    sequence = AllocateStartupSequence();
                }
                catch (Exception ex)
                {
                    return new RbxWorldPackageWriteResult(
                        false,
                        _startupDirectory,
                        "The startup selection entry could not be allocated: " + ex.Message);
                }

                string stem = FormatStartupSequence(sequence);
                string packagePath = Path.Combine(_startupDirectory, stem + Extension);
                string metadataPath = Path.Combine(_startupDirectory, stem + StartupMetadataExtension);
                // WHY the metadata goes first: the package's own durability confirmation then covers
                // both files, and an entry is only ever current once its package exists.
                bool metadataInstalled = await TryInstallStartupMetadataAsync(
                    metadataPath,
                    sequence,
                    payload,
                    sourceKind,
                    sourceName,
                    cancellationToken);
                RbxWorldPackageWriteResult written = await WriteCreateOnceAsync(
                    packagePath,
                    payload,
                    false,
                    cancellationToken,
                    companionPath: metadataInstalled ? metadataPath : null);
                if (written.Success)
                {
                    await PruneStartupEntriesAsync(sequence);
                }

                return written;
            }
            finally
            {
                _mutationGate.Release();
            }
        }

        public async UniTask<RbxWorldPackageWriteResult> ClearStartupAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await _mutationGate.WaitAsync(cancellationToken);
            try
            {
                int sequence;
                try
                {
                    StartupEntry current = FindCurrentStartupEntry();
                    if (current == null || current.IsDefault)
                    {
                        return new RbxWorldPackageWriteResult(true, current?.Path ?? "", "");
                    }

                    _fileSystem.CreateDirectory(_startupDirectory);
                    sequence = AllocateStartupSequence();
                }
                catch (Exception ex)
                {
                    return new RbxWorldPackageWriteResult(
                        false,
                        _startupDirectory,
                        "The startup selection could not be listed: " + ex.Message);
                }

                string markerPath = Path.Combine(
                    _startupDirectory,
                    FormatStartupSequence(sequence) + StartupDefaultExtension);
                RbxWorldPackageWriteResult written = await WriteCreateOnceAsync(
                    markerPath,
                    null,
                    false,
                    cancellationToken,
                    Array.Empty<byte>());
                if (written.Success)
                {
                    await PruneStartupEntriesAsync(sequence);
                }

                return written;
            }
            finally
            {
                _mutationGate.Release();
            }
        }

        public async UniTask<RbxWorldStartupSelection> ReadStartupAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await _mutationGate.WaitAsync(cancellationToken);
            try
            {
                StartupEntry current;
                try
                {
                    current = FindCurrentStartupEntry();
                }
                catch (Exception ex)
                {
                    return ListingFailure(ex);
                }

                if (current == null)
                {
                    return RbxWorldStartupSelection.NoneSelected;
                }

                if (current.IsDefault)
                {
                    return DefaultSelection(current);
                }

                StartupMetadata metadata = await TryReadStartupMetadataAsync(
                    current.Sequence,
                    cancellationToken);
                try
                {
                    RbxWorldPackagePayload payload = await ReadValidatedAsync(current.Path, cancellationToken);
                    string worldId = payload.Settings?.WorldId;
                    return new RbxWorldStartupSelection(
                        RbxWorldStartupSelectionKind.Package,
                        current.Sequence,
                        payload,
                        string.IsNullOrEmpty(worldId) ? metadata.WorldId : worldId,
                        metadata.SelectedAtUtc,
                        metadata.SourceKind,
                        metadata.SourceName,
                        "");
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    string problem = ex is FileNotFoundException || ex is DirectoryNotFoundException
                        ? " disappeared before it could be read: "
                        : " is not a loadable world package: ";
                    return new RbxWorldStartupSelection(
                        RbxWorldStartupSelectionKind.Invalid,
                        current.Sequence,
                        null,
                        metadata.WorldId,
                        metadata.SelectedAtUtc,
                        metadata.SourceKind,
                        metadata.SourceName,
                        "Startup entry " + Path.GetFileName(current.Path) + problem + ex.Message);
                }
            }
            finally
            {
                _mutationGate.Release();
            }
        }

        public async UniTask<RbxWorldStartupSelection> ReadStartupInfoAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await _mutationGate.WaitAsync(cancellationToken);
            try
            {
                StartupEntry current;
                try
                {
                    current = FindCurrentStartupEntry();
                }
                catch (Exception ex)
                {
                    return ListingFailure(ex);
                }

                if (current == null)
                {
                    return RbxWorldStartupSelection.NoneSelected;
                }

                if (current.IsDefault)
                {
                    return DefaultSelection(current);
                }

                StartupMetadata metadata = await TryReadStartupMetadataAsync(
                    current.Sequence,
                    cancellationToken);
                return new RbxWorldStartupSelection(
                    RbxWorldStartupSelectionKind.Package,
                    current.Sequence,
                    null,
                    metadata.WorldId,
                    metadata.SelectedAtUtc,
                    metadata.SourceKind,
                    metadata.SourceName,
                    "");
            }
            finally
            {
                _mutationGate.Release();
            }
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

                // WHY bounded: manual slots are create-once and no tool can delete one, so without a
                // cap a model could fill the disk (on WebGL the origin's whole storage quota) with
                // saves, after which every pre-mutation autosave fails and the world stops changing.
                int slotCount;
                long slotBytes;
                try
                {
                    MeasureManualSlots(out slotCount, out slotBytes);
                }
                catch (Exception ex)
                {
                    return new RbxWorldPackageWriteResult(
                        false,
                        path,
                        "Manual slot '" + safeSlot + "' was not written: the existing manual slots could "
                        + "not be counted (" + ex.Message + ").");
                }

                if (slotCount >= _maximumManualSlots)
                {
                    return new RbxWorldPackageWriteResult(
                        false,
                        path,
                        "Manual slot '" + safeSlot + "' was not written: this store already keeps "
                        + slotCount.ToString(CultureInfo.InvariantCulture) + " manual slots, the limit is "
                        + _maximumManualSlots.ToString(CultureInfo.InvariantCulture) + ". "
                        + DescribeManualSlotRecovery());
                }

                byte[] bytes;
                try
                {
                    bytes = await EncodeForStoreAsync(payload, cancellationToken);
                }
                catch (Exception ex)
                {
                    return new RbxWorldPackageWriteResult(false, path, ex.Message, IsEncodingFailure(ex));
                }

                if (slotBytes + bytes.LongLength > _maximumManualSlotBytes)
                {
                    return new RbxWorldPackageWriteResult(
                        false,
                        path,
                        "Manual slot '" + safeSlot + "' was not written: it needs "
                        + bytes.LongLength.ToString(CultureInfo.InvariantCulture) + " bytes and the "
                        + "existing manual slots already use "
                        + slotBytes.ToString(CultureInfo.InvariantCulture) + " of the "
                        + _maximumManualSlotBytes.ToString(CultureInfo.InvariantCulture)
                        + " bytes this store allows. " + DescribeManualSlotRecovery());
                }

                return await WriteCreateOnceAsync(path, payload, false, cancellationToken, bytes);
            }
            finally
            {
                _mutationGate.Release();
            }
        }

        private static string DescribeManualSlotRecovery()
        {
            return "Nothing was written. Manual slots are create-once and no tool can delete them; "
                   + "autosaves keep working. Load an existing slot with load_world instead, or ask the "
                   + "player to remove old manual saves.";
        }

        private void MeasureManualSlots(out int count, out long bytes)
        {
            count = 0;
            bytes = 0L;
            if (!_fileSystem.DirectoryExists(_manualDirectory))
            {
                return;
            }

            foreach (string path in _fileSystem.GetFiles(_manualDirectory, Extension))
            {
                if (!path.EndsWith(Extension, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                count++;
                bytes += _fileSystem.GetFileLength(path);
            }
        }

        /// <summary>
        /// Deletes the <c>&lt;entry&gt;.&lt;guid&gt;.tmp</c> files a crash between a temporary write and
        /// its install left in the manual, autosave and startup directories. Only names of exactly that
        /// shape are touched. Never throws: a file that cannot be removed now is retried at the next open.
        /// </summary>
        /// <remarks>
        /// WHY at open: a temporary file is never read, so the only harm is the space it holds, and a
        /// crash-left one is otherwise never removed. A store instance over the same root that is
        /// mid-write in another composition loses that one write (it reports the failure); the store
        /// is a singleton per composition, so that needs two live compositions on one save root.
        /// </remarks>
        private void SweepCrashLeftTemporaryFiles()
        {
            SweepCrashLeftTemporaryFiles(_manualDirectory, false);
            SweepCrashLeftTemporaryFiles(_autoDirectory, false);
            SweepCrashLeftTemporaryFiles(_startupDirectory, true);
        }

        private void SweepCrashLeftTemporaryFiles(string directory, bool startupEntries)
        {
            try
            {
                if (!_fileSystem.DirectoryExists(directory))
                {
                    return;
                }

                foreach (string path in _fileSystem.GetFiles(directory, TemporaryExtension))
                {
                    if (IsCrashLeftTemporaryName(Path.GetFileName(path), startupEntries))
                    {
                        TryDeleteFile(path);
                    }
                }
            }
            catch (Exception)
            {
            }
        }

        /// <summary>
        /// True for exactly <c>&lt;name&gt;&lt;ext&gt;.&lt;32 lowercase hex&gt;.tmp</c>, where ext is
        /// <c>.world</c>, or in the startup directory also <c>.default</c> or <c>.json</c>.
        /// </summary>
        internal static bool IsCrashLeftTemporaryName(string fileName, bool startupEntries)
        {
            if (string.IsNullOrEmpty(fileName)
                || !fileName.EndsWith(TemporaryExtension, StringComparison.Ordinal))
            {
                return false;
            }

            int guidEnd = fileName.Length - TemporaryExtension.Length;
            int guidStart = guidEnd - TemporaryGuidLength;
            if (guidStart < 2 || fileName[guidStart - 1] != '.')
            {
                return false;
            }

            for (int index = guidStart; index < guidEnd; index++)
            {
                char character = fileName[index];
                if (!((character >= '0' && character <= '9') || (character >= 'a' && character <= 'f')))
                {
                    return false;
                }
            }

            string entryName = fileName.Substring(0, guidStart - 1);
            if (HasEntryExtension(entryName, Extension))
            {
                return true;
            }

            return startupEntries
                   && (HasEntryExtension(entryName, StartupDefaultExtension)
                       || HasEntryExtension(entryName, StartupMetadataExtension));
        }

        private static bool HasEntryExtension(string entryName, string extension)
        {
            return entryName.Length > extension.Length
                   && entryName.EndsWith(extension, StringComparison.Ordinal);
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
                string path;
                try
                {
                    string safeTrigger = SanitizeTrigger(trigger);
                    string timestamp = NormalizeUtc(_utcNow()).ToString(
                        "yyyyMMdd'T'HHmmssfff'Z'", System.Globalization.CultureInfo.InvariantCulture);
                    _fileSystem.CreateDirectory(_autoDirectory);
                    path = AllocateUniqueAutoPath(timestamp, safeTrigger);
                }
                catch (Exception ex)
                {
                    // WHY a result and not a throw: every other autosave failure is a result, as in
                    // CreateManualAsync, and a caller that checks only the result must see this one.
                    return new RbxWorldPackageWriteResult(
                        false,
                        _autoDirectory,
                        "The autosave was not written: the autosave folder could not be prepared ("
                        + ex.Message + ").");
                }

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

            names.Sort(CompareAutoFileNames);
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

            infos.Sort((a, b) => CompareAutoFileNames(a.FileName, b.FileName));
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

        /// <param name="rawBytes">Exact bytes to install instead of encoding <paramref name="payload"/>.</param>
        /// <param name="companionPath">
        /// An already installed file that belongs to this create; a failed create removes it before its
        /// cleanup durability confirmation, so neither survives a reload.
        /// </param>
        private async UniTask<RbxWorldPackageWriteResult> WriteCreateOnceAsync(
            string path,
            RbxWorldPackagePayload payload,
            bool rotateAutoRing,
            CancellationToken cancellationToken,
            byte[] rawBytes = null,
            string companionPath = null)
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

                byte[] bytes = rawBytes;
                if (bytes == null)
                {
                    try
                    {
                        bytes = await EncodeForStoreAsync(payload, cancellationToken);
                    }
                    catch (Exception ex) when (IsEncodingFailure(ex))
                    {
                        // WHY typed: nothing was written, and the same world fails the same way on
                        // every retry; the pre-mutation gate lets the actions that can shrink or
                        // replace such a world run without the backup it can never get.
                        return new RbxWorldPackageWriteResult(false, path, ex.Message, true);
                    }
                }

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
                    TryDeleteCompanion(companionPath);
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

                if (companionPath != null)
                {
                    localRecoveryCompleted = TryDeleteCompanion(companionPath) && localRecoveryCompleted;
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

        /// <summary>
        /// True for a failure of the payload itself (format limit, WebGL budget, text the encoder
        /// refuses), as opposed to a cancellation or an I/O or durability failure.
        /// </summary>
        private static bool IsEncodingFailure(Exception exception)
        {
            return exception is RbxWorldPackageException || exception is EncoderFallbackException;
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

        /// <summary>
        /// Refuses a payload whose JSON/ZIP encoding would exceed the bounded work the WebGL player can
        /// do without releasing the frame. Called only on the WebGL player; compiled everywhere so the
        /// budget itself is testable in the editor.
        /// </summary>
        internal static void ValidateWebGlWorkBudget(RbxWorldPackagePayload payload)
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

            MeasureWebGlWork(payload, out int collectionItems, out long textCharacters);
            if (collectionItems > MaximumWebGlSafeCollectionItems
                || textCharacters > MaximumWebGlSafeTextCharacters)
            {
                throw new RbxWorldPackageException(
                    "WebGL world-package write exceeds the bounded non-blocking JSON/ZIP work budget: "
                    + textCharacters.ToString(CultureInfo.InvariantCulture) + " text characters (limit "
                    + MaximumWebGlSafeTextCharacters.ToString(CultureInfo.InvariantCulture) + ") and "
                    + collectionItems.ToString(CultureInfo.InvariantCulture) + " collection items (limit "
                    + MaximumWebGlSafeCollectionItems.ToString(CultureInfo.InvariantCulture) + ").");
            }
        }

        /// <summary>
        /// Counts the collection items and the characters of every string the package encoding writes
        /// for <paramref name="payload"/>: names, ledger metadata, value payloads, Humanoid state,
        /// attributes, tags, mod sources and manifests, and Part material names.
        /// </summary>
        internal static void MeasureWebGlWork(
            RbxWorldPackagePayload payload,
            out int collectionItems,
            out long textCharacters)
        {
            textCharacters = 0L;
            collectionItems = payload.Mods.Count;
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
                AddTextLength(ref textCharacters, node.MaterialVariant?.BaseMaterial);
                AddTextLength(ref textCharacters, node.MaterialVariant?.ColorMap);
                AddTextLength(ref textCharacters, node.MaterialVariant?.NormalMap);
                AddTextLength(ref textCharacters, node.MaterialVariant?.RoughnessMap);
                AddTextLength(ref textCharacters, node.MaterialVariant?.MetalnessMap);
                AddTextLength(ref textCharacters, node.MaterialVariant?.StudsPerTile);
                // WHY the value and Humanoid strings: a StringValue holds up to 200,000 characters and
                // every scalar and datatype value (Vector3, CFrame) is encoded into this string, so
                // leaving them out let eleven StringValues pass a two-million-character budget.
                AddTextLength(ref textCharacters, node.Value?.StringValue);
                AddTextLength(ref textCharacters, node.Humanoid?.Health);
                AddTextLength(ref textCharacters, node.Humanoid?.MaxHealth);
                AddTextLength(ref textCharacters, node.Humanoid?.WalkSpeed);
                AddTextLength(ref textCharacters, node.Humanoid?.JumpPower);
                AddTextLength(ref textCharacters, node.Humanoid?.JumpHeight);
                AddTextLength(ref textCharacters, node.Humanoid?.DisplayName);
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

            if (payload.Parts == null)
            {
                return;
            }

            foreach (KeyValuePair<InstanceId, PartProperties> part in payload.Parts)
            {
                AddTextLength(ref textCharacters, part.Value.Material.Name);
                AddTextLength(ref textCharacters, part.Value.MaterialVariant);
            }
        }

        private static void AddTextLength(ref long total, string value)
        {
            if (value != null)
            {
                total += value.Length;
            }
        }

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

            paths.Sort((left, right) => CompareAutoFileNames(
                Path.GetFileName(left), Path.GetFileName(right)));
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

        private bool TryDeleteCompanion(string companionPath)
        {
            if (companionPath == null)
            {
                return true;
            }

            try
            {
                return !_fileSystem.FileExists(companionPath) || TryDeleteFile(companionPath);
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

        private static string FormatStartupSequence(int sequence)
        {
            return sequence.ToString("D" + StartupSequenceDigits, CultureInfo.InvariantCulture);
        }

        private static bool TryParseStartupSequence(string path, out int sequence)
        {
            sequence = 0;
            string stem = Path.GetFileNameWithoutExtension(path);
            if (string.IsNullOrEmpty(stem) || stem.Length > StartupSequenceDigits)
            {
                return false;
            }

            for (int index = 0; index < stem.Length; index++)
            {
                if (stem[index] < '0' || stem[index] > '9')
                {
                    return false;
                }
            }

            return int.TryParse(stem, NumberStyles.None, CultureInfo.InvariantCulture, out sequence)
                   && sequence > 0;
        }

        private List<string> ListStartupFiles(string extension)
        {
            List<string> files = new();
            if (!_fileSystem.DirectoryExists(_startupDirectory))
            {
                return files;
            }

            foreach (string path in _fileSystem.GetFiles(_startupDirectory, extension))
            {
                if (path.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
                {
                    files.Add(path);
                }
            }

            return files;
        }

        /// <summary>The entry with the highest sequence among packages and default markers, or null.</summary>
        private StartupEntry FindCurrentStartupEntry()
        {
            StartupEntry current = null;
            foreach (string path in ListStartupFiles(Extension))
            {
                if (TryParseStartupSequence(path, out int sequence)
                    && (current == null || sequence > current.Sequence))
                {
                    current = new StartupEntry(sequence, path, false);
                }
            }

            foreach (string path in ListStartupFiles(StartupDefaultExtension))
            {
                // WHY a tie goes to the marker: create-once allocation never produces one, and if a
                // foreign writer did, the reproducible default world is the only safe answer.
                if (TryParseStartupSequence(path, out int sequence)
                    && (current == null || sequence >= current.Sequence))
                {
                    current = new StartupEntry(sequence, path, true);
                }
            }

            return current;
        }

        private int AllocateStartupSequence()
        {
            int highest = 0;
            foreach (string extension in StartupEntryExtensions)
            {
                foreach (string path in ListStartupFiles(extension))
                {
                    if (TryParseStartupSequence(path, out int sequence) && sequence > highest)
                    {
                        highest = sequence;
                    }
                }
            }

            return checked(highest + 1);
        }

        /// <summary>
        /// Deletes every startup entry older than the just-confirmed one, then asks for one more
        /// durability confirmation whose answer is ignored.
        /// </summary>
        /// <remarks>
        /// WHY a failed prune is harmless: an older entry that comes back after a reload is still older,
        /// and only the highest sequence is ever read, so no journal or rollback is needed here.
        /// </remarks>
        private async UniTask PruneStartupEntriesAsync(int keptSequence)
        {
            bool removed = false;
            try
            {
                foreach (string extension in StartupEntryExtensions)
                {
                    foreach (string path in ListStartupFiles(extension))
                    {
                        if (TryParseStartupSequence(path, out int sequence) && sequence < keptSequence)
                        {
                            removed = TryDeleteFile(path) || removed;
                        }
                    }
                }
            }
            catch (Exception)
            {
            }

            if (removed)
            {
                await ConfirmPersistenceWithoutCancellationAsync();
            }
        }

        private async UniTask<bool> TryInstallStartupMetadataAsync(
            string metadataPath,
            int sequence,
            RbxWorldPackagePayload payload,
            string sourceKind,
            string sourceName,
            CancellationToken cancellationToken)
        {
            string temporaryPath = metadataPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                JObject metadata = new()
                {
                    ["format"] = 1,
                    ["sequence"] = sequence,
                    ["world_id"] = payload.Settings?.WorldId ?? "",
                    ["selected_at_utc"] = NormalizeUtc(_utcNow()).ToString(
                        "O",
                        CultureInfo.InvariantCulture),
                    ["source_kind"] = sourceKind ?? "",
                    ["source_name"] = sourceName ?? ""
                };
                byte[] bytes = Encoding.UTF8.GetBytes(metadata.ToString(Formatting.None));
                await _fileSystem.WriteAllBytesCreateNewAsync(temporaryPath, bytes, cancellationToken);
                _fileSystem.MoveCreateNew(temporaryPath, metadataPath);
                return true;
            }
            catch (Exception)
            {
                return false;
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

        private async UniTask<StartupMetadata> TryReadStartupMetadataAsync(
            int sequence,
            CancellationToken cancellationToken)
        {
            string path = Path.Combine(
                _startupDirectory,
                FormatStartupSequence(sequence) + StartupMetadataExtension);
            try
            {
                if (!_fileSystem.FileExists(path)
                    || _fileSystem.GetFileLength(path) > MaximumStartupMetadataBytes)
                {
                    return StartupMetadata.Unavailable;
                }

                byte[] bytes = await _fileSystem.ReadAllBytesAsync(path, cancellationToken);
                JObject metadata = JsonConvert.DeserializeObject<JObject>(
                    Encoding.UTF8.GetString(bytes),
                    new JsonSerializerSettings { DateParseHandling = DateParseHandling.None });
                if (metadata == null)
                {
                    return StartupMetadata.Unavailable;
                }

                DateTime? selectedAtUtc = null;
                if (DateTime.TryParseExact(
                        (string)metadata["selected_at_utc"],
                        "O",
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.RoundtripKind,
                        out DateTime parsed))
                {
                    selectedAtUtc = NormalizeUtc(parsed);
                }

                return new StartupMetadata(
                    (string)metadata["world_id"],
                    selectedAtUtc,
                    (string)metadata["source_kind"],
                    (string)metadata["source_name"]);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception)
            {
                return StartupMetadata.Unavailable;
            }
        }

        private RbxWorldStartupSelection ListingFailure(Exception exception)
        {
            return new RbxWorldStartupSelection(
                RbxWorldStartupSelectionKind.Invalid,
                0,
                null,
                "",
                null,
                "",
                "",
                "The startup selection in '" + _startupDirectory + "' could not be listed: "
                + exception.Message);
        }

        private static RbxWorldStartupSelection DefaultSelection(StartupEntry entry)
        {
            return new RbxWorldStartupSelection(
                RbxWorldStartupSelectionKind.Default,
                entry.Sequence,
                null,
                "",
                null,
                "",
                "",
                "");
        }

        /// <summary>
        /// Creation order of two autosave file names: timestamp, then the numeric sequence, then the
        /// ordinal name. A name outside the autosave pattern compares by its ordinal name.
        /// </summary>
        /// <remarks>
        /// WHY the sequence is compared as a number: after a backwards clock step every new autosave
        /// shares the newest existing timestamp (see <see cref="AllocateUniqueAutoPath"/>), so the
        /// sequence can pass 9999, and "10000" sorts before "9999" as text.
        /// </remarks>
        internal static int CompareAutoFileNames(string left, string right)
        {
            if (TrySplitAutoFileName(left, out string leftStamp, out long leftSequence)
                && TrySplitAutoFileName(right, out string rightStamp, out long rightSequence))
            {
                int byStamp = string.CompareOrdinal(leftStamp, rightStamp);
                if (byStamp != 0)
                {
                    return byStamp;
                }

                int bySequence = leftSequence.CompareTo(rightSequence);
                if (bySequence != 0)
                {
                    return bySequence;
                }
            }

            return string.CompareOrdinal(left, right);
        }

        private static bool TrySplitAutoFileName(string fileName, out string stamp, out long sequence)
        {
            stamp = null;
            sequence = 0L;
            if (string.IsNullOrEmpty(fileName))
            {
                return false;
            }

            int stampEnd = fileName.IndexOf('-');
            if (stampEnd <= 0 || !IsAutoTimestamp(fileName.Substring(0, stampEnd)))
            {
                return false;
            }

            int sequenceEnd = fileName.IndexOf('-', stampEnd + 1);
            if (sequenceEnd <= stampEnd + 1
                || !long.TryParse(
                    fileName.Substring(stampEnd + 1, sequenceEnd - stampEnd - 1),
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out sequence))
            {
                return false;
            }

            stamp = fileName.Substring(0, stampEnd);
            return true;
        }

        private static bool IsAutoTimestamp(string text)
        {
            return DateTime.TryParseExact(
                text,
                "yyyyMMdd'T'HHmmssfff'Z'",
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out DateTime _);
        }

        private string AllocateUniqueAutoPath(string timestamp, string trigger)
        {
            IReadOnlyList<string> existingPaths = _fileSystem.GetFiles(_autoDirectory, Extension);
            // WHY never older than the newest existing name: the ring rotates the oldest name first,
            // so after a backwards clock step the autosave just written sorted before the ring and
            // was the next one rotated out, while stale autosaves from the future survived. Taking
            // the newest existing timestamp keeps name order equal to creation order.
            foreach (string existingPath in existingPaths)
            {
                string existingName = Path.GetFileNameWithoutExtension(existingPath);
                int stampEnd = existingName.IndexOf('-');
                if (stampEnd != timestamp.Length)
                {
                    continue;
                }

                string existingStamp = existingName.Substring(0, stampEnd);
                if (string.CompareOrdinal(existingStamp, timestamp) > 0 && IsAutoTimestamp(existingStamp))
                {
                    timestamp = existingStamp;
                }
            }

            string prefix = timestamp + "-";
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
            if (!RbxWorldPackageNames.TryValidateAutoFileName(fileName, out string error))
            {
                throw new ArgumentException(error, nameof(fileName));
            }

            return fileName;
        }

        private static string ValidateName(string value, string field)
        {
            if (!RbxWorldPackageNames.TryValidateManualSlot(value, field, out string trimmed, out string error))
            {
                throw new ArgumentException(error, nameof(value));
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

    /// <summary>
    /// The pure naming rules of the package store, callable without touching the store: the AI tools
    /// refuse a bad name here, before any service call, so an invalid name is an ordinary tool result
    /// and never an exception crossing the tool invocation boundary. The store keeps throwing the same
    /// messages for callers that bypass the tools.
    /// </summary>
    internal static class RbxWorldPackageNames
    {
        public const string Extension = ".world";
        public const int MaximumNameLength = 64;

        /// <summary>The <c>status</c> a load tool reports when it refused the call on its argument alone.</summary>
        public const string InvalidArgumentStatus = "invalid_argument";

        /// <summary>The <c>status</c> reported when the named package does not exist.</summary>
        public const string NotFoundStatus = "not_found";

        /// <summary>The <c>status</c> reported when the package is corrupt, above the read limits, or not loadable here.</summary>
        public const string InvalidPackageStatus = "invalid_package";

        /// <summary>The <c>status</c> reported when the package exists but could not be read.</summary>
        public const string ReadFailedStatus = "read_failed";

        /// <summary>The <c>status</c> <c>save_world</c> reports when the live world could not be captured.</summary>
        public const string CaptureFailedStatus = "capture_failed";

        /// <summary>The <c>status</c> a load tool reports when the world session was already shut down.</summary>
        public const string SessionUnavailableStatus = "session_unavailable";

        /// <summary>The tool-result text for a refused argument: the parameter, the rule, and that nothing ran.</summary>
        public static string DescribeInvalidArgument(string parameter, string ruleError)
        {
            return "Parameter '" + parameter + "' is invalid: " + ruleError
                   + " The tool was NOT executed. Retry with a valid '" + parameter + "'.";
        }

        /// <summary>The tool-result text for a load requested from a world session that was shut down.</summary>
        public static string DescribeSessionUnavailable(string subject)
        {
            return subject + " cannot be requested: the world session was shut down (the game is closing "
                   + "or restarting its session). No request was queued. The tool was NOT executed. "
                   + "Retry once the game has restarted its world session.";
        }

        private static readonly HashSet<string> ReservedDeviceNames = new(StringComparer.OrdinalIgnoreCase)
        {
            "CON", "PRN", "AUX", "NUL",
            "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
            "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
        };

        /// <summary>
        /// Accepts 1-64 letters, digits, '-' and '_' (surrounding whitespace trimmed) that are not a
        /// reserved Windows device name; <paramref name="field"/> names the value in the error text.
        /// </summary>
        public static bool TryValidateManualSlot(string value, string field, out string normalized, out string error)
        {
            normalized = null;
            error = null;
            string trimmed = value?.Trim();
            if (string.IsNullOrEmpty(trimmed) || trimmed.Length > MaximumNameLength)
            {
                error = field + " must contain 1-" + MaximumNameLength + " characters.";
                return false;
            }

            string baseName = trimmed;
            int dotIndex = trimmed.IndexOf('.');
            if (dotIndex >= 0)
            {
                baseName = trimmed.Substring(0, dotIndex);
            }

            if (ReservedDeviceNames.Contains(baseName))
            {
                error = field + " '" + trimmed + "' is a reserved device name.";
                return false;
            }

            for (int index = 0; index < trimmed.Length; index++)
            {
                char character = trimmed[index];
                bool allowed = char.IsLetterOrDigit(character)
                               || character == '-'
                               || character == '_';
                if (!allowed)
                {
                    error = field + " may contain only letters, digits, '-' and '_'.";
                    return false;
                }
            }

            normalized = trimmed;
            return true;
        }

        /// <summary>
        /// Accepts exactly one <c>.world</c> file name with no directory part and no character a file
        /// name cannot hold on any supported player.
        /// </summary>
        /// <remarks>
        /// WHY explicit character checks and no <see cref="Path"/> call: on Mono,
        /// <c>Path.GetFileName</c> throws <see cref="ArgumentException"/> for a name holding '"', '&lt;',
        /// '&gt;', '|' or a control character (Windows players) or '\0' (every player), and that throw
        /// escaped the tool as an exception instead of an ordinary refusal.
        /// </remarks>
        public static bool TryValidateAutoFileName(string fileName, out string error)
        {
            error = null;
            if (string.IsNullOrWhiteSpace(fileName)
                || !fileName.EndsWith(Extension, StringComparison.OrdinalIgnoreCase)
                || ContainsForbiddenFileNameCharacter(fileName))
            {
                error = "Auto package name must be one .world file name without a path.";
                return false;
            }

            // WHY the same device rule as a manual slot: on Windows "CON.world" or "nul.WORLD" opens the
            // device, not a file, and the open throws instead of refusing (audit B2-14).
            int dotIndex = fileName.IndexOf('.');
            if (ReservedDeviceNames.Contains(dotIndex >= 0 ? fileName.Substring(0, dotIndex) : fileName))
            {
                error = "Auto package name '" + fileName + "' is a reserved device name.";
                return false;
            }

            return true;
        }

        private static bool ContainsForbiddenFileNameCharacter(string fileName)
        {
            for (int index = 0; index < fileName.Length; index++)
            {
                char character = fileName[index];
                if (character < ' '
                    || character == '\u007F'
                    || character == '/'
                    || character == '\\'
                    || character == ':'
                    || character == '"'
                    || character == '<'
                    || character == '>'
                    || character == '|'
                    || character == '?'
                    || character == '*')
                {
                    return true;
                }
            }

            return false;
        }
    }
}
