using System;
using System.Collections.Generic;
using System.IO;
using CoreAI.Editor;
using NUnit.Framework;
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
}
