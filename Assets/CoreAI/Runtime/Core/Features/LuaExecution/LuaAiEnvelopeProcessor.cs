#if COREAI_HAS_MOONSHARP && !COREAI_NO_LUA
﻿using System;
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

        /// <summary>Cap on the success payload built from the script result (`ToPrintString`).</summary>
        public const int MaxResultSummaryLength = 4_000;

        /// <summary>Cap on error text published in failure payloads and repair prompts.</summary>
        public const int MaxErrorMessageLength = 500;

        private static readonly System.Diagnostics.Stopwatch Clock = System.Diagnostics.Stopwatch.StartNew();

        private readonly SecureLuaEnvironment _sandbox;
        private readonly IGameLuaRuntimeBindings _bindings;
        private readonly IAiGameCommandSink _sink;
        private readonly Func<IAiOrchestrationService> _resolveOrchestrator;
        private readonly ILuaExecutionObserver _observer;
        private readonly ILuaScriptVersionStore _luaScriptVersions;
        private readonly ICoreAISettings _settings;
        private readonly LuaGenerationRateLimiter _rateLimiter;

        public LuaAiEnvelopeProcessor(
            SecureLuaEnvironment sandbox,
            IGameLuaRuntimeBindings bindings,
            IAiGameCommandSink sink,
            Func<IAiOrchestrationService> resolveOrchestrator,
            ILuaExecutionObserver observer,
            ILuaScriptVersionStore luaScriptVersions,
            ICoreAISettings settings = null,
            LuaGenerationRateLimiter rateLimiter = null)
        {
            _sandbox = sandbox ?? throw new ArgumentNullException(nameof(sandbox));
            _bindings = bindings ?? throw new ArgumentNullException(nameof(bindings));
            _sink = sink ?? throw new ArgumentNullException(nameof(sink));
            _resolveOrchestrator = resolveOrchestrator ?? throw new ArgumentNullException(nameof(resolveOrchestrator));
            _observer = observer ?? throw new ArgumentNullException(nameof(observer));
            _luaScriptVersions = luaScriptVersions ?? new NullLuaScriptVersionStore();
            _settings = settings;
            _rateLimiter = rateLimiter ?? new LuaGenerationRateLimiter();
        }

        /// <summary>Rate limiter guarding envelope executions and repair generations.</summary>
        public LuaGenerationRateLimiter RateLimiter => _rateLimiter;

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

            if (!_rateLimiter.TryAcquire(Clock.Elapsed.TotalSeconds))
            {
                // Runaway-loop guard: a failing script (or spamming agent) cannot saturate the
                // sandbox/LLM with executions; the failure is reported without scheduling a repair.
                string limitMsg =
                    $"CoreAI Lua rate limit exceeded ({_rateLimiter.MaxPerWindow} per {_rateLimiter.WindowSeconds:0}s); envelope dropped.";
                PublishLuaFailure(cmd, limitMsg);
                _observer.OnLuaFailure(limitMsg);
                return;
            }

            // The world bindings are a shared singleton: a prior envelope that died between
            // coreai_world_begin() and commit/rollback leaves its transaction open, which would
            // silently buffer this envelope's world commands. Reset before running and abort in the
            // finally so a leaked transaction can never bleed across envelopes in either direction.
            (_bindings as ILuaTransactionScope)?.ResetTransactions();
            try
            {
                LuaApiRegistry registry = new();
                _bindings.RegisterGameplayApis(registry);
                Script script = _sandbox.CreateScript(registry);
                DynValue result = _sandbox.RunChunk(script, lua);
                string summary = Truncate(result.ToPrintString(), MaxResultSummaryLength);
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
                string msg = NormalizeError(ex is InterpreterException ie ? ie.Message : ex.Message);
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
            finally
            {
                (_bindings as ILuaTransactionScope)?.ResetTransactions();
            }
        }

        private static string Truncate(string value, int maxLength)
        {
            if (string.IsNullOrEmpty(value) || value.Length <= maxLength)
            {
                return value ?? "";
            }

            return value.Substring(0, maxLength) + " ...(truncated)";
        }

        // Error text travels into payloads and repair prompts; collapse newlines so host exception
        // messages (stack fragments, file paths) cannot inject multi-line content, and cap length.
        private static string NormalizeError(string message)
        {
            string flat = (message ?? "").Replace("\r", " ").Replace("\n", " ").Trim();
            return Truncate(flat, MaxErrorMessageLength);
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
            if (!_rateLimiter.TryAcquire(Clock.Elapsed.TotalSeconds))
            {
                _observer.OnLuaFailure(
                    $"repair schedule skipped: Lua generation rate limit exceeded ({_rateLimiter.MaxPerWindow} per {_rateLimiter.WindowSeconds:0}s).");
                return;
            }

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
#endif