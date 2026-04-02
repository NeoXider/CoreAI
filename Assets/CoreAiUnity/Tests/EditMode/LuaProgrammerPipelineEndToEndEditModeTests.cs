using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Ai;
using CoreAI.Authority;
using CoreAI.Messaging;
using CoreAI.Sandbox;
using CoreAI.Session;
using NUnit.Framework;
using static CoreAI.Messaging.AiGameCommandTypeIds;

namespace CoreAI.Tests.EditMode
{
    /// <summary>
    /// Пайплайн: оркестратор → конверт → <see cref="LuaAiEnvelopeProcessor"/> → при ошибке Programmer
    /// планируется повторный <see cref="IAiOrchestrationService.RunTaskAsync"/> с полями ремонта; второй ответ LLM — валидный Lua.
    /// </summary>
    public sealed class LuaProgrammerPipelineEndToEndEditModeTests
    {
        private sealed class QueueLlmClient : ILlmClient
        {
            private readonly Queue<string> _responses = new();

            public QueueLlmClient(params string[] responses)
            {
                foreach (var r in responses)
                    _responses.Enqueue(r);
            }

            public Task<LlmCompletionResult> CompleteAsync(LlmCompletionRequest request, CancellationToken cancellationToken = default)
            {
                var text = _responses.Count > 0 ? _responses.Dequeue() : "";
                return Task.FromResult(new LlmCompletionResult { Ok = true, Content = text });
            }
        }

        private sealed class ListCommandSink : IAiGameCommandSink
        {
            public readonly List<ApplyAiGameCommand> Items = new();

            public void Publish(ApplyAiGameCommand command) => Items.Add(command);
        }

        private sealed class EnvelopeDispatchSink : IAiGameCommandSink
        {
            private readonly LuaAiEnvelopeProcessor _processor;
            private readonly IAiGameCommandSink _nonEnvelope;

            public EnvelopeDispatchSink(LuaAiEnvelopeProcessor processor, IAiGameCommandSink nonEnvelope)
            {
                _processor = processor;
                _nonEnvelope = nonEnvelope;
            }

            public void Publish(ApplyAiGameCommand command)
            {
                if (command.CommandTypeId == Envelope)
                    _processor.Process(command);
                else
                    _nonEnvelope.Publish(command);
            }
        }

        private sealed class CapturingBindings : IGameLuaRuntimeBindings
        {
            public readonly List<string> Reports = new();

            public void RegisterGameplayApis(LuaApiRegistry registry)
            {
                registry.Register("report", new System.Action<string>(s => Reports.Add(s)));
                registry.Register("add", new System.Func<double, double, double>((a, b) => a + b));
            }
        }

        [Test]
        public async Task Orchestrator_Programmer_BadLua_ThenRepair_GoodLua_ReportsSuccess()
        {
            const string bad = "```lua\nnot_a_function()\n```";
            const string good = "```lua\nreport('repaired')\n```";

            var llm = new QueueLlmClient(bad, good);
            var events = new ListCommandSink();
            var bindings = new CapturingBindings();
            var telemetry = new SessionTelemetryCollector();
            var provider = new BuiltInDefaultAgentSystemPromptProvider();
            var composer = new AiPromptComposer(provider, new NoAgentUserPromptTemplateProvider());

            IAiOrchestrationService orchestrator = null;
            var proc = new LuaAiEnvelopeProcessor(
                new SecureLuaEnvironment(),
                bindings,
                events,
                () => orchestrator!,
                new NullLuaExecutionObserver());

            var sink = new EnvelopeDispatchSink(proc, events);
            orchestrator = new AiOrchestrator(new SoloAuthorityHost(), llm, sink, telemetry, composer, new NullAgentMemoryStore(), new AgentMemoryPolicy(),
                new NoOpRoleStructuredResponsePolicy(), new NullAiOrchestrationMetrics());

            await orchestrator.RunTaskAsync(new AiTaskRequest
            {
                RoleId = BuiltInAgentRoleIds.Programmer,
                Hint = "Write Lua using report()."
            }).ConfigureAwait(false);

            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < 5000)
            {
                if (events.Items.Any(c => c.CommandTypeId == LuaExecutionSucceeded))
                    break;
                await Task.Delay(25).ConfigureAwait(false);
            }

            Assert.IsTrue(events.Items.Any(c => c.CommandTypeId == LuaExecutionFailed),
                "Ожидался сбой MoonSharp на первом ответе");
            Assert.IsTrue(events.Items.Any(c => c.CommandTypeId == LuaExecutionSucceeded),
                "После ремонта ожидался успешный запуск Lua (второй ответ очереди LLM)");
            Assert.Contains("repaired", bindings.Reports);
        }

        [Test]
        public async Task Orchestrator_Programmer_ComplexWrongThenFixed_RepairCarriesErrorInUserPayload()
        {
            var bad = "```lua\n" +
                      "local function chaos() return missing_global + 1 end\n" +
                      "chaos()\n```";
            const string good = "```lua\nreport('ok_after_complex_fail')\n```";

            var llm = new QueueLlmClient(bad, good);
            var events = new ListCommandSink();
            var bindings = new CapturingBindings();
            var telemetry = new SessionTelemetryCollector();
            var provider = new BuiltInDefaultAgentSystemPromptProvider();
            var composer = new AiPromptComposer(provider, new NoAgentUserPromptTemplateProvider());

            var gotSecondPayload = false;
            var secondUserPayload = "";
            var capturingLlm = new PayloadCapturingLlm(llm, (_, user) =>
            {
                gotSecondPayload = true;
                secondUserPayload = user;
            });

            IAiOrchestrationService orchestrator = null;
            var proc = new LuaAiEnvelopeProcessor(
                new SecureLuaEnvironment(),
                bindings,
                events,
                () => orchestrator!,
                new NullLuaExecutionObserver());

            var sink = new EnvelopeDispatchSink(proc, events);
            orchestrator = new AiOrchestrator(new SoloAuthorityHost(), capturingLlm, sink, telemetry, composer, new NullAgentMemoryStore(), new AgentMemoryPolicy(),
                new NoOpRoleStructuredResponsePolicy(), new NullAiOrchestrationMetrics());

            await orchestrator.RunTaskAsync(new AiTaskRequest
            {
                RoleId = BuiltInAgentRoleIds.Programmer,
                Hint = "complex_task"
            }).ConfigureAwait(false);

            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < 5000)
            {
                if (events.Items.Any(c => c.CommandTypeId == LuaExecutionSucceeded))
                    break;
                await Task.Delay(25).ConfigureAwait(false);
            }

            Assert.IsTrue(events.Items.Any(c => c.CommandTypeId == LuaExecutionSucceeded));
            Assert.IsTrue(gotSecondPayload);
            StringAssert.Contains("lua_error=", secondUserPayload);
            StringAssert.Contains("fix_this_lua=", secondUserPayload);
            StringAssert.Contains("lua_repair_generation=1", secondUserPayload);
        }

        /// <summary>Прокси: первый вызов как есть; со второго вызова сохраняет user payload.</summary>
        private sealed class PayloadCapturingLlm : ILlmClient
        {
            private readonly ILlmClient _inner;
            private readonly System.Action<LlmCompletionRequest, string> _onSecond;
            private int _n;

            public PayloadCapturingLlm(ILlmClient inner, System.Action<LlmCompletionRequest, string> onSecond)
            {
                _inner = inner;
                _onSecond = onSecond;
            }

            public async Task<LlmCompletionResult> CompleteAsync(LlmCompletionRequest request, CancellationToken cancellationToken = default)
            {
                _n++;
                if (_n >= 2)
                    _onSecond(request, request.UserPayload);
                return await _inner.CompleteAsync(request, cancellationToken).ConfigureAwait(false);
            }
        }
    }
}
