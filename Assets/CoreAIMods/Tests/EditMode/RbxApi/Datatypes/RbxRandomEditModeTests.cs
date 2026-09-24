using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using CoreAI.Mods.Rbx.Datatypes;
using CoreAI.Mods.Rbx.Instances;
#if UNITY_5_3_OR_NEWER
using CoreAI.Mods.Rbx.Spatial;
#endif
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
            RbxRandom a = new(42);
            RbxRandom b = new(42);
            for (int i = 0; i < 100; i++)
            {
                Assert.AreEqual(a.NextNumber(), b.NextNumber());
                Assert.AreEqual(a.NextInteger(-1000, 1000), b.NextInteger(-1000, 1000));
            }
        }

        [Test]
        public void DifferentSeeds_ProduceDifferentSequences()
        {
            RbxRandom a = new(1);
            RbxRandom b = new(2);
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
            RbxRandom zero = new(0.0);
            RbxRandom almostOne = new(0.99);
            for (int i = 0; i < 32; i++)
            {
                Assert.AreEqual(zero.NextNumber(), almostOne.NextNumber());
            }

            RbxRandom negative = new(-1.01);
            RbxRandom minusTwo = new(-2.0);
            Assert.AreEqual(minusTwo.NextNumber(), negative.NextNumber(), "floor(-1.01) == -2");
        }

        [Test]
        public void Clone_ContinuesIdenticallyAndIndependently()
        {
            RbxRandom original = new(7);
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
            RbxRandom rng = new(123);
            HashSet<long> seen = new();
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
            RbxRandom rng = new(5);
            Assert.AreEqual(9, rng.NextInteger(9, 9));
        }

        [Test]
        public void NextNumber_StaysInRange()
        {
            RbxRandom rng = new(99);
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
            RbxRandom rng = new(1);
            RbxApiStubException intEx = Assert.Throws<RbxApiStubException>(() => rng.NextInteger(3, 1));
            Assert.AreEqual("BAD_ARGUMENT", intEx.Code);
            RbxApiStubException numEx = Assert.Throws<RbxApiStubException>(() => rng.NextNumber(3.0, 1.0));
            Assert.AreEqual("BAD_ARGUMENT", numEx.Code);
        }

        [Test]
        public void NextUnitVector_IsUnitLengthAndDeterministic()
        {
            RbxRandom a = new(2026);
            RbxRandom b = new(2026);
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

        [Test]
        public void EmptyIntervalErrorText_IsCultureInvariant()
        {
            RbxRandom rng = new(1);
            RbxApiStubException numberError = null;
            RbxApiStubException integerError = null;
            RunUnderCommaDecimalCulture(() =>
            {
                numberError = Assert.Throws<RbxApiStubException>(() => rng.NextNumber(2.5, 1.5));
                integerError = Assert.Throws<RbxApiStubException>(() => rng.NextInteger(-3, -5));
            });

            StringAssert.Contains("got min=2.5, max=1.5", numberError.Message,
                "the self-repair numbers must read the same under every host locale");
            StringAssert.DoesNotContain("2,5", numberError.Message);
            StringAssert.Contains("got min=-3, max=-5", integerError.Message);
        }

        [Test]
        public void RaycastLengthRefusalText_IsCultureInvariant()
        {
            RbxWorldPhysics physics = new(new InstanceRegistry());
            RbxError error = null;
            RunUnderCommaDecimalCulture(() =>
            {
                error = Assert.Throws<RbxError>(() => physics.Raycast(
                    new RbxVector3(0f, 0f, 0f), new RbxVector3(15000.5f, 0f, 0f), null));
            });

            Assert.AreEqual(RbxErrorCode.BadArgument, error.Code);
            StringAssert.Contains("direction length 15000.5 studs", error.RawMessage,
                "the refused length must read the same under every host locale");
            StringAssert.Contains("maximum of 15000 studs", error.RawMessage);
            StringAssert.DoesNotContain("15000,5", error.RawMessage);
        }

#if UNITY_5_3_OR_NEWER
        [Test]
        public void RbxSpaceScaleConflictText_IsCultureInvariant()
        {
            RbxSpace.ResetForTests();
            try
            {
                RbxSpace.Configure(1.5f);
                InvalidOperationException error = null;
                RunUnderCommaDecimalCulture(() =>
                {
                    error = Assert.Throws<InvalidOperationException>(() => RbxSpace.Configure(0.28f));
                });

                StringAssert.Contains("configured to 1.5 m/stud", error.Message,
                    "the configured scale must read the same under every host locale");
                StringAssert.DoesNotContain("1,5", error.Message);
            }
            finally
            {
                RbxSpace.ResetForTests();
            }
        }
#endif

        /// <summary>
        /// Runs <paramref name="action"/> with a current culture whose decimal separator is a
        /// comma and restores the previous culture afterwards, whatever the action does.
        /// </summary>
        private static void RunUnderCommaDecimalCulture(Action action)
        {
            CultureInfo saved = CultureInfo.CurrentCulture;
            try
            {
                CultureInfo.CurrentCulture = CommaDecimalCulture();
                Assert.AreEqual(",", CultureInfo.CurrentCulture.NumberFormat.NumberDecimalSeparator,
                    "the check is only meaningful under a comma-decimal culture");
                action();
            }
            finally
            {
                CultureInfo.CurrentCulture = saved;
            }
        }

        /// <summary>
        /// de-DE where the runtime carries culture data; otherwise a clone of the invariant
        /// culture with a comma decimal separator, so the check never silently runs under '.'.
        /// </summary>
        private static CultureInfo CommaDecimalCulture()
        {
            try
            {
                CultureInfo german = CultureInfo.GetCultureInfo("de-DE");
                if (german.NumberFormat.NumberDecimalSeparator == ",")
                {
                    return german;
                }
            }
            catch (CultureNotFoundException)
            {
                // WHY: a runtime in invariant-globalization mode has no de-DE data; the clone
                // below still yields a comma culture.
            }

            CultureInfo comma = (CultureInfo)new CultureInfo("").Clone();
            comma.NumberFormat.NumberDecimalSeparator = ",";
            return comma;
        }
    }
}
