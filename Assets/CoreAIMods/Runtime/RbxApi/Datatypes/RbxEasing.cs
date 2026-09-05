using System;

namespace CoreAI.Mods.Rbx.Datatypes
{
    /// <summary>
    /// Mirror <c>Enum.EasingStyle</c> item set, faithfully ordered and valued
    /// (Linear 0 through Cubic 10). Declared here so the engine-free tween driver and
    /// <see cref="RbxTweenInfo"/> share one source of truth with the enum registry seed.
    /// </summary>
    public enum RbxEasingStyle
    {
        Linear = 0,
        Sine = 1,
        Back = 2,
        Quad = 3,
        Quart = 4,
        Quint = 5,
        Bounce = 6,
        Elastic = 7,
        Exponential = 8,
        Circular = 9,
        Cubic = 10
    }

    /// <summary>Mirror <c>Enum.EasingDirection</c> (In 0, Out 1, InOut 2).</summary>
    public enum RbxEasingDirection
    {
        In = 0,
        Out = 1,
        InOut = 2
    }

    /// <summary>
    /// Pure easing math behind <c>TweenService:GetValue</c> and the tween driver. Each style is
    /// defined once as its ease-in base; Out and InOut derive from it
    /// (<c>Out(t) = 1 - In(1 - t)</c>), which reproduces the mirror curves for every style.
    /// </summary>
    public static class RbxEasing
    {
        private const double BackOvershoot = 1.70158d;
        private const double BounceN1 = 7.5625d;
        private const double BounceD = 2.75d;

        /// <summary>
        /// Eased alpha for a clamped input. The input is clamped to [0, 1] (mirror:
        /// "The provided `alpha` value will be clamped between `0` and `1`").
        /// </summary>
        public static double Evaluate(double alpha, RbxEasingStyle style,
            RbxEasingDirection direction)
        {
            double clamped = alpha < 0d ? 0d : alpha > 1d ? 1d : alpha;
            switch (direction)
            {
                case RbxEasingDirection.In:
                    return EaseIn(clamped, style);
                case RbxEasingDirection.Out:
                    return 1d - EaseIn(1d - clamped, style);
                default:
                    return clamped < 0.5d
                        ? EaseIn(clamped * 2d, style) * 0.5d
                        : (2d - EaseIn((1d - clamped) * 2d, style)) * 0.5d;
            }
        }

        private static double EaseIn(double time, RbxEasingStyle style)
        {
            switch (style)
            {
                case RbxEasingStyle.Linear:
                    return time;
                case RbxEasingStyle.Sine:
                    return 1d - Math.Cos(time * Math.PI * 0.5d);
                case RbxEasingStyle.Back:
                    return (BackOvershoot + 1d) * time * time * time
                        - BackOvershoot * time * time;
                case RbxEasingStyle.Quad:
                    return time * time;
                case RbxEasingStyle.Quart:
                    return time * time * time * time;
                case RbxEasingStyle.Quint:
                    return time * time * time * time * time;
                case RbxEasingStyle.Bounce:
                    return 1d - BounceOut(1d - time);
                case RbxEasingStyle.Elastic:
                    return ElasticIn(time);
                case RbxEasingStyle.Exponential:
                    return time <= 0d ? 0d : Math.Pow(2d, (10d * time) - 10d);
                case RbxEasingStyle.Circular:
                    return 1d - Math.Sqrt(1d - (time * time));
                default:
                    return time * time * time;
            }
        }

        private static double BounceOut(double time)
        {
            if (time < 1d / BounceD)
            {
                return BounceN1 * time * time;
            }

            if (time < 2d / BounceD)
            {
                double shifted = time - (1.5d / BounceD);
                return (BounceN1 * shifted * shifted) + 0.75d;
            }

            if (time < 2.5d / BounceD)
            {
                double shifted = time - (2.25d / BounceD);
                return (BounceN1 * shifted * shifted) + 0.9375d;
            }

            double tail = time - (2.625d / BounceD);
            return (BounceN1 * tail * tail) + 0.984375d;
        }

        private static double ElasticIn(double time)
        {
            if (time <= 0d)
            {
                return 0d;
            }

            if (time >= 1d)
            {
                return 1d;
            }

            return -Math.Pow(2d, (10d * time) - 10d)
                * Math.Sin((((10d * time) - 10.75d) * 2d * Math.PI) / 3d);
        }
    }
}
