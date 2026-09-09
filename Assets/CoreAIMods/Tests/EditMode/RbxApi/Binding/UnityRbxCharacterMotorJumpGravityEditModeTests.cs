using CoreAI.Mods.Rbx.Binding;
using CoreAI.Mods.Rbx.Spatial;
using NUnit.Framework;
using UnityEngine;

namespace CoreAI.Tests.EditMode.RbxApi.Binding
{
    /// <summary>
    /// A height-based jump (<c>UseJumpPower == false</c>) must solve its launch velocity against
    /// the acceleration the world actually applies to the body, not against the Unity engine's own
    /// <c>Physics.gravity</c> — these bound bodies have gravity disabled and fall under whatever
    /// <c>Workspace.Gravity</c> <see cref="UnityRbxPhysicsPort"/> is applying per fixed step (DEV-6).
    /// </summary>
    [TestFixture]
    public sealed class UnityRbxCharacterMotorJumpGravityEditModeTests
    {
        private const float Epsilon = 1e-3f;
        private const double JumpHeightStuds = 16d;

        private GameObject _floor;
        private GameObject _characterBody;
        private Rigidbody _rigidbody;
        private Vector3 _savedPhysicsGravity;

        [SetUp]
        public void SetUp()
        {
            RbxSpace.ResetForTests(0.28f);
            _savedPhysicsGravity = Physics.gravity;

            // WHY a wrong, distinctive engine gravity: if the motor ever reads Physics.gravity again
            // (the regression this guards against), the launch velocity below would reflect THIS
            // value instead of the one it was actually given, and the assertions would catch it.
            Physics.gravity = new Vector3(0f, -1234f, 0f);

            _floor = GameObject.CreatePrimitive(PrimitiveType.Cube);
            _floor.transform.position = new Vector3(0f, -0.5f, 0f);
            _floor.transform.localScale = new Vector3(10f, 1f, 10f);

            _characterBody = new GameObject("MotorGravityTestBody");
            _rigidbody = _characterBody.AddComponent<Rigidbody>();
            _rigidbody.useGravity = false;
            _characterBody.transform.position = new Vector3(0f, 0.3f, 0f);

            Physics.SyncTransforms();
        }

        [TearDown]
        public void TearDown()
        {
            Physics.gravity = _savedPhysicsGravity;
            if (_characterBody != null)
            {
                Object.DestroyImmediate(_characterBody);
            }

            if (_floor != null)
            {
                Object.DestroyImmediate(_floor);
            }

            RbxSpace.ResetForTests();
        }

        [Test]
        public void Jump_HeightBased_MatchesRequestedHeightUnderInjectedWorldGravity()
        {
            const float worldGravityMetresPerSecondSquared = 6f;
            UnityRbxCharacterMotor motor =
                new(_rigidbody, worldGravityMetresPerSecondSquared: () => worldGravityMetresPerSecondSquared);

            motor.Jump(jumpPower: 0d, jumpHeight: JumpHeightStuds, useJumpPower: false);

            float heightMetres = RbxSpace.LengthToUnity((float)JumpHeightStuds);
            float expectedVelocity = Mathf.Sqrt(2f * worldGravityMetresPerSecondSquared * heightMetres);
            Assert.AreEqual(expectedVelocity, _rigidbody.linearVelocity.y, Epsilon,
                "v = sqrt(2*g*h) must be solved against the world gravity the motor was given, "
                    + "not Physics.gravity (set to a deliberately wrong value in SetUp).");

            // Reached height under the acceleration that was actually applied: h = v^2 / (2*g).
            float reachedHeightMetres =
                (expectedVelocity * expectedVelocity) / (2f * worldGravityMetresPerSecondSquared);
            Assert.AreEqual(heightMetres, reachedHeightMetres, Epsilon,
                "the launch velocity must land the character back at the requested JumpHeight "
                    + "under the world's own acceleration.");
        }

        [Test]
        public void Jump_HeightBased_LaunchVelocityChangesWithWorldGravity()
        {
            float currentGravity = 3f;
            UnityRbxCharacterMotor motor = new(_rigidbody, worldGravityMetresPerSecondSquared: () => currentGravity);

            motor.Jump(jumpPower: 0d, jumpHeight: JumpHeightStuds, useJumpPower: false);
            float velocityAtLowGravity = _rigidbody.linearVelocity.y;

            // WHY this proves live tracking and not a value captured once at construction: the same
            // motor instance, same requested height, only the delegate's return value changed —
            // exactly what happens in production when a script writes Workspace.Gravity mid-session.
            currentGravity = 27f;
            motor.Jump(jumpPower: 0d, jumpHeight: JumpHeightStuds, useJumpPower: false);
            float velocityAtHighGravity = _rigidbody.linearVelocity.y;

            Assert.Greater(velocityAtHighGravity, velocityAtLowGravity,
                "a higher world gravity must require a higher launch velocity to reach the same "
                    + "requested JumpHeight; the motor must re-read gravity on every jump, not cache it.");

            float heightMetres = RbxSpace.LengthToUnity((float)JumpHeightStuds);
            Assert.AreEqual(Mathf.Sqrt(2f * 3f * heightMetres), velocityAtLowGravity, Epsilon);
            Assert.AreEqual(Mathf.Sqrt(2f * 27f * heightMetres), velocityAtHighGravity, Epsilon);
        }
    }
}
