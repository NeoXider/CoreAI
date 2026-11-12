using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace CoreAI.Mods.Rbx.Datatypes
{
    /// <summary>
    /// Pure-spec Roblox <c>NumberRange</c> (NumberRange.yaml): a <see cref="Min"/> at or below a <see cref="Max"/>,
    /// stored as 32-bit floats. The tostring form is the dump's, <c>"min max "</c>.
    /// </summary>
    public readonly struct RbxNumberRange : IEquatable<RbxNumberRange>
    {
        public RbxNumberRange(float value)
            : this(value, value)
        {
        }

        /// <exception cref="ArgumentException">Thrown when a bound is not finite or min is above max.</exception>
        public RbxNumberRange(float min, float max)
        {
            if (float.IsNaN(min) || float.IsInfinity(min) || float.IsNaN(max) || float.IsInfinity(max))
            {
                throw new ArgumentException("a NumberRange needs finite bounds");
            }

            if (min > max)
            {
                throw new ArgumentException("a NumberRange's minimum must be at or below its maximum");
            }

            Min = min;
            Max = max;
        }

        public float Min { get; }

        public float Max { get; }

        public bool Equals(RbxNumberRange other)
        {
            return Min.Equals(other.Min) && Max.Equals(other.Max);
        }

        public override bool Equals(object obj)
        {
            return obj is RbxNumberRange other && Equals(other);
        }

        public override int GetHashCode()
        {
            return HashCode.Combine(Min, Max);
        }

        public static bool operator ==(RbxNumberRange left, RbxNumberRange right)
        {
            return left.Equals(right);
        }

        public static bool operator !=(RbxNumberRange left, RbxNumberRange right)
        {
            return !left.Equals(right);
        }

        /// <summary>Roblox tostring format: <c>"min max "</c>.</summary>
        public override string ToString()
        {
            return string.Format(CultureInfo.InvariantCulture, "{0} {1} ", Min, Max);
        }
    }

    /// <summary>One point of a <see cref="RbxNumberSequence"/>: a time in [0, 1], a value and an envelope of at least 0.</summary>
    public readonly struct RbxNumberSequenceKeypoint : IEquatable<RbxNumberSequenceKeypoint>
    {
        /// <exception cref="ArgumentException">Thrown for a time outside [0, 1] or a non-finite number.</exception>
        public RbxNumberSequenceKeypoint(float time, float value, float envelope = 0f)
        {
            if (float.IsNaN(time) || time < 0f || time > 1f)
            {
                throw new ArgumentException("a keypoint's time must be between 0 and 1");
            }

            if (float.IsNaN(value) || float.IsInfinity(value) || float.IsNaN(envelope) || float.IsInfinity(envelope))
            {
                throw new ArgumentException("a keypoint needs a finite value and envelope");
            }

            Time = time;
            Value = value;
            Envelope = Math.Max(0f, envelope);
        }

        public float Time { get; }

        public float Value { get; }

        public float Envelope { get; }

        public bool Equals(RbxNumberSequenceKeypoint other)
        {
            return Time.Equals(other.Time) && Value.Equals(other.Value) && Envelope.Equals(other.Envelope);
        }

        public override bool Equals(object obj)
        {
            return obj is RbxNumberSequenceKeypoint other && Equals(other);
        }

        public override int GetHashCode()
        {
            return HashCode.Combine(Time, Value, Envelope);
        }

        /// <summary>Roblox tostring format: <c>"time value envelope "</c>.</summary>
        public override string ToString()
        {
            return string.Format(CultureInfo.InvariantCulture, "{0} {1} {2} ", Time, Value, Envelope);
        }
    }

    /// <summary>One point of a <see cref="RbxColorSequence"/>: a time in [0, 1] and a colour.</summary>
    public readonly struct RbxColorSequenceKeypoint : IEquatable<RbxColorSequenceKeypoint>
    {
        /// <exception cref="ArgumentException">Thrown for a time outside [0, 1] or a non-finite colour.</exception>
        public RbxColorSequenceKeypoint(float time, RbxColor3 value)
        {
            if (float.IsNaN(time) || time < 0f || time > 1f)
            {
                throw new ArgumentException("a keypoint's time must be between 0 and 1");
            }

            if (!IsFinite(value.R) || !IsFinite(value.G) || !IsFinite(value.B))
            {
                throw new ArgumentException("a keypoint needs a finite colour");
            }

            Time = time;
            Value = value;
        }

        public float Time { get; }

        public RbxColor3 Value { get; }

        public bool Equals(RbxColorSequenceKeypoint other)
        {
            return Time.Equals(other.Time) && Value == other.Value;
        }

        public override bool Equals(object obj)
        {
            return obj is RbxColorSequenceKeypoint other && Equals(other);
        }

        public override int GetHashCode()
        {
            return HashCode.Combine(Time, Value.R, Value.G, Value.B);
        }

        /// <summary>Roblox tostring format: <c>"time r g b 0 "</c> (a colour keypoint has no envelope).</summary>
        public override string ToString()
        {
            return string.Format(CultureInfo.InvariantCulture, "{0} {1} {2} {3} 0 ", Time, Value.R, Value.G, Value.B);
        }

        private static bool IsFinite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }
    }

    /// <summary>The keypoint rules NumberSequence.yaml and ColorSequence.yaml state for the array constructor.</summary>
    public static class RbxSequenceRules
    {
        /// <summary>The most keypoints one sequence holds.</summary>
        public const int MaxKeypoints = 20;

        /// <summary>
        /// Null when <paramref name="times"/> is a valid keypoint list: at least two, at most
        /// <see cref="MaxKeypoints"/>, in non-descending time order, the first at 0 and the last at 1; otherwise
        /// the reason.
        /// </summary>
        public static string Check(IReadOnlyList<float> times)
        {
            if (times == null || times.Count < 2)
            {
                return "at least two keypoints are needed";
            }

            if (times.Count > MaxKeypoints)
            {
                return "at most " + MaxKeypoints + " keypoints are allowed";
            }

            if (times[0] != 0f)
            {
                return "the first keypoint's time must be 0";
            }

            if (times[times.Count - 1] != 1f)
            {
                return "the last keypoint's time must be 1";
            }

            for (int index = 1; index < times.Count; index++)
            {
                if (times[index] < times[index - 1])
                {
                    return "keypoints must be in non-descending time order";
                }
            }

            return null;
        }
    }

    /// <summary>
    /// Pure-spec Roblox <c>NumberSequence</c>: an immutable keypoint list from time 0 to time 1. <see cref="Evaluate"/>
    /// interpolates linearly between keypoints, ignoring envelopes (NumberSequence.yaml's evaluation recipe).
    /// </summary>
    public sealed class RbxNumberSequence : IEquatable<RbxNumberSequence>
    {
        private readonly RbxNumberSequenceKeypoint[] _keypoints;

        /// <summary>Two keypoints holding <paramref name="value"/>.</summary>
        public RbxNumberSequence(float value)
            : this(value, value)
        {
        }

        /// <summary>From <paramref name="start"/> at time 0 to <paramref name="end"/> at time 1.</summary>
        public RbxNumberSequence(float start, float end)
            : this(new[] { new RbxNumberSequenceKeypoint(0f, start), new RbxNumberSequenceKeypoint(1f, end) })
        {
        }

        /// <exception cref="ArgumentException">Thrown when the keypoints break <see cref="RbxSequenceRules.Check"/>.</exception>
        public RbxNumberSequence(IReadOnlyList<RbxNumberSequenceKeypoint> keypoints)
        {
            if (keypoints == null)
            {
                throw new ArgumentNullException(nameof(keypoints));
            }

            float[] times = new float[keypoints.Count];
            for (int index = 0; index < times.Length; index++)
            {
                times[index] = keypoints[index].Time;
            }

            string problem = RbxSequenceRules.Check(times);
            if (problem != null)
            {
                throw new ArgumentException(problem);
            }

            _keypoints = new RbxNumberSequenceKeypoint[keypoints.Count];
            for (int index = 0; index < _keypoints.Length; index++)
            {
                _keypoints[index] = keypoints[index];
            }
        }

        public IReadOnlyList<RbxNumberSequenceKeypoint> Keypoints => _keypoints;

        /// <summary>The value at <paramref name="time"/> (held to [0, 1]).</summary>
        public float Evaluate(float time)
        {
            float t = float.IsNaN(time) ? 0f : Math.Max(0f, Math.Min(1f, time));
            for (int index = 0; index < _keypoints.Length - 1; index++)
            {
                RbxNumberSequenceKeypoint from = _keypoints[index];
                RbxNumberSequenceKeypoint to = _keypoints[index + 1];
                if (t < to.Time || index == _keypoints.Length - 2)
                {
                    float span = to.Time - from.Time;
                    float alpha = span > 0f ? Math.Max(0f, Math.Min(1f, (t - from.Time) / span)) : 1f;
                    return from.Value + (to.Value - from.Value) * alpha;
                }
            }

            return _keypoints[_keypoints.Length - 1].Value;
        }

        /// <summary>The largest value any keypoint reaches.</summary>
        public float MaxValue()
        {
            float max = float.MinValue;
            for (int index = 0; index < _keypoints.Length; index++)
            {
                max = Math.Max(max, _keypoints[index].Value);
            }

            return max;
        }

        public bool Equals(RbxNumberSequence other)
        {
            if (other is null || other._keypoints.Length != _keypoints.Length)
            {
                return false;
            }

            for (int index = 0; index < _keypoints.Length; index++)
            {
                if (!_keypoints[index].Equals(other._keypoints[index]))
                {
                    return false;
                }
            }

            return true;
        }

        public override bool Equals(object obj)
        {
            return obj is RbxNumberSequence other && Equals(other);
        }

        public override int GetHashCode()
        {
            int hash = _keypoints.Length;
            for (int index = 0; index < _keypoints.Length; index++)
            {
                hash = hash * 31 + _keypoints[index].GetHashCode();
            }

            return hash;
        }

        /// <summary>Roblox tostring format: each keypoint's <c>"time value envelope "</c> in order.</summary>
        public override string ToString()
        {
            StringBuilder text = new();
            for (int index = 0; index < _keypoints.Length; index++)
            {
                text.Append(_keypoints[index].ToString());
            }

            return text.ToString();
        }
    }

    /// <summary>
    /// Pure-spec Roblox <c>ColorSequence</c>: an immutable keypoint list from time 0 to time 1. <see cref="Evaluate"/>
    /// interpolates each channel linearly between keypoints (ColorSequence.yaml's evaluation recipe).
    /// </summary>
    public sealed class RbxColorSequence : IEquatable<RbxColorSequence>
    {
        private readonly RbxColorSequenceKeypoint[] _keypoints;

        /// <summary>Two keypoints holding <paramref name="color"/>.</summary>
        public RbxColorSequence(RbxColor3 color)
            : this(color, color)
        {
        }

        /// <summary>From <paramref name="start"/> at time 0 to <paramref name="end"/> at time 1.</summary>
        public RbxColorSequence(RbxColor3 start, RbxColor3 end)
            : this(new[] { new RbxColorSequenceKeypoint(0f, start), new RbxColorSequenceKeypoint(1f, end) })
        {
        }

        /// <exception cref="ArgumentException">Thrown when the keypoints break <see cref="RbxSequenceRules.Check"/>.</exception>
        public RbxColorSequence(IReadOnlyList<RbxColorSequenceKeypoint> keypoints)
        {
            if (keypoints == null)
            {
                throw new ArgumentNullException(nameof(keypoints));
            }

            float[] times = new float[keypoints.Count];
            for (int index = 0; index < times.Length; index++)
            {
                times[index] = keypoints[index].Time;
            }

            string problem = RbxSequenceRules.Check(times);
            if (problem != null)
            {
                throw new ArgumentException(problem);
            }

            _keypoints = new RbxColorSequenceKeypoint[keypoints.Count];
            for (int index = 0; index < _keypoints.Length; index++)
            {
                _keypoints[index] = keypoints[index];
            }
        }

        public IReadOnlyList<RbxColorSequenceKeypoint> Keypoints => _keypoints;

        /// <summary>The colour at <paramref name="time"/> (held to [0, 1]).</summary>
        public RbxColor3 Evaluate(float time)
        {
            float t = float.IsNaN(time) ? 0f : Math.Max(0f, Math.Min(1f, time));
            for (int index = 0; index < _keypoints.Length - 1; index++)
            {
                RbxColorSequenceKeypoint from = _keypoints[index];
                RbxColorSequenceKeypoint to = _keypoints[index + 1];
                if (t < to.Time || index == _keypoints.Length - 2)
                {
                    float span = to.Time - from.Time;
                    float alpha = span > 0f ? Math.Max(0f, Math.Min(1f, (t - from.Time) / span)) : 1f;
                    return new RbxColor3(
                        from.Value.R + (to.Value.R - from.Value.R) * alpha,
                        from.Value.G + (to.Value.G - from.Value.G) * alpha,
                        from.Value.B + (to.Value.B - from.Value.B) * alpha);
                }
            }

            return _keypoints[_keypoints.Length - 1].Value;
        }

        public bool Equals(RbxColorSequence other)
        {
            if (other is null || other._keypoints.Length != _keypoints.Length)
            {
                return false;
            }

            for (int index = 0; index < _keypoints.Length; index++)
            {
                if (!_keypoints[index].Equals(other._keypoints[index]))
                {
                    return false;
                }
            }

            return true;
        }

        public override bool Equals(object obj)
        {
            return obj is RbxColorSequence other && Equals(other);
        }

        public override int GetHashCode()
        {
            int hash = _keypoints.Length;
            for (int index = 0; index < _keypoints.Length; index++)
            {
                hash = hash * 31 + _keypoints[index].GetHashCode();
            }

            return hash;
        }

        /// <summary>Roblox tostring format: each keypoint's <c>"time r g b 0 "</c> in order.</summary>
        public override string ToString()
        {
            StringBuilder text = new();
            for (int index = 0; index < _keypoints.Length; index++)
            {
                text.Append(_keypoints[index].ToString());
            }

            return text.ToString();
        }
    }
}
