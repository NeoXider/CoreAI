using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using CoreAI.Infrastructure.Logging;
using CoreAI.Infrastructure.World;
using Cysharp.Threading.Tasks;
using NUnit.Framework;
using UnityEngine;

namespace CoreAI.Tests.EditMode
{
    public sealed class WorldStateWriteResultTests
    {
        private string _directory;
        private string _path;
        private WorldStateManager _manager;
        private RecordingLogger _logger;

        [SetUp]
        public void SetUp()
        {
            _directory = Path.Combine(Path.GetTempPath(), "CoreAiWorldWrite_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_directory);
            _path = Path.Combine(_directory, "world.json");
            _logger = new RecordingLogger();
            _manager = new WorldStateManager(_logger)
            {
                WebGlFlushSync = () => true,
                WebGlFlushAsync = () => UniTask.FromResult(true)
            };
            typeof(WorldStateManager).GetField("_saveFilePath", BindingFlags.Instance | BindingFlags.NonPublic)
                .SetValue(_manager, _path);
        }

        [TearDown]
        public void TearDown()
        {
            _manager?.Dispose();
            if (File.Exists(_path))
            {
                File.SetAttributes(_path, FileAttributes.Normal);
            }

            if (Directory.Exists(_directory))
            {
                Directory.Delete(_directory, true);
            }
        }

        [Test]
        public void DefaultReceipt_IsUnsupportedAndUnconfirmed()
        {
            WorldStateWriteResult result = default;
            Assert.AreEqual(WorldStateWriteStatus.Unsupported, result.Status);
            Assert.IsFalse(result.IsConfirmed);
        }

        [Test]
        public void Save_WriteFailure_RemainsUnconfirmedAfterIndependentArmingCheck()
        {
            File.WriteAllText(_path, "previous snapshot");
            Directory.CreateDirectory(_path + ".tmp");

            WorldStateWriteResult failure = _manager.SaveWithResult();
            bool? armed = null;
            _manager.ConfirmDurability(value => armed = value);

            Assert.AreEqual(WorldStateWriteStatus.Failed, failure.Status);
            Assert.IsFalse(failure.LocalOperationSucceeded);
            Assert.IsFalse(failure.IsConfirmed);
            Assert.AreEqual(true, armed, "Arming is an independent observation, not a local write receipt.");
            Assert.AreEqual("previous snapshot", File.ReadAllText(_path));
            StringAssert.DoesNotContain(_directory, failure.Error);

            Directory.Delete(_path + ".tmp");
            WorldStateWriteResult success = _manager.SaveWithResult();
            Assert.IsTrue(success.IsConfirmed);
            Assert.IsFalse(failure.IsConfirmed, "A later operation must not change an earlier receipt.");
        }

        [Test]
        public void Save_LocalSuccessWithoutArming_IsNotConfirmed()
        {
            _manager.WebGlFlushSync = () => false;
            WorldStateWriteResult result = _manager.SaveWithResult();
            Assert.IsTrue(result.LocalOperationSucceeded);
            Assert.IsTrue(File.Exists(_path));
            Assert.IsFalse(result.PersistenceArmed);
            Assert.IsFalse(result.IsConfirmed);
        }

        [Test]
        public void Save_ReplaceFailure_PreservesPreviousSnapshot()
        {
            if (Application.platform != RuntimePlatform.WindowsEditor)
            {
                Assert.Ignore("This native file-sharing fault fixture requires Windows.");
            }

            File.WriteAllText(_path, "previous snapshot");
            using (FileStream lease = File.Open(_path, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                WorldStateWriteResult result = _manager.SaveWithResult();
                Assert.AreEqual(WorldStateWriteStatus.Failed, result.Status);
                Assert.IsFalse(result.IsConfirmed);
            }

            Assert.AreEqual("previous snapshot", File.ReadAllText(_path));
        }

        [Test]
        public void Reset_DeleteFailure_CannotBeConfirmedByArmedPersistence()
        {
            if (Application.platform != RuntimePlatform.WindowsEditor)
            {
                Assert.Ignore("This native read-only deletion fault fixture requires Windows.");
            }

            if (UnityEngine.Object.FindObjectsByType<WorldObjectComponent>(
                    FindObjectsInactive.Include, FindObjectsSortMode.None).Length != 0)
            {
                Assert.Ignore("Reset fixtures require an empty tracked world to preserve external scene objects.");
            }

            File.WriteAllText(_path, "previous snapshot");
            File.SetAttributes(_path, FileAttributes.ReadOnly);
            WorldStateWriteResult result = _manager.ResetWithResult();
            Assert.AreEqual(WorldStateWriteStatus.Failed, result.Status);
            Assert.IsTrue(result.PersistenceArmed);
            Assert.IsFalse(result.IsConfirmed);
            Assert.AreEqual("previous snapshot", File.ReadAllText(_path));
            StringAssert.DoesNotContain(_directory, result.Error);
            Assert.IsFalse(_logger.Info.Exists(message => message.Contains("snapshot deleted")),
                "A failed deletion must not emit the success log.");
        }

        [Test]
        public void Reset_ThrowingObserver_DoesNotHideLocalReceiptOrBlockOtherObservers()
        {
            if (UnityEngine.Object.FindObjectsByType<WorldObjectComponent>(
                    FindObjectsInactive.Include, FindObjectsSortMode.None).Length != 0)
            {
                Assert.Ignore("Reset fixtures require an empty tracked world to preserve external scene objects.");
            }

            File.WriteAllText(_path, "previous snapshot");
            int observed = 0;
            _manager.StateReset += () => throw new InvalidOperationException("Injected observer failure.");
            _manager.StateReset += () => observed++;

            WorldStateWriteResult result = _manager.ResetWithResult();
            Assert.IsTrue(result.IsConfirmed);
            Assert.AreEqual(1, observed);
            Assert.IsFalse(File.Exists(_path));
        }

        [Test]
        public void DisposedManager_ReportsUnsupportedInsteadOfSuccess()
        {
            _manager.Dispose();
            Assert.AreEqual(WorldStateWriteStatus.Unsupported, _manager.SaveWithResult().Status);
            Assert.AreEqual(WorldStateWriteStatus.Unsupported, _manager.ResetWithResult().Status);
        }

        private sealed class RecordingLogger : IGameLogger
        {
            public List<string> Info { get; } = new List<string>();
            public void LogDebug(GameLogFeature feature, string message, UnityEngine.Object context = null) { }
            public void LogInfo(GameLogFeature feature, string message, UnityEngine.Object context = null) { Info.Add(message); }
            public void LogWarning(GameLogFeature feature, string message, UnityEngine.Object context = null) { }
            public void LogError(GameLogFeature feature, string message, UnityEngine.Object context = null) { }
        }
    }
}
