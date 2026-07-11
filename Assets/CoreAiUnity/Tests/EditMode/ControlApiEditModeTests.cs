#if !COREAI_NO_LLM
using System;
using System.Collections.Generic;
using System.Reflection;
using CoreAI.Messaging;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode
{
    /// <summary>
    /// EditMode coverage for the <see cref="CoreAi"/> control API:
    /// — OnToolExecuted event firing;
    /// - ClearContext with granular flags;
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
            ClearToolCallSubscribers();
            CoreAi.ClearToolCallHistory();
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

            Dictionary<string, object> testArgs = new() { { "x", 42 }, { "mode", "fast" } };
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
            CoreAi.OnToolExecuted += (_, _, _, _) =>
                throw new InvalidOperationException("Bad subscriber");
            CoreAi.OnToolExecuted += (_, _, _, _) => { };

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

        [Test]
        public void NotifyToolExecuted_SubscriberThrows_StillCallsLaterSubscribers()
        {
            int callCount = 0;
            CoreAi.OnToolExecuted += (_, _, _, _) =>
                throw new InvalidOperationException("Bad subscriber");
            CoreAi.OnToolExecuted += (_, _, _, _) => callCount++;

            CoreAi.NotifyToolExecuted("Role", "tool_name", null, null);

            Assert.AreEqual(1, callCount, "A throwing subscriber must not block later subscribers.");
        }

        // ===================== Tool-call lifecycle API =====================

        [Test]
        public void ToolCallLifecycle_RecordsHistoryAndNotifiesSubscribers()
        {
            List<LlmToolCallRecord> records = new();
            using IDisposable sub = CoreAi.SubscribeToolCalls(records.Add);

            LlmToolCallInfo info = new("trace-1", "Teacher", "call-1", "spawn_item", "{\"id\":1}");
            CoreAi.NotifyToolCallStarted(new LlmToolCallStarted(info));
            CoreAi.NotifyToolCallCompleted(new LlmToolCallCompleted(info, "{\"ok\":true}", 12.5));

            IReadOnlyList<LlmToolCallRecord> snapshot = CoreAi.GetToolCallHistorySnapshot();
            Assert.AreEqual(2, records.Count);
            Assert.AreEqual(2, snapshot.Count);
            Assert.AreEqual("started", snapshot[0].Status);
            Assert.AreEqual("completed", snapshot[1].Status);
            Assert.AreEqual("Teacher", snapshot[1].Info.RoleId);
            Assert.AreEqual("spawn_item", snapshot[1].Info.ToolName);
            Assert.AreEqual("{\"ok\":true}", snapshot[1].ResultJson);
        }

        [Test]
        public void SubscribeToolCalls_DisposeStopsNotifications()
        {
            int callCount = 0;
            IDisposable sub = CoreAi.SubscribeToolCalls(_ => callCount++);
            sub.Dispose();

            CoreAi.NotifyToolCallStarted(new LlmToolCallStarted("trace", "Role", "tool", "{}"));

            Assert.AreEqual(0, callCount);
        }

        [Test]
        public void SubscribeToolCalls_ReplayExisting_ReplaysSnapshot()
        {
            CoreAi.NotifyToolCallFailed(new LlmToolCallFailed("trace", "Role", "tool", "{}", "boom", 3));

            List<LlmToolCallRecord> replayed = new();
            using IDisposable sub = CoreAi.SubscribeToolCalls(replayed.Add, true);

            Assert.AreEqual(1, replayed.Count);
            Assert.AreEqual("failed", replayed[0].Status);
            Assert.AreEqual("boom", replayed[0].Error);
        }

        // ===================== ClearContext (без LifetimeScope — EditMode) =====================

        [Test]
        public void ClearContext_WithoutScope_DoesNotThrow()
        {
            // В EditMode нет CoreAILifetimeScope, ClearContext должен отработать молча
            Assert.DoesNotThrow(() =>
                CoreAi.ClearContext("SomeRole", true, false));
        }

        [Test]
        public void ClearContext_BothFlagsTrue_DoesNotThrow()
        {
            Assert.DoesNotThrow(() =>
                CoreAi.ClearContext("SomeRole", true, true));
        }

        [Test]
        public void ClearContext_BothFlagsFalse_DoesNotThrow()
        {
            // Edge case: nothing to clear
            Assert.DoesNotThrow(() =>
                CoreAi.ClearContext("SomeRole", false, false));
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
            FieldInfo field = typeof(CoreAi).GetField("OnToolExecuted",
                BindingFlags.Static | BindingFlags.NonPublic |
                BindingFlags.Public);

            // For events, the backing field has the same name
            FieldInfo eventField = typeof(CoreAi).GetField("OnToolExecuted",
                BindingFlags.Static | BindingFlags.NonPublic);

            if (eventField != null)
            {
                eventField.SetValue(null, null);
            }
            else if (field != null)
            {
                field.SetValue(null, null);
            }
        }

        private static void ClearToolCallSubscribers()
        {
            ClearStaticEvent("OnToolCallStarted");
            ClearStaticEvent("OnToolCallCompleted");
            ClearStaticEvent("OnToolCallFailed");
            ClearStaticEvent("OnToolCallRecord");
        }

        private static void ClearStaticEvent(string eventName)
        {
            FieldInfo eventField = typeof(CoreAi).GetField(
                eventName,
                BindingFlags.Static | BindingFlags.NonPublic);
            eventField?.SetValue(null, null);
        }
    }
}
#endif
