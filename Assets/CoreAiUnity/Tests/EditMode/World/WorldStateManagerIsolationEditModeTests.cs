using System;
using System.IO;
using CoreAI.Infrastructure;
using CoreAI.Infrastructure.Logging;
using CoreAI.Infrastructure.World;
using NUnit.Framework;
using UnityEngine;

namespace CoreAI.Tests.EditMode
{
    public sealed class WorldStateManagerIsolationEditModeTests
    {
        [Test]
        public void DefaultConstructor_PreservesPublishedSnapshotLocationWithoutReadingIt()
        {
            using WorldStateManager manager = new(GameLoggerUnscopedFallback.Instance);
            Assert.AreEqual(Path.GetFullPath(Path.Combine(CoreAiStorageRoot.PersistentDataRoot,
                CoreAiPersistentPaths.RootFolderName, CoreAiPersistentPaths.WorldState, "world_state.json")),
                Path.GetFullPath(manager.SaveFilePath));
            Assert.AreEqual(Path.GetFullPath(manager.SaveFilePath),
                Path.GetFullPath(WorldStateManager.DefaultSaveFilePath()));
        }

        [Test]
        public void ExplicitPath_InitializesLoadsSavesAndResetsOnlyItsOwnedSnapshot()
        {
            Assert.IsEmpty(UnityEngine.Object.FindObjectsByType<WorldObjectComponent>(
                FindObjectsInactive.Include, FindObjectsSortMode.None), "This test requires a clean test scene.");
            string root = Path.Combine(Path.GetTempPath(), "CoreAI-world-isolation-" + Guid.NewGuid().ToString("N"));
            string path = Path.Combine(root, "world_state.json");
            Directory.CreateDirectory(root);
            try
            {
                File.WriteAllText(path, "{\"version\":\"1.1\",\"objects\":[{\"id\":\"owned-unresolved\",\"prefabKey\":\"\",\"name\":\"retained\"}]}");
                using WorldStateManager manager = new(GameLoggerUnscopedFallback.Instance, null, true, path);
                manager.WebGlFlushSync = () => true;
                Assert.AreEqual(Path.GetFullPath(path), manager.SaveFilePath);
                Assert.IsTrue(manager.HasSavedState);
                manager.Initialize();
                Assert.IsTrue(manager.WorldRestoreCompleted);
                Assert.IsTrue(manager.SaveWithResult().LocalOperationSucceeded);
                StringAssert.Contains("owned-unresolved", File.ReadAllText(path),
                    "Startup must load the owned snapshot and retain its unresolved entries on save.");
                File.WriteAllText(path + ".tmp", "owned temporary file");
                manager.Reset();
                Assert.IsFalse(File.Exists(path));
                Assert.IsFalse(File.Exists(path + ".tmp"));
            }
            finally
            {
                Directory.Delete(root, true);
            }
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase(" ")]
        [TestCase("world_state.json")]
        public void ExplicitPath_RejectsMissingOrRelativePath(string path)
        {
            Assert.Throws<ArgumentException>(() =>
                new WorldStateManager(GameLoggerUnscopedFallback.Instance, null, true, path));
        }

        [Test]
        public void ExplicitPath_RejectsTraversalAndDirectoryPaths()
        {
            string root = Path.Combine(Path.GetTempPath(), "CoreAI-world-isolation-" + Guid.NewGuid().ToString("N"));
            Assert.Throws<ArgumentException>(() => new WorldStateManager(GameLoggerUnscopedFallback.Instance,
                null, true, Path.Combine(root, "..", "world_state.json")));
            Assert.Throws<ArgumentException>(() => new WorldStateManager(GameLoggerUnscopedFallback.Instance,
                null, true, root + Path.DirectorySeparatorChar));
        }
    }
}
