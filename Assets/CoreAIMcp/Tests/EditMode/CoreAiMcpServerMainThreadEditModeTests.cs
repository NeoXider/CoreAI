using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Ai;
using CoreAI.Authority;
using CoreAI.Composition;
using CoreAI.Infrastructure.Llm;
using CoreAI.Infrastructure.Logging;
using CoreAI.Mcp.Server;
using CoreAI.Mcp.Tools;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using VContainer;

namespace CoreAI.Mcp.Tests
{
    /// <summary>
    /// The main-thread marshalling contract of <see cref="CoreAiMcpServer"/>. EditMode is the perfect
    /// stand-in for a paused game: <c>Update</c> never runs, so a queued <c>tools/call</c> would hang
    /// forever without the timeout and the shutdown drain.
    /// </summary>
    public sealed class CoreAiMcpServerMainThreadEditModeTests
    {
        private const string MissingWorldHostLog =
            "[CoreAI] [Core] [CoreAiMods] RbxWorldHost NOT resolved — mods run headless. " +
            "Instance.new / workspace mutations produce no GameObjects. " +
            "Check: (1) RbxWorldHost component exists in the scene, " +
            "(2) CoreAiModsLifetimeScope.robloxWorldHost is wired to it, " +
            "(3) link.xml preserves CoreAI.RbxApi.Binding assembly.";

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

        [Test]
        public void BuildRegistry_ManageModsRequiresExplicitUnrestrictedHostAdminAdmission()
        {
            CoreAISettingsAsset settings = ScriptableObject.CreateInstance<CoreAISettingsAsset>();
            IObjectResolver container = null;
            try
            {
                ContainerBuilder builder = new ContainerBuilder();
                builder.Register<DefaultGameLogSettings>(Lifetime.Singleton).As<IGameLogSettings>();
                builder.RegisterCore();
                builder.RegisterInstance<ICoreAISettings, CoreAISettingsAsset>(settings);
                builder.Register<AgentMemoryPolicy>(Lifetime.Singleton);
                builder.Register(_ => new LuaGenerationRateLimiter(), Lifetime.Singleton);
                builder.RegisterCoreAiMods(
                    applicationIsPlayingProvider: () => false,
                    skillTextProvider: _ => null);
                LogAssert.Expect(LogType.Error, MissingWorldHostLog);
                container = builder.Build();

                McpToolRegistry defaultRegistry = _server.BuildRegistry(container);
                Assert.IsFalse(defaultRegistry.Contains("manage_mods"),
                    "A composed mod runtime alone must not grant MCP host-admin authority.");

                LocalActorIdentityProvider restricted = new LocalActorIdentityProvider("restricted-mcp");
                Assert.Throws<ArgumentException>(() =>
                    _server.ConfigureHostAdminModManagement(restricted));
                Assert.IsFalse(_server.BuildRegistry(container).Contains("manage_mods"),
                    "A restricted actor rejected by admission must leave manage_mods omitted.");

                IActorIdentityProvider hostAdmin = container.Resolve<IActorIdentityProvider>();
                ActorContext admittedActor = hostAdmin.GetActorContext(BuiltInAgentRoleIds.Programmer);
                Assert.IsTrue(admittedActor.Grants.IsUnrestricted);
                _server.ConfigureHostAdminModManagement(hostAdmin);

                McpToolRegistry admittedRegistry = _server.BuildRegistry(container);
                Assert.IsTrue(admittedRegistry.Contains("manage_mods"),
                    "The explicit unrestricted host-admin identity must reach the shipped MCP registry.");
                Assert.AreEqual(admittedActor.ActorId, _server.HostAdminActorId);
            }
            finally
            {
                container?.Dispose();
                UnityEngine.Object.DestroyImmediate(settings);
            }
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task AdmissionCapacity_BoundsQueuedAndRunningWork(bool startBodies)
        {
            _server.MainThreadTimeoutSeconds = 0;
            TaskCompletionSource<int> release = new(TaskCreationOptions.RunContinuationsAsynchronously);
            List<Task<int>> admitted = new();
            int started = 0;
            try
            {
                for (int index = 0; index < CoreAiMcpServer.MainThreadCallCapacity; index++)
                    admitted.Add(_server.RunOnMainThreadAsync(() => { started++; return release.Task; }));
                if (startBodies) _server.PumpMainThreadQueue(TimeSpan.MaxValue);
                Exception rejected = await CaptureAsync(_server.RunOnMainThreadAsync(() => Task.FromResult(-1)));
                Assert.IsInstanceOf<InvalidOperationException>(rejected);
                Assert.AreEqual(CoreAiMcpServer.MainThreadCallCapacity, _server.AdmittedMainThreadCalls);
                Assert.AreEqual(startBodies ? admitted.Count : 0, started);
            }
            finally
            {
                release.TrySetResult(7);
                _server.PumpMainThreadQueue(TimeSpan.MaxValue);
                await Task.WhenAll(admitted);
            }
            Assert.AreEqual(0, _server.AdmittedMainThreadCalls);
            Task<int> next = _server.RunOnMainThreadAsync(() => Task.FromResult(8));
            _server.PumpMainThreadQueue();
            Assert.AreEqual(8, await next);
        }

        [Test]
        public async Task QueueTimeout_ReclaimsResidencyAcrossRepeatedUnpumpedWaves()
        {
            _server.MainThreadTimeoutSeconds = 0.05f;
            int executed = 0;
            for (int wave = 0; wave < 3; wave++)
            {
                List<Task<int>> pending = new();
                for (int index = 0; index < CoreAiMcpServer.MainThreadCallCapacity; index++)
                    pending.Add(_server.RunOnMainThreadAsync(() => Task.FromResult(++executed)));
                foreach (Task<int> call in pending)
                    Assert.IsInstanceOf<TimeoutException>(await CaptureAsync(call));
                Assert.AreEqual(0, _server.AdmittedMainThreadCalls,
                    "A paused host must recover capacity without retaining timed-out queued requests.");
            }
            _server.PumpMainThreadQueue(TimeSpan.MaxValue);
            Assert.AreEqual(0, executed);
        }

        [TestCase("result")]
        [TestCase("exception")]
        [TestCase("cancel")]
        public async Task QueueDeadline_CannotFinishAnAlreadyStartedBody(string outcome)
        {
            _server.MainThreadTimeoutSeconds = 0.05f;
            TaskCompletionSource<int> body = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Task<int> running = _server.RunOnMainThreadAsync(() => body.Task);
            _server.PumpMainThreadQueue();
            try
            {
                Task<int> laterQueued = _server.RunOnMainThreadAsync(() => Task.FromResult(-1));
                Assert.IsInstanceOf<TimeoutException>(await CaptureAsync(laterQueued));
                Assert.IsFalse(running.IsCompleted, "The queue deadline must not report fake completion of a running mutation.");
                Assert.AreEqual(1, _server.AdmittedMainThreadCalls);
                if (outcome == "exception") body.SetException(new InvalidOperationException("body failure"));
                else if (outcome == "cancel") body.SetCanceled();
                else body.SetResult(42);
                Exception failure = await CaptureAsync(running);
                if (outcome == "exception") Assert.IsInstanceOf<InvalidOperationException>(failure);
                else if (outcome == "cancel") Assert.IsInstanceOf<OperationCanceledException>(failure);
                else { Assert.IsNull(failure); Assert.AreEqual(42, await running); }
                Assert.AreEqual(0, _server.AdmittedMainThreadCalls);
            }
            finally
            {
                body.TrySetResult(0);
                await CaptureAsync(running);
            }
        }

        [Test]
        public async Task Stop_ReclaimsOnlyQueuedCallsAndKeepsRunningLeasesUntilActualCompletion()
        {
            _server.MainThreadTimeoutSeconds = 0;
            TaskCompletionSource<int> body = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Task<int> running = _server.RunOnMainThreadAsync(() => body.Task);
            _server.PumpMainThreadQueue();
            Task<int> queued = _server.RunOnMainThreadAsync(() => Task.FromResult(-1));
            try
            {
                _server.StopListening();
                Assert.IsInstanceOf<OperationCanceledException>(await CaptureAsync(queued));
                Assert.IsFalse(running.IsCompleted);
                Assert.AreEqual(1, _server.AdmittedMainThreadCalls);
                Task<int> unrelated = _server.RunOnMainThreadAsync(() => Task.FromResult(9));
                _server.PumpMainThreadQueue();
                Assert.AreEqual(9, await unrelated, "Independent work can use remaining capacity.");
                Assert.AreEqual(1, _server.AdmittedMainThreadCalls);
            }
            finally { body.TrySetResult(4); await running; }
            Assert.AreEqual(0, _server.AdmittedMainThreadCalls);
        }

        [Test]
        public async Task Pump_SelfEnqueuedChildrenWaitForAnotherFrame()
        {
            _server.MainThreadTimeoutSeconds = 0;
            List<Task<int>> pending = new();
            int started = 0;
            int finiteWitness = CoreAiMcpServer.MainThreadCallCapacity + 2;
            Func<Task<int>> body = null;
            body = () =>
            {
                started++;
                if (started < finiteWitness) pending.Add(_server.RunOnMainThreadAsync(body));
                return Task.FromResult(started);
            };
            pending.Add(_server.RunOnMainThreadAsync(body));
            try
            {
                _server.PumpMainThreadQueue(TimeSpan.MaxValue);
                Assert.AreEqual(1, started, "A self-enqueuing body must not monopolize the frame.");
                _server.PumpMainThreadQueue(TimeSpan.MaxValue);
                Assert.AreEqual(2, started);
            }
            finally
            {
                for (int index = 0; index < finiteWitness; index++) _server.PumpMainThreadQueue(TimeSpan.MaxValue);
                await Task.WhenAll(pending);
            }
        }

        [Test]
        public async Task Pump_ZeroFrameBudgetStartsOneCallAndPreservesRemainingOrder()
        {
            _server.MainThreadTimeoutSeconds = 0;
            List<int> order = new();
            Task<int> first = _server.RunOnMainThreadAsync(() => { order.Add(1); return Task.FromResult(1); });
            Task<int> second = _server.RunOnMainThreadAsync(() => { order.Add(2); return Task.FromResult(2); });
            try
            {
                _server.PumpMainThreadQueue(TimeSpan.Zero);
                CollectionAssert.AreEqual(new[] { 1 }, order);
                Assert.IsFalse(second.IsCompleted);
                _server.PumpMainThreadQueue(TimeSpan.Zero);
                CollectionAssert.AreEqual(new[] { 1, 2 }, order);
            }
            finally { _server.PumpMainThreadQueue(TimeSpan.MaxValue); await Task.WhenAll(first, second); }
        }

        private static async Task<Exception> CaptureAsync(Task pending)
        {
            Task completed = await Task.WhenAny(pending, Task.Delay(TimeSpan.FromSeconds(5)));
            Assert.AreSame(pending, completed, "The MCP operation exceeded the bounded test deadline.");
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
