using System;
using System.Diagnostics;

namespace UnityEngine
{
    /// <summary>
    /// The <c>UnityEngine.Mathf</c> members the Lua tier calls, with the bodies of Unity's managed
    /// implementation (single-precision, Unity's clamping and approximate-equality rules).
    /// </summary>
    public static class Mathf
    {
        public const float PI = (float)Math.PI;

        public const float Infinity = float.PositiveInfinity;

        public const float Deg2Rad = PI * 2F / 360F;

        public const float Rad2Deg = 1F / Deg2Rad;

        /// <summary>float.Epsilon, which is Unity's value on x86/x64 where denormals are not flushed.</summary>
        public static readonly float Epsilon = float.Epsilon;

        public static int Max(int a, int b)
        {
            return a > b ? a : b;
        }

        public static float Max(float a, float b)
        {
            return a > b ? a : b;
        }

        public static int Min(int a, int b)
        {
            return a < b ? a : b;
        }

        public static float Min(float a, float b)
        {
            return a < b ? a : b;
        }

        public static float Abs(float f)
        {
            return Math.Abs(f);
        }

        public static float Sqrt(float f)
        {
            return (float)Math.Sqrt(f);
        }

        public static float Acos(float f)
        {
            return (float)Math.Acos(f);
        }

        public static float Sign(float f)
        {
            return f >= 0F ? 1F : -1F;
        }

        public static float Clamp(float value, float min, float max)
        {
            if (value < min)
            {
                value = min;
            }
            else if (value > max)
            {
                value = max;
            }

            return value;
        }

        public static int Clamp(int value, int min, int max)
        {
            if (value < min)
            {
                value = min;
            }
            else if (value > max)
            {
                value = max;
            }

            return value;
        }

        public static float Clamp01(float value)
        {
            if (value < 0F)
            {
                return 0F;
            }

            if (value > 1F)
            {
                return 1F;
            }

            return value;
        }

        public static bool Approximately(float a, float b)
        {
            return Abs(b - a) < Max(0.000001f * Max(Abs(a), Abs(b)), Epsilon * 8);
        }
    }

    /// <summary>
    /// The <c>UnityEngine.Time</c> members the Lua tier reads. Real time counts from process start, like
    /// Unity counts from player (or editor) start, and advances monotonically after the first read, so
    /// code that compares an uptime against a short elapsed interval sees a realistic magnitude.
    /// <see cref="timeScale"/> and <see cref="fixedDeltaTime"/> hold the project's settings (1 and 0.02, see
    /// ProjectSettings/TimeManager.asset) and are settable; the engine's out-of-range check on
    /// <see cref="timeScale"/> is not reproduced (the only caller clamps first). Frame-loop values
    /// (<see cref="time"/>, <see cref="deltaTime"/>, <see cref="unscaledDeltaTime"/>,
    /// <see cref="frameCount"/>) come from the engine's player loop, which does not exist here, so they
    /// are refused rather than invented.
    /// </summary>
    public static class Time
    {
        private static readonly Stopwatch Clock = Stopwatch.StartNew();
        private static readonly double SecondsBeforeClock = SecondsSinceProcessStart();
        private static float _timeScale = 1F;
        private static float _fixedDeltaTime = 0.02F;

        public static float realtimeSinceStartup => (float)realtimeSinceStartupAsDouble;

        public static double realtimeSinceStartupAsDouble => SecondsBeforeClock + Clock.Elapsed.TotalSeconds;

        public static float timeScale
        {
            get => _timeScale;
            set => _timeScale = value;
        }

        public static float fixedDeltaTime
        {
            get => _fixedDeltaTime;
            set => _fixedDeltaTime = value;
        }

        public static float time => throw PortableEngine.Unavailable("Time.time");

        public static float deltaTime => throw PortableEngine.Unavailable("Time.deltaTime");

        public static float unscaledDeltaTime => throw PortableEngine.Unavailable("Time.unscaledDeltaTime");

        public static int frameCount => throw PortableEngine.Unavailable("Time.frameCount");

        private static double SecondsSinceProcessStart()
        {
            try
            {
                double seconds = (DateTime.Now - Process.GetCurrentProcess().StartTime).TotalSeconds;
                return seconds > 0d ? seconds : 0d;
            }
            catch (InvalidOperationException)
            {
                return 0d;
            }
            catch (NotSupportedException)
            {
                return 0d;
            }
        }
    }

    /// <summary>
    /// Raised by every shim member that would need the real engine (scene objects, physics, input,
    /// rendering, the native serializer, the player loop). The portable suite never fabricates those
    /// answers: a test that reaches one fails with this exception and is triaged as a platform artifact.
    /// </summary>
    public sealed class PortableEngineUnavailableException : NotSupportedException
    {
        public PortableEngineUnavailableException(string api)
            : base("PORTABLE_ENGINE_UNAVAILABLE: UnityEngine." + api +
                   " needs the Unity engine, which the portable Lua suite does not have.")
        {
            Api = api;
            PortableRefusalLog.Record(api);
        }

        /// <summary>The refused member, e.g. <c>GameObject.Find</c>.</summary>
        public string Api { get; }
    }

    /// <summary>
    /// Every refusal raised since the last <see cref="Drain"/>. Runtime code may catch a refusal and
    /// carry on (a generic catch turning it into an error status), which could make a test pass for
    /// the wrong reason; the portable test assembly drains this after each test and never reports such
    /// a test as passed.
    /// </summary>
    public static class PortableRefusalLog
    {
        private static readonly object Gate = new object();
        private static readonly System.Collections.Generic.List<string> Refused =
            new System.Collections.Generic.List<string>();

        internal static void Record(string api)
        {
            lock (Gate)
            {
                Refused.Add(api);
            }
        }

        /// <summary>Returns the refused members recorded so far and clears the log.</summary>
        public static string[] Drain()
        {
            lock (Gate)
            {
                string[] snapshot = Refused.ToArray();
                Refused.Clear();
                return snapshot;
            }
        }
    }

    internal static class PortableEngine
    {
        internal static PortableEngineUnavailableException Unavailable(string api)
        {
            return new PortableEngineUnavailableException(api);
        }
    }
}
