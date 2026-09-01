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
    /// <summary>
    /// File-backed Lua source store. Every instance sharing a root also shares one fail-fast mutation
    /// gate; exact world imports are written into an isolated session-version directory and durably
    /// synced before that directory can become a runtime session's source store.
    /// </summary>
    public sealed class FileLuaModSourceStore : ILuaModSourceStore, IRbxWorldModSourceStore, IDisposable
    {
        private const string ManifestFileName = "manifest.json";
        private const string SourceFileName = "main.lua";
        private const string WorldSessionsDirectoryName = ".world-sessions";
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

        public FileLuaModSourceStore(
            string rootDirectory = null,
            ILog log = null,
            string storeId = null,
            Func<CancellationToken, UniTask<bool>> persistenceSyncAsync = null)
            : this(rootDirectory, log, storeId, persistenceSyncAsync, false)
        {
        }

        private FileLuaModSourceStore(
            string rootDirectory,
            ILog log,
            string storeId,
            Func<CancellationToken, UniTask<bool>> persistenceSyncAsync,
            bool useCaseSafeFolderNames)
        {
            string baseDirectory = !string.IsNullOrWhiteSpace(rootDirectory)
                ? rootDirectory.Trim()
                : Path.Combine(
                    Application.persistentDataPath,
                    CoreAiPersistentPaths.RootFolderName,
                    CoreAiPersistentPaths.ModPackages);
            _dir = Path.GetFullPath(LuaModStoreId.ApplyTo(baseDirectory, storeId));
            _log = log;
            _rootState = RootStates.GetOrAdd(_dir, _ => new RootState());
            _persistenceSyncAsync = persistenceSyncAsync
                ?? (cancellationToken => CoreAiWebGlPersistence.SyncAsync(cancellationToken));
            _useCaseSafeFolderNames = useCaseSafeFolderNames;
        }

        public void Save(string id, string source, LuaModManifest manifest)
        {
            string modId = Normalize(id);
            if (modId.Length == 0)
            {
                return;
            }

            EnterRoot("save mod '" + modId + "'");
            try
            {
                LuaModManifest toWrite = CloneManifest(manifest ?? new LuaModManifest());
                toWrite.Id = modId;
                string modDirectory = GetModDirectory(_dir, modId, _useCaseSafeFolderNames);
                Directory.CreateDirectory(modDirectory);
                AtomicWriteAllText(
                    Path.Combine(modDirectory, ManifestFileName),
                    JsonConvert.SerializeObject(toWrite, JsonSettings));
                AtomicWriteAllText(
                    Path.Combine(modDirectory, SourceFileName),
                    source ?? "");
                CoreAiWebGlPersistence.Sync();
            }
            catch (Exception ex)
            {
                _log?.Error("[FileLuaModSourceStore] Save failed for " + modId + ": " + ex);
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
                string modDirectory = GetModDirectory(_dir, modId, _useCaseSafeFolderNames);
                string manifestPath = Path.Combine(modDirectory, ManifestFileName);
                string sourcePath = Path.Combine(modDirectory, SourceFileName);
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
                return Empty;
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
                    GetModDirectory(_dir, modId, _useCaseSafeFolderNames), ManifestFileName);
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
                AtomicWriteAllText(
                    manifestPath,
                    JsonConvert.SerializeObject(manifest, JsonSettings));
                CoreAiWebGlPersistence.Sync();
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
            string modId = Normalize(id);
            if (modId.Length == 0)
            {
                return;
            }

            EnterRoot("delete mod '" + modId + "'");
            try
            {
                string modDirectory = GetModDirectory(_dir, modId, _useCaseSafeFolderNames);
                if (Directory.Exists(modDirectory))
                {
                    Directory.Delete(modDirectory, true);
                    CoreAiWebGlPersistence.Sync();
                }
            }
            catch (Exception ex)
            {
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

            cancellationToken.ThrowIfCancellationRequested();
            string sessionDirectory = Path.Combine(
                _dir, WorldSessionsDirectoryName, Guid.NewGuid().ToString("N"));
            EnterRoot("prepare exact world mod source set");
            _rootState.AsyncMutationPending = true;
            try
            {
                CleanupOrphanedSessionVersions(sessionDirectory);
                Directory.CreateDirectory(sessionDirectory);
                HashSet<string> ids = new(StringComparer.Ordinal);
                for (int index = 0; index < mods.Count; index++)
                {
                    RbxWorldModSource mod = mods[index]
                        ?? throw new InvalidOperationException(
                            "The replacement source set contains a nil mod.");
                    string modId = Normalize(mod.Manifest?.Id);
                    if (modId.Length == 0 || !ids.Add(modId))
                    {
                        throw new InvalidOperationException(
                            "The replacement source set contains an empty or duplicate mod id.");
                    }

                    string modDirectory = GetModDirectory(
                        sessionDirectory,
                        modId,
                        useCaseSafeFolderNames: true);
                    Directory.CreateDirectory(modDirectory);
                    LuaModManifest manifest = CloneManifest(mod.Manifest);
                    manifest.Id = modId;
                    File.WriteAllText(
                        Path.Combine(modDirectory, ManifestFileName),
                        JsonConvert.SerializeObject(manifest, JsonSettings));
                    File.WriteAllText(
                        Path.Combine(modDirectory, SourceFileName),
                        mod.Source ?? "");
                }

                VerifyExactReplacement(sessionDirectory, mods);
            }
            catch
            {
                DeleteDirectory(sessionDirectory);
                _rootState.AsyncMutationPending = false;
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
                useCaseSafeFolderNames: true);
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

        private void ClearAsyncMutationPending()
        {
            _rootState.AsyncMutationPending = false;
        }

        private static string GetModDirectory(
            string rootDirectory,
            string modId,
            bool useCaseSafeFolderNames)
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
                        ? CaseSafeFolderName(modId)
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

        private static string CaseSafeFolderName(string modId)
        {
            byte[] idBytes = Encoding.UTF8.GetBytes(modId);
            byte[] digest;
            using (SHA256 algorithm = SHA256.Create())
            {
                digest = algorithm.ComputeHash(idBytes);
            }

            StringBuilder folderName = new(3 + digest.Length * 2);
            folderName.Append("id-");
            for (int index = 0; index < digest.Length; index++)
            {
                folderName.Append(digest[index].ToString("x2"));
            }

            return folderName.ToString();
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
                useCaseSafeFolderNames: true);
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

        private static void AtomicWriteAllText(string path, string contents)
        {
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
                if (string.Equals(directory, activeDirectory, StringComparison.Ordinal)
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
