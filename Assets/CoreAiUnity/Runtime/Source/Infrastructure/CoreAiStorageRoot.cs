using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace CoreAI.Infrastructure
{
    /// <summary>
    /// The one place CoreAI resolves where its file-backed stores live by default. Every default store
    /// path (agent memory, conversation summaries, token calibration, endpoint registry, Lua script and
    /// data-overlay versions, skills, the audit log, the world snapshot, Lua mod state, mod sources,
    /// world packages and the Lua log file) is built from <see cref="PersistentDataRoot"/>, so one call
    /// to <see cref="Override"/> moves all of them together.
    /// <para>
    /// The default is <see cref="Application.persistentDataPath"/>. A host overrides it before its
    /// scopes build (a dedicated server choosing a data volume, a launcher isolating profiles); the test
    /// run overrides it for its whole duration so no test can write into the developer's real saves.
    /// A store resolves the root once, in its constructor, so a store built under an override keeps its
    /// directory even if the override ends before the store is disposed.
    /// </para>
    /// <para>
    /// An explicit path passed to a store's constructor still wins over this root; the root only
    /// replaces the default that the parameterless shapes used to compute from
    /// <see cref="Application.persistentDataPath"/> directly.
    /// </para>
    /// <para>
    /// <b>WebGL.</b> The engine persists only <see cref="Application.persistentDataPath"/> to IndexedDB.
    /// An override outside it works for the session but does not survive a reload, and
    /// <c>CoreAiWebGlPersistence.Sync()</c> still reports the engine's arming state, not the override's.
    /// </para>
    /// </summary>
    public static class CoreAiStorageRoot
    {
        private static readonly object Gate = new();
        private static readonly List<OverrideScope> Scopes = new();
        private static volatile string _overrideRoot;

        /// <summary>
        /// The directory that contains the <c>CoreAI</c> data folder: the innermost active
        /// <see cref="Override"/>, otherwise <see cref="Application.persistentDataPath"/>.
        /// </summary>
        public static string PersistentDataRoot => _overrideRoot ?? Application.persistentDataPath;

        /// <summary>The <c>CoreAI</c> data folder under <see cref="PersistentDataRoot"/>.</summary>
        public static string CoreAiDirectory =>
            Path.Combine(PersistentDataRoot, CoreAiPersistentPaths.RootFolderName);

        /// <summary>True while at least one <see cref="Override"/> scope is active.</summary>
        public static bool IsOverridden => _overrideRoot != null;

        /// <summary>
        /// The engine default the root falls back to. Internal so tests can compare against it without
        /// letting production code bypass an override.
        /// </summary>
        internal static string EngineDefaultRoot => Application.persistentDataPath;

        /// <summary>
        /// Combines relative <paramref name="segments"/> under <see cref="CoreAiDirectory"/>.
        /// </summary>
        /// <exception cref="ArgumentException">
        /// A segment is empty, rooted or a traversal segment. <see cref="Path.Combine(string[])"/> would
        /// silently discard the root for a rooted segment, which is exactly the escape this rejects.
        /// </exception>
        public static string Combine(params string[] segments)
        {
            if (segments == null)
            {
                throw new ArgumentNullException(nameof(segments));
            }

            string path = CoreAiDirectory;
            foreach (string segment in segments)
            {
                if (string.IsNullOrWhiteSpace(segment))
                {
                    throw new ArgumentException("A storage path segment must not be empty.", nameof(segments));
                }

                if (Path.IsPathRooted(segment))
                {
                    throw new ArgumentException(
                        $"A storage path segment must be relative, but '{segment}' is rooted.", nameof(segments));
                }

                foreach (string part in segment.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
                {
                    if (part == "." || part == "..")
                    {
                        throw new ArgumentException(
                            $"A storage path segment must not contain traversal parts: '{segment}'.",
                            nameof(segments));
                    }
                }

                path = Path.Combine(path, segment);
            }

            return path;
        }

        /// <summary>
        /// Redirects <see cref="PersistentDataRoot"/> to <paramref name="rootDirectory"/> until the
        /// returned scope is disposed. Scopes nest: disposing one restores whatever was active before
        /// it, and disposing out of order removes only that scope. The directory is not created here;
        /// each store creates its own folder on first write, exactly as under the engine default.
        /// </summary>
        /// <param name="rootDirectory">An absolute directory without traversal segments.</param>
        /// <exception cref="ArgumentException">The path is empty, relative or contains traversal parts.</exception>
        public static IDisposable Override(string rootDirectory)
        {
            string normalized = NormalizeRoot(rootDirectory);
            OverrideScope scope = new(normalized);
            lock (Gate)
            {
                Scopes.Add(scope);
                _overrideRoot = normalized;
            }

            return scope;
        }

        private static string NormalizeRoot(string rootDirectory)
        {
            if (string.IsNullOrWhiteSpace(rootDirectory))
            {
                throw new ArgumentException("The storage root must be a non-empty absolute directory.",
                    nameof(rootDirectory));
            }

            string trimmed = rootDirectory.Trim();
            if (!Path.IsPathFullyQualified(trimmed))
            {
                throw new ArgumentException($"The storage root must be an absolute directory: '{trimmed}'.",
                    nameof(rootDirectory));
            }

            foreach (string part in trimmed.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
            {
                if (part == "." || part == "..")
                {
                    throw new ArgumentException(
                        $"The storage root must not contain traversal parts: '{trimmed}'.", nameof(rootDirectory));
                }
            }

            string full = Path.GetFullPath(trimmed);
            string pathRoot = Path.GetPathRoot(full) ?? string.Empty;
            return full.Length > pathRoot.Length
                ? full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                : full;
        }

        private static void Release(OverrideScope scope)
        {
            lock (Gate)
            {
                if (!Scopes.Remove(scope))
                {
                    return;
                }

                _overrideRoot = Scopes.Count > 0 ? Scopes[Scopes.Count - 1].Root : null;
            }
        }

        private sealed class OverrideScope : IDisposable
        {
            private bool _disposed;

            public OverrideScope(string root)
            {
                Root = root;
            }

            public string Root { get; }

            public void Dispose()
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                Release(this);
            }
        }
    }
}
