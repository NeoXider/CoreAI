using System;
using CoreAI.Messaging;
using CoreAI.Sandbox;
using MoonSharp.Interpreter;
using static CoreAI.Messaging.AiGameCommandTypeIds;

namespace CoreAI.Ai
{
    /// <summary>
    /// Разбор <see cref="AiGameCommandTypeIds.Envelope"/>, исполнение Lua в песочнице, при ошибке — повторный вызов Programmer.
    /// </summary>
    public sealed class LuaAiEnvelopeProcessor
    {
        /// <summary>Максимум автоматических повторов Programmer при ошибке Lua в одном конверте.</summary>
        public const int DefaultMaxLuaRepairGenerations = 4;

        private readonly SecureLuaEnvironment _sandbox;
        private readonly IGameLuaRuntimeBindings _bindings;
        private readonly IAiGameCommandSink _sink;
        private readonly Func<IAiOrchestrationService> _resolveOrchestrator;
        private readonly ILuaExecutionObserver _observer;
        private readonly int _maxLuaRepairGenerationOnEnvelope;

        public LuaAiEnvelopeProcessor(
            SecureLuaEnvironment sandbox,
            IGameLuaRuntimeBindings bindings,
            IAiGameCommandSink sink,
            Func<IAiOrchestrationService> resolveOrchestrator,
            ILuaExecutionObserver observer)
        {
            _sandbox = sandbox ?? throw new ArgumentNullException(nameof(sandbox));
            _bindings = bindings ?? throw new ArgumentNullException(nameof(bindings));
            _sink = sink ?? throw new ArgumentNullException(nameof(sink));
            _resolveOrchestrator = resolveOrchestrator ?? throw new ArgumentNullException(nameof(resolveOrchestrator));
            _observer = observer ?? throw new ArgumentNullException(nameof(observer));
            // Важно: VContainer плохо резолвит optional-примитивы. Поэтому лимит не передаём через DI.
            _maxLuaRepairGenerationOnEnvelope = DefaultMaxLuaRepairGenerations;
        }

        /// <summary>Обработать команду-конверт: извлечь Lua, выполнить, опубликовать результат или запланировать ремонт.</summary>
        public void Process(ApplyAiGameCommand cmd)
        {
            if (cmd == null || cmd.CommandTypeId != Envelope)
                return;
            if (!AiLuaPayloadParser.TryGetExecutableLua(cmd.JsonPayload ?? "", out var lua))
                return;

            try
            {
                var registry = new LuaApiRegistry();
                _bindings.RegisterGameplayApis(registry);
                var script = _sandbox.CreateScript(registry);
                var result = _sandbox.RunChunk(script, lua);
                var summary = result.ToPrintString();
                _sink.Publish(new ApplyAiGameCommand
                {
                    CommandTypeId = LuaExecutionSucceeded,
                    JsonPayload = summary,
                    SourceRoleId = cmd.SourceRoleId,
                    SourceTaskHint = cmd.SourceTaskHint,
                    LuaRepairGeneration = cmd.LuaRepairGeneration,
                    TraceId = cmd.TraceId ?? ""
                });
                _observer.OnLuaSuccess(summary);
            }
            catch (Exception ex)
            {
                var msg = ex is InterpreterException ie ? ie.Message : ex.Message;
                _sink.Publish(new ApplyAiGameCommand
                {
                    CommandTypeId = LuaExecutionFailed,
                    JsonPayload = msg,
                    SourceRoleId = cmd.SourceRoleId,
                    SourceTaskHint = cmd.SourceTaskHint,
                    LuaRepairGeneration = cmd.LuaRepairGeneration,
                    TraceId = cmd.TraceId ?? ""
                });
                _observer.OnLuaFailure(msg);

                if (string.Equals(cmd.SourceRoleId, BuiltInAgentRoleIds.Programmer, StringComparison.Ordinal) &&
                    cmd.LuaRepairGeneration < _maxLuaRepairGenerationOnEnvelope)
                {
                    var next = cmd.LuaRepairGeneration + 1;
                    _observer.OnLuaRepairScheduled(next, msg);
                    ScheduleProgrammerRepair(cmd, lua, msg, next);
                }
            }
        }

        private void ScheduleProgrammerRepair(ApplyAiGameCommand cmd, string failedLua, string error, int nextGeneration)
        {
            try
            {
                _ = _resolveOrchestrator().RunTaskAsync(new AiTaskRequest
                {
                    RoleId = BuiltInAgentRoleIds.Programmer,
                    Hint = string.IsNullOrEmpty(cmd.SourceTaskHint) ? "fix_lua" : cmd.SourceTaskHint,
                    LuaRepairGeneration = nextGeneration,
                    LuaRepairPreviousCode = failedLua,
                    LuaRepairErrorMessage = error,
                    TraceId = cmd.TraceId ?? ""
                });
            }
            catch (Exception ex)
            {
                _observer.OnLuaFailure("repair schedule: " + ex.Message);
            }
        }
    }
}
