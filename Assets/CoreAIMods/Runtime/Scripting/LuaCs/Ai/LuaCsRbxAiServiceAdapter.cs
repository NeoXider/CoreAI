using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Ai.Npc;
using CoreAI.Logging;
using CoreAI.Mods.Rbx.Datatypes;
using CoreAI.Mods.Rbx.Instances;
using CoreAI.Mods.Rbx.Instances.Networking;
using CoreAI.Mods.Rbx.Instances.Scheduling;
using CoreAI.Sandbox.LuaCs;
using CoreAI.Scripting;
using Lua;
using Lua.Runtime;
using Newtonsoft.Json.Linq;
using static CoreAI.Ai.LuaCs.LuaCsRbxLua;

namespace CoreAI.Ai.LuaCs
{
    /// <summary>
    /// The Lua surface of AI from mods: Roblox's <c>TextGenerator</c> (four properties and the yielding
    /// <c>GenerateTextAsync</c>) and CoreAI's <c>AIService</c> (<c>CreateAgent</c>, <c>GetAgent</c>, <c>GetAgents</c>,
    /// <c>Hear</c>) with the agent objects it returns (<c>Ask</c>, <c>Say</c>, <c>Destroy</c>, <c>IsSpeaking</c>,
    /// <c>Replied</c>, <c>Addressed</c>). Installed as an environment decorator next to HttpService: it patches the
    /// instance metatable for the two classes only and delegates every other member. Yielding calls use the same
    /// one-signal-per-request <c>signal:Wait</c> bridge as HttpService, and completions are applied on the
    /// scheduler's phase drain, so nothing runs on a thread of its own (WebGL-safe). Everything is refused with an
    /// error naming <see cref="CoreAiLuaAiOptions.SettingName"/> while the host's framework setting is off.
    /// </summary>
    internal sealed class LuaCsRbxAiServiceAdapter : IDisposable
    {
        private const string BridgeSource = @"
            local prepareGenerate = task._prepareTextGeneration
            local prepareAsk = task._prepareAgentAsk
            task._prepareTextGeneration = nil
            task._prepareAgentAsk = nil
            local function await(prepared)
                local ok, first, second = prepared:Wait()
                if not ok then
                    error(first, 0)
                end
                return first, second
            end
            return {
                GenerateTextAsync = function(generator, request)
                    local result = await(prepareGenerate(generator, request))
                    return result
                end,
                Ask = function(agent, text, speaker)
                    return await(prepareAsk(agent, text, speaker))
                end,
            }";

        private static readonly HashSet<string> AgentConfigKeys = new(StringComparer.Ordinal)
        {
            "Name", "Persona", "VoiceRules", "Aliases", "FallbackLines", "Memory", "Body", "HearingRadius",
            "InteractionRadius", "FollowUpSeconds", "AnswerUnaddressedWithin", "RequireLineOfSight",
            "CooldownSeconds", "MaxTokens", "RoutingProfile"
        };

        private static readonly HashSet<string> RequestKeys = new(StringComparer.Ordinal)
        {
            "UserPrompt", "ContextToken", "MaxTokens", "JsonSchema"
        };

        private const int MaxAgentNameLength = 64;
        private const double BubbleHeadOffsetStuds = 3d;

        private readonly LuaCsRbxApiBindings _bindings;
        private readonly CoreAiLuaAiOptions _options;
        private readonly IRbxTextGenerationBackend _text;
        private readonly INpcDirector _director;
        private readonly Func<RbxInstance, NpcPosition?> _bodyAnchor;
        private readonly NpcSpeechAnchorRegistry _anchors;
        private readonly ITextModerationFilter _moderation;
        private readonly Func<double> _clock;
        private readonly ILog _log;
        private readonly ILuaCsRuntimeLifetimeHost _lifetimeHost;
        private readonly RbxTextGenerationQuota _quota;
        private readonly RbxTextGenerationContexts _contexts;
        private readonly object _gate = new();
        private readonly Dictionary<long, PendingGeneration> _generations = new();
        private readonly Dictionary<long, PendingAsk> _asks = new();
        private readonly Dictionary<long, PendingAsk> _stashedEnded = new();
        private readonly Dictionary<string, AgentRecord> _agents = new(StringComparer.Ordinal);
        private readonly Dictionary<LuaTable, AgentRecord> _handles = new(ReferenceComparer.Instance);
        private readonly List<Action> _queuedSignals = new();
        private readonly List<Action> _firing = new();
        private readonly RbxInstance _service;
        private long _generationCounter;
        private long _agentSuffix;
        private bool _inAskCall;
        private bool _disposed;

        public LuaCsRbxAiServiceAdapter(
            LuaCsRbxApiBindings bindings,
            CoreAiLuaAiOptions options,
            IRbxTextGenerationBackend textBackend,
            INpcDirector director,
            Func<RbxInstance, NpcPosition?> bodyAnchor = null,
            NpcSpeechAnchorRegistry anchors = null,
            ITextModerationFilter moderation = null,
            Func<double> monotonicClock = null,
            ILog log = null)
        {
            _bindings = bindings ?? throw new ArgumentNullException(nameof(bindings));
            _options = options ?? new CoreAiLuaAiOptions();
            _text = textBackend;
            _director = director;
            _bodyAnchor = bodyAnchor;
            _anchors = anchors;
            _moderation = moderation ?? PassThroughTextModerationFilter.Instance;
            System.Diagnostics.Stopwatch watch = System.Diagnostics.Stopwatch.StartNew();
            _clock = monotonicClock ?? (() => watch.Elapsed.TotalSeconds);
            _log = log ?? NullLog.Instance;
            _quota = new RbxTextGenerationQuota(_options.TextGeneratorRequestsPerMinute);
            _contexts = new RbxTextGenerationContexts(_options.MaxContextTokens, _options.MaxContextTurns);
            _lifetimeHost = (object)bindings as ILuaCsRuntimeLifetimeHost;

            RbxAiClasses.EnsureRegistered(_bindings.Registry.Catalog);
            _service = AttachService();
            _bindings.Scheduler.PhaseReached += Drain;
            if (_director != null)
            {
                _director.NpcAddressed += OnNpcAddressed;
                _director.NpcSaid += OnNpcSaid;
                _director.TurnEnded += OnTurnEnded;
            }

            _lifetimeHost?.RegisterRuntimeLifetime(this);
        }

        /// <summary>The service instance <c>game:GetService("AIService")</c> returns.</summary>
        public RbxInstance Service => _service;

        /// <summary>Agents held for every mod.</summary>
        public int AgentCount => _agents.Count;

        /// <summary>Requests waiting for an answer.</summary>
        public int OutstandingRequests
        {
            get
            {
                lock (_gate)
                {
                    return _generations.Count + _asks.Count;
                }
            }
        }

        /// <summary>Registers the surface on one script registry at the registry's effective capabilities.</summary>
        public void Register(IScriptFunctionRegistry registry, LuaCapabilities capabilities)
        {
            if (!(registry is LuaCsApiRegistry luaRegistry))
            {
                throw new ArgumentException("The Rbx AI surface requires the Lua-CSharp registry adapter.",
                    nameof(registry));
            }

            luaRegistry.RegisterEnvironmentDecorator(state => InstallIntoState(state, capabilities));
        }

        /// <summary>Drains completions now (the scheduler does it every phase); for hosts and tests.</summary>
        internal void DrainForTesting()
        {
            Drain(SchedulerPhase.Heartbeat, 0d);
        }

        internal Task CurrentWorkForTesting()
        {
            lock (_gate)
            {
                List<Task> work = new();
                foreach (PendingGeneration pending in _generations.Values)
                {
                    work.Add(pending.Work);
                }

                return Task.WhenAll(work);
            }
        }

        public void Dispose()
        {
            List<PendingGeneration> generations;
            List<AgentRecord> agents;
            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                generations = new List<PendingGeneration>(_generations.Values);
                _generations.Clear();
                _asks.Clear();
                _stashedEnded.Clear();
                _queuedSignals.Clear();
            }

            _bindings.Scheduler.PhaseReached -= Drain;
            foreach (PendingGeneration pending in generations)
            {
                pending.Cancel();
            }

            agents = new List<AgentRecord>(_agents.Values);
            foreach (AgentRecord record in agents)
            {
                RemoveAgent(record);
            }

            if (_director != null)
            {
                _director.NpcAddressed -= OnNpcAddressed;
                _director.NpcSaid -= OnNpcSaid;
                _director.TurnEnded -= OnTurnEnded;
            }
        }

        private RbxInstance AttachService()
        {
            RbxDataModel game = _bindings.Game;
            RbxInstance existing = null;
            try
            {
                existing = game.Services.FindService(RbxAiClasses.AiService);
            }
            catch (RbxError)
            {
                existing = null;
            }

            if (existing is RbxAiService attached)
            {
                return attached;
            }

            RbxInstance service = RbxAiClasses.CreateDetachedService(_bindings.Registry.Catalog);
            game.Services.Register(RbxAiClasses.AiService, service);
            return service;
        }

        private void InstallIntoState(LuaState state, LuaCapabilities capabilities)
        {
            LuaValue gameValue = state.Environment["game"];
            if (gameValue.Type == LuaValueType.Nil)
            {
                return;
            }

            if (!TryGetInstance(gameValue, out LuaCsRbxInstanceProxy gameProxy))
            {
                throw new InvalidOperationException("The Rbx AI surface requires the production game proxy.");
            }

            LuaCsRbxModContext context = gameProxy.Context;
            LuaTable bridge = BuildBridge(state, context, capabilities);
            LuaTable generatorMethods = new();
            generatorMethods["GenerateTextAsync"] = bridge["GenerateTextAsync"];
            LuaTable serviceMethods = BuildServiceMethods(context, capabilities, bridge["Ask"]);
            LuaTable instanceMeta = gameProxy.Metatable;
            LuaValue originalIndex = instanceMeta[Metamethods.Index];
            LuaValue originalNewIndex = instanceMeta[Metamethods.NewIndex];
            if (originalIndex.Type != LuaValueType.Function || originalNewIndex.Type != LuaValueType.Function)
            {
                throw new InvalidOperationException(
                    "The Rbx AI surface requires the production Instance.__index and __newindex functions.");
            }

            instanceMeta[Metamethods.Index] = new LuaFunction("Instance.__index.ai", async (ctx, ct) =>
            {
                LuaValue selfValue = Arg(ctx, 0);
                LuaValue keyValue = Arg(ctx, 1);
                if (keyValue.Type == LuaValueType.String && TryGetInstance(selfValue, out LuaCsRbxInstanceProxy proxy) &&
                    ReferenceEquals(proxy.Context, context))
                {
                    string key = keyValue.Read<string>();
                    if (proxy.Instance is RbxTextGenerator generator)
                    {
                        if (TryReadGeneratorProperty(generator, key, out LuaValue property))
                        {
                            return ctx.Return(property);
                        }

                        if (generatorMethods.TryGetValue(key, out LuaValue method))
                        {
                            return ctx.Return(method);
                        }
                    }
                    else if (ReferenceEquals(proxy.Instance, _service) && serviceMethods.TryGetValue(key, out LuaValue method))
                    {
                        return ctx.Return(method);
                    }
                }

                LuaValue[] arguments = { selfValue, keyValue };
                LuaValue[] results = await ctx.State.CallAsync(originalIndex, arguments.AsSpan(), ct);
                return ctx.Return(results);
            });

            instanceMeta[Metamethods.NewIndex] = new LuaFunction("Instance.__newindex.ai", async (ctx, ct) =>
            {
                LuaValue selfValue = Arg(ctx, 0);
                LuaValue keyValue = Arg(ctx, 1);
                LuaValue value = Arg(ctx, 2);
                if (keyValue.Type == LuaValueType.String && TryGetInstance(selfValue, out LuaCsRbxInstanceProxy proxy) &&
                    ReferenceEquals(proxy.Context, context) && proxy.Instance is RbxTextGenerator generator &&
                    IsGeneratorProperty(keyValue.Read<string>()))
                {
                    try
                    {
                        WriteGeneratorProperty(context, generator, keyValue.Read<string>(), value);
                    }
                    catch (Exception ex)
                    {
                        throw ToLuaError(ctx.State, ex);
                    }

                    return ctx.Return();
                }

                LuaValue[] arguments = { selfValue, keyValue, value };
                LuaValue[] results = await ctx.State.CallAsync(originalNewIndex, arguments.AsSpan(), ct);
                return ctx.Return(results);
            });
        }

        private LuaTable BuildBridge(LuaState state, LuaCsRbxModContext context, LuaCapabilities capabilities)
        {
            LuaValue taskValue = state.Environment["task"];
            if (taskValue.Type != LuaValueType.Table)
            {
                throw new InvalidOperationException("The Rbx AI surface requires the production task scheduler table.");
            }

            LuaTable task = taskValue.Read<LuaTable>();
            task["_prepareTextGeneration"] = Fn("task._prepareTextGeneration",
                ctx => PrepareGeneration(ctx, context, capabilities), context);
            task["_prepareAgentAsk"] = Fn("task._prepareAgentAsk", ctx => PrepareAsk(ctx, context, capabilities), context);
            try
            {
                LuaValue[] results = LuaCsRbxHttpServiceAdapter.RequireSynchronousBridgeResult(
                    state.DoStringAsync(BridgeSource, "coreai_ai_bridge"));
                if (results.Length != 1 || results[0].Type != LuaValueType.Table)
                {
                    throw new InvalidOperationException("The Rbx AI scheduler bridge did not return its method table.");
                }

                return results[0].Read<LuaTable>();
            }
            finally
            {
                task["_prepareTextGeneration"] = LuaValue.Nil;
                task["_prepareAgentAsk"] = LuaValue.Nil;
            }
        }

        private LuaTable BuildServiceMethods(LuaCsRbxModContext context, LuaCapabilities capabilities, LuaValue ask)
        {
            LuaTable methods = new();
            methods["CreateAgent"] = Fn("AIService.CreateAgent", ctx =>
            {
                RequireService(ctx, context);
                RequireAllowed(context, capabilities, "AIService:CreateAgent");
                return new LuaValue(CreateAgent(ctx, context, ask));
            }, context);
            methods["GetAgent"] = Fn("AIService.GetAgent", ctx =>
            {
                RequireService(ctx, context);
                RequireAllowed(context, capabilities, "AIService:GetAgent");
                string name = ReadString(ctx, 1, "AIService:GetAgent", 1);
                SweepDeadAgents();
                AgentRecord record = FindAgent(context, name);
                return record?.Handle != null ? new LuaValue(record.Handle) : LuaValue.Nil;
            }, context);
            methods["GetAgents"] = Fn("AIService.GetAgents", ctx =>
            {
                RequireService(ctx, context);
                RequireAllowed(context, capabilities, "AIService:GetAgents");
                SweepDeadAgents();
                LuaTable list = new();
                int index = 1;
                foreach (AgentRecord record in _agents.Values)
                {
                    if (ReferenceEquals(record.Context, context) && record.Handle != null)
                    {
                        list[index++] = record.Handle;
                    }
                }

                return new LuaValue(list);
            }, context);
            methods["Hear"] = Fn("AIService.Hear", ctx =>
            {
                RequireService(ctx, context);
                RequireAllowed(context, capabilities, "AIService:Hear");
                RequireDirector("AIService:Hear");
                Speaker speaker = ReadSpeaker(Arg(ctx, 1), "AIService:Hear");
                string text = ReadString(ctx, 2, "AIService:Hear", 2);
                SweepDeadAgents();
                NpcHearResult heard = _director.Hear(NpcHeardLine.Chat(speaker.Id, speaker.Name, text, speaker.Position));
                return heard.IsTaken && _agents.TryGetValue(heard.Decision.NpcId, out AgentRecord record) &&
                       ReferenceEquals(record.Context, context) && record.Handle != null
                    ? new LuaValue(record.Handle)
                    : LuaValue.Nil;
            }, context);
            return methods;
        }

        private LuaTable CreateAgent(LuaFunctionExecutionContext ctx, LuaCsRbxModContext context, LuaValue ask)
        {
            string modId = (context.OwnerModId ?? "").Trim();
            if (modId.Length == 0)
            {
                throw new RbxError(RbxErrorCode.ContextViolation,
                    "AIService:CreateAgent needs a mod: an agent belongs to the mod that creates it and ends with it",
                    "create agents from a loaded mod, not from a one-off chunk");
            }

            RequireDirector("AIService:CreateAgent");
            LuaValue configValue = Arg(ctx, 1);
            if (!configValue.TryRead(out LuaTable config))
            {
                throw ExpectedArgument("AIService:CreateAgent config", "a table", configValue, 1);
            }

            foreach (KeyValuePair<LuaValue, LuaValue> pair in config)
            {
                string key = pair.Key.Type == LuaValueType.String ? pair.Key.Read<string>() : "";
                if (!AgentConfigKeys.Contains(key))
                {
                    throw RbxError.BadArgument(
                        "AIService:CreateAgent does not know the key " + KeyText(pair.Key),
                        "use only " + string.Join(", ", AgentConfigKeys));
                }
            }

            string name = ReadConfigString(config, "Name", true, MaxAgentNameLength);
            SweepDeadAgents();
            int owned = 0;
            foreach (AgentRecord existing in _agents.Values)
            {
                owned += string.Equals(existing.ModId, modId, StringComparison.Ordinal) ? 1 : 0;
            }

            if (_options.MaxAgentsPerMod > 0 && owned >= _options.MaxAgentsPerMod)
            {
                throw new RbxError(RbxErrorCode.BudgetExceeded,
                    "AIService:CreateAgent: mod '" + modId + "' already holds " + owned + " agents, the host's limit",
                    "destroy an agent you no longer need with agent:Destroy()", modId);
            }

            if (FindAgent(context, name) != null)
            {
                throw RbxError.BadArgument("an agent named '" + name + "' already exists in this mod",
                    "reuse AIService:GetAgent('" + name + "') or destroy the old agent first");
            }

            // WHY a suffix instead of a refusal: a reload runs the new chunk before the outgoing generation is torn
            // down, and a replaced world's surface leaves the shared director only when it is disposed, so an agent
            // of the same name can still be alive here for one moment.
            string id = AgentId(context, name);
            while (_agents.ContainsKey(id) || _director.TryGet(id, out _))
            {
                id = AgentId(context, name) + "#" +
                     (++_agentSuffix).ToString(System.Globalization.CultureInfo.InvariantCulture);
            }

            RbxInstance body = ReadConfigBody(config);
            NpcAgentDefinition definition = new()
            {
                Persona = new NpcPersona
                {
                    Id = id,
                    DisplayName = name,
                    Aliases = ReadConfigStrings(config, "Aliases"),
                    PromptFragment = ReadConfigString(config, "Persona", false, 4000),
                    VoiceRules = ReadConfigString(config, "VoiceRules", false, 2000),
                    FallbackLines = ReadConfigStrings(config, "FallbackLines"),
                    RoutingProfileId = ReadConfigString(config, "RoutingProfile", false, 128)
                },
                Addressing = ReadAddressing(config),
                Budget = ReadBudget(config),
                MemoryKey = modId + "/" + NonEmpty(ReadConfigString(config, "Memory", false, 128), name),
                OriginTag = OriginTag.FromAi(modId),
                SourceTag = "Mod:" + modId,
                PositionSource = body != null ? () => StudsPosition(body) : (Func<NpcPosition?>)null
            };

            NpcAgent agent;
            try
            {
                agent = _director.Add(definition);
            }
            catch (InvalidOperationException ex)
            {
                throw RbxError.BadArgument("AIService:CreateAgent: " + ex.Message,
                    "destroy agents you no longer need before creating more");
            }

            AgentRecord record = new(agent.Id, name, modId, context, body, agent.OriginTag);
            record.Replied.BindScheduler(_bindings.Scheduler);
            record.Addressed.BindScheduler(_bindings.Scheduler);
            record.LifetimeSignal.BindScheduler(_bindings.Scheduler);
            record.Lifetime = record.LifetimeSignal.Connect((Action<object[]>)(_ => { }));
            context.TrackConnection(record.Lifetime);
            record.Handle = BuildHandle(record, context, ask);
            _agents.Add(record.NpcId, record);
            _handles[record.Handle] = record;
            if (body != null && _anchors != null)
            {
                _anchors.Register(record.NpcId, () => BubbleAnchor(body));
            }

            return record.Handle;
        }

        private LuaTable BuildHandle(AgentRecord record, LuaCsRbxModContext context, LuaValue ask)
        {
            LuaTable handle = new();
            handle["Name"] = record.Name;
            handle["Id"] = record.NpcId;
            handle["OriginTag"] = record.OriginTag;
            handle["Replied"] = LuaCsRbxDatatypeBindings.Wrap(record.Replied, context);
            handle["Addressed"] = LuaCsRbxDatatypeBindings.Wrap(record.Addressed, context);
            handle["Ask"] = ask;
            handle["Say"] = Fn("Agent.Say", ctx =>
            {
                AgentRecord self = RequireAgent(ctx, context, "agent:Say");
                string text = ReadString(ctx, 1, "agent:Say", 1);
                if (!_director.Say(self.NpcId, text))
                {
                    throw RbxError.BadArgument("agent:Say needs a non-empty line", "pass the words the agent says");
                }

                return LuaValue.Nil;
            }, context);
            handle["IsSpeaking"] = Fn("Agent.IsSpeaking", ctx =>
            {
                AgentRecord self = RequireAgent(ctx, context, "agent:IsSpeaking");
                return _director.TryGet(self.NpcId, out NpcAgent agent) && (agent.IsSpeaking || agent.HasPendingLine);
            }, context);
            handle["Destroy"] = Fn("Agent.Destroy", ctx =>
            {
                if (Arg(ctx, 0).TryRead(out LuaTable table) && _handles.TryGetValue(table, out AgentRecord self) &&
                    ReferenceEquals(self.Context, context))
                {
                    RemoveAgent(self);
                }

                return LuaValue.Nil;
            }, context);
            return handle;
        }

        private LuaValue PrepareGeneration(LuaFunctionExecutionContext ctx, LuaCsRbxModContext context,
            LuaCapabilities capabilities)
        {
            if (!TryGetInstance(Arg(ctx, 0), out LuaCsRbxInstanceProxy proxy) || !ReferenceEquals(proxy.Context, context) ||
                !(proxy.Instance is RbxTextGenerator generator))
            {
                throw RbxError.BadArgument("TextGenerator:GenerateTextAsync expects a TextGenerator as self",
                    "call it with a colon: generator:GenerateTextAsync({ UserPrompt = text })");
            }

            if (generator.IsDestroyed)
            {
                throw RbxError.InstanceDestroyed("GenerateTextAsync", generator.Name, generator.Id);
            }

            RequireAllowed(context, capabilities, "TextGenerator:GenerateTextAsync");
            if (_text == null)
            {
                throw new RbxError(RbxErrorCode.NotImplemented,
                    "TextGenerator:GenerateTextAsync has no model in this world: the host attached no text backend",
                    "the host composes the AI surface with its orchestration service", context.OwnerModId);
            }

            RbxTextGenerationRequest request = ReadRequest(Arg(ctx, 1), generator, context);
            JObject schema = null;
            if (request.JsonSchema.Length > 0 &&
                !RbxJsonSchemaCheck.TryParseSchema(request.JsonSchema, out schema, out string schemaReason))
            {
                throw RbxError.BadArgument("TextGenerator:GenerateTextAsync JsonSchema: " + schemaReason,
                    "pass a json-schema.org object as a JSON string");
            }

            string contextToken = ReadOptionalRequestString(Arg(ctx, 1), "ContextToken");
            if (!_contexts.TryGet(contextToken, out IReadOnlyList<RbxTextGenerationTurn> history))
            {
                throw RbxError.BadArgument(
                    "TextGenerator:GenerateTextAsync: the ContextToken was not issued by this world or has expired",
                    "pass the ContextToken of an earlier answer, or omit it to start a new conversation");
            }

            request.History = history;
            if (!_moderation.TryFilter(request.UserPrompt, TextModerationDirection.Input, out string filtered))
            {
                throw Failure(RbxTextGenerationFailure.Moderated, "the prompt was refused by the host's filter",
                    context.OwnerModId);
            }

            request.UserPrompt = filtered ?? "";
            CancellationToken lifetime = _lifetimeHost?.CurrentScriptLifetimeToken ?? new CancellationToken(true);
            if (!lifetime.CanBeCanceled || lifetime.IsCancellationRequested)
            {
                throw new RbxError(RbxErrorCode.ContextViolation,
                    "TextGenerator:GenerateTextAsync yields and needs a live scheduler thread",
                    "call it from the mod's chunk, a task.spawn function or a signal handler");
            }

            if (!_quota.TryTake(_clock(), out double retryAfter))
            {
                throw Failure(RbxTextGenerationFailure.QuotaExceeded,
                    "the world's limit of " + _options.TextGeneratorRequestsPerMinute +
                    " requests per minute is reached; retry in " + Math.Ceiling(retryAfter) + " s",
                    context.OwnerModId);
            }

            long id = Interlocked.Increment(ref _generationCounter);
            PendingGeneration pending = new(id, new RbxScriptSignal("TextGenerator.GenerateTextAsync[" + id + "]"),
                lifetime, request, schema, history, context);
            LuaValue wrapped = LuaCsRbxDatatypeBindings.Wrap(pending.Signal, context);
            lock (_gate)
            {
                if (_disposed || _generations.Count + _asks.Count >= Math.Max(1, _options.MaxOutstandingRequests))
                {
                    pending.Cancel();
                    _quota.Release();
                    throw new RbxError(RbxErrorCode.BudgetExceeded,
                        "TextGenerator:GenerateTextAsync: too many requests are waiting for an answer",
                        "wait for an answer before asking again", context.OwnerModId);
                }

                _generations.Add(id, pending);
            }

            pending.Work = RunGenerationAsync(pending);
            return wrapped;
        }

        private LuaValue PrepareAsk(LuaFunctionExecutionContext ctx, LuaCsRbxModContext context,
            LuaCapabilities capabilities)
        {
            AgentRecord record = RequireAgent(ctx, context, "agent:Ask");
            RequireAllowed(context, capabilities, "agent:Ask");
            string text = ReadString(ctx, 1, "agent:Ask", 1);
            Speaker speaker = ReadSpeaker(Arg(ctx, 2), "agent:Ask");
            CancellationToken lifetime = _lifetimeHost?.CurrentScriptLifetimeToken ?? new CancellationToken(true);
            if (!lifetime.CanBeCanceled || lifetime.IsCancellationRequested)
            {
                throw new RbxError(RbxErrorCode.ContextViolation, "agent:Ask yields and needs a live scheduler thread",
                    "call it from the mod's chunk, a task.spawn function or a signal handler");
            }

            lock (_gate)
            {
                if (_generations.Count + _asks.Count >= Math.Max(1, _options.MaxOutstandingRequests))
                {
                    throw new RbxError(RbxErrorCode.BudgetExceeded, "agent:Ask: too many requests are waiting for an answer",
                        "wait for an answer before asking again", context.OwnerModId);
                }
            }

            NpcHeardLine line = NpcHeardLine.Interaction(record.NpcId, speaker.Id, speaker.Name, text, speaker.Position);
            NpcHearResult result;
            _inAskCall = true;
            try
            {
                result = _director.Ask(record.NpcId, line);
            }
            finally
            {
                _inAskCall = false;
            }

            if (!result.IsTaken)
            {
                _stashedEnded.Clear();
                throw RbxError.BadArgument("agent:Ask: the agent cannot take the line (" + result.Decision.IgnoreReason + ")",
                    "ask a live agent with a non-empty line");
            }

            PendingAsk pending = new(result.TurnId, new RbxScriptSignal("Agent.Ask[" + result.TurnId + "]"), lifetime);
            LuaValue wrapped = LuaCsRbxDatatypeBindings.Wrap(pending.Signal, context);
            lock (_gate)
            {
                if (_stashedEnded.TryGetValue(result.TurnId, out PendingAsk ended))
                {
                    pending.Complete(ended.Text, ended.IsFallback);
                }

                _stashedEnded.Clear();
                _asks[result.TurnId] = pending;
            }

            return wrapped;
        }

        private async Task RunGenerationAsync(PendingGeneration pending)
        {
            RbxTextGenerationResult result;
            try
            {
                Task<RbxTextGenerationResult> work = _text.GenerateAsync(pending.Request, pending.Token);
                result = work == null
                    ? RbxTextGenerationResult.Failed(RbxTextGenerationFailure.ProviderError, "the text backend returned no task")
                    : await work;
            }
            catch (OperationCanceledException)
            {
                result = RbxTextGenerationResult.Failed(RbxTextGenerationFailure.Cancelled, "the request was stopped");
            }
            catch (Exception ex)
            {
                result = RbxTextGenerationResult.Failed(RbxTextGenerationFailure.ProviderError,
                    "the text backend failed (" + ex.GetType().Name + ")");
            }

            lock (_gate)
            {
                pending.Result = result ?? RbxTextGenerationResult.Failed(RbxTextGenerationFailure.EmptyResponse,
                    "the model answered nothing");
                pending.Completed = true;
            }
        }

        private void Drain(SchedulerPhase phase, double deltaSeconds)
        {
            if (_disposed)
            {
                return;
            }

            // WHY Heartbeat only: this subscriber runs on all six phases of every frame, and the director needs one
            // tick per frame; the sweep is just as cheap to do once.
            if (phase == SchedulerPhase.Heartbeat && _agents.Count > 0)
            {
                SweepDeadAgents();
                _director?.Tick();
            }

            List<PendingGeneration> finished = null;
            List<PendingAsk> answered = null;
            lock (_gate)
            {
                if (_generations.Count == 0 && _asks.Count == 0 && _queuedSignals.Count == 0)
                {
                    return;
                }

                RemoveCancelledLocked();
                foreach (PendingGeneration pending in _generations.Values)
                {
                    if (pending.Completed)
                    {
                        (finished ??= new List<PendingGeneration>()).Add(pending);
                    }
                }

                foreach (PendingAsk pending in _asks.Values)
                {
                    if (pending.Completed)
                    {
                        (answered ??= new List<PendingAsk>()).Add(pending);
                    }
                }

                if (finished != null)
                {
                    foreach (PendingGeneration pending in finished)
                    {
                        _generations.Remove(pending.Id);
                    }
                }

                if (answered != null)
                {
                    foreach (PendingAsk pending in answered)
                    {
                        _asks.Remove(pending.TurnId);
                    }
                }

                _firing.AddRange(_queuedSignals);
                _queuedSignals.Clear();
            }

            try
            {
                foreach (Action fire in _firing)
                {
                    FireContained(fire);
                }
            }
            finally
            {
                _firing.Clear();
            }

            if (finished != null)
            {
                foreach (PendingGeneration pending in finished)
                {
                    FireContained(() => FinishGeneration(pending));
                    pending.Release();
                }
            }

            if (answered != null)
            {
                foreach (PendingAsk pending in answered)
                {
                    if (!pending.Token.IsCancellationRequested)
                    {
                        FireContained(() => pending.Signal.Fire(true, pending.Text, pending.IsFallback));
                    }
                }
            }
        }

        private void FinishGeneration(PendingGeneration pending)
        {
            if (pending.Token.IsCancellationRequested)
            {
                return;
            }

            RbxTextGenerationResult result = pending.Result;
            if (!result.Ok)
            {
                pending.Signal.Fire(false, FailureMessage(result.Failure, result.Message));
                return;
            }

            string text = (result.GeneratedText ?? "").Trim();
            if (pending.Schema != null)
            {
                if (!RbxJsonSchemaCheck.TryConform(text, pending.Schema, out string json, out string reason))
                {
                    pending.Signal.Fire(false, FailureMessage(RbxTextGenerationFailure.SchemaMismatch,
                        "the answer does not conform to the JsonSchema: " + reason));
                    return;
                }

                text = json;
            }
            else if (text.Length == 0)
            {
                pending.Signal.Fire(false, FailureMessage(RbxTextGenerationFailure.EmptyResponse, "the model answered nothing"));
                return;
            }

            if (!_moderation.TryFilter(text, TextModerationDirection.Output, out string shown))
            {
                pending.Signal.Fire(false, FailureMessage(RbxTextGenerationFailure.Moderated,
                    "the answer was refused by the host's filter"));
                return;
            }

            string token = _contexts.Append(pending.History,
                new RbxTextGenerationTurn(pending.Request.UserPrompt, shown ?? ""));
            LuaTable table = new();
            table["GeneratedText"] = shown ?? "";
            table["ContextToken"] = token;
            table["Model"] = result.Model ?? "";
            pending.Signal.Fire(true, new LuaValue(table));
        }

        private void OnNpcAddressed(NpcAddressed addressed)
        {
            if (!_agents.TryGetValue(addressed.NpcId, out AgentRecord record))
            {
                return;
            }

            string speaker = addressed.SpeakerName;
            string text = addressed.Text;
            string reason = addressed.Reason.ToString();
            Queue(() => record.Addressed.Fire(speaker, text, reason));
        }

        private void OnNpcSaid(NpcSaid said)
        {
            if (said.IsScripted || !_agents.TryGetValue(said.NpcId, out AgentRecord record))
            {
                return;
            }

            string text = said.Text;
            string speaker = said.SpeakerName;
            bool fallback = said.IsFallback;
            Queue(() => record.Replied.Fire(text, speaker, fallback));
        }

        private void OnTurnEnded(NpcTurnEnded ended)
        {
            bool spoke = ended.Outcome == NpcTurnOutcome.Replied || ended.Outcome == NpcTurnOutcome.FallbackLine;
            string text = spoke ? ended.Text : null;
            bool fallback = ended.Outcome == NpcTurnOutcome.FallbackLine;
            lock (_gate)
            {
                if (_asks.TryGetValue(ended.TurnId, out PendingAsk pending))
                {
                    pending.Complete(text, fallback);
                    return;
                }

                if (_inAskCall)
                {
                    PendingAsk stash = new(ended.TurnId, null, CancellationToken.None);
                    stash.Complete(text, fallback);
                    _stashedEnded[ended.TurnId] = stash;
                }
            }
        }

        private void Queue(Action fire)
        {
            lock (_gate)
            {
                if (!_disposed)
                {
                    _queuedSignals.Add(fire);
                }
            }
        }

        private void FireContained(Action fire)
        {
            try
            {
                fire();
            }
            catch (Exception ex)
            {
                // WHY contained: this runs in the scheduler's phase drain; one failed delivery must not abort the
                // drain of every other request or the frame of every other mod.
                _log.Warn("[RbxApi] Delivering an AI result failed: " + ex.GetType().Name + ": " + ex.Message, LogTag.Lua);
            }
        }

        private void RemoveCancelledLocked()
        {
            List<long> cancelledGenerations = null;
            foreach (PendingGeneration pending in _generations.Values)
            {
                if (pending.Token.IsCancellationRequested)
                {
                    (cancelledGenerations ??= new List<long>()).Add(pending.Id);
                }
            }

            if (cancelledGenerations != null)
            {
                foreach (long id in cancelledGenerations)
                {
                    _generations[id].Cancel();
                    _generations.Remove(id);
                }
            }

            List<long> cancelledAsks = null;
            foreach (PendingAsk pending in _asks.Values)
            {
                if (pending.Token.IsCancellationRequested)
                {
                    (cancelledAsks ??= new List<long>()).Add(pending.TurnId);
                }
            }

            if (cancelledAsks != null)
            {
                foreach (long id in cancelledAsks)
                {
                    _asks.Remove(id);
                }
            }
        }

        private void SweepDeadAgents()
        {
            List<AgentRecord> dead = null;
            foreach (AgentRecord record in _agents.Values)
            {
                if (record.Lifetime == null || !record.Lifetime.Connected)
                {
                    (dead ??= new List<AgentRecord>()).Add(record);
                }
            }

            if (dead == null)
            {
                return;
            }

            foreach (AgentRecord record in dead)
            {
                RemoveAgent(record);
            }
        }

        private void RemoveAgent(AgentRecord record)
        {
            if (record.Destroyed)
            {
                return;
            }

            record.Destroyed = true;
            _agents.Remove(record.NpcId);
            if (record.Handle != null)
            {
                _handles.Remove(record.Handle);
            }

            _anchors?.Unregister(record.NpcId);
            _director?.Remove(record.NpcId);
            if (record.Lifetime != null && record.Lifetime.Connected)
            {
                record.Lifetime.Disconnect();
            }

            record.Replied.DisconnectAll();
            record.Addressed.DisconnectAll();
        }

        private void RequireAllowed(LuaCsRbxModContext context, LuaCapabilities capabilities, string member)
        {
            if (!_options.AllowAgentsFromLua)
            {
                throw new RbxError(RbxErrorCode.NotAuthority,
                    member + " is turned off in this game: the host's framework setting " + CoreAiLuaAiOptions.SettingName +
                    " is false",
                    "the game turns on " + CoreAiLuaAiOptions.SettingName + " for all its worlds; a world or a mod cannot",
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

        private void RequireDirector(string member)
        {
            if (_director == null)
            {
                throw new RbxError(RbxErrorCode.NotImplemented,
                    member + " has no agent director in this world", "the host composes the AI surface with its NPC director",
                    null);
            }
        }

        private void RequireService(LuaFunctionExecutionContext ctx, LuaCsRbxModContext context)
        {
            if (!TryGetInstance(Arg(ctx, 0), out LuaCsRbxInstanceProxy proxy) || !ReferenceEquals(proxy.Context, context) ||
                !ReferenceEquals(proxy.Instance, _service))
            {
                throw RbxError.BadArgument("AIService method expects AIService as self",
                    "call methods with a colon on game:GetService('AIService')");
            }
        }

        private AgentRecord RequireAgent(LuaFunctionExecutionContext ctx, LuaCsRbxModContext context, string member)
        {
            SweepDeadAgents();
            if (!Arg(ctx, 0).TryRead(out LuaTable table) || !_handles.TryGetValue(table, out AgentRecord record) ||
                !ReferenceEquals(record.Context, context))
            {
                throw RbxError.BadArgument(member + " expects a live agent of this mod as self",
                    "call it with a colon on the agent AIService:CreateAgent returned, before destroying it");
            }

            return record;
        }

        private RbxTextGenerationRequest ReadRequest(LuaValue requestValue, RbxTextGenerator generator,
            LuaCsRbxModContext context)
        {
            RbxTextGenerationRequest request = new()
            {
                SystemPrompt = generator.SystemPrompt,
                Temperature = generator.Temperature,
                TopP = generator.TopP,
                Seed = generator.Seed,
                ModId = context.OwnerModId ?? "",
                MaxTokens = Math.Max(1, _options.TextGeneratorMaxTokens)
            };

            if (requestValue.Type == LuaValueType.Nil)
            {
                return request;
            }

            if (!requestValue.TryRead(out LuaTable table))
            {
                throw ExpectedArgument("TextGenerator:GenerateTextAsync request", "a table", requestValue, 1);
            }

            foreach (KeyValuePair<LuaValue, LuaValue> pair in table)
            {
                string key = pair.Key.Type == LuaValueType.String ? pair.Key.Read<string>() : "";
                if (!RequestKeys.Contains(key))
                {
                    throw RbxError.BadArgument(
                        "TextGenerator:GenerateTextAsync request does not know the key " + KeyText(pair.Key),
                        "use only UserPrompt, ContextToken, MaxTokens and JsonSchema");
                }
            }

            request.UserPrompt = ReadOptionalRequestString(requestValue, "UserPrompt");
            request.JsonSchema = ReadOptionalRequestString(requestValue, "JsonSchema").Trim();
            LuaValue maxTokens = table["MaxTokens"];
            if (maxTokens.Type != LuaValueType.Nil)
            {
                if (!TryCoerceNumber(maxTokens, out double number) || double.IsNaN(number) ||
                    Math.Round(number, MidpointRounding.AwayFromZero) < 1d)
                {
                    throw RbxError.BadArgument("TextGenerator:GenerateTextAsync MaxTokens must be an integer of at least 1",
                        "pass a positive number of tokens, or omit MaxTokens");
                }

                double rounded = Math.Round(number, MidpointRounding.AwayFromZero);
                int cap = Math.Max(1, _options.TextGeneratorMaxTokens);
                request.MaxTokens = rounded >= cap ? cap : (int)rounded;
            }

            return request;
        }

        private static string ReadOptionalRequestString(LuaValue requestValue, string key)
        {
            if (!requestValue.TryRead(out LuaTable table))
            {
                return "";
            }

            LuaValue value = table[key];
            if (value.Type == LuaValueType.Nil)
            {
                return "";
            }

            if (value.Type != LuaValueType.String)
            {
                throw RbxError.BadArgument("TextGenerator:GenerateTextAsync " + key + " must be a string",
                    "pass " + key + " as text");
            }

            return value.Read<string>();
        }

        private static bool IsGeneratorProperty(string key)
        {
            return key == "SystemPrompt" || key == "Temperature" || key == "TopP" || key == "Seed";
        }

        private static bool TryReadGeneratorProperty(RbxTextGenerator generator, string key, out LuaValue value)
        {
            switch (key)
            {
                case "SystemPrompt":
                    value = generator.SystemPrompt;
                    return true;
                case "Temperature":
                    value = generator.Temperature;
                    return true;
                case "TopP":
                    value = generator.TopP;
                    return true;
                case "Seed":
                    value = generator.Seed;
                    return true;
                default:
                    value = LuaValue.Nil;
                    return false;
            }
        }

        private static void WriteGeneratorProperty(LuaCsRbxModContext context, RbxTextGenerator generator, string key,
            LuaValue value)
        {
            context.ApplyServerGeneratedMutation("write instance member", () =>
            {
                if (generator.IsDestroyed)
                {
                    throw RbxError.InstanceDestroyed(key, generator.Name, generator.Id);
                }

                context.RequireWorldEditForWrite(generator, key);
                switch (key)
                {
                    case "SystemPrompt":
                        generator.SystemPrompt = ReadAssignedString(value, generator.ClassName, key);
                        break;
                    case "Temperature":
                        generator.Temperature = RbxTextGenerator.ClampTemperature(
                            ReadAssignedNumber(value, generator.ClassName, key));
                        break;
                    case "TopP":
                        generator.TopP = RbxTextGenerator.ClampTopP(ReadAssignedNumber(value, generator.ClassName, key));
                        break;
                    case "Seed":
                        generator.Seed = RbxTextGenerator.TruncateSeed(ReadAssignedNumber(value, generator.ClassName, key));
                        break;
                }

                return LuaValue.Nil;
            });
        }

        private Speaker ReadSpeaker(LuaValue value, string member)
        {
            if (value.Type == LuaValueType.Nil)
            {
                return new Speaker("player", "", null);
            }

            if (value.Type == LuaValueType.String)
            {
                string name = value.Read<string>().Trim();
                return new Speaker(name.Length > 0 ? name : "player", name, null);
            }

            if (TryGetInstance(value, out LuaCsRbxInstanceProxy proxy))
            {
                if (proxy.Instance is RbxPlayer player)
                {
                    string id = player.UserId > 0
                        ? "player:" + player.UserId.ToString(System.Globalization.CultureInfo.InvariantCulture)
                        : "player:" + player.Name;
                    string name = string.IsNullOrWhiteSpace(player.DisplayName) ? player.Name : player.DisplayName;
                    return new Speaker(id, name, StudsPosition(player));
                }

                return new Speaker("instance:" + proxy.Instance.Id, proxy.Instance.Name, StudsPosition(proxy.Instance));
            }

            throw ExpectedArgument(member + " speaker", "a Player, a name or nil", value, 2);
        }

        private NpcAddressingRules ReadAddressing(LuaTable config)
        {
            NpcAddressingRules rules = (_options.AgentAddressing ?? new NpcAddressingRules()).Clone();
            rules.HearingRadius = ReadConfigNumber(config, "HearingRadius", rules.HearingRadius);
            rules.InteractionRadius = ReadConfigNumber(config, "InteractionRadius", rules.InteractionRadius);
            rules.FollowUpSeconds = ReadConfigNumber(config, "FollowUpSeconds", rules.FollowUpSeconds);
            rules.AnswerUnaddressedWithin = ReadConfigNumber(config, "AnswerUnaddressedWithin",
                rules.AnswerUnaddressedWithin);
            LuaValue sight = config["RequireLineOfSight"];
            if (sight.Type == LuaValueType.Boolean)
            {
                rules.RequireLineOfSight = sight.Read<bool>();
            }
            else if (sight.Type != LuaValueType.Nil)
            {
                throw RbxError.BadArgument("AIService:CreateAgent RequireLineOfSight must be a boolean",
                    "pass true or false");
            }

            return rules;
        }

        private NpcBudget ReadBudget(LuaTable config)
        {
            NpcBudget budget = (_options.AgentBudget ?? new NpcBudget()).Clone();
            budget.CooldownSeconds = ReadConfigNumber(config, "CooldownSeconds", budget.CooldownSeconds);
            double maxTokens = ReadConfigNumber(config, "MaxTokens", budget.MaxOutputTokens);
            int hostCap = (_options.AgentBudget ?? new NpcBudget()).MaxOutputTokens;
            int requested = (int)Math.Max(1d, Math.Min(int.MaxValue, Math.Round(maxTokens)));
            budget.MaxOutputTokens = hostCap > 0 ? Math.Min(requested, hostCap) : requested;
            return budget;
        }

        private RbxInstance ReadConfigBody(LuaTable config)
        {
            LuaValue value = config["Body"];
            if (value.Type == LuaValueType.Nil)
            {
                return null;
            }

            if (!TryGetInstance(value, out LuaCsRbxInstanceProxy proxy) ||
                !(proxy.Instance.IsA("BasePart") || proxy.Instance is RbxModel))
            {
                throw RbxError.BadArgument("AIService:CreateAgent Body must be a BasePart or a Model",
                    "pass the villager's model (its PrimaryPart or HumanoidRootPart is used) or a part");
            }

            return proxy.Instance;
        }

        private static string ReadConfigString(LuaTable config, string key, bool required, int maxLength)
        {
            LuaValue value = config[key];
            if (value.Type == LuaValueType.Nil)
            {
                if (required)
                {
                    throw RbxError.BadArgument("AIService:CreateAgent needs " + key, "pass " + key + " = \"...\"");
                }

                return "";
            }

            if (value.Type != LuaValueType.String)
            {
                throw RbxError.BadArgument("AIService:CreateAgent " + key + " must be a string", "pass " + key + " as text");
            }

            string text = value.Read<string>().Trim();
            if (required && text.Length == 0)
            {
                throw RbxError.BadArgument("AIService:CreateAgent " + key + " must not be empty", "pass a non-empty " + key);
            }

            if (text.Length > maxLength)
            {
                throw RbxError.BadArgument("AIService:CreateAgent " + key + " is longer than " + maxLength + " characters",
                    "shorten " + key);
            }

            return text;
        }

        private static IReadOnlyList<string> ReadConfigStrings(LuaTable config, string key)
        {
            LuaValue value = config[key];
            if (value.Type == LuaValueType.Nil)
            {
                return Array.Empty<string>();
            }

            if (value.Type == LuaValueType.String)
            {
                return new[] { value.Read<string>() };
            }

            if (!value.TryRead(out LuaTable list))
            {
                throw RbxError.BadArgument("AIService:CreateAgent " + key + " must be a list of strings",
                    "pass " + key + " = { \"...\", \"...\" }");
            }

            List<string> items = new();
            for (int index = 1; index <= 64; index++)
            {
                LuaValue item = list[index];
                if (item.Type == LuaValueType.Nil)
                {
                    break;
                }

                if (item.Type != LuaValueType.String)
                {
                    throw RbxError.BadArgument("AIService:CreateAgent " + key + " must hold only strings",
                        "pass " + key + " = { \"...\", \"...\" }");
                }

                items.Add(item.Read<string>());
            }

            return items;
        }

        private static double ReadConfigNumber(LuaTable config, string key, double fallback)
        {
            LuaValue value = config[key];
            if (value.Type == LuaValueType.Nil)
            {
                return fallback;
            }

            if (!TryCoerceNumber(value, out double number) || double.IsNaN(number) || number < 0d)
            {
                throw RbxError.BadArgument("AIService:CreateAgent " + key + " must be a number of at least 0",
                    "pass " + key + " as a number");
            }

            return number;
        }

        private NpcPosition? StudsPosition(RbxInstance target)
        {
            RbxInstance part = ResolvePart(target);
            if (part == null)
            {
                return null;
            }

            RbxVector3 position = _bindings.PartSink.GetLivePositionStuds(part.Id);
            return new NpcPosition(position.X, position.Y, position.Z);
        }

        private NpcPosition? BubbleAnchor(RbxInstance body)
        {
            RbxInstance part = ResolvePart(body);
            if (part == null)
            {
                return null;
            }

            if (_bodyAnchor != null)
            {
                return _bodyAnchor(part);
            }

            RbxVector3 studs = _bindings.PartSink.GetLivePositionStuds(part.Id);
            UnityEngine.Vector3 world = CoreAI.Mods.Rbx.Spatial.RbxSpace.ToUnity(
                new RbxVector3(studs.X, studs.Y + (float)BubbleHeadOffsetStuds, studs.Z));
            return new NpcPosition(world.x, world.y, world.z);
        }

        private static RbxInstance ResolvePart(RbxInstance target)
        {
            RbxInstance current = target is RbxPlayer player ? player.Character : target;
            if (current == null || current.IsDestroyed)
            {
                return null;
            }

            if (current.IsA("BasePart"))
            {
                return current;
            }

            if (current is RbxModel model)
            {
                if (model.PrimaryPart != null && !model.PrimaryPart.IsDestroyed)
                {
                    return model.PrimaryPart;
                }

                RbxInstance root = model.FindFirstChild("HumanoidRootPart");
                if (root != null && root.IsA("BasePart"))
                {
                    return root;
                }

                return model.FindFirstChildWhichIsA("BasePart");
            }

            return null;
        }

        private static string KeyText(LuaValue key)
        {
            return key.Type == LuaValueType.String ? "'" + key.Read<string>() + "'" : "of type " + Describe(key);
        }

        private static string AgentId(LuaCsRbxModContext context, string name)
        {
            return (context.OwnerModId ?? "").Trim() + "/" + (name ?? "").Trim();
        }

        private AgentRecord FindAgent(LuaCsRbxModContext context, string name)
        {
            string trimmed = (name ?? "").Trim();
            foreach (AgentRecord record in _agents.Values)
            {
                if (ReferenceEquals(record.Context, context) && !record.Destroyed &&
                    string.Equals(record.Name, trimmed, StringComparison.Ordinal))
                {
                    return record;
                }
            }

            return null;
        }

        private static string NonEmpty(string value, string fallback)
        {
            return string.IsNullOrWhiteSpace(value) ? fallback : value;
        }

        private static RbxError Failure(RbxTextGenerationFailure failure, string message, string modId)
        {
            return new RbxError(failure == RbxTextGenerationFailure.QuotaExceeded
                    ? RbxErrorCode.BudgetExceeded
                    : RbxErrorCode.BadArgument,
                "TextGenerator:GenerateTextAsync failed (" + failure + "): " + message,
                "handle the failure with pcall and try again later", modId);
        }

        private static string FailureMessage(RbxTextGenerationFailure failure, string message)
        {
            return "TextGenerator:GenerateTextAsync failed (" + failure + "): " + message;
        }

        private readonly struct Speaker
        {
            public Speaker(string id, string name, NpcPosition? position)
            {
                Id = id ?? "";
                Name = name ?? "";
                Position = position;
            }

            public string Id { get; }

            public string Name { get; }

            public NpcPosition? Position { get; }
        }

        private sealed class AgentRecord
        {
            public AgentRecord(string npcId, string name, string modId, LuaCsRbxModContext context, RbxInstance body,
                string originTag)
            {
                NpcId = npcId;
                Name = name;
                ModId = modId;
                Context = context;
                Body = body;
                OriginTag = originTag;
                Replied = new RbxScriptSignal("Agent.Replied");
                Addressed = new RbxScriptSignal("Agent.Addressed");
                LifetimeSignal = new RbxScriptSignal("Agent.Lifetime");
            }

            public string NpcId { get; }

            public string Name { get; }

            public string ModId { get; }

            public LuaCsRbxModContext Context { get; }

            public RbxInstance Body { get; }

            public string OriginTag { get; }

            public RbxScriptSignal Replied { get; }

            public RbxScriptSignal Addressed { get; }

            public RbxScriptSignal LifetimeSignal { get; }

            public RbxScriptConnection Lifetime { get; set; }

            public LuaTable Handle { get; set; }

            public bool Destroyed { get; set; }
        }

        private sealed class PendingGeneration
        {
            private readonly CancellationTokenSource _source;

            public PendingGeneration(long id, RbxScriptSignal signal, CancellationToken lifetime,
                RbxTextGenerationRequest request, JObject schema, IReadOnlyList<RbxTextGenerationTurn> history,
                LuaCsRbxModContext context)
            {
                Id = id;
                Signal = signal;
                _source = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
                Token = _source.Token;
                Request = request;
                Schema = schema;
                History = history;
                Context = context;
            }

            public long Id { get; }

            public RbxScriptSignal Signal { get; }

            public CancellationToken Token { get; }

            public RbxTextGenerationRequest Request { get; }

            public JObject Schema { get; }

            public IReadOnlyList<RbxTextGenerationTurn> History { get; }

            public LuaCsRbxModContext Context { get; }

            public Task Work { get; set; } = Task.CompletedTask;

            public bool Completed { get; set; }

            public RbxTextGenerationResult Result { get; set; }

            public void Release()
            {
                _source.Dispose();
            }

            public void Cancel()
            {
                try
                {
                    _source.Cancel();
                }
                catch (ObjectDisposedException)
                {
                }
                catch (AggregateException)
                {
                }
            }
        }

        private sealed class PendingAsk
        {
            public PendingAsk(long turnId, RbxScriptSignal signal, CancellationToken lifetime)
            {
                TurnId = turnId;
                Signal = signal;
                Token = lifetime;
            }

            public long TurnId { get; }

            public RbxScriptSignal Signal { get; }

            public CancellationToken Token { get; }

            public bool Completed { get; private set; }

            public string Text { get; private set; }

            public bool IsFallback { get; private set; }

            public void Complete(string text, bool isFallback)
            {
                Text = text;
                IsFallback = isFallback;
                Completed = true;
            }
        }

        private sealed class ReferenceComparer : IEqualityComparer<LuaTable>
        {
            public static readonly ReferenceComparer Instance = new();

            public bool Equals(LuaTable x, LuaTable y)
            {
                return ReferenceEquals(x, y);
            }

            public int GetHashCode(LuaTable obj)
            {
                return System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
            }
        }
    }
}
