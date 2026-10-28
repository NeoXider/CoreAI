using System;
using System.IO;
using System.Text.RegularExpressions;
using CoreAI.Infrastructure;
using CoreAI.Tests.PlayMode;
using NUnit.Framework.Interfaces;
using UnityEngine;
using UnityEngine.TestRunner;

[assembly: TestRunCallback(typeof(CoreAiTestStorageRootRunCallback))]

namespace CoreAI.Tests.PlayMode
{
    /// <summary>
    /// Redirects <see cref="CoreAiStorageRoot"/> to a fresh per-run temporary directory for every test
    /// run in this project, EditMode and PlayMode alike, and removes it when the run finishes.
    /// <para>
    /// WHY a run callback rather than per-fixture setup: the stores that damaged real saves were not
    /// built by storage tests. Scene smoke tests, chat demos and scope fixtures build production scopes
    /// whose default stores root at the persistent data path, and a full run on 2026-10-28 overwrote the
    /// developer's world snapshot, appended to the audit log, seeded sample mods and pruned autosaves.
    /// Unity discovers <see cref="TestRunCallbackAttribute"/> in every loaded test assembly and calls
    /// it before the first test of any run, so one declaration here covers every assembly, every scene
    /// a test loads, and every default store those scenes build.
    /// </para>
    /// <para>
    /// A domain reload in the middle of a run drops static state, so the active root is also kept in a
    /// process environment variable and re-applied before the next test starts.
    /// </para>
    /// </summary>
    public sealed class CoreAiTestStorageRootRunCallback : ITestRunCallback
    {
        /// <summary>Process variable holding the active run root across a mid-run domain reload.</summary>
        public const string RunRootVariable = "COREAI_STORAGE_TEST_RUN_ROOT";

        /// <summary>Name prefix of per-run directories under the system temporary folder.</summary>
        public const string RunDirectoryPrefix = "cai-ts-";

        /// <summary>Marker file that identifies a directory as a CoreAI test storage root.</summary>
        public const string MarkerFileName = ".coreai-test-storage";

        // WHY 10 hex characters: the per-run root must not be longer than the engine default it
        // replaces, or Windows path-budget checks in the mod source store would reject paths in tests
        // that production accepts. %TEMP%\cai-ts-<10 hex> is no longer than LocalLow\<company>\<product>.
        private static readonly Regex RunDirectoryName = new("^" + RunDirectoryPrefix + "[0-9a-f]{10}$");

        private static readonly TimeSpan StaleAge = TimeSpan.FromHours(12);

        private static IDisposable _scope;
        private static string _runRoot;

        /// <summary>The root this callback applied for the current run, or null outside a run.</summary>
        public static string ActiveRunRoot => _runRoot ?? Environment.GetEnvironmentVariable(RunRootVariable);

        /// <inheritdoc />
        public void RunStarted(ITest testsToRun)
        {
            Apply();
        }

        /// <inheritdoc />
        public void TestStarted(ITest test)
        {
            if (!CoreAiStorageRoot.IsOverridden)
            {
                Apply();
            }
        }

        /// <inheritdoc />
        public void TestFinished(ITestResult result)
        {
        }

        /// <inheritdoc />
        public void RunFinished(ITestResult testResults)
        {
            string root = ActiveRunRoot;
            _scope?.Dispose();
            _scope = null;
            _runRoot = null;
            Environment.SetEnvironmentVariable(RunRootVariable, null);
            DeleteOwnedRoot(root);
        }

        private static void Apply()
        {
            if (_scope != null && CoreAiStorageRoot.IsOverridden)
            {
                return;
            }

            string root = Environment.GetEnvironmentVariable(RunRootVariable);
            if (string.IsNullOrEmpty(root) || !IsOwnedRunRoot(root))
            {
                DeleteStaleRoots();
                root = CreateRunRoot();
                Environment.SetEnvironmentVariable(RunRootVariable, root);
            }
            else
            {
                Directory.CreateDirectory(root);
            }

            _runRoot = root;
            _scope = CoreAiStorageRoot.Override(root);
        }

        private static string CreateRunRoot()
        {
            string temp = Path.GetFullPath(Path.GetTempPath());
            string engineDefault = Path.GetFullPath(Application.persistentDataPath);
            if (IsUnder(temp, engineDefault))
            {
                throw new InvalidOperationException(
                    "The system temporary folder is inside the persistent data path; refusing to run tests " +
                    "with storage that would land in the developer's saves.");
            }

            for (int attempt = 0; attempt < 8; attempt++)
            {
                string candidate = Path.Combine(temp, RunDirectoryPrefix + Guid.NewGuid().ToString("N").Substring(0, 10));
                if (Directory.Exists(candidate))
                {
                    continue;
                }

                Directory.CreateDirectory(candidate);
                File.WriteAllText(Path.Combine(candidate, MarkerFileName), DateTime.UtcNow.ToString("O"));
                return candidate;
            }

            throw new IOException("Could not allocate a unique CoreAI test storage directory.");
        }

        /// <summary>
        /// True only for a directory directly under the system temporary folder whose name matches the
        /// run pattern. Cleanup never touches anything else.
        /// </summary>
        public static bool IsOwnedRunRoot(string root)
        {
            if (string.IsNullOrWhiteSpace(root) || !Path.IsPathFullyQualified(root))
            {
                return false;
            }

            string full = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string parent = Path.GetDirectoryName(full);
            string temp = Path.GetFullPath(Path.GetTempPath())
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return parent != null
                   && string.Equals(parent, temp, StringComparison.OrdinalIgnoreCase)
                   && RunDirectoryName.IsMatch(Path.GetFileName(full));
        }

        private static void DeleteOwnedRoot(string root)
        {
            if (!IsOwnedRunRoot(root) || !Directory.Exists(root))
            {
                return;
            }

            try
            {
                Directory.Delete(root, true);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                Debug.LogWarning($"[CoreAiTestStorageRoot] Could not remove test storage '{root}': {ex.Message}");
            }
        }

        private static void DeleteStaleRoots()
        {
            string temp = Path.GetTempPath();
            string[] candidates;
            try
            {
                candidates = Directory.GetDirectories(temp, RunDirectoryPrefix + "*");
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                return;
            }

            DateTime cutoff = DateTime.UtcNow - StaleAge;
            foreach (string candidate in candidates)
            {
                if (IsOwnedRunRoot(candidate) && Directory.GetLastWriteTimeUtc(candidate) < cutoff)
                {
                    DeleteOwnedRoot(candidate);
                }
            }
        }

        private static bool IsUnder(string path, string root)
        {
            string normalizedRoot = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                                    + Path.DirectorySeparatorChar;
            string normalizedPath = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                                    + Path.DirectorySeparatorChar;
            return normalizedPath.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase);
        }
    }
}
