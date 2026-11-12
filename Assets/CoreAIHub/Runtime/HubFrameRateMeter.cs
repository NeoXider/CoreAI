using System.Globalization;

namespace CoreAI.Hub.UI
{
    /// <summary>
    /// Frames per second over the stretch between two readings: fed the frame counter and a clock, it reports the
    /// average of the frames in between. No per-frame code: the page asks a few times a second.
    /// </summary>
    internal sealed class HubFrameRateMeter
    {
        /// <summary>Text shown until the first reading is in.</summary>
        public const string NoReadingText = "measuring...";

        /// <summary>The shortest stretch a reading is taken over; a shorter one is too noisy to show.</summary>
        public const double MinimumWindowSeconds = 0.25;

        private int _lastFrame = -1;
        private double _lastSeconds;

        /// <summary>True once a reading was taken.</summary>
        public bool HasReading { get; private set; }

        /// <summary>The last reading, in frames per second.</summary>
        public double FramesPerSecond { get; private set; }

        /// <summary>
        /// Takes a reading from the frame counter and the clock. Returns true when <see cref="FramesPerSecond"/>
        /// changed; false while the stretch since the last reading is still too short, and on the first call.
        /// </summary>
        public bool Sample(int frameCount, double seconds)
        {
            if (_lastFrame < 0 || frameCount < _lastFrame || seconds < _lastSeconds)
            {
                _lastFrame = frameCount;
                _lastSeconds = seconds;
                return false;
            }

            int frames = frameCount - _lastFrame;
            double elapsed = seconds - _lastSeconds;
            if (frames <= 0 || elapsed < MinimumWindowSeconds)
            {
                return false;
            }

            FramesPerSecond = frames / elapsed;
            HasReading = true;
            _lastFrame = frameCount;
            _lastSeconds = seconds;
            return true;
        }

        /// <summary>The reading as the page shows it, for example "60 (16.7 ms a frame)".</summary>
        public string Describe()
        {
            if (!HasReading || FramesPerSecond <= 0d)
            {
                return NoReadingText;
            }

            return string.Format(CultureInfo.InvariantCulture, "{0:0} ({1:0.0} ms a frame)", FramesPerSecond,
                1000d / FramesPerSecond);
        }
    }
}
