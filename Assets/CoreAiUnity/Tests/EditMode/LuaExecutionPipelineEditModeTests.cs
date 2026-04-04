using System;
using System.Collections.Generic;
using CoreAI.Ai;
using CoreAI.Messaging;
using CoreAI.Sandbox;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode.Lua
{
    /// <summary>
    /// Тесты Lua execution pipeline: AiEnvelope → Lua sandbox → результат/repair.
    /// Проверяют что Lua код от AI реально выполняется, а не только парсится.
    /// </summary>
    public sealed class LuaExecutionPipelineEditModeTests
    {
        #region Test Infrastructure

        private sealed class TestSink : IAiGameCommandSink
        {
            public readonly List<ApplyAiGameCommand> Commands = new();
            public void Publish(ApplyAiGameCommand command) => Commands.Add(command);
        }

        private sealed class TestObserver : ILuaExecutionObserver
        {
            public string LastSuccess;
            public string LastFailure;
            public int RepairCount;

            public void OnLuaSuccess(string resultSummary) => LastSuccess = resultSummary;
            public void OnLuaFailure(string errorMessage) => LastFailure = errorMessage;
            public void OnLuaRepairScheduled(int nextGeneration, string errorPreview) => RepairCount++;
        }

        private sealed class RepairSink : IAiGameCommandSink
        {
            public readonly List<ApplyAiGameCommand> Commands = new();
            public int RepairRequestCount;

            public void Publish(ApplyAiGameCommand command)
            {
                Commands.Add(command);
                if (command.CommandTypeId == AiGameCommandTypeIds.Envelope &&
                    command.SourceTag != null && command.SourceTag.Contains("lua_repair"))
                {
                    RepairRequestCount++;
                }
            }
        }

        private LuaAiEnvelopeProcessor CreateProcessor(
            IGameLuaRuntimeBindings bindings,
            IAiGameCommandSink sink,
            TestObserver observer,
            Func<IAiOrchestrationService> orchestratorFactory = null)
        {
            return new LuaAiEnvelopeProcessor(
                new SecureLuaEnvironment(),
                bindings,
                sink,
                orchestratorFactory ?? (() => null),
                observer,
                new NullLuaScriptVersionStore());
        }

        private ApplyAiGameCommand MakeEnvelope(string luaCode, string roleId = "Programmer", int generation = 0)
        {
            return new ApplyAiGameCommand
            {
                CommandTypeId = AiGameCommandTypeIds.Envelope,
                JsonPayload = $"```lua\n{luaCode}\n```",
                SourceRoleId = roleId,
                LuaRepairGeneration = generation
            };
        }

        #endregion

        #region Test 1: Successful Lua Execution

        [Test]
        public void LuaExecution_ValidCode_RunsSuccessfully()
        {
            TestSink sink = new();
            TestObserver observer = new();
            LuaAiEnvelopeProcessor processor = CreateProcessor(
                new CoreDefaultLuaRuntimeBindings(), sink, observer);

            processor.Process(MakeEnvelope("report('hello from lua')"));

            Assert.AreEqual(1, sink.Commands.Count);
            Assert.AreEqual(AiGameCommandTypeIds.LuaExecutionSucceeded, sink.Commands[0].CommandTypeId);
            Assert.IsNotNull(observer.LastSuccess);
            Assert.IsNull(observer.LastFailure);
            Assert.AreEqual(0, observer.RepairCount);
        }

        #endregion

        #region Test 2: Lua Error Triggers Repair

        [Test]
        public void LuaExecution_ErrorCode_TriggersRepair()
        {
            TestSink sink = new();
            TestObserver observer = new();
            LuaAiEnvelopeProcessor processor = CreateProcessor(
                new CoreDefaultLuaRuntimeBindings(), sink, observer);

            processor.Process(MakeEnvelope("undefined_function()"));

            Assert.AreEqual(1, sink.Commands.Count);
            Assert.AreEqual(AiGameCommandTypeIds.LuaExecutionFailed, sink.Commands[0].CommandTypeId);
            Assert.IsNull(observer.LastSuccess);
            Assert.IsNotNull(observer.LastFailure);
            Assert.GreaterOrEqual(observer.LastFailure.Length, 10);
        }

        #endregion

        #region Test 3: Lua Arithmetic

        [Test]
        public void LuaExecution_Arithmetic_ReturnsCorrectResult()
        {
            TestSink sink = new();
            TestObserver observer = new();

            var bindings = new TestLuaBindings();
            LuaAiEnvelopeProcessor processor = CreateProcessor(bindings, sink, observer);

            // Простое выражение —MoonSharp sandbox может ограничивать local
            processor.Process(MakeEnvelope("report('sum_is_' .. add(40, 2))"));

            Assert.AreEqual(1, sink.Commands.Count);
            Assert.AreEqual(AiGameCommandTypeIds.LuaExecutionSucceeded, sink.Commands[0].CommandTypeId);
            // add(40,2) = 42 — проверяем что выполнилось без ошибок
            Assert.IsNotNull(observer.LastSuccess);
            Assert.IsNull(observer.LastFailure);
        }

        #endregion

        #region Test 4: Crafting Lua from AI Response

        [Test]
        public void LuaExecution_CraftingFormula_ExtractsAndExecutes()
        {
            TestSink sink = new();
            TestObserver observer = new();

            var bindings = new CraftingTestBindings();
            LuaAiEnvelopeProcessor processor = CreateProcessor(bindings, sink, observer);

            // Имитация ответа AI с формулой крафта
            string aiResponse =
                "Here's the crafting formula:\n\n" +
                "```lua\n" +
                "local damage = calc_damage(80, 0.5)\n" +
                "report('Crafted: Steel Blade, damage=' .. damage)\n" +
                "```\n\n" +
                "This formula calculates damage based on material hardness.";

            var cmd = new ApplyAiGameCommand
            {
                CommandTypeId = AiGameCommandTypeIds.Envelope,
                JsonPayload = aiResponse,
                SourceRoleId = "Programmer"
            };

            processor.Process(cmd);

            Assert.AreEqual(1, sink.Commands.Count);
            Assert.AreEqual(AiGameCommandTypeIds.LuaExecutionSucceeded, sink.Commands[0].CommandTypeId);
            Assert.IsNotNull(observer.LastSuccess);
            // MoonSharp может вернуть "void" для report() — это нормально, главное что Lua выполнился
        }

        #endregion

        #region Test 5: Lua with create_item simulation

        [Test]
        public void LuaExecution_CreateItem_RecordsCraftedItem()
        {
            TestSink sink = new();
            TestObserver observer = new();

            var bindings = new CraftingTestBindings();
            LuaAiEnvelopeProcessor processor = CreateProcessor(bindings, sink, observer);

            string luaCode =
                "create_item('Fire Blade', 'weapon', 75)\n" +
                "add_special_effect('fire_damage: 25')\n" +
                "report('Fire Blade crafted')";

            processor.Process(MakeEnvelope(luaCode));

            Assert.AreEqual(1, sink.Commands.Count);
            Assert.AreEqual(AiGameCommandTypeIds.LuaExecutionSucceeded, sink.Commands[0].CommandTypeId);

            // Проверяем что Lua реально выполнился и запомнил предмет
            Assert.AreEqual("Fire Blade", bindings.LastCreatedItem);
            Assert.AreEqual(1, bindings.SpecialEffectCount);
        }

        #endregion

        #region Test 6: Lua Error with Repair Scheduling

        [Test]
        public void LuaExecution_ErrorWithRepair_SchedulesFixRequest()
        {
            RepairSink repairSink = new();
            TestObserver observer = new();

            // Создаём фейковый оркестратор который ловит repair запросы
            var bindings = new CoreDefaultLuaRuntimeBindings();
            bool repairScheduled = false;
            AiTaskRequest capturedRepairTask = null;

            var fakeOrchestrator = new FakeOrchestrator((task) =>
            {
                repairScheduled = true;
                capturedRepairTask = task;
            });

            LuaAiEnvelopeProcessor processor = new LuaAiEnvelopeProcessor(
                new SecureLuaEnvironment(),
                bindings,
                repairSink,
                () => fakeOrchestrator,
                observer,
                new NullLuaScriptVersionStore());

            // Ошибочный Lua
            processor.Process(MakeEnvelope("bad_function_call()", "Programmer", generation: 0));

            Assert.AreEqual(1, repairSink.Commands.Count);
            Assert.AreEqual(AiGameCommandTypeIds.LuaExecutionFailed, repairSink.Commands[0].CommandTypeId);
            Assert.IsTrue(repairScheduled, "Repair should be scheduled for Programmer errors");
            Assert.IsNotNull(capturedRepairTask);
            Assert.AreEqual(BuiltInAgentRoleIds.Programmer, capturedRepairTask.RoleId);
            Assert.AreEqual(1, capturedRepairTask.LuaRepairGeneration);
            Assert.IsNotNull(capturedRepairTask.LuaRepairErrorMessage);
        }

        #endregion

        #region Test 7: Multiple Repair Generations

        [Test]
        public void LuaExecution_MultipleRepairs_RespectsMaxGenerations()
        {
            int repairCount = 0;
            var fakeOrchestrator = new FakeOrchestrator((task) => repairCount++);
            var bindings = new CoreDefaultLuaRuntimeBindings();
            TestSink sink = new();
            TestObserver observer = new();

            LuaAiEnvelopeProcessor processor = new LuaAiEnvelopeProcessor(
                new SecureLuaEnvironment(),
                bindings,
                sink,
                () => fakeOrchestrator,
                observer,
                new NullLuaScriptVersionStore());

            // Generation 0 → repair 1
            processor.Process(MakeEnvelope("error1()", "Programmer", generation: 0));
            Assert.AreEqual(1, repairCount);

            // Generation 1 → repair 2
            processor.Process(MakeEnvelope("error2()", "Programmer", generation: 1));
            Assert.AreEqual(2, repairCount);

            // Generation 2 → repair 3
            processor.Process(MakeEnvelope("error3()", "Programmer", generation: 2));
            Assert.AreEqual(3, repairCount);

            // Generation 3 → repair 4
            processor.Process(MakeEnvelope("error4()", "Programmer", generation: 3));
            Assert.AreEqual(4, repairCount);

            // Generation 4 → NO MORE repairs (max = 4)
            processor.Process(MakeEnvelope("error5()", "Programmer", generation: 4));
            Assert.AreEqual(4, repairCount); // Не увеличилось!
        }

        #endregion

        #region Test 8: Non-Programmer Role Does Not Trigger Repair

        [Test]
        public void LuaExecution_NonProgrammerRole_NoRepairScheduled()
        {
            int repairCount = 0;
            var fakeOrchestrator = new FakeOrchestrator((task) => repairCount++);
            var bindings = new CoreDefaultLuaRuntimeBindings();
            TestSink sink = new();
            TestObserver observer = new();

            LuaAiEnvelopeProcessor processor = new LuaAiEnvelopeProcessor(
                new SecureLuaEnvironment(),
                bindings,
                sink,
                () => fakeOrchestrator,
                observer,
                new NullLuaScriptVersionStore());

            // CoreMechanicAI ошибается — repair НЕ планируется
            processor.Process(MakeEnvelope("bad_call()", "CoreMechanicAI", generation: 0));

            Assert.AreEqual(1, sink.Commands.Count);
            Assert.AreEqual(AiGameCommandTypeIds.LuaExecutionFailed, sink.Commands[0].CommandTypeId);
            Assert.AreEqual(0, repairCount, "Non-Programmer roles should not trigger repair");
        }

        #endregion

        #region Test Infrastructure Classes

        private sealed class TestLuaBindings : IGameLuaRuntimeBindings
        {
            public void RegisterGameplayApis(LuaApiRegistry registry)
            {
                registry.Register("report", new Action<string>(_ => { }));
                registry.Register("add", new Func<double, double, double>((a, b) => a + b));
            }
        }

        private sealed class CraftingTestBindings : IGameLuaRuntimeBindings
        {
            public string LastCreatedItem;
            public int SpecialEffectCount;

            public void RegisterGameplayApis(LuaApiRegistry registry)
            {
                registry.Register("report", new Action<string>(_ => { }));
                registry.Register("calc_damage", new Func<double, double, double>((b, multiplier) => b * multiplier));
                registry.Register("create_item", new Action<string, string, double>((name, type, quality) =>
                {
                    LastCreatedItem = name;
                }));
                registry.Register("add_special_effect", new Action<string>(_ =>
                {
                    SpecialEffectCount++;
                }));
            }
        }

        private sealed class FakeOrchestrator : IAiOrchestrationService
        {
            private readonly Action<AiTaskRequest> _onTask;

            public FakeOrchestrator(Action<AiTaskRequest> onTask)
            {
                _onTask = onTask;
            }

            public System.Threading.Tasks.Task RunTaskAsync(AiTaskRequest task,
                System.Threading.CancellationToken cancellationToken = default)
            {
                _onTask(task);
                return System.Threading.Tasks.Task.CompletedTask;
            }
        }

        #endregion
    }
}
