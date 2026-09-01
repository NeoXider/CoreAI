using System;
using System.Collections.Generic;
using System.IO;
using CoreAI.Editor;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.Build;
using UnityEngine.TestTools;

namespace CoreAI.Tests.EditMode
{
    /// <summary>
    /// EditMode coverage for the WebGL StreamingAssets guard's restore bookkeeping. The guard moves the
    /// user's native LLM binaries into <c>Library/CoreAI/WebGlBuildBackup</c>, so the record of where they
    /// came from must survive an editor restart — SessionState alone does not.
    /// </summary>
    [TestFixture]
    public sealed class CoreAIWebGlStreamingAssetsGuardEditModeTests
    {
        private string _backupRoot;

        [SetUp]
        public void SetUp()
        {
            _backupRoot = Path.Combine(
                Path.GetTempPath(), "CoreAiWebGlGuardTests_" + Guid.NewGuid().ToString("N"));
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_backupRoot))
            {
                Directory.Delete(_backupRoot, true);
            }
        }

        [Test]
        public void Manifest_IsPersistedInsideTheBackupRoot()
        {
            CoreAIWebGlStreamingAssetsGuard.WriteManifestFile(_backupRoot, NewEntries());

            string manifestPath = CoreAIWebGlStreamingAssetsGuard.GetManifestPath(_backupRoot);

            Assert.AreEqual(_backupRoot, Path.GetDirectoryName(manifestPath),
                "The manifest must live next to the backed-up folders so it survives an editor restart.");
            FileAssert.Exists(manifestPath);
        }

        [Test]
        public void Manifest_RoundTrips_SourceAndBackupPaths()
        {
            CoreAIWebGlStreamingAssetsGuard.WriteManifestFile(_backupRoot, NewEntries());

            List<CoreAIWebGlStreamingAssetsGuard.MovedFolderEntry> read =
                CoreAIWebGlStreamingAssetsGuard.ReadManifestFile(_backupRoot);

            Assert.AreEqual(2, read.Count);
            Assert.AreEqual(Path.Combine("Assets", "StreamingAssets", "LlamaLib"), read[0].sourceAbsolutePath);
            Assert.AreEqual(Path.Combine(_backupRoot, "LlamaLib"), read[0].backupAbsolutePath);
            Assert.AreEqual(Path.Combine("Assets", "StreamingAssets", "LLMUnity"), read[1].sourceAbsolutePath);
        }

        [Test]
        public void Manifest_RewrittenAfterEachMove_KeepsOnlyTheLatestSet()
        {
            CoreAIWebGlStreamingAssetsGuard.WriteManifestFile(_backupRoot, NewEntries());
            CoreAIWebGlStreamingAssetsGuard.WriteManifestFile(
                _backupRoot,
                new List<CoreAIWebGlStreamingAssetsGuard.MovedFolderEntry>());

            Assert.AreEqual(0, CoreAIWebGlStreamingAssetsGuard.ReadManifestFile(_backupRoot).Count);
        }

        [Test]
        public void ReadManifestFile_WhenMissing_ReturnsEmptyInsteadOfThrowing()
        {
            Assert.AreEqual(0, CoreAIWebGlStreamingAssetsGuard.ReadManifestFile(_backupRoot).Count);
        }

        [Test]
        public void ParseManifest_MalformedPayload_ReturnsEmptyList()
        {
            LogAssert.ignoreFailingMessages = true;
            try
            {
                Assert.AreEqual(0, CoreAIWebGlStreamingAssetsGuard.ParseManifest("not json at all").Count);
                Assert.AreEqual(0, CoreAIWebGlStreamingAssetsGuard.ParseManifest("{}").Count);
                Assert.AreEqual(0, CoreAIWebGlStreamingAssetsGuard.ParseManifest("").Count);
            }
            finally
            {
                LogAssert.ignoreFailingMessages = false;
            }
        }

        [TestCase("LlamaLib", true)]
        [TestCase("llamalib-win-cuda", true)]
        [TestCase("LLMUnity", true)]
        [TestCase("LLMUnityBuild", true)]
        [TestCase("MyGameData", false)]
        [TestCase("", false)]
        [TestCase(null, false)]
        public void ShouldGuardFolder_MatchesOnlyLlmFolders(string folderName, bool expected)
        {
            Assert.AreEqual(expected, CoreAIWebGlStreamingAssetsGuard.ShouldGuardFolder(folderName));
        }

        private List<CoreAIWebGlStreamingAssetsGuard.MovedFolderEntry> NewEntries()
        {
            return new List<CoreAIWebGlStreamingAssetsGuard.MovedFolderEntry>
            {
                new()
                {
                    sourceAbsolutePath = Path.Combine("Assets", "StreamingAssets", "LlamaLib"),
                    backupAbsolutePath = Path.Combine(_backupRoot, "LlamaLib")
                },
                new()
                {
                    sourceAbsolutePath = Path.Combine("Assets", "StreamingAssets", "LLMUnity"),
                    backupAbsolutePath = Path.Combine(_backupRoot, "LLMUnity")
                }
            };
        }
    }

    /// <summary>
    /// EditMode coverage for the deterministic G11 WebGL build request and its guarded output cleanup.
    /// </summary>
    [TestFixture]
    public sealed class CoreAIG11WebGlBuildEditModeTests
    {
        private string _projectRoot;

        [SetUp]
        public void SetUp()
        {
            _projectRoot = Path.Combine(
                Path.GetTempPath(), "CoreAiG11WebGlBuildTests_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_projectRoot);
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_projectRoot))
            {
                Directory.Delete(_projectRoot, true);
            }
        }

        [Test]
        public void FrozenScenes_HaveTheRequiredOrderAndCannotBeMutatedByCaller()
        {
            string[] expected =
            {
                "Assets/CoreAI.Demos/FullAccess/FullAccessDemo.unity",
                "Assets/CoreAI.Demos/Hub/CoreAiHubDemo.unity",
                "Assets/CoreAiUnity/Scenes/CoreAiChatDemo.unity"
            };
            string[] firstRead = CoreAIG11WebGlBuild.GetFrozenScenePaths();

            CollectionAssert.AreEqual(expected, firstRead);
            firstRead[0] = "mutated";
            CollectionAssert.AreEqual(expected, CoreAIG11WebGlBuild.GetFrozenScenePaths());
        }

        [Test]
        public void BuildOptions_AreExplicitReleaseWebGlSettings()
        {
            string outputPath = CoreAIG11WebGlBuild.GetOutputPath(_projectRoot);
            BuildPlayerOptions options = CoreAIG11WebGlBuild.CreateBuildPlayerOptions(outputPath);

            Assert.AreEqual(outputPath, options.locationPathName);
            Assert.AreEqual(BuildTarget.WebGL, options.target);
            Assert.AreEqual(BuildTargetGroup.WebGL, options.targetGroup);
            Assert.AreEqual(BuildOptions.CleanBuildCache | BuildOptions.StrictMode, options.options);
            CollectionAssert.AreEqual(CoreAIG11WebGlBuild.GetFrozenScenePaths(), options.scenes);
        }

        [Test]
        public void PrepareOutputDirectory_RemovesStaleFilesAndRejectsOtherPaths()
        {
            string outputPath = CoreAIG11WebGlBuild.GetOutputPath(_projectRoot);
            Directory.CreateDirectory(outputPath);
            string stalePath = Path.Combine(outputPath, "stale.txt");
            File.WriteAllText(stalePath, "stale");

            CoreAIG11WebGlBuild.PrepareOutputDirectory(_projectRoot, outputPath);

            DirectoryAssert.Exists(outputPath);
            FileAssert.DoesNotExist(stalePath);
            Assert.Throws<BuildFailedException>(() => CoreAIG11WebGlBuild.PrepareOutputDirectory(
                _projectRoot,
                Path.Combine(_projectRoot, "outside")));
        }
    }
}
