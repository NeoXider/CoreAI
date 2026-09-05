using System;

namespace CoreAI.Mods.Rbx.Datatypes
{
    /// <summary>
    /// Mirror <c>TweenInfo</c> datatype: immutable playback parameters for
    /// <c>TweenService:Create</c>. Mirror-pinned <c>TweenInfo.new</c> defaults, in order:
    /// time 1, easingStyle Quad, easingDirection Out, repeatCount 0, reverses false,
    /// delayTime 0. Negative time/delay clamp to 0 (OURS — the mirror does not specify;
    /// same rule as Debris lifetimes); non-finite inputs are refused.
    /// </summary>
    public sealed class RbxTweenInfo : IEquatable<RbxTweenInfo>
    {
        /// <summary>Mirror default: duration when TweenInfo.new omits time.</summary>
        public const double DefaultTime = 1d;

        /// <summary>Mirror default: style when TweenInfo.new omits easingStyle.</summary>
        public const RbxEasingStyle DefaultEasingStyle = RbxEasingStyle.Quad;

        /// <summary>Mirror default: direction when TweenInfo.new omits easingDirection.</summary>
        public const RbxEasingDirection DefaultEasingDirection = RbxEasingDirection.Out;

        /// <summary>Mirror default: extra repeats when TweenInfo.new omits repeatCount.</summary>
        public const int DefaultRepeatCount = 0;

        /// <summary>Mirror default: reverse flag when TweenInfo.new omits reverses.</summary>
        public const bool DefaultReverses = false;

        /// <summary>Mirror default: start delay when TweenInfo.new omits delayTime.</summary>
        public const double DefaultDelayTime = 0d;

        /// <summary>Mirror default instance: TweenInfo.new() with no arguments.</summary>
        public RbxTweenInfo()
            : this(DefaultTime, DefaultEasingStyle, DefaultEasingDirection,
                DefaultRepeatCount, DefaultReverses, DefaultDelayTime)
        {
        }

        /// <summary>
        /// Creates playback parameters. Non-finite time/delay throw; negatives clamp to 0.
        /// </summary>
        public RbxTweenInfo(double time, RbxEasingStyle easingStyle,
            RbxEasingDirection easingDirection, int repeatCount, bool reverses,
            double delayTime)
        {
            if (double.IsNaN(time) || double.IsInfinity(time))
            {
                throw new ArgumentException(
                    "TweenInfo time must be finite", nameof(time));
            }

            if (double.IsNaN(delayTime) || double.IsInfinity(delayTime))
            {
                throw new ArgumentException(
                    "TweenInfo delayTime must be finite", nameof(delayTime));
            }

            Time = time < 0d ? 0d : time;
            EasingStyle = easingStyle;
            EasingDirection = easingDirection;
            RepeatCount = repeatCount;
            Reverses = reverses;
            DelayTime = delayTime < 0d ? 0d : delayTime;
        }

        /// <summary>Duration for the tween, in seconds.</summary>
        public double Time { get; }

        /// <summary>Easing style for the tween.</summary>
        public RbxEasingStyle EasingStyle { get; }

        /// <summary>The direction in which the tween executes.</summary>
        public RbxEasingDirection EasingDirection { get; }

        /// <summary>Number of times the tween repeats after its first run; negative loops.</summary>
        public int RepeatCount { get; }

        /// <summary>Whether the tween reverses to its start values once it reaches its targets.</summary>
        public bool Reverses { get; }

        /// <summary>Time of delay until the tween begins, in seconds.</summary>
        public double DelayTime { get; }

        /// <inheritdoc />
        public bool Equals(RbxTweenInfo other)
        {
            return other != null
                && Time.Equals(other.Time)
                && EasingStyle == other.EasingStyle
                && EasingDirection == other.EasingDirection
                && RepeatCount == other.RepeatCount
                && Reverses == other.Reverses
                && DelayTime.Equals(other.DelayTime);
        }

        /// <inheritdoc />
        public override bool Equals(object obj)
        {
            return Equals(obj as RbxTweenInfo);
        }

        /// <inheritdoc />
        public override int GetHashCode()
        {
            return HashCode.Combine(Time, EasingStyle, EasingDirection, RepeatCount, Reverses,
                DelayTime);
        }

        /// <inheritdoc />
        public override string ToString()
        {
            return "TweenInfo";
        }
    }
}
