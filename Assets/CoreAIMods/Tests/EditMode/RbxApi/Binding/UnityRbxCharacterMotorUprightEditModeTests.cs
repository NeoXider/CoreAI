using CoreAI.Mods.Rbx.Binding;
using CoreAI.Mods.Rbx.Spatial;
using NUnit.Framework;
using UnityEngine;

namespace CoreAI.Tests.EditMode.RbxApi.Binding
{
    /// <summary>
    /// A character's body stays upright, as a Roblox Humanoid's does: a tilt given from outside (the owner moved
    /// villagers and they lay on the grass) is undone on the next motor step, and the heading is kept.
    /// </summary>
    [TestFixture]
    public sealed class UnityRbxCharacterMotorUprightEditModeTests
    {
        private GameObject _characterBody;
        private Rigidbody _rigidbody;

        [SetUp]
        public void SetUp()
        {
            RbxSpace.ResetForTests(0.28f);
            _characterBody = new GameObject("MotorUprightTestBody");
            _rigidbody = _characterBody.AddComponent<Rigidbody>();
            _rigidbody.useGravity = false;
        }

        [TearDown]
        public void TearDown()
        {
            if (_characterBody != null)
            {
                Object.DestroyImmediate(_characterBody);
            }

            RbxSpace.ResetForTests();
        }

        [Test]
        public void ATiltedBody_StandsBackUp_OnTheNextStep_KeepingItsHeading()
        {
            UnityRbxCharacterMotor motor = new(_rigidbody, worldGravityMetresPerSecondSquared: () => 9.8f);
            Quaternion heading = Quaternion.Euler(0f, 70f, 0f);
            _characterBody.transform.rotation = heading * Quaternion.Euler(0f, 0f, 85f);

            motor.Step(0.02d);

            Assert.AreEqual(0f, Vector3.Angle(_characterBody.transform.up, Vector3.up), 0.01f, "upright again");
            Vector3 forward = _characterBody.transform.forward;
            Vector3 expected = heading * Vector3.forward;
            Assert.AreEqual(0f, Vector3.Angle(new Vector3(forward.x, 0f, forward.z), expected), 0.5f,
                "facing where it faced before it was tipped");
        }

        [Test]
        public void AnUprightBody_IsLeftAlone_NegativeTwin()
        {
            UnityRbxCharacterMotor motor = new(_rigidbody, worldGravityMetresPerSecondSquared: () => 9.8f);
            Quaternion heading = Quaternion.Euler(0f, 33f, 0f);
            _characterBody.transform.rotation = heading;

            motor.Step(0.02d);

            Assert.AreEqual(0f, Quaternion.Angle(heading, _characterBody.transform.rotation), 0.01f);
        }

        [Test]
        public void AKinematicBody_HeldByTheRig_IsNotStoodUp()
        {
            UnityRbxCharacterMotor motor = new(_rigidbody, worldGravityMetresPerSecondSquared: () => 9.8f);
            _rigidbody.isKinematic = true;
            Quaternion tilted = Quaternion.Euler(0f, 0f, 40f);
            _characterBody.transform.rotation = tilted;

            motor.Step(0.02d);

            Assert.AreEqual(0f, Quaternion.Angle(tilted, _characterBody.transform.rotation), 0.01f,
                "a body something else holds is posed by it");
        }
    }
}
