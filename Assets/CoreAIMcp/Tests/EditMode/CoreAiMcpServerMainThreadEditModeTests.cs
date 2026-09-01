using System;
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
