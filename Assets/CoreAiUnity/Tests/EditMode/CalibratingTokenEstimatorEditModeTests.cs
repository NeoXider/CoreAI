using System;
using System.IO;
using CoreAI.Ai;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode
{
    /// <summary>
    /// Covers the real BPE token counter (R3): encoding resolution, byte-level BPE merge logic with a
    /// synthetic ranks table, and the automatic heuristic-estimator fallback when the model is unknown or
    /// tokenizer data is unavailable.
    /// </summary>
    public sealed class BpeTokenCounterEditModeTests
    {
        private sealed class StubRanks : IBpeRanksProvider
        {
            private readonly string _data;

            public StubRanks(string data)
            {
                _data = data;
            }

            public Stream OpenRanks(BpeEncoding encoding)
            {
                return new MemoryStream(System.Text.Encoding.UTF8.GetBytes(_data));
            }
        }

        [TestCase("gpt-4o", BpeEncoding.O200kBase)]
        [TestCase("gpt-4o-mini", BpeEncoding.O200kBase)]
        [TestCase("o3-mini", BpeEncoding.O200kBase)]
        [TestCase("gpt-4", BpeEncoding.Cl100kBase)]
        [TestCase("gpt-3.5-turbo", BpeEncoding.Cl100kBase)]
        [TestCase("text-embedding-3-small", BpeEncoding.Cl100kBase)]
        [TestCase("mistral-7b", BpeEncoding.Unknown)]
        [TestCase("", BpeEncoding.Unknown)]
        [TestCase(null, BpeEncoding.Unknown)]
        public void EncodingResolver_MapsModelFamilies(string model, BpeEncoding expected)
        {
            Assert.AreEqual(expected, BpeEncodingResolver.Resolve(model));
        }

        [Test]
        public void CountTokens_UnknownModel_UsesEstimatorFallback()
        {
            BpeTokenCounter counter = new(); // Null ranks provider, default estimator fallback.
            Assert.AreEqual(counter.Fallback.EstimateText("hello world"),
                counter.CountTokens("hello world", "llama-3"));
        }

        [Test]
        public void CountTokens_KnownModelButNoData_FallsBackToEstimator()
        {
            BpeTokenCounter counter = new(NullBpeRanksProvider.Instance);
            Assert.AreEqual(counter.Fallback.EstimateText("hello world"),
                counter.CountTokens("hello world", "gpt-4o"));
        }

        [Test]
        public void CountTokens_NullOrEmpty_ReturnsZero()
        {
            BpeTokenCounter counter = new();
            Assert.AreEqual(0, counter.CountTokens("", "gpt-4o"));
            Assert.AreEqual(0, counter.CountTokens(null, "gpt-4o"));
        }

        [Test]
        public void CountTokens_RealBpe_NoMerge_CountsSingleByteTokens()
        {
            string b64h = Convert.ToBase64String(new[] { (byte)'h' });
            string b64i = Convert.ToBase64String(new[] { (byte)'i' });
            string ranks = b64h + " 0\n" + b64i + " 1\n";
            BpeTokenCounter counter = new(new StubRanks(ranks));

            // "hi" -> bytes h,i with no merge rank -> 2 single-byte tokens (real BPE, not the estimator).
            Assert.AreEqual(2, counter.CountTokens("hi", "gpt-4"));
        }

        [Test]
        public void CountTokens_RealBpe_WithMerge_MergesPair()
        {
            string b64h = Convert.ToBase64String(new[] { (byte)'h' });
            string b64i = Convert.ToBase64String(new[] { (byte)'i' });
            string b64hi = Convert.ToBase64String(new[] { (byte)'h', (byte)'i' });
            string ranks = b64h + " 0\n" + b64i + " 1\n" + b64hi + " 2\n";
            BpeTokenCounter counter = new(new StubRanks(ranks));

            // "hi" merges h+i into the rank-2 piece -> 1 token.
            Assert.AreEqual(1, counter.CountTokens("hi", "gpt-4"));
        }
    }

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
            string text = new('a', 100);
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