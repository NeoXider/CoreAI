using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Ai;
using CoreAI.Authority;
using CoreAI.Mods.Rbx.Instances;
using CoreAI.Mods.Rbx.Instances.Scheduling;
using CoreAI.Mods.WorldPackages;
using CoreAI.Sandbox.LuaCs;
using CoreAI.Scripting;
using CoreAI.Scripting.LuaCs;
using Cysharp.Threading.Tasks;
using Microsoft.Extensions.AI;
using Newtonsoft.Json;

namespace CoreAI.Mods.WorldPackages
{
    /// <summary>Serializes capture, confirmed autosave, and the mutation protected by that autosave.</summary>
    public interface IConfirmedWorldMutationGate
    {
        /// <summary>Runs a mutation only after its trigger-labelled snapshot is durably confirmed.</summary>
        Task<TResult> ExecuteAsync<TResult>(
            string trigger,
            Func<CancellationToken, Task<TResult>> mutationAsync,
            CancellationToken cancellationToken);
    }

    /// <summary>Requires a durable world-package autosave before allowing a runtime mutation.</summary>
    public sealed class ConfirmedWorldMutationGate : IConfirmedWorldMutationGate
    {
        private readonly Func<CancellationToken, UniTask<RbxWorldPackagePayload>> _captureCurrentAsync;
        private readonly IRbxWorldPackageStore _packageStore;
        private readonly SemaphoreSlim _singleFlight = new(1, 1);

        /// <summary>Creates a shared gate over the host capture port and durable package store.</summary>
        public ConfirmedWorldMutationGate(
            Func<CancellationToken, UniTask<RbxWorldPackagePayload>> captureCurrentAsync,
            IRbxWorldPackageStore packageStore)
        {
            _captureCurrentAsync = captureCurrentAsync
                                   ?? throw new ArgumentNullException(nameof(captureCurrentAsync));
            _packageStore = packageStore ?? throw new ArgumentNullException(nameof(packageStore));
        }

        /// <inheritdoc />
        public async Task<TResult> ExecuteAsync<TResult>(
            string trigger,
            Func<CancellationToken, Task<TResult>> mutationAsync,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(trigger))
            {
                throw new ArgumentException("A deterministic backup trigger is required.", nameof(trigger));
            }

            if (mutationAsync == null)
            {
                throw new ArgumentNullException(nameof(mutationAsync));
            }

            await _singleFlight.WaitAsync(cancellationToken);
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                RbxWorldPackagePayload payload = await _captureCurrentAsync(cancellationToken);
                if (payload == null)
                {
                    throw new InvalidOperationException(
                        "Confirmed pre-mutation backup capture returned no world payload.");
                }

                RbxWorldPackageWriteResult backup = await _packageStore.CreateAutoAsync(
                    trigger,
                    payload,
                    cancellationToken);
                if (backup == null || !backup.Success)
                {
                    string reason = backup == null || string.IsNullOrWhiteSpace(backup.Error)
                        ? "the package store did not confirm durability"
                        : backup.Error;
                    throw new InvalidOperationException(
                        "Confirmed pre-mutation backup '" + trigger + "' failed: " + reason);
                }

                cancellationToken.ThrowIfCancellationRequested();
                return await mutationAsync(cancellationToken);
            }
            finally
            {
                _singleFlight.Release();
            }
        }
    }
}

namespace CoreAI.Ai.LuaCs
{
    /// <summary>
    /// Gameplay bindings seam for the Lua-CSharp one-shot/envelope path. This is the ADDITIVE
    /// Lua-CSharp counterpart of <c>CoreAI.Ai.IGameLuaRuntimeBindings</c> (which is coupled to
    /// the MoonSharp <see cref="CoreAI.Sandbox.LuaApiRegistry"/> and therefore cannot be reused for the
    /// managed VM). Implementations register the game's callable APIs on a
    /// <see cref="LuaCsApiRegistry"/>. Both <c>LuaCsWorldRuntimeBindings</c> and
    /// <c>LuaCsFullUnityRuntimeBindings</c> already expose this exact method shape, so a host can adapt
    /// them into this interface for a later type-swap away from the MoonSharp executor.
    /// </summary>
    public interface ILuaCsGameRuntimeBindings
    {
        /// <summary>Registers gameplay-facing Lua-CSharp APIs in the provided registry.</summary>
        void RegisterGameplayApis(LuaCsApiRegistry registry);
    }

    internal interface IActorScopedLuaCsGameRuntimeBindings
    {
        InstanceRegistry MutationRegistry { get; }

        void RegisterGameplayApis(LuaCsApiRegistry registry,
            ActorContext actorContext);

        void RegisterGameplayApis(LuaCsApiRegistry registry, ActorContext actorContext,
            MutationEnvelope mutationEnvelope);
    }

    /// <summary>Registers the default Lua-CSharp runtime APIs (mirrors <c>CoreDefaultLuaRuntimeBindings</c>).</summary>
    public sealed class CoreDefaultLuaCsRuntimeBindings : ILuaCsGameRuntimeBindings
    {
        /// <inheritdoc />
        public void RegisterGameplayApis(LuaCsApiRegistry registry)
        {
            registry.Register("report", new Action<string>(_ => { }));
            registry.Register("add", new Func<double, double, double>((a, b) => a + b));
        }
    }

    /// <summary>
    /// Lua-CSharp (nuskey8/Lua-CSharp) counterpart of
    /// <see cref="CoreAI.Infrastructure.Lua.GameLuaToolExecutor"/>. Implements the SAME
    /// <see cref="LuaTool.ILuaExecutor"/> seam so the native <c>execute_lua</c> tool can later be
    /// re-pointed from the MoonSharp executor to this managed one by type. Each chunk runs in a fresh
    /// sandboxed <see cref="LuaState"/> under the <see cref="LuaCsExecutionGuard"/>, mirroring the
    /// envelope pipeline in <see cref="LuaCsAiEnvelopeProcessor"/> (same sandbox limits, same observer
    /// notifications, same result/error caps). State persists through the C#-side APIs the bindings
    /// expose (logic slots, mods, world commands), not through Lua globals.
    /// </summary>
    public sealed class LuaCsGameToolExecutor : LuaTool.ILuaExecutor, LuaTool.IMutationExecutor
    {
        /// <summary>Stable autosave trigger for one-shot Lua execution.</summary>
        public const string ExecuteLuaBackupTrigger = "execute_lua";

        private readonly IScriptEngine _engine;
        private readonly ILuaCsGameRuntimeBindings _bindings;
        private readonly ILuaExecutionObserver _observer;
        private readonly IConfirmedWorldMutationGate _worldMutationGate;

        /// <summary>
        /// Trusted host/local actor for the plain <see cref="ExecuteAsync(string, CancellationToken)"/>
        /// seam. Production composition supplies it; a null resolver keeps the ACL-world refusal.
        /// </summary>
        public Func<ActorContext> LocalActorResolver { get; set; }

        /// <summary>
        /// Raised after <c>execute_lua</c> successfully runs a chunk. Mirrors
        /// <see cref="CoreAI.Infrastructure.Lua.GameLuaToolExecutor.LuaExecutedSuccessfully"/> so scene
        /// demos can persist their own game-specific Lua changes without the generic executor owning
        /// scene policy.
        /// </summary>
        public static event Action<string> LuaExecutedSuccessfully;

        /// <summary>
        /// True when the Lua-CSharp sandbox is available. Lua-CSharp is a managed, AOT-safe VM (the
        /// reason for this migration), so unlike <c>SecureLuaEnvironment</c> this is always supported
        /// — including IL2CPP/WebGL.
        /// </summary>
        public static bool IsSupported => true;

        /// <summary>
        /// Creates the executor without an observability sink.
        /// </summary>
        // WHY: an OPTIONAL sink parameter would force every calling assembly to reference the
        // WHY: engine-free scheduling assembly just to name the type it never passes, so the sink-free
        // WHY: overload keeps that dependency off consumers such as the demos.
        public LuaCsGameToolExecutor(
            LuaCsSecureEnvironment sandbox,
            ILuaCsGameRuntimeBindings bindings,
            ILuaExecutionObserver observer)
            : this(sandbox, bindings, observer, null, null)
        {
        }

        public LuaCsGameToolExecutor(
            LuaCsSecureEnvironment sandbox,
            ILuaCsGameRuntimeBindings bindings,
            ILuaExecutionObserver observer,
            IRbxRuntimeObservabilitySink observability)
            : this(sandbox, bindings, observer, observability, null)
        {
        }

        public LuaCsGameToolExecutor(
            LuaCsSecureEnvironment sandbox,
            ILuaCsGameRuntimeBindings bindings,
            ILuaExecutionObserver observer,
            IRbxRuntimeObservabilitySink observability,
            IConfirmedWorldMutationGate worldMutationGate)
        {
            if (sandbox == null)
            {
                throw new ArgumentNullException(nameof(sandbox));
            }

            _engine = new LuaCsScriptEngine(sandbox, observability);
            _bindings = bindings ?? throw new ArgumentNullException(nameof(bindings));
            _observer = observer ?? throw new ArgumentNullException(nameof(observer));
            _worldMutationGate = worldMutationGate;
        }

        /// <inheritdoc />
        public async Task<LuaTool.LuaResult> ExecuteAsync(
            string code,
            CancellationToken cancellationToken)
        {
            if (_bindings is IActorScopedLuaCsGameRuntimeBindings scopedCheck
                && scopedCheck.MutationRegistry != null
                && scopedCheck.MutationRegistry.IsWorldAclEnabled)
            {
                ActorContext localActor = LocalActorResolver != null
                    ? LocalActorResolver()
                    : default;
                if (!localActor.IsTrusted)
                {
                    return CreateFailure(
                        "world mutation requires an actor-scoped envelope in this ACL-versioned world: " +
                        "no trusted local actor resolver is configured for the plain execute seam");
                }

                return await ExecuteAsync(code, localActor, cancellationToken);
            }

            try
            {
                return await ExecuteWithBackupAsync(
                    token => Task.FromResult(ExecuteCore(
                        code,
                        token,
                        _bindings.RegisterGameplayApis)),
                    cancellationToken);
            }
            catch (Exception ex)
            {
                return CreateFailure(ex.Message);
            }
        }

        /// <summary>Server-generated envelope execution used by production Lua tools.</summary>
        public async Task<LuaTool.LuaResult> ExecuteAsync(string code, ActorContext actorContext,
            CancellationToken cancellationToken)
        {
            if (!actorContext.IsTrusted)
            {
                return CreateFailure(
                    "actor '<untrusted>' cannot execute Lua: actor context was not issued by an identity provider");
            }

            if (!(_bindings is IActorScopedLuaCsGameRuntimeBindings scopedBindings)
                || scopedBindings.MutationRegistry == null)
            {
                return CreateFailure(
                    "actor '" + actorContext.ActorId + "' cannot execute Lua: the production Rbx mutation surface is not configured");
            }

            try
            {
                return await ExecuteWithBackupAsync(
                    token => Task.FromResult(
                        scopedBindings.MutationRegistry.ApplyServerGeneratedMutation(
                            actorContext.ActorId,
                            actorContext.Grants.IsUnrestricted,
                            actorContext.WorldId,
                            "execute_lua",
                            () => ExecuteCore(code, token,
                                registry => scopedBindings.RegisterGameplayApis(
                                    registry, actorContext)))),
                    cancellationToken);
            }
            catch (Exception ex)
            {
                return CreateFailure(ex.Message);
            }
        }

        /// <summary>
        /// Executes one actor-scoped world mutation under optimistic revision and idempotency checks.
        /// Kept as an overload so existing executor consumers do not need to reference the envelope
        /// assembly unless they opt into the mutation protocol.
        /// </summary>
        public async Task<LuaTool.LuaResult> ExecuteAsync(string code, ActorContext actorContext,
            MutationEnvelope mutationEnvelope, CancellationToken cancellationToken)
        {
            if (!actorContext.IsTrusted)
            {
                return CreateFailure(
                    "actor '<untrusted>' cannot apply operation '"
                    + mutationEnvelope.OperationId
                    + "': actor context was not issued by an identity provider");
            }

            if (!string.Equals(actorContext.ActorId, mutationEnvelope.ActorId,
                    StringComparison.Ordinal))
            {
                return CreateFailure(
                    "actor '" + actorContext.ActorId + "' cannot apply operation '"
                    + mutationEnvelope.OperationId + "': the mutation envelope belongs to actor '"
                    + mutationEnvelope.ActorId + "'");
            }

            if (!(_bindings is IActorScopedLuaCsGameRuntimeBindings scopedBindings)
                || scopedBindings.MutationRegistry == null)
            {
                return CreateFailure(
                    "actor '" + actorContext.ActorId + "' cannot apply operation '"
                    + mutationEnvelope.OperationId
                    + "': the production Rbx mutation surface is not configured");
            }

            try
            {
                return await ExecuteWithBackupAsync(
                    token => Task.FromResult(
                        scopedBindings.MutationRegistry.ApplyMutation(
                            mutationEnvelope,
                            () =>
                            {
                                using (scopedBindings.MutationRegistry
                                           .BeginMutationEnvelopeScope(
                                               mutationEnvelope,
                                               actorContext.Grants.IsUnrestricted,
                                               actorContext.WorldId))
                                {
                                    return ExecuteCore(code, token,
                                        registry => scopedBindings.RegisterGameplayApis(
                                            registry, actorContext,
                                            mutationEnvelope));
                                }
                            })),
                    cancellationToken);
            }
            catch (Exception ex)
            {
                return CreateFailure(ex.Message);
            }
        }

        private Task<LuaTool.LuaResult> ExecuteWithBackupAsync(
            Func<CancellationToken, Task<LuaTool.LuaResult>> executeAsync,
            CancellationToken cancellationToken)
        {
            if (_worldMutationGate == null)
            {
                return executeAsync(cancellationToken);
            }

            return _worldMutationGate.ExecuteAsync(
                ExecuteLuaBackupTrigger,
                executeAsync,
                cancellationToken);
        }

        private LuaTool.LuaResult ExecuteCore(string code, CancellationToken cancellationToken,
            Action<LuaCsApiRegistry> registerGameplayApis)
        {
            if (!IsSupported)
            {
                return new LuaTool.LuaResult
                {
                    Success = false,
                    Error = "CoreAI Lua execution is disabled on this platform."
                };
            }

            // WHY: The world bindings are a shared singleton: a prior chunk that died between
            // WHY: coreai_world_begin() and commit/rollback leaves its transaction open, which would
            // WHY: silently buffer this chunk's world commands. Reset before running and abort in the
            // WHY: finally so a leaked transaction can never bleed across runs in either direction.
            (_bindings as ILuaTransactionScope)?.ResetTransactions();
            try
            {
                LuaCsApiRegistry registry = new();
                registerGameplayApis(registry);
                IScriptState state = _engine.CreateState();
                registry.ApplyTo(state);

                // WHY: Downlevel Luau -> Lua 5.2 before compiling so one-shot scripts accept the same
                // WHY: Luau syntax mods do; a downlevel Error is thrown here and caught below as the exec
                // WHY: failure (never a silent raw fallback). Chunk name is stable so diagnostics are legible.
                string compileCode = LuauSourceGate.ToLua52(code, "execute_lua");
                object[] results = _engine.RunChunk(state, compileCode, cancellationToken: cancellationToken);
                string summary = Truncate(Summarize(results), LuaCsAiEnvelopeProcessor.MaxResultSummaryLength);
                _observer.OnLuaSuccess(summary);
                LuaExecutedSuccessfully?.Invoke(code ?? "");
                return new LuaTool.LuaResult { Success = true, Output = summary };
            }
            catch (Exception ex)
            {
                return CreateFailure(ex.Message);
            }
            finally
            {
                (_bindings as ILuaTransactionScope)?.ResetTransactions();
            }
        }

        private LuaTool.LuaResult CreateFailure(string message)
        {
            string flat = Truncate(
                (message ?? "").Replace("\r", " ").Replace("\n", " ").Trim(),
                LuaCsAiEnvelopeProcessor.MaxErrorMessageLength);
            _observer.OnLuaFailure(flat);
            return new LuaTool.LuaResult { Success = false, Error = flat };
        }

        /// <summary>Renders the chunk's first return value into a printable summary (VM-agnostic).</summary>
        internal static string Summarize(object[] results)
        {
            if (results == null || results.Length == 0)
            {
                return "nil";
            }

            return Stringify(results[0]);
        }

        /// <summary>Max table nesting rendered into the Output summary before values are elided.</summary>
        private const int MaxTableSummaryDepth = 4;

        private static string Stringify(object value)
        {
            IValueMarshaller marshaller = LuaCsValueMarshaller.Instance;
            switch (marshaller.GetKind(value))
            {
                case ScriptValueKind.Nil:
                    return "nil";
                case ScriptValueKind.Boolean:
                    return (bool)marshaller.ToHostValue(value) ? "true" : "false";
                case ScriptValueKind.Number:
                    return ((double)marshaller.ToHostValue(value)).ToString(CultureInfo.InvariantCulture);
                case ScriptValueKind.String:
                    return (string)marshaller.ToHostValue(value) ?? "";
                case ScriptValueKind.Table:
                    return StringifyTable(marshaller, value);
                default:
                    return marshaller.Describe(value);
            }
        }

        // WHY: a returned table otherwise renders as "table: 0x…" (its address), which is useless to the
        // WHY: model — e.g. `return coreai_world_list_prefabs()` or `coreai_world_find(...)` would hand the LLM
        // WHY: an opaque handle instead of the actual names. Convert it to a portable structure and JSON-encode
        // WHY: it so discovery tools are legible; fall back to the plain describe if conversion/encoding fails.
        private static string StringifyTable(IValueMarshaller marshaller, object value)
        {
            try
            {
                object portable = marshaller.ToPortable(value, MaxTableSummaryDepth);
                return Newtonsoft.Json.JsonConvert.SerializeObject(portable);
            }
            catch (Exception)
            {
                return marshaller.Describe(value);
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
    }
}

namespace CoreAI.Mods.WorldPackages
{
    /// <summary>AI tool that lists autosave packages with metadata.</summary>
    public sealed class ListAutoSavesLlmTool : LlmToolBase, IAIFunctionLlmTool
    {
        private readonly IRbxWorldRuntimeService _service;
        private readonly IActorIdentityProvider _identityProvider;
        private readonly string _roleId;

        public ListAutoSavesLlmTool(
            IRbxWorldRuntimeService service,
            IActorIdentityProvider identityProvider,
            string roleId)
        {
            _service = service ?? throw new ArgumentNullException(nameof(service));
            _identityProvider = identityProvider ?? throw new ArgumentNullException(nameof(identityProvider));
            _roleId = roleId ?? BuiltInAgentRoleIds.Programmer;
        }

        public override string Name => "list_autosaves";

        public override string Description =>
            "List timestamped, trigger-labelled autosave world packages with name, trigger, timestamp and size.";

        public override string ParametersSchema => JsonParams();

        public AIFunction CreateAIFunction()
        {
            Func<CancellationToken, Task<string>> function = ExecuteAsync;
            return AIFunctionFactory.Create(function, new AIFunctionFactoryOptions
            {
                Name = Name,
                Description = Description
            });
        }

        public Task<string> ExecuteAsync(CancellationToken cancellationToken = default)
        {
            _identityProvider.GetActorContext(_roleId);
            IReadOnlyList<RbxAutoSaveInfo> autosaves = _service.ListAutoSaves();
            return Task.FromResult(Newtonsoft.Json.JsonConvert.SerializeObject(new
            {
                autosaves = System.Linq.Enumerable.Select(autosaves, info => new
                {
                    name = info.FileName,
                    trigger = info.Trigger,
                    timestamp = info.TimestampUtc.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
                    size = info.SizeBytes
                })
            }));
        }
    }

    /// <summary>AI tool that requests a confirmed load of a named autosave.</summary>
    public sealed class LoadAutoSaveLlmTool : LlmToolBase, IAIFunctionLlmTool
    {
        private readonly IRbxWorldRuntimeService _service;
        private readonly IActorIdentityProvider _identityProvider;
        private readonly string _roleId;

        public LoadAutoSaveLlmTool(
            IRbxWorldRuntimeService service,
            IActorIdentityProvider identityProvider,
            string roleId)
        {
            _service = service ?? throw new ArgumentNullException(nameof(service));
            _identityProvider = identityProvider ?? throw new ArgumentNullException(nameof(identityProvider));
            _roleId = roleId ?? BuiltInAgentRoleIds.Programmer;
        }

        public override string Name => "load_autosave";

        public override string Description =>
            "Request loading a named autosave package. This never applies directly; the player must confirm the returned request.";

        public override string ParametersSchema => JsonParams(
            ("name", "string", true, "Autosave file name (e.g. 20260902T120000000Z-0000-execute_lua.world)."));

        public AIFunction CreateAIFunction()
        {
            Func<string, CancellationToken, Task<string>> function = ExecuteAsync;
            return AIFunctionFactory.Create(function, new AIFunctionFactoryOptions
            {
                Name = Name,
                Description = Description
            });
        }

        public async Task<string> ExecuteAsync(
            [System.ComponentModel.Description("Autosave file name.")] string name,
            CancellationToken cancellationToken = default)
        {
            CoreAI.Authority.ActorContext actor = _identityProvider.GetActorContext(_roleId);
            RbxWorldLoadRequest request = await _service.RequestAutoLoadAsync(actor, name, cancellationToken);
            return Newtonsoft.Json.JsonConvert.SerializeObject(new
            {
                success = false,
                status = "player_confirmation_required",
                player_confirmation_required = request.PlayerConfirmationRequired,
                request_id = request.RequestId,
                slot = request.Slot,
                world_id = request.WorldId
            });
        }
    }
}
