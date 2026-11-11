using System;
using System.Collections.Generic;
using System.IO;
using CoreAI.Infrastructure;
using CoreAI.Infrastructure.Logging;
using CoreAI.Look;
using NUnit.Framework;
using Object = UnityEngine.Object;

namespace CoreAI.Tests.EditMode
{
    /// <summary>
    /// The graphics options file: round trip across store instances (a restart), defaults for a missing
    /// file, defaults plus exactly one warning for a corrupt one, and a reported failure when the write
    /// will not survive a WebGL reload.
    /// </summary>
    public sealed class FileGraphicsOptionsStoreEditModeTests
    {
        private string _directory;

        [SetUp]
        public void SetUp()
        {
            _directory = Path.Combine(Path.GetTempPath(),
                "cai-graphics-" + Guid.NewGuid().ToString("N").Substring(0, 10));
            Directory.CreateDirectory(_directory);
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_directory))
            {
                Directory.Delete(_directory, true);
            }
        }

        [Test]
        public void MissingFile_LoadsDefaults_WithoutAWarning()
        {
            CapturingLogger logger = new();
            FileGraphicsOptionsStore store = new(FilePath(), logger, () => true);

            GraphicsOptions loaded = store.Load();

            Assert.AreEqual(GraphicsOptions.Default, loaded);
            Assert.IsEmpty(logger.Warnings);
            Assert.IsEmpty(logger.Errors);
        }

        [Test]
        public void SavedOptions_SurviveANewStoreInstance()
        {
            GraphicsOptions options = new(GraphicsQualityPreset.Low, postProcessing: true,
                antiAliasing: GraphicsAntiAliasing.Fxaa, renderScale: 0.9f);
            FileGraphicsOptionsStore writer = new(FilePath(), new CapturingLogger(), () => true);

            bool saved = writer.Save(options);
            FileGraphicsOptionsStore reader = new(FilePath(), new CapturingLogger(), () => true);
            GraphicsOptionsService restarted = new(reader, GraphicsQualityPreset.High);

            Assert.IsTrue(saved);
            Assert.AreEqual(options, restarted.Options);
            Assert.AreEqual(GraphicsQualityPreset.Low, restarted.Effective.Preset);
            Assert.IsTrue(restarted.Effective.PostProcessing);
            Assert.IsEmpty(Directory.GetFiles(_directory, "*.tmp"), "the temp file is cleaned up");
        }

        [Test]
        public void SecondSave_ReplacesTheFirst()
        {
            FileGraphicsOptionsStore store = new(FilePath(), new CapturingLogger(), () => true);

            store.Save(GraphicsOptions.ForPreset(GraphicsQualityPreset.Low));
            store.Save(GraphicsOptions.ForPreset(GraphicsQualityPreset.High));

            Assert.AreEqual(GraphicsOptions.ForPreset(GraphicsQualityPreset.High), store.Load());
        }

        [Test]
        public void CorruptFile_LoadsDefaults_WithExactlyOneWarning()
        {
            File.WriteAllText(FilePath(), "{ this is not json");
            CapturingLogger logger = new();
            FileGraphicsOptionsStore store = new(FilePath(), logger, () => true);

            GraphicsOptions first = store.Load();
            GraphicsOptions second = store.Load();

            Assert.AreEqual(GraphicsOptions.Default, first);
            Assert.AreEqual(GraphicsOptions.Default, second);
            Assert.AreEqual(1, logger.Warnings.Count, string.Join("\n", logger.Warnings));
            StringAssert.Contains(FileGraphicsOptionsStore.FileName, logger.Warnings[0]);
            Assert.IsEmpty(logger.Errors);
        }

        [Test]
        public void CorruptFile_IsReplacedByTheNextSave()
        {
            File.WriteAllText(FilePath(), "[]");
            FileGraphicsOptionsStore store = new(FilePath(), new CapturingLogger(), () => true);
            store.Load();

            store.Save(GraphicsOptions.Default.WithFog(false));

            Assert.AreEqual(GraphicsOptions.Default.WithFog(false),
                new FileGraphicsOptionsStore(FilePath(), new CapturingLogger(), () => true).Load());
        }

        [Test]
        public void WriteThatWillNotSurviveAReload_IsReportedAsAFailure()
        {
            CapturingLogger logger = new();
            FileGraphicsOptionsStore store = new(FilePath(), logger, () => false);

            bool saved = store.Save(GraphicsOptions.ForPreset(GraphicsQualityPreset.Medium));

            Assert.IsFalse(saved);
            Assert.AreEqual(1, logger.Errors.Count);
            StringAssert.Contains("will not survive", logger.Errors[0]);
        }

        [Test]
        public void DefaultPath_LivesUnderTheStorageRoot()
        {
            using (CoreAiStorageRoot.Override(_directory))
            {
                FileGraphicsOptionsStore store = new(logger: new CapturingLogger(), durabilityCheck: () => true);

                StringAssert.StartsWith(Path.GetFullPath(_directory), store.FilePath);
                StringAssert.EndsWith(FileGraphicsOptionsStore.FileName, store.FilePath);
            }
        }

        private string FilePath()
        {
            return Path.Combine(_directory, FileGraphicsOptionsStore.FileName);
        }

        private sealed class CapturingLogger : IGameLogger
        {
            public List<string> Warnings { get; } = new();

            public List<string> Errors { get; } = new();

            public void LogDebug(GameLogFeature feature, string message, Object context = null)
            {
            }

            public void LogInfo(GameLogFeature feature, string message, Object context = null)
            {
            }

            public void LogWarning(GameLogFeature feature, string message, Object context = null)
            {
                Warnings.Add(message);
            }

            public void LogError(GameLogFeature feature, string message, Object context = null)
            {
                Errors.Add(message);
            }
        }
    }
}
