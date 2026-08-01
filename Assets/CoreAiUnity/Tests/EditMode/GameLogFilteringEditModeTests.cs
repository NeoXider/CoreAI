using System;
using System.Collections.Generic;
using CoreAI.Infrastructure.Logging;
using NUnit.Framework;
using UnityEngine;

namespace CoreAI.Tests.EditMode
{
    /// <summary>
    /// EditMode coverage proving that filtered entries never reach the sink, that the unconfigured
    /// defaults stay usable, and that <see cref="GameLogFilter"/> retunes logging while the game runs.
    /// </summary>
    [TestFixture]
    public sealed class GameLogFilteringEditModeTests
    {
        private GameLogSettingsOptions _savedFilter;

        [SetUp]
        public void SetUp()
        {
            _savedFilter = GameLogFilter.Snapshot();
        }

        [TearDown]
        public void TearDown()
        {
            GameLogFilter.UseAuthoredSettings(_savedFilter);
        }

        #region Feature Mask Arithmetic

        [Test]
        public void AllBuiltIn_ContainsMetrics()
        {
            Assert.AreEqual(63, (int)GameLogFeature.AllBuiltIn,
                "Core|Composition|MessagePipe|ExampleRoguelite|Llm|Metrics = 1+2+4+8+16+32");
            Assert.IsTrue((GameLogFeature.AllBuiltIn & GameLogFeature.Metrics) != 0);
        }

        [Test]
        public void All_ContainsEveryDeclaredCategory()
        {
            Assert.AreEqual(831, (int)GameLogFeature.All, "AllBuiltIn(63) | CustomA(256) | CustomB(512)");

            foreach (GameLogFeature feature in Enum.GetValues(typeof(GameLogFeature)))
            {
                Assert.AreEqual(feature, GameLogFeature.All & feature,
                    $"{feature} is not covered by GameLogFeature.All");
            }
        }

        [Test]
        public void NewAsset_DefaultsToEveryCategory()
        {
            GameLogSettingsAsset asset = ScriptableObject.CreateInstance<GameLogSettingsAsset>();
            try
            {
                Assert.AreEqual(GameLogFeature.All, asset.ToOptions().EnabledFeatures);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(asset);
            }
        }

        #endregion

        #region Filtered Entries Never Reach The Sink

        [Test]
        public void Info_BelowMinimumLevel_NeverReachesSink()
        {
            FakeGameLogSink sink = new();
            FilteringGameLogger logger = new(sink, Settings(GameLogFeature.All, GameLogLevel.Warning));

            logger.LogInfo(GameLogFeature.Core, "info");
            logger.LogDebug(GameLogFeature.Core, "debug");

            Assert.IsEmpty(sink.Entries);
        }

        [Test]
        public void Warning_AtMinimumLevel_ReachesSink()
        {
            FakeGameLogSink sink = new();
            FilteringGameLogger logger = new(sink, Settings(GameLogFeature.All, GameLogLevel.Warning));

            logger.LogWarning(GameLogFeature.Core, "warning");

            Assert.AreEqual(1, sink.Entries.Count);
            Assert.AreEqual(GameLogLevel.Warning, sink.Entries[0].Level);
            StringAssert.Contains("warning", sink.Entries[0].Message);
        }

        [Test]
        public void Error_OnDisabledCategory_NeverReachesSink()
        {
            FakeGameLogSink sink = new();
            FilteringGameLogger logger = new(sink, Settings(GameLogFeature.Core, GameLogLevel.Debug));

            logger.LogError(GameLogFeature.Llm, "llm error");

            Assert.IsEmpty(sink.Entries, "An error on a disabled category must be dropped before the sink");
        }

        [Test]
        public void LevelAndCategory_Combined_OnlyMatchingEntryReachesSink()
        {
            FakeGameLogSink sink = new();
            FilteringGameLogger logger = new(sink, Settings(GameLogFeature.Llm, GameLogLevel.Warning));

            logger.LogInfo(GameLogFeature.Llm, "right category, level too low");
            logger.LogWarning(GameLogFeature.Core, "right level, wrong category");
            logger.LogWarning(GameLogFeature.Llm, "kept");

            Assert.AreEqual(1, sink.Entries.Count);
            StringAssert.Contains("kept", sink.Entries[0].Message);
        }

        [Test]
        public void NoneCategory_NeverReachesSink()
        {
            FakeGameLogSink sink = new();
            FilteringGameLogger logger = new(sink, Settings(GameLogFeature.All, GameLogLevel.Debug));

            logger.LogError(GameLogFeature.None, "unattributed");

            Assert.IsEmpty(sink.Entries);
        }

        [Test]
        public void Metrics_WithAllCategoriesEnabled_ReachesSink()
        {
            FakeGameLogSink sink = new();
            FilteringGameLogger logger = new(sink, Settings(GameLogFeature.All, GameLogLevel.Debug));

            logger.LogInfo(GameLogFeature.Metrics, "metrics entry");

            Assert.AreEqual(1, sink.Entries.Count, "GameLogFeature.All must not silently mute Metrics");
        }

        #endregion

        #region Defaults Without An Assigned Asset

        [Test]
        public void DefaultSettings_KeepInfo_AndRespectCategories()
        {
            DefaultGameLogSettings defaults = new();

            Assert.IsTrue(defaults.ShouldLog(GameLogFeature.Core, GameLogLevel.Info));
            Assert.IsTrue(defaults.ShouldLog(GameLogFeature.Metrics, GameLogLevel.Info));
            Assert.IsFalse(defaults.ShouldLog(GameLogFeature.Core, GameLogLevel.Debug));
            Assert.IsFalse(defaults.ShouldLog(GameLogFeature.None, GameLogLevel.Error));
        }

        [Test]
        public void DefaultSettings_DeliverInfoToSink()
        {
            FakeGameLogSink sink = new();
            FilteringGameLogger logger = new(sink, new DefaultGameLogSettings());

            logger.LogInfo(GameLogFeature.Llm, "default info");
            logger.LogDebug(GameLogFeature.Llm, "default debug");

            Assert.AreEqual(1, sink.Entries.Count);
            Assert.AreEqual(GameLogLevel.Info, sink.Entries[0].Level);
        }

        #endregion

        #region Runtime Filter API

        [Test]
        public void GameLogFilter_MinimumLevel_RetunesALiveLogger()
        {
            FakeGameLogSink sink = new();
            FilteringGameLogger logger = new(sink, GameLogFilter.Settings);
            GameLogFilter.UseAuthoredSettings(new GameLogSettingsOptions
            {
                EnabledFeatures = GameLogFeature.All,
                MinimumLevel = GameLogLevel.Debug
            });

            logger.LogDebug(GameLogFeature.Core, "before");
            GameLogFilter.MinimumLevel = GameLogLevel.Error;
            logger.LogDebug(GameLogFeature.Core, "after");

            Assert.AreEqual(1, sink.Entries.Count);
            StringAssert.Contains("before", sink.Entries[0].Message);
        }

        [Test]
        public void GameLogFilter_SetFeatureEnabled_TogglesOneCategoryOnly()
        {
            FakeGameLogSink sink = new();
            FilteringGameLogger logger = new(sink, GameLogFilter.Settings);
            GameLogFilter.UseAuthoredSettings(new GameLogSettingsOptions
            {
                EnabledFeatures = GameLogFeature.All,
                MinimumLevel = GameLogLevel.Debug
            });

            GameLogFilter.SetFeatureEnabled(GameLogFeature.Llm, false);

            Assert.IsFalse(GameLogFilter.IsFeatureEnabled(GameLogFeature.Llm));
            Assert.IsTrue(GameLogFilter.IsFeatureEnabled(GameLogFeature.Core));

            logger.LogError(GameLogFeature.Llm, "muted");
            logger.LogError(GameLogFeature.Core, "kept");

            Assert.AreEqual(1, sink.Entries.Count);
            StringAssert.Contains("kept", sink.Entries[0].Message);

            GameLogFilter.SetFeatureEnabled(GameLogFeature.Llm, true);
            logger.LogError(GameLogFeature.Llm, "unmuted");

            Assert.AreEqual(2, sink.Entries.Count);
        }

        [Test]
        public void GameLogFilter_EnabledFeatures_ReplacesTheWholeMask()
        {
            GameLogFilter.UseAuthoredSettings(new GameLogSettingsOptions
            {
                EnabledFeatures = GameLogFeature.All,
                MinimumLevel = GameLogLevel.Debug
            });

            GameLogFilter.EnabledFeatures = GameLogFeature.Llm | GameLogFeature.Metrics;

            Assert.AreEqual(GameLogFeature.Llm | GameLogFeature.Metrics, GameLogFilter.EnabledFeatures);
            Assert.IsFalse(GameLogFilter.Settings.ShouldLog(GameLogFeature.Core, GameLogLevel.Error));
            Assert.IsTrue(GameLogFilter.Settings.ShouldLog(GameLogFeature.Metrics, GameLogLevel.Debug));
        }

        [Test]
        public void GameLogFilter_ResetToAuthored_DiscardsRuntimeEdits()
        {
            GameLogFilter.UseAuthoredSettings(new GameLogSettingsOptions
            {
                EnabledFeatures = GameLogFeature.Core | GameLogFeature.Llm,
                MinimumLevel = GameLogLevel.Warning
            });

            GameLogFilter.EnabledFeatures = GameLogFeature.None;
            GameLogFilter.MinimumLevel = GameLogLevel.Error;
            GameLogFilter.ResetToAuthored();

            Assert.AreEqual(GameLogFeature.Core | GameLogFeature.Llm, GameLogFilter.EnabledFeatures);
            Assert.AreEqual(GameLogLevel.Warning, GameLogFilter.MinimumLevel);
        }

        [Test]
        public void GameLogFilter_AlsoDrivesTheUnscopedFallbackLogger()
        {
            string token = "coreai-filter-probe-" + Guid.NewGuid().ToString("N");
            List<string> captured = new();

            void Handler(string condition, string stackTrace, LogType type)
            {
                if (condition != null && condition.Contains(token))
                {
                    captured.Add(condition);
                }
            }

            Application.logMessageReceived += Handler;
            try
            {
                GameLogFilter.UseAuthoredSettings(new GameLogSettingsOptions
                {
                    EnabledFeatures = GameLogFeature.All,
                    MinimumLevel = GameLogLevel.Error
                });
                GameLoggerUnscopedFallback.Instance.LogInfo(GameLogFeature.Core, token + " muted");

                Assert.IsEmpty(captured, "The unscoped fallback logger must honour the runtime filter");

                GameLogFilter.MinimumLevel = GameLogLevel.Info;
                GameLoggerUnscopedFallback.Instance.LogInfo(GameLogFeature.Core, token + " audible");

                Assert.AreEqual(1, captured.Count);
            }
            finally
            {
                Application.logMessageReceived -= Handler;
            }
        }

        #endregion

        #region Asset Migration

        [Test]
        public void Migration_LegacyVersionAndLegacyPreset_WidensToAllBuiltIn()
        {
            const GameLogFeature legacyBeforeLlm = GameLogFeature.Core | GameLogFeature.Composition |
                                                   GameLogFeature.MessagePipe | GameLogFeature.ExampleRoguelite;
            const GameLogFeature legacyBeforeMetrics = legacyBeforeLlm | GameLogFeature.Llm;

            Assert.IsTrue(GameLogSettingsAsset.TryMigrateFeatures(0, legacyBeforeLlm, out GameLogFeature fromOldest));
            Assert.AreEqual(GameLogFeature.AllBuiltIn, fromOldest);

            Assert.IsTrue(
                GameLogSettingsAsset.TryMigrateFeatures(0, legacyBeforeMetrics, out GameLogFeature fromRecent));
            Assert.AreEqual(GameLogFeature.AllBuiltIn, fromRecent);
        }

        [Test]
        public void Migration_CurrentVersion_KeepsEverythingExceptLlmAndMetrics()
        {
            const GameLogFeature deliberate = GameLogFeature.Core | GameLogFeature.Composition |
                                              GameLogFeature.MessagePipe | GameLogFeature.ExampleRoguelite;

            Assert.IsFalse(GameLogSettingsAsset.TryMigrateFeatures(1, deliberate, out GameLogFeature unchanged),
                "A migrated asset must keep a deliberate selection that looks like an old preset");
            Assert.AreEqual(deliberate, unchanged);
        }

        [Test]
        public void Migration_LegacyVersionAndCustomMask_LeavesTheMaskAlone()
        {
            const GameLogFeature custom = GameLogFeature.Core | GameLogFeature.Metrics;

            Assert.IsFalse(GameLogSettingsAsset.TryMigrateFeatures(0, custom, out GameLogFeature unchanged));
            Assert.AreEqual(custom, unchanged);
        }

        [Test]
        public void Asset_RoundTripsAMaskWithoutLlmAndMetrics()
        {
            const GameLogFeature deliberate = GameLogFeature.Core | GameLogFeature.Composition |
                                              GameLogFeature.MessagePipe | GameLogFeature.ExampleRoguelite;
            GameLogSettingsAsset asset = ScriptableObject.CreateInstance<GameLogSettingsAsset>();
            try
            {
                asset.ApplyOptions(new GameLogSettingsOptions
                {
                    EnabledFeatures = deliberate,
                    MinimumLevel = GameLogLevel.Info
                });

                Assert.AreEqual(deliberate, asset.ToOptions().EnabledFeatures);
                Assert.IsFalse(asset.ShouldLog(GameLogFeature.Llm, GameLogLevel.Error));
                Assert.IsTrue(asset.ShouldLog(GameLogFeature.Core, GameLogLevel.Info));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(asset);
            }
        }

        #endregion

        #region Helpers

        private static RuntimeGameLogSettings Settings(GameLogFeature features, GameLogLevel minimumLevel)
        {
            return new RuntimeGameLogSettings(features, minimumLevel);
        }

        /// <summary>Sink that records everything the filter let through.</summary>
        private sealed class FakeGameLogSink : IGameLogSink
        {
            public readonly List<(GameLogLevel Level, string Message)> Entries = new();

            public void Write(GameLogLevel level, string message, UnityEngine.Object context = null)
            {
                Entries.Add((level, message));
            }
        }

        #endregion
    }
}
