using System;
using System.Collections.Generic;
using CoreAI.Mods.Rbx.Datatypes;
using CoreAI.Mods.Rbx.Instances;
using CoreAI.Mods.Rbx.Instances.Scheduling;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode.RbxApi.Instances
{
    /// <summary>
    /// MVP17's minimal ParticleEmitter and the datatypes it takes, engine-free: the <c>Full-API-Dump.json</c> defaults
    /// (and their dump text), NumberRange, NumberSequence and ColorSequence with the keypoint rules of their yaml, the
    /// emitter's setters, <c>Emit</c>/<c>Clear</c> as queued commands, and the reconciler with a fake backend (drawn
    /// only from a part in the Workspace, commands dropped elsewhere, re-applied only on a change, the budget). Each
    /// behaviour has its negative twin.
    /// </summary>
    [TestFixture]
    public sealed class Mvp17ParticleEngineFreeEditModeTests
    {
        private InstanceRegistry _registry;
        private RbxDataModel _game;

        [SetUp]
        public void SetUp()
        {
            _registry = new InstanceRegistry();
            _game = DataModelBootstrap.CreateGame(_registry);
        }

        private sealed class NoThreadFactory : IRbxScriptThreadFactory
        {
            public IRbxScriptThread Create(string ownerModId, object callable)
            {
                throw new InvalidOperationException("these tests connect plain C# handlers only");
            }
        }

        private sealed class FakeBackend : IRbxParticleBackend
        {
            public readonly Dictionary<InstanceId, (InstanceId Host, RbxParticleRenderState State)> Drawn = new();
            public readonly List<string> Calls = new();
            public int ApplyCalls;

            public void Apply(InstanceId emitterId, InstanceId hostPartId, in RbxParticleRenderState state)
            {
                ApplyCalls++;
                Drawn[emitterId] = (hostPartId, state);
            }

            public void Emit(InstanceId emitterId, int count)
            {
                Calls.Add("emit " + count);
            }

            public void Clear(InstanceId emitterId)
            {
                Calls.Add("clear");
            }

            public void Release(InstanceId emitterId)
            {
                Calls.Add("release");
                Drawn.Remove(emitterId);
            }

            public bool Holds(InstanceId emitterId)
            {
                return Drawn.ContainsKey(emitterId);
            }
        }

        private RbxParticleEmitter NewEmitter(RbxInstance parent)
        {
            RbxParticleEmitter emitter = (RbxParticleEmitter)_registry.Create("ParticleEmitter");
            emitter.Parent = parent;
            return emitter;
        }

        private RbxInstance NewPart(string name)
        {
            RbxInstance part = _registry.Create("Part");
            part.Name = name;
            part.Parent = _registry.WorldRoot;
            return part;
        }

        private RbxParticleReconciler Reconciler(FakeBackend backend, int budget = 64, List<string> notices = null,
            Dictionary<InstanceId, RbxVector3> positions = null)
        {
            return new RbxParticleReconciler(_registry, backend,
                id => positions != null && positions.TryGetValue(id, out RbxVector3 p) ? p : new RbxVector3(0f, 0f, 0f),
                _ => new RbxVector3(4f, 1f, 2f), new RbxParticleBudget("test", budget, 100),
                notices == null ? null : (Action<string>)notices.Add);
        }

        // ---- datatypes ---------------------------------------------------------------------------

        [Test]
        public void MVP17_NumberRange_TakesOneOrTwoBounds_AndRefusesAMinimumAboveTheMaximum()
        {
            RbxNumberRange single = new(3f);
            Assert.AreEqual(3f, single.Min);
            Assert.AreEqual(3f, single.Max);
            Assert.AreEqual("5 10 ", new RbxNumberRange(5f, 10f).ToString(), "the dump's text form");
            Assert.AreEqual(new RbxNumberRange(1f, 2f), new RbxNumberRange(1f, 2f));
            Assert.Throws<ArgumentException>(() => new RbxNumberRange(2f, 1f), "NumberRange.yaml: minimum <= maximum");
            Assert.Throws<ArgumentException>(() => new RbxNumberRange(float.NaN, 1f));
        }

        [Test]
        public void MVP17_Sequences_FollowTheKeypointRules_AndEvaluateLinearly_NegativeTwin()
        {
            RbxNumberSequence fade = new(new[]
            {
                new RbxNumberSequenceKeypoint(0f, 0f), new RbxNumberSequenceKeypoint(0.5f, 1f, 0.25f),
                new RbxNumberSequenceKeypoint(1f, 0f)
            });
            Assert.AreEqual(0.5f, fade.Evaluate(0.25f), 1e-6f);
            Assert.AreEqual(1f, fade.Evaluate(0.5f), 1e-6f);
            Assert.AreEqual(0f, fade.Evaluate(2f), 1e-6f, "time is held to [0, 1]");
            Assert.AreEqual(1f, fade.MaxValue());
            Assert.AreEqual("0 0 0 0.5 1 0.25 1 0 0 ", fade.ToString());

            RbxColorSequence ramp = new(new RbxColor3(1f, 0f, 0f), new RbxColor3(0f, 0f, 1f));
            RbxColor3 middle = ramp.Evaluate(0.5f);
            Assert.AreEqual(0.5f, middle.R, 1e-6f);
            Assert.AreEqual(0.5f, middle.B, 1e-6f);
            Assert.AreEqual("0 1 0 0 0 1 0 0 1 0 ", ramp.ToString());
            Assert.AreEqual(new RbxColorSequence(new RbxColor3(1f, 1f, 1f)),
                new RbxColorSequence(new RbxColor3(1f, 1f, 1f)), "equal keypoints are equal sequences");

            Assert.Throws<ArgumentException>(() => new RbxNumberSequence(new[] { new RbxNumberSequenceKeypoint(0f, 1f) }),
                "at least two keypoints");
            Assert.Throws<ArgumentException>(() => new RbxNumberSequence(new[]
            {
                new RbxNumberSequenceKeypoint(0.1f, 1f), new RbxNumberSequenceKeypoint(1f, 1f)
            }), "the first is at time 0");
            Assert.Throws<ArgumentException>(() => new RbxNumberSequence(new[]
            {
                new RbxNumberSequenceKeypoint(0f, 1f), new RbxNumberSequenceKeypoint(0.9f, 1f)
            }), "the last is at time 1");
            Assert.Throws<ArgumentException>(() => new RbxNumberSequence(new[]
            {
                new RbxNumberSequenceKeypoint(0f, 1f), new RbxNumberSequenceKeypoint(0.7f, 1f),
                new RbxNumberSequenceKeypoint(0.3f, 1f), new RbxNumberSequenceKeypoint(1f, 1f)
            }), "non-descending times");
            Assert.Throws<ArgumentException>(() => new RbxNumberSequenceKeypoint(1.5f, 0f), "time within [0, 1]");
            RbxNumberSequenceKeypoint[] many = new RbxNumberSequenceKeypoint[21];
            for (int index = 0; index < many.Length; index++)
            {
                many[index] = new RbxNumberSequenceKeypoint(index / 20f, 0f);
            }

            Assert.Throws<ArgumentException>(() => new RbxNumberSequence(many), "at most 20 keypoints");
        }

        // ---- the emitter -------------------------------------------------------------------------

        [Test]
        public void MVP17_ParticleEmitter_DefaultsEqualTheFullApiDump()
        {
            RbxParticleEmitter emitter = (RbxParticleEmitter)_registry.Create("ParticleEmitter");

            Assert.AreEqual("ParticleEmitter", emitter.Name);
            Assert.IsTrue(emitter.Enabled);
            Assert.AreEqual(20f, emitter.Rate);
            Assert.AreEqual("5 10 ", emitter.Lifetime.ToString(), "the dump prints Lifetime as '5 10 '");
            Assert.AreEqual("5 5 ", emitter.Speed.ToString());
            Assert.AreEqual("0 1 1 1 0 1 1 1 1 0 ", emitter.Color.ToString(), "the dump's Color default");
            Assert.AreEqual("0 1 0 1 1 0 ", emitter.Size.ToString(), "the dump's Size default");
            Assert.AreEqual("0 0 0 1 0 0 ", emitter.Transparency.ToString(), "the dump's Transparency default");
            Assert.AreEqual("rbxasset://textures/particles/sparkles_main.dds", emitter.Texture);
            Assert.AreEqual(new RbxVector2(0f, 0f), emitter.SpreadAngle);
            Assert.AreEqual(new RbxVector3(0f, 0f, 0f), emitter.Acceleration);
            Assert.AreEqual(RbxParticleFace.Top, emitter.EmissionDirection);
            Assert.IsFalse(_registry.Catalog.TryGetKnownUnimplementedClass("ParticleEmitter", out _),
                "the MVP17 loud stub is gone");
        }

        [Test]
        public void MVP17_ParticleEmitter_SettersClampAndRefuse_AndAnEqualWriteFiresNothing()
        {
            RbxParticleEmitter emitter = (RbxParticleEmitter)_registry.Create("ParticleEmitter");
            ModScheduler scheduler = new(new NoThreadFactory(), new RbxAccumulatingTimeSource());
            List<string> changed = new();
            emitter.Changed.BindScheduler(scheduler);
            emitter.Changed.Connect((Action<object[]>)(arguments => changed.Add((string)arguments[0])));

            emitter.SetRate(-5d);
            emitter.SetRate(-5d);
            emitter.SetLifetime(new RbxNumberRange(-1f, 2f));
            emitter.SetColor(new RbxColorSequence(new RbxColor3(1f, 1f, 1f)));
            emitter.SetEmissionDirection(RbxParticleFace.Bottom);
            scheduler.Advance(0d);

            Assert.AreEqual(0f, emitter.Rate, "a negative rate emits nothing");
            Assert.AreEqual(0f, emitter.Lifetime.Min, "a negative life is held to 0");
            CollectionAssert.AreEqual(new[] { "Rate", "Lifetime", "EmissionDirection" }, changed,
                "the white Color equals the default, so it fired nothing");
            Assert.Throws<RbxError>(() => emitter.SetRate(double.NaN));
            Assert.Throws<RbxError>(() => emitter.SetColor(null));
            Assert.Throws<RbxError>(() => emitter.SetEmissionDirection((RbxParticleFace)9));
            Assert.Throws<RbxError>(() => emitter.SetAcceleration(new RbxVector3(float.NaN, 0f, 0f)));
        }

        [Test]
        public void MVP17_ParticleEmitter_EmitAndClearAreQueuedCommands_HeldToTheCap()
        {
            RbxParticleEmitter emitter = (RbxParticleEmitter)_registry.Create("ParticleEmitter");
            emitter.Emit(5);
            emitter.Emit();
            Assert.AreEqual(21, emitter.PendingEmit, "Emit's default count is 16 (ParticleEmitter.yaml)");
            emitter.Emit(0);
            emitter.Emit(-3);
            Assert.AreEqual(21, emitter.PendingEmit, "a count of zero or less emits nothing");
            emitter.Emit(5000);
            Assert.AreEqual(RbxParticleEmitter.MaxEmitCount, emitter.PendingEmit);

            emitter.Clear();
            Assert.AreEqual(0, emitter.PendingEmit, "Clear drops what was not emitted yet");
            Assert.IsTrue(emitter.PendingClear);
            emitter.TakeCommands(out int emit, out bool clear);
            Assert.AreEqual(0, emit);
            Assert.IsTrue(clear);
            Assert.IsFalse(emitter.PendingClear);
        }

        // ---- the reconciler ----------------------------------------------------------------------

        [Test]
        public void MVP17_Reconciler_DrawsAnEmitterOnAWorkspacePart_AndHandsOverItsBurst()
        {
            FakeBackend backend = new();
            using RbxParticleReconciler reconciler = Reconciler(backend);
            RbxInstance part = NewPart("Fountain");
            RbxParticleEmitter emitter = NewEmitter(part);
            emitter.SetRate(0d);
            emitter.Emit(30);

            reconciler.Tick(new RbxVector3(0f, 0f, 0f));

            Assert.IsTrue(backend.Drawn.TryGetValue(emitter.Id, out (InstanceId Host, RbxParticleRenderState State) drawn));
            Assert.AreEqual(part.Id, drawn.Host);
            Assert.AreEqual(new RbxVector3(4f, 1f, 2f), drawn.State.PartSizeStuds, "particles start in the part's box");
            Assert.AreEqual(0f, drawn.State.Rate);
            CollectionAssert.AreEqual(new[] { "emit 30" }, backend.Calls);

            reconciler.Tick(new RbxVector3(0f, 0f, 0f));
            Assert.AreEqual(1, backend.ApplyCalls, "an unchanged emitter is not applied again");
            emitter.SetRate(10d);
            emitter.Emit(4);
            emitter.Clear();
            emitter.Emit(2);
            reconciler.Tick(new RbxVector3(0f, 0f, 0f));
            Assert.AreEqual(2, backend.ApplyCalls, "a real change is applied once");
            CollectionAssert.AreEqual(new[] { "emit 30", "clear", "emit 2" }, backend.Calls,
                "Clear removes the live particles first, then the burst asked for after it is emitted");
        }

        [Test]
        public void MVP17_Reconciler_AnEmitterOutsideAPart_IsNotDrawn_AndItsBurstIsDropped_NegativeTwin()
        {
            FakeBackend backend = new();
            using RbxParticleReconciler reconciler = Reconciler(backend);
            RbxInstance folder = _registry.Create("Folder");
            folder.Parent = _registry.WorldRoot;
            RbxParticleEmitter inFolder = NewEmitter(folder);
            RbxParticleEmitter loose = NewEmitter(null);
            inFolder.Emit(10);
            loose.Emit(10);

            reconciler.Tick(new RbxVector3(0f, 0f, 0f));

            Assert.AreEqual(0, backend.Drawn.Count, "an emitter emits only from a part (ParticleEmitter.yaml)");
            CollectionAssert.IsEmpty(backend.Calls);
            Assert.AreEqual(0, inFolder.PendingEmit, "the burst is dropped, as Emit on an undrawn emitter does nothing");

            RbxInstance part = NewPart("Later");
            inFolder.Parent = part;
            reconciler.Tick(new RbxVector3(0f, 0f, 0f));
            Assert.IsTrue(backend.Holds(inFolder.Id), "twin: moved into a part it is drawn");
            CollectionAssert.IsEmpty(backend.Calls, "the dropped burst does not come back");

            inFolder.Parent = folder;
            reconciler.Tick(new RbxVector3(0f, 0f, 0f));
            Assert.IsFalse(backend.Holds(inFolder.Id), "moved out of the part it is released");
        }

        [Test]
        public void MVP17_Reconciler_ADestroyedEmitterIsReleasedAtOnce()
        {
            FakeBackend backend = new();
            using RbxParticleReconciler reconciler = Reconciler(backend);
            RbxParticleEmitter emitter = NewEmitter(NewPart("Torch"));
            reconciler.Tick(new RbxVector3(0f, 0f, 0f));
            Assert.IsTrue(backend.Holds(emitter.Id));

            emitter.Destroy();

            Assert.IsFalse(backend.Holds(emitter.Id), "its particles go with it");
            Assert.AreEqual(0, reconciler.TrackedEmitters);
        }

        [Test]
        public void MVP17_Reconciler_OverBudget_TheNearestEmittersAreDrawn_AndTheBudgetIsReportedOnce()
        {
            FakeBackend backend = new();
            List<string> notices = new();
            Dictionary<InstanceId, RbxVector3> positions = new();
            using RbxParticleReconciler reconciler = Reconciler(backend, 2, notices, positions);
            List<RbxParticleEmitter> emitters = new();
            for (int index = 0; index < 4; index++)
            {
                RbxInstance part = NewPart("P" + index);
                positions[part.Id] = new RbxVector3(10f * (4 - index), 0f, 0f);
                emitters.Add(NewEmitter(part));
            }

            reconciler.Tick(new RbxVector3(0f, 0f, 0f));
            reconciler.Tick(new RbxVector3(0f, 0f, 0f));

            Assert.AreEqual(2, reconciler.DrawnEmitters);
            Assert.AreEqual(2, reconciler.CulledByBudget);
            Assert.IsTrue(backend.Holds(emitters[3].Id), "the nearest is drawn");
            Assert.IsTrue(backend.Holds(emitters[2].Id));
            Assert.IsFalse(backend.Holds(emitters[0].Id), "the farthest is not");
            Assert.AreEqual(1, notices.Count);
            StringAssert.Contains("particle budget", notices[0]);
        }
    }
}
