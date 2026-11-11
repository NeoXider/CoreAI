using System;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Ai;
using CoreAI.Ai.Logging;
using CoreAI.Ai.LuaCs;
using CoreAI.Authority;
using CoreAI.Composition;
using CoreAI.Infrastructure.Logging;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode.SelfRepair
{
    /// <summary>
    /// <c>get_mod_logs</c> as the watch surface after a change: the <c>next_sequence</c> cursor, <c>since_reload</c>
    /// starting after the mod's latest load, and the bounded <c>wait_seconds</c> that answers on the first warning or
    /// error. The wait runs on a stepping marshaler, so no test sleeps.
    /// </summary>
    public sealed class GetModLogsWatchEditModeTests
    {
        [Test]
        public async Task Result_CarriesTheCursor_ForTheNextCall()
        {
            LuaLogService logs = new();
            Append(logs, "m", LuaLogLevel.Print, "one");
            Append(logs, "m", LuaLogLevel.Print, "two");
            GetModLogsLlmTool tool = new(logs);

            JObject first = JObject.Parse(await tool.ExecuteAsync(mod_id: "m"));
            Append(logs, "m", LuaLogLevel.Error, "three");
            JObject next = JObject.Parse(await tool.ExecuteAsync(mod_id: "m",
                since_sequence: first.Value<long>("next_sequence")));

            Assert.AreEqual(2, first.Value<int>("count"));
            Assert.AreEqual(2L, first.Value<long>("next_sequence"));
            Assert.AreEqual(1, next.Value<int>("count"));
            StringAssert.Contains("three", next.Value<string>("logs"));
            Assert.IsNull(next["waited_seconds"], "no wait, no waited field");
        }

        [Test]
        public async Task SinceReload_ShowsOnlyWhatTheNewCodeDid()
        {
            LuaLogService logs = new();
            LuaCsModStack stack = LuaCsModRuntimeFactory.Create(new LuaCsModStackOptions
            {
                Logger = new NullGameLogger(), LogService = logs, AutoPersistMods = false
            });
            ActorContext host = CoreServicesInstaller.DefaultLocalHostIdentityProvider
                .GetActorContext(BuiltInAgentRoleIds.Programmer);
            using LuaModLifecycleLog lifecycle = new(logs, stack.Runtime, host);
            stack.Runtime.LoadMod(host, "tower", "hooks_every(0.05, function() error('old bug') end)",
                LuaCapabilities.All, false);
            stack.Runtime.Tick(host, 0.1d);
            stack.Runtime.ReloadMod(host, "tower", "hooks_every(0.05, function() print('fixed') end)");
            stack.Runtime.Tick(host, 0.1d);
            GetModLogsLlmTool tool = new(logs, null, lifecycle);

            JObject result = JObject.Parse(await tool.ExecuteAsync(mod_id: "tower", since_reload: true));

            string text = result.Value<string>("logs");
            StringAssert.DoesNotContain("old bug", text);
            StringAssert.Contains("fixed", text);
            Assert.IsTrue(lifecycle.TryGetLastLoadSequence("tower", out long sequence));
            Assert.Greater(sequence, 0L);
        }

        [Test]
        public async Task SinceReload_AfterAGatedLoad_IncludesWhatTheNewMainChunkPrinted()
        {
            LuaLogService logs = new();
            LuaCsModStack stack = LuaCsModRuntimeFactory.Create(new LuaCsModStackOptions
            {
                Logger = new NullGameLogger(), LogService = logs, AutoPersistMods = false
            });
            ActorContext host = CoreServicesInstaller.DefaultLocalHostIdentityProvider
                .GetActorContext(BuiltInAgentRoleIds.Programmer);
            using LuaModLifecycleLog lifecycle = new(logs, stack.Runtime, host);
            stack.Runtime.LoadMod(host, "tower", "print('old')", LuaCapabilities.All, false);
            CoreAI.Mods.WorldPackages.RbxWorldMutationInfo mutation = new("manage_mods-reload", host.ActorId, 1);

            lifecycle.OnMutationStarting(mutation);
            stack.Runtime.ReloadMod(host, "tower", "print('new main chunk')");
            lifecycle.OnMutationFinished(mutation, null, true);
            JObject result = JObject.Parse(await new GetModLogsLlmTool(logs, null, lifecycle).ExecuteAsync(
                mod_id: "tower", since_reload: true));

            string text = result.Value<string>("logs");
            StringAssert.Contains("new main chunk", text);
            StringAssert.DoesNotContain("old", text);
        }

        [Test]
        public async Task SinceReload_WithoutTheRecorder_FindsTheLoadLineInTheLog()
        {
            LuaLogService logs = new();
            Append(logs, "tower", LuaLogLevel.RuntimeError, "old bug");
            logs.Append(new LuaLogEntry
            {
                ModId = "tower", Level = LuaLogLevel.Print, Message = LuaModLifecycleLog.ReloadedMessage,
                ScriptName = LuaModLifecycleLog.LifecycleScriptName
            });
            Append(logs, "tower", LuaLogLevel.Print, "fixed");

            JObject result = JObject.Parse(await new GetModLogsLlmTool(logs).ExecuteAsync(mod_id: "tower",
                since_reload: true));

            StringAssert.DoesNotContain("old bug", result.Value<string>("logs"));
            StringAssert.Contains("fixed", result.Value<string>("logs"));
        }

        [Test]
        public async Task SinceReload_WithoutModId_IsRefused()
        {
            JObject result = JObject.Parse(await new GetModLogsLlmTool(new LuaLogService()).ExecuteAsync(
                since_reload: true));

            Assert.IsFalse(result.Value<bool>("success"));
            StringAssert.Contains("since_reload needs mod_id", result.Value<string>("message"));
        }

        [Test]
        public async Task Wait_AnswersAsSoonAsAnErrorArrives()
        {
            LuaLogService logs = new();
            SteppingMarshaler marshaler = new();
            marshaler.OnStep = step =>
            {
                if (step == 3)
                {
                    Append(logs, "tower", LuaLogLevel.RuntimeError, "new bug");
                }
            };
            GetModLogsLlmTool tool = new(logs, marshaler);

            JObject result = JObject.Parse(await tool.ExecuteAsync(mod_id: "tower", wait_seconds: 5));

            Assert.AreEqual(3, marshaler.Steps, "it stopped waiting at the step the error arrived");
            Assert.AreEqual(0.3d, result.Value<double>("waited_seconds"), 0.0001d);
            StringAssert.Contains("new bug", result.Value<string>("logs"));
        }

        [Test]
        public async Task Wait_RunsTheWholeWindow_WhenNothingFails_AndPrintsDoNotEndIt()
        {
            LuaLogService logs = new();
            SteppingMarshaler marshaler = new();
            marshaler.OnStep = step =>
            {
                Append(logs, "tower", LuaLogLevel.Print, "tick " + step);
                Append(logs, "other", LuaLogLevel.RuntimeError, "not mine");
            };
            GetModLogsLlmTool tool = new(logs, marshaler);

            JObject result = JObject.Parse(await tool.ExecuteAsync(mod_id: "tower", wait_seconds: 2));

            Assert.AreEqual(20, marshaler.Steps, "2 s in 100 ms steps");
            Assert.AreEqual(2d, result.Value<double>("waited_seconds"), 0.0001d);
            StringAssert.DoesNotContain("not mine", result.Value<string>("logs"));
        }

        [Test]
        public async Task Wait_AnErrorAlreadyThere_AnswersAtOnce()
        {
            LuaLogService logs = new();
            Append(logs, "tower", LuaLogLevel.Warn, "already");
            SteppingMarshaler marshaler = new();

            JObject result = JObject.Parse(await new GetModLogsLlmTool(logs, marshaler).ExecuteAsync(mod_id: "tower",
                wait_seconds: 5));

            Assert.AreEqual(0, marshaler.Steps);
            Assert.AreEqual(0d, result.Value<double>("waited_seconds"));
        }

        [Test]
        public async Task Wait_IsCappedAtTenSeconds()
        {
            SteppingMarshaler marshaler = new();

            await new GetModLogsLlmTool(new LuaLogService(), marshaler).ExecuteAsync(wait_seconds: 600);

            Assert.AreEqual(GetModLogsLlmTool.MaxWaitSeconds * 1000 / GetModLogsLlmTool.WaitStepMilliseconds,
                marshaler.Steps);
        }

        [Test]
        public async Task Wait_IsCancellable()
        {
            using CancellationTokenSource cancel = new();
            SteppingMarshaler marshaler = new() { OnStep = _ => cancel.Cancel() };
            GetModLogsLlmTool tool = new(new LuaLogService(), marshaler);
            bool cancelled = false;

            try
            {
                await tool.ExecuteAsync(wait_seconds: 5, cancellationToken: cancel.Token);
            }
            catch (OperationCanceledException)
            {
                cancelled = true;
            }

            Assert.IsTrue(cancelled);
        }

        [Test]
        public async Task Wait_WithoutAHostScheduler_AnswersAtOnce()
        {
            JObject result = JObject.Parse(await new GetModLogsLlmTool(new LuaLogService()).ExecuteAsync(
                mod_id: "tower", wait_seconds: 5));

            Assert.IsTrue(result.Value<bool>("success"));
            Assert.AreEqual(0d, result.Value<double>("waited_seconds"),
                "a tool built without a scheduler never waits, so it cannot hang a WebGL turn");
        }

        [Test]
        public async Task TheMcpShapedOverload_DoesNotWait()
        {
            SteppingMarshaler marshaler = new();
            GetModLogsLlmTool tool = new(new LuaLogService(), marshaler);

            JObject result = JObject.Parse(await tool.ExecuteAsync(null, null, 0L, 50, CancellationToken.None));

            Assert.IsTrue(result.Value<bool>("success"));
            Assert.AreEqual(0, marshaler.Steps);
        }

        [Test]
        public void Contract_StaysWithinItsTokenBudget()
        {
            PromptTokenCostMeter meter = new();
            GetModLogsLlmTool tool = new(new LuaLogService());
            int tokens = meter.CountTool(tool);
            TestContext.WriteLine(meter.FormatTextRow("`get_mod_logs`", PromptTokenCostMeter.ToProviderToolsJson(tool)));

            Assert.LessOrEqual(tokens, 280, "the watch shape costs at most 41 tokens over the 239 it cost before");
        }

        private static void Append(LuaLogService logs, string modId, LuaLogLevel level, string message)
        {
            logs.Append(new LuaLogEntry { ModId = modId, Level = level, Message = message });
        }

        /// <summary>A delay that completes at once and runs a callback per step, so a wait is deterministic.</summary>
        private sealed class SteppingMarshaler : ILlmAsyncMarshaler
        {
            public int Steps { get; private set; }

            public Action<int> OnStep { get; set; }

            public Task<T> InvokeAsync<T>(Func<Task<T>> factory, CancellationToken cancellationToken)
            {
                return factory();
            }

            public Task DelayAsync(int milliseconds, CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Steps++;
                OnStep?.Invoke(Steps);
                return Task.CompletedTask;
            }
        }
    }
}
