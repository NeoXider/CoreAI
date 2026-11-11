using System;
using System.Collections.Generic;
using CoreAI.Mods.Rbx.Datatypes;
using CoreAI.Mods.Rbx.Instances;
using CoreAI.Mods.Rbx.Instances.Scheduling;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode.RbxApi.Instances
{
    /// <summary>
    /// P9's day-cycle slice and PointLight, engine-free: the <c>Lighting</c> defaults of
    /// <c>Full-API-Dump.json</c>, one clock behind <c>ClockTime</c>/<c>TimeOfDay</c>/minutes, the sun
    /// model, the replaceable sink (with a fake), the durable state, the light mapping, PointLight's
    /// members and the light reconciler with its budget (with a fake backend). Each behaviour has its
    /// negative twin.
    /// </summary>
    [TestFixture]
    public sealed class P9LightingEngineFreeEditModeTests
    {
        private InstanceRegistry _registry;
        private RbxDataModel _game;
        private RbxLighting _lighting;
        private List<string> _diagnostics;

        [SetUp]
        public void SetUp()
        {
            _registry = new InstanceRegistry();
            _diagnostics = new List<string>();
            _registry.Diagnostics = _diagnostics.Add;
            _game = DataModelBootstrap.CreateGame(_registry);
            _lighting = (RbxLighting)_game.GetService("Lighting");
        }

        private sealed class RecordingSink : IRbxLightingSink
        {
            public readonly List<RbxLightingState> States = new();

            public void Apply(in RbxLightingState state)
            {
                States.Add(state);
            }
        }

        private sealed class ThrowingSink : IRbxLightingSink
        {
            public int Calls;

            public void Apply(in RbxLightingState state)
            {
                Calls++;
                throw new InvalidOperationException("renderer gone");
            }
        }

        private sealed class FakeBackend : IRbxLightBackend
        {
            public readonly Dictionary<InstanceId, (InstanceId Host, RbxLightRenderState State)> Drawn = new();
            public int ApplyCalls;
            public int ReleaseCalls;

            public void Apply(InstanceId lightId, InstanceId hostPartId, in RbxLightRenderState state)
            {
                ApplyCalls++;
                Drawn[lightId] = (hostPartId, state);
            }

            public void Release(InstanceId lightId)
            {
                ReleaseCalls++;
                Drawn.Remove(lightId);
            }

            public bool Holds(InstanceId lightId)
            {
                return Drawn.ContainsKey(lightId);
            }
        }

        // ---- Lighting defaults and the clock --------------------------------------------------

        [Test]
        public void P9_Lighting_DefaultsEqualTheFullApiDump()
        {
            Assert.IsInstanceOf<RbxLighting>(_lighting, "the Lighting service is the day-cycle behaviour class");
            Assert.AreEqual(14f, _lighting.ClockTime);
            Assert.AreEqual("14:00:00", _lighting.TimeOfDay);
            Assert.AreEqual(1f, _lighting.Brightness);
            Assert.AreEqual(41.7332993f, _lighting.GeographicLatitude);
            Assert.AreEqual(new RbxColor3(0.5f, 0.5f, 0.5f), _lighting.Ambient);
            Assert.AreEqual(new RbxColor3(0.5f, 0.5f, 0.5f), _lighting.OutdoorAmbient);
            Assert.AreEqual(840d, _lighting.GetMinutesAfterMidnight(), 1e-9);
            Assert.IsFalse(_lighting.IsDriven, "a fresh world leaves the host scene's lighting alone");
        }

        [Test]
        public void P9_Lighting_TimeOfDayAndClockTimeAreOneClock()
        {
            _lighting.SetTimeOfDay("06:30:00");
            Assert.AreEqual(6.5f, _lighting.ClockTime);
            _lighting.SetTimeOfDay("21:30");
            Assert.AreEqual(21.5f, _lighting.ClockTime);
            Assert.AreEqual("21:30:00", _lighting.TimeOfDay);
            _lighting.SetTimeOfDay(" 7 ");
            Assert.AreEqual("07:00:00", _lighting.TimeOfDay);
            _lighting.SetClockTime(13.25d);
            Assert.AreEqual("13:15:00", _lighting.TimeOfDay);
            _lighting.SetTimeOfDay("25:00:00");
            Assert.AreEqual(1f, _lighting.ClockTime, "past midnight lands in the next day");
            _lighting.SetClockTime(-1d);
            Assert.AreEqual(23f, _lighting.ClockTime);
            _lighting.SetClockTime(24d);
            Assert.AreEqual(0f, _lighting.ClockTime);
            Assert.AreEqual("00:00:00", _lighting.TimeOfDay);
        }

        [Test]
        public void P9_Lighting_SetMinutesAfterMidnight_RoundTrips_AndWrapsPastADay()
        {
            _lighting.SetMinutesAfterMidnight(390d);
            Assert.AreEqual(390d, _lighting.GetMinutesAfterMidnight(), 1e-4);
            Assert.AreEqual(6.5f, _lighting.ClockTime);
            Assert.AreEqual("06:30:00", _lighting.TimeOfDay);
            _lighting.SetMinutesAfterMidnight(1440d + 60d);
            Assert.AreEqual(60d, _lighting.GetMinutesAfterMidnight(), 1e-4);
            _lighting.SetMinutesAfterMidnight(-30d);
            Assert.AreEqual("23:30:00", _lighting.TimeOfDay);
        }

        [Test]
        public void P9_Lighting_RefusesAMalformedTimeAndANonFiniteValue_NegativeTwin()
        {
            foreach (string bad in new[] { "", "noon", "12:-5", "1:2:3:4", "12::00", "6.5", null })
            {
                RbxError error = Assert.Throws<RbxError>(() => _lighting.SetTimeOfDay(bad), bad ?? "null");
                Assert.AreEqual(RbxErrorCode.BadArgument, error.Code);
            }

            Assert.Throws<RbxError>(() => _lighting.SetClockTime(double.NaN));
            Assert.Throws<RbxError>(() => _lighting.SetMinutesAfterMidnight(double.PositiveInfinity));
            Assert.Throws<RbxError>(() => _lighting.SetBrightness(double.NaN));
            Assert.Throws<RbxError>(() => _lighting.SetAmbient(new RbxColor3(float.NaN, 0f, 0f)));
            Assert.AreEqual(14f, _lighting.ClockTime, "a refused write leaves the clock alone");
            Assert.IsFalse(_lighting.IsDriven);
        }

        // ---- the sun -------------------------------------------------------------------------

        [Test]
        public void P9_Sun_MorningAndEveningAreLowerThanNoon_AndMidnightIsBelowTheHorizon()
        {
            float latitude = RbxLighting.DefaultGeographicLatitude;
            RbxVector3 six = RbxSunModel.SunDirection(6d, latitude);
            RbxVector3 noon = RbxSunModel.SunDirection(12d, latitude);
            RbxVector3 eighteen = RbxSunModel.SunDirection(18d, latitude);
            RbxVector3 midnight = RbxSunModel.SunDirection(0d, latitude);

            Assert.Less(six.Y, noon.Y);
            Assert.Less(eighteen.Y, noon.Y);
            Assert.Less(midnight.Y, 0f, "the sun is below the horizon at midnight");
            Assert.AreEqual(90d - latitude, RbxSunModel.ElevationDegrees(noon), 1e-3,
                "noon elevation is 90 minus the latitude at the equinox");
            Assert.AreEqual(1f, six.X, 1e-5f, "06:00 is sunrise due east (+X)");
            Assert.AreEqual(0f, six.Y, 1e-5f);
            Assert.AreEqual(-1f, eighteen.X, 1e-5f, "18:00 is sunset due west");
            Assert.Greater(noon.Z, 0f, "a northern noon sun leans south, towards +Z");
            foreach (RbxVector3 direction in new[] { six, noon, eighteen, midnight })
            {
                Assert.AreEqual(1f, direction.Magnitude, 1e-5f, "a unit vector");
            }
        }

        [Test]
        public void P9_GetSunDirection_FollowsClockTimeAndLatitude()
        {
            _lighting.SetClockTime(9d);
            _lighting.SetGeographicLatitude(0d);
            Assert.AreEqual(RbxSunModel.SunDirection(9d, 0d), _lighting.GetSunDirection());
            RbxVector3 equator = _lighting.GetSunDirection();
            _lighting.SetGeographicLatitude(60d);
            Assert.Less(_lighting.GetSunDirection().Y, equator.Y, "a higher latitude gives a lower sun");
            Assert.AreEqual(_lighting.GetSunDirection(), _lighting.CaptureState().SunDirection);
        }

        // ---- the sink ------------------------------------------------------------------------

        [Test]
        public void P9_Sink_IsCalledOnlyOnceTheWorldDrivesItsLighting_AndAnEqualWriteCallsNothing()
        {
            RecordingSink sink = new();
            _lighting.AttachSink(sink);
            Assert.AreEqual(0, sink.States.Count, "attaching to an undriven world draws nothing");

            _lighting.SetClockTime(14d);
            Assert.AreEqual(0, sink.States.Count, "an equal write is not a change");
            Assert.IsFalse(_lighting.IsDriven);

            long revision = Revision(_lighting);
            _lighting.SetClockTime(0d);
            Assert.IsTrue(_lighting.IsDriven);
            Assert.AreEqual(1, sink.States.Count);
            Assert.AreEqual(0f, sink.States[0].ClockTime);
            Assert.Less(sink.States[0].SunDirection.Y, 0f);
            Assert.Greater(Revision(_lighting), revision);

            _lighting.SetClockTime(0d);
            _lighting.SetBrightness(1d);
            _lighting.SetAmbient(RbxLighting.DefaultAmbient);
            Assert.AreEqual(1, sink.States.Count, "equal writes call nothing");

            _lighting.SetBrightness(2d);
            _lighting.SetOutdoorAmbient(new RbxColor3(0.2f, 0.3f, 0.4f));
            Assert.AreEqual(3, sink.States.Count);
            Assert.AreEqual(2f, sink.States[2].Brightness);
            Assert.AreEqual(new RbxColor3(0.2f, 0.3f, 0.4f), sink.States[2].OutdoorAmbient);
        }

        [Test]
        public void P9_Sink_IsReplaceable_AndAnAttachToADrivenWorldPushesAtOnce()
        {
            RecordingSink first = new();
            RecordingSink second = new();
            _lighting.AttachSink(first);
            _lighting.SetClockTime(8d);
            _lighting.AttachSink(second);
            Assert.AreEqual(1, second.States.Count, "the replacement draws the current state at once");
            _lighting.SetClockTime(9d);
            Assert.AreEqual(1, first.States.Count, "the replaced sink hears nothing more");
            Assert.AreEqual(2, second.States.Count);
            _lighting.AttachSink(null);
            _lighting.SetClockTime(10d);
            Assert.AreEqual(2, second.States.Count, "a detached world draws nothing");
        }

        [Test]
        public void P9_Sink_AFailingSinkDoesNotFailTheWrite_AndIsReportedOnce()
        {
            ThrowingSink sink = new();
            _lighting.AttachSink(sink);
            Assert.DoesNotThrow(() => _lighting.SetClockTime(3d));
            Assert.DoesNotThrow(() => _lighting.SetClockTime(4d));
            Assert.AreEqual(4f, _lighting.ClockTime);
            Assert.AreEqual(2, sink.Calls);
            Assert.AreEqual(1, _diagnostics.Count, "reported once per attached sink");
            StringAssert.Contains("renderer gone", _diagnostics[0]);
        }

        [Test]
        public void P9_DurableState_RoundTrips_AndRestoreDrivesTheWorld()
        {
            _lighting.SetClockTime(19.5d);
            _lighting.SetBrightness(3d);
            _lighting.SetGeographicLatitude(10d);
            _lighting.SetAmbient(new RbxColor3(0.1f, 0.1f, 0.2f));
            _lighting.SetOutdoorAmbient(new RbxColor3(0.3f, 0.3f, 0.5f));
            RbxLightingDurableState saved = _lighting.CaptureDurableState();

            InstanceRegistry otherRegistry = new();
            RbxLighting other = (RbxLighting)DataModelBootstrap.CreateGame(otherRegistry).GetService("Lighting");
            RecordingSink sink = new();
            other.AttachSink(sink);
            other.RestoreDurableState(saved);

            Assert.IsTrue(other.IsDriven);
            Assert.AreEqual(19.5f, other.ClockTime);
            Assert.AreEqual("19:30:00", other.TimeOfDay);
            Assert.AreEqual(3f, other.Brightness);
            Assert.AreEqual(10f, other.GeographicLatitude);
            Assert.AreEqual(new RbxColor3(0.1f, 0.1f, 0.2f), other.Ambient);
            Assert.AreEqual(new RbxColor3(0.3f, 0.3f, 0.5f), other.OutdoorAmbient);
            Assert.AreEqual(_lighting.CaptureState(), sink.States[sink.States.Count - 1]);

            RbxLightingDurableState defaults = RbxLightingDurableState.Defaults;
            Assert.AreEqual(14f, defaults.ClockTime);
            Assert.AreEqual(RbxLighting.DefaultOutdoorAmbient, defaults.OutdoorAmbient);
        }

        [Test]
        public void P9_DurableState_RestoreThenCapture_KeepsTheStateAndTheRevision()
        {
            RbxLightingDurableState saved = new(19.5f, 10f, 3f, new RbxColor3(0.1f, 0.1f, 0.2f),
                new RbxColor3(0.3f, 0.3f, 0.5f));
            RecordingSink sink = new();
            _lighting.AttachSink(sink);
            long revision = Revision(_lighting);

            _lighting.RestoreDurableState(saved);

            Assert.AreEqual(saved, _lighting.CaptureDurableState(), "a restore reproduces the saved block");
            Assert.AreEqual(revision, Revision(_lighting),
                "a restore is not a change: a load followed by a save keeps the revision");
            Assert.IsTrue(_lighting.IsDriven, "a restored lighting block still drives the world");
            Assert.Greater(sink.States.Count, 0, "the restored state still reaches the sink");
            Assert.AreEqual(19.5f, sink.States[sink.States.Count - 1].ClockTime);
        }

        [Test]
        public void P9_EnvironmentScales_ReachTheSink_TheDurableState_AndClamp()
        {
            RecordingSink sink = new();
            _lighting.AttachSink(sink);

            _lighting.SetEnvironmentDiffuseScale(0.25);
            _lighting.SetEnvironmentSpecularScale(7);
            _lighting.SetEnvironmentSpecularScale(0.5);

            Assert.AreEqual(2, sink.States.Count, "7 clamps to the default 1, an equal value, and calls nothing");
            Assert.AreEqual(0.25f, sink.States[1].EnvironmentDiffuseScale, 1e-6f);
            Assert.AreEqual(0.5f, sink.States[1].EnvironmentSpecularScale, 1e-6f);
            RbxLightingDurableState saved = _lighting.CaptureDurableState();
            Assert.AreEqual(0.25f, saved.EnvironmentDiffuseScale, 1e-6f);
            Assert.AreEqual(1f, new RbxLightingDurableState(12f, 0f, 1f, RbxLighting.DefaultAmbient,
                RbxLighting.DefaultOutdoorAmbient).EnvironmentDiffuseScale, "a block saved before the scales reads 1");
            Assert.Throws<RbxError>(() => _lighting.SetEnvironmentDiffuseScale(double.NaN));
        }

        [Test]
        public void P9_Mapping_EnvironmentDiffuseScale_BlendsTheSkyFacingAmbientDownToAmbient()
        {
            RbxColor3 ambient = new(0.1f, 0.1f, 0.1f);
            RbxColor3 outdoor = new(0.5f, 0.5f, 0.5f);
            RbxVector3 noonSun = RbxSunModel.SunDirection(12f, 0f);
            RbxLightingState full = new(12f, 0f, 1f, ambient, outdoor, noonSun, 1f, 1f);
            RbxLightingState none = new(12f, 0f, 1f, ambient, outdoor, noonSun, 0f, 1f);
            RbxLightingState half = new(12f, 0f, 1f, ambient, outdoor, noonSun, 0.5f, 1f);

            Assert.AreEqual(RbxLightingMapping.EffectiveOutdoorAmbient(full), RbxLightingMapping.SkyFacingAmbient(full),
                "at 1 the sky-facing ambient is today's outdoor ambient");
            Assert.AreEqual(ambient, RbxLightingMapping.SkyFacingAmbient(none), "at 0 only Ambient remains");
            Assert.AreEqual(0.3f, RbxLightingMapping.SkyFacingAmbient(half).R, 1e-5f);
        }

        [Test]
        public void P9_DurableState_AScriptWriteAfterTheRestore_AdvancesTheRevision_NegativeTwin()
        {
            _lighting.RestoreDurableState(new RbxLightingDurableState(6f, 10f, 2f, RbxLighting.DefaultAmbient,
                RbxLighting.DefaultOutdoorAmbient));
            long revision = Revision(_lighting);
            ModScheduler scheduler = new(new NoThreadFactory(), new RbxAccumulatingTimeSource());
            List<string> changed = new();
            int clockSignals = 0;
            _lighting.Changed.BindScheduler(scheduler);
            _lighting.Changed.Connect((Action<object[]>)(arguments => changed.Add((string)arguments[0])));
            RbxScriptSignal clock = _lighting.GetPropertyChangedSignal("ClockTime");
            clock.BindScheduler(scheduler);
            clock.Connect((Action<object[]>)(_ => clockSignals++));

            _lighting.SetClockTime(7d);
            scheduler.Advance(0d);

            Assert.Greater(Revision(_lighting), revision, "a script write after the restore is a change");
            Assert.AreEqual(7f, _lighting.CaptureDurableState().ClockTime);
            CollectionAssert.Contains(changed, "ClockTime", "Changed fires with the property name as before");
            Assert.AreEqual(1, clockSignals, "the property signal fires as before");
        }

        private sealed class NoThreadFactory : IRbxScriptThreadFactory
        {
            public IRbxScriptThread Create(string ownerModId, object callable)
            {
                throw new InvalidOperationException("these tests connect plain C# handlers only");
            }
        }

        // ---- the mapping the default sinks share --------------------------------------------

        [Test]
        public void P9_Mapping_NoonIsFullDaylight_MidnightIsDark_AndAmbientStays()
        {
            RbxLightingState noon = new(12f, 41.7f, 2f, new RbxColor3(0.2f, 0.2f, 0.2f),
                new RbxColor3(0.6f, 0.6f, 0.6f), RbxSunModel.SunDirection(12d, 41.7d));
            RbxLightingState midnight = new(0f, 41.7f, 2f, new RbxColor3(0.2f, 0.2f, 0.2f),
                new RbxColor3(0.6f, 0.6f, 0.6f), RbxSunModel.SunDirection(0d, 41.7d));

            Assert.AreEqual(1f, RbxLightingMapping.Daylight(noon));
            Assert.AreEqual(2f, RbxLightingMapping.SunIntensity(noon), "Brightness at noon");
            Assert.AreEqual(0f, RbxLightingMapping.Daylight(midnight));
            Assert.AreEqual(0f, RbxLightingMapping.SunIntensity(midnight));
            Assert.AreEqual(0.6f, RbxLightingMapping.EffectiveOutdoorAmbient(noon).R, 1e-6f);
            Assert.AreEqual(0.6f * RbxLightingMapping.NightOutdoorAmbientFloor,
                RbxLightingMapping.EffectiveOutdoorAmbient(midnight).R, 1e-6f);

            RbxLightingState negative = new(12f, 0f, -5f, RbxColor3.FromRGB(), RbxColor3.FromRGB(),
                RbxSunModel.SunDirection(12d, 0d));
            Assert.AreEqual(0f, RbxLightingMapping.SunIntensity(negative), "a negative Brightness draws no sun");
        }

        // ---- PointLight -----------------------------------------------------------------------

        [Test]
        public void P9_PointLight_DefaultsAndRangeClamp()
        {
            RbxPointLight light = (RbxPointLight)_registry.Create("PointLight");
            Assert.IsTrue(light.IsA("Light"));
            Assert.AreEqual(1f, light.Brightness);
            Assert.AreEqual(new RbxColor3(1f, 1f, 1f), light.Color);
            Assert.IsTrue(light.Enabled);
            Assert.IsFalse(light.Shadows);
            Assert.AreEqual(8f, light.Range);

            light.SetRange(500d);
            Assert.AreEqual(RbxPointLight.MaxRange, light.Range);
            light.SetRange(-3d);
            Assert.AreEqual(0f, light.Range);
            int version = light.StateVersion;
            light.SetRange(0d);
            light.SetEnabled(true);
            Assert.AreEqual(version, light.StateVersion, "equal writes are not changes");
            Assert.Throws<RbxError>(() => light.SetBrightness(double.NaN));
            Assert.Throws<RbxError>(() => light.SetRange(double.NegativeInfinity));
        }

        [Test]
        public void P9_PointLight_CloneCopiesItsState()
        {
            RbxPointLight light = (RbxPointLight)_registry.Create("PointLight");
            light.SetBrightness(4d);
            light.SetColor(new RbxColor3(1f, 0.5f, 0.2f));
            light.SetRange(16d);
            light.SetShadows(true);
            light.SetEnabled(false);
            RbxPointLight copy = (RbxPointLight)light.Clone();

            Assert.AreEqual(4f, copy.Brightness);
            Assert.AreEqual(new RbxColor3(1f, 0.5f, 0.2f), copy.Color);
            Assert.AreEqual(16f, copy.Range);
            Assert.IsTrue(copy.Shadows);
            Assert.IsFalse(copy.Enabled);
        }

        [Test]
        public void P9_Catalog_LightIsAbstract_PointLightIsCreatable_AndTheRestOfP9IsPlanned()
        {
            Assert.IsTrue(_registry.Catalog.TryGet("Light", out ClassDescriptor light));
            Assert.IsTrue(light.IsAbstract);
            Assert.IsTrue(_registry.Catalog.TryGet("PointLight", out ClassDescriptor pointLight));
            Assert.IsTrue(pointLight.IsCreatable);
            Assert.IsFalse(_registry.Catalog.TryGetKnownUnimplementedClass("PointLight", out _));

            Assert.IsTrue(_registry.Catalog.TryGetKnownUnimplementedClass("SpotLight",
                out RbxKnownUnimplementedClassDescriptor spot));
            Assert.AreEqual("P9", spot.Phase);
            Assert.IsTrue(_registry.Catalog.TryGetKnownUnimplementedMember("Lighting", "FogEnd",
                RbxKnownUnimplementedMemberAccess.Read, out _, out RbxKnownUnimplementedMemberDescriptor fog));
            Assert.AreEqual("P9", fog.Phase);
            Assert.IsFalse(_registry.Catalog.TryGetKnownUnimplementedMember("Lighting", "ClockTime",
                RbxKnownUnimplementedMemberAccess.Read, out _, out _), "a shipped member has no stub");
            Assert.IsTrue(_registry.Catalog.TryGetKnownUnimplementedMember("Lighting", "Technology",
                RbxKnownUnimplementedMemberAccess.Write, out _, out RbxKnownUnimplementedMemberDescriptor tech));
            Assert.AreEqual(RbxKnownUnimplementedMemberStatus.Unsupported, tech.Status);
        }

        // ---- the reconciler and its budget -----------------------------------------------------

        private RbxInstance Part(string name, float x, RbxInstance parent = null)
        {
            RbxInstance part = _registry.Create("Part");
            part.Name = name;
            part.Parent = parent ?? _registry.WorldRoot;
            _positions[part.Id] = new RbxVector3(x, 0f, 0f);
            return part;
        }

        private readonly Dictionary<InstanceId, RbxVector3> _positions = new();

        private RbxPointLight Torch(RbxInstance parent)
        {
            RbxPointLight light = (RbxPointLight)_registry.Create("PointLight");
            light.Parent = parent;
            return light;
        }

        private RbxLightReconciler Reconciler(FakeBackend backend, int budget, List<string> notices = null)
        {
            return new RbxLightReconciler(_registry, backend,
                id => _positions.TryGetValue(id, out RbxVector3 position) ? position : new RbxVector3(0f, 0f, 0f),
                new RbxLightBudget("test", budget), notices == null ? null : (Action<string>)notices.Add);
        }

        [Test]
        public void P9_Reconciler_LightsOnlyAPointLightOnAPartInTheWorkspace()
        {
            FakeBackend backend = new();
            RbxInstance part = Part("Torch", 0f);
            RbxPointLight lit = Torch(part);
            RbxPointLight stored = Torch(Part("Stored", 1f, _game.GetService("ReplicatedStorage")));
            RbxPointLight loose = Torch(_registry.WorldRoot);
            using RbxLightReconciler reconciler = Reconciler(backend, 8);
            reconciler.Tick(new RbxVector3(0f, 0f, 0f));

            Assert.IsTrue(backend.Drawn.ContainsKey(lit.Id));
            Assert.AreEqual(part.Id, backend.Drawn[lit.Id].Host);
            Assert.IsTrue(backend.Drawn[lit.Id].State.Active);
            Assert.AreEqual(8f, backend.Drawn[lit.Id].State.RangeStuds);
            Assert.IsFalse(backend.Drawn.ContainsKey(stored.Id), "outside the Workspace a light does not shine");
            Assert.IsFalse(backend.Drawn.ContainsKey(loose.Id), "a light needs a BasePart parent");
            Assert.AreEqual(3, reconciler.TrackedLights);
            Assert.AreEqual(1, reconciler.ActiveLights);
        }

        [Test]
        public void P9_Reconciler_AStillWorldCallsTheBackendOnce_AndAChangeAgain()
        {
            FakeBackend backend = new();
            RbxPointLight light = Torch(Part("Torch", 0f));
            using RbxLightReconciler reconciler = Reconciler(backend, 8);
            reconciler.Tick(new RbxVector3(0f, 0f, 0f));
            reconciler.Tick(new RbxVector3(5f, 0f, 0f));
            Assert.AreEqual(1, backend.ApplyCalls, "an unchanged light is not re-applied");

            light.SetEnabled(false);
            reconciler.Tick(new RbxVector3(0f, 0f, 0f));
            Assert.AreEqual(2, backend.ApplyCalls);
            Assert.IsFalse(backend.Drawn[light.Id].State.Active, "a disabled light is drawn off");

            RbxInstance other = Part("Other", 3f);
            light.Parent = other;
            reconciler.Tick(new RbxVector3(0f, 0f, 0f));
            Assert.AreEqual(other.Id, backend.Drawn[light.Id].Host, "a reparented light follows its new part");

            light.Destroy();
            Assert.IsFalse(backend.Drawn.ContainsKey(light.Id), "a destroyed light is released");
            Assert.AreEqual(0, reconciler.TrackedLights);
        }

        [Test]
        public void P9_Reconciler_OverBudget_KeepsTheNearestOn_AndSaysSoOnce()
        {
            FakeBackend backend = new();
            List<string> notices = new();
            List<RbxPointLight> lights = new();
            for (int index = 0; index < 6; index++)
            {
                lights.Add(Torch(Part("Torch" + index, index * 10f)));
            }

            using RbxLightReconciler reconciler = Reconciler(backend, 2, notices);
            reconciler.Tick(new RbxVector3(41f, 0f, 0f));

            Assert.AreEqual(2, reconciler.ActiveLights);
            Assert.AreEqual(4, reconciler.CulledByBudget);
            Assert.IsTrue(backend.Drawn[lights[4].Id].State.Active, "40 studs away: nearest");
            Assert.IsTrue(backend.Drawn[lights[5].Id].State.Active, "50 studs away: second nearest");
            Assert.IsFalse(backend.Drawn[lights[0].Id].State.Active, "the far lights are switched off");
            Assert.AreEqual(1, notices.Count);
            StringAssert.Contains("2 of 6 lights are on", notices[0]);
            StringAssert.Contains("keeps the nearest 2", notices[0]);

            reconciler.Tick(new RbxVector3(1f, 0f, 0f));
            Assert.IsTrue(backend.Drawn[lights[0].Id].State.Active, "the budget follows the viewer");
            Assert.IsFalse(backend.Drawn[lights[5].Id].State.Active);
            Assert.AreEqual(1, notices.Count, "the diagnostic is not repeated while the overflow lasts");
        }

        [Test]
        public void P9_Reconciler_UnderBudget_NeverReadsPositions_AndDisposeReleasesEverything()
        {
            FakeBackend backend = new();
            Torch(Part("A", 0f));
            Torch(Part("B", 1f));
            int positionReads = 0;
            RbxLightReconciler reconciler = new(_registry, backend, _ =>
            {
                positionReads++;
                return new RbxVector3(0f, 0f, 0f);
            }, RbxLightBudget.WebGl);
            reconciler.Tick(new RbxVector3(0f, 0f, 0f));
            Assert.AreEqual(0, positionReads, "under the budget no light is ranked");
            Assert.AreEqual(2, backend.Drawn.Count);

            reconciler.Dispose();
            Assert.AreEqual(0, backend.Drawn.Count);
            Torch(Part("C", 2f));
            reconciler.Tick(new RbxVector3(0f, 0f, 0f));
            Assert.AreEqual(0, backend.Drawn.Count, "a disposed reconciler draws nothing");
        }

        [Test]
        public void P9_LightBudget_TierDefaults()
        {
            Assert.AreEqual(64, RbxLightBudget.Desktop.MaxActiveLights);
            Assert.AreEqual(8, RbxLightBudget.WebGl.MaxActiveLights);
            Assert.AreEqual(0, new RbxLightBudget("x", -4).MaxActiveLights);
        }

        private long Revision(RbxInstance instance)
        {
            Assert.IsTrue(_registry.TryGetRecord(instance.Id, out InstanceRecord record));
            return record.Revision;
        }
    }
}
