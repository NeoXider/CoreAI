using System;
using CoreAI.Decisions;
using NUnit.Framework;

namespace CoreAI.Core.Tests.EditMode.Decisions
{
    /// <summary>The softmax over label logprobs, renormalisation and arg-max.</summary>
    [TestFixture]
    public sealed class DecisionProbabilityMathEditModeTests
    {
        private const double Tolerance = 1e-9;

        [Test]
        public void Softmax_OfLogprobs_EqualsRenormalisedProbabilities()
        {
            double[] probabilities = DecisionProbabilityMath.SoftmaxFromLogprobs(
                new[] { Math.Log(0.6), Math.Log(0.25), Math.Log(0.05) });

            Assert.AreEqual(0.6 / 0.9, probabilities[0], Tolerance);
            Assert.AreEqual(0.25 / 0.9, probabilities[1], Tolerance);
            Assert.AreEqual(0.05 / 0.9, probabilities[2], Tolerance);
            Assert.AreEqual(1d, probabilities[0] + probabilities[1] + probabilities[2], Tolerance);
        }

        [Test]
        public void Softmax_UnlistedLabel_GetsZero()
        {
            double[] probabilities = DecisionProbabilityMath.SoftmaxFromLogprobs(
                new[] { Math.Log(0.3), double.NegativeInfinity });

            Assert.AreEqual(new[] { 1d, 0d }, probabilities);
        }

        [Test]
        public void Softmax_NoFiniteLogprob_IsNull()
        {
            Assert.IsNull(DecisionProbabilityMath.SoftmaxFromLogprobs(new[] { double.NegativeInfinity, double.NaN }));
            Assert.IsNull(DecisionProbabilityMath.SoftmaxFromLogprobs(Array.Empty<double>()));
        }

        [Test]
        public void Softmax_IsStableForLargeMagnitudes()
        {
            double[] probabilities = DecisionProbabilityMath.SoftmaxFromLogprobs(new[] { -1000d, -1001d });

            Assert.AreEqual(1d / (1d + Math.Exp(-1d)), probabilities[0], Tolerance);
        }

        [Test]
        public void Renormalize_DropsNegativeAndNonFiniteEntries()
        {
            double[] probabilities = DecisionProbabilityMath.Renormalize(new[] { 0.2, -1d, double.NaN, 0.2, double.PositiveInfinity });

            Assert.AreEqual(new[] { 0.5, 0d, 0d, 0.5, 0d }, probabilities);
        }

        [Test]
        public void Renormalize_AllZero_IsNull()
        {
            Assert.IsNull(DecisionProbabilityMath.Renormalize(new[] { 0d, 0d }));
        }

        [Test]
        public void LogSumExp_MergesTwoVariantsOfOneLabel()
        {
            Assert.AreEqual(Math.Log(0.62), DecisionProbabilityMath.LogSumExp(Math.Log(0.6), Math.Log(0.02)), Tolerance);
            Assert.AreEqual(-2d, DecisionProbabilityMath.LogSumExp(double.NegativeInfinity, -2d));
        }

        [Test]
        public void ArgMax_TieGoesToTheLowestIndex()
        {
            Assert.AreEqual(1, DecisionProbabilityMath.ArgMax(new[] { 0.2, 0.4, 0.4 }));
            Assert.AreEqual(-1, DecisionProbabilityMath.ArgMax(Array.Empty<double>()));
        }

        [Test]
        public void ExpectedIndex_IsTheProbabilityWeightedLevel()
        {
            Assert.AreEqual(2.4, DecisionProbabilityMath.ExpectedIndex(new[] { 0.05, 0.15, 0.15, 0.65 }), Tolerance);
        }
    }
}
