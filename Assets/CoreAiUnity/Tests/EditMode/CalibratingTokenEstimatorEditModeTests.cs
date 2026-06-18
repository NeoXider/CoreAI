using System;
using CoreAI.Ai;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode
{
    public sealed class CalibratingTokenEstimatorEditModeTests
    {
        private sealed class MemoryCalibrationStore : ITokenCalibrationStore
        {
            public readonly System.Collections.Generic.Dictionary<string, double> Values = new();

            public bool TryLoadScale(string modelKey, out double scale)
            {
                return Values.TryGetValue(modelKey, out scale);
            }

            public void SaveScale(string modelKey, double scale)
            {
                Values[modelKey] = scale;
            }
        }

        [Test]
        public void EstimateText_AllLatin_EqualsLegacyCharsDivFour()
        {
            CalibratingTokenEstimator estimator = new(new CoreAISettingsOptions());
            string text = "The quick brown fox jumps over the lazy dog. 12345!?";

            Assert.AreEqual(LegacyEstimate(text), estimator.EstimateText(text));
        }

        [Test]
        public void EstimateText_Cyrillic_EstimatesMoreThanLegacyCharsDivFour()
        {
            CalibratingTokenEstimator estimator = new(new CoreAISettingsOptions());
            string text = "\u041f\u0440\u0438\u0432\u0435\u0442 \u043c\u0438\u0440, " +
                          "\u043f\u0440\u043e\u0432\u0435\u0440\u043a\u0430 " +
                          "\u0442\u043e\u043a\u0435\u043d\u043e\u0432";

            Assert.Greater(estimator.EstimateText(text), LegacyEstimate(text));
        }

        [Test]
        public void RecordObservation_WhenEnabled_MovesScaleUpWithinClamp()
        {
            CalibratingTokenEstimator estimator = new(new CoreAISettingsOptions());

            estimator.RecordObservation(100, 200);

            Assert.Greater(estimator.CurrentScale, 1.0d);
            Assert.LessOrEqual(estimator.CurrentScale, 2.0d);
        }

        [Test]
        public void RecordObservation_WhenDisabled_LeavesScaleAtOne()
        {
            CalibratingTokenEstimator estimator = new(new CoreAISettingsOptions
            {
                EnableTokenCalibration = false
            });

            estimator.RecordObservation(100, 200);

            Assert.AreEqual(1.0d, estimator.CurrentScale, 0.0001d);
        }

        [Test]
        public void EstimateText_AllLatinAfterObservation_ReflectsScale()
        {
            CalibratingTokenEstimator estimator = new(new CoreAISettingsOptions());
            string text = new string('a', 100);
            int legacy = LegacyEstimate(text);

            estimator.RecordObservation(100, 200);

            Assert.Greater(estimator.EstimateText(text), legacy);
            Assert.AreEqual(
                (int)Math.Ceiling(legacy * estimator.CurrentScale),
                estimator.EstimateText(text));
        }

        [Test]
        public void Constructor_WithPersistedScale_LoadsModelSpecificCalibration()
        {
            MemoryCalibrationStore store = new();
            CoreAISettingsOptions settings = new()
            {
                TokenCalibrationModelKey = "qwen-test"
            };

            CalibratingTokenEstimator first = new(settings, store);
            first.RecordObservation(100, 200);
            double saved = store.Values["qwen-test"];

            CalibratingTokenEstimator second = new(settings, store);

            Assert.AreEqual(saved, second.CurrentScale, 0.0001d);
        }

        private static int LegacyEstimate(string text)
        {
            return string.IsNullOrEmpty(text) ? 0 : Math.Max(1, (text.Length + 3) / 4);
        }
    }
}
