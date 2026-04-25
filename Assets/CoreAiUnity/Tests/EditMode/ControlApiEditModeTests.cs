#if !COREAI_NO_LLM
using System.Collections.Generic;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode
{
    /// <summary>
    /// EditMode-тесты для Control API (<see cref="CoreAi"/>):
    /// — OnToolExecuted event firing;
    /// — ClearContext с гранулярными флагами;
    /// — NotifyToolExecuted error safety.
    /// </summary>
    [TestFixture]
    public sealed class ControlApiEditModeTests
    {
        [TearDown]
        public void TearDown()
        {
            // Cleanup static event listeners after each test
            ClearOnToolExecutedSubscribers();
        }

        // ===================== OnToolExecuted =====================

        [Test]
        public void NotifyToolExecuted_FiresEventWithCorrectData()
        {
            string capturedRoleId = null;
            string capturedToolName = null;
            IDictionary<string, object> capturedArgs = null;
            object capturedResult = null;

            CoreAi.OnToolExecuted += (roleId, toolName, args, result) =>
            {
                capturedRoleId = roleId;
                capturedToolName = toolName;
                capturedArgs = args;
                capturedResult = result;
            };

            var testArgs = new Dictionary<string, object> { { "x", 42 }, { "mode", "fast" } };
            CoreAi.NotifyToolExecuted("Teacher", "spawn_item", testArgs, "item_123");

            Assert.AreEqual("Teacher", capturedRoleId);
            Assert.AreEqual("spawn_item", capturedToolName);
            Assert.AreEqual(42, capturedArgs["x"]);
            Assert.AreEqual("fast", capturedArgs["mode"]);
            Assert.AreEqual("item_123", capturedResult);
        }

        [Test]
        public void NotifyToolExecuted_NullArgs_DoesNotCrash()
        {
            bool wasCalled = false;
            CoreAi.OnToolExecuted += (_, _, _, _) => wasCalled = true;

            Assert.DoesNotThrow(() =>
                CoreAi.NotifyToolExecuted("Role", "tool_name", null, null));

            Assert.IsTrue(wasCalled);
        }

        [Test]
        public void NotifyToolExecuted_SubscriberThrows_DoesNotPropagateException()
        {
            bool secondCalled = false;
            CoreAi.OnToolExecuted += (_, _, _, _) =>
                throw new System.InvalidOperationException("Bad subscriber");
            CoreAi.OnToolExecuted += (_, _, _, _) => secondCalled = true;

            // NotifyToolExecuted wraps in try/catch, should not throw
            Assert.DoesNotThrow(() =>
                CoreAi.NotifyToolExecuted("Role", "tool_name", null, null));

            // Note: due to multicast delegate behavior, exception in first handler
            // may prevent second handler from executing. The key assertion is no exception propagates.
        }

        [Test]
        public void NotifyToolExecuted_NoSubscribers_DoesNotCrash()
        {
            // No subscribers registered — should be a no-op
            Assert.DoesNotThrow(() =>
                CoreAi.NotifyToolExecuted("Role", "tool_name", null, null));
        }

        [Test]
        public void NotifyToolExecuted_MultipleSubscribers_AllReceiveEvent()
        {
            int callCount = 0;
            CoreAi.OnToolExecuted += (_, _, _, _) => callCount++;
            CoreAi.OnToolExecuted += (_, _, _, _) => callCount++;
            CoreAi.OnToolExecuted += (_, _, _, _) => callCount++;

            CoreAi.NotifyToolExecuted("Role", "tool", null, null);

            Assert.AreEqual(3, callCount, "All three subscribers should be called");
        }

        // ===================== ClearContext (без LifetimeScope — EditMode) =====================

        [Test]
        public void ClearContext_WithoutScope_DoesNotThrow()
        {
            // В EditMode нет CoreAILifetimeScope, ClearContext должен отработать молча
            Assert.DoesNotThrow(() =>
                CoreAi.ClearContext("SomeRole", clearChatHistory: true, clearLongTermMemory: false));
        }

        [Test]
        public void ClearContext_BothFlagsTrue_DoesNotThrow()
        {
            Assert.DoesNotThrow(() =>
                CoreAi.ClearContext("SomeRole", clearChatHistory: true, clearLongTermMemory: true));
        }

        [Test]
        public void ClearContext_BothFlagsFalse_DoesNotThrow()
        {
            // Edge case: nothing to clear
            Assert.DoesNotThrow(() =>
                CoreAi.ClearContext("SomeRole", clearChatHistory: false, clearLongTermMemory: false));
        }

        // ===================== StopAgent (без LifetimeScope — EditMode) =====================

        [Test]
        public void StopAgent_WithoutScope_DoesNotThrow()
        {
            // В EditMode нет оркестратора; вызов должен быть безопасным
            Assert.DoesNotThrow(() => CoreAi.StopAgent("SomeRole"));
        }

        // ===================== Helpers =====================

        /// <summary>
        /// Clear all subscribers from the static OnToolExecuted event.
        /// Uses reflection to reset the event field since we can't -= all handlers otherwise.
        /// </summary>
        private static void ClearOnToolExecutedSubscribers()
        {
            var field = typeof(CoreAi).GetField("OnToolExecuted",
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic |
                System.Reflection.BindingFlags.Public);

            // For events, the backing field has the same name
            var eventField = typeof(CoreAi).GetField("OnToolExecuted",
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);

            if (eventField != null)
            {
                eventField.SetValue(null, null);
            }
            else if (field != null)
            {
                field.SetValue(null, null);
            }
        }
    }
}
#endif
