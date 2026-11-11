using System;
using System.Collections.Generic;

namespace CoreAI.Decisions
{
    /// <summary>
    /// Numerics shared by the backends: renormalising a server distribution, the softmax over label logprobs, and
    /// deterministic arg-max. Pure functions; no allocation beyond the returned arrays.
    /// </summary>
    public static class DecisionProbabilityMath
    {
        /// <summary>
        /// Scales <paramref name="values"/> to sum to 1. Negative, NaN and infinite entries count as 0. Returns
        /// null when nothing positive remains, so the caller decides what an empty distribution means.
        /// </summary>
        public static double[] Renormalize(IReadOnlyList<double> values)
        {
            if (values == null || values.Count == 0)
            {
                return null;
            }

            double[] result = new double[values.Count];
            double sum = 0d;
            for (int i = 0; i < values.Count; i++)
            {
                double value = values[i];
                double clean = double.IsNaN(value) || double.IsInfinity(value) || value < 0d ? 0d : value;
                result[i] = clean;
                sum += clean;
            }

            if (sum <= 0d)
            {
                return null;
            }

            for (int i = 0; i < result.Length; i++)
            {
                result[i] /= sum;
            }

            return result;
        }

        /// <summary>
        /// Softmax over natural-log probabilities: <c>p_i = exp(l_i - max) / sum_j exp(l_j - max)</c>, which equals
        /// renormalising the raw label probabilities. <see cref="double.NegativeInfinity"/> marks a label the server
        /// did not report and yields probability 0. Returns null when no label has a finite logprob.
        /// </summary>
        public static double[] SoftmaxFromLogprobs(IReadOnlyList<double> logprobs)
        {
            if (logprobs == null || logprobs.Count == 0)
            {
                return null;
            }

            double max = double.NegativeInfinity;
            for (int i = 0; i < logprobs.Count; i++)
            {
                double value = logprobs[i];
                if (!double.IsNaN(value) && !double.IsPositiveInfinity(value) && value > max)
                {
                    max = value;
                }
            }

            if (double.IsNegativeInfinity(max))
            {
                return null;
            }

            double[] weights = new double[logprobs.Count];
            for (int i = 0; i < logprobs.Count; i++)
            {
                double value = logprobs[i];
                weights[i] = double.IsNaN(value) || double.IsInfinity(value) ? 0d : Math.Exp(value - max);
            }

            return Renormalize(weights);
        }

        /// <summary>log(exp(a) + exp(b)) without overflow; merges token variants of one label.</summary>
        public static double LogSumExp(double a, double b)
        {
            if (double.IsNegativeInfinity(a))
            {
                return b;
            }

            if (double.IsNegativeInfinity(b))
            {
                return a;
            }

            double max = Math.Max(a, b);
            return max + Math.Log(Math.Exp(a - max) + Math.Exp(b - max));
        }

        /// <summary>Index of the largest entry; ties resolve to the lowest index. -1 for an empty list.</summary>
        public static int ArgMax(IReadOnlyList<double> values)
        {
            if (values == null || values.Count == 0)
            {
                return -1;
            }

            int best = 0;
            for (int i = 1; i < values.Count; i++)
            {
                if (values[i] > values[best])
                {
                    best = i;
                }
            }

            return best;
        }

        /// <summary>Probability-weighted zero-based index: <c>sum_i i * p_i</c>.</summary>
        public static double ExpectedIndex(IReadOnlyList<double> probabilities)
        {
            double expected = 0d;
            if (probabilities == null)
            {
                return expected;
            }

            for (int i = 0; i < probabilities.Count; i++)
            {
                expected += i * probabilities[i];
            }

            return expected;
        }
    }
}
