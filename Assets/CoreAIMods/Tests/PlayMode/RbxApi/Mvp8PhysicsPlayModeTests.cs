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

        private SimulationMode _savedSimulationMode;
        private Vector3 _savedHostGravity;
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
            _world.Dispose();
            Physics.simulationMode = _savedSimulationMode;
            Physics.gravity = _savedHostGravity;
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
    }
}
