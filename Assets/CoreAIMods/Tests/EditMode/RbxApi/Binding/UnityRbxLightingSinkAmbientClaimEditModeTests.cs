using System.Collections.Generic;
using CoreAI.Look;
using CoreAI.Mods.Rbx.Binding;
using CoreAI.Mods.Rbx.Datatypes;
using CoreAI.Mods.Rbx.Instances;
using CoreAI.Mods.Rbx.Spatial;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace CoreAI.Tests.EditMode.RbxApi.Binding
{
    /// <summary>
    /// The scene lighting sink and the look share the ambient through <see cref="LookAmbientOwnership"/>: the sink
    /// claims it while the world drives its lighting (before it writes the ambient), holds one claim however often
    /// it applies, and releases it at the end of <see cref="UnityRbxLightingSink.Revert"/>, after the scene is
    /// restored, so the look puts its own ambient back last. Each test passes its own ownership instance.
    /// </summary>
    [TestFixture]
    public sealed class UnityRbxLightingSinkAmbientClaimEditModeTests
    {
        private static readonly Color Sentinel = new(0.21f, 0.32f, 0.43f, 1f);

        private GameObject _sunGo;
        private Light _sun;
        private Light _previousSun;
        private AmbientMode _previousMode;
        private Color _previousSky;
        private Color _previousEquator;
        private Color _previousGround;
        private LookAmbientOwnership _ownership;
        private List<bool> _changes;

        [SetUp]
        public void SetUp()
        {
            RbxSpace.ResetForTests();
            _previousSun = RenderSettings.sun;
            _previousMode = RenderSettings.ambientMode;
            _previousSky = RenderSettings.ambientSkyColor;
            _previousEquator = RenderSettings.ambientEquatorColor;
            _previousGround = RenderSettings.ambientGroundColor;
            _sunGo = new GameObject("Ambient claim sun");
            _sun = _sunGo.AddComponent<Light>();
            _sun.type = LightType.Directional;
            RenderSettings.ambientMode = AmbientMode.Flat;
            RenderSettings.ambientSkyColor = Sentinel;
            _ownership = new LookAmbientOwnership();
            _changes = new List<bool>();
        }

        [TearDown]
        public void TearDown()
        {
            Object.DestroyImmediate(_sunGo);
            RenderSettings.sun = _previousSun;
            RenderSettings.ambientMode = _previousMode;
            RenderSettings.ambientSkyColor = _previousSky;
            RenderSettings.ambientEquatorColor = _previousEquator;
            RenderSettings.ambientGroundColor = _previousGround;
            RbxSpace.ResetForTests();
        }

        private static RbxLightingState State(float clockTime)
        {
            return new RbxLightingState(clockTime, 41.7f, 2f, new RbxColor3(0.1f, 0.1f, 0.1f),
                new RbxColor3(0.6f, 0.6f, 0.6f), RbxSunModel.SunDirection(clockTime, 41.7d));
        }

        private UnityRbxLightingSink Sink()
        {
            return new UnityRbxLightingSink(_sun, _ownership);
        }

        private void RecordSkyOnChange()
        {
            _ownership.Changed += () =>
            {
                _changes.Add(_ownership.IsClaimed);
                SkyAtChange = RenderSettings.ambientSkyColor;
            };
        }

        private Color SkyAtChange { get; set; }

        private static void AssertSentinel(Color actual, string message)
        {
            Assert.AreEqual(Sentinel.r, actual.r, 1e-5f, message);
            Assert.AreEqual(Sentinel.g, actual.g, 1e-5f, message);
            Assert.AreEqual(Sentinel.b, actual.b, 1e-5f, message);
        }

        [Test]
        public void Apply_ClaimsTheAmbientWhileTheWorldDrivesIt_BeforeWritingIt()
        {
            RecordSkyOnChange();
            UnityRbxLightingSink sink = Sink();
            RbxLightingState noon = State(12f);

            sink.Apply(in noon);

            Assert.IsTrue(_ownership.IsClaimed);
            Assert.IsTrue(sink.ClaimsAmbient);
            CollectionAssert.AreEqual(new[] { UnityRbxLightingSink.AmbientOwnerName }, _ownership.Owners);
            CollectionAssert.AreEqual(new[] { true }, _changes);
            AssertSentinel(SkyAtChange, "the claim is taken before the sink writes the ambient");
            Assert.AreEqual(AmbientMode.Trilight, RenderSettings.ambientMode);
            Assert.AreEqual(0.6f, RenderSettings.ambientSkyColor.r, 1e-5f);
        }

        [Test]
        public void SecondApplyAndReapply_DoNotClaimTwice()
        {
            RecordSkyOnChange();
            UnityRbxLightingSink sink = Sink();
            RbxLightingState noon = State(12f);
            RbxLightingState evening = State(18.5f);

            sink.Apply(in noon);
            sink.Apply(in evening);
            sink.Reapply();

            Assert.AreEqual(1, _ownership.Owners.Count);
            CollectionAssert.AreEqual(new[] { true }, _changes, "one claim, one change");
        }

        [Test]
        public void Revert_RestoresTheSceneFirst_AndReleasesTheClaimLast()
        {
            UnityRbxLightingSink sink = Sink();
            RbxLightingState noon = State(12f);
            sink.Apply(in noon);
            RecordSkyOnChange();

            sink.Revert();

            Assert.IsFalse(_ownership.IsClaimed);
            Assert.IsFalse(sink.ClaimsAmbient);
            CollectionAssert.AreEqual(new[] { false }, _changes);
            AssertSentinel(SkyAtChange, "the look takes the ambient back after the sink restored it");
            Assert.AreEqual(AmbientMode.Flat, RenderSettings.ambientMode);

            sink.Apply(in noon);
            Assert.IsTrue(_ownership.IsClaimed, "a later apply claims again");
        }

        [Test]
        public void Revert_WithoutAnApply_ReleasesNothing_AndLeavesAnotherOwnersClaim_NegativeTwin()
        {
            System.IDisposable other = _ownership.Claim("other day cycle");
            RecordSkyOnChange();
            UnityRbxLightingSink sink = Sink();

            sink.Revert();
            Assert.IsTrue(_ownership.IsClaimed);

            RbxLightingState noon = State(12f);
            sink.Apply(in noon);
            sink.Revert();

            Assert.IsTrue(_ownership.IsClaimed, "the sink releases only its own claim");
            CollectionAssert.AreEqual(new[] { "other day cycle" }, _ownership.Owners);
            Assert.IsEmpty(_changes, "the ambient never changed hands");
            other.Dispose();
        }

        [Test]
        public void DefaultSink_ClaimsTheSharedOwnership_TheDefaultLookReads()
        {
            UnityRbxLightingSink sink = new(_sun);

            Assert.AreSame(LookAmbientOwnership.Shared, sink.AmbientOwnership);
            Assert.IsFalse(sink.ClaimsAmbient, "nothing is claimed before the world drives its lighting");
        }
    }
}
