using System;
using CoreAI.Ai;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode
{
    public sealed class CoreAiEventsEditModeTests
    {
        [SetUp]
        public void SetUp()
        {
            CoreAiEvents.ClearAll();
        }

        [TearDown]
        public void TearDown()
        {
            CoreAiEvents.ClearAll();
        }

        [Test]
        public void CoreAiEvents_SubscribeAndPublish_NoPayload_TriggersCallback()
        {
            bool triggered = false;

            CoreAiEvents.Subscribe("my_event", () => triggered = true);
            CoreAiEvents.Publish("my_event");

            Assert.IsTrue(triggered);
        }

        [Test]
        public void CoreAiEvents_SubscribeAndPublish_WithPayload_TriggersCallbackWithPayload()
        {
            string receivedPayload = null;

            CoreAiEvents.Subscribe("my_payload_event", (payload) => receivedPayload = payload);
            CoreAiEvents.Publish("my_payload_event", "Hello World");

            Assert.AreEqual("Hello World", receivedPayload);
        }

        [Test]
        public void CoreAiEvents_Unsubscribe_StopsTriggering()
        {
            int triggerCount = 0;
            Action handler = () => triggerCount++;

            CoreAiEvents.Subscribe("once_event", handler);
            CoreAiEvents.Publish("once_event");

            CoreAiEvents.Unsubscribe("once_event", handler);
            CoreAiEvents.Publish("once_event");

            Assert.AreEqual(1, triggerCount);
        }

        [Test]
        public void AgentBuilder_WithEventTool_StoresDelegateToolSuccessfully()
        {
            AgentConfig config = new AgentBuilder("tester")
                .WithEventTool("test_event", "Triggers a test event")
                .WithEventTool("test_payload_event", "Triggers with payload", true)
                .Build();

            Assert.AreEqual(2, config.Tools.Count);
            Assert.IsInstanceOf<DelegateLlmTool>(config.Tools[0]);
            Assert.IsInstanceOf<DelegateLlmTool>(config.Tools[1]);

            Assert.AreEqual("test_event", config.Tools[0].Name);
            Assert.AreEqual("test_payload_event", config.Tools[1].Name);
        }

        [Test]
        public void AgentBuilder_WithAction_StoresDelegateSuccessfully()
        {
            int value = 0;
            Action<int> addAction = (amount) => value += amount;

            AgentConfig config = new AgentBuilder("action_tester")
                .WithAction("add_value", "Adds value", addAction)
                .Build();

            Assert.AreEqual(1, config.Tools.Count);
            DelegateLlmTool delTool = config.Tools[0] as DelegateLlmTool;
            Assert.IsNotNull(delTool);
            Assert.AreEqual("add_value", delTool.Name);

            // Invoke delegate directly
            delTool.ActionDelegate.DynamicInvoke(5);
            Assert.AreEqual(5, value);
        }
    }
}