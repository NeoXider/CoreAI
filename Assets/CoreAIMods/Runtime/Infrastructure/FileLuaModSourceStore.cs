using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using CoreAI.Ai;
using CoreAI.Infrastructure;
using CoreAI.Logging;
using CoreAI.Mods.WorldPackages;
using Cysharp.Threading.Tasks;
using Newtonsoft.Json;
using UnityEngine;

namespace CoreAI.Infrastructure.Lua
{
    /// <summary>A failed strict source mutation; local completion can precede persistence-arm refusal.</summary>
    public sealed class LuaModSourcePersistenceException : IOException
    {
        /// <summary>True when the requested local writes or removal completed before failure.</summary>
        public bool LocalCommitCompleted { get; }

        public LuaModSourcePersistenceException(bool localCommitCompleted, Exception innerException)
            : base(localCommitCompleted
                ? "The mod source operation committed locally, but automatic persistence is not armed. Retry the operation."
                : "The mod source operation did not complete. Retry the operation.", innerException)
        {
            LocalCommitCompleted = localCommitCompleted;
        }
    }

    /// <summary>
    /// File-backed Lua source store. Every instance sharing a root also shares one fail-fast mutation
    /// gate; exact world imports are written into an isolated session-version directory and their
    /// persistence hook must accept the write before that directory can become a session's source store.
    /// Canonical roots retain SHA-256 hex folders. Owned <c>.world-sessions</c> versions reserve
    /// <c>.source-layout</c>; new versions encode full GUIDs and SHA-256 digests as lowercase Base32.
    /// Reopening a recognized version validates its marker; marker-free versions retain hex folders.
    /// </summary>
    public sealed class FileLuaModSourceStore
        : ILuaModSourceStore, ILuaModSourceStrictWriter, ILuaModSourceStrictRemover,
          IRbxWorldModSourceStore, ILuaModSourceAdmission, IDisposable
    {
        private const string ManifestFileName = "manifest.json";
        private const string SourceFileName = "main.lua";
        private const string WorldSessionsDirectoryName = ".world-sessions";
        private const string SourceLayoutFileName = ".source-layout";
        private const string CompactSourceLayout = "sha256-base32-v1";
        private const string LowerBase32Alphabet = "abcdefghijklmnopqrstuvwxyz234567";
        private const int WindowsMaximumPathLength = 259;
        private const int MaximumRetainedWorldSessionVersions = 3;

        private static readonly JsonSerializerSettings JsonSettings = new()
        {
            Formatting = Formatting.Indented
        };

        private static readonly IReadOnlyList<LuaModManifest> Empty =
            new LuaModManifest[0];
        private static readonly ConcurrentDictionary<string, RootState> RootStates =
            new(StringComparer.Ordinal);

        private readonly string _dir;
        private readonly ILog _log;
        private readonly RootState _rootState;
        private readonly Func<CancellationToken, UniTask<bool>> _persistenceSyncAsync;
        private readonly bool _useCaseSafeFolderNames;
        private readonly bool _useCompactFolderNames;
        internal static Func<bool> PersistenceArmingForTesting;

        internal static Func<bool> FailFirstCanonicalInstallationForTesting;

        public FileLuaModSourceStore(
            string rootDirectory = null,
            ILog log = null,
            string storeId = null,
            Func<CancellationToken, UniTask<bool>> persistenceSyncAsync = null)
            : this(rootDirectory, log, storeId, persistenceSyncAsync, true)
        {
        }

        private FileLuaModSourceStore(
            string rootDirectory,
            ILog log,
            string storeId,
            Func<CancellationToken, UniTask<bool>> persistenceSyncAsync,
            bool useCaseSafeFolderNames,
            bool useCompactFolderNames = false)
        {
            string baseDirectory = !string.IsNullOrWhiteSpace(rootDirectory)
                ? rootDirectory.Trim()
                : Path.Combine(
                    Application.persistentDataPath,
                    CoreAiPersistentPaths.RootFolderName,
                    CoreAiPersistentPaths.ModPackages);
            _dir = Path.GetFullPath(LuaModStoreId.ApplyTo(baseDirectory, storeId));
            bool ownedVersion = IsWorldSessionDirectory(_dir);
            bool compactLayoutRequired = useCompactFolderNames
                || (ownedVersion && new DirectoryInfo(_dir).Name.Length == 26);
            bool compactLayoutDetected = (useCompactFolderNames || ownedVersion)
                && ReadCompactSourceLayout(_dir);
            if (compactLayoutRequired && !compactLayoutDetected)
            {
                throw new IOException("The compact Lua mod source layout marker is missing.");
            }

            _log = log;
            _rootState = RootStates.GetOrAdd(_dir, _ => new RootState());
            _persistenceSyncAsync = persistenceSyncAsync
                ?? (cancellationToken => CoreAiWebGlPersistence.SyncAsync(cancellationToken));
            _useCaseSafeFolderNames = useCaseSafeFolderNames;
            _useCompactFolderNames = useCompactFolderNames || compactLayoutDetected;
        }

        /// <summary>
        /// The durability hook exact world-source preparation awaits: the injected delegate, or the
        /// production default that asks <see cref="CoreAiWebGlPersistence.SyncAsync"/>. Read-only; tests
        /// use it to pin that default, which no EditMode run can observe through behaviour.
        /// </summary>
        internal Func<CancellationToken, UniTask<bool>> PersistenceSyncForTests => _persistenceSyncAsync;

        /// <summary>
        /// Saves the mod's source and manifest. A new mod id past the world-package mod limit is
        /// refused with <see cref="RbxWorldPackageFormatLimitException"/>; any other failure is logged.
        /// </summary>
        public void Save(string id, string source, LuaModManifest manifest)
        {
            SaveCore(id, source, manifest, false);
        }

        /// <inheritdoc />
        public void SaveStrict(string id, string source, LuaModManifest manifest)
        {
            SaveCore(id, source, manifest, true);
        }

        private void SaveCore(string id, string source, LuaModManifest manifest, bool strict)
        {
            string modId = Normalize(id);
            if (modId.Length == 0)
            {
                if (strict) { throw new ArgumentException("A mod id is required for a strict source save.", nameof(id)); }
                return;
            }

            bool localCommitCompleted = false;
            EnterRoot("save mod '" + modId + "'");
            try
            {
                LuaModManifest toWrite = CloneManifest(manifest ?? new LuaModManifest());
                toWrite.Id = modId;
                string modDirectory = ResolveModDirectory(modId, forWrite: true);
                // WHY refused here, where a source is created: an unloaded mod keeps its source, so a
                // 257th stored id makes every capture of this world throw, which blocks every gated
                // mutation and every save until a mod is forgotten, and persists across restarts.
                // WHY thrown rather than logged: a caller that goes on as if the source were kept runs
                // a mod that is missing from every save and from the next start.
                if (!AdmitsLocked(modDirectory, modId, out string refusal))
                {
                    _log?.Error("[FileLuaModSourceStore] Save refused: " + refusal);
                    throw new RbxWorldPackageFormatLimitException(refusal);
                }

                // WHY stage a brand-new canonical package before publishing it: a transient I/O fault
                // before the first manifest is written must not leave an invalid canonical directory
                // that blocks every later retry. Owned staging is deleted on failure; foreign paths
                // and previously seeded evidence are never touched.
                bool firstInstallation = !Directory.Exists(modDirectory);
                string stagingDirectory = null;
                try
                {
                    if (firstInstallation)
                    {
                        string rootFull = Path.GetFullPath(_dir);
                        string rootPrefix = rootFull.EndsWith(
                            Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal)
                            ? rootFull
                            : rootFull + Path.DirectorySeparatorChar;
                        stagingDirectory = Path.GetFullPath(Path.Combine(
                            rootFull, ".staging-" + Guid.NewGuid().ToString("N")));
                        if (!stagingDirectory.StartsWith(rootPrefix, StringComparison.Ordinal))
                        {
                            throw new InvalidOperationException(
                                "[FileLuaModSourceStore] Staging path resolves outside the store root.");
                        }

                        EnsureSourcePaths(stagingDirectory, includeTemporaryPaths: true);
                        EnsureWindowsPathLength(stagingDirectory);
                        Directory.CreateDirectory(stagingDirectory);
                        if (FailFirstCanonicalInstallationForTesting != null &&
                            FailFirstCanonicalInstallationForTesting())
                        {
                            throw new IOException("Injected first canonical installation failure.");
                        }

                        AtomicWriteAllText(
                            Path.Combine(stagingDirectory, ManifestFileName),
                            JsonConvert.SerializeObject(toWrite, JsonSettings));
                        AtomicWriteAllText(
                            Path.Combine(stagingDirectory, SourceFileName),
                            source ?? "");

                        if (Directory.Exists(modDirectory))
                        {
                            throw new IOException("The canonical mod directory appeared during staging.");
                        }

                        Directory.Move(stagingDirectory, modDirectory);
                        stagingDirectory = null;
                    }
                    else
                    {
                        Directory.CreateDirectory(modDirectory);
                        AtomicWriteAllText(
                            Path.Combine(modDirectory, ManifestFileName),
                            JsonConvert.SerializeObject(toWrite, JsonSettings));
                        AtomicWriteAllText(
                            Path.Combine(modDirectory, SourceFileName),
                            source ?? "");
                    }

                    localCommitCompleted = true;
                    RequestPersistenceArming();
                }
                finally
                {
                    if (stagingDirectory != null && Directory.Exists(stagingDirectory))
                    {
                        Directory.Delete(stagingDirectory, true);
                    }
                }
            }
            catch (RbxWorldPackageFormatLimitException)
            {
                throw;
            }
            catch (OperationCanceledException) when (strict)
            {
                throw;
            }
            catch (Exception ex)
            {
                if (strict) { throw new LuaModSourcePersistenceException(localCommitCompleted, ex); }
                _log?.Error("[FileLuaModSourceStore] Save failed for " + modId + ": " + ex);
            }
            finally
            {
                ExitRoot();
            }
        }

        /// <summary>
        /// True when <see cref="Save"/> would keep the source of <paramref name="id"/> now: the store
        /// already holds a manifest file for it, or fewer mod folders than one world package holds.
        /// Throws when the store is busy.
        /// </summary>
        bool ILuaModSourceAdmission.CanAdmit(string id, out string refusal)
        {
            refusal = "";
            string modId = Normalize(id);
            if (modId.Length == 0)
            {
                return true;
            }

            EnterRoot("check room for mod '" + modId + "'");
            try
            {
                return AdmitsLocked(
                    ResolveModDirectory(modId, forWrite: true), modId, out refusal);
            }
            finally
            {
                ExitRoot();
            }
        }

        public bool TryLoad(string id, out string source, out LuaModManifest manifest)
        {
            source = "";
            manifest = null;
            string modId = Normalize(id);
            if (modId.Length == 0)
            {
                return false;
            }

            EnterRoot("load mod '" + modId + "'");
            try
            {
                string modDirectory = ResolveModDirectory(modId);
                string manifestPath = Path.Combine(modDirectory, ManifestFileName);
                string sourcePath = Path.Combine(modDirectory, SourceFileName);
                if (!File.Exists(manifestPath) || !File.Exists(sourcePath))
                {
                    return false;
                }

                LuaModManifest loaded = ReadManifest(manifestPath);
                if (loaded == null || !string.Equals(loaded.Id, modId, StringComparison.Ordinal))
                {
                    return false;
                }

                source = File.ReadAllText(sourcePath);
                manifest = loaded;
                return true;
            }
            catch (Exception ex)
            {
                _log?.Error("[FileLuaModSourceStore] Load failed for " + modId + ": " + ex);
                source = "";
                manifest = null;
                return false;
            }
            finally
            {
                ExitRoot();
            }
        }

        /// <summary>
        /// Every readable stored manifest, sorted by id; a manifest that cannot be read is logged and left
        /// out. When the store cannot be listed at all the answer is empty and marked unreadable, so a
        /// load stamped meanwhile records no load order rather than the first one.
        /// </summary>
        public IReadOnlyList<LuaModManifest> List()
        {
            EnterRoot("list mods");
            try
            {
                if (!Directory.Exists(_dir))
                {
                    return Empty;
                }

                List<LuaModManifest> result = new();
                string[] directories = Directory.GetDirectories(_dir);
                for (int index = 0; index < directories.Length; index++)
                {
                    string manifestPath = Path.Combine(directories[index], ManifestFileName);
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

                result.Sort((left, right) => string.CompareOrdinal(left.Id, right.Id));
                return result;
            }
            catch (Exception ex)
            {
                _log?.Error("[FileLuaModSourceStore] List failed: " + ex);
                // WHY not the shared empty list: a caller stamping a load order must not read a store it
                // could not list as a store that holds nothing (C2-08); every other caller still sees none.
                return new UnreadableLuaModSourceListing(
                    "The mod source store at '" + _dir + "' could not be listed: " + ex.Message);
            }
            finally
            {
                ExitRoot();
            }
        }

        public void SetActive(string id, bool active)
        {
            string modId = Normalize(id);
            if (modId.Length == 0)
            {
                return;
            }

            EnterRoot("set active state for mod '" + modId + "'");
            try
            {
                string manifestPath = Path.Combine(
                    ResolveModDirectory(modId, forWrite: true), ManifestFileName);
                if (!File.Exists(manifestPath))
                {
                    return;
                }

                LuaModManifest manifest = ReadManifest(manifestPath);
                if (manifest == null || !string.Equals(manifest.Id, modId, StringComparison.Ordinal))
                {
                    return;
                }

                manifest.Active = active;
                AtomicWriteAllText(
                    manifestPath,
                    JsonConvert.SerializeObject(manifest, JsonSettings));
                RequestPersistenceArming();
            }
            catch (Exception ex)
            {
                _log?.Error(
                    "[FileLuaModSourceStore] SetActive failed for " + modId + ": " + ex);
            }
            finally
            {
                ExitRoot();
            }
        }

        public void Delete(string id)
        {
            DeleteCore(id, false);
        }

        /// <inheritdoc />
        public void DeleteStrict(string id)
        {
            DeleteCore(id, true);
        }

        private void DeleteCore(string id, bool strict)
        {
            string modId = Normalize(id);
            if (modId.Length == 0)
            {
                if (strict) { throw new ArgumentException("A mod id is required for a strict source removal.", nameof(id)); }
                return;
            }

            bool localCommitCompleted = false;
            EnterRoot("delete mod '" + modId + "'");
            try
            {
                string modDirectory = ResolveModDirectory(modId, strict);
                if (Directory.Exists(modDirectory))
                {
                    Directory.Delete(modDirectory, true);
                    localCommitCompleted = true;
                    RequestPersistenceArming();
                }
                else if (strict)
                {
                    localCommitCompleted = true;
                    RequestPersistenceArming();
                }
            }
            catch (OperationCanceledException) when (strict)
            {
                throw;
            }
            catch (Exception ex)
            {
                if (strict) { throw new LuaModSourcePersistenceException(localCommitCompleted, ex); }
                _log?.Error("[FileLuaModSourceStore] Delete failed for " + modId + ": " + ex);
            }
            finally
            {
                ExitRoot();
            }
        }

        public async UniTask<IRbxWorldModSourceReplacement> PrepareExactReplacementAsync(
            IReadOnlyList<RbxWorldModSource> mods,
            CancellationToken cancellationToken = default)
        {
            if (mods == null)
            {
                throw new ArgumentNullException(nameof(mods));
            }

            int modCount = mods.Count;
            if (modCount > RbxWorldPackageSerializer.MaximumMods)
            {
                throw new RbxWorldPackageFormatLimitException(
                    "The replacement source set holds " + modCount + " mods; a world package holds at most "
                    + RbxWorldPackageSerializer.MaximumMods + ".");
            }

            cancellationToken.ThrowIfCancellationRequested();
            string sessionDirectory = Path.Combine(
                _dir, WorldSessionsDirectoryName, EncodeLowerBase32(Guid.NewGuid().ToByteArray()));
            EnsureWindowsPathLength(Path.Combine(sessionDirectory, SourceLayoutFileName));
            List<RbxWorldModSource> stagedMods = new(modCount);
            HashSet<string> ids = new(StringComparer.Ordinal);
            for (int index = 0; index < modCount; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                RbxWorldModSource mod = mods[index]
                    ?? throw new InvalidOperationException(
                        "The replacement source set contains a nil mod.");
                LuaModManifest manifest = CloneManifest(mod.Manifest);
                string modId = Normalize(manifest.Id);
                if (modId.Length == 0 || !ids.Add(modId))
                {
                    throw new InvalidOperationException(
                        "The replacement source set contains an empty or duplicate mod id.");
                }

                manifest.Id = modId;
                EnsureSourcePaths(GetModDirectory(
                    sessionDirectory, modId, useCaseSafeFolderNames: true, useCompactFolderNames: true),
                    includeTemporaryPaths: true);
                stagedMods.Add(new RbxWorldModSource(manifest, mod.Source));
            }

            cancellationToken.ThrowIfCancellationRequested();
            EnterRoot("prepare exact world mod source set");
            _rootState.AsyncMutationPending = true;
            bool ownsSessionDirectory = false;
            try
            {
                if (Directory.Exists(sessionDirectory) || File.Exists(sessionDirectory))
                {
                    throw new IOException("The exact world source session version already exists; no data was replaced.");
                }

                CleanupOrphanedSessionVersions(sessionDirectory);
                Directory.CreateDirectory(sessionDirectory);
                using (FileStream marker = new(
                    Path.Combine(sessionDirectory, SourceLayoutFileName), FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    ownsSessionDirectory = true;
                    byte[] markerBytes = Encoding.ASCII.GetBytes(CompactSourceLayout);
                    marker.Write(markerBytes, 0, markerBytes.Length);
                }

                for (int index = 0; index < stagedMods.Count; index++)
                {
                    RbxWorldModSource mod = stagedMods[index];
                    string modId = mod.Manifest.Id;
                    string modDirectory = GetModDirectory(
                        sessionDirectory,
                        modId,
                        useCaseSafeFolderNames: true,
                        useCompactFolderNames: true);
                    Directory.CreateDirectory(modDirectory);
                    File.WriteAllText(
                        Path.Combine(modDirectory, ManifestFileName),
                        JsonConvert.SerializeObject(mod.Manifest, JsonSettings));
                    File.WriteAllText(
                        Path.Combine(modDirectory, SourceFileName),
                        mod.Source ?? "");
                }

                VerifyExactReplacement(sessionDirectory, stagedMods);
            }
            catch
            {
                try
                {
                    if (ownsSessionDirectory)
                    {
                        DeleteDirectory(sessionDirectory);
                    }
                }
                finally
                {
                    _rootState.AsyncMutationPending = false;
                }

                throw;
            }
            finally
            {
                ExitRoot();
            }

            bool persisted = false;
            Exception persistenceError = null;
            try
            {
                persisted = await _persistenceSyncAsync(cancellationToken);
            }
            catch (Exception ex)
            {
                persistenceError = ex;
            }

            if (!persisted)
            {
                Exception cleanupError = null;
                try
                {
                    EnterRoot(
                        "clean an unconfirmed exact world mod source set",
                        allowAsyncMutationPending: true);
                    try
                    {
                        DeleteDirectory(sessionDirectory);
                    }
                    finally
                    {
                        ExitRoot();
                    }

                    await _persistenceSyncAsync(CancellationToken.None);
                }
                catch (Exception ex)
                {
                    cleanupError = ex;
                }
                finally
                {
                    ClearAsyncMutationPending();
                }

                if (persistenceError is OperationCanceledException cancellationError)
                {
                    throw cancellationError;
                }

                throw new IOException(
                    cleanupError == null
                        ? "Exact world mod sources were written, but durable persistence was not confirmed."
                        : "Exact world mod sources were not confirmed and cleanup persistence also failed.",
                    persistenceError ?? cleanupError);
            }

            ClearAsyncMutationPending();

            FileLuaModSourceStore sessionStore = new(
                sessionDirectory,
                _log,
                storeId: null,
                persistenceSyncAsync: _persistenceSyncAsync,
                useCaseSafeFolderNames: true,
                useCompactFolderNames: true);
            return new ExactReplacement(this, sessionDirectory, sessionStore);
        }

        public void Dispose()
        {
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
                _log?.Error(
                    "[FileLuaModSourceStore] Manifest read failed for "
                    + manifestPath + ": " + ex);
                return null;
            }
        }

        private void EnterRoot(string operation, bool allowAsyncMutationPending = false)
        {
            if (!Monitor.TryEnter(_rootState.Gate))
            {
                throw new InvalidOperationException(
                    "Lua mod source store is busy; cannot " + operation
                    + " while another mutation owns root '" + _dir + "'.");
            }

            if (_rootState.AsyncMutationPending && !allowAsyncMutationPending)
            {
                Monitor.Exit(_rootState.Gate);
                throw new InvalidOperationException(
                    "Lua mod source store is busy; cannot " + operation
                    + " while durable exact-source preparation is pending for root '"
                    + _dir + "'.");
            }
        }

        private void ExitRoot()
        {
            Monitor.Exit(_rootState.Gate);
        }

        /// <summary>
        /// The one admission rule of this store, shared by <see cref="Save"/> and
        /// <see cref="ILuaModSourceAdmission.CanAdmit"/>: an id whose folder holds a manifest file is
        /// always kept, a new one only while fewer folders hold one than a world package can.
        /// </summary>
        private bool AdmitsLocked(string modDirectory, string modId, out string refusal)
        {
            refusal = "";
            if (File.Exists(Path.Combine(modDirectory, ManifestFileName)))
            {
                return true;
            }

            int stored = CountStoredMods();
            if (stored < RbxWorldPackageSerializer.MaximumMods)
            {
                return true;
            }

            refusal = RbxWorldPackageFormatLimitException.DescribeModSourceLimit(modId, stored);
            return false;
        }

        /// <summary>Stored mod ids: the store root's folders that hold a manifest file, readable or not.</summary>
        /// <remarks>
        /// WHY an unreadable manifest counts: once repaired it is listed and captured again, and a store
        /// that had admitted a new mod in its place would then hold more mods than one package captures.
        /// </remarks>
        private int CountStoredMods()
        {
            if (!Directory.Exists(_dir))
            {
                return 0;
            }

            int count = 0;
            string[] directories = Directory.GetDirectories(_dir);
            for (int index = 0; index < directories.Length; index++)
            {
                if (File.Exists(Path.Combine(directories[index], ManifestFileName)))
                {
                    count++;
                }
            }

            return count;
        }

        private void ClearAsyncMutationPending()
        {
            _rootState.AsyncMutationPending = false;
        }

        private string ResolveModDirectory(
            string modId, bool requireKnownLegacyIdentity = false, bool forWrite = false)
        {
            string destination = GetModDirectory(
                _dir, modId, _useCaseSafeFolderNames, _useCompactFolderNames);
            EnsureSourcePaths(destination, includeTemporaryPaths: forWrite);
            if (Directory.Exists(destination))
            {
                LuaModManifest existing = ReadManifest(Path.Combine(destination, ManifestFileName));
                if (existing == null || !string.Equals(existing.Id, modId, StringComparison.Ordinal))
                {
                    throw new IOException("Stored mod manifest does not match the requested identity.");
                }

                return destination;
            }

            string legacy = GetModDirectory(_dir, modId, false);
            if (Directory.Exists(legacy))
            {
                LuaModManifest manifest = ReadManifest(Path.Combine(legacy, ManifestFileName));
                if (manifest == null && requireKnownLegacyIdentity)
                {
                    throw new IOException("The legacy mod package identity is unreadable; administrative recovery is required.");
                }

                if (manifest != null && string.Equals(manifest.Id, modId, StringComparison.Ordinal))
                {
                    EnsureWindowsPathLength(legacy);
                    Directory.Move(legacy, destination);
                    RequestPersistenceArming();
                }
            }

            return destination;
        }

        private static void RequestPersistenceArming()
        {
            if (!(PersistenceArmingForTesting?.Invoke() ?? CoreAiWebGlPersistence.Sync()))
            {
                throw new IOException("The mod source change was committed locally, but automatic persistence is not armed.");
            }
        }

        private static string GetModDirectory(
            string rootDirectory,
            string modId,
            bool useCaseSafeFolderNames,
            bool useCompactFolderNames = false)
        {
            string rootFull = Path.GetFullPath(rootDirectory);
            string rootPrefix = rootFull.EndsWith(
                Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal)
                ? rootFull
                : rootFull + Path.DirectorySeparatorChar;
            string fullPath = Path.GetFullPath(
                Path.Combine(
                    rootFull,
                    useCaseSafeFolderNames
                        ? CaseSafeFolderName(modId, useCompactFolderNames)
                        : SanitizedFolderName(modId)));
            if (!fullPath.StartsWith(rootPrefix, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "[FileLuaModSourceStore] Mod id resolves outside the store root.");
            }

            return fullPath;
        }

        private static string SanitizedFolderName(string modId)
        {
            string safe = string.Join("_", modId.Split(Path.GetInvalidFileNameChars()));
            if (string.Equals(safe, modId, StringComparison.Ordinal))
            {
                return safe;
            }

            uint hash = 2166136261u;
            foreach (char character in modId)
            {
                hash = (hash ^ character) * 16777619u;
            }

            return safe + "_" + hash.ToString("x8");
        }

        private static string CaseSafeFolderName(string modId, bool useCompactFolderNames)
        {
            byte[] idBytes = Encoding.UTF8.GetBytes(modId);
            byte[] digest;
            using (SHA256 algorithm = SHA256.Create())
            {
                digest = algorithm.ComputeHash(idBytes);
            }

            if (useCompactFolderNames)
            {
                return "id-" + EncodeLowerBase32(digest);
            }

            StringBuilder folderName = new(3 + digest.Length * 2);
            folderName.Append("id-");
            for (int index = 0; index < digest.Length; index++)
            {
                folderName.Append(digest[index].ToString("x2"));
            }

            return folderName.ToString();
        }

        private static string EncodeLowerBase32(byte[] bytes)
        {
            StringBuilder encoded = new((bytes.Length * 8 + 4) / 5);
            uint pending = 0;
            int pendingBits = 0;
            foreach (byte value in bytes)
            {
                pending = (pending << 8) | value;
                pendingBits += 8;
                while (pendingBits >= 5)
                {
                    pendingBits -= 5;
                    encoded.Append(LowerBase32Alphabet[(int)((pending >> pendingBits) & 31u)]);
                }

                pending &= (1u << pendingBits) - 1u;
            }

            if (pendingBits > 0)
            {
                encoded.Append(LowerBase32Alphabet[(int)(pending << (5 - pendingBits))]);
            }

            return encoded.ToString();
        }

        private static bool ReadCompactSourceLayout(string directory)
        {
            string markerPath = Path.Combine(directory, SourceLayoutFileName);
            if (Directory.Exists(markerPath))
            {
                throw new IOException("The Lua mod source layout marker must be a supported regular file.");
            }

            try
            {
                using FileStream stream = new(markerPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                if (stream.Length != CompactSourceLayout.Length)
                {
                    throw new IOException("The Lua mod source layout marker is malformed or unsupported.");
                }

                foreach (char expected in CompactSourceLayout)
                {
                    if (stream.ReadByte() != expected)
                    {
                        throw new IOException("The Lua mod source layout marker is malformed or unsupported.");
                    }
                }

                if (stream.ReadByte() != -1)
                {
                    throw new IOException("The Lua mod source layout marker is malformed or unsupported.");
                }

                return true;
            }
            catch (FileNotFoundException)
            {
                return false;
            }
            catch (DirectoryNotFoundException)
            {
                return false;
            }
        }

        private static bool IsWorldSessionDirectory(string directory)
        {
            DirectoryInfo version = new(directory);
            DirectoryInfo parent = version.Parent;
            bool windows = Path.DirectorySeparatorChar == '\\';
            StringComparison comparison = windows ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if (parent == null || !string.Equals(parent.Name, WorldSessionsDirectoryName, comparison))
            {
                return false;
            }

            string name = version.Name;
            if (name.Length == 32 && Guid.TryParseExact(name, "N", out _))
            {
                return true;
            }

            if (name.Length != 26)
            {
                return false;
            }

            int lastValue = -1;
            foreach (char character in name)
            {
                lastValue = LowerBase32Alphabet.IndexOf(windows ? char.ToLowerInvariant(character) : character);
                if (lastValue < 0)
                {
                    return false;
                }
            }

            return (lastValue & 3) == 0;
        }

        private void EnsureSourcePaths(string directory, bool includeTemporaryPaths)
        {
            EnsureWindowsPathLength(Path.Combine(directory, ManifestFileName));
            EnsureWindowsPathLength(Path.Combine(directory, SourceFileName));
            if (includeTemporaryPaths)
            {
                EnsureWindowsPathLength(Path.Combine(directory, ManifestFileName + ".tmp"));
                EnsureWindowsPathLength(Path.Combine(directory, SourceFileName + ".tmp"));
            }
        }

        private void EnsureWindowsPathLength(string path)
        {
            if (Path.DirectorySeparatorChar == '\\' && path.Length > WindowsMaximumPathLength)
            {
                throw new PathTooLongException(
                    "The Lua mod source root '" + _dir + "' requires a Windows source path of "
                    + path.Length + " characters; at most " + WindowsMaximumPathLength
                    + " are supported. Configure a shorter source root.");
            }
        }

        private void VerifyExactReplacement(
            string sessionDirectory,
            IReadOnlyList<RbxWorldModSource> expected)
        {
            FileLuaModSourceStore verificationStore = new(
                sessionDirectory,
                _log,
                storeId: null,
                persistenceSyncAsync: _persistenceSyncAsync,
                useCaseSafeFolderNames: true,
                useCompactFolderNames: true);
            IReadOnlyList<LuaModManifest> actualManifests = verificationStore.List();
            if (actualManifests.Count != expected.Count)
            {
                throw new IOException(
                    "Exact world mod source verification found a different package count.");
            }

            for (int index = 0; index < expected.Count; index++)
            {
                RbxWorldModSource expectedMod = expected[index];
                string expectedId = Normalize(expectedMod.Manifest.Id);
                if (!verificationStore.TryLoad(
                        expectedId,
                        out string actualSource,
                        out LuaModManifest actualManifest)
                    || actualManifest == null
                    || !string.Equals(actualManifest.Id, expectedId, StringComparison.Ordinal)
                    || !string.Equals(actualSource, expectedMod.Source ?? "", StringComparison.Ordinal))
                {
                    throw new IOException(
                        "Exact world mod source verification failed for mod '"
                        + expectedId + "'.");
                }

                LuaModManifest expectedManifest = CloneManifest(expectedMod.Manifest);
                expectedManifest.Id = expectedId;
                string expectedJson = JsonConvert.SerializeObject(expectedManifest, JsonSettings);
                string actualJson = JsonConvert.SerializeObject(actualManifest, JsonSettings);
                if (!string.Equals(expectedJson, actualJson, StringComparison.Ordinal))
                {
                    throw new IOException(
                        "Exact world mod manifest verification failed for mod '"
                        + expectedId + "'.");
                }
            }
        }

        private void AtomicWriteAllText(string path, string contents)
        {
            EnsureWindowsPathLength(path);
            EnsureWindowsPathLength(path + ".tmp");
            string directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            string temporaryPath = path + ".tmp";
            File.WriteAllText(temporaryPath, contents);
            if (File.Exists(path))
            {
                File.Replace(temporaryPath, path, null);
            }
            else
            {
                File.Move(temporaryPath, path);
            }
        }

        private static LuaModManifest CloneManifest(LuaModManifest source)
        {
            if (source == null)
            {
                throw new ArgumentNullException(nameof(source));
            }

            return JsonConvert.DeserializeObject<LuaModManifest>(
                JsonConvert.SerializeObject(source, JsonSettings), JsonSettings)
                ?? throw new InvalidOperationException("The mod manifest could not be cloned.");
        }

        private static string Normalize(string value)
        {
            return (value ?? "").Trim();
        }

        private static void DeleteDirectory(string path)
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, true);
            }
        }

        private void CleanupOrphanedSessionVersions(string incomingDirectory)
        {
            string sessionsRoot = Path.Combine(_dir, WorldSessionsDirectoryName);
            if (!Directory.Exists(sessionsRoot))
            {
                return;
            }

            string activeDirectory = _rootState.ActiveRuntimeDirectory;
            List<string> removable = new();
            string[] directories = Directory.GetDirectories(sessionsRoot);
            for (int index = 0; index < directories.Length; index++)
            {
                string directory = Path.GetFullPath(directories[index]);
                if (!IsWorldSessionDirectory(directory)
                    || string.Equals(directory, activeDirectory, StringComparison.Ordinal)
                    || string.Equals(directory, incomingDirectory, StringComparison.Ordinal))
                {
                    continue;
                }

                removable.Add(directory);
            }

            removable.Sort((left, right) =>
                Directory.GetLastWriteTimeUtc(left).CompareTo(Directory.GetLastWriteTimeUtc(right)));
            int removeCount = Math.Max(
                0,
                removable.Count - MaximumRetainedWorldSessionVersions + 2);
            for (int index = 0; index < removeCount; index++)
            {
                DeleteDirectory(removable[index]);
                _log?.Info(
                    "[FileLuaModSourceStore] Removed orphan world-source version '"
                        + removable[index] + "'.");
            }
        }

        private sealed class RootState
        {
            public object Gate { get; } = new();

            public volatile bool AsyncMutationPending;

            public string ActiveRuntimeDirectory;
        }

        private sealed class ExactReplacement : IRbxWorldModSourceReplacement
        {
            private readonly FileLuaModSourceStore _owner;
            private readonly string _sessionDirectory;
            private bool _activated;
            private bool _completed;
            private bool _disposed;
            private string _previousActiveDirectory;

            public ExactReplacement(
                FileLuaModSourceStore owner,
                string sessionDirectory,
                ILuaModSourceStore sourceStore)
            {
                _owner = owner;
                _sessionDirectory = sessionDirectory;
                SourceStore = sourceStore;
            }

            public ILuaModSourceStore SourceStore { get; }

            public void Activate()
            {
                if (_disposed)
                {
                    throw new ObjectDisposedException(nameof(ExactReplacement));
                }

                _owner.EnterRoot("activate exact world mod sources");
                try
                {
                    _activated = true;
                    _previousActiveDirectory = _owner._rootState.ActiveRuntimeDirectory;
                    _owner._rootState.ActiveRuntimeDirectory = _sessionDirectory;
                }
                finally
                {
                    _owner.ExitRoot();
                }
            }

            public UniTask CompleteAsync(CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!_activated)
                {
                    throw new InvalidOperationException(
                        "The exact source replacement was not activated.");
                }

                _completed = true;
                return UniTask.CompletedTask;
            }

            public async UniTask RollbackAsync(CancellationToken cancellationToken = default)
            {
                if (_completed || _disposed)
                {
                    return;
                }

                _owner.EnterRoot("roll back exact world mod sources");
                try
                {
                    if (_activated
                        && string.Equals(
                            _owner._rootState.ActiveRuntimeDirectory,
                            _sessionDirectory,
                            StringComparison.Ordinal))
                    {
                        _owner._rootState.ActiveRuntimeDirectory = _previousActiveDirectory;
                    }

                    DeleteDirectory(_sessionDirectory);
                }
                finally
                {
                    _owner.ExitRoot();
                }

                bool persisted = await _owner._persistenceSyncAsync(cancellationToken);
                if (!persisted)
                {
                    throw new IOException(
                        "Exact world mod source rollback was not durably confirmed.");
                }

                _disposed = true;
            }

            public void Dispose()
            {
                if (_completed || _disposed)
                {
                    return;
                }

                _owner.EnterRoot("dispose exact world mod sources");
                try
                {
                    if (_activated
                        && string.Equals(
                            _owner._rootState.ActiveRuntimeDirectory,
                            _sessionDirectory,
                            StringComparison.Ordinal))
                    {
                        _owner._rootState.ActiveRuntimeDirectory = _previousActiveDirectory;
                    }

                    DeleteDirectory(_sessionDirectory);
                }
                finally
                {
                    _owner.ExitRoot();
                }

                _disposed = true;
            }
        }
    }
}
