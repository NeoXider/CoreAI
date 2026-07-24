using System.Collections.Generic;
using System.Linq;
using CoreAI.Mods.Rbx.Datatypes;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode.RbxApi.Datatypes
{
    /// <summary>
    /// Deterministic Random per the architecture rules (§3: RNG behind reproducible seams)
    /// and Roblox Random.new docs semantics: floored seed, inclusive NextInteger, Clone state.
    /// </summary>
    [TestFixture]
    public sealed class RbxRandomEditModeTests
    {
        [Test]
        public void SameSeed_ProducesIdenticalSequence()
        {
            var a = new RbxRandom(42);
            var b = new RbxRandom(42);
            for (int i = 0; i < 100; i++)
            {
                Assert.AreEqual(a.NextNumber(), b.NextNumber());
                Assert.AreEqual(a.NextInteger(-1000, 1000), b.NextInteger(-1000, 1000));
            }
        }

        [Test]
        public void DifferentSeeds_ProduceDifferentSequences()
        {
            var a = new RbxRandom(1);
            var b = new RbxRandom(2);
            bool anyDifferent = false;
            for (int i = 0; i < 16 && !anyDifferent; i++)
            {
                anyDifferent = a.NextNumber() != b.NextNumber();
            }

            Assert.IsTrue(anyDifferent);
        }

        [Test]
        public void Seed_IsFlooredToInteger_DocsParity()
        {
            // WHY: Roblox docs — seeds 0 and 0.99 produce identical generators.
            var zero = new RbxRandom(0.0);
            var almostOne = new RbxRandom(0.99);
            for (int i = 0; i < 32; i++)
            {
                Assert.AreEqual(zero.NextNumber(), almostOne.NextNumber());
            }

            var negative = new RbxRandom(-1.01);
            var minusTwo = new RbxRandom(-2.0);
            Assert.AreEqual(minusTwo.NextNumber(), negative.NextNumber(), "floor(-1.01) == -2");
        }

        [Test]
        public void Clone_ContinuesIdenticallyAndIndependently()
        {
            var original = new RbxRandom(7);
            original.NextNumber();
            RbxRandom clone = original.Clone();

            for (int i = 0; i < 32; i++)
            {
                Assert.AreEqual(original.NextInteger(0, 1_000_000), clone.NextInteger(0, 1_000_000));
            }

            // Advancing one must not affect the other.
            original.NextNumber();
            Assert.AreNotEqual(original.NextNumber(), clone.NextNumber());
        }

        [Test]
        public void NextInteger_BothBoundsInclusive()
        {
            var rng = new RbxRandom(123);
            var seen = new HashSet<long>();
            for (int i = 0; i < 400; i++)
            {
                long value = rng.NextInteger(1, 3);
                Assert.GreaterOrEqual(value, 1);
                Assert.LessOrEqual(value, 3);
                seen.Add(value);
            }

            CollectionAssert.AreEquivalent(new long[] { 1, 2, 3 }, seen.ToList());
        }

        [Test]
        public void NextInteger_SingleValueInterval()
        {
            var rng = new RbxRandom(5);
            Assert.AreEqual(9, rng.NextInteger(9, 9));
        }

        [Test]
        public void NextNumber_StaysInRange()
        {
            var rng = new RbxRandom(99);
            for (int i = 0; i < 200; i++)
            {
                double v = rng.NextNumber();
                Assert.GreaterOrEqual(v, 0.0);
                Assert.LessOrEqual(v, 1.0);

                double ranged = rng.NextNumber(-2.5, 7.5);
                Assert.GreaterOrEqual(ranged, -2.5);
                Assert.LessOrEqual(ranged, 7.5);
            }
        }

        [Test]
        public void EmptyIntervals_RaiseBadArgument()
        {
            var rng = new RbxRandom(1);
            var intEx = Assert.Throws<RbxApiStubException>(() => rng.NextInteger(3, 1));
            Assert.AreEqual("BAD_ARGUMENT", intEx.Code);
            var numEx = Assert.Throws<RbxApiStubException>(() => rng.NextNumber(3.0, 1.0));
            Assert.AreEqual("BAD_ARGUMENT", numEx.Code);
        }

        [Test]
        public void NextUnitVector_IsUnitLengthAndDeterministic()
        {
            var a = new RbxRandom(2026);
            var b = new RbxRandom(2026);
            for (int i = 0; i < 50; i++)
            {
                RbxVector3 v = a.NextUnitVector();
                Assert.AreEqual(1f, v.Magnitude, 1e-4f);
                Assert.AreEqual(v, b.NextUnitVector());
            }
        }

        [Test]
        public void Shuffle_IsPermutationAndSeedDeterministic()
        {
            List<int> first = Enumerable.Range(1, 20).ToList();
            List<int> second = Enumerable.Range(1, 20).ToList();
            new RbxRandom(11).Shuffle(first);
            new RbxRandom(11).Shuffle(second);

            CollectionAssert.AreEqual(first, second, "same seed shuffles identically");
            CollectionAssert.AreEquivalent(Enumerable.Range(1, 20).ToList(), first);
            CollectionAssert.AreNotEqual(Enumerable.Range(1, 20).ToList(), first,
                "20 elements shuffling to identity would indicate a broken generator");
        }
    }
}
