using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using CoreAI.ChatOverlay;
using CoreAI.Mods.Rbx.Instances;
using CoreAI.Mods.Rbx.Instances.Scheduling;
using Lua;
using Lua.Runtime;
using static CoreAI.Ai.LuaCs.LuaCsRbxLua;

namespace CoreAI.Ai.LuaCs
{
    /// <summary>
    /// <c>AIService:GetAssistant()</c> (CoreAI's own API, DEV-140): the player's own agent, the one the in-world chat
    /// talks to, for a chat made in Lua. <c>assistant:Send(text, {Role = ...})</c> starts a turn through
    /// <see cref="ChatAgentTurns"/> and returns a turn whose <c>TextAdded(delta, startsNewMessage)</c>,
    /// <c>StatusChanged(status)</c> and <c>Ended(result)</c> fire on the scheduler, and whose <c>Cancel()</c> stops it.
    /// Read-only members: <c>IsAvailable</c>, <c>IsBusy</c>, <c>Role</c>, <c>CanSwitchModels</c>, <c>Status</c>; the
    /// signal <c>StatusChanged(status)</c> reports every status change, also of host turns (a mod's self-repair, an
    /// orchestrator wake) and of background work; methods: <c>GetRoles</c>, <c>SetRole</c>, <c>GetModels</c>,
    /// <c>GetModel</c>, <c>SetModel</c>, <c>Stop</c>. Refused unless
    /// the host turned on <see cref="CoreAiLuaAiOptions.AllowPlayerAssistantFromLua"/>.
    /// </summary>
    internal sealed class LuaCsRbxAssistantSurface : IDisposable
    {
        private readonly LuaCsRbxApiBindings _bindings;
        private readonly ChatAgentTurns _turns;
        private readonly CoreAiLuaAiOptions _options;
        private readonly Dictionary<LuaCsRbxModContext, ContextRecord> _records = new();
        private readonly List<LuaCsRbxModContext> _dead = new();
        private readonly Action<SchedulerPhase, double> _onPhase;
        private bool _disposed;

        public LuaCsRbxAssistantSurface(LuaCsRbxApiBindings bindings, ChatAgentTurns turns, CoreAiLuaAiOptions options)
        {
            _bindings = bindings ?? throw new ArgumentNullException(nameof(bindings));
            _turns = turns;
            _options = options ?? new CoreAiLuaAiOptions();
            _onPhase = OnPhase;
            _bindings.Scheduler.PhaseReached += _onPhase;
        }

        /// <summary>The <c>GetAssistant</c> method of <c>AIService</c> for one mod context.</summary>
        public LuaFunction BuildGetAssistant(LuaCsRbxModContext context, LuaCapabilities capabilities,
            Action<LuaFunctionExecutionContext> requireService)
        {
            return Fn("AIService.GetAssistant", ctx =>
            {
                requireService(ctx);
                RequireAllowed(context, capabilities, "AIService:GetAssistant");
                if (_turns == null)
                {
                    throw new RbxError(RbxErrorCode.NotImplemented,
                        "AIService:GetAssistant has no player's agent in this world: the host composed no chat",
                        "the host registers the in-world chat services (RegisterCoreAiChatOverlayServices)",
                        context.OwnerModId);
                }

                if (!_records.TryGetValue(context, out ContextRecord record))
                {
                    RbxScriptSignal statusChanged = new("Assistant.StatusChanged");
                    statusChanged.BindScheduler(_bindings.Scheduler);
                    record = new ContextRecord(BuildAssistant(context, capabilities, statusChanged), Lifetime(context),
                        status => statusChanged.Fire(new LuaValue(StatusTable(status))));
                    _turns.StatusChanged += record.OnStatus;
                    _records[context] = record;
                }

                return new LuaValue(record.Handle);
            }, context);
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _bindings.Scheduler.PhaseReached -= _onPhase;
            foreach (ContextRecord record in _records.Values)
            {
                if (_turns != null)
                {
                    _turns.StatusChanged -= record.OnStatus;
                }
            }

            _records.Clear();
        }

        private void OnPhase(SchedulerPhase phase, double deltaSeconds)
        {
            if (phase != SchedulerPhase.Heartbeat || _disposed)
            {
                return;
            }

            _turns?.Tick();
            if (_records.Count == 0)
            {
                return;
            }

            foreach (KeyValuePair<LuaCsRbxModContext, ContextRecord> pair in _records)
            {
                if (!pair.Value.Lifetime.Connected)
                {
                    _dead.Add(pair.Key);
                }
            }

            for (int i = 0; i < _dead.Count; i++)
            {
                // WHY a turn of an unloaded mod is stopped: nothing shows it any more, and a reloaded chat must be able
                // to send at once instead of waiting for an answer nobody will see.
                ContextRecord gone = _records[_dead[i]];
                if (gone.LastTurn != null && gone.LastTurn.IsActive)
                {
                    gone.LastTurn.Cancel();
                }

                _turns.StatusChanged -= gone.OnStatus;
                _records.Remove(_dead[i]);
            }

            _dead.Clear();
        }

        private RbxScriptConnection Lifetime(LuaCsRbxModContext context)
        {
            RbxScriptSignal lifetime = new("AIService.AssistantLifetime");
            lifetime.BindScheduler(_bindings.Scheduler);
            RbxScriptConnection connection = lifetime.Connect((Action<object[]>)(_ => { }));
            context.TrackConnection(connection);
            return connection;
        }

        private void RequireAllowed(LuaCsRbxModContext context, LuaCapabilities capabilities, string member)
        {
            if (!_options.AllowPlayerAssistantFromLua)
            {
                throw new RbxError(RbxErrorCode.NotAuthority,
                    member + " is turned off in this game: the host's framework setting "
                           + CoreAiLuaAiOptions.AssistantSettingName + " is false",
                    "the game turns on " + CoreAiLuaAiOptions.AssistantSettingName
                                         + " together with a chat made in Lua; a world or a mod cannot",
                    context.OwnerModId);
            }

            if (_options.RequireAiCapability && !LuaAiCapability.IsGranted(capabilities))
            {
                throw new RbxError(RbxErrorCode.NotAuthority,
                    member + " requires the Ai capability, which was not granted to this mod",
                    "grant the mod the Ai capability (LuaCapabilities bit 1 << 6) or leave AI out of it",
                    context.OwnerModId);
            }
        }

        private LuaTable BuildAssistant(LuaCsRbxModContext context, LuaCapabilities capabilities,
            RbxScriptSignal statusChanged)
        {
            LuaValue statusSignal = LuaCsRbxDatatypeBindings.Wrap(statusChanged, context);
            LuaTable methods = new();
            methods["Send"] = Fn("Assistant.Send", ctx =>
            {
                RequireSelf(ctx, context, "Send");
                RequireAllowed(context, capabilities, "assistant:Send");
                string text = ReadString(ctx, 1, "assistant:Send", 1);
                string role = ReadRoleOption(Arg(ctx, 2));
                ChatAgentTurn turn;
                try
                {
                    turn = _turns.Start(text, role);
                }
                catch (InvalidOperationException ex)
                {
                    bool busy = _turns.IsBusy;
                    throw new RbxError(busy ? RbxErrorCode.BudgetExceeded : RbxErrorCode.NotImplemented,
                        "assistant:Send: " + ex.Message,
                        busy ? "wait for the running turn's Ended, or stop it with turn:Cancel()"
                            : "check assistant.IsAvailable before sending",
                        context.OwnerModId);
                }
                catch (ArgumentException)
                {
                    throw RbxError.BadArgument("assistant:Send needs a non-empty line", "pass what the player said");
                }

                if (_records.TryGetValue(context, out ContextRecord owner))
                {
                    owner.LastTurn = turn;
                }

                return new LuaValue(BuildTurn(turn, context));
            }, context);
            methods["Stop"] = Fn("Assistant.Stop", ctx =>
            {
                RequireSelf(ctx, context, "Stop");
                return _turns.StopCurrent();
            }, context);
            methods["GetRoles"] = Fn("Assistant.GetRoles", ctx =>
            {
                RequireSelf(ctx, context, "GetRoles");
                return new LuaValue(StringList(_turns.AvailableRoles));
            }, context);
            methods["SetRole"] = Pair("Assistant.SetRole", ctx =>
            {
                RequireSelf(ctx, context, "SetRole");
                string wanted = ReadString(ctx, 1, "assistant:SetRole", 1);
                bool ok = _turns.TrySelectRole(wanted, out string selected);
                return (ok, ok ? new LuaValue(selected ?? "") : new LuaValue(_turns.ActiveRoleId));
            });
            methods["GetModels"] = Fn("Assistant.GetModels", ctx =>
            {
                RequireSelf(ctx, context, "GetModels");
                LuaTable list = new();
                IReadOnlyList<ChatModelProfile> profiles = _turns.Models != null && _turns.Models.IsAvailable
                    ? _turns.Models.GetProfiles() ?? Array.Empty<ChatModelProfile>()
                    : Array.Empty<ChatModelProfile>();
                for (int i = 0; i < profiles.Count; i++)
                {
                    LuaTable entry = new();
                    entry["Id"] = profiles[i].Id;
                    entry["Name"] = profiles[i].DisplayName;
                    entry["Status"] = profiles[i].Status;
                    list[i + 1] = entry;
                }

                return new LuaValue(list);
            }, context);
            methods["GetModel"] = Fn("Assistant.GetModel", ctx =>
            {
                RequireSelf(ctx, context, "GetModel");
                return _turns.Models != null && _turns.Models.IsAvailable
                    ? _turns.Models.GetProfileForRole(_turns.ActiveRoleId) ?? ""
                    : "";
            }, context);
            methods["SetModel"] = Pair("Assistant.SetModel", ctx =>
            {
                RequireSelf(ctx, context, "SetModel");
                string profile = Arg(ctx, 1).Type == LuaValueType.Nil ? "" : ReadString(ctx, 1, "assistant:SetModel", 1);
                if (_turns.Models == null || !_turns.Models.IsAvailable)
                {
                    return (false, new LuaValue("no model routing is configured"));
                }

                bool ok = _turns.Models.TryAssign(_turns.ActiveRoleId, profile.Trim(), out string message);
                return (ok, new LuaValue(message ?? ""));
            });

            LuaTable handle = new();
            LuaTable meta = new();
            meta[Metamethods.Index] = Fn("Assistant.__index", ctx =>
            {
                LuaValue key = Arg(ctx, 1);
                if (key.Type != LuaValueType.String)
                {
                    return LuaValue.Nil;
                }

                switch (key.Read<string>())
                {
                    case "IsAvailable":
                        return _turns.IsAvailable;
                    case "IsBusy":
                        return _turns.IsBusy;
                    case "Role":
                        return _turns.ActiveRoleId;
                    case "CanSwitchModels":
                        return _turns.Models != null && _turns.Models.IsAvailable;
                    case "StatusChanged":
                        return statusSignal;
                    case "Status":
                        return new LuaValue(StatusTable(_turns.Status));
                    default:
                        return methods.TryGetValue(key, out LuaValue method) ? method : LuaValue.Nil;
                }
            }, context);
            meta[Metamethods.NewIndex] = Fn("Assistant.__newindex", _ => throw RbxError.BadArgument(
                "the assistant's members are read-only",
                "switch the role with assistant:SetRole(role) and the model with assistant:SetModel(profile)"), context);
            handle.Metatable = meta;
            return handle;
        }

        private LuaTable BuildTurn(ChatAgentTurn turn, LuaCsRbxModContext context)
        {
            ModScheduler scheduler = _bindings.Scheduler;
            RbxScriptSignal textAdded = new("AssistantTurn.TextAdded");
            RbxScriptSignal statusChanged = new("AssistantTurn.StatusChanged");
            RbxScriptSignal ended = new("AssistantTurn.Ended");
            textAdded.BindScheduler(scheduler);
            statusChanged.BindScheduler(scheduler);
            ended.BindScheduler(scheduler);
            turn.TextAdded += (delta, startsNew) => textAdded.Fire(delta, startsNew);
            turn.StatusChanged += status => statusChanged.Fire(new LuaValue(StatusTable(status)));
            turn.Ended += result => ended.Fire(new LuaValue(ResultTable(result)));

            LuaTable handle = new();
            LuaTable fields = new();
            fields["TextAdded"] = LuaCsRbxDatatypeBindings.Wrap(textAdded, context);
            fields["StatusChanged"] = LuaCsRbxDatatypeBindings.Wrap(statusChanged, context);
            fields["Ended"] = LuaCsRbxDatatypeBindings.Wrap(ended, context);
            fields["Role"] = turn.RoleId;
            fields["Cancel"] = Fn("AssistantTurn.Cancel", _ =>
            {
                turn.Cancel();
                return LuaValue.Nil;
            }, context);
            LuaTable meta = new();
            meta[Metamethods.Index] = Fn("AssistantTurn.__index", ctx =>
            {
                LuaValue key = Arg(ctx, 1);
                if (key.Type != LuaValueType.String)
                {
                    return LuaValue.Nil;
                }

                switch (key.Read<string>())
                {
                    case "IsActive":
                        return turn.IsActive;
                    case "Status":
                        return new LuaValue(StatusTable(turn.Status));
                    default:
                        return fields.TryGetValue(key, out LuaValue value) ? value : LuaValue.Nil;
                }
            }, context);
            meta[Metamethods.NewIndex] = Fn("AssistantTurn.__newindex", _ => throw RbxError.BadArgument(
                "a turn's members are read-only", "stop it with turn:Cancel()"), context);
            handle.Metatable = meta;
            return handle;
        }

        /// <summary>The status table a script reads: the names of <see cref="AgentStatusKind"/> as <c>State</c>.</summary>
        internal static LuaTable StatusTable(ChatAgentStatus status)
        {
            LuaTable table = new();
            table["State"] = status.State.ToString();
            table["Tool"] = status.Tool;
            table["Characters"] = status.Characters;
            table["Tokens"] = status.Tokens;
            table["Attempt"] = status.Attempt;
            table["MaxAttempts"] = status.MaxAttempts;
            table["Step"] = status.Step;
            table["Detail"] = status.Detail;
            table["Host"] = status.Host;
            table["Background"] = status.Background;
            return table;
        }

        internal static LuaTable ResultTable(ChatAgentTurnResult result)
        {
            LuaTable table = new();
            table["Outcome"] = result.Outcome.ToString();
            table["Message"] = result.Message;
            table["ErrorCode"] = result.ErrorCode.ToString();
            table["ToolOnly"] = result.ToolOnly == ChatAgentToolOnly.None ? "" : result.ToolOnly.ToString();
            table["ToolSummary"] = result.ToolSummary;
            table["AnyText"] = result.AnyText;
            return table;
        }

        private static string ReadRoleOption(LuaValue options)
        {
            if (options.Type == LuaValueType.Nil)
            {
                return "";
            }

            if (!options.TryRead(out LuaTable table))
            {
                throw ExpectedArgument("assistant:Send options", "a table", options, 2);
            }

            foreach (KeyValuePair<LuaValue, LuaValue> pair in table)
            {
                string key = pair.Key.Type == LuaValueType.String ? pair.Key.Read<string>() : "";
                if (key != "Role")
                {
                    throw RbxError.BadArgument("assistant:Send does not know the option " + pair.Key,
                        "pass only { Role = \"Programmer\" }");
                }
            }

            LuaValue role = table["Role"];
            if (role.Type == LuaValueType.Nil)
            {
                return "";
            }

            if (role.Type != LuaValueType.String)
            {
                throw ExpectedArgument("assistant:Send Role", "a string", role, 2);
            }

            return role.Read<string>();
        }

        private void RequireSelf(LuaFunctionExecutionContext ctx, LuaCsRbxModContext context, string method)
        {
            if (!Arg(ctx, 0).TryRead(out LuaTable self) || !_records.TryGetValue(context, out ContextRecord record)
                                                    || !ReferenceEquals(self, record.Handle))
            {
                throw RbxError.BadArgument("assistant:" + method + " must be called with a colon on the assistant",
                    "write assistant:" + method + "(...), with assistant = AIService:GetAssistant()");
            }
        }

        private static LuaTable StringList(IReadOnlyList<string> values)
        {
            LuaTable list = new();
            for (int i = 0; i < values.Count; i++)
            {
                list[i + 1] = values[i] ?? "";
            }

            return list;
        }

        private sealed class ContextRecord
        {
            public ContextRecord(LuaTable handle, RbxScriptConnection lifetime, Action<ChatAgentStatus> onStatus)
            {
                Handle = handle;
                Lifetime = lifetime;
                OnStatus = onStatus;
            }

            public LuaTable Handle { get; }

            public Action<ChatAgentStatus> OnStatus { get; }

            public RbxScriptConnection Lifetime { get; }

            public ChatAgentTurn LastTurn { get; set; }
        }

        private static LuaFunction Pair(string name, Func<LuaFunctionExecutionContext, (bool, LuaValue)> body)
        {
            return new LuaFunction(name, (ctx, _) =>
            {
                try
                {
                    (bool ok, LuaValue second) = body(ctx);
                    return new ValueTask<int>(ctx.Return(ok, second));
                }
                catch (Exception ex)
                {
                    throw ToLuaError(ctx.State, ex);
                }
            });
        }
    }
}
