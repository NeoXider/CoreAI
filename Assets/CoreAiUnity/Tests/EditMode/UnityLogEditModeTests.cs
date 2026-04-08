using System.Collections.Generic;
using CoreAI.Infrastructure.Logging;
using CoreAI.Logging;
using CoreAI.Unity.Logging;
using NUnit.Framework;
using UnityEngine;

namespace CoreAI.Tests.EditMode
{
    /// <summary>
    /// EditMode тесты для UnityLog — мост ILog → IGameLogger с маппингом тегов.
    /// </summary>
    [TestFixture]
    public sealed class UnityLogEditModeTests
    {
        #region Log Level Tests

        [Test]
        public void Debug_DelegatesToGameLogger_LogDebug()
        {
            CapturingGameLogger logger = new();
            UnityLog unityLog = new(logger);

            unityLog.Debug("test message", LogTag.Core);

            Assert.AreEqual(1, logger.Messages.Count);
            Assert.AreEqual("Debug", logger.Messages[0].Level);
            Assert.AreEqual("test message", logger.Messages[0].Message);
            Assert.AreEqual(GameLogFeature.Core, logger.Messages[0].Feature);
        }

        [Test]
        public void Info_DelegatesToGameLogger_LogInfo()
        {
            CapturingGameLogger logger = new();
            UnityLog unityLog = new(logger);

            unityLog.Info("info message", LogTag.Llm);

            Assert.AreEqual(1, logger.Messages.Count);
            Assert.AreEqual("Info", logger.Messages[0].Level);
            Assert.AreEqual("info message", logger.Messages[0].Message);
            Assert.AreEqual(GameLogFeature.Llm, logger.Messages[0].Feature);
        }

        [Test]
        public void Warn_DelegatesToGameLogger_LogWarning()
        {
            CapturingGameLogger logger = new();
            UnityLog unityLog = new(logger);

            unityLog.Warn("warn message", LogTag.Metrics);

            Assert.AreEqual(1, logger.Messages.Count);
            Assert.AreEqual("Warning", logger.Messages[0].Level);
            Assert.AreEqual(GameLogFeature.Metrics, logger.Messages[0].Feature);
        }

        [Test]
        public void Error_DelegatesToGameLogger_LogError()
        {
            CapturingGameLogger logger = new();
            UnityLog unityLog = new(logger);

            unityLog.Error("error message", LogTag.Core);

            Assert.AreEqual(1, logger.Messages.Count);
            Assert.AreEqual("Error", logger.Messages[0].Level);
        }

        #endregion

        #region Tag Mapping Tests

        [Test]
        public void NullTag_MapsToCore()
        {
            CapturingGameLogger logger = new();
            UnityLog unityLog = new(logger);

            unityLog.Debug("msg", null);

            Assert.AreEqual(GameLogFeature.Core, logger.Messages[0].Feature);
        }

        [Test]
        public void EmptyTag_MapsToCore()
        {
            CapturingGameLogger logger = new();
            UnityLog unityLog = new(logger);

            unityLog.Debug("msg", "");

            Assert.AreEqual(GameLogFeature.Core, logger.Messages[0].Feature);
        }

        [Test]
        public void UnknownTag_MapsToCore()
        {
            CapturingGameLogger logger = new();
            UnityLog unityLog = new(logger);

            unityLog.Debug("msg", "SomeUnknownTag");

            Assert.AreEqual(GameLogFeature.Core, logger.Messages[0].Feature);
        }

        [Test]
        public void LlmTag_MapsToLlm()
        {
            CapturingGameLogger logger = new();
            UnityLog unityLog = new(logger);

            unityLog.Info("msg", LogTag.Llm);

            Assert.AreEqual(GameLogFeature.Llm, logger.Messages[0].Feature);
        }

        [Test]
        public void MemoryTag_MapsToLlm()
        {
            CapturingGameLogger logger = new();
            UnityLog unityLog = new(logger);

            unityLog.Info("msg", LogTag.Memory);

            Assert.AreEqual(GameLogFeature.Llm, logger.Messages[0].Feature);
        }

        [Test]
        public void CompositionTag_MapsToComposition()
        {
            CapturingGameLogger logger = new();
            UnityLog unityLog = new(logger);

            unityLog.Info("msg", LogTag.Composition);

            Assert.AreEqual(GameLogFeature.Composition, logger.Messages[0].Feature);
        }

        [Test]
        public void LuaTag_MapsToMessagePipe()
        {
            CapturingGameLogger logger = new();
            UnityLog unityLog = new(logger);

            unityLog.Info("msg", LogTag.Lua);

            Assert.AreEqual(GameLogFeature.MessagePipe, logger.Messages[0].Feature);
        }

        [Test]
        public void MessagePipeTag_MapsToMessagePipe()
        {
            CapturingGameLogger logger = new();
            UnityLog unityLog = new(logger);

            unityLog.Info("msg", LogTag.MessagePipe);

            Assert.AreEqual(GameLogFeature.MessagePipe, logger.Messages[0].Feature);
        }

        [Test]
        public void MetricsTag_MapsToMetrics()
        {
            CapturingGameLogger logger = new();
            UnityLog unityLog = new(logger);

            unityLog.Info("msg", LogTag.Metrics);

            Assert.AreEqual(GameLogFeature.Metrics, logger.Messages[0].Feature);
        }

        #endregion

        #region Test Helpers

        private struct LogEntry
        {
            public string Level;
            public GameLogFeature Feature;
            public string Message;
        }

        private sealed class CapturingGameLogger : IGameLogger
        {
            public readonly List<LogEntry> Messages = new();

            public void LogDebug(GameLogFeature feature, string message, Object context = null)
            {
                Messages.Add(new LogEntry { Level = "Debug", Feature = feature, Message = message });
            }

            public void LogInfo(GameLogFeature feature, string message, Object context = null)
            {
                Messages.Add(new LogEntry { Level = "Info", Feature = feature, Message = message });
            }

            public void LogWarning(GameLogFeature feature, string message, Object context = null)
            {
                Messages.Add(new LogEntry { Level = "Warning", Feature = feature, Message = message });
            }

            public void LogError(GameLogFeature feature, string message, Object context = null)
            {
                Messages.Add(new LogEntry { Level = "Error", Feature = feature, Message = message });
            }
        }

        #endregion
    }
}