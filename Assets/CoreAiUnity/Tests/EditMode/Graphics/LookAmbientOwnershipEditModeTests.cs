using System;
using CoreAI.Look;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode
{
    /// <summary>
    /// Engine-free contract of <see cref="LookAmbientOwnership"/>: claims nest, <see cref="LookAmbientOwnership.Changed"/>
    /// fires on the first claim and on the last release only, and a handle releases once. Linked into the
    /// portable suite, so it must never touch UnityEngine.
    /// </summary>
    public sealed class LookAmbientOwnershipEditModeTests
    {
        [Test]
        public void Claims_Nest_AndChangedFiresOnlyAtTheEdges()
        {
            LookAmbientOwnership ownership = new();
            int changes = 0;
            ownership.Changed += () => changes++;

            IDisposable dayCycle = ownership.Claim("day cycle");
            IDisposable weather = ownership.Claim("weather");

            Assert.IsTrue(ownership.IsClaimed);
            Assert.AreEqual(1, changes, "only the first claim changes the owner");
            CollectionAssert.AreEqual(new[] { "day cycle", "weather" }, ownership.Owners);

            dayCycle.Dispose();
            Assert.IsTrue(ownership.IsClaimed, "one claim is still held");
            Assert.AreEqual(1, changes);

            weather.Dispose();
            Assert.IsFalse(ownership.IsClaimed);
            Assert.AreEqual(2, changes, "the last release gives the ambient back");
        }

        [Test]
        public void DisposingAHandleTwice_ReleasesItOnce()
        {
            LookAmbientOwnership ownership = new();
            IDisposable first = ownership.Claim("day cycle");
            ownership.Claim("weather");

            first.Dispose();
            first.Dispose();

            Assert.IsTrue(ownership.IsClaimed, "the second dispose must not release the other claim");
            CollectionAssert.AreEqual(new[] { "weather" }, ownership.Owners);
        }

        [Test]
        public void SkyReflectionScale_StartsAtOne_ClampsBelowZero_AndRaisesOnlyOnAChange()
        {
            LookAmbientOwnership ownership = new();
            int changes = 0;
            ownership.SkyReflectionScaleChanged += () => changes++;

            Assert.AreEqual(1f, ownership.SkyReflectionScale);
            ownership.SetSkyReflectionScale(1f);
            Assert.AreEqual(0, changes, "an equal value is not a change");

            ownership.SetSkyReflectionScale(0.25f);
            ownership.SetSkyReflectionScale(-3f);
            ownership.SetSkyReflectionScale(float.NaN);

            Assert.AreEqual(0f, ownership.SkyReflectionScale);
            Assert.AreEqual(2, changes, "0.25, then 0; NaN is 0 as well and changes nothing");
            Assert.IsFalse(ownership.IsClaimed, "the scale is not a claim");
        }

        [Test]
        public void FogClaims_Nest_AndFogChangedFiresOnlyAtTheEdges()
        {
            LookAmbientOwnership ownership = new();
            int fogChanges = 0;
            ownership.FogChanged += () => fogChanges++;

            IDisposable world = ownership.ClaimFog("world fog");
            IDisposable weather = ownership.ClaimFog("weather");
            world.Dispose();
            world.Dispose();

            Assert.IsTrue(ownership.IsFogClaimed, "one fog claim is still held");
            CollectionAssert.AreEqual(new[] { "weather" }, ownership.FogOwners);
            Assert.AreEqual(1, fogChanges);
            weather.Dispose();
            Assert.IsFalse(ownership.IsFogClaimed);
            Assert.AreEqual(2, fogChanges, "the last release gives the fog back");
            Assert.Throws<ArgumentException>(() => ownership.ClaimFog(""));
        }

        [Test]
        public void TheFogAndTheAmbient_AreClaimedSeparately_NegativeTwin()
        {
            LookAmbientOwnership ownership = new();
            int ambientChanges = 0;
            int fogChanges = 0;
            ownership.Changed += () => ambientChanges++;
            ownership.FogChanged += () => fogChanges++;

            using (ownership.Claim("day cycle"))
            {
                Assert.IsTrue(ownership.IsClaimed);
                Assert.IsFalse(ownership.IsFogClaimed, "a day cycle that drives the ambient does not own the fog");
            }

            using (ownership.ClaimFog("world fog"))
            {
                Assert.IsTrue(ownership.IsFogClaimed);
                Assert.IsFalse(ownership.IsClaimed, "a world's fog does not own the ambient");
            }

            Assert.AreEqual(2, ambientChanges);
            Assert.AreEqual(2, fogChanges);
        }

        [Test]
        public void AClaimNeedsAnOwner()
        {
            LookAmbientOwnership ownership = new();

            Assert.Throws<ArgumentException>(() => ownership.Claim(""));
            Assert.Throws<ArgumentException>(() => ownership.Claim(null));
            Assert.IsFalse(ownership.IsClaimed);
        }
    }
}
