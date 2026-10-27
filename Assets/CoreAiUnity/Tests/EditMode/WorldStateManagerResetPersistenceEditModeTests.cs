using System;
using System.IO;
using CoreAI.Infrastructure.Logging;
using CoreAI.Infrastructure.World;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode
{
    /// <summary>
    /// Regression coverage for the WebGL save-resurrection bug: <see cref="WorldStateManager.Reset"/>
    /// must delete both the save file and its <c>.tmp</c> sibling AND flush the IDBFS tree to
    /// IndexedDB afterwards, or the deleted save resurrects from IndexedDB on the next page reload.
    /// The flush is observed through the internal <c>WebGlFlushSync</c> seam.
    /// </summary>
    public sealed class WorldStateManagerResetPersistenceEditModeTests
    {
        private string _saveFilePath;
        private DirectoryInfo _ownedDirectory;

        [SetUp]
        public void SetUp()
        {
            _ownedDirectory = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(),
                "CoreAI-world-reset-" + Guid.NewGuid().ToString("N")));
            _saveFilePath = Path.Combine(_ownedDirectory.FullName, "world_state.json");
        }

        [TearDown]
        public void TearDown()
        {
            if (_ownedDirectory != null && _ownedDirectory.Exists)
            {
                _ownedDirectory.Delete(true);
            }
        }

        [Test]
        public void Reset_DeletesSaveFileAndTmpFile_AndFlushesPersistence()
        {
            int flushCount = 0;
            using WorldStateManager manager = new(GameLoggerUnscopedFallback.Instance, null, true, _saveFilePath)
            {
                WebGlFlushSync = () =>
                {
                    flushCount++;
                    return true;
                }
            };

            Directory.CreateDirectory(Path.GetDirectoryName(_saveFilePath));
            File.WriteAllText(_saveFilePath, "{}");
            File.WriteAllText(_saveFilePath + ".tmp", "{}");

            manager.Reset();

            Assert.IsFalse(File.Exists(_saveFilePath), "Reset must delete the save file.");
            Assert.IsFalse(File.Exists(_saveFilePath + ".tmp"), "Reset must delete the .tmp file.");
            Assert.AreEqual(1, flushCount,
                "Reset must flush persistence after the deletes, or a WebGL reload resurrects the save.");
        }

        [Test]
        public void Reset_WithoutSaveFile_StillFlushesPersistence()
        {
            int flushCount = 0;
            using WorldStateManager manager = new(GameLoggerUnscopedFallback.Instance, null, true, _saveFilePath)
            {
                WebGlFlushSync = () =>
                {
                    flushCount++;
                    return true;
                }
            };

            Assert.IsFalse(File.Exists(_saveFilePath));
            manager.Reset();

            Assert.AreEqual(1, flushCount,
                "Flush must not be conditioned on the save file existing: a WebGL reload would still " +
                "serve the IndexedDB copy of a file deleted in an earlier session.");
        }
    }
}
