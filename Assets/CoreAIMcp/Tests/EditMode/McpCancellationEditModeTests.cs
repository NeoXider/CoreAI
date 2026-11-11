using System;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Mcp.Protocol;
using CoreAI.Mcp.Server;
using CoreAI.Mcp.Tools;
using NUnit.Framework;
using Newtonsoft.Json.Linq;

namespace CoreAI.Mcp.Tests
{
    /// <summary>
    /// <c>notifications/cancelled</c> must stop the request it names: a call still waiting for the game
    /// thread never runs, a running body sees its token cancelled, and only the same session can cancel.
    /// Engine-free: the dispatcher and a held main-thread queue, no sockets.
    /// </summary>
    public sealed class McpCancellationEditModeTests
    {
        private static JObject Call(JToken id, string tool = "run")
        {
            return new JObject
            {
                ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = McpMethods.ToolsCall,
                ["params"] = new JObject { ["name"] = tool, ["arguments"] = new JObject() }
            };
        }

        private static JObject Cancel(JToken requestId)
        {
            return new JObject
            {
                ["jsonrpc"] = "2.0", ["method"] = McpMethods.CancelledNotification,
                ["params"] = new JObject { ["requestId"] = requestId, ["reason"] = "user pressed Esc" }
            };
        }

        [Test]
        public async Task CancelledNotification_ForAQueuedCall_KeepsItsBodyFromEverRunning()
        {
            FakeMcpTool tool = new("run");
            HeldMainThread queue = new();
            McpRpcDispatcher dispatcher = new(new McpToolRegistry(new[] { tool }), new McpSessionStore(), queue);

            Task<McpDispatchResult> pending = dispatcher.DispatchAsync(Call(7), "session-a", CancellationToken.None);
            McpDispatchResult notification = await dispatcher.DispatchAsync(Cancel(7), "session-a", CancellationToken.None);
            queue.Release();
            McpDispatchResult result = await Bounded(pending);

            Assert.IsTrue(notification.IsNotification, "a cancellation is a notification: no response body.");
            Assert.AreEqual(0, tool.InvocationCount, "a call the client cancelled must not mutate the game later.");
            Assert.AreEqual(JsonRpcErrorCodes.InternalError, (int)result.Response["error"]["code"]);
            StringAssert.Contains("cancelled", (string)result.Response["error"]["message"]);
        }

        [TestCase("other-id")]
        [TestCase("7-as-string")]
        public async Task CancelledNotification_ForADifferentRequestId_LeavesTheCallAlone(string variant)
        {
            FakeMcpTool tool = new("run");
            HeldMainThread queue = new();
            McpRpcDispatcher dispatcher = new(new McpToolRegistry(new[] { tool }), new McpSessionStore(), queue);

            Task<McpDispatchResult> pending = dispatcher.DispatchAsync(Call(7), "session-a", CancellationToken.None);
            JToken otherId = variant == "other-id" ? (JToken)8 : "7";
            await dispatcher.DispatchAsync(Cancel(otherId), "session-a", CancellationToken.None);
            queue.Release();
            McpDispatchResult result = await Bounded(pending);

            Assert.AreEqual(1, tool.InvocationCount, "the integer id 7 and the string id \"7\" are different requests.");
            Assert.IsNull(result.Response["error"]);
            Assert.IsFalse((bool)result.Response["result"]["isError"]);
        }

        [TestCase("session-b")]
        [TestCase(null)]
        public async Task CancelledNotification_FromAnotherSession_LeavesTheCallAlone(string cancellingSession)
        {
            FakeMcpTool tool = new("run");
            HeldMainThread queue = new();
            McpRpcDispatcher dispatcher = new(new McpToolRegistry(new[] { tool }), new McpSessionStore(), queue);

            Task<McpDispatchResult> pending = dispatcher.DispatchAsync(Call(7), "session-a", CancellationToken.None);
            await dispatcher.DispatchAsync(Cancel(7), cancellingSession, CancellationToken.None);
            queue.Release();
            await Bounded(pending);

            Assert.AreEqual(1, tool.InvocationCount, "request ids are scoped by session.");
        }

        [Test]
        public async Task CancelledNotification_ForARunningBody_CancelsItsToken()
        {
            BlockingTool tool = new();
            McpRpcDispatcher dispatcher = new(new McpToolRegistry(new IMcpTool[] { tool }), new McpSessionStore(),
                new InlineMainThreadDispatcher());

            Task<McpDispatchResult> pending = dispatcher.DispatchAsync(Call("call-1", tool.Name), null,
                CancellationToken.None);
            await Bounded(tool.Started.Task);
            Assert.AreEqual(1, dispatcher.CancellableCallCount);
            await dispatcher.DispatchAsync(Cancel("call-1"), null, CancellationToken.None);
            McpDispatchResult result = await Bounded(pending);

            Assert.IsTrue(tool.ObservedCancellation, "the body must receive the client's cancellation.");
            Assert.AreEqual("stopped", (string)result.Response["result"]["content"][0]["text"]);
            Assert.AreEqual(0, dispatcher.CancellableCallCount, "a finished call is no longer registered.");
        }

        [Test]
        public async Task CancelledNotification_WithdrawsTheCallFromACancellableQueue()
        {
            FakeMcpTool tool = new("run");
            CancellableHeldMainThread queue = new();
            McpRpcDispatcher dispatcher = new(new McpToolRegistry(new[] { tool }), new McpSessionStore(), queue);

            Task<McpDispatchResult> pending = dispatcher.DispatchAsync(Call(3), "s", CancellationToken.None);
            await dispatcher.DispatchAsync(Cancel(3), "s", CancellationToken.None);
            McpDispatchResult result = await Bounded(pending);

            Assert.IsTrue(queue.Withdrawn, "a cancellable queue gets the request token and frees the slot at once.");
            Assert.AreEqual(0, tool.InvocationCount);
            Assert.IsNotNull(result.Response["error"], "the response arrives without waiting for a frame.");
        }

        [Test]
        public async Task UncancelledCall_ThroughACancellableQueue_RunsNormally()
        {
            FakeMcpTool tool = new("run");
            CancellableHeldMainThread queue = new();
            McpRpcDispatcher dispatcher = new(new McpToolRegistry(new[] { tool }), new McpSessionStore(), queue);

            Task<McpDispatchResult> pending = dispatcher.DispatchAsync(Call(3), "s", CancellationToken.None);
            queue.Release();
            McpDispatchResult result = await Bounded(pending);

            Assert.IsFalse(queue.Withdrawn);
            Assert.AreEqual(1, tool.InvocationCount);
            Assert.IsNull(result.Response["error"]);
        }

        [TestCase("null")]
        [TestCase("{}")]
        [TestCase("true")]
        public async Task CancelledNotification_WithAMalformedRequestId_IsIgnored(string requestId)
        {
            FakeMcpTool tool = new("run");
            HeldMainThread queue = new();
            McpRpcDispatcher dispatcher = new(new McpToolRegistry(new[] { tool }), new McpSessionStore(), queue);

            Task<McpDispatchResult> pending = dispatcher.DispatchAsync(Call(1), null, CancellationToken.None);
            McpDispatchResult notification =
                await dispatcher.DispatchAsync(Cancel(JToken.Parse(requestId)), null, CancellationToken.None);
            queue.Release();
            await Bounded(pending);

            Assert.IsTrue(notification.IsNotification);
            Assert.AreEqual(1, tool.InvocationCount);
        }

        private static async Task<T> Bounded<T>(Task<T> task)
        {
            Assert.AreSame(task, await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(5))),
                "The MCP operation exceeded the bounded test deadline.");
            return await task;
        }

        /// <summary>A game thread that has not reached its next frame yet: work runs only after Release.</summary>
        private sealed class HeldMainThread : IMainThreadDispatcher
        {
            private readonly TaskCompletionSource<bool> _frame = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public void Release() => _frame.TrySetResult(true);

            public async Task<T> RunOnMainThreadAsync<T>(Func<Task<T>> work)
            {
                await _frame.Task.ConfigureAwait(false);
                return await work().ConfigureAwait(false);
            }
        }

        /// <summary>A held game thread that withdraws work whose token is cancelled before Release.</summary>
        private sealed class CancellableHeldMainThread : ICancellableMainThreadDispatcher
        {
            private readonly TaskCompletionSource<bool> _frame = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public bool Withdrawn { get; private set; }
            public void Release() => _frame.TrySetResult(true);

            public Task<T> RunOnMainThreadAsync<T>(Func<Task<T>> work) =>
                RunOnMainThreadAsync(work, CancellationToken.None);

            public async Task<T> RunOnMainThreadAsync<T>(Func<Task<T>> work, CancellationToken cancellationToken)
            {
                TaskCompletionSource<bool> cancelled = new(TaskCreationOptions.RunContinuationsAsynchronously);
                using (cancellationToken.Register(() => cancelled.TrySetResult(true)))
                {
                    if (await Task.WhenAny(_frame.Task, cancelled.Task).ConfigureAwait(false) == cancelled.Task)
                    {
                        Withdrawn = true;
                        throw new OperationCanceledException("withdrawn before the frame", cancellationToken);
                    }
                }

                return await work().ConfigureAwait(false);
            }
        }

        private sealed class BlockingTool : IMcpTool
        {
            public string Name => "blocking";
            public string Description => "Waits until its call is cancelled.";
            public string InputSchemaJson => "{\"type\":\"object\"}";
            public TaskCompletionSource<bool> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public bool ObservedCancellation { get; private set; }

            public async Task<McpToolResult> InvokeAsync(JObject arguments, CancellationToken cancellationToken)
            {
                Started.TrySetResult(true);
                try
                {
                    await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    ObservedCancellation = true;
                }

                return McpToolResult.Text("stopped");
            }
        }
    }
}
