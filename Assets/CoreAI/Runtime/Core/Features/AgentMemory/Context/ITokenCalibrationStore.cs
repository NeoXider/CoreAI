namespace CoreAI.Ai
{
    /// <summary>
    /// Optional persistence for token-estimator calibration scales keyed by model id.
    /// </summary>
    public interface ITokenCalibrationStore
    {
        /// <summary>Returns a persisted scale for the model key, if one exists.</summary>
        bool TryLoadScale(string modelKey, out double scale);

        /// <summary>Persists the latest bounded scale for the model key.</summary>
        void SaveScale(string modelKey, double scale);
    }

    /// <summary>
    /// No-op calibration store used by portable hosts that do not persist estimator state.
    /// </summary>
    public sealed class NullTokenCalibrationStore : ITokenCalibrationStore
    {
        /// <summary>Shared stateless instance.</summary>
        public static readonly NullTokenCalibrationStore Instance = new();

        private NullTokenCalibrationStore()
        {
        }

        /// <inheritdoc />
        public bool TryLoadScale(string modelKey, out double scale)
        {
            scale = 1.0d;
            return false;
        }

        /// <inheritdoc />
        public void SaveScale(string modelKey, double scale)
        {
        }
    }
}
