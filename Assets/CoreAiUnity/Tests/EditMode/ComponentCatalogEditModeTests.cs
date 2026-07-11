using System.Collections.Generic;
using CoreAI.Infrastructure.Logging;
using CoreAI.Infrastructure.World;
using CoreAI.Messaging;
using NUnit.Framework;
using UnityEngine;

namespace CoreAI.Tests.EditMode
{
    /// <summary>
    /// EditMode coverage for the curated, reflection-free component path. Drives
    /// <see cref="CoreAiComponentCommandExecutor"/> directly (no live LLM) to add and configure
    /// Unity components, then asserts the components exist and the property writes took effect.
    /// This is the curated path used by both the native <c>component_command</c> tool and the
    /// <c>coreai_component_*</c> Lua bindings.
    /// </summary>
    [TestFixture]
    public sealed class ComponentCatalogEditModeTests
    {
        private readonly List<GameObject> _createdObjects = new();
        private CoreAiComponentCommandExecutor _executor;
        private GameObject _target;
        private string _targetName;

        [SetUp]
        public void SetUp()
        {
            _executor = new CoreAiComponentCommandExecutor(new NullGameLogger());

            // GameObject.Find (used by the executor to resolve targets) matches active, scene-root
            // objects by name. Use a unique name so the lookup is deterministic regardless of other
            // objects that may exist in the editor scene.
            _targetName = "ComponentCatalogTarget_" + System.Guid.NewGuid().ToString("N");
            _target = new GameObject(_targetName);
            _createdObjects.Add(_target);
        }

        [TearDown]
        public void TearDown()
        {
            foreach (GameObject go in _createdObjects)
            {
                if (go != null)
                {
                    Object.DestroyImmediate(go);
                }
            }

            _createdObjects.Clear();
        }

        [Test]
        public void Executor_AddAndConfigure_RigidbodyAndBoxCollider_PropertiesTakeEffect()
        {
            // Add a Rigidbody.
            Assert.IsTrue(Execute(CoreAiComponentCommandEnvelope.Add(_targetName, "rigidbody")),
                "add rigidbody should succeed");

            // Set Rigidbody.mass (float) and useGravity (bool).
            Assert.IsTrue(Execute(CoreAiComponentCommandEnvelope.SetFloat(_targetName, "rigidbody", "mass", 7.5f)),
                "set rigidbody.mass should succeed");
            Assert.IsTrue(
                Execute(CoreAiComponentCommandEnvelope.SetBool(_targetName, "rigidbody", "useGravity", false)),
                "set rigidbody.useGravity should succeed");

            // Add a BoxCollider.
            Assert.IsTrue(Execute(CoreAiComponentCommandEnvelope.Add(_targetName, "boxcollider")),
                "add boxcollider should succeed");

            // Set BoxCollider.isTrigger (bool) and size (vector).
            Assert.IsTrue(
                Execute(CoreAiComponentCommandEnvelope.SetBool(_targetName, "boxcollider", "isTrigger", true)),
                "set boxcollider.isTrigger should succeed");
            Assert.IsTrue(Execute(CoreAiComponentCommandEnvelope.SetVector(
                    _targetName, "boxcollider", "size", new Vector3(2f, 3f, 4f))),
                "set boxcollider.size should succeed");

            // Components must now exist on the GameObject.
            Rigidbody rigidbody = _target.GetComponent<Rigidbody>();
            BoxCollider boxCollider = _target.GetComponent<BoxCollider>();
            Assert.IsNotNull(rigidbody, "Rigidbody should have been added");
            Assert.IsNotNull(boxCollider, "BoxCollider should have been added");

            // Property writes must have landed on the live components.
            Assert.AreEqual(7.5f, rigidbody.mass, 0.0001f, "Rigidbody.mass should be set");
            Assert.IsFalse(rigidbody.useGravity, "Rigidbody.useGravity should be false");
            Assert.IsTrue(boxCollider.isTrigger, "BoxCollider.isTrigger should be true");
            Assert.AreEqual(new Vector3(2f, 3f, 4f), boxCollider.size, "BoxCollider.size should be set");
        }

        [Test]
        public void Executor_Set_AutoAddsMissingComponent()
        {
            // 'set' on a not-yet-present component should add it implicitly, then apply the property.
            Assert.IsNull(_target.GetComponent<Rigidbody>(), "precondition: no Rigidbody yet");

            Assert.IsTrue(Execute(CoreAiComponentCommandEnvelope.SetFloat(_targetName, "rigidbody", "mass", 12f)),
                "set on a missing component should auto-add and succeed");

            Rigidbody rigidbody = _target.GetComponent<Rigidbody>();
            Assert.IsNotNull(rigidbody, "Rigidbody should have been auto-added by 'set'");
            Assert.AreEqual(12f, rigidbody.mass, 0.0001f, "Rigidbody.mass should be set");
        }

        [Test]
        public void Executor_Set_UnsupportedProperty_FailsAndDoesNotThrow()
        {
            Assert.IsTrue(Execute(CoreAiComponentCommandEnvelope.Add(_targetName, "rigidbody")),
                "add rigidbody should succeed");

            // 'bananas' is not a curated setter for Rigidbody; the command should report failure
            // rather than throw or silently succeed.
            bool result = Execute(CoreAiComponentCommandEnvelope.SetFloat(_targetName, "rigidbody", "bananas", 1f));
            Assert.IsFalse(result, "setting an unsupported property should fail");
        }

        private bool Execute(CoreAiComponentCommandEnvelope envelope)
        {
            // Mirror the marshaling the real tools/bindings perform: serialize the envelope into the
            // generic command payload the executor consumes. JsonUtility matches the executor's
            // FromJson deserialization (see CoreAiComponentCommandExecutor.TryExecute).
            string json = JsonUtility.ToJson(envelope, false);
            return _executor.TryExecute(new ApplyAiGameCommand
            {
                CommandTypeId = AiGameCommandTypeIds.ComponentCommand,
                JsonPayload = json
            });
        }
    }
}
