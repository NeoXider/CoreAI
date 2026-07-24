using System.Collections.Generic;
using CoreAI.Infrastructure.Logging;
using CoreAI.Infrastructure.World;
using CoreAI.Messaging;
using NUnit.Framework;
using UnityEngine;

namespace CoreAI.Tests.EditMode
{
    /// <summary>
    /// <c>list_components</c> results must travel with the call. Tool calls run in parallel through
    /// <c>ToolExecutionPolicy</c>, so a listing published on shared executor state would hand the model
    /// one object's components labelled as another's.
    /// </summary>
    [TestFixture]
    public sealed class CoreAiComponentCommandExecutorListEditModeTests
    {
        private readonly List<GameObject> _createdObjects = new();
        private CoreAiComponentCommandExecutor _executor;

        [SetUp]
        public void SetUp()
        {
            _executor = new CoreAiComponentCommandExecutor(new NullGameLogger());
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
        public void ListComponents_TwoInterleavedCalls_EachCallKeepsItsOwnResult()
        {
            string withRigidbody = CreateObject(typeof(Rigidbody));
            string withLight = CreateObject(typeof(Light));

            Assert.IsTrue(Execute(CoreAiComponentCommandEnvelope.ListComponents(withRigidbody),
                out List<string> firstResult));
            Assert.IsTrue(Execute(CoreAiComponentCommandEnvelope.ListComponents(withLight),
                out List<string> secondResult));

            Assert.Contains(nameof(Rigidbody), firstResult,
                "The first call must still describe its own object after a second call ran.");
            CollectionAssert.DoesNotContain(firstResult, nameof(Light));

            Assert.Contains(nameof(Light), secondResult);
            CollectionAssert.DoesNotContain(secondResult, nameof(Rigidbody));
        }

        [Test]
        public void NonListAction_ReturnsAnEmptyComponentListing()
        {
            string target = CreateObject();

            Assert.IsTrue(Execute(CoreAiComponentCommandEnvelope.Add(target, "rigidbody"),
                out List<string> listed));

            Assert.IsNotNull(listed);
            Assert.IsEmpty(listed);
        }

        [Test]
        public void ListComponents_UnknownObject_FailsWithAnEmptyListing()
        {
            Assert.IsFalse(Execute(
                CoreAiComponentCommandEnvelope.ListComponents("CoreAiMissingTarget_" + System.Guid.NewGuid()),
                out List<string> listed));

            Assert.IsNotNull(listed);
            Assert.IsEmpty(listed);
        }

        private string CreateObject(params System.Type[] components)
        {
            string name = "ComponentListTarget_" + System.Guid.NewGuid().ToString("N");
            GameObject go = new(name, components);
            _createdObjects.Add(go);
            return name;
        }

        private bool Execute(CoreAiComponentCommandEnvelope envelope, out List<string> listedComponents)
        {
            return _executor.TryExecute(
                new ApplyAiGameCommand
                {
                    CommandTypeId = AiGameCommandTypeIds.ComponentCommand,
                    JsonPayload = JsonUtility.ToJson(envelope, false)
                },
                out listedComponents);
        }
    }
}
