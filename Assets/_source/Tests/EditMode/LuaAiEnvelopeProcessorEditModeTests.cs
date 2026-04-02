using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Ai;
using CoreAI.Messaging;
using CoreAI.Sandbox;
using NUnit.Framework;
using static CoreAI.Messaging.AiGameCommandTypeIds;

namespace CoreAI.Tests.EditMode
{
    public sealed class LuaAiEnvelopeProcessorEditModeTests
    {
        private sealed class ListSink : IAiGameCommandSink
        {
            public readonly List<ApplyAiGameCommand> Items = new List<ApplyAiGameCommand>();

            public void Publish(ApplyAiGameCommand command) => Items.Add(command);
        }

        private sealed class CapturingBindings : IGameLuaRuntimeBindings
        {
            public readonly List<string> Reports = new List<string>();

            public void RegisterGameplayApis(LuaApiRegistry registry)
            {
                registry.Register("report", new Action<string>(s => Reports.Add(s)));
                registry.Register("add", new Func<double, double, double>((a, b) => a + b));
            }
        }

        private sealed class SpyOrchestrator : IAiOrchestrationService
        {
            public int RunCount;
            public AiTaskRequest LastTask;

            public Task RunTaskAsync(AiTaskRequest task, CancellationToken cancellationToken = default)
            {
                RunCount++;
                LastTask = task;
                return Task.CompletedTask;
            }
        }

        [Test]
        public void Processor_GoodLua_PublishesSuccess()
        {
            var sink = new ListSink();
            var bindings = new CapturingBindings();
            var spy = new SpyOrchestrator();
            var proc = new LuaAiEnvelopeProcessor(
                new SecureLuaEnvironment(),
                bindings,
                sink,
                () => spy,
                new NullLuaExecutionObserver());

            proc.Process(new ApplyAiGameCommand
            {
                CommandTypeId = Envelope,
                JsonPayload = "```lua\nreport('ok')\n```",
                SourceRoleId = BuiltInAgentRoleIds.Programmer,
                SourceTaskHint = "t",
                LuaRepairGeneration = 0
            });

            Assert.AreEqual(1, sink.Items.Count);
            Assert.AreEqual(LuaExecutionSucceeded, sink.Items[0].CommandTypeId);
            Assert.AreEqual(1, bindings.Reports.Count);
            Assert.AreEqual("ok", bindings.Reports[0]);
            Assert.AreEqual(0, spy.RunCount);
        }

        [Test]
        public void Processor_BadLua_SchedulesProgrammerRepair()
        {
            var sink = new ListSink();
            var bindings = new CapturingBindings();
            var spy = new SpyOrchestrator();
            var proc = new LuaAiEnvelopeProcessor(
                new SecureLuaEnvironment(),
                bindings,
                sink,
                () => spy,
                new NullLuaExecutionObserver());

            proc.Process(new ApplyAiGameCommand
            {
                CommandTypeId = Envelope,
                JsonPayload = "```lua\nboom()\n```",
                SourceRoleId = BuiltInAgentRoleIds.Programmer,
                SourceTaskHint = "original",
                LuaRepairGeneration = 0
            });

            Assert.That(sink.Items.Count, Is.GreaterThanOrEqualTo(1));
            Assert.AreEqual(LuaExecutionFailed, sink.Items[0].CommandTypeId);
            Assert.AreEqual(1, spy.RunCount);
            Assert.AreEqual(BuiltInAgentRoleIds.Programmer, spy.LastTask.RoleId);
            Assert.AreEqual(1, spy.LastTask.LuaRepairGeneration);
            StringAssert.Contains("boom", spy.LastTask.LuaRepairPreviousCode);
            Assert.IsFalse(string.IsNullOrEmpty(spy.LastTask.LuaRepairErrorMessage));
        }

        [Test]
        public void Processor_NonProgrammer_DoesNotRepair()
        {
            var sink = new ListSink();
            var spy = new SpyOrchestrator();
            var proc = new LuaAiEnvelopeProcessor(
                new SecureLuaEnvironment(),
                new CoreDefaultLuaRuntimeBindings(),
                sink,
                () => spy,
                new NullLuaExecutionObserver());

            proc.Process(new ApplyAiGameCommand
            {
                CommandTypeId = Envelope,
                JsonPayload = "```lua\nundefined_fn()\n```",
                SourceRoleId = BuiltInAgentRoleIds.Creator,
                LuaRepairGeneration = 0
            });

            Assert.AreEqual(1, sink.Items.Count);
            Assert.AreEqual(LuaExecutionFailed, sink.Items[0].CommandTypeId);
            Assert.AreEqual(0, spy.RunCount);
        }

        [Test]
        public void Processor_ProgrammerAtMaxRepairGeneration_DoesNotScheduleAgain()
        {
            var sink = new ListSink();
            var spy = new SpyOrchestrator();
            var proc = new LuaAiEnvelopeProcessor(
                new SecureLuaEnvironment(),
                new CoreDefaultLuaRuntimeBindings(),
                sink,
                () => spy,
                new NullLuaExecutionObserver());

            proc.Process(new ApplyAiGameCommand
            {
                CommandTypeId = Envelope,
                JsonPayload = "```lua\nstill_bad()\n```",
                SourceRoleId = BuiltInAgentRoleIds.Programmer,
                LuaRepairGeneration = 4
            });

            Assert.AreEqual(LuaExecutionFailed, sink.Items[0].CommandTypeId);
            Assert.AreEqual(0, spy.RunCount, "При LuaRepairGeneration >= max цикл ремонта не продолжается");
        }
    }
}
