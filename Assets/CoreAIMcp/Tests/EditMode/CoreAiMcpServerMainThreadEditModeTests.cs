using System;
using System.Threading.Tasks;
using CoreAI.Mcp.Server;
using NUnit.Framework;
using UnityEngine;

namespace CoreAI.Mcp.Tests
{
    /// <summary>
    /// The main-thread marshalling contract of <see cref="CoreAiMcpServer"/>. EditMode is the perfect
    /// stand-in for a paused game: <c>Update</c> never runs, so a queued <c>tools/call</c> would hang
    /// forever without the timeout and the shutdown drain.
    /// </summary>
    public sealed class CoreAiMcpServerMainThreadEditModeTests
    {
        private GameObject _host;
        private CoreAiMcpServer _server;

        [SetUp]
        public void SetUp()
        {
            _host = new GameObject("CoreAiMcpServerTestHost");
            _server = _host.AddComponent<CoreAiMcpServer>();
        }

        [TearDown]
        public void TearDown()
        {
            if (_host != null)
            {
                UnityEngine.Object.DestroyImmediate(_host);
            }

            _host = null;
            _server = null;
        }

        [Test]
        public async Task RunOnMainThreadAsync_WhenQueueIsNeverDrained_TimesOutWithAnActionableReason()
        {
            _server.MainThreadTimeoutSeconds = 0.25f;

            Task<string> pending = _server.RunOnMainThreadAsync(() => Task.FromResult("never runs"));

            Exception observed = await CaptureAsync(pending);

            Assert.IsInstanceOf<TimeoutException>(observed,
                "a tools/call must fail, not hang, when the player loop is not pumping.");
            StringAssert.Contains("paused", observed.Message);
            StringAssert.Contains("disabled", observed.Message);
        }

        [Test]
        public async Task PumpMainThreadQueue_RunsQueuedWorkAndReturnsItsResult()
        {
            Task<int> pending = _server.RunOnMainThreadAsync(() => Task.FromResult(42));

            _server.PumpMainThreadQueue();

            Assert.AreEqual(42, await pending);
        }

        [Test]
        public async Task StopListening_FailsCallsStillWaitingInTheQueue()
        {
            // WHY: without this the TaskCompletionSource - and the HTTP worker awaiting it - leaks forever.
            Task<int> pending = _server.RunOnMainThreadAsync(() => Task.FromResult(1));

            _server.StopListening();

            Assert.IsTrue(pending.IsCompleted, "a stop must resolve every queued call immediately.");
            Exception observed = await CaptureAsync(pending);
            Assert.IsInstanceOf<OperationCanceledException>(observed);
        }

        [Test]
        public async Task TimedOutCall_IsNotExecutedByALaterPump()
        {
            _server.MainThreadTimeoutSeconds = 0.25f;
            bool executed = false;

            Task<int> pending = _server.RunOnMainThreadAsync(() =>
            {
                executed = true;
                return Task.FromResult(7);
            });

            await CaptureAsync(pending);
            _server.PumpMainThreadQueue();

            Assert.IsFalse(executed,
                "a call the client already gave up on must not mutate the game later.");
        }

        [Test]
        public async Task ZeroTimeout_DisablesTheWatchdog()
        {
            _server.MainThreadTimeoutSeconds = 0f;

            Task<int> pending = _server.RunOnMainThreadAsync(() => Task.FromResult(5));
            await Task.Delay(100);

            Assert.IsFalse(pending.IsCompleted, "timeout 0 must mean 'wait for the main thread'.");
            _server.PumpMainThreadQueue();
            Assert.AreEqual(5, await pending);
        }

        private static async Task<Exception> CaptureAsync(Task pending)
        {
            try
            {
                await pending;
                return null;
            }
            catch (Exception ex)
            {
                return ex;
            }
        }
    }
}
