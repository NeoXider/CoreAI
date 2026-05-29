using System;
using CoreAI.Messaging;
using CoreAI.Sandbox;
using MoonSharp.Interpreter;
using static CoreAI.Messaging.AiGameCommandTypeIds;

namespace CoreAI.Ai
{
    /// <summary>
    /// Processes AI commands that contain Lua execution envelopes.
    /// </summary>
    public sealed class LuaAiEnvelopeProcessor
    {
        /// <summary>Default max lua repair retries.</summary>
        public const int DefaultMaxLuaRepairRetries = 3; // Matches CoreAISettings.MaxLuaRepairRetries compatibility.

        private readonly SecureLuaEnvironment _sandbox;
        private readonly IGameLuaRuntimeBindings _bindings;
        private readonly IAiGameCommandSink _sink;
        private readonly Func<IAiOrchestrationService> _resolveOrchestrator;
        private readonly ILuaExecutionObserver _observer;
        private readonly ILuaScriptVersionStore _luaScriptVersions;
        private readonly ICoreAISettings _settings;

        public LuaAiEnvelopeProcessor(
            SecureLuaEnvironment sandbox,
            IGameLuaRuntimeBindings bindings,
            IAiGameCommandSink sink,
            Func<IAiOrchestrationService> resolveOrchestrator,
            ILuaExecutionObserver observer,
            ILuaScriptVersionStore luaScriptVersions,
            ICoreAISettings settings = null)
        {
            _sandbox = sandbox ?? throw new ArgumentNullException(nameof(sandbox));
            _bindings = bindings ?? throw new ArgumentNullException(nameof(bindings));
            _sink = sink ?? throw new ArgumentNullException(nameof(sink));
            _resolveOrchestrator = resolveOrchestrator ?? throw new ArgumentNullException(nameof(resolveOrchestrator));
            _observer = observer ?? throw new ArgumentNullException(nameof(observer));
            _luaScriptVersions = luaScriptVersions ?? new NullLuaScriptVersionStore();
            _settings = settings;
        }

        /// <summary>Processes an AI game command and dispatches any embedded Lua work.</summary>
        public void Process(ApplyAiGameCommand cmd)
        {
            if (cmd == null || cmd.CommandTypeId != Envelope)
            {
                return;
            }

            if (!AiLuaPayloadParser.TryGetExecutableLua(cmd.JsonPayload ?? "", out string lua))
            {
                return;
            }

            if (!SecureLuaEnvironment.IsSupported)
            {
                string msg = "CoreAI Lua execution is disabled on this platform.";
                PublishLuaFailure(cmd, msg);
                _observer.OnLuaFailure(msg);
                return;
            }

            try
            {
                LuaApiRegistry registry = new();
                _bindings.RegisterGameplayApis(registry);
                Script script = _sandbox.CreateScript(registry);
                DynValue result = _sandbox.RunChunk(script, lua);
                string summary = result.ToPrintString();
                if (!string.IsNullOrWhiteSpace(cmd.LuaScriptVersionKey))
                {
                    _luaScriptVersions.RecordSuccessfulExecution(cmd.LuaScriptVersionKey.Trim(), lua);
                }

                _sink.Publish(new ApplyAiGameCommand
                {
                    CommandTypeId = LuaExecutionSucceeded,
                    JsonPayload = summary,
                    SourceRoleId = cmd.SourceRoleId,
                    SourceTaskHint = cmd.SourceTaskHint,
                    SourceTag = cmd.SourceTag ?? "",
                    LuaRepairGeneration = cmd.LuaRepairGeneration,
                    TraceId = cmd.TraceId ?? "",
                    LuaScriptVersionKey = cmd.LuaScriptVersionKey ?? "",
                    DataOverlayVersionKeysCsv = cmd.DataOverlayVersionKeysCsv ?? ""
                });
                _observer.OnLuaSuccess(summary);
            }
            catch (Exception ex)
            {
                string msg = ex is InterpreterException ie ? ie.Message : ex.Message;
                PublishLuaFailure(cmd, msg);
                _observer.OnLuaFailure(msg);

                if (string.Equals(cmd.SourceRoleId, BuiltInAgentRoleIds.Programmer, StringComparison.Ordinal) &&
                    cmd.LuaRepairGeneration < (_settings?.MaxLuaRepairRetries ?? CoreAISettings.MaxLuaRepairRetries))
                {
                    int next = cmd.LuaRepairGeneration + 1;
                    _observer.OnLuaRepairScheduled(next, msg);
                    ScheduleProgrammerRepair(cmd, lua, msg, next);
                }
            }
        }

        private void PublishLuaFailure(ApplyAiGameCommand cmd, string message)
        {
            _sink.Publish(new ApplyAiGameCommand
            {
                CommandTypeId = LuaExecutionFailed,
                JsonPayload = message,
                SourceRoleId = cmd.SourceRoleId,
                SourceTaskHint = cmd.SourceTaskHint,
                SourceTag = cmd.SourceTag ?? "",
                LuaRepairGeneration = cmd.LuaRepairGeneration,
                TraceId = cmd.TraceId ?? "",
                LuaScriptVersionKey = cmd.LuaScriptVersionKey ?? ""
            });
        }

        private void ScheduleProgrammerRepair(ApplyAiGameCommand cmd, string failedLua, string error,
            int nextGeneration)
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
                    TraceId = cmd.TraceId ?? "",
                    SourceTag = string.IsNullOrEmpty(cmd.SourceTag) ? "lua_repair" : cmd.SourceTag + ":lua_repair",
                    LuaScriptVersionKey = cmd.LuaScriptVersionKey ?? "",
                    DataOverlayVersionKeysCsv = cmd.DataOverlayVersionKeysCsv ?? ""
                });
            }
            catch (Exception ex)
            {
                _observer.OnLuaFailure("repair schedule: " + ex.Message);
            }
        }
    }
}
