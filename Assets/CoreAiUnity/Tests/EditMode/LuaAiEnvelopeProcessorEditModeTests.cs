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
            public readonly List<ApplyAiGameCommand> Items = new();

            public void Publish(ApplyAiGameCommand command)
            {
                Items.Add(command);
            }
        }

        private sealed class CapturingBindings : IGameLuaRuntimeBindings
        {
            public readonly List<string> Reports = new();

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
            ListSink sink = new();
            CapturingBindings bindings = new();
            SpyOrchestrator spy = new();
            MemoryLuaScriptVersionStore versions = new();
            LuaAiEnvelopeProcessor proc = new(
                new SecureLuaEnvironment(),
                bindings,
                sink,
                () => spy,
                new NullLuaExecutionObserver(),
                versions);

            proc.Process(new ApplyAiGameCommand
            {
                CommandTypeId = Envelope,
                JsonPayload = "```lua\nreport('ok')\n```",
                SourceRoleId = BuiltInAgentRoleIds.Programmer,
                SourceTaskHint = "t",
                LuaRepairGeneration = 0,
                LuaScriptVersionKey = "vslot",
                DataOverlayVersionKeysCsv = "arena.cfg;meta"
            });

            Assert.AreEqual(1, sink.Items.Count);
            Assert.AreEqual(LuaExecutionSucceeded, sink.Items[0].CommandTypeId);
            Assert.AreEqual("vslot", sink.Items[0].LuaScriptVersionKey);
            Assert.AreEqual(1, bindings.Reports.Count);
            Assert.AreEqual("ok", bindings.Reports[0]);
            Assert.AreEqual(0, spy.RunCount);
            Assert.IsTrue(versions.TryGetSnapshot("vslot", out LuaScriptVersionRecord vs));
            StringAssert.Contains("report('ok')", vs.CurrentLua);
        }

        [Test]
        public void Processor_BadLua_SchedulesProgrammerRepair()
        {
            ListSink sink = new();
            CapturingBindings bindings = new();
            SpyOrchestrator spy = new();
            LuaAiEnvelopeProcessor proc = new(
                new SecureLuaEnvironment(),
                bindings,
                sink,
                () => spy,
                new NullLuaExecutionObserver(),
                new NullLuaScriptVersionStore());

            proc.Process(new ApplyAiGameCommand
            {
                CommandTypeId = Envelope,
                JsonPayload = "```lua\nboom()\n```",
                SourceRoleId = BuiltInAgentRoleIds.Programmer,
                SourceTaskHint = "original",
                LuaRepairGeneration = 0,
                LuaScriptVersionKey = "repair_slot",
                DataOverlayVersionKeysCsv = "d1,d2"
            });

            Assert.That(sink.Items.Count, Is.GreaterThanOrEqualTo(1));
            Assert.AreEqual(LuaExecutionFailed, sink.Items[0].CommandTypeId);
            Assert.AreEqual(1, spy.RunCount);
            Assert.AreEqual(BuiltInAgentRoleIds.Programmer, spy.LastTask.RoleId);
            Assert.AreEqual("repair_slot", spy.LastTask.LuaScriptVersionKey);
            Assert.AreEqual("d1,d2", spy.LastTask.DataOverlayVersionKeysCsv);
            Assert.AreEqual(1, spy.LastTask.LuaRepairGeneration);
            StringAssert.Contains("boom", spy.LastTask.LuaRepairPreviousCode);
            Assert.IsFalse(string.IsNullOrEmpty(spy.LastTask.LuaRepairErrorMessage));
        }

        [Test]
        public void Processor_NonProgrammer_DoesNotRepair()
        {
            ListSink sink = new();
            SpyOrchestrator spy = new();
            LuaAiEnvelopeProcessor proc = new(
                new SecureLuaEnvironment(),
                new CoreDefaultLuaRuntimeBindings(),
                sink,
                () => spy,
                new NullLuaExecutionObserver(),
                new NullLuaScriptVersionStore());

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
            int originalMax = CoreAISettings.MaxLuaRepairRetries;
            CoreAISettings.MaxLuaRepairRetries = 3;

            try
            {
                ListSink sink = new();
                SpyOrchestrator spy = new();
                LuaAiEnvelopeProcessor proc = new(
                    new SecureLuaEnvironment(),
                    new CoreDefaultLuaRuntimeBindings(),
                    sink,
                    () => spy,
                    new NullLuaExecutionObserver(),
                    new NullLuaScriptVersionStore());

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
            finally
            {
                CoreAISettings.MaxLuaRepairRetries = originalMax;
            }
        }
    }
}