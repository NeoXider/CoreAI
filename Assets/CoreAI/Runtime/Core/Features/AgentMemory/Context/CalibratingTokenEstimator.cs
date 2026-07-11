using System;

namespace CoreAI.Ai
{
    /// <summary>
    /// Script-aware pre-flight estimator with bounded dynamic calibration from observed prompt usage.
    /// </summary>
    public sealed class CalibratingTokenEstimator : ICalibratingTokenEstimator
    {
        private const double LatinWeight = 0.25d;
        private const double DenseScriptWeight = 0.4d;
        private const double Alpha = 0.1d;
        private const double MinScale = 0.5d;
        private const double MaxScale = 2.0d;

        private readonly object _lock = new();
        private readonly ICoreAISettings _settings;
        private readonly ITokenCalibrationStore _store;
        private readonly string _modelKey;
        private double _scale = 1.0d;

        public CalibratingTokenEstimator(
            ICoreAISettings settings = null,
            ITokenCalibrationStore store = null)
        {
            _settings = settings;
            _store = store ?? NullTokenCalibrationStore.Instance;
            _modelKey = NormalizeModelKey(settings?.TokenCalibrationModelKey);
            if (_store.TryLoadScale(_modelKey, out double persisted))
            {
                _scale = Clamp(persisted, MinScale, MaxScale);
            }
        }

        /// <inheritdoc />
        public double CurrentScale
        {
            get
            {
                if (!IsCalibrationEnabled)
                {
                    return 1.0d;
                }

                lock (_lock)
                {
                    return _scale;
                }
            }
        }

        /// <inheritdoc />
        public int EstimateText(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return 0;
            }

            double weightedChars = 0d;
            for (int i = 0; i < text.Length; i++)
            {
                weightedChars += GetCharacterWeight(text[i]);
            }

            return Math.Max(1, (int)Math.Ceiling(weightedChars * CurrentScale));
        }

        /// <inheritdoc />
        public void RecordObservation(int estimatedPromptTokens, int realPromptTokens)
        {
            if (!IsCalibrationEnabled || estimatedPromptTokens <= 0 || realPromptTokens <= 0)
            {
                return;
            }

            double updatedScale;
            lock (_lock)
            {
                // WHY: The observed estimate was produced as baseEstimate * _scale. Convert the
                // real/estimated ratio back into scale units so repeated observations converge
                // toward real/baseEstimate, not sqrt(real/baseEstimate).
                double targetScale = _scale * realPromptTokens / estimatedPromptTokens;
                _scale = Clamp(_scale * (1d - Alpha) + targetScale * Alpha, MinScale, MaxScale);
                updatedScale = _scale;
            }

            // WHY: Persist outside the lock so a blocking disk write in the store does not stall concurrent
            // EstimateText/CurrentScale calls (which also take _lock). The in-memory scale is already
            // updated; the store write does not need the estimator lock.
            _store.SaveScale(_modelKey, updatedScale);
        }

        private bool IsCalibrationEnabled => _settings?.EnableTokenCalibration ?? true;

        private static double GetCharacterWeight(char c)
        {
            return IsCyrillic(c) || IsCjk(c) ? DenseScriptWeight : LatinWeight;
        }

        private static bool IsCyrillic(char c)
        {
            return (c >= '\u0400' && c <= '\u052F') ||
                   (c >= '\u2DE0' && c <= '\u2DFF') ||
                   (c >= '\uA640' && c <= '\uA69F');
        }

        private static bool IsCjk(char c)
        {
            return (c >= '\u3400' && c <= '\u4DBF') ||
                   (c >= '\u4E00' && c <= '\u9FFF') ||
                   (c >= '\uF900' && c <= '\uFAFF') ||
                   (c >= '\u3040' && c <= '\u30FF') ||
                   (c >= '\uAC00' && c <= '\uD7AF') ||
                   (c >= '\u1100' && c <= '\u11FF') ||
                   (c >= '\u3130' && c <= '\u318F') ||
                   (c >= '\uA960' && c <= '\uA97F') ||
                   (c >= '\uD7B0' && c <= '\uD7FF');
        }

        private static double Clamp(double value, double min, double max)
        {
            if (value < min)
            {
                return min;
            }

            return value > max ? max : value;
        }

        private static string NormalizeModelKey(string modelKey)
        {
            return string.IsNullOrWhiteSpace(modelKey) ? "default" : modelKey.Trim();
        }
    }
}
