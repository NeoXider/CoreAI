using System;
using System.Collections;
using System.Collections.Generic;
using CoreAI.Mods.Rbx.Binding;
using CoreAI.Mods.Rbx.Datatypes;
using CoreAI.Mods.Rbx.Instances;
using CoreAI.Mods.Rbx.Spatial;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace CoreAI.Tests.PlayMode.RbxApi
{
    /// <summary>
    /// MVP2.5 slice 8.5 gate, engine half (plan §E.1 row P8.3): the real Unity simulation actually
    /// feeds CoreAI's gravity, raycasts and contacts.
    /// </summary>
    /// <remarks>
    /// WHY this exists next to the EditMode gates rather than instead of them: the EditMode file
    /// proves the Roblox RULES against a fake port; nothing there would notice if the adapter never
    /// applied a force, converted metres as studs, or missed every collider. This file proves the
    /// opposite half and nothing else — it asserts numbers that only a running physics engine can
    /// produce.
    /// <para>
    /// The simulation is stepped from script (<c>SimulationMode.Script</c>) so a slow frame cannot
    /// change a measurement: every test advances a fixed, known amount of simulated time.
    /// </para>
    /// </remarks>
    [TestFixture]
    public sealed class Mvp8PhysicsPlayModeTests
    {
        private const float FixedStep = 0.02f;
        private const float WalkSeconds = 1f;

        private SimulationMode _savedSimulationMode;
        private Vector3 _savedHostGravity;
        private Action _restoreScale;
        private PhysicsWorld _world;

        [SetUp]
        public void CreateWorld()
        {
            _savedSimulationMode = Physics.simulationMode;
            _savedHostGravity = Physics.gravity;
            Physics.simulationMode = SimulationMode.Script;
            _world = new PhysicsWorld();
        }

        [TearDown]
        public void DestroyWorld()
        {
            // WHY try/finally: simulationMode, gravity and the RbxSpace scale are process-global, and
            // play mode starts here without a domain reload, so they outlive the session. If Dispose()
            // ever threw before the lines below ran, every later PlayMode test and the developer's
            // next Play would silently inherit a scripted simulation or the wrong world scale.
            try
            {
                _world?.Dispose();
                _world = null;
            }
            finally
            {
                Physics.simulationMode = _savedSimulationMode;
                Physics.gravity = _savedHostGravity;
                _restoreScale?.Invoke();
                _restoreScale = null;
            }
        }

        /// <summary>
        /// Runs the rest of the current test at <paramref name="metersPerStud"/>;
        /// <see cref="DestroyWorld"/> rolls the scale back on every exit path.
        /// </summary>
        /// <remarks>
        /// WHY BeginSessionReplacement: Configure refuses a second value per session and CreateWorld
        /// has already pinned the default, while ResetForTests is internal to CoreAI.Mods.Tests and
        /// invisible from this assembly. The rollback it returns restores scale and configured flag
        /// exactly, which is what a shared static needs.
        /// </remarks>
        internal void UseMetersPerStud(float metersPerStud)
        {
            if (_restoreScale != null)
            {
                throw new InvalidOperationException("the scale is already switched for this test");
            }

            _restoreScale = RbxSpace.BeginSessionReplacement(metersPerStud);
        }

        [UnityTest]
        public IEnumerator DroppedPart_FallsAtTheWorldsGravity()
        {
            RbxInstance part = _world.CreatePart("faller", new RbxVector3(0f, 100f, 0f), anchored: false);
            yield return null;

            float startY = _world.UnityPosition(part).y;
            _world.Simulate(0.5f);
            float fallenMetres = startY - _world.UnityPosition(part).y;

            float expected = ExpectedFall(RbxWorldPhysics.DefaultGravity, 0.5f);
            Assert.AreEqual(expected, fallenMetres, expected * 0.03f,
                "a dropped part must fall at Workspace.Gravity, within 3%");
        }

        [UnityTest]
        public IEnumerator ScriptedGravity_ChangesTheFallAndLeavesTheHostSceneAlone()
        {
            // DEV-6: CoreAI is a package inside someone else's project. A world that halves its own
            // gravity must not change how the host's own objects fall.
            Vector3 hostGravityBefore = Physics.gravity;
            _world.Physics.Gravity = RbxWorldPhysics.DefaultGravity / 4d;
            RbxInstance part = _world.CreatePart("slow", new RbxVector3(0f, 100f, 0f), anchored: false);
            yield return null;

            float startY = _world.UnityPosition(part).y;
            _world.Simulate(0.5f);
            float fallenMetres = startY - _world.UnityPosition(part).y;

            float expected = ExpectedFall(RbxWorldPhysics.DefaultGravity / 4d, 0.5f);
            Assert.AreEqual(expected, fallenMetres, expected * 0.03f);
            Assert.AreEqual(hostGravityBefore, Physics.gravity,
                "the host scene's Physics.gravity must be byte-equal before and after");
        }

        [UnityTest]
        public IEnumerator RealCollision_ReportsContactBeganThenEnded()
        {
            RbxInstance floor = _world.CreatePart("floor", new RbxVector3(0f, 0f, 0f), anchored: true);
            RbxInstance ball = _world.CreatePart("ball", new RbxVector3(0f, 6f, 0f), anchored: false);
            yield return null;

            _world.Simulate(1.5f);

            CollectionAssert.Contains(_world.Contacts, Pair(floor, ball, began: true),
                "a part falling onto another must report a contact; contacts seen: "
                + string.Join(", ", _world.Contacts));

            // Lift it back off and the contact must end.
            _world.SetUnityPosition(ball, _world.UnityPosition(ball) + Vector3.up * 5f);
            _world.Simulate(0.2f);

            CollectionAssert.Contains(_world.Contacts, Pair(floor, ball, began: false));
        }

        [UnityTest]
        public IEnumerator Negative_PartsThatNeverTouch_ReportNothing()
        {
            _world.CreatePart("left", new RbxVector3(-50f, 0f, 0f), anchored: true);
            _world.CreatePart("right", new RbxVector3(50f, 0f, 0f), anchored: true);
            yield return null;

            _world.Simulate(0.5f);

            Assert.IsEmpty(_world.Contacts,
                "a zero-work counter: two distant anchored parts must produce no contacts");
        }

        [UnityTest]
        public IEnumerator Raycast_HitsTheExpectedPartWithConvertedGeometry()
        {
            RbxInstance target = _world.CreatePart("target", new RbxVector3(0f, 0f, 0f), anchored: true);
            yield return null;

            RbxRaycastResult result = _world.Physics.Raycast(
                new RbxVector3(0f, 20f, 0f), new RbxVector3(0f, -40f, 0f), null);

            Assert.IsNotNull(result, "the ray starts above the part and points through it");
            Assert.AreSame(target, result.Instance);
            // The part is 4x1.2x2 studs by Roblox default, so its top face sits 0.6 studs up.
            Assert.AreEqual(0.6f, result.Position.Y, 0.15f, "the hit point is reported in studs");
            Assert.AreEqual(1f, result.Normal.Y, 0.01f, "an upward face normal");
            Assert.AreEqual(19.4d, result.Distance, 0.3d, "distance is studs, not metres");
        }

        [UnityTest]
        public IEnumerator Negative_Raycast_ExcludingTheOnlyPart_Misses()
        {
            RbxInstance target = _world.CreatePart("target", new RbxVector3(0f, 0f, 0f), anchored: true);
            yield return null;

            RbxRaycastParams filter = new();
            filter.SetFilterDescendantsInstances(new[] { target });

            Assert.IsNull(_world.Physics.Raycast(
                new RbxVector3(0f, 20f, 0f), new RbxVector3(0f, -40f, 0f), filter),
                "an excluded part cannot be the hit, even when it is the only thing in the way");
        }

        [UnityTest]
        public IEnumerator Negative_Raycast_OverTheMirrorsCap_IsRefusedBeforeTheEngineRuns()
        {
            _world.CreatePart("target", new RbxVector3(0f, 0f, 0f), anchored: true);
            yield return null;

            RbxError error = Assert.Throws<RbxError>(() => _world.Physics.Raycast(
                new RbxVector3(0f, 20f, 0f), new RbxVector3(0f, -20000f, 0f), null));

            Assert.AreEqual(RbxErrorCode.BadArgument, error.Code);
            StringAssert.Contains("15000", error.Message);
        }

        [UnityTest]
        public IEnumerator Raycast_PastMoreCollidersThanTheOldFixedScratchBuffer_StillFindsTheAcceptedTarget()
        {
            // 40 anchored, filtered-out parts stacked along the ray, then one accepted part past all
            // of them. A 32-slot RaycastNonAlloc buffer fills up on the near blockers alone and never
            // sees the target at all, so a fixed-size buffer reports a miss here every time.
            for (int index = 0; index < 40; index++)
            {
                _world.CreatePart("blocker" + index, new RbxVector3(0f, 40f - index, 0f), anchored: true);
            }

            RbxInstance target = _world.CreatePart("target", new RbxVector3(0f, -5f, 0f), anchored: true);
            yield return null;

            RbxRaycastParams filter = new() { FilterType = RbxRaycastFilterType.Include };
            filter.SetFilterDescendantsInstances(new[] { target });

            RbxRaycastResult result = _world.Physics.Raycast(
                new RbxVector3(0f, 50f, 0f), new RbxVector3(0f, -100f, 0f), filter);

            Assert.IsNotNull(result,
                "the accepted target sits past 40 other colliders on the ray, more than a 32-slot "
                + "scratch buffer can hold without growing");
            Assert.AreSame(target, result.Instance);
        }

        [UnityTest]
        public IEnumerator Humanoid_WalksAtWalkSpeedInStudsPerSecond()
        {
            // The one number the whole metric contract rests on: WalkSpeed is studs per second, and
            // a stud is 0.28 m. A motor that walked in metres would be 3.5x too fast and nothing
            // else in the API would notice.
            Walker walker = new();
            yield return null;

            float travelled = walker.WalkThenDestroy(RbxHumanoid.DefaultWalkSpeed);
            float expected = ExpectedWalk(RbxHumanoid.DefaultWalkSpeed, RbxSpace.DefaultMetersPerStud);

            Assert.AreEqual(expected, travelled, expected * 0.02f,
                "16 studs/s must measure 16 x 0.28 m/s on the controller, within 2%");
        }

        [UnityTest]
        public IEnumerator Humanoid_AtOneMetrePerStud_StillWalksAtWalkSpeedInStudsPerSecond()
        {
            // WHY a second scale: at 0.28 m/stud a motor that multiplied by a hard-coded 0.28 walks
            // exactly as far as one that read MetersPerStud, so the test above cannot tell them
            // apart. At 1 m/stud they differ by 3.5x, while a motor that ignored the scale entirely
            // passes here and fails above. Only the pair pins the conversion.
            UseMetersPerStud(1f);
            Walker walker = new();
            yield return null;

            float travelled = walker.WalkThenDestroy(RbxHumanoid.DefaultWalkSpeed);
            float expected = ExpectedWalk(RbxHumanoid.DefaultWalkSpeed, 1f);

            Assert.AreEqual(expected, travelled, expected * 0.02f,
                "16 studs/s must measure 16 m/s on the controller at 1 m/stud, within 2%");
        }

        /// <summary>
        /// How far a body falls in <paramref name="seconds"/> under <paramref name="gravityStuds"/>,
        /// in metres, as a fixed-step simulation actually integrates it.
        /// </summary>
        /// <remarks>
        /// WHY not ½·a·t²: that is the continuous answer, and a stepped integrator does not produce
        /// it — velocity is applied for a whole step after each acceleration, so the body falls
        /// ½·a·t·(t + dt), about 4% further over half a second at a 20 ms step. Asserting the
        /// continuous formula would mean either a permanently failing gate or a tolerance widened
        /// until it stopped testing gravity at all; the discrete form keeps the 3% band meaningful.
        /// </remarks>
        private static float ExpectedFall(double gravityStuds, float seconds)
        {
            float acceleration = RbxSpace.AccelerationToUnity((float)gravityStuds);
            return 0.5f * acceleration * seconds * (seconds + FixedStep);
        }

        /// <summary>
        /// How far a body walking at <paramref name="walkSpeedStuds"/> covers in
        /// <see cref="WalkSeconds"/>, in metres, at an explicit <paramref name="metersPerStud"/>.
        /// </summary>
        /// <remarks>
        /// WHY the multiplication is spelled out rather than taken from RbxSpace.LengthToUnity: the
        /// motor converts with that very call, so an expectation built on it agrees with any
        /// self-consistent mistake — an inverted or ignored scale — and pins nothing.
        /// </remarks>
        private static float ExpectedWalk(double walkSpeedStuds, float metersPerStud)
        {
            return (float)walkSpeedStuds * metersPerStud * WalkSeconds;
        }

        private static string Pair(RbxInstance first, RbxInstance second, bool began)
        {
            ulong low = Math.Min(first.Id.Value, second.Id.Value);
            ulong high = Math.Max(first.Id.Value, second.Id.Value);
            return low + "-" + high + ":" + (began ? "began" : "ended");
        }

        /// <summary>
        /// A minimal live world: a registry bound to real GameObjects, the Unity physics port, and a
        /// scripted simulation step.
        /// </summary>
        private sealed class PhysicsWorld : IDisposable
        {
            private readonly GameObject _root;
            private readonly InstanceGameObjectBinder _binder;
            private readonly UnityRbxPhysicsPort _port;

            public PhysicsWorld()
            {
                RbxSpace.Configure(RbxSpace.DefaultMetersPerStud);
                _root = new GameObject("CoreAI_PhysicsPlayModeWorld");
                _binder = new InstanceGameObjectBinder(_root.transform, null);
                Registry = new InstanceRegistry(
                    null, _binder, worldInstanceAdapter: new WorldInstanceAdapter(_binder));
                DataModelBootstrap.CreateGame(Registry);
                _port = new UnityRbxPhysicsPort(_binder);
                Physics = new RbxWorldPhysics(Registry);
                Physics.AttachPort(_port);
                Contacts = new List<string>();
                _port.ContactBegan += (first, second) => Contacts.Add(Key(first, second, true));
                _port.ContactEnded += (first, second) => Contacts.Add(Key(first, second, false));
            }

            public InstanceRegistry Registry { get; }

            public RbxWorldPhysics Physics { get; }

            public List<string> Contacts { get; }

            public RbxInstance CreatePart(string name, RbxVector3 position, bool anchored)
            {
                RbxInstance part = Registry.Create("Part");
                part.Name = name;
                part.Parent = Registry.WorldRoot;
                _binder.SetPosition(part.Id, position);
                _binder.SetAnchored(part.Id, anchored);
                return part;
            }

            public Vector3 UnityPosition(RbxInstance part)
            {
                return _binder.TryGetBoundObject(part.Id, out GameObject gameObject)
                    ? gameObject.transform.position
                    : Vector3.zero;
            }

            public void SetUnityPosition(RbxInstance part, Vector3 position)
            {
                if (_binder.TryGetBoundObject(part.Id, out GameObject gameObject))
                {
                    gameObject.transform.position = position;
                }
            }

            /// <summary>Advances the simulation by a whole number of fixed steps, applying gravity.</summary>
            public void Simulate(float seconds)
            {
                int steps = Mathf.Max(1, Mathf.RoundToInt(seconds / FixedStep));
                for (int step = 0; step < steps; step++)
                {
                    Physics.BeginPhysicsStep();
                    _port.ApplyGravity();
                    UnityEngine.Physics.Simulate(FixedStep);
                }
            }

            public void Dispose()
            {
                _port.Dispose();
                if (_root != null)
                {
                    UnityEngine.Object.DestroyImmediate(_root);
                }
            }

            private static string Key(InstanceId first, InstanceId second, bool began)
            {
                ulong low = Math.Min(first.Value, second.Value);
                ulong high = Math.Max(first.Value, second.Value);
                return low + "-" + high + ":" + (began ? "began" : "ended");
            }
        }

        /// <summary>
        /// A gravity-free capsule driven by CoreAI's own motor: one rig for every WalkSpeed
        /// measurement, so two scales differ in nothing but the scale.
        /// </summary>
        private sealed class Walker
        {
            private readonly GameObject _character;
            private readonly Rigidbody _body;
            private readonly UnityRbxCharacterMotor _motor;

            public Walker()
            {
                _character = GameObject.CreatePrimitive(PrimitiveType.Capsule);
                _character.transform.position = new Vector3(0f, 0.5f, 0f);
                _body = _character.AddComponent<Rigidbody>();
                _body.useGravity = false;
                _motor = new UnityRbxCharacterMotor(_body);
            }

            /// <summary>
            /// Walks toward a far-off target for <see cref="WalkSeconds"/> of fixed steps, destroys
            /// the capsule, and returns the metres actually covered.
            /// </summary>
            public float WalkThenDestroy(double walkSpeedStuds)
            {
                _motor.SetWalkSpeed(walkSpeedStuds);
                _motor.MoveTo(new RbxVector3(0f, 0f, 1000f));
                Vector3 start = _body.position;
                int steps = Mathf.RoundToInt(WalkSeconds / FixedStep);
                for (int step = 0; step < steps; step++)
                {
                    _motor.Step();
                    UnityEngine.Physics.Simulate(FixedStep);
                }

                float travelled = Vector3.Distance(start, _body.position);
                UnityEngine.Object.DestroyImmediate(_character);
                return travelled;
            }
        }
    }

    /// <summary>
    /// Pins <see cref="Mvp8PhysicsPlayModeTests"/>'s save/restore of the host's own
    /// <c>Physics.simulationMode</c>, <c>Physics.gravity</c> and the RbxSpace scale.
    /// </summary>
    /// <remarks>
    /// WHY a separate fixture: calling <c>CreateWorld</c>/<c>DestroyWorld</c> as plain methods from
    /// inside a test of the fixture they belong to would also be wrapped by NUnit's own automatic
    /// [SetUp]/[TearDown] invocation for that same fixture, doubling the save/restore and hiding
    /// exactly the regression this guards: that DestroyWorld puts the host's globals back to what
    /// they were before CreateWorld touched them, even for a fixture instance NUnit never manages.
    /// </remarks>
    [TestFixture]
    public sealed class Mvp8PhysicsPlayModeTearDownRegressionTests
    {
        private SimulationMode _hostSimulationMode;
        private Vector3 _hostGravity;
        private float _hostMetersPerStud;
        private Mvp8PhysicsPlayModeTests _harness;

        [SetUp]
        public void RememberHostGlobals()
        {
            _hostSimulationMode = Physics.simulationMode;
            _hostGravity = Physics.gravity;
            _hostMetersPerStud = RbxSpace.MetersPerStud;
        }

        [TearDown]
        public void RestoreHostGlobals()
        {
            // WHY this owes nothing to the harness: the test bodies are DestroyWorld's only callers
            // here, and a timeout, a throw out of CreateWorld or UseMetersPerStud, or the very
            // regression this fixture pins all leave the globals switched with nothing else to put
            // them back — play mode starts without a domain reload, so a scripted simulation or a
            // 1 m/stud scale would outlive the session. [TearDown] runs after every outcome, and the
            // finally restores what RememberHostGlobals saw even when DestroyWorld throws or was
            // already called.
            try
            {
                _harness?.DestroyWorld();
            }
            finally
            {
                _harness = null;
                Physics.simulationMode = _hostSimulationMode;
                Physics.gravity = _hostGravity;
                if (RbxSpace.MetersPerStud != _hostMetersPerStud)
                {
                    // WHY the rollback is discarded: BeginSessionReplacement is the one public way to
                    // move the scale, and a scale that differs here was set through Configure or a
                    // replacement already, so the configured flag it leaves is the one the session had.
                    RbxSpace.BeginSessionReplacement(_hostMetersPerStud);
                }
            }
        }

        [UnityTest]
        public IEnumerator CreateWorldThenDestroyWorld_RestoresTheHostsSimulationModeAndGravity()
        {
            SimulationMode modeBefore = Physics.simulationMode;
            Vector3 gravityBefore = Physics.gravity;

            _harness = new();
            _harness.CreateWorld();
            yield return null;
            DestroyWorldUnderTest();
            SimulationMode modeAfter = Physics.simulationMode;
            Vector3 gravityAfter = Physics.gravity;

            Assert.AreEqual(modeBefore, modeAfter,
                "DestroyWorld must restore the host's own simulation mode, or every other PlayMode "
                + "test sharing this process would inherit a scripted simulation");
            Assert.AreEqual(gravityBefore, gravityAfter,
                "DestroyWorld must restore the host's own gravity too");
        }

        [UnityTest]
        public IEnumerator CreateWorldSwitchScaleThenDestroyWorld_RestoresTheDefaultScale()
        {
            _harness = new();
            _harness.CreateWorld();
            _harness.UseMetersPerStud(1f);
            float switched = RbxSpace.MetersPerStud;
            yield return null;
            DestroyWorldUnderTest();
            float restored = RbxSpace.MetersPerStud;

            Assert.AreEqual(1f, switched, "the switch itself must have taken, or this pins nothing");
            Assert.AreEqual(RbxSpace.DefaultMetersPerStud, restored,
                "DestroyWorld must roll the RbxSpace scale back: play mode starts without a domain "
                + "reload here, so a scale left at 1 m/stud would outlive the session and mis-scale "
                + "every later test and the developer's next Play");
        }

        /// <summary>
        /// The call under test, made exactly once: the harness is handed over before the call so
        /// <see cref="RestoreHostGlobals"/> never repeats it, whether it returns or throws.
        /// </summary>
        private void DestroyWorldUnderTest()
        {
            Mvp8PhysicsPlayModeTests harness = _harness;
            _harness = null;
            harness.DestroyWorld();
        }
    }
}
