using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using CoreAI.Ai.Logging;
using CoreAI.Mods.Rbx.Instances;
using CoreAI.Mods.Rbx.Instances.Scheduling;
using CoreAI.Mods.Rbx.Instances.Scripts;
using CoreAI.Sandbox.LuaCs;
using CoreAI.Scripting;
using CoreAI.Scripting.LuaCs;
using Lua;
using Lua.Runtime;
using static CoreAI.Ai.LuaCs.LuaCsRbxLua;

namespace CoreAI.Ai.LuaCs
{
    /// <summary>
    /// <c>require</c> (MVP4 step C4; decisions SC-10, SC-11, SC-12): the host side of the Lua bridge that
    /// <see cref="LuaCsRbxScriptThreadFactory.PrepareRequireBindings"/> installs on every registered state. A module runs
    /// once per requiring script's environment (its Lua state), inline on the thread that first requires it, and every
    /// later <c>require</c> there gets the same value; another script loads its own copy.
    /// </summary>
    public sealed partial class LuaCsRbxApiBindings
    {
        private const int RequireStepValue = 1;
        private const int RequireStepLoad = 2;
        private const int RequireStepWait = 3;

        /// <summary>One require in flight: the module a token names until the bridge settles or re-asks for it.</summary>
        private sealed class PendingRequire
        {
            public PendingRequire(RbxLuaSourceContainer module, string moduleId, string path)
            {
                Module = module;
                ModuleId = moduleId;
                Path = path;
            }

            public RbxLuaSourceContainer Module { get; }

            public string ModuleId { get; }

            public string Path { get; }
        }

        /// <summary>
        /// What one requiring environment (one registered context, so one Lua state) knows about its modules: the
        /// require graph, the tokens the bridge holds, the signals its waiting threads wait on, and the ids of every
        /// module it loaded, which SC-18 restarts it for.
        /// </summary>
        private sealed class RequireEnvironment
        {
            private readonly Dictionary<int, PendingRequire> _pending = new();
            private int _nextToken;

            public RequireEnvironment(LuaCsRbxModContext context)
            {
                Context = context;
            }

            public LuaCsRbxModContext Context { get; }

            public ModuleRequireGraph Graph { get; } = new();

            public HashSet<string> ModuleIds { get; } = new(StringComparer.Ordinal);

            public Dictionary<InstanceId, RbxScriptSignal> LoadSignals { get; } = new();

            public int Hold(PendingRequire pending)
            {
                _nextToken++;
                _pending[_nextToken] = pending;
                return _nextToken;
            }

            public PendingRequire Take(LuaValue token)
            {
                if (token.TryRead(out double number) && _pending.Remove((int)number, out PendingRequire pending))
                {
                    return pending;
                }

                throw RbxError.BadArgument("require was resumed with a token it never gave out",
                    "call require(module) instead of the bridge's own functions");
            }
        }

        /// <summary>The first script that loaded a module and the side it runs on, for the SC-11 note.</summary>
        private readonly struct ModuleFirstRequirer
        {
            public ModuleFirstRequirer(string requirerId, bool serverSide)
            {
                RequirerId = requirerId;
                ServerSide = serverSide;
            }

            public string RequirerId { get; }

            public bool ServerSide { get; }
        }

        // WHY weak tables: an environment lives exactly as long as its context and its Lua state, so a reload, an
        // unload or the end of a console chunk needs no teardown call to drop its modules.
        private readonly ConditionalWeakTable<LuaCsRbxModContext, RequireEnvironment> _requireEnvironments = new();
        private readonly ConditionalWeakTable<LuaState, RequireEnvironment> _requireEnvironmentsByState = new();
        private readonly Dictionary<IRbxScriptThread, RequireEnvironment> _requireThreads = new();
        private readonly Dictionary<string, ModuleFirstRequirer> _moduleFirstRequirers = new(StringComparer.Ordinal);
        private readonly HashSet<string> _modulesNotedAsCopied = new(StringComparer.Ordinal);

        /// <summary>
        /// True when the script whose state is <paramref name="state"/> loaded (or tried to load) the module source
        /// <paramref name="moduleId"/>, directly or through another module: SC-18 restarts it when that module's source
        /// changes.
        /// </summary>
        internal bool HasRequiredModule(IScriptState state, string moduleId)
        {
            if (state == null || string.IsNullOrEmpty(moduleId))
            {
                return false;
            }

            LuaState luaState = LuaCsScriptState.Unwrap(state);
            return luaState != null && _requireEnvironmentsByState.TryGetValue(luaState, out RequireEnvironment environment)
                                    && environment.ModuleIds.Contains(moduleId);
        }

        private RequireEnvironment RequireEnvironmentOf(LuaCsRbxModContext context)
        {
            return _requireEnvironments.GetValue(context, created => new RequireEnvironment(created));
        }

        /// <summary>Installs <c>require</c> on every state this registration is applied to.</summary>
        private void RegisterRequire(LuaCsApiRegistry luaRegistry, LuaCsRbxModContext context)
        {
            luaRegistry.RegisterEnvironmentDecorator(state =>
            {
                _requireEnvironmentsByState.AddOrUpdate(state, RequireEnvironmentOf(context));
                _schedulerThreadFactory.PrepareRequireBindings(new LuaCsScriptState(state));
            });
        }

        /// <summary>The host primitives the require bridge captures from <c>task</c> and erases.</summary>
        private void AddRequirePrimitives(LuaTable task, LuaCsRbxModContext context)
        {
            task["_requireBegin"] = FnMulti("require", ctx => BeginRequire(context, ctx), context);
            task["_requireSettle"] = FnMulti("require", ctx => SettleRequire(context, ctx), context);
            task["_requireAfterWait"] = FnMulti("require", ctx => ResumeRequire(context, ctx), context);
        }

        private LuaValue[] BeginRequire(LuaCsRbxModContext context, LuaFunctionExecutionContext ctx)
        {
            RbxInstance anchor = TryGetInstance(Arg(ctx, 1), out LuaCsRbxInstanceProxy anchorProxy)
                ? anchorProxy.Instance
                : string.IsNullOrWhiteSpace(context.OwnerModId)
                    ? null
                    : ResolveExecutingScript(context);
            RbxInstance target = ResolveRequireTarget(ctx, anchor);
            string path = target.GetFullName();
            if (!(target is RbxLuaSourceContainer module)
                || !string.Equals(target.ClassName, ScriptClassNames.ModuleScript, StringComparison.Ordinal))
            {
                throw ModuleRequirePolicy.NotAModuleScriptError(path, target.ClassName);
            }

            if (!module.IsBound)
            {
                throw ModuleRequirePolicy.NoSourceError(path);
            }

            ILuaCsScriptSourceHost host = ScriptSourceHost;
            if (host == null || !host.TryGetRequirableModule(module.SourceId, out LuaCsRequirableModule source))
            {
                throw ModuleRequirePolicy.UnloadedError(path);
            }

            RbxError trust = ModuleRequirePolicy.CheckTrust(context.IsHost ? "" : context.ActorContext.ActorId,
                source.OwnerHasHostAuthority ? "" : source.OwnerActorId, source.ModuleId);
            if (trust != null)
            {
                throw trust;
            }

            DescribeScriptLocation(module, out ScriptContainer container, out ScriptPlayerScope _);
            RbxError location = ModuleRequirePolicy.CheckLocation(context.Context, container, path);
            if (location != null)
            {
                throw location;
            }

            return StepRequire(context, ctx, new PendingRequire(module, source.ModuleId, path), source);
        }

        /// <summary>
        /// The ModuleScript a <c>require</c> argument names (R3.5): an instance, or a string path resolved from the
        /// calling chunk's <c>script</c> (<paramref name="anchor"/>) without waiting (R3.6). An asset id is refused.
        /// </summary>
        private RbxInstance ResolveRequireTarget(LuaFunctionExecutionContext ctx, RbxInstance anchor)
        {
            LuaValue argument = Arg(ctx, 0);
            if (TryGetInstance(argument, out LuaCsRbxInstanceProxy proxy))
            {
                return proxy.Instance;
            }

            switch (argument.Type)
            {
                case LuaValueType.Number:
                    throw ModuleRequirePolicy.AssetIdError();
                case LuaValueType.String:
                    if (!RequirePath.TryParse(argument.Read<string>(), out RequirePath parsed, out string parseError))
                    {
                        throw ModuleRequirePolicy.PathError(parseError);
                    }

                    RequirePathResolution<RbxInstance> resolution = parsed.Resolve<RbxInstance>(anchor, _game,
                        node => node.Parent, (node, name) => node.FindFirstChild(name));
                    if (!resolution.Found)
                    {
                        throw ModuleRequirePolicy.PathError(resolution.Error);
                    }

                    return resolution.Node;
                default:
                    throw ModuleRequirePolicy.ArgumentTypeError(RobloxTypeOf(ctx.State, argument));
            }
        }

        /// <summary>
        /// Asks the require graph what this request does next (SC-11, SC-12) and answers the bridge: the cached value,
        /// the module's chunk to run, or a wait for the thread that is loading it. A refusal is raised.
        /// </summary>
        private LuaValue[] StepRequire(LuaCsRbxModContext context, LuaFunctionExecutionContext ctx,
            PendingRequire pending, LuaCsRequirableModule source)
        {
            RequireEnvironment environment = RequireEnvironmentOf(context);
            object thread = RequireThreadIdentity(ctx);
            RequireStep step = environment.Graph.Begin(environment, pending.Module.Id, pending.Path, thread);
            switch (step.Kind)
            {
                case RequireStepKind.Cached:
                    return new LuaValue[] { RequireStepValue, (LuaValue)step.Value };
                case RequireStepKind.Failed:
                    throw ModuleRequirePolicy.PreviouslyFailedError(pending.Path, step.FirstError);
                case RequireStepKind.Cycle:
                    throw ModuleRequirePolicy.CycleError(step.CyclePath);
                case RequireStepKind.Wait:
                    return WaitForLoadingModule(environment, context, ctx, pending);
            }

            environment.ModuleIds.Add(pending.ModuleId);
            if (thread is IRbxScriptThread schedulerThread)
            {
                _requireThreads[schedulerThread] = environment;
            }

            LuaClosure chunk;
            LuaTable moduleEnvironment = new();
            try
            {
                moduleEnvironment["script"] = context.WrapInstance(pending.Module);
                LuaTable lookup = new();
                lookup[Metamethods.Index] = new LuaValue(ctx.State.Environment);
                moduleEnvironment.Metatable = lookup;
                chunk = ctx.State.Load(source.CompileSource, ModChunkName(pending.ModuleId), moduleEnvironment);
            }
            catch (Exception ex)
            {
                RbxError compileError = RbxError.BadArgument(
                    "module '" + pending.Path + "' does not compile: " + ex.Message,
                    "fix the module's syntax; it compiles again when its source changes");
                FailModule(environment, pending, compileError.Message);
                throw compileError;
            }

            NoteSecondEnvironment(context, pending);
            int token = environment.Hold(pending);
            return new[]
            {
                new LuaValue(RequireStepLoad), new LuaValue(token), new LuaValue(chunk),
                new LuaValue(moduleEnvironment), context.WrapInstance(pending.Module)
            };
        }

        /// <summary>
        /// The bridge's report of a module body that ended: <c>(token, ok, ...)</c> as <c>pcall</c> returned. A body
        /// that returned exactly one non-nil value completes the module (R3.1); any other end fails it for this
        /// environment (R3.8), and its error is logged once in the module's log. Returns <c>(true, value)</c> or
        /// <c>(false, error)</c>, which the bridge raises at the call site.
        /// </summary>
        private LuaValue[] SettleRequire(LuaCsRbxModContext context, LuaFunctionExecutionContext ctx)
        {
            RequireEnvironment environment = RequireEnvironmentOf(context);
            PendingRequire pending = environment.Take(Arg(ctx, 0));
            bool ok = Arg(ctx, 1).ToBoolean();
            int count = Math.Max(0, ctx.ArgumentCount - 2);
            if (!ok)
            {
                LuaValue error = Arg(ctx, 2);
                string text = error.Type == LuaValueType.String ? error.Read<string>() : LuaCsRbxLua.Describe(error);
                FailModule(environment, pending, text);
                LogModuleFailure(context, pending, text);
                return new[] { new LuaValue(false), error };
            }

            RbxError refusal = count != 1
                ? ModuleRequirePolicy.ReturnCountError(pending.Path, count)
                : Arg(ctx, 2).Type == LuaValueType.Nil
                    ? ModuleRequirePolicy.ReturnedNilError(pending.Path)
                    : null;
            if (refusal != null)
            {
                FailModule(environment, pending, refusal.Message);
                LogModuleFailure(context, pending, refusal.Message);
                throw refusal;
            }

            LuaValue value = Arg(ctx, 2);
            if (environment.Graph.TryGetStatus(environment, pending.Module.Id, out ModuleLoadStatus status)
                && status == ModuleLoadStatus.Loading)
            {
                environment.Graph.Complete(environment, pending.Module.Id, value);
            }

            WakeWaiters(environment, pending.Module.Id);
            return new[] { new LuaValue(true), value };
        }

        /// <summary>A thread that waited for another thread's load asks again once that load ended.</summary>
        private LuaValue[] ResumeRequire(LuaCsRbxModContext context, LuaFunctionExecutionContext ctx)
        {
            RequireEnvironment environment = RequireEnvironmentOf(context);
            PendingRequire pending = environment.Take(Arg(ctx, 0));
            ILuaCsScriptSourceHost host = ScriptSourceHost;
            if (host == null || !host.TryGetRequirableModule(pending.ModuleId, out LuaCsRequirableModule source))
            {
                throw ModuleRequirePolicy.UnloadedError(pending.Path);
            }

            return StepRequire(context, ctx, pending, source);
        }

        /// <summary>
        /// Suspends the calling thread until the thread loading the module ends its load (SC-12): a wait on a signal
        /// the load fires, like <c>signal:Wait</c>. Code that cannot wait on the scheduler (the console, a
        /// <c>coroutine.create</c> coroutine) gets CONTEXT_VIOLATION, as every other wait there does.
        /// </summary>
        private LuaValue[] WaitForLoadingModule(RequireEnvironment environment, LuaCsRbxModContext context,
            LuaFunctionExecutionContext ctx, PendingRequire pending)
        {
            if (string.IsNullOrWhiteSpace(context.OwnerModId)
                || !(_schedulerThreadFactory.CurrentThread is LuaCsRbxScriptThread caller)
                || !string.Equals(caller.OwnerModId, context.OwnerModId, StringComparison.Ordinal))
            {
                throw new RbxError(RbxErrorCode.ContextViolation,
                    "require of module '" + pending.Path + "' would wait for the thread that is loading it, and this "
                    + "code cannot wait on the scheduler",
                    "require the module from the mod's chunk or a task.spawn function");
            }

            int token = environment.Hold(pending);
            if (!CanSuspendCaller(ctx, caller, "require"))
            {
                return new LuaValue[] { RequireStepWait, token };
            }

            if (!environment.LoadSignals.TryGetValue(pending.Module.Id, out RbxScriptSignal signal))
            {
                signal = new RbxScriptSignal("ModuleScript.Loaded");
                environment.LoadSignals[pending.Module.Id] = signal;
            }

            signal.BindScheduler(_scheduler);
            _scheduler.ScheduleSignalWait(caller);
            RbxScriptConnection connection = signal.Wait(_ =>
                _scheduler.ResumeSignalWait(caller, Array.Empty<object>()));
            context.TrackConnection(connection);
            caller.PendingWaitConnection = connection;
            caller.NoteScheduledYield();
            _requireThreads[caller] = environment;
            return new LuaValue[] { RequireStepWait, token };
        }

        /// <summary>
        /// The thread a require runs on, for cycle detection: the scheduler thread when its own coroutine runs, else
        /// the running Lua thread (a <c>coroutine.create</c> coroutine, or the console's chunk).
        /// </summary>
        private object RequireThreadIdentity(LuaFunctionExecutionContext ctx)
        {
            return _schedulerThreadFactory.CurrentThread is LuaCsRbxScriptThread thread && thread.IsOwnCoroutineRunning
                ? thread
                : ctx.State;
        }

        private void FailModule(RequireEnvironment environment, PendingRequire pending, string error)
        {
            if (environment.Graph.TryGetStatus(environment, pending.Module.Id, out ModuleLoadStatus status)
                && status == ModuleLoadStatus.Loading)
            {
                environment.Graph.Fail(environment, pending.Module.Id, error);
            }

            WakeWaiters(environment, pending.Module.Id);
        }

        private static void WakeWaiters(RequireEnvironment environment, InstanceId module)
        {
            if (environment.LoadSignals.Remove(module, out RbxScriptSignal signal))
            {
                signal.Fire();
            }
        }

        /// <summary>
        /// A scheduler thread that stopped while it loaded a module (a budget trip, <c>task.cancel</c>, its script
        /// stopping) fails that module for its environment, so a thread waiting for it is woken with the reason instead
        /// of waiting forever.
        /// </summary>
        private void ForgetRequireThread(IRbxScriptThread thread)
        {
            if (thread == null || !_requireThreads.Remove(thread, out RequireEnvironment environment))
            {
                return;
            }

            environment.Graph.ForgetThread(environment, thread, "the thread loading the module stopped");
            if (environment.LoadSignals.Count == 0)
            {
                return;
            }

            List<InstanceId> settled = new();
            foreach (InstanceId module in environment.LoadSignals.Keys)
            {
                if (!environment.Graph.TryGetStatus(environment, module, out ModuleLoadStatus status)
                    || status != ModuleLoadStatus.Loading)
                {
                    settled.Add(module);
                }
            }

            for (int index = 0; index < settled.Count; index++)
            {
                WakeWaiters(environment, settled[index]);
            }
        }

        /// <summary>
        /// R3.8: the module's own log records its failure once per environment, next to what the requiring script's
        /// log records when the error reaches it.
        /// </summary>
        private void LogModuleFailure(LuaCsRbxModContext context, PendingRequire pending, string error)
        {
            string requirer = string.IsNullOrWhiteSpace(context.OwnerModId) ? "the console" : "script '" + context.OwnerModId + "'";
            string message = "module failed while " + requirer + " required it: " + error;
            ILuaLogService modLog = _modLog;
            if (modLog == null)
            {
                _log?.Invoke("[RbxApi] mod '" + pending.ModuleId + "': " + message);
                return;
            }

            try
            {
                modLog.Append(new LuaLogEntry
                {
                    ModId = pending.ModuleId,
                    Level = LuaLogLevel.RuntimeError,
                    Message = message
                });
            }
            catch (Exception ex)
            {
                _log?.Invoke("[RbxApi] The mod log refused the failure of module '" + pending.ModuleId + "': " + ex.Message);
            }
        }

        /// <summary>
        /// SC-11 (DEV-126): the first time a module loads into a second script on the same side, one note says the two
        /// scripts do not share its table, where Roblox would hand both the same one.
        /// </summary>
        private void NoteSecondEnvironment(LuaCsRbxModContext context, PendingRequire pending)
        {
            string requirerId = context.OwnerModId;
            if (string.IsNullOrWhiteSpace(requirerId))
            {
                return;
            }

            bool serverSide = context.Context != ScriptContext.Client;
            if (!_moduleFirstRequirers.TryGetValue(pending.ModuleId, out ModuleFirstRequirer first))
            {
                _moduleFirstRequirers[pending.ModuleId] = new ModuleFirstRequirer(requirerId, serverSide);
                return;
            }

            if (string.Equals(first.RequirerId, requirerId, StringComparison.Ordinal) || first.ServerSide != serverSide
                || !_modulesNotedAsCopied.Add(pending.ModuleId))
            {
                return;
            }

            string note = "note: module '" + pending.Path + "' now runs separately in scripts '" + first.RequirerId
                          + "' and '" + requirerId + "'; its table is not shared between them (CoreAI keeps every "
                          + "script in its own environment) - share state through attributes or value objects, or move "
                          + "both scripts' logic into one script that requires the module";
            _log?.Invoke("[RbxApi] " + note);
            ILuaLogService modLog = _modLog;
            if (modLog == null)
            {
                return;
            }

            try
            {
                modLog.Append(new LuaLogEntry { ModId = pending.ModuleId, Level = LuaLogLevel.Warn, Message = note });
            }
            catch (Exception ex)
            {
                _log?.Invoke("[RbxApi] The mod log refused the note of module '" + pending.ModuleId + "': " + ex.Message);
            }
        }
    }
}
