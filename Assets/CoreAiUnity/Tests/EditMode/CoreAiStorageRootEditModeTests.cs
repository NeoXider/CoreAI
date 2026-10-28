using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using CoreAI.Composition;
using CoreAI.Features.Audit;
using CoreAI.Infrastructure;
using CoreAI.Infrastructure.AiMemory;
using CoreAI.Infrastructure.Llm;
using CoreAI.Infrastructure.Logging;
using CoreAI.Infrastructure.Lua;
using CoreAI.Infrastructure.World;
using CoreAI.Tests.PlayMode;
using NUnit.Framework;
using UnityEngine;

namespace CoreAI.Tests.EditMode
{
    /// <summary>
    /// The single storage root every default CoreAI store resolves from, the test-run redirect that
    /// keeps tests out of the developer's saves, and the guard that keeps the engine data path from
    /// being read anywhere else.
    /// <para>
    /// WHY this exists: a full EditMode run deleted five world autosave packages from the developer's
    /// persistent data folder and a hermetic PlayMode run overwrote the world snapshot, appended to the
    /// audit log and seeded sample mods there. Sixteen runtime files computed their default from the
    /// engine path directly, so isolating one store at a time always missed one.
    /// </para>
    /// </summary>
    public sealed class CoreAiStorageRootEditModeTests
    {
        private string _ownedRoot;

        [SetUp]
        public void SetUp()
        {
            _ownedRoot = Path.Combine(Path.GetTempPath(), "cai-root-" + Guid.NewGuid().ToString("N").Substring(0, 10));
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_ownedRoot))
            {
                Directory.Delete(_ownedRoot, true);
            }
        }

        [Test]
        public void TestRun_RedirectsTheRootToAnOwnedTemporaryDirectoryOutsideTheEngineDataPath()
        {
            Assert.IsTrue(CoreAiStorageRoot.IsOverridden,
                "Every test run must redirect the storage root before the first test starts.");
            string root = Path.GetFullPath(CoreAiStorageRoot.PersistentDataRoot);
            Assert.IsTrue(CoreAiTestStorageRootRunCallback.IsOwnedRunRoot(root),
                "The active root must be the run callback's own temporary directory: " + root);
            Assert.IsFalse(IsUnder(root, Path.GetFullPath(CoreAiStorageRoot.EngineDefaultRoot)),
                "The test root must not sit inside the developer's persistent data path.");
            Assert.IsFalse(IsUnder(Path.GetFullPath(CoreAiStorageRoot.EngineDefaultRoot), root),
                "The developer's persistent data path must not sit inside the test root either.");
            Assert.LessOrEqual(root.Length, Path.GetFullPath(CoreAiStorageRoot.EngineDefaultRoot).Length,
                "The test root must not be longer than the engine default, or Windows path budgets in the " +
                "mod source store would reject paths in tests that production accepts.");
        }

        [Test]
        public void DefaultStores_BuiltByATest_LandUnderTheRedirectedRoot()
        {
            IGameLogger logger = GameLoggerUnscopedFallback.Instance;
            using (CoreAiStorageRoot.Override(_ownedRoot))
            {
                string coreAi = Path.Combine(_ownedRoot, CoreAiPersistentPaths.RootFolderName);

                using (WorldStateManager world = new(logger))
                {
                    AssertUnder(coreAi, world.SaveFilePath, "WorldStateManager");
                }

                using (AuditLogWriter audit = new())
                {
                    AssertUnder(coreAi, audit.FilePath, "AuditLogWriter");
                }

                FileTokenCalibrationStore calibration = new();
                calibration.SaveScale("storage-root-probe", 1.25);
                AssertUnder(coreAi, calibration.StoragePath, "FileTokenCalibrationStore");
                Assert.IsTrue(File.Exists(calibration.StoragePath), "A token calibration write must land at its path.");

                FileLlmEndpointRegistryStore endpoints = new();
                endpoints.Save(new LlmEndpointRegistryState());
                AssertUnder(coreAi, endpoints.StoragePath, "FileLlmEndpointRegistryStore");
                Assert.IsTrue(File.Exists(endpoints.StoragePath), "An endpoint registry write must land at its path.");

                FileLuaScriptVersionStore scripts = new(logger);
                AssertUnder(coreAi, scripts.StoragePath, "FileLuaScriptVersionStore");

                FileDataOverlayVersionStore overlays = new(logger);
                AssertUnder(coreAi, overlays.StoragePath, "FileDataOverlayVersionStore");

                using (FileSkillStore skills = new())
                {
                    AssertUnder(coreAi, skills.StoragePath, "FileSkillStore");
                }

                using (FileAgentMemoryStore memory = new())
                {
                    AssertUnder(coreAi, memory.StoragePath, "FileAgentMemoryStore");
                }
            }

            Assert.IsTrue(Directory.Exists(Path.Combine(_ownedRoot, CoreAiPersistentPaths.RootFolderName)),
                "The probe writes must have created the CoreAI folder under the redirected root.");
        }

        [Test]
        public void DefaultWorldSnapshotPath_FollowsTheRedirectedRoot()
        {
            using (CoreAiStorageRoot.Override(_ownedRoot))
            {
                Assert.AreEqual(
                    Path.GetFullPath(Path.Combine(_ownedRoot, CoreAiPersistentPaths.RootFolderName,
                        CoreAiPersistentPaths.WorldState, "world_state.json")),
                    Path.GetFullPath(WorldStateManager.DefaultSaveFilePath()));
            }
        }

        [Test]
        public void Override_NestsAndRestoresScopesInAnyDisposalOrder()
        {
            string outer = CoreAiStorageRoot.PersistentDataRoot;
            string first = Path.Combine(_ownedRoot, "first");
            string second = Path.Combine(_ownedRoot, "second");

            IDisposable firstScope = CoreAiStorageRoot.Override(first);
            IDisposable secondScope = CoreAiStorageRoot.Override(second);
            Assert.AreEqual(Path.GetFullPath(second), CoreAiStorageRoot.PersistentDataRoot);

            firstScope.Dispose();
            Assert.AreEqual(Path.GetFullPath(second), CoreAiStorageRoot.PersistentDataRoot,
                "Disposing an outer scope first must not drop the inner one.");

            secondScope.Dispose();
            Assert.AreEqual(outer, CoreAiStorageRoot.PersistentDataRoot,
                "Disposing every scope this test opened must restore the run's root.");

            secondScope.Dispose();
            Assert.AreEqual(outer, CoreAiStorageRoot.PersistentDataRoot, "A second Dispose is a no-op.");
            Assert.IsFalse(Directory.Exists(first), "Override must not create the directory.");
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase("   ")]
        [TestCase("relative/root")]
        public void Override_RejectsEmptyAndRelativeRoots(string root)
        {
            string before = CoreAiStorageRoot.PersistentDataRoot;
            Assert.Throws<ArgumentException>(() => CoreAiStorageRoot.Override(root));
            Assert.AreEqual(before, CoreAiStorageRoot.PersistentDataRoot);
        }

        [Test]
        public void Override_RejectsTraversalSegments()
        {
            Assert.Throws<ArgumentException>(() =>
                CoreAiStorageRoot.Override(_ownedRoot + Path.DirectorySeparatorChar + ".." +
                                           Path.DirectorySeparatorChar + "escape"));
        }

        [Test]
        public void Combine_RejectsRootedTraversalAndEmptySegments()
        {
            Assert.Throws<ArgumentException>(() => CoreAiStorageRoot.Combine(Path.GetTempPath()));
            Assert.Throws<ArgumentException>(() => CoreAiStorageRoot.Combine("Skills", ".."));
            Assert.Throws<ArgumentException>(() => CoreAiStorageRoot.Combine("a/../b"));
            Assert.Throws<ArgumentException>(() => CoreAiStorageRoot.Combine(""));
            Assert.Throws<ArgumentNullException>(() => CoreAiStorageRoot.Combine(null));
            Assert.AreEqual(
                Path.Combine(CoreAiStorageRoot.CoreAiDirectory, "Skills", "x.json"),
                CoreAiStorageRoot.Combine("Skills", "x.json"));
        }

        /// <summary>Package roots scanned by the guard, relative to the project's Assets folder.</summary>
        private static readonly string[] GuardedRoots =
        {
            "CoreAI",
            "CoreAiUnity",
            "CoreAIMods",
            "CoreAIMcp",
            "CoreAIMirror",
            "CoreAIHub",
            "CoreAIBenchmark",
            "CoreAI.Demos",
            "_exampleGame",
        };

        /// <summary>
        /// Files allowed to read the engine data path, each with the reason. Everything else goes
        /// through <see cref="CoreAiStorageRoot"/>.
        /// </summary>
        private static readonly Dictionary<string, string> EngineDataPathReaders = new(StringComparer.Ordinal)
        {
            ["CoreAiUnity/Runtime/Source/Infrastructure/CoreAiStorageRoot.cs"] =
                "the provider itself: its default is the engine data path",
            ["CoreAiUnity/Tests/PlayMode/Shared/CoreAiTestStorageRootRunCallback.cs"] =
                "reads it only to refuse a temporary folder that would sit inside the developer's saves",
        };

        private static readonly Regex EngineDataPathIdentifier = new(@"\bpersistentDataPath\b");

        [Test]
        public void Sources_ReadTheEngineDataPathOnlyThroughTheStorageRoot()
        {
            List<string> violations = new();
            foreach (string file in GuardedSourceFiles())
            {
                string relative = ToRelative(file);
                if (EngineDataPathReaders.ContainsKey(relative))
                {
                    continue;
                }

                string[] lines = StripCommentsAndStrings(File.ReadAllText(file, Encoding.UTF8)).Split('\n');
                for (int index = 0; index < lines.Length; index++)
                {
                    if (EngineDataPathIdentifier.IsMatch(lines[index]))
                    {
                        violations.Add($"{relative}:{index + 1}: {lines[index].Trim()}");
                    }
                }
            }

            Assert.IsEmpty(violations,
                "Code outside CoreAiStorageRoot reads the engine persistent data path. Build store paths with " +
                "CoreAiStorageRoot.Combine so a host or the test run can redirect every store at once; a " +
                "direct read is how tests wrote into the developer's saves.\n" + string.Join("\n", violations));
        }

        [Test]
        public void EngineDataPathReaderAllowlist_NamesOnlyFilesThatStillExist()
        {
            List<string> stale = EngineDataPathReaders.Keys
                .Where(relative => !File.Exists(Path.Combine(Application.dataPath, relative)))
                .ToList();

            Assert.IsEmpty(stale, "Allowlist entries point at files that no longer exist: " + string.Join(", ", stale));
        }

        [Test]
        public void Guard_SeesReadsInCodeAndInterpolationButNotInCommentsOrStrings()
        {
            string source =
                "// Application.persistentDataPath in a line comment\n" +
                "/* Application.persistentDataPath in a block comment */\n" +
                "string a = \"Application.persistentDataPath in a string\";\n" +
                "string b = @\"verbatim \"\" persistentDataPath\";\n" +
                "char c = '\"';\n" +
                "string d = $\"{Application.persistentDataPath}/x\";\n" +
                "string e = Application.persistentDataPath;\n";

            string[] lines = StripCommentsAndStrings(source).Split('\n');
            int[] hits = Enumerable.Range(0, lines.Length)
                .Where(index => EngineDataPathIdentifier.IsMatch(lines[index]))
                .Select(index => index + 1)
                .ToArray();

            CollectionAssert.AreEqual(new[] { 6, 7 }, hits);
        }

        private static IEnumerable<string> GuardedSourceFiles()
        {
            foreach (string root in GuardedRoots)
            {
                string full = Path.Combine(Application.dataPath, root);
                if (!Directory.Exists(full))
                {
                    continue;
                }

                foreach (string file in Directory.EnumerateFiles(full, "*.cs", SearchOption.AllDirectories))
                {
                    yield return file;
                }
            }
        }

        private static string ToRelative(string file)
        {
            string assets = Path.GetFullPath(Application.dataPath).Replace('\\', '/').TrimEnd('/') + "/";
            return Path.GetFullPath(file).Replace('\\', '/').Substring(assets.Length);
        }

        /// <summary>
        /// Blanks comments and literal text while keeping line breaks and the code inside interpolation
        /// holes, so a read hidden in <c>$"{...}"</c> is still seen and a mention in prose is not.
        /// </summary>
        internal static string StripCommentsAndStrings(string source)
        {
            StringBuilder output = new(source.Length);
            Stack<bool> interpolationVerbatim = new();
            Stack<int> braceDepth = new();
            int index = 0;
            while (index < source.Length)
            {
                char current = source[index];
                char next = index + 1 < source.Length ? source[index + 1] : '\0';

                if (braceDepth.Count > 0 && current == '}' && braceDepth.Peek() == 0)
                {
                    braceDepth.Pop();
                    bool verbatim = interpolationVerbatim.Pop();
                    output.Append(' ');
                    index = SkipStringBody(source, index + 1, verbatim, true, output, interpolationVerbatim, braceDepth);
                    continue;
                }

                if (braceDepth.Count > 0 && (current == '{' || current == '}'))
                {
                    int depth = braceDepth.Pop();
                    braceDepth.Push(current == '{' ? depth + 1 : depth - 1);
                    output.Append(current);
                    index++;
                    continue;
                }

                if (current == '/' && next == '/')
                {
                    while (index < source.Length && source[index] != '\n')
                    {
                        index++;
                    }

                    continue;
                }

                if (current == '/' && next == '*')
                {
                    index += 2;
                    while (index < source.Length && !(source[index] == '*' && index + 1 < source.Length && source[index + 1] == '/'))
                    {
                        output.Append(source[index] == '\n' ? '\n' : ' ');
                        index++;
                    }

                    index += 2;
                    continue;
                }

                if (current == '\'')
                {
                    output.Append(' ');
                    index++;
                    while (index < source.Length && source[index] != '\'' && source[index] != '\n')
                    {
                        index += source[index] == '\\' ? 2 : 1;
                    }

                    index++;
                    continue;
                }

                bool interpolated = false;
                bool isVerbatim = false;
                int start = index;
                while (start < source.Length && (source[start] == '$' || source[start] == '@'))
                {
                    interpolated |= source[start] == '$';
                    isVerbatim |= source[start] == '@';
                    start++;
                }

                if (start < source.Length && source[start] == '"' && (start > index || current == '"'))
                {
                    output.Append(' ');
                    index = SkipStringBody(source, start + 1, isVerbatim, interpolated, output, interpolationVerbatim,
                        braceDepth);
                    continue;
                }

                output.Append(current);
                index++;
            }

            return output.ToString();
        }

        private static int SkipStringBody(string source, int index, bool verbatim, bool interpolated,
            StringBuilder output, Stack<bool> interpolationVerbatim, Stack<int> braceDepth)
        {
            while (index < source.Length)
            {
                char current = source[index];
                char next = index + 1 < source.Length ? source[index + 1] : '\0';
                if (!verbatim && current == '\\')
                {
                    index += 2;
                    continue;
                }

                if (current == '"')
                {
                    if (verbatim && next == '"')
                    {
                        index += 2;
                        continue;
                    }

                    return index + 1;
                }

                if (interpolated && current == '{')
                {
                    if (next == '{')
                    {
                        index += 2;
                        continue;
                    }

                    interpolationVerbatim.Push(verbatim);
                    braceDepth.Push(0);
                    output.Append(' ');
                    return index + 1;
                }

                if (current == '\n')
                {
                    output.Append('\n');
                    if (!verbatim)
                    {
                        return index + 1;
                    }
                }

                index++;
            }

            return index;
        }

        private static void AssertUnder(string root, string path, string store)
        {
            Assert.IsTrue(IsUnder(Path.GetFullPath(path), Path.GetFullPath(root)),
                $"{store} resolved '{path}', outside the redirected root '{root}'.");
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
