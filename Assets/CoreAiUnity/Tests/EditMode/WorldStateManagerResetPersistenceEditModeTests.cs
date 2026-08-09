using System.IO;
using CoreAI.Infrastructure;
using CoreAI.Infrastructure.Logging;
using CoreAI.Infrastructure.World;
using NUnit.Framework;
using UnityEngine;

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
        private string _backupPath;

        [SetUp]
        public void SetUp()
        {
            _saveFilePath = Path.Combine(
                Application.persistentDataPath,
                CoreAiPersistentPaths.RootFolderName,
                CoreAiPersistentPaths.WorldState,
                "world_state.json");
            _backupPath = _saveFilePath + ".test_backup";

            // WHY: back up any real save so the test never clobbers a live world.
            if (File.Exists(_saveFilePath))
            {
                File.Copy(_saveFilePath, _backupPath, true);
                File.Delete(_saveFilePath);
            }
        }

        [TearDown]
        public void TearDown()
        {
            if (File.Exists(_saveFilePath))
            {
                File.Delete(_saveFilePath);
            }

            if (File.Exists(_saveFilePath + ".tmp"))
            {
                File.Delete(_saveFilePath + ".tmp");
            }

            if (File.Exists(_backupPath))
            {
                File.Copy(_backupPath, _saveFilePath, true);
                File.Delete(_backupPath);
            }
        }

        [Test]
        public void Reset_DeletesSaveFileAndTmpFile_AndFlushesPersistence()
        {
            int flushCount = 0;
            WorldStateManager manager = new(GameLoggerUnscopedFallback.Instance)
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
            WorldStateManager manager = new(GameLoggerUnscopedFallback.Instance)
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
