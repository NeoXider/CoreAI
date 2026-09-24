using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using CoreAI.Authority;
using CoreAI.Composition;
using CoreAI.Mods.Rbx.Binding;
using CoreAI.Mods.Rbx.Datatypes;
using CoreAI.Mods.Rbx.Instances;
using RegistryWorldAclAuthorizer = CoreAI.Mods.Rbx.Instances.WorldAclAuthorizer;
using RegistryWorldAclDecision = CoreAI.Mods.Rbx.Instances.WorldAclDecision;
using CoreAI.Mods.Rbx.Instances.Networking;
using Lua;
using Lua.Runtime;
using static CoreAI.Ai.LuaCs.LuaCsRbxLua;

namespace CoreAI.Ai.LuaCs
{
    /// <summary>
    /// Per-registration mod context for the Roblox Lua surface: capability tier, ownership
    /// attribution (owner mod id + origin tag for the instance ledger), the proxy cache that keeps
    /// one Lua identity per <see cref="RbxInstance"/>, and per-mod once-only diagnostics flags.
    /// </summary>
    internal sealed class LuaCsRbxModContext
    {
        /// <summary>Detached-proxy entries tolerated before the first sweep of collected ones.</summary>
        private const int MinimumDetachedSweepThreshold = 64;

        private readonly MutationEnvelope? _mutationEnvelope;
        private readonly bool _serverGeneratesMutationEnvelopes;
        private readonly Dictionary<RbxInstance, LuaValue> _proxyCache = new();
        private readonly Dictionary<InstanceId, WeakReference<LuaCsRbxInstanceProxy>> _detachedProxies =
            new();
        private readonly LuaTable _instanceMeta;
        private int _detachedSweepThreshold = MinimumDetachedSweepThreshold;

        public LuaCsRbxModContext(LuaCsRbxApiBindings bindings, LuaCapabilities capabilities,
            string ownerModId, string originTag)
            : this(bindings, capabilities, ownerModId, originTag,
                ResolveLoadActorContext(bindings, ownerModId, originTag), null, false)
        {
        }

        public LuaCsRbxModContext(LuaCsRbxApiBindings bindings, LuaCapabilities capabilities,
            string ownerModId, string originTag, ActorContext actorContext)
            : this(bindings, capabilities, ownerModId, originTag, actorContext, null, true)
        {
        }

        public LuaCsRbxModContext(LuaCsRbxApiBindings bindings, LuaCapabilities capabilities,
            string ownerModId, string originTag, ActorContext actorContext,
            MutationEnvelope mutationEnvelope)
            : this(bindings, capabilities, ownerModId, originTag, actorContext,
                (MutationEnvelope?)mutationEnvelope, false)
        {
        }

        private LuaCsRbxModContext(LuaCsRbxApiBindings bindings, LuaCapabilities capabilities,
            string ownerModId, string originTag, ActorContext actorContext,
            MutationEnvelope? mutationEnvelope, bool serverGeneratesMutationEnvelopes)
        {
            if (!actorContext.IsTrusted)
            {
                throw new InvalidOperationException(
                    "Actor context was not issued by an identity provider.");
            }

            if (mutationEnvelope.HasValue
                && !string.Equals(mutationEnvelope.Value.ActorId, actorContext.ActorId,
                    StringComparison.Ordinal))
            {
                throw RbxError.BadArgument(
                    "actor '" + actorContext.ActorId + "' cannot apply operation '"
                    + mutationEnvelope.Value.OperationId + "': the mutation envelope belongs to actor '"
                    + mutationEnvelope.Value.ActorId + "'",
                    "create the envelope with the durable actor id from the trusted actor context");
            }

            Bindings = bindings;
            Capabilities = capabilities;
            OwnerModId = ownerModId;
            OriginTag = originTag;
            ActorContext = actorContext;
            _mutationEnvelope = mutationEnvelope;
            _serverGeneratesMutationEnvelopes = serverGeneratesMutationEnvelopes;
            if (!IsHost)
            {
                bindings.Registry.BindActorAttribution(ownerModId, originTag, actorContext.ActorId);
            }

            ModActorLedger.For(bindings).RecordLoad(ownerModId, IsHost ? null : actorContext.ActorId);

            // WHY: stamp this load's connection generation BEFORE the mod chunk runs, so every Connect
            // the chunk makes is tracked under it. On reload a fresh context bumps the generation first
            // (BuildMod runs before the reload teardown), letting teardown disconnect only the previous
            // generation and keep this chunk's connections. Mirrors the logic-slot keepState exclusion.
            ConnectionGeneration = bindings.Connections?.BeginGeneration(ownerModId) ?? 0;
            _instanceMeta = LuaCsRbxInstanceBindings.BuildInstanceMeta(this);
            ProxyCachePruner.Attach(bindings.Registry, this);
        }

        /// <summary>
        /// The actor a mod's code runs as when it resumes: a scheduler thread, a legacy hook, an
        /// exported function another mod calls. The attributed actor when one is bound; the host
        /// only for a mod whose current load was the host's.
        /// </summary>
        /// <remarks>
        /// WHY a mod loaded for an actor never falls back to the host: the disconnect seam releases
        /// the actor's attribution but leaves its mods loaded, and the fallback then opened a HOST
        /// envelope around that actor's legacy hooks. It only failed closed because the envelope
        /// and the context disagreed; a refusal here is closed by construction (M2-24).
        /// </remarks>
        internal static ActorContext ResolveActorContext(LuaCsRbxApiBindings bindings,
            string ownerModId, string originTag)
        {
            ModActorLedger ledger = ModActorLedger.For(bindings);
            if (bindings.Registry.TryGetActorAttribution(
                    ownerModId, originTag, out string ownerActorId))
            {
                return ledger.GetAttributedContext(ownerModId, ownerActorId,
                    bindings.Registry.WorldId);
            }

            if (ledger.TryGetReleasedOwner(ownerModId, out string releasedActorId))
            {
                throw new RbxError(
                    RbxErrorCode.NotAuthority,
                    "mod '" + ownerModId + "' was loaded for actor '" + releasedActorId
                    + "', whose attribution has been released; its code cannot run as the host",
                    "reload the mod under its owner once the actor reconnects, or unload it");
            }

            return CoreServicesInstaller.DefaultLocalHostIdentityProvider
                .GetActorContext(BuiltInAgentRoleIds.Programmer);
        }

        /// <summary>
        /// The actor a NEW load of a mod runs as: whoever the composition attributed it to, or the
        /// host when nothing is attributed. A load is where ownership is decided, so unlike
        /// <see cref="ResolveActorContext"/> it may hand an unattributed mod to the host.
        /// </summary>
        private static ActorContext ResolveLoadActorContext(LuaCsRbxApiBindings bindings,
            string ownerModId, string originTag)
        {
            if (bindings.Registry.TryGetActorAttribution(
                    ownerModId, originTag, out string ownerActorId))
            {
                return ModActorLedger.CreateAttributedContext(ownerActorId,
                    bindings.Registry.WorldId);
            }

            return CoreServicesInstaller.DefaultLocalHostIdentityProvider
                .GetActorContext(BuiltInAgentRoleIds.Programmer);
        }

        /// <summary>
        /// Per-bindings memory of which actor each mod was loaded for, plus the actor context its
        /// resumes run under, built once per attributed actor instead of once per resume.
        /// </summary>
        /// <remarks>
        /// WHY cached: every scheduler resume, legacy hook and cross-mod call resolves its actor,
        /// and building a fresh identity provider with a new GUID session id each time was two
        /// strings and a provider per resume on the hottest path in the runtime (M2-10). The entry
        /// is rebuilt only when the attributed actor or the world changes.
        /// </remarks>
        private sealed class ModActorLedger
        {
            private static readonly ConditionalWeakTable<LuaCsRbxApiBindings, ModActorLedger>
                Ledgers = new();

            private readonly Dictionary<string, Entry> _byModId = new(StringComparer.Ordinal);

            public static ModActorLedger For(LuaCsRbxApiBindings bindings)
            {
                return Ledgers.GetValue(bindings, _ => new ModActorLedger());
            }

            /// <summary>Records the actor a mod's current load belongs to; null for the host.</summary>
            public void RecordLoad(string ownerModId, string loadedActorId)
            {
                if (string.IsNullOrWhiteSpace(ownerModId))
                {
                    return;
                }

                lock (_byModId)
                {
                    GetOrAddEntry(ownerModId).LoadedActorId = loadedActorId;
                }
            }

            /// <summary>True when the mod's current load belongs to a non-host actor.</summary>
            public bool TryGetReleasedOwner(string ownerModId, out string actorId)
            {
                actorId = null;
                if (string.IsNullOrWhiteSpace(ownerModId))
                {
                    return false;
                }

                lock (_byModId)
                {
                    if (_byModId.TryGetValue(ownerModId, out Entry entry))
                    {
                        actorId = entry.LoadedActorId;
                    }
                }

                return actorId != null;
            }

            /// <summary>The restricted context for an attributed actor, reused while unchanged.</summary>
            public ActorContext GetAttributedContext(string ownerModId, string ownerActorId,
                string worldId)
            {
                if (string.IsNullOrWhiteSpace(ownerModId))
                {
                    return CreateAttributedContext(ownerActorId, worldId);
                }

                lock (_byModId)
                {
                    Entry entry = GetOrAddEntry(ownerModId);
                    if (!entry.HasAttributedContext
                        || !string.Equals(entry.AttributedActorId, ownerActorId,
                            StringComparison.Ordinal)
                        || !string.Equals(entry.AttributedWorldId, worldId,
                            StringComparison.Ordinal))
                    {
                        entry.AttributedContext = CreateAttributedContext(ownerActorId, worldId);
                        entry.AttributedActorId = ownerActorId;
                        entry.AttributedWorldId = worldId;
                        entry.HasAttributedContext = true;
                    }

                    return entry.AttributedContext;
                }
            }

            /// <summary>A fresh restricted context with its own connection session id.</summary>
            public static ActorContext CreateAttributedContext(string ownerActorId, string worldId)
            {
                LocalActorIdentityProvider provider = new(
                    ownerActorId,
                    Guid.NewGuid().ToString("N"),
                    worldId,
                    ActorGrantSet.None,
                    AgentMemoryScope.Empty);
                return provider.GetActorContext(BuiltInAgentRoleIds.Programmer);
            }

            private Entry GetOrAddEntry(string ownerModId)
            {
                if (!_byModId.TryGetValue(ownerModId, out Entry entry))
                {
                    entry = new Entry();
                    _byModId.Add(ownerModId, entry);
                }

                return entry;
            }

            private sealed class Entry
            {
                public string LoadedActorId;
                public bool HasAttributedContext;
                public string AttributedActorId;
                public string AttributedWorldId;
                public ActorContext AttributedContext;
            }
        }

        public LuaCsRbxApiBindings Bindings { get; }

        public LuaCapabilities Capabilities { get; }

        /// <summary>Issued actor identity used for object-level authorization.</summary>
        public ActorContext ActorContext { get; }

        /// <summary>Whether the caller is the composition-issued host.</summary>
        public bool IsHost => ActorContext.Grants.IsUnrestricted;

        /// <summary>Record owner attribution for objects created by this caller.</summary>
        public string OwnerActorId => IsHost ? null : ActorContext.ActorId;

        /// <summary>Teardown owner recorded on created instances; null for one-off consoles.</summary>
        public string OwnerModId { get; }

        /// <summary>Ledger origin recorded on created instances (mod:&lt;id&gt; / console:&lt;n&gt;).</summary>
        public string OriginTag { get; }

        /// <summary>This load's connection-ownership generation; connections opened by this context's
        /// chunk are tracked under it so a reload teardown keeps them and drops the prior generation.</summary>
        public int ConnectionGeneration { get; }

        /// <summary>Deprecation note for Instance.new(className, parent) fires once per mod.</summary>
        public bool HasLoggedInstanceNewParentDeprecation { get; set; }

        /// <summary>True once this mod has been told LoadCharacter is deprecated.</summary>
        public bool HasLoggedLoadCharacterDeprecation { get; set; }

        /// <summary>
        /// Logs the LoadCharacter deprecation note once per mod, never once per process.
        /// </summary>
        /// <remarks>
        /// WHY per mod: the note is advice to the author of THIS mod, and a process-wide flag would
        /// tell the first mod loaded and silently withhold it from every later one.
        /// </remarks>
        public void NoteLoadCharacterDeprecation(System.Action<string> log)
        {
            if (HasLoggedLoadCharacterDeprecation)
            {
                return;
            }

            HasLoggedLoadCharacterDeprecation = true;
            log?.Invoke(
                "[RbxApi] Player:LoadCharacter() is deprecated by Roblox; call " +
                "LoadCharacterAsync() instead. (Logged once per mod.)");
        }

        /// <summary>DEV-5 task.synchronize/desynchronize no-op note fires once per mod.</summary>
        public bool HasLoggedParallelNoOp { get; set; }

        /// <summary>tick() legacy deprecation note fires once per mod.</summary>
        public bool HasLoggedTickDeprecation { get; set; }

        public bool CanWorldEdit => (Capabilities & LuaCapabilities.WorldEdit) != 0;

        public bool IsNetworkServer => Bindings.NetworkBridge.Topology != RbxNetworkTopology.Client
                                       && IsHost;

        public void RequireNetworkSide(string member, bool serverOnly)
        {
            bool actualServer = IsNetworkServer;
            if (actualServer == serverOnly)
            {
                return;
            }

            string requiredSide = serverOnly ? "server" : "client";
            string actualSide = actualServer ? "server" : "client";
            throw new RbxError(
                RbxErrorCode.NotAuthority,
                "actor '" + ActorContext.ActorId + "' cannot use " + member
                + " because " + member + " is " + requiredSide
                + "-only and this actor runs as " + actualSide,
                "call " + member + " from a " + requiredSide + " script context",
                OwnerModId);
        }

        /// <summary>
        /// Demands the composition-issued unrestricted (host) grant for a Lua-callable member whose
        /// Roblox security class (e.g. <c>PluginSecurity</c>) has no ordinary-script equivalent — e.g.
        /// <c>ScriptContext:SetTimeout</c>. Mirrors <see cref="RequireNetworkSide"/>'s refusal shape
        /// (same <see cref="RbxErrorCode.NotAuthority"/> code, same "actor '&lt;id&gt;' cannot use
        /// &lt;member&gt; because ..." message/fix/mod-attribution shape) with <see cref="IsHost"/> —
        /// <c>ActorContext.Grants.IsUnrestricted</c> — as the gate instead of server/client.
        /// </summary>
        public void RequireUnrestricted(string member)
        {
            if (IsHost)
            {
                return;
            }

            throw new RbxError(
                RbxErrorCode.NotAuthority,
                "actor '" + ActorContext.ActorId + "' cannot use " + member
                + " because " + member + " requires the composition's unrestricted host authority",
                "call " + member + " only from host-composed code",
                OwnerModId);
        }

        /// <summary>Sink storing BasePart spatial/appearance state in Roblox space (shared world).</summary>
        public IPartPropertySink PartSink => Bindings.PartSink;

        /// <summary>Live instances this context holds a proxy for (registered ones only).</summary>
        internal int ProxyCacheCount => _proxyCache.Count;

        /// <summary>Weakly remembered proxies of unregistered instances, collected ones included.</summary>
        internal int DetachedProxyCount => _detachedProxies.Count;

        /// <summary>
        /// Wraps an instance keeping one proxy per instance so Lua <c>==</c> and table keys behave
        /// like Roblox reference identity.
        /// </summary>
        /// <remarks>
        /// WHY two tiers: a registered instance's proxy is held strongly so identity is stable for
        /// as long as the instance lives, and the registry's <c>Unregistered</c> event moves it to
        /// a weak tier when the instance is destroyed — a spawner mod that creates and destroys
        /// parts all session no longer pins every dead part it ever touched. The weak tier keeps
        /// identity for a destroyed instance while Lua still holds its proxy: deferred handlers
        /// (<c>PlayerRemoving</c>, <c>ChildRemoved</c>) receive the destroyed instance after the
        /// unregister, and <c>data[player] = nil</c> in a save-on-leave handler must find the key
        /// the join handler stored.
        /// </remarks>
        public LuaValue WrapInstance(RbxInstance instance)
        {
            if (instance == null)
            {
                return LuaValue.Nil;
            }

            if (_proxyCache.TryGetValue(instance, out LuaValue cached))
            {
                return cached;
            }

            if (instance.IsDestroyed)
            {
                return WrapDetachedInstance(instance);
            }

            LuaValue proxy = new(new LuaCsRbxInstanceProxy(instance, this, _instanceMeta));
            _proxyCache[instance] = proxy;
            return proxy;
        }

        /// <summary>
        /// Moves the proxy of an unregistered instance from the strong cache to the weak tier.
        /// Called from the registry's <c>Unregistered</c> event; never throws.
        /// </summary>
        internal void ForgetProxy(RbxInstance instance)
        {
            if (instance == null || !_proxyCache.TryGetValue(instance, out LuaValue cached))
            {
                return;
            }

            _proxyCache.Remove(instance);
            if (TryGetInstance(cached, out LuaCsRbxInstanceProxy proxy))
            {
                RememberDetachedProxy(instance.Id, proxy);
            }
        }

        private LuaValue WrapDetachedInstance(RbxInstance instance)
        {
            if (_detachedProxies.TryGetValue(instance.Id,
                    out WeakReference<LuaCsRbxInstanceProxy> weakProxy)
                && weakProxy.TryGetTarget(out LuaCsRbxInstanceProxy liveProxy)
                && ReferenceEquals(liveProxy.Instance, instance))
            {
                return new LuaValue(liveProxy);
            }

            LuaCsRbxInstanceProxy created = new(instance, this, _instanceMeta);
            RememberDetachedProxy(instance.Id, created);
            return new LuaValue(created);
        }

        private void RememberDetachedProxy(InstanceId id, LuaCsRbxInstanceProxy proxy)
        {
            _detachedProxies[id] = new WeakReference<LuaCsRbxInstanceProxy>(proxy);
            if (_detachedProxies.Count < _detachedSweepThreshold)
            {
                return;
            }

            List<InstanceId> collected = new();
            foreach (KeyValuePair<InstanceId, WeakReference<LuaCsRbxInstanceProxy>> pair
                     in _detachedProxies)
            {
                if (!pair.Value.TryGetTarget(out _))
                {
                    collected.Add(pair.Key);
                }
            }

            for (int index = 0; index < collected.Count; index++)
            {
                _detachedProxies.Remove(collected[index]);
            }

            // WHY doubling: a sweep costs one pass over the tier, so sweeping again only after the
            // survivors have doubled keeps the amortized cost per destroyed instance constant.
            _detachedSweepThreshold = Math.Max(MinimumDetachedSweepThreshold,
                _detachedProxies.Count * 2);
        }

        /// <summary>
        /// Forwards <see cref="InstanceRegistry.Unregistered"/> to one context without keeping the
        /// context alive: a context lives as long as its Lua state, and a registry that held it
        /// strongly would keep every finished console surface alive with it.
        /// </summary>
        private sealed class ProxyCachePruner
        {
            private readonly WeakReference<LuaCsRbxModContext> _context;
            private readonly InstanceRegistry _registry;

            private ProxyCachePruner(InstanceRegistry registry, LuaCsRbxModContext context)
            {
                _registry = registry;
                _context = new WeakReference<LuaCsRbxModContext>(context);
            }

            public static void Attach(InstanceRegistry registry, LuaCsRbxModContext context)
            {
                if (registry == null)
                {
                    return;
                }

                ProxyCachePruner pruner = new(registry, context);
                registry.Unregistered += pruner.OnUnregistered;
            }

            private void OnUnregistered(InstanceRecord record)
            {
                if (!_context.TryGetTarget(out LuaCsRbxModContext context))
                {
                    _registry.Unregistered -= OnUnregistered;
                    return;
                }

                context.ForgetProxy(record?.Instance);
            }
        }

        /// <summary>
        /// Records a signal connection this mod opened against the shared connection ledger so the
        /// composition disconnects it on teardown. No-op for the ownerless one-off surface (no mod id).
        /// </summary>
        public void TrackConnection(RbxScriptConnection connection)
        {
            Bindings.Connections?.Track(OwnerModId, ConnectionGeneration, connection);
        }

        public void RequireWorldEdit(string what)
        {
            if (!CanWorldEdit)
            {
                throw RbxError.BadArgument(
                    what + " requires the WorldEdit capability, which was not granted to this script",
                    "grant the mod the WorldEdit capability or remove the instance mutation");
            }
        }

        /// <summary>
        /// WorldEdit check for a property write, taking the class and member separately so the
        /// description string is built only when the check FAILS. Part property writes run per frame,
        /// and the eagerly-concatenated description was an allocation on every successful write.
        /// </summary>
        public void RequireWorldEditForWrite(RbxInstance target, string member)
        {
            if (!CanWorldEdit)
            {
                RequireWorldEdit("setting " + target.ClassName + "." + member);
            }

            RequireMutationTarget(target, "write property");
            Bindings.Registry.AuthorizeMutation(ActorContext.ActorId,
                ActorContext.Grants.IsUnrestricted, ActorContext.WorldId, target,
                RegistryWorldAclDecision.WriteProperty, "write property");
        }

        public void RequireMetadataMutation(RbxInstance target, string operation)
        {
            RequireWorldEdit(operation);
            RequireMutationTarget(target, operation);
            Bindings.Registry.AuthorizeMutation(ActorContext.ActorId,
                ActorContext.Grants.IsUnrestricted, ActorContext.WorldId, target,
                RegistryWorldAclDecision.MutateMetadata, operation);
        }

        public void RequirePivotMutation(RbxInstance target)
        {
            if (!CanWorldEdit)
            {
                RequireWorldEdit(target.ClassName + ":PivotTo");
            }

            RequireMutationTarget(target, "pivot");
            Bindings.Registry.AuthorizeMutation(ActorContext.ActorId,
                ActorContext.Grants.IsUnrestricted, ActorContext.WorldId, target,
                RegistryWorldAclDecision.WriteProperty, "pivot");
            // WHY parts and models rather than every PVInstance: those are the descendants PivotTo
            // moves; the Camera is a PVInstance too, and demanding write rights over the world
            // camera for a pivot that never moves it would refuse a restricted actor for nothing.
            foreach (RbxInstance descendant in target.GetDescendants())
            {
                if (descendant.IsA("BasePart") || descendant is RbxModel)
                {
                    Bindings.Registry.AuthorizeMutation(ActorContext.ActorId,
                        ActorContext.Grants.IsUnrestricted, ActorContext.WorldId,
                        descendant,
                        RegistryWorldAclDecision.WriteProperty, "pivot descendant");
                }
            }
        }

        public void RequireReparent(RbxInstance target, RbxInstance destination)
        {
            RequireWorldEdit("setting Instance.Parent");
            RequireMutationTarget(target, "reparent");
            if (target.IsDestroyed)
            {
                return;
            }

            RbxInstance sourceContainer = target.Parent;
            Bindings.Registry.AuthorizeMutation(ActorContext.ActorId,
                ActorContext.Grants.IsUnrestricted, ActorContext.WorldId, target,
                RegistryWorldAclDecision.ReparentSelf, "reparent source");
            if (sourceContainer != null && !ReferenceEquals(sourceContainer, destination))
            {
                Bindings.Registry.AuthorizeMutation(ActorContext.ActorId,
                    ActorContext.Grants.IsUnrestricted, ActorContext.WorldId,
                    sourceContainer,
                    RegistryWorldAclDecision.AcceptChild,
                    "reparent source container");
            }

            if (destination != null)
            {
                Bindings.Registry.AuthorizeMutation(ActorContext.ActorId,
                    ActorContext.Grants.IsUnrestricted, ActorContext.WorldId,
                    destination,
                    RegistryWorldAclDecision.AcceptChild, "reparent destination");
            }
        }

        public void RequireCreateUnder(RbxInstance destination)
        {
            RequireWorldEdit("Instance.new parent assignment");
            RequireMutationTarget(destination, "create child");
            Bindings.Registry.AuthorizeMutation(ActorContext.ActorId,
                ActorContext.Grants.IsUnrestricted, ActorContext.WorldId,
                destination,
                RegistryWorldAclDecision.AcceptChild, "create child");
        }

        /// <summary>
        /// Resolves and authorizes the envelope target as the revision anchor for an unparented
        /// <c>Instance.new</c>. Legacy non-enveloped scripts have no creation anchor.
        /// </summary>
        public RbxInstance RequireUnparentedCreationAnchor()
        {
            if (!_mutationEnvelope.HasValue)
            {
                return null;
            }

            MutationEnvelope envelope = _mutationEnvelope.Value;
            if (!Bindings.Registry.TryGet(
                    envelope.TargetInstanceId, out RbxInstance creationAnchor))
            {
                throw RbxError.BadArgument(
                    "actor '" + ActorContext.ActorId + "' cannot create an instance: operation '"
                    + envelope.OperationId + "' targets no live instance",
                    "refresh the target revision and submit a new caller-generated operation id");
            }

            RequireCreateUnder(creationAnchor);
            return creationAnchor;
        }

        public void RequireDestroyTree(RbxInstance target, string operation)
        {
            RequireWorldEdit(operation);
            RequireMutationTarget(target, operation);
            Bindings.Registry.AuthorizeMutation(ActorContext.ActorId,
                ActorContext.Grants.IsUnrestricted, ActorContext.WorldId, target,
                RegistryWorldAclDecision.Destroy, operation);
            foreach (RbxInstance descendant in target.GetDescendants())
            {
                RegistryWorldAclAuthorizer.Demand(Bindings.Registry,
                    ActorContext.ActorId,
                    ActorContext.Grants.IsUnrestricted, ActorContext.WorldId,
                    descendant,
                    RegistryWorldAclDecision.Destroy, operation);
            }
        }

        public void RequireDestroyForest(RbxInstance container,
            IReadOnlyList<RbxInstance> roots, string operation)
        {
            RequireWorldEdit(operation);
            RequireMutationTarget(container, operation);
            Bindings.Registry.AuthorizeMutation(ActorContext.ActorId,
                ActorContext.Grants.IsUnrestricted, ActorContext.WorldId,
                container,
                RegistryWorldAclDecision.AcceptChild, operation + " container");
            for (int rootIndex = 0; rootIndex < roots.Count; rootIndex++)
            {
                RbxInstance root = roots[rootIndex];
                RegistryWorldAclAuthorizer.Demand(Bindings.Registry,
                    ActorContext.ActorId,
                    ActorContext.Grants.IsUnrestricted, ActorContext.WorldId, root,
                    RegistryWorldAclDecision.Destroy, operation);
                foreach (RbxInstance descendant in root.GetDescendants())
                {
                    RegistryWorldAclAuthorizer.Demand(Bindings.Registry,
                        ActorContext.ActorId,
                        ActorContext.Grants.IsUnrestricted, ActorContext.WorldId,
                        descendant,
                        RegistryWorldAclDecision.Destroy, operation);
                }
            }
        }

        public void RequireMutationTarget(RbxInstance target, string operation)
        {
            if (!_mutationEnvelope.HasValue)
            {
                Bindings.Registry.DemandMutationEnvelope(
                    ActorContext.ActorId, operation);
                return;
            }

            MutationEnvelope envelope = _mutationEnvelope.Value;
            if (target != null && target.Id == envelope.TargetInstanceId)
            {
                return;
            }

            string actualTarget = target == null
                ? "no instance"
                : "instance id " + target.Id.Value;
            throw RbxError.BadArgument(
                "actor '" + ActorContext.ActorId + "' cannot " + operation + " on "
                + actualTarget + ": operation '" + envelope.OperationId
                + "' targets instance id " + envelope.TargetInstanceId.Value,
                "submit a separate mutation envelope for each target instance");
        }

        public T ApplyServerGeneratedMutation<T>(string operation, Func<T> mutation)
        {
            if (!_serverGeneratesMutationEnvelopes)
            {
                return mutation();
            }

            return Bindings.Registry.ApplyServerGeneratedMutation(
                ActorContext.ActorId,
                ActorContext.Grants.IsUnrestricted,
                ActorContext.WorldId,
                operation,
                mutation);
        }

        public void RecordMutation(RbxInstance target)
        {
            Bindings.Registry.AdvanceRevision(target.Id);
        }
    }

    /// <summary>
    /// Lua member dispatch for <see cref="RbxInstance"/> proxies (roadmap §5.1.3): properties,
    /// navigation, lifecycle, attributes, tags, child-by-name sugar, ServiceProvider members on
    /// the DataModel, BasePart spatial/appearance properties over the part-property sink, and the
    /// scheduler-backed yielding navigation surface.
    /// Destroyed instances follow DEV-7 at the Lua boundary: every member access raises
    /// INSTANCE_DESTROYED except for Name/ClassName/Parent inside destruction-queued handlers.
    /// </summary>
    internal static class LuaCsRbxInstanceBindings
    {
        public static LuaTable BuildInstanceMeta(LuaCsRbxModContext context)
        {
            LuaCsRbxMethodTable methods = BuildMethods(context);
            RbxEnumItem deferredSignalBehavior = EnsureDeferredSignalBehavior(context.Bindings.Enums);

            LuaTable meta = new();
            meta[Metamethods.Index] = Fn("Instance.__index", ctx =>
            {
                RbxInstance self = Self(ctx, context);
                string key = ReadString(ctx, 1, "Instance member access");
                ThrowIfDestroyedForLua(self, key, memberRead: true);
                ThrowIfStubServiceForLua(self, key);

                switch (key)
                {
                    case "Name": return self.Name;
                    case "ClassName": return self.ClassName;
                    case "Parent": return context.WrapInstance(self.Parent);
                    case "Archivable": return self.Archivable;
                    case "ChildAdded": return LuaCsRbxDatatypeBindings.Wrap(self.ChildAdded, context);
                    case "ChildRemoved": return LuaCsRbxDatatypeBindings.Wrap(self.ChildRemoved, context);
                    case "DescendantAdded":
                        return LuaCsRbxDatatypeBindings.Wrap(self.DescendantAdded, context);
                    case "DescendantRemoving":
                        return LuaCsRbxDatatypeBindings.Wrap(self.DescendantRemoving, context);
                    case "Destroying": return LuaCsRbxDatatypeBindings.Wrap(self.Destroying, context);
                    case "AncestryChanged":
                        return LuaCsRbxDatatypeBindings.Wrap(self.AncestryChanged, context);
                    case "AttributeChanged":
                        return LuaCsRbxDatatypeBindings.Wrap(self.AttributeChanged, context);
                    case "Changed":
                        // WHY: a value object's Changed carries the new Value (Object.yaml); every
                        // other instance's carries the name of the property that changed.
                        return LuaCsRbxDatatypeBindings.Wrap(
                            self is RbxValueBase changedValue ? changedValue.Changed : self.Changed,
                            context);
                    case "TagAdded":
                    case "TagRemoved":
                        if (self is RbxCollectionService tagSignalService)
                        {
                            tagSignalService.EnsureHost(context.Bindings.Scheduler);
                            return LuaCsRbxDatatypeBindings.Wrap(
                                key == "TagAdded"
                                    ? tagSignalService.TagAdded
                                    : tagSignalService.TagRemoved,
                                context);
                        }

                        break;
                    case "WaitForChild": return ReadWaitForChildBridge(ctx);
                    // WHY guarded on the class here rather than in a Player-only table: these are
                    // the only two YIELDING Player members, and yielding members resolve through
                    // the task bridge, which lives on this path. Without the guard
                    // `part:LoadCharacterAsync()` would resolve on any instance instead of being
                    // the unknown member it is.
                    case "LoadCharacterAsync" when self is RbxPlayer:
                        return ReadTaskBridge(ctx, "_loadCharacterBridge",
                            "Player.LoadCharacterAsync");
                    case "LoadCharacter" when self is RbxPlayer:
                        return ReadTaskBridge(ctx, "_loadCharacterDeprecatedBridge",
                            "Player.LoadCharacter");
                }

                if (TryReadNetworkMember(context, self, key, ctx,
                        out LuaValue networkValue))
                {
                    return networkValue;
                }

                if (methods.TryResolve(self, key, out LuaValue method))
                {
                    return method;
                }

                if (key == "SignalBehavior" && self.IsA("Workspace"))
                {
                    return LuaCsRbxDatatypeBindings.Wrap(deferredSignalBehavior);
                }

                // WHY a signal that never fires: a mod only runs once its world has loaded, so the
                // Roblox header `if not game:IsLoaded() then game.Loaded:Wait() end` must pass
                // straight through, and a late Connect waits for a load that already happened,
                // exactly as it does in Roblox.
                if (key == "Loaded" && self is RbxDataModel loadedDataModel)
                {
                    return LuaCsRbxDatatypeBindings.Wrap(
                        LoadedSignals.GetValue(loadedDataModel,
                            _ => new RbxScriptSignal("DataModel.Loaded")),
                        context);
                }

                if (TryReadCamera(context, self, key, out LuaValue cameraValue))
                {
                    return cameraValue;
                }

                if (TryReadUserInput(context, self, key, out LuaValue inputValue))
                {
                    return inputValue;
                }

                if (TryReadRunService(context, self, key, out LuaValue runValue))
                {
                    return runValue;
                }

                if (TryReadClickDetector(context, self, key, out LuaValue clickValue))
                {
                    return clickValue;
                }

                if (TryReadMaterialVariant(context, self, key, out LuaValue variantValue))
                {
                    return variantValue;
                }

                if (TryReadValue(context, self, key, out LuaValue valueResult))
                {
                    return valueResult;
                }

                if (TryReadModelPivot(context, self, key, out LuaValue modelPivotValue))
                {
                    return modelPivotValue;
                }

                if (TryReadSpatial(context, self, key, out LuaValue spatial))
                {
                    return spatial;
                }

                if (TryReadTween(context, self, key, out LuaValue tweenValue))
                {
                    return tweenValue;
                }

                RbxError knownMemberError = GetKnownUnimplementedMemberError(
                    context, self, key, RbxKnownUnimplementedMemberAccess.Read);
                if (knownMemberError != null)
                {
                    throw knownMemberError;
                }

                RbxInstance child = self.FindFirstChild(key);
                if (child != null)
                {
                    return context.WrapInstance(child);
                }

                throw RbxError.BadArgument(
                    key + " is not a valid member of " + self.ClassName + " \"" + self.GetFullName() + "\"",
                    "use FindFirstChild(\"" + key + "\") for children that may not exist yet");
            }, context);

            meta[Metamethods.NewIndex] = Fn("Instance.__newindex", ctx =>
            {
                return context.ApplyServerGeneratedMutation("write instance member", () =>
                {
                    RbxInstance self = Self(ctx, context);
                    string key = ReadString(ctx, 1, "Instance member assignment");
                    LuaValue value = Arg(ctx, 2);
                    ThrowIfStubServiceForLua(self, key);

                    switch (key)
                    {
                        case "Name":
                            ThrowIfDestroyedForLua(self, key);
                            context.RequireWorldEditForWrite(self, "Name");
                            self.Name = ReadAssignedString(value, self.ClassName, "Name");
                            return LuaValue.Nil;
                        case "Parent":
                        // WHY: no destroyed pre-check here — the Domain setter raises the exact
                        // D6 PARENT_LOCKED message for destroyed instances.
                        RbxInstance destination = ReadAssignedOptionalInstance(
                            value, self.ClassName, "Parent");
                        if (self is RbxPlayer)
                        {
                            // WHY: a Player leaves only through the leave teardown (PlayerRemoving,
                            // character cleanup, actor release); moving it out of Players left a
                            // ghost that GetPlayers still returned and the actor never got back.
                            throw RbxError.BadArgument(
                                "Player.Parent cannot be set from a script; a Player leaves only "
                                + "through Player:Kick()",
                                "call player:Kick() to remove a player; parent your own objects "
                                + "into the player instead");
                        }

                        if (!context.Bindings.Registry.IsWorldAclEnabled
                            && IsProtectedSingleton(self))
                        {
                            // WHY: a service's Parent is locked in Roblox — reparenting (or nil-ing)
                            // it would detach it so game:GetService stops resolving it for the world.
                            throw RbxError.BadArgument(
                                self.ClassName + ".Parent is locked — it is a shared singleton",
                                "services, the DataModel and workspace.CurrentCamera are fixed for "
                                + "the world's lifetime");
                        }

                            context.RequireReparent(self, destination);
                            self.Parent = destination;
                            return LuaValue.Nil;
                        case "Archivable":
                            ThrowIfDestroyedForLua(self, key);
                            context.RequireWorldEditForWrite(self, "Archivable");
                            self.Archivable = ReadAssignedBoolean(value, self.ClassName, "Archivable");
                            return LuaValue.Nil;
                    }

                    ThrowIfDestroyedForLua(self, key);
                    if (TryWriteNetworkMember(context, self, key, ctx.State, value))
                    {
                        return LuaValue.Nil;
                    }

                if (TryWriteCamera(context, self, key, value))
                {
                    return LuaValue.Nil;
                }

                if (TryWriteUserInput(context, self, key, value))
                {
                    return LuaValue.Nil;
                }

                if (TryWriteClickDetector(context, self, key, value))
                {
                    return LuaValue.Nil;
                }

                if (TryWriteMaterialVariant(context, self, key, value))
                {
                    return LuaValue.Nil;
                }

                if (TryWriteValue(context, self, key, value))
                {
                    return LuaValue.Nil;
                }

                if (TryWriteModelPivot(context, self, key, value))
                {
                    return LuaValue.Nil;
                }

                if (TryWriteSpatial(context, self, key, value))
                {
                    return LuaValue.Nil;
                }

                if (IsReadOnlyMember(self, key))
                {
                    throw RbxError.BadArgument(
                        "Unable to assign property " + key + ". Property is read only",
                        "read " + self.ClassName + "." + key + " instead; a script cannot set it");
                }

                if (key == "CurrentCamera" && self.IsA("Workspace"))
                {
                    context.RequireWorldEditForWrite(self, key);
                    throw RbxError.NotImplemented(
                        "Workspace.CurrentCamera assignment",
                        "a later MVP",
                        "drive the one world camera through workspace.CurrentCamera.CFrame and "
                        + "CameraType instead of swapping it for another Camera");
                }

                RbxError knownMemberError = GetKnownUnimplementedMemberError(
                    context, self, key, RbxKnownUnimplementedMemberAccess.Write);
                if (knownMemberError != null)
                {
                    context.RequireWorldEditForWrite(self, key);
                    throw knownMemberError;
                }

                    throw RbxError.BadArgument(
                        key + " is not a valid member of " + self.ClassName + " \"" + self.GetFullName() + "\"",
                        "set a writable Instance property (Name, Parent, Archivable, or a BasePart " +
                        "spatial property like Position/Size/CFrame/Color) or use SetAttribute");
                });
            }, context);

            meta[Metamethods.Eq] = Fn("Instance.__eq", ctx =>
                TryGetInstance(Arg(ctx, 0), out LuaCsRbxInstanceProxy a)
                && TryGetInstance(Arg(ctx, 1), out LuaCsRbxInstanceProxy b)
                && ReferenceEquals(a.Instance, b.Instance), context);

            meta[Metamethods.ToString] = Fn(
                "Instance.__tostring", ctx => Self(ctx, context).Name, context);
            return Lock(meta);
        }

        private static LuaValue ReadWaitForChildBridge(LuaFunctionExecutionContext ctx)
        {
            return ReadTaskBridge(ctx, "_waitForChildBridge", "Instance.WaitForChild");
        }

        /// <summary>
        /// Returns the named yielding bridge out of the mod's <c>task</c> table.
        /// </summary>
        /// <remarks>
        /// WHY yielding members resolve to a Lua function rather than being bound as C# methods:
        /// yielding is <c>coroutine.yield</c>, which only Lua can perform. The bridge is a Lua
        /// closure over C# hooks, so the member both does its work in C# and suspends properly.
        /// </remarks>
        private static LuaValue ReadTaskBridge(LuaFunctionExecutionContext ctx, string bridgeName,
            string memberName)
        {
            LuaValue taskValue = ctx.State.Environment["task"];
            if (taskValue.Type != LuaValueType.Table)
            {
                throw RbxError.BadArgument(
                    memberName + " requires the task scheduler bridge",
                    "call it from a loaded mod scheduler thread");
            }

            LuaValue bridge = taskValue.Read<LuaTable>()[bridgeName];
            if (bridge.Type != LuaValueType.Function)
            {
                throw RbxError.BadArgument(
                    memberName + " bridge is unavailable",
                    "call it after the mod scheduler initializes");
            }

            return bridge;
        }

        private static bool TryReadNetworkMember(LuaCsRbxModContext context,
            RbxInstance instance, string member, LuaFunctionExecutionContext ctx,
            out LuaValue value)
        {
            if (instance is RbxPlayers players)
            {
                switch (member)
                {
                    case "LocalPlayer":
                        value = context.WrapInstance(context.Bindings.GetLocalPlayer(context));
                        return true;
                    case "PlayerAdded":
                        value = LuaCsRbxDatatypeBindings.Wrap(players.PlayerAdded, context);
                        return true;
                    case "PlayerRemoving":
                        value = LuaCsRbxDatatypeBindings.Wrap(players.PlayerRemoving, context);
                        return true;
                    case "CharacterAutoLoads":
                        value = new LuaValue(players.CharacterAutoLoads);
                        return true;
                    case "RespawnTime":
                        value = new LuaValue(players.RespawnTime);
                        return true;
                    case "MaxPlayers":
                        value = new LuaValue((double)players.MaxPlayers);
                        return true;
                }
            }

            // WHY read through the physics facade rather than a stored property: gravity is world
            // state the engine adapter must see, and a copy on the Workspace instance would be the
            // version that drifts when a host attaches physics after a script already set it.
            if (member == "Gravity" && instance.IsA("Workspace"))
            {
                value = context.Bindings.WorldPhysics.Gravity;
                return true;
            }

            if (instance is RbxHumanoid humanoid)
            {
                switch (member)
                {
                    case "Health": return Ok(humanoid.Health, out value);
                    case "MaxHealth": return Ok(humanoid.MaxHealth, out value);
                    case "WalkSpeed": return Ok(humanoid.WalkSpeed, out value);
                    case "JumpPower": return Ok(humanoid.JumpPower, out value);
                    case "JumpHeight": return Ok(humanoid.JumpHeight, out value);
                    case "UseJumpPower": return Ok(humanoid.UseJumpPower, out value);
                    case "DisplayName": return Ok(humanoid.DisplayName ?? "", out value);
                    case "MoveDirection":
                        return Ok(LuaCsRbxDatatypeBindings.Wrap(humanoid.MoveDirection), out value);
                    case "RootPart":
                        return Ok(context.WrapInstance(humanoid.RootPart), out value);
                    // WHY Jump reads the state rather than a stored flag: the mirror's Jump is a
                    // request on write, and on read it answers "am I jumping" — which is exactly
                    // the state machine's answer. A constant false would make a legitimate
                    // `if humanoid.Jump then` branch dead code.
                    case "Jump":
                        return Ok(humanoid.GetState() == RbxHumanoidState.Jumping, out value);
                    case "Died":
                        return Ok(LuaCsRbxDatatypeBindings.Wrap(humanoid.Died, context), out value);
                    case "HealthChanged":
                        return Ok(LuaCsRbxDatatypeBindings.Wrap(humanoid.HealthChanged, context), out value);
                    case "MoveToFinished":
                        return Ok(LuaCsRbxDatatypeBindings.Wrap(humanoid.MoveToFinished, context), out value);
                    case "Running":
                        return Ok(LuaCsRbxDatatypeBindings.Wrap(humanoid.Running, context), out value);
                    case "Jumping":
                        return Ok(LuaCsRbxDatatypeBindings.Wrap(humanoid.Jumping, context), out value);
                    case "FreeFalling":
                        return Ok(LuaCsRbxDatatypeBindings.Wrap(humanoid.FreeFalling, context), out value);
                    case "StateChanged":
                        return Ok(LuaCsRbxDatatypeBindings.Wrap(humanoid.StateChanged, context), out value);
                }
            }

            // WHY here and not in the part-property reader: Touched/TouchEnded are signals on the
            // instance, not state in the part sink, and a part with no listener must not have its
            // signal created just because something read a different member.
            if (instance is RbxBasePart basePart)
            {
                switch (member)
                {
                    case "Touched":
                        value = LuaCsRbxDatatypeBindings.Wrap(basePart.Touched, context);
                        return true;
                    case "TouchEnded":
                        value = LuaCsRbxDatatypeBindings.Wrap(basePart.TouchEnded, context);
                        return true;
                }
            }

            if (instance is RbxPlayer player)
            {
                switch (member)
                {
                    case "UserId":
                        value = (double)player.UserId;
                        return true;
                    case "DisplayName":
                        value = player.DisplayName ?? "";
                        return true;
                    case "Character":
                        value = context.WrapInstance(player.Character);
                        return true;
                    case "CharacterAdded":
                        value = LuaCsRbxDatatypeBindings.Wrap(player.CharacterAdded, context);
                        return true;
                    case "CharacterRemoving":
                        value = LuaCsRbxDatatypeBindings.Wrap(player.CharacterRemoving, context);
                        return true;
                }
            }

            if (instance is RbxRemoteEvent remoteEvent)
            {
                switch (member)
                {
                    case "OnServerEvent":
                        context.RequireNetworkSide(
                            remoteEvent.ClassName + ".OnServerEvent", true);
                        value = LuaCsRbxDatatypeBindings.Wrap(
                            remoteEvent.OnServerEvent, context);
                        return true;
                    case "OnClientEvent":
                        context.RequireNetworkSide(
                            remoteEvent.ClassName + ".OnClientEvent", false);
                        value = LuaCsRbxDatatypeBindings.Wrap(
                            remoteEvent.GetOnClientEvent(context.ActorContext.ActorId), context);
                        return true;
                }
            }

            if (instance is RbxRemoteFunction remoteFunction)
            {
                switch (member)
                {
                    case "OnServerInvoke":
                        context.RequireNetworkSide(
                            "RemoteFunction.OnServerInvoke", true);
                        value = context.Bindings.ReadRemoteFunctionCallback(
                            context, remoteFunction, true);
                        return true;
                    case "OnClientInvoke":
                        context.RequireNetworkSide(
                            "RemoteFunction.OnClientInvoke", false);
                        value = context.Bindings.ReadRemoteFunctionCallback(
                            context, remoteFunction, false);
                        return true;
                    case "InvokeServer":
                        context.RequireNetworkSide(
                            "RemoteFunction:InvokeServer", false);
                        value = ReadRemoteFunctionInvokeBridge(ctx, true);
                        return true;
                    case "InvokeClient":
                        context.RequireNetworkSide(
                            "RemoteFunction:InvokeClient", true);
                        value = ReadRemoteFunctionInvokeBridge(ctx, false);
                        return true;
                }
            }

            value = LuaValue.Nil;
            return false;
        }

        private static bool TryWriteNetworkMember(LuaCsRbxModContext context,
            RbxInstance instance, string member, LuaState state, LuaValue value)
        {
            if (instance is RbxPlayers playersTarget)
            {
                switch (member)
                {
                    case "CharacterAutoLoads":
                        context.RequireWorldEditForWrite(instance, "CharacterAutoLoads");
                        playersTarget.CharacterAutoLoads = ReadAssignedBoolean(
                            value, instance.ClassName, "CharacterAutoLoads");
                        return true;
                    case "RespawnTime":
                        context.RequireWorldEditForWrite(instance, "RespawnTime");
                        playersTarget.RespawnTime = ReadRespawnTime(value, instance.ClassName);
                        return true;
                    case "MaxPlayers":
                        // WHY refused rather than silently ignored: the mirror tags MaxPlayers
                        // ReadOnly, and a script that "sets" a capacity the host owns would carry
                        // on believing it changed something.
                        throw RbxError.BadArgument(
                            "Players.MaxPlayers is read-only",
                            "the host owns the capacity; MaxPlayers cannot be set from a mod");
                }
            }

            if (member == "Gravity" && instance.IsA("Workspace"))
            {
                context.RequireWorldEditForWrite(instance, "Gravity");
                double gravity = ReadAssignedNumber(value, instance.ClassName, "Gravity");
                double previousGravity = context.Bindings.WorldPhysics.Gravity;
                context.Bindings.WorldPhysics.Gravity = gravity;
                // WHY here and not in a setter: gravity lives on the physics facade, not on the
                // Workspace instance, so this write is the only place that knows it changed.
                if (!previousGravity.Equals(context.Bindings.WorldPhysics.Gravity))
                {
                    instance.NotifyPropertyChanged("Gravity");
                }

                return true;
            }

            if (instance is RbxHumanoid humanoidTarget)
            {
                switch (member)
                {
                    case "Health":
                        context.RequireWorldEditForWrite(instance, "Health");
                        humanoidTarget.Health = ReadAssignedNumber(value, instance.ClassName, "Health");
                        context.RecordMutation(instance);
                        return true;
                    case "MaxHealth":
                        context.RequireWorldEditForWrite(instance, "MaxHealth");
                        humanoidTarget.MaxHealth =
                            ReadAssignedNumber(value, instance.ClassName, "MaxHealth");
                        context.RecordMutation(instance);
                        return true;
                    case "WalkSpeed":
                        context.RequireWorldEditForWrite(instance, "WalkSpeed");
                        humanoidTarget.WalkSpeed =
                            ReadAssignedNumber(value, instance.ClassName, "WalkSpeed");
                        context.RecordMutation(instance);
                        return true;
                    case "JumpPower":
                        context.RequireWorldEditForWrite(instance, "JumpPower");
                        humanoidTarget.JumpPower =
                            ReadAssignedNumber(value, instance.ClassName, "JumpPower");
                        context.RecordMutation(instance);
                        return true;
                    case "JumpHeight":
                        context.RequireWorldEditForWrite(instance, "JumpHeight");
                        humanoidTarget.JumpHeight =
                            ReadAssignedNumber(value, instance.ClassName, "JumpHeight");
                        context.RecordMutation(instance);
                        return true;
                    case "UseJumpPower":
                        context.RequireWorldEditForWrite(instance, "UseJumpPower");
                        humanoidTarget.UseJumpPower = ReadAssignedBoolean(
                            value, instance.ClassName, "UseJumpPower");
                        context.RecordMutation(instance);
                        return true;
                    case "DisplayName":
                        context.RequireWorldEditForWrite(instance, "DisplayName");
                        humanoidTarget.DisplayName =
                            ReadAssignedString(value, instance.ClassName, "DisplayName");
                        context.RecordMutation(instance);
                        return true;
                    // WHY a write and not a method: the mirror's jump request IS an assignment
                    // (humanoid.Jump = true), and scripts written for Roblox spell it that way.
                    case "Jump":
                        context.RequireWorldEditForWrite(instance, "Jump");
                        if (ReadAssignedBoolean(value, instance.ClassName, "Jump"))
                        {
                            humanoidTarget.RequestJump();
                        }

                        return true;
                }
            }

            if (instance is RbxPlayer player)
            {
                switch (member)
                {
                    case "DisplayName":
                        context.RequireWorldEditForWrite(player, "DisplayName");
                        player.DisplayName = ReadAssignedString(value, player.ClassName, "DisplayName");
                        context.RecordMutation(player);
                        return true;
                    case "Character":
                        context.RequireWorldEditForWrite(player, "Character");
                        player.Character = ReadAssignedOptionalInstance(
                            value, player.ClassName, "Character");
                        context.RecordMutation(player);
                        return true;
                    default:
                        return false;
                }
            }

            if (!(instance is RbxRemoteFunction remoteFunction))
            {
                return false;
            }

            switch (member)
            {
                case "OnServerInvoke":
                    context.RequireNetworkSide("RemoteFunction.OnServerInvoke", true);
                    context.RequireMetadataMutation(instance, "set server invoke callback");
                    context.Bindings.WriteRemoteFunctionCallback(
                        context, remoteFunction, true, state, value);
                    context.RecordMutation(instance);
                    return true;
                case "OnClientInvoke":
                    context.RequireNetworkSide("RemoteFunction.OnClientInvoke", false);
                    context.RequireMetadataMutation(instance, "set client invoke callback");
                    context.Bindings.WriteRemoteFunctionCallback(
                        context, remoteFunction, false, state, value);
                    context.RecordMutation(instance);
                    return true;
                default:
                    return false;
            }
        }

        private static LuaValue ReadRemoteFunctionInvokeBridge(
            LuaFunctionExecutionContext ctx, bool invokeServer)
        {
            LuaValue taskValue = ctx.State.Environment["task"];
            if (taskValue.Type != LuaValueType.Table)
            {
                throw RbxError.BadArgument(
                    "RemoteFunction invoke requires the task scheduler bridge",
                    "invoke from a loaded mod scheduler thread");
            }

            string bridgeName = invokeServer
                ? "_remoteFunctionInvokeServerBridge"
                : "_remoteFunctionInvokeClientBridge";
            LuaValue bridge = taskValue.Read<LuaTable>()[bridgeName];
            if (bridge.Type != LuaValueType.Function)
            {
                throw RbxError.BadArgument(
                    "RemoteFunction scheduler bridge is unavailable",
                    "invoke after the mod scheduler initializes");
            }

            return bridge;
        }

        private static LuaCsRbxMethodTable BuildMethods(LuaCsRbxModContext context)
        {
            LuaCsRbxMethodTable methods = new(context.Bindings.Registry.Catalog);

            // WHY argument numbers exclude self: VM slot 0 is the instance a colon call passes, and
            // Roblox numbers method arguments without it — `part:SetAttribute(5, true)` must blame
            // argument 1, the name, not the value the author then edits by mistake (M1-07).
            void Method(string name, Func<LuaFunctionExecutionContext, RbxInstance, LuaValue> body,
                string declaringClassName = null)
            {
                bool mutating = IsMutatingMethod(name);
                string operation = mutating ? "invoke Instance:" + name : null;
                LuaValue value = new(Fn("Instance." + name, ctx =>
                    {
                        RbxInstance self = Self(ctx, context);
                        ThrowIfDestroyedForLua(self, name);
                        if (!mutating)
                        {
                            return body(ctx, self);
                        }

                        return context.ApplyServerGeneratedMutation(
                            operation,
                            () => body(ctx, self));
                    }, context));
                methods.Add(name, value, declaringClassName);
            }

            // ---- Navigation ----
            Method("FindFirstChild", (ctx, self) => context.WrapInstance(self.FindFirstChild(
                ReadString(ctx, 1, "Instance:FindFirstChild", 1), Arg(ctx, 2).ToBoolean())));
            Method("FindFirstChildOfClass", (ctx, self) => context.WrapInstance(
                self.FindFirstChildOfClass(
                    ReadString(ctx, 1, "Instance:FindFirstChildOfClass", 1))));
            Method("FindFirstChildWhichIsA", (ctx, self) => context.WrapInstance(
                self.FindFirstChildWhichIsA(
                    ReadString(ctx, 1, "Instance:FindFirstChildWhichIsA", 1),
                    Arg(ctx, 2).ToBoolean())));
            Method("FindFirstAncestor", (ctx, self) => context.WrapInstance(
                self.FindFirstAncestor(ReadString(ctx, 1, "Instance:FindFirstAncestor", 1))));
            Method("FindFirstAncestorOfClass", (ctx, self) => context.WrapInstance(
                self.FindFirstAncestorOfClass(
                    ReadString(ctx, 1, "Instance:FindFirstAncestorOfClass", 1))));
            Method("FindFirstAncestorWhichIsA", (ctx, self) => context.WrapInstance(
                self.FindFirstAncestorWhichIsA(
                    ReadString(ctx, 1, "Instance:FindFirstAncestorWhichIsA", 1))));
            Method("GetChildren", (_, self) => WrapList(context, self.GetChildren()));
            Method("GetDescendants", (_, self) => WrapList(context, self.GetDescendants()));
            Method("IsA", (ctx, self) => self.IsA(ReadString(ctx, 1, "Instance:IsA", 1)));
            Method("IsDescendantOf", (ctx, self) => self.IsDescendantOf(
                ReadOptionalInstanceArgument(ctx, 1, "Instance:IsDescendantOf", 1)));
            Method("IsAncestorOf", (ctx, self) => self.IsAncestorOf(
                ReadOptionalInstanceArgument(ctx, 1, "Instance:IsAncestorOf", 1)));
            Method("GetFullName", (_, self) => self.GetFullName());

            // ---- Lifecycle ----
            Method("Clone", (_, self) =>
            {
                context.RequireWorldEdit("Instance:Clone");
                context.RequireMutationTarget(self, "clone");
                if (IsProtectedSingleton(self))
                {
                    // WHY: Roblox marks singletons non-archivable, so Clone yields nil here
                    // instead of a second live instance.
                    return LuaValue.Nil;
                }

                RbxInstance copy = self.Clone(context.OwnerModId, context.OriginTag);
                if (copy == null)
                {
                    return LuaValue.Nil;
                }

                try
                {
                    context.RecordMutation(self);
                    context.Bindings.Registry.SetAccessControl(copy, context.OwnerActorId,
                        InstanceAccessScope.Owned, true);
                    CopyPartSinkState(context.PartSink, self, copy);
                    return context.WrapInstance(copy);
                }
                catch
                {
                    copy.Destroy();
                    throw;
                }
            });
            Method("Destroy", (_, self) =>
            {
                if (self is RbxPlayer)
                {
                    // WHY: destroying a Player bypassed the leave teardown — no PlayerRemoving, the
                    // character stayed in the world, GetPlayers kept returning the dead Player and
                    // its actor could never get a live one back this session.
                    throw RbxError.BadArgument(
                        "Player cannot be destroyed from a script; a Player leaves only through "
                        + "Player:Kick()",
                        "call player:Kick() instead; PlayerRemoving fires and the character is "
                        + "cleaned up");
                }

                context.RequireDestroyTree(self, "destroy");
                if (!context.Bindings.Registry.IsWorldAclEnabled
                    && IsProtectedSingleton(self))
                {
                    // WHY: a shared service is cached once at composition and never re-resolved, so
                    // destroying it would brick input/lighting/etc for every mod; Roblox locks these
                    // against destruction too.
                    throw RbxError.BadArgument(
                        self.ClassName + " cannot be destroyed — it is a shared singleton",
                        "services, the DataModel and workspace.CurrentCamera live for the world's "
                        + "lifetime; never Destroy them");
                }

                self.Destroy();
                return LuaValue.Nil;
            });
            Method("ClearAllChildren", (_, self) =>
            {
                // WHY: game:ClearAllChildren() must not wipe the world's services (Roblox locks
                // them). GetChildren returns a snapshot, so destroying non-protected children while
                // iterating is safe; protected singletons (services/Camera) are left intact, and so
                // are Players, which leave only through Player:Kick() (see Destroy above).
                List<RbxInstance> destroyRoots = new();
                foreach (RbxInstance child in self.GetChildren())
                {
                    if (!IsProtectedSingleton(child) && !(child is RbxPlayer))
                    {
                        destroyRoots.Add(child);
                    }
                }

                context.RequireDestroyForest(self, destroyRoots, "clear descendants");
                for (int index = 0; index < destroyRoots.Count; index++)
                {
                    destroyRoots[index].Destroy();
                }

                return LuaValue.Nil;
            });

            // WHY: AddItem stays out of IsMutatingMethod on purpose — scheduling runs unenveloped
            // (authorization is the call-time ACL Demand inside RbxDebris) and the destroy takes
            // its own server-generated envelope when the timer fires, so the retained-operation
            // count proves the destroy went through an envelope.
            Method("AddItem", (ctx, self) =>
            {
                // WHY: scheduling a destroy IS a destroy, just later — WorldEdit is the capability
                // documented as "spawn, move, destroy", and without it a Read-tier mod could delete
                // anything the ACL lets its actor destroy.
                context.RequireWorldEdit("Debris:AddItem");
                RbxDebris debris = (RbxDebris)self;
                debris.EnsureHost(context.Bindings.Scheduler, context.Bindings.LogSink);
                RbxInstance item = ReadTargetInstance(Arg(ctx, 1), "Debris:AddItem", 1);
                LuaValue lifetimeValue = Arg(ctx, 2);
                double lifetime;
                if (lifetimeValue.Type == LuaValueType.Nil)
                {
                    lifetime = RbxDebris.DefaultLifetimeSeconds;
                }
                else if (lifetimeValue.Type == LuaValueType.Number)
                {
                    lifetime = lifetimeValue.Read<double>();
                }
                else
                {
                    throw ExpectedArgument("Debris:AddItem", "a number", lifetimeValue, 2);
                }

                // WHY: the caller identity is copied from the trusted ActorContext issued at mod
                // load — never from a Lua argument — so a script cannot schedule destruction as
                // another actor.
                debris.AddItem(item, lifetime, new DebrisCaller(
                    context.ActorContext.ActorId,
                    context.ActorContext.Grants.IsUnrestricted,
                    context.ActorContext.WorldId));
                return LuaValue.Nil;
            }, "Debris");

            // WHY host-gated: ScriptContext.yaml pins security: PluginSecurity and properties: [] —
            // an ordinary mod may not call this (matched via RequireUnrestricted, the same
            // RbxErrorCode.NotAuthority refusal shape as RequireNetworkSide) and there is no readable
            // Timeout property to invent. Only the wall-clock half changes: Roblox has no scriptable
            // instruction-budget equivalent, so that half stays composition-only — see
            // LuaCsCoroutineBudgetSettings.
            Method("SetTimeout", (ctx, self) =>
            {
                context.RequireUnrestricted("ScriptContext:SetTimeout");
                double seconds = ReadDouble(ctx, 1, "ScriptContext:SetTimeout", 1);
                if (double.IsNaN(seconds) || double.IsInfinity(seconds))
                {
                    throw RbxError.BadArgument(
                        "ScriptContext:SetTimeout expects a finite number of seconds at argument 1",
                        "pass a positive number of seconds, e.g. ScriptContext:SetTimeout(10)");
                }

                // WHY milliseconds, rounded: LuaCsCoroutineHandle's wall-clock budget is stored in
                // whole milliseconds; a non-positive result falls back to the documented default
                // rather than disabling the guard (LuaCsCoroutineBudgetSettings.ResumeTimeoutMs).
                double milliseconds = Math.Round(seconds * 1000d, MidpointRounding.AwayFromZero);

                // WHY an explicit ceiling before the cast: C# leaves an unchecked double-to-int cast
                // of an out-of-range value unspecified, and on x64 it produces int.MinValue — so a
                // finite but huge request such as SetTimeout(1e9) used to READ BACK as the short
                // default through the non-positive fallback above, the opposite of what the host
                // asked for, with no error. The caller is host code that can be fixed, so the request
                // is refused rather than clamped to int.MaxValue. The negative side has nothing to
                // refuse — every non-positive result already means "use the default" — so it is only
                // kept off that same unspecified cast.
                if (milliseconds > int.MaxValue)
                {
                    const double maxSeconds = int.MaxValue / 1000d;
                    throw RbxError.BadArgument(
                        "ScriptContext:SetTimeout expects at most "
                        + maxSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture)
                        + " seconds at argument 1, got "
                        + seconds.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        "pass a number of seconds no larger than "
                        + maxSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture)
                        + " (about 24.8 days), e.g. ScriptContext:SetTimeout(10)");
                }

                context.Bindings.CoroutineResumeBudget.SetResumeTimeoutMs(
                    (int)Math.Max(milliseconds, int.MinValue));
                return LuaValue.Nil;
            }, "ScriptContext");

            // WHY: Create/Play/Pause/Cancel stay out of IsMutatingMethod like AddItem —
            // Create authorizes at call time inside RbxTweenService, Play re-checks there, and
            // per-frame writes take no envelope (they converge to the authorized goals), so
            // none of them run inside the per-call server-generated mutation envelope.
            Method("Create", (ctx, self) =>
            {
                // WHY: a tween moves, recolours and resizes parts frame by frame — the same writes
                // a property assignment gates on WorldEdit; Play/Pause/Cancel re-check it below.
                context.RequireWorldEdit("TweenService:Create");
                RbxTweenService tweenService = (RbxTweenService)self;
                tweenService.EnsureHost(context.Bindings.Scheduler,
                    context.Bindings.TweenPropertyHost,
                    context.Bindings.ResolvePlaybackStateItem,
                    context.Bindings.LogSink);
                RbxInstance target =
                    ReadTargetInstance(Arg(ctx, 1), "TweenService:Create", 1);
                RbxTweenInfo info = LuaCsRbxDatatypeBindings.ReadTweenInfo(
                    Arg(ctx, 2), "TweenService:Create", 2);
                List<KeyValuePair<string, object>> goals =
                    ReadPropertyTable(Arg(ctx, 3));
                RbxTween tween = tweenService.Create(target, info, goals,
                    CreateTweenCaller(context));
                return context.WrapInstance(tween);
            }, "TweenService");
            Method("GetValue", (ctx, self) =>
            {
                double alpha = ReadDouble(ctx, 1, "TweenService:GetValue", 1);
                if (double.IsNaN(alpha) || double.IsInfinity(alpha))
                {
                    throw RbxError.BadArgument(
                        "TweenService:GetValue expects a finite alpha at argument 1",
                        "pass an interpolation value between 0 and 1 at argument 1");
                }

                RbxEasingStyle style = ReadEasingStyle(Arg(ctx, 2));
                RbxEasingDirection direction = ReadEasingDirection(Arg(ctx, 3));
                return RbxTweenService.GetValue(alpha, style, direction);
            }, "TweenService");
            Method("SmoothDamp", (_, _) =>
            {
                throw RbxError.NotImplemented(
                    "TweenService:SmoothDamp",
                    "a later MVP",
                    "interpolate manually with TweenService:GetValue over RunService.Heartbeat");
            }, "TweenService");
            // WHY the caller-aware overloads: a tween reference can travel between actors (an
            // ObjectValue, a module export), and whoever calls Play/Pause/Cancel must hold the
            // write right over the target — not the actor that happened to create the tween.
            Method("Play", (_, self) =>
            {
                context.RequireWorldEdit("Tween:Play");
                ((RbxTween)self).Play(CreateTweenCaller(context));
                return LuaValue.Nil;
            }, "Tween");
            Method("Pause", (_, self) =>
            {
                context.RequireWorldEdit("Tween:Pause");
                ((RbxTween)self).Pause(CreateTweenCaller(context));
                return LuaValue.Nil;
            }, "Tween");
            Method("Cancel", (_, self) =>
            {
                context.RequireWorldEdit("Tween:Cancel");
                ((RbxTween)self).Cancel(CreateTweenCaller(context));
                return LuaValue.Nil;
            }, "Tween");

            // ---- Attributes / tags ----
            Method("GetAttribute", (ctx, self) => AttributeToLua(
                self.GetAttribute(ReadString(ctx, 1, "Instance:GetAttribute", 1))));
            Method("SetAttribute", (ctx, self) =>
            {
                context.RequireMetadataMutation(self, "set attribute");
                string attributeName = ReadString(ctx, 1, "Instance:SetAttribute", 1);
                object attributeValue = AttributeFromLua(Arg(ctx, 2));
                // WHY the mirror's ASCII rule only for a name this call creates: a world saved
                // before the rule may hold a non-ASCII name, and its scripts must still be able to
                // update and remove it; only a script creating a new one is refused.
                if (attributeValue != null && self.GetAttribute(attributeName) == null)
                {
                    AttributeContract.ValidateNewName(attributeName);
                }

                self.SetAttribute(attributeName, attributeValue);
                return LuaValue.Nil;
            });
            Method("GetAttributes", (_, self) =>
            {
                LuaTable table = new();
                foreach (KeyValuePair<string, object> pair in self.GetAttributes())
                {
                    table[pair.Key] = AttributeToLua(pair.Value);
                }

                return new LuaValue(table);
            });
            Method("AddTag", (ctx, self) =>
            {
                if (self is RbxCollectionService addTagService)
                {
                    addTagService.EnsureHost(context.Bindings.Scheduler);
                    RbxInstance addTagTarget =
                        ReadTargetInstance(Arg(ctx, 1), "CollectionService:AddTag", 1);
                    string addTagName = ReadString(ctx, 2, "CollectionService:AddTag", 2);
                    context.RequireMetadataMutation(addTagTarget, "add tag");
                    addTagService.AddTag(addTagTarget, addTagName);
                    return LuaValue.Nil;
                }

                context.RequireMetadataMutation(self, "add tag");
                self.AddTag(ReadString(ctx, 1, "Instance:AddTag", 1));
                return LuaValue.Nil;
            });
            Method("RemoveTag", (ctx, self) =>
            {
                if (self is RbxCollectionService removeTagService)
                {
                    removeTagService.EnsureHost(context.Bindings.Scheduler);
                    RbxInstance removeTagTarget =
                        ReadTargetInstance(Arg(ctx, 1), "CollectionService:RemoveTag", 1);
                    string removeTagName = ReadString(ctx, 2, "CollectionService:RemoveTag", 2);
                    context.RequireMetadataMutation(removeTagTarget, "remove tag");
                    removeTagService.RemoveTag(removeTagTarget, removeTagName);
                    return LuaValue.Nil;
                }

                context.RequireMetadataMutation(self, "remove tag");
                self.RemoveTag(ReadString(ctx, 1, "Instance:RemoveTag", 1));
                return LuaValue.Nil;
            });
            Method("HasTag", (ctx, self) =>
            {
                if (self is RbxCollectionService hasTagService)
                {
                    hasTagService.EnsureHost(context.Bindings.Scheduler);
                    RbxInstance hasTagTarget =
                        ReadTargetInstance(Arg(ctx, 1), "CollectionService:HasTag", 1);
                    return hasTagService.HasTag(
                        hasTagTarget, ReadString(ctx, 2, "CollectionService:HasTag", 2));
                }

                return self.HasTag(ReadString(ctx, 1, "Instance:HasTag", 1));
            });
            Method("GetTags", (ctx, self) =>
            {
                if (self is RbxCollectionService getTagsService)
                {
                    getTagsService.EnsureHost(context.Bindings.Scheduler);
                    RbxInstance getTagsTarget =
                        ReadTargetInstance(Arg(ctx, 1), "CollectionService:GetTags", 1);
                    LuaTable serviceTags = new();
                    int serviceTagsIndex = 1;
                    foreach (string serviceTag in getTagsService.GetTags(getTagsTarget))
                    {
                        serviceTags[serviceTagsIndex++] = serviceTag;
                    }

                    return new LuaValue(serviceTags);
                }

                LuaTable table = new();
                int index = 1;
                foreach (string tag in self.GetTags())
                {
                    table[index++] = tag;
                }

                return new LuaValue(table);
            });
            Method("GetTagged", (ctx, self) =>
            {
                RbxCollectionService taggedService = (RbxCollectionService)self;
                taggedService.EnsureHost(context.Bindings.Scheduler);
                return WrapList(context, taggedService.GetTagged(
                    ReadString(ctx, 1, "CollectionService:GetTagged", 1)));
            }, "CollectionService");
            Method("GetAllTags", (_, self) =>
            {
                RbxCollectionService allTagsService = (RbxCollectionService)self;
                allTagsService.EnsureHost(context.Bindings.Scheduler);
                LuaTable allTags = new();
                int allTagsIndex = 1;
                foreach (string tag in allTagsService.GetAllTags())
                {
                    allTags[allTagsIndex++] = tag;
                }

                return new LuaValue(allTags);
            }, "CollectionService");
            Method("GetInstanceAddedSignal", (ctx, self) =>
            {
                RbxCollectionService addedSignalService = (RbxCollectionService)self;
                addedSignalService.EnsureHost(context.Bindings.Scheduler);
                return LuaCsRbxDatatypeBindings.Wrap(
                    addedSignalService.GetInstanceAddedSignal(
                        ReadString(ctx, 1, "CollectionService:GetInstanceAddedSignal", 1)),
                    context);
            }, "CollectionService");
            Method("GetInstanceRemovedSignal", (ctx, self) =>
            {
                RbxCollectionService removedSignalService = (RbxCollectionService)self;
                removedSignalService.EnsureHost(context.Bindings.Scheduler);
                return LuaCsRbxDatatypeBindings.Wrap(
                    removedSignalService.GetInstanceRemovedSignal(
                        ReadString(ctx, 1, "CollectionService:GetInstanceRemovedSignal", 1)),
                    context);
            }, "CollectionService");
            Method("GetAttributeChangedSignal", (ctx, self) => LuaCsRbxDatatypeBindings.Wrap(
                self.GetAttributeChangedSignal(
                    ReadString(ctx, 1, "Instance:GetAttributeChangedSignal", 1)), context));
            Method("GetPropertyChangedSignal", (ctx, self) =>
            {
                string property = ReadString(ctx, 1, "Instance:GetPropertyChangedSignal", 1);
                RequireKnownPropertyName(context, self, property);
                return LuaCsRbxDatatypeBindings.Wrap(self.GetPropertyChangedSignal(property), context);
            });

            Method("GetPivot", (_, self) => LuaCsRbxDatatypeBindings.Wrap(
                GetPivot(context, self)), "PVInstance");
            Method("PivotTo", (ctx, self) =>
            {
                context.RequirePivotMutation(self);
                PivotTo(context, self, ReadPartCFrameArgument(ctx, 1, "PVInstance:PivotTo", 1));
                return LuaValue.Nil;
            }, "PVInstance");

            // ---- ServiceProvider (DataModel) ----
            Method("GetService", (ctx, self) => context.WrapInstance(
                    RequireDataModel(self, "GetService").GetService(
                        ReadString(ctx, 1, "game:GetService", 1))),
                "ServiceProvider");
            Method("FindService", (ctx, self) => context.WrapInstance(
                    RequireDataModel(self, "FindService").FindService(
                        ReadString(ctx, 1, "game:FindService", 1))),
                "ServiceProvider");
            Method("BindToClose", (ctx, self) =>
            {
                LuaValue callback = Arg(ctx, 1);
                if (callback.Type != LuaValueType.Function)
                {
                    throw ExpectedArgument("game:BindToClose", "a function", callback, 1);
                }

                RequireDataModel(self, "BindToClose").BindToClose(callback.Read<LuaFunction>());
                return LuaValue.Nil;
            }, "DataModel");
            // WHY always true: a mod's chunk only runs against a world that has finished loading,
            // so the Roblox loading guard answers "loaded" and game.Loaded never has to fire.
            Method("IsLoaded", (_, _) => true, "DataModel");

            Method("GetPlayers", (_, self) => WrapList(
                context, ((RbxPlayers)self).GetPlayers()), "Players");
            Method("GetPlayerByUserId", (ctx, self) => context.WrapInstance(
                    ((RbxPlayers)self).GetPlayerByUserId(ReadUserId(ctx))), "Players");
            Method("GetPlayerFromCharacter", (ctx, self) =>
            {
                LuaValue characterArg = Arg(ctx, 1);
                if (characterArg.Type == LuaValueType.Nil)
                {
                    return LuaValue.Nil;
                }

                if (!TryGetInstance(characterArg, out LuaCsRbxInstanceProxy characterProxy))
                {
                    throw ExpectedArgument("Players:GetPlayerFromCharacter",
                        "a character Model or nil", characterArg, 1);
                }

                return context.WrapInstance(
                    ((RbxPlayers)self).GetPlayerFromCharacter(characterProxy.Instance));
            }, "Players");
            Method("Kick", (ctx, self) =>
            {
                RbxPlayer player = (RbxPlayer)self;
                LuaValue messageArg = Arg(ctx, 1);
                if (messageArg.Type != LuaValueType.Nil)
                {
                    ReadString(ctx, 1, "Player:Kick", 1);
                }

                // WHY: kicking destroys the player's whole subtree (Player + empty containers),
                // so it authorizes exactly like Destroy: the host kicks anyone, an actor kicks
                // its own player, and a cross-actor kick by a plain actor is refused. The
                // message is validated above and dropped — headless runtime has no surface that
                // could present it to the kicked user.
                context.RequireDestroyTree(player, "kick");
                context.Bindings.KickPlayerWithCreatorKick(player);
                return LuaValue.Nil;
            }, "Player");

            Method("DistanceFromCharacter", (ctx, self) =>
            {
                RbxVector3 point = ReadVector3(ctx, 1, "Player:DistanceFromCharacter", 1);
                return ((RbxPlayer)self).DistanceFromCharacter(point);
            }, "Player");

            // WHY the three Humanoid mutators authorize exactly like the property each one writes
            // (Health, WalkToPoint, Jump) and run enveloped (IsMutatingMethod): as bare calls they
            // let any actor kill, heal, walk or launch a character owned by another actor — and a
            // Read-tier mod do it at all — while `humanoid.Health = 0` was refused.
            Method("TakeDamage", (ctx, self) =>
            {
                context.RequireWorldEditForWrite(self, "Health");
                ((RbxHumanoid)self).TakeDamage(ReadDouble(ctx, 1, "Humanoid:TakeDamage", 1));
                context.RecordMutation(self);
                return LuaValue.Nil;
            }, "Humanoid");
            Method("MoveTo", (ctx, self) =>
            {
                context.RequireWorldEditForWrite(self, "WalkToPoint");
                // The mirror's second argument is a part to follow; following a moving target needs
                // the character rig, so it is refused rather than silently ignored.
                if (Arg(ctx, 2).Type != LuaValueType.Nil)
                {
                    throw RbxError.BadArgument(
                        "Humanoid:MoveTo part following is not implemented",
                        "pass only the destination Vector3; re-issue MoveTo as the target moves");
                }

                ((RbxHumanoid)self).MoveTo(ReadVector3(ctx, 1, "Humanoid:MoveTo", 1));
                return LuaValue.Nil;
            }, "Humanoid");
            Method("GetState", (_, self) =>
                LuaCsRbxDatatypeBindings.Wrap(ResolveHumanoidStateItem(
                    context, ((RbxHumanoid)self).GetState())), "Humanoid");
            Method("ChangeState", (ctx, self) =>
            {
                context.RequireWorldEditForWrite(self, "Jump");
                // WHY only Jumping: it is the one state a script can legitimately force without a
                // rig. Anything else would be a state the machine never leaves, so it says so.
                RbxEnumItem requested = ReadHumanoidStateItem(Arg(ctx, 1));
                if (requested.Value != (int)RbxHumanoidState.Jumping)
                {
                    throw new RbxApiStubException(
                        "NOT_IMPLEMENTED",
                        "Humanoid:ChangeState(Enum.HumanoidStateType." + requested.Name
                        + ") is not implemented; CoreAI's character is a motor, not a full R15 rig",
                        "force only Enum.HumanoidStateType.Jumping, or read Humanoid:GetState()");
                }

                ((RbxHumanoid)self).RequestJump();
                return LuaValue.Nil;
            }, "Humanoid");

            // WHY declared on WorldRoot and not Workspace: the mirror puts Raycast on WorldRoot, so
            // any future WorldModel gets it from the same declaration rather than a copy.
            Method("Raycast", (ctx, self) =>
            {
                RbxVector3 origin = ReadVector3(ctx, 1, "WorldRoot:Raycast", 1);
                RbxVector3 direction = ReadVector3(ctx, 2, "WorldRoot:Raycast", 2);
                LuaValue paramsArgument = Arg(ctx, 3);
                RbxRaycastParams raycastParams = paramsArgument.Type == LuaValueType.Nil
                    ? null
                    : ReadRaycastParams(paramsArgument);

                RbxRaycastResult result =
                    context.Bindings.WorldPhysics.Raycast(origin, direction, raycastParams);
                return result == null ? LuaValue.Nil : WrapRaycastResult(context, result);
            }, "WorldRoot");

            // WHY: the server clock is unscaled and monotonic-smoothed (it never steps back over
            // NTP/system-clock corrections); gameplay timing still belongs on task.wait and time().
            Method("GetServerTimeNow", (_, self) =>
                context.Bindings.GetServerTimeNow(), "Workspace");

            // WHY: topology answers come from the instance's IRbxRuntimeTopology (solo by default),
            // never from literals here, so the host/client slice swaps the source, not the binding.
            Method("IsServer", (_, self) => ((RbxRunService)self).Topology.IsServer, "RunService");
            Method("IsClient", (_, self) => ((RbxRunService)self).Topology.IsClient, "RunService");
            Method("IsStudio", (_, self) => ((RbxRunService)self).Topology.IsStudio, "RunService");
            Method("IsRunning", (_, self) => ((RbxRunService)self).Topology.IsRunning, "RunService");

            Method("FireServer", (ctx, self) =>
            {
                context.RequireNetworkSide(self.ClassName + ":FireServer", false);
                context.Bindings.FireRemoteServer(
                    context, (RbxRemoteEvent)self, ctx);
                return LuaValue.Nil;
            }, "BaseRemoteEvent");
            Method("FireClient", (ctx, self) =>
            {
                context.RequireNetworkSide(self.ClassName + ":FireClient", true);
                context.Bindings.FireRemoteClient(
                    (RbxRemoteEvent)self,
                    ReadPlayer(ctx, 1, self.ClassName + ":FireClient"), ctx);
                return LuaValue.Nil;
            }, "BaseRemoteEvent");
            Method("FireAllClients", (ctx, self) =>
            {
                context.RequireNetworkSide(self.ClassName + ":FireAllClients", true);
                context.Bindings.FireRemoteAllClients((RbxRemoteEvent)self, ctx);
                return LuaValue.Nil;
            }, "BaseRemoteEvent");

            return methods;
        }

        private static bool IsMutatingMethod(string name)
        {
            return name == "Clone"
                   || name == "Destroy"
                   || name == "ClearAllChildren"
                   || name == "SetAttribute"
                   || name == "AddTag"
                   || name == "RemoveTag"
                   || name == "PivotTo"
                   || name == "Kick"
                   || name == "TakeDamage"
                   || name == "MoveTo"
                   || name == "ChangeState";
        }

        private static long ReadUserId(LuaFunctionExecutionContext ctx)
        {
            double rawUserId = ReadDouble(ctx, 1, "Players:GetPlayerByUserId", 1);
            if (double.IsNaN(rawUserId) || double.IsInfinity(rawUserId))
            {
                throw RbxError.BadArgument(
                    "Players:GetPlayerByUserId expects a finite UserId at argument 1, got "
                    + rawUserId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    "pass the numeric UserId, e.g. Players:GetPlayerByUserId(player.UserId)");
            }

            return (long)rawUserId;
        }

        /// <summary>A Player argument of a method; <paramref name="index"/> is both the VM slot
        /// and the author's argument number, since slot 0 is self.</summary>
        private static RbxPlayer ReadPlayer(LuaFunctionExecutionContext ctx,
            int index, string functionName)
        {
            LuaValue value = Arg(ctx, index);
            if (TryGetInstance(value, out LuaCsRbxInstanceProxy proxy)
                && proxy.Instance is RbxPlayer player)
            {
                return player;
            }

            throw ExpectedArgument(functionName, "a Player", value, index);
        }

        private static RbxInstance Self(LuaFunctionExecutionContext ctx, LuaCsRbxModContext context)
        {
            if (TryGetInstance(Arg(ctx, 0), out LuaCsRbxInstanceProxy proxy))
            {
                return proxy.Instance;
            }

            throw RbxError.BadArgument(
                "Instance member access expects an Instance as self",
                "call instance methods with a colon, e.g. workspace:FindFirstChild(\"Part\")");
        }

        // WHY: services (UserInputService/Lighting/Workspace/…), the canonical Camera and the
        // DataModel itself are world-lifetime singletons; the lifecycle bindings refuse to
        // Clone/Destroy/reparent them so one mod cannot brick a shared service for every other mod.
        // The DataModel is listed on its own because its descriptor is not a service: without it
        // game:Clone() duplicated the whole world and game:Destroy() tore it down in an ACL-off world.
        private static bool IsProtectedSingleton(RbxInstance instance)
        {
            return instance.IsService || instance is RbxDataModel || instance.ClassName == "Camera";
        }

        /// <summary>game.Loaded per DataModel; weak so a torn-down world takes its signal with it.</summary>
        private static readonly ConditionalWeakTable<RbxDataModel, RbxScriptSignal> LoadedSignals =
            new();

        /// <summary>
        /// Every property the Lua member dispatch answers, by the class that declares it. Methods,
        /// events and children are not properties, so GetPropertyChangedSignal refuses them.
        /// </summary>
        private static readonly (string ClassName, HashSet<string> Properties)[] BoundProperties =
        {
            ("Instance", Names("Name", "ClassName", "Parent", "Archivable")),
            ("Workspace", Names("Gravity", "CurrentCamera", "SignalBehavior")),
            ("Model", Names("PrimaryPart", "WorldPivot")),
            ("BasePart", Names("Shape", "Material", "MaterialVariant", "Position", "Size", "CFrame",
                "Orientation", "Rotation", "Color", "Transparency", "Anchored", "CanCollide")),
            ("Camera", Names("CFrame", "CameraType", "CameraSubject")),
            ("Humanoid", Names("Health", "MaxHealth", "WalkSpeed", "JumpPower", "JumpHeight",
                "UseJumpPower", "DisplayName", "MoveDirection", "RootPart", "Jump")),
            ("Player", Names("UserId", "DisplayName", "Character")),
            ("Players", Names("LocalPlayer", "CharacterAutoLoads", "RespawnTime", "MaxPlayers")),
            ("UserInputService", Names("MouseBehavior")),
            ("ClickDetector", Names("MaxActivationDistance")),
            ("MaterialVariant", Names("BaseMaterial", "ColorMap", "NormalMap", "RoughnessMap",
                "MetalnessMap", "StudsPerTile")),
            ("ValueBase", Names("Value")),
            ("Tween", Names("Instance", "TweenInfo", "PlaybackState"))
        };

        private static HashSet<string> Names(params string[] names)
        {
            return new HashSet<string>(names, StringComparer.Ordinal);
        }

        /// <summary>
        /// Every (declaring class, property) pair GetPropertyChangedSignal accepts as a bound
        /// property, in table order. The drift guard in the acceptance tests compares it with what
        /// the Lua member read actually answers.
        /// </summary>
        internal static IReadOnlyList<(string ClassName, string Property)> EnumerateBoundProperties()
        {
            List<(string ClassName, string Property)> pairs = new();
            for (int index = 0; index < BoundProperties.Length; index++)
            {
                foreach (string property in BoundProperties[index].Properties)
                {
                    pairs.Add((BoundProperties[index].ClassName, property));
                }
            }

            return pairs;
        }

        /// <summary>
        /// Refuses a GetPropertyChangedSignal name that is neither a bound property nor a catalogued
        /// real property of the instance's class. The catalog counts because a real Roblox property
        /// CoreAI has not bound yet is still a valid name — the author is not told it made a typo.
        /// </summary>
        /// <remarks>
        /// WHY refused at all: every distinct string used to mint a new signal that never fired, so a
        /// typo (<c>"position"</c>) silently did nothing and each one grew the instance's signal
        /// table (M1-03).
        /// </remarks>
        private static void RequireKnownPropertyName(LuaCsRbxModContext context, RbxInstance instance,
            string property)
        {
            if (string.IsNullOrEmpty(property))
            {
                return;
            }

            for (int index = 0; index < BoundProperties.Length; index++)
            {
                if (BoundProperties[index].Properties.Contains(property)
                    && instance.IsA(BoundProperties[index].ClassName))
                {
                    return;
                }
            }

            ClassCatalog catalog = context.Bindings.Registry.Catalog;
            if (IsCataloguedProperty(catalog, instance, property,
                    RbxKnownUnimplementedMemberAccess.Read)
                || IsCataloguedProperty(catalog, instance, property,
                    RbxKnownUnimplementedMemberAccess.Write))
            {
                return;
            }

            throw RbxError.BadArgument(
                property + " is not a valid property name.",
                "pass the exact name of a " + instance.ClassName
                + " property, e.g. GetPropertyChangedSignal(\"Name\"); property names are "
                + "case-sensitive");
        }

        private static bool IsCataloguedProperty(ClassCatalog catalog, RbxInstance instance,
            string property, RbxKnownUnimplementedMemberAccess access)
        {
            return catalog.TryGetKnownUnimplementedMember(instance.ClassName, property, access,
                       out _, out RbxKnownUnimplementedMemberDescriptor descriptor)
                   && !descriptor.IsMethod;
        }

        /// <summary>
        /// Bound members a script may read but never assign, answered with the mirror's read-only
        /// error rather than "not a valid member" (which reads like a typo).
        /// </summary>
        private static bool IsReadOnlyMember(RbxInstance instance, string key)
        {
            switch (key)
            {
                case "ClassName":
                    return true;
                case "MoveDirection":
                case "RootPart":
                    return instance is RbxHumanoid;
                case "UserId":
                    return instance is RbxPlayer;
                case "LocalPlayer":
                    return instance is RbxPlayers;
                case "Instance":
                case "TweenInfo":
                case "PlaybackState":
                    return instance is RbxTween;
                default:
                    return false;
            }
        }

        /// <summary>Enforces DEV-7, including the destruction-handler tombstone exception.</summary>
        /// <param name="memberRead">
        /// True on the <c>__index</c> path, where the member is being READ. Reads of the instance a
        /// destruction handler was handed are permitted; writes and method calls never are.
        /// </param>
        /// <remarks>
        /// WHY reads are wider than the three navigation members: the mirror documents
        /// <c>Players.PlayerRemoving</c> as firing "right before a Player leaves… useful for
        /// storing player data using a GlobalDataStore", and a DataStore write needs
        /// <c>player.UserId</c> — the key. With only Name/ClassName/Parent readable, the canonical
        /// save-on-leave handler raised INSTANCE_DESTROYED on its first line, and because signal
        /// callbacks report faults through the mod logger instead of throwing, the handler simply
        /// did nothing. The narrowness stays where it matters: the exception covers reads only,
        /// only inside a destruction handler, and only for the instance that handler was given.
        /// </remarks>
        private static void ThrowIfDestroyedForLua(RbxInstance instance, string memberName,
            bool memberRead = false)
        {
            bool tombstoneMember = memberRead
                                   || memberName == "Name"
                                   || memberName == "ClassName"
                                   || memberName == "Parent";
            if (instance.IsDestroyed
                && !(tombstoneMember && RbxScriptSignal.CanReadTombstone(instance)))
            {
                throw RbxError.InstanceDestroyed(memberName, instance.Name, instance.Id);
            }
        }

        private static void ThrowIfStubServiceForLua(RbxInstance instance, string memberName)
        {
            if (instance is RbxStubService stubService)
            {
                throw stubService.MemberAccessError(memberName);
            }
        }

        // WHY: Clone deep-copies identity/attributes/tags, but BasePart spatial/appearance state
        // lives in the external part sink keyed by id (D2 keeps RbxInstance engine-free) and must
        // be walked and copied separately; Clone preserves the order of the children it copies, so
        // the trees align as long as this walk skips exactly the children Clone skips.
        // TODO: MVP2 — move this sink-copy into a registry-level clone seam so completeness no
        // longer depends on each Clone call site (the registry already owns the binder/sink).
        private static void CopyPartSinkState(IPartPropertySink sink, RbxInstance source,
            RbxInstance copy)
        {
            if (sink == null || source == null || copy == null)
            {
                return;
            }

            // WHY iterative: a live tree may be as deep as the snapshot depth cap, and a recursive
            // walk over it is the stack overflow CORE-A removed from Clone itself (M1-13).
            Stack<KeyValuePair<RbxInstance, RbxInstance>> pending = new();
            pending.Push(new KeyValuePair<RbxInstance, RbxInstance>(source, copy));
            while (pending.Count > 0)
            {
                KeyValuePair<RbxInstance, RbxInstance> pair = pending.Pop();
                if (sink.TryGetPartProperties(pair.Key.Id, out PartProperties properties))
                {
                    sink.SetPartProperties(pair.Value.Id, in properties);
                }

                IReadOnlyList<RbxInstance> sourceChildren = pair.Key.GetChildren();
                IReadOnlyList<RbxInstance> copyChildren = pair.Value.GetChildren();
                int copyIndex = 0;
                for (int index = 0; index < sourceChildren.Count && copyIndex < copyChildren.Count;
                     index++)
                {
                    if (!IsClonedChild(sourceChildren[index]))
                    {
                        continue;
                    }

                    pending.Push(new KeyValuePair<RbxInstance, RbxInstance>(
                        sourceChildren[index], copyChildren[copyIndex]));
                    copyIndex++;
                }
            }
        }

        /// <summary>
        /// The children <see cref="RbxInstance.Clone()"/> copies: archivable ones that are not a
        /// service or the DataModel. A non-archivable child and a world singleton have no
        /// counterpart in the copy, so the walk advances only the source side past them.
        /// </summary>
        private static bool IsClonedChild(RbxInstance child)
        {
            return child.Archivable && !child.IsService && !(child is RbxDataModel);
        }

        private static RbxDataModel RequireDataModel(RbxInstance instance, string member)
        {
            if (instance is RbxDataModel dataModel)
            {
                return dataModel;
            }

            throw RbxError.BadArgument(
                member + " is not a valid member of " + instance.ClassName
                + " \"" + instance.GetFullName() + "\"",
                "call " + member + " on the game DataModel, e.g. game:" + member + "(...)");
        }

        private static RbxError GetKnownUnimplementedMemberError(LuaCsRbxModContext context,
            RbxInstance instance, string memberName, RbxKnownUnimplementedMemberAccess access)
        {
            if (!context.Bindings.Registry.Catalog.TryGetKnownUnimplementedMember(
                    instance.ClassName, memberName, access,
                    out string declaringClassName,
                    out RbxKnownUnimplementedMemberDescriptor descriptor))
            {
                return null;
            }

            string separator = descriptor.IsMethod ? ":" : ".";
            string feature = declaringClassName + separator + memberName;
            switch (descriptor.Status)
            {
                case RbxKnownUnimplementedMemberStatus.Planned:
                    return RbxError.NotImplemented(
                        feature, descriptor.Phase, descriptor.Workaround);
                case RbxKnownUnimplementedMemberStatus.Backlog:
                    return new RbxError(
                        RbxErrorCode.NotImplemented,
                        feature + " is a known Rbx member, but no roadmap rung is assigned.",
                        descriptor.Workaround);
                case RbxKnownUnimplementedMemberStatus.Unsupported:
                    return new RbxError(
                        RbxErrorCode.NotImplemented,
                        feature + " is a known Rbx member deliberately unsupported by CoreAI.",
                        descriptor.Workaround);
                default:
                    throw new ArgumentOutOfRangeException(
                        nameof(descriptor.Status), descriptor.Status, null);
            }
        }

        private static RbxEnumItem EnsureDeferredSignalBehavior(RbxEnumRegistry enums)
        {
            if (enums.TryGet("SignalBehavior", out RbxEnum signalBehavior)
                && signalBehavior.TryGetItem("Deferred", out RbxEnumItem deferred))
            {
                return deferred;
            }

            signalBehavior = new RbxEnum("SignalBehavior",
                ("Default", 0), ("Immediate", 1), ("Deferred", 2), ("AncestryDeferred", 3));
            enums.Register(signalBehavior);
            return signalBehavior["Deferred"];
        }

        /// <summary>An Instance-or-nil method argument; nil reads as null.</summary>
        private static RbxInstance ReadOptionalInstanceArgument(LuaFunctionExecutionContext ctx,
            int index, string what, int argumentNumber)
        {
            LuaValue value = Arg(ctx, index);
            if (value.Type == LuaValueType.Nil)
            {
                return null;
            }

            if (TryGetInstance(value, out LuaCsRbxInstanceProxy proxy))
            {
                return proxy.Instance;
            }

            throw ExpectedArgument(what, "an Instance or nil", value, argumentNumber);
        }

        /// <summary>An Instance-or-nil property write (Parent, Character, PrimaryPart, ...).</summary>
        private static RbxInstance ReadAssignedOptionalInstance(LuaValue value, string ownerName,
            string property)
        {
            if (value.Type == LuaValueType.Nil)
            {
                return null;
            }

            if (TryGetInstance(value, out LuaCsRbxInstanceProxy proxy))
            {
                return proxy.Instance;
            }

            throw PropertyAssignmentError(ownerName, property, "an Instance or nil", value);
        }

        /// <summary>
        /// Reads the Instance a service method acts on (argument 1 after self, e.g.
        /// CollectionService:AddTag, Debris:AddItem); destroyed proxies are rejected by the service
        /// call itself, so only shape is checked here.
        /// </summary>
        private static RbxInstance ReadTargetInstance(LuaValue value, string what,
            int argumentNumber)
        {
            if (TryGetInstance(value, out LuaCsRbxInstanceProxy proxy)
                && proxy.Instance != null)
            {
                return proxy.Instance;
            }

            throw ExpectedArgument(what, "an Instance", value, argumentNumber);
        }

        private static LuaValue WrapList(LuaCsRbxModContext context,
            IReadOnlyList<RbxInstance> instances)
        {
            LuaTable table = new();
            for (int i = 0; i < instances.Count; i++)
            {
                table[i + 1] = context.WrapInstance(instances[i]);
            }

            return new LuaValue(table);
        }

        private static LuaValue AttributeToLua(object value)
        {
            switch (value)
            {
                case null: return LuaValue.Nil;
                case string s: return s;
                case bool b: return b;
                case double d: return d;
                case RbxVector3 v3: return LuaCsRbxDatatypeBindings.Wrap(v3);
                case RbxVector2 v2: return LuaCsRbxDatatypeBindings.Wrap(v2);
                case RbxColor3 c: return LuaCsRbxDatatypeBindings.Wrap(c);
                case RbxUDim u: return LuaCsRbxDatatypeBindings.Wrap(u);
                default: return LuaValue.Nil;
            }
        }

        private static object AttributeFromLua(LuaValue value)
        {
            switch (value.Type)
            {
                case LuaValueType.Nil: return null;
                case LuaValueType.Boolean: return value.Read<bool>();
                case LuaValueType.Number: return value.Read<double>();
                case LuaValueType.String: return value.Read<string>();
                default:
                    // WHY: only the datatype subset the attribute contract serializes is accepted;
                    // other userdata/tables/functions are rejected here.
                    if (TryUnbox(value, out RbxVector3 v3))
                    {
                        return v3;
                    }

                    if (TryUnbox(value, out RbxVector2 v2))
                    {
                        return v2;
                    }

                    if (TryUnbox(value, out RbxColor3 c))
                    {
                        return c;
                    }

                    if (TryUnbox(value, out RbxUDim u))
                    {
                        return u;
                    }

                    throw RbxError.BadArgument(
                        "attribute value of type " + Describe(value) + " is not supported",
                        "pass a string, boolean, number, Vector3, Vector2, Color3, or UDim at argument 2");
            }
        }

        // ---- BasePart spatial/appearance (part-property sink) -------------------------------

        private static bool TryReadModelPivot(LuaCsRbxModContext context, RbxInstance self,
            string key, out LuaValue value)
        {
            if (!(self is RbxModel model))
            {
                value = LuaValue.Nil;
                return false;
            }

            switch (key)
            {
                case "PrimaryPart":
                    value = context.WrapInstance(model.PrimaryPart);
                    return true;
                case "WorldPivot":
                    value = LuaCsRbxDatatypeBindings.Wrap(
                        GetWorldPivot(context.PartSink, model));
                    return true;
                default:
                    value = LuaValue.Nil;
                    return false;
            }
        }

        private static bool TryWriteModelPivot(LuaCsRbxModContext context, RbxInstance self,
            string key, LuaValue value)
        {
            if (!(self is RbxModel model))
            {
                return false;
            }

            switch (key)
            {
                case "PrimaryPart":
                    context.RequireWorldEditForWrite(self, "PrimaryPart");
                    model.SetPrimaryPart(ReadAssignedOptionalInstance(
                        value, self.ClassName, "PrimaryPart"));
                    return true;
                case "WorldPivot":
                    context.RequireWorldEditForWrite(self, "WorldPivot");
                    RbxCFrame worldPivot = ReadAssignedPartCFrame(
                        value, self.ClassName, "WorldPivot");
                    model.SetWorldPivot(in worldPivot);
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>
        /// Mirror <c>PVInstance:GetPivot</c>: a part's CFrame, a Model's PrimaryPart CFrame or
        /// WorldPivot, and the camera's CFrame (Camera inherits PVInstance, M1-21).
        /// </summary>
        private static RbxCFrame GetPivot(LuaCsRbxModContext context, RbxInstance instance)
        {
            IPartPropertySink sink = context.PartSink;
            if (instance.IsA("BasePart"))
            {
                return sink.GetPartPropertiesOrDefault(instance.Id).CFrame;
            }

            if (instance is RbxModel model)
            {
                RbxInstance primaryPart = model.PrimaryPart;
                return primaryPart != null
                    ? sink.GetPartPropertiesOrDefault(primaryPart.Id).CFrame
                    : GetWorldPivot(sink, model);
            }

            if (instance.ClassName == "Camera")
            {
                return context.Bindings.CameraRig.GetCFrame();
            }

            throw RbxError.BadArgument(
                "GetPivot is not available on " + instance.ClassName,
                "call GetPivot on a BasePart, Model or Camera");
        }

        private static RbxCFrame GetWorldPivot(IPartPropertySink sink, RbxModel model)
        {
            if (model.HasStoredWorldPivot)
            {
                return model.StoredWorldPivot;
            }

            bool foundPart = false;
            RbxVector3 minimum = RbxVector3.Zero;
            RbxVector3 maximum = RbxVector3.Zero;
            foreach (RbxInstance descendant in model.GetDescendants())
            {
                if (!descendant.IsA("BasePart"))
                {
                    continue;
                }

                PartProperties properties = sink.GetPartPropertiesOrDefault(descendant.Id);
                RbxVector3 halfSize = properties.Size.Abs() * 0.5f;
                RbxVector3 extents = properties.CFrame.XVector.Abs() * halfSize.X
                                     + properties.CFrame.YVector.Abs() * halfSize.Y
                                     + properties.CFrame.ZVector.Abs() * halfSize.Z;
                RbxVector3 partMinimum = properties.Position - extents;
                RbxVector3 partMaximum = properties.Position + extents;
                if (!foundPart)
                {
                    minimum = partMinimum;
                    maximum = partMaximum;
                    foundPart = true;
                    continue;
                }

                minimum = minimum.Min(partMinimum);
                maximum = maximum.Max(partMaximum);
            }

            return foundPart
                ? RbxCFrame.FromPosition((minimum + maximum) * 0.5f)
                : RbxCFrame.Identity;
        }

        /// <summary>
        /// Mirror <c>PVInstance:PivotTo</c>: "transforms the PVInstance along with all of its
        /// descendant PVInstances" — every descendant BasePart and Model moves rigidly with the
        /// pivot, for a BasePart root exactly as for a Model root.
        /// </summary>
        private static void PivotTo(LuaCsRbxModContext context, RbxInstance instance,
            RbxCFrame target)
        {
            IPartPropertySink sink = context.PartSink;
            bool isPart = instance.IsA("BasePart");
            bool isCamera = instance.ClassName == "Camera";
            RbxModel model = instance as RbxModel;
            if (!isPart && model == null && !isCamera)
            {
                throw RbxError.BadArgument(
                    "PivotTo is not available on " + instance.ClassName,
                    "call PivotTo on a BasePart, Model or Camera");
            }

            RbxCFrame transform = target * GetPivot(context, instance).Inverse();
            List<RbxInstance> parts = new();
            List<PartProperties> partsBefore = new();
            List<RbxModel> models = new();
            List<RbxCFrame> modelWorldPivots = new();
            if (model != null)
            {
                models.Add(model);
                modelWorldPivots.Add(GetWorldPivot(sink, model));
            }

            foreach (RbxInstance descendant in instance.GetDescendants())
            {
                if (descendant.IsA("BasePart"))
                {
                    parts.Add(descendant);
                    partsBefore.Add(sink.GetPartPropertiesOrDefault(descendant.Id));
                }

                if (descendant is RbxModel descendantModel)
                {
                    models.Add(descendantModel);
                    modelWorldPivots.Add(GetWorldPivot(sink, descendantModel));
                }
            }

            if (isPart)
            {
                // WHY the root takes the target verbatim: target * cf^-1 * cf is only equal to the
                // target up to float error, and the part the script pivoted must land exactly there.
                PartProperties rootBefore = sink.GetPartPropertiesOrDefault(instance.Id);
                sink.SetCFrame(instance.Id, target);
                context.RecordMutation(instance);
                PartProperties rootAfter = sink.GetPartPropertiesOrDefault(instance.Id);
                NotifyPartChanges(instance, "CFrame", in rootBefore, in rootAfter);
                if (rootBefore.CFrame != rootAfter.CFrame)
                {
                    EndWalksOfMovedRootPart(instance);
                }
            }

            if (isCamera)
            {
                SetCameraCFrame(context.Bindings.CameraRig, instance, in target);
                context.RecordMutation(instance);
            }

            for (int partIndex = 0; partIndex < parts.Count; partIndex++)
            {
                RbxInstance part = parts[partIndex];
                PartProperties partBefore = partsBefore[partIndex];
                sink.SetCFrame(part.Id, transform * partBefore.CFrame);
                context.RecordMutation(part);
                PartProperties partAfter = sink.GetPartPropertiesOrDefault(part.Id);
                NotifyPartChanges(part, "CFrame", in partBefore, in partAfter);
                if (partBefore.CFrame != partAfter.CFrame)
                {
                    EndWalksOfMovedRootPart(part);
                }
            }

            for (int modelIndex = 0; modelIndex < models.Count; modelIndex++)
            {
                RbxCFrame nextWorldPivot = transform * modelWorldPivots[modelIndex];
                models[modelIndex].SetWorldPivot(in nextWorldPivot);
            }
        }

        /// <summary>Reads a wired BasePart property from the sink as a Roblox-space datatype.</summary>
        private static bool TryReadSpatial(LuaCsRbxModContext context, RbxInstance self, string key,
            out LuaValue value)
        {
            if (!self.IsA("BasePart"))
            {
                value = LuaValue.Nil;
                return false;
            }

            PartProperties properties = context.PartSink.GetPartPropertiesOrDefault(self.Id);
            switch (key)
            {
                case "Shape":
                    value = WrapPartType(context, properties.Shape);
                    return true;
                case "Material":
                    value = WrapMaterial(context, properties.Material);
                    return true;
                case "MaterialVariant":
                    value = properties.MaterialVariant ?? string.Empty;
                    return true;
                case "Position":
                    value = LuaCsRbxDatatypeBindings.Wrap(properties.Position);
                    return true;
                case "Size":
                    value = LuaCsRbxDatatypeBindings.Wrap(properties.Size);
                    return true;
                case "CFrame":
                    value = LuaCsRbxDatatypeBindings.Wrap(properties.CFrame);
                    return true;
                case "Orientation":
                    (float rx, float ry, float rz) orientation = properties.CFrame.ToOrientation();
                    value = LuaCsRbxDatatypeBindings.Wrap(new RbxVector3(
                        orientation.rx * 180f / MathF.PI,
                        orientation.ry * 180f / MathF.PI,
                        orientation.rz * 180f / MathF.PI));
                    return true;
                case "Rotation":
                    (float rx, float ry, float rz) rotation = properties.CFrame.ToEulerAnglesXYZ();
                    value = LuaCsRbxDatatypeBindings.Wrap(new RbxVector3(
                        rotation.rx * 180f / MathF.PI,
                        rotation.ry * 180f / MathF.PI,
                        rotation.rz * 180f / MathF.PI));
                    return true;
                case "Color":
                    value = LuaCsRbxDatatypeBindings.Wrap(properties.Color);
                    return true;
                case "Transparency":
                    value = properties.Transparency;
                    return true;
                case "Anchored":
                    value = properties.Anchored;
                    return true;
                case "CanCollide":
                    value = properties.CanCollide;
                    return true;
                default:
                    value = LuaValue.Nil;
                    return false;
            }
        }

        /// <summary>Writes a wired BasePart property through the sink (Roblox Part semantics:
        /// setting Position keeps orientation, setting CFrame sets both), then fires Changed and
        /// the property signals for every member the write actually changed.</summary>
        private static bool TryWriteSpatial(LuaCsRbxModContext context, RbxInstance self, string key,
            LuaValue value)
        {
            if (!self.IsA("BasePart"))
            {
                return false;
            }

            IPartPropertySink sink = context.PartSink;
            InstanceId id = self.Id;
            string className = self.ClassName;
            PartProperties before;
            switch (key)
            {
                case "Shape":
                    context.RequireWorldEditForWrite(self, "Shape");
                    RbxPartShape shape = (RbxPartShape)ReadAssignedEnumItem(
                        value, className, "Shape", "PartType").Value;
                    before = sink.GetPartPropertiesOrDefault(id);
                    sink.SetShape(id, shape);
                    break;
                case "Material":
                    context.RequireWorldEditForWrite(self, "Material");
                    RbxMaterialId material = ReadAssignedMaterial(value, className, "Material");
                    before = sink.GetPartPropertiesOrDefault(id);
                    sink.SetMaterial(id, in material);
                    break;
                case "MaterialVariant":
                    context.RequireWorldEditForWrite(self, "MaterialVariant");
                    string variantName =
                        ReadAssignedOptionalString(value, className, "MaterialVariant");
                    before = sink.GetPartPropertiesOrDefault(id);
                    sink.SetMaterialVariant(id, variantName);
                    break;
                case "Position":
                    context.RequireWorldEditForWrite(self, "Position");
                    RbxVector3 position = ReadAssignedFiniteVector3(value, className, "Position");
                    before = sink.GetPartPropertiesOrDefault(id);
                    sink.SetPosition(id, position);
                    // WHY every positional assignment is noted: the mirror's Touched fires only for
                    // physical movement, so a part MOVED by a script must not report the overlap it
                    // lands in as a collision. The physics relay drops contacts for parts noted here.
                    context.Bindings.WorldPhysics.NoteTeleport(id);
                    break;
                case "Size":
                    context.RequireWorldEditForWrite(self, "Size");
                    RbxVector3 size = ClampPartSize(
                        ReadAssignedFiniteVector3(value, className, "Size"));
                    before = sink.GetPartPropertiesOrDefault(id);
                    sink.SetSize(id, size);
                    break;
                case "CFrame":
                    context.RequireWorldEditForWrite(self, "CFrame");
                    RbxCFrame cframe = ReadAssignedPartCFrame(value, className, "CFrame");
                    before = sink.GetPartPropertiesOrDefault(id);
                    sink.SetCFrame(id, cframe);
                    context.Bindings.WorldPhysics.NoteTeleport(id);
                    break;
                case "Orientation":
                    context.RequireWorldEditForWrite(self, "Orientation");
                    RbxVector3 orientation =
                        ReadAssignedFiniteVector3(value, className, "Orientation");
                    before = sink.GetPartPropertiesOrDefault(id);
                    RbxCFrame orientationCFrame = RbxCFrame.FromOrientation(
                        orientation.X * MathF.PI / 180f,
                        orientation.Y * MathF.PI / 180f,
                        orientation.Z * MathF.PI / 180f);
                    sink.SetCFrame(id,
                        RbxCFrame.FromPosition(before.Position) * orientationCFrame);
                    // A rotation is a scripted move like any other: it can spin a part into an
                    // overlap, and that overlap is not a collision.
                    context.Bindings.WorldPhysics.NoteTeleport(id);
                    break;
                case "Rotation":
                    context.RequireWorldEditForWrite(self, "Rotation");
                    RbxVector3 rotation = ReadAssignedFiniteVector3(value, className, "Rotation");
                    before = sink.GetPartPropertiesOrDefault(id);
                    RbxCFrame rotationCFrame = RbxCFrame.FromEulerAnglesXYZ(
                        rotation.X * MathF.PI / 180f,
                        rotation.Y * MathF.PI / 180f,
                        rotation.Z * MathF.PI / 180f);
                    sink.SetCFrame(id,
                        RbxCFrame.FromPosition(before.Position) * rotationCFrame);
                    context.Bindings.WorldPhysics.NoteTeleport(id);
                    break;
                case "Color":
                    context.RequireWorldEditForWrite(self, "Color");
                    RbxColor3 color = ReadAssignedColor3(value, className, "Color");
                    before = sink.GetPartPropertiesOrDefault(id);
                    sink.SetColor(id, color);
                    break;
                case "Transparency":
                    context.RequireWorldEditForWrite(self, "Transparency");
                    float transparency = ReadAssignedFloat(value, className, "Transparency");
                    before = sink.GetPartPropertiesOrDefault(id);
                    sink.SetTransparency(id, transparency);
                    break;
                case "Anchored":
                    context.RequireWorldEditForWrite(self, "Anchored");
                    bool anchored = ReadAssignedBoolean(value, className, "Anchored");
                    before = sink.GetPartPropertiesOrDefault(id);
                    sink.SetAnchored(id, anchored);
                    break;
                case "CanCollide":
                    context.RequireWorldEditForWrite(self, "CanCollide");
                    bool canCollide = ReadAssignedBoolean(value, className, "CanCollide");
                    before = sink.GetPartPropertiesOrDefault(id);
                    sink.SetCanCollide(id, canCollide);
                    break;
                default:
                    return false;
            }

            context.RecordMutation(self);
            PartProperties after = sink.GetPartPropertiesOrDefault(id);
            NotifyPartChanges(self, key, in before, in after);
            if (before.CFrame != after.CFrame)
            {
                EndWalksOfMovedRootPart(self);
            }

            return true;
        }

        /// <summary>
        /// Ends the MoveTo of every Humanoid whose RootPart is <paramref name="part"/>, after a script
        /// changed that part's CFrame — by CFrame, Position, Orientation, Rotation or a PivotTo that
        /// carried it (Humanoid.yaml MoveTo: the walk "ends if ... a script changes the CFrame
        /// property of the humanoid's RootPart").
        /// </summary>
        /// <remarks>
        /// WHY only a part named HumanoidRootPart, and only its siblings are asked: the character
        /// pipeline (LuaCsRbxApiBindings.ResolveRootPart) only ever hands a Humanoid the sibling
        /// BasePart of that name as its RootPart, and the name check keeps every other part write —
        /// a script moving a hundred parts of a building each frame — from scanning its siblings at
        /// all. A host that calls RbxHumanoid.AttachHost itself with a differently named part is not
        /// covered.
        /// </remarks>
        private static void EndWalksOfMovedRootPart(RbxInstance part)
        {
            if (!string.Equals(part.Name, RbxCharacterFactory.RootPartName, StringComparison.Ordinal))
            {
                return;
            }

            RbxInstance character = part.Parent;
            if (character == null || character.IsDestroyed)
            {
                return;
            }

            IReadOnlyList<RbxInstance> siblings = character.GetChildren();
            for (int index = 0; index < siblings.Count; index++)
            {
                if (siblings[index] is RbxHumanoid humanoid
                    && ReferenceEquals(humanoid.RootPart, part))
                {
                    humanoid.EndWalkForScriptedRootPartMove();
                }
            }
        }

        /// <summary>
        /// Fires Changed and the property signal for every BasePart member that differs between
        /// two sink snapshots, the member the script or tween wrote first. An equal assignment
        /// changes nothing and fires nothing.
        /// </summary>
        /// <remarks>
        /// WHY derived members fire too: Position, Orientation and Rotation are views of CFrame, so
        /// moving a part by CFrame changes its Position — a script watching
        /// GetPropertyChangedSignal("Position") on a part another script tweens by CFrame must see
        /// it move (M1-03). Part state lives in the sink, not on the instance, so no setter on
        /// <see cref="RbxInstance"/> could fire these.
        /// </remarks>
        internal static void NotifyPartChanges(RbxInstance part, string writtenMember,
            in PartProperties before, in PartProperties after)
        {
            if (writtenMember != null && PartMemberChanged(writtenMember, in before, in after))
            {
                part.NotifyPropertyChanged(writtenMember);
            }

            for (int index = 0; index < NotifiedPartMembers.Length; index++)
            {
                string member = NotifiedPartMembers[index];
                if (!string.Equals(member, writtenMember, StringComparison.Ordinal)
                    && PartMemberChanged(member, in before, in after))
                {
                    part.NotifyPropertyChanged(member);
                }
            }
        }

        private static readonly string[] NotifiedPartMembers =
        {
            "CFrame", "Position", "Orientation", "Rotation", "Size", "Color", "Transparency",
            "Anchored", "CanCollide", "Shape", "Material", "MaterialVariant"
        };

        private static bool PartMemberChanged(string member, in PartProperties before,
            in PartProperties after)
        {
            switch (member)
            {
                case "CFrame":
                    return before.CFrame != after.CFrame;
                case "Position":
                    return before.CFrame.Position != after.CFrame.Position;
                case "Orientation":
                case "Rotation":
                    return before.CFrame.Rotation != after.CFrame.Rotation;
                case "Size":
                    return before.Size != after.Size;
                case "Color":
                    return before.Color != after.Color;
                case "Transparency":
                    return !before.Transparency.Equals(after.Transparency);
                case "Anchored":
                    return before.Anchored != after.Anchored;
                case "CanCollide":
                    return before.CanCollide != after.CanCollide;
                case "Shape":
                    return before.Shape != after.Shape;
                case "Material":
                    return before.Material != after.Material;
                case "MaterialVariant":
                    return !string.Equals(before.MaterialVariant, after.MaterialVariant,
                        StringComparison.Ordinal);
                default:
                    return false;
            }
        }

        /// <summary>Part.Shape as its interned Enum.PartType item (values match RbxPartShape).</summary>
        private static LuaValue WrapPartType(LuaCsRbxModContext context, RbxPartShape shape)
        {
            if (context.Bindings.Enums.TryGet("PartType", out RbxEnum partType)
                && partType.TryGetItem(shape.ToString(), out RbxEnumItem item))
            {
                return LuaCsRbxDatatypeBindings.Wrap(item);
            }

            return LuaValue.Nil;
        }

        /// <summary>Part.Material as its interned Enum.Material item.</summary>
        private static LuaValue WrapMaterial(LuaCsRbxModContext context, in RbxMaterialId material)
        {
            if (context.Bindings.Enums.TryGet("Material", out RbxEnum materialType)
                && materialType.TryGetItemByValue(material.Value, out RbxEnumItem item))
            {
                return LuaCsRbxDatatypeBindings.Wrap(item);
            }

            return LuaValue.Nil;
        }

        private static RbxMaterialId ReadAssignedMaterial(LuaValue value, string ownerName,
            string property)
        {
            RbxEnumItem item = ReadAssignedEnumItem(value, ownerName, property, "Material");
            return new RbxMaterialId(item.Name, item.Value);
        }

        // ---- UserInputService (input signals + poll surface over IInputSource) ---------------

        /// <summary>UserInputService members: the input signals, MouseBehavior, and the poll
        /// methods. All input READS are open at the Read tier (no capability gate) — observing
        /// input mutates nothing in the world.</summary>
        private static bool TryReadUserInput(LuaCsRbxModContext context, RbxInstance self, string key,
            out LuaValue value)
        {
            if (!(self is RbxUserInputService service))
            {
                value = LuaValue.Nil;
                return false;
            }

            switch (key)
            {
                case "InputBegan":
                    value = LuaCsRbxDatatypeBindings.Wrap(service.InputBegan, context);
                    return true;
                case "InputEnded":
                    value = LuaCsRbxDatatypeBindings.Wrap(service.InputEnded, context);
                    return true;
                case "InputChanged":
                    value = LuaCsRbxDatatypeBindings.Wrap(service.InputChanged, context);
                    return true;
                case "MouseBehavior":
                    value = service.MouseBehavior != null
                        ? LuaCsRbxDatatypeBindings.Wrap(service.MouseBehavior)
                        : LuaValue.Nil;
                    return true;
                case "IsKeyDown":
                    value = GetUserInputMethods(service).IsKeyDown;
                    return true;
                case "GetKeysPressed":
                    value = GetUserInputMethods(service).GetKeysPressed;
                    return true;
                case "GetMouseLocation":
                    value = GetUserInputMethods(service).GetMouseLocation;
                    return true;
                default:
                    value = LuaValue.Nil;
                    return false;
            }
        }

        // WHY: the poll methods close over `service` only (not the per-mod context) and the world
        // has one UserInputService, so their Lua wrappers are built once per service and shared —
        // the skill's flagship loop reads IsKeyDown several times per tick, and a fresh closure per
        // access would be a per-frame allocation. The weak table drops the cache when the service is.
        private static readonly ConditionalWeakTable<RbxUserInputService, UserInputMethods> InputMethodCache = new();

        private sealed class UserInputMethods
        {
            public LuaValue IsKeyDown;
            public LuaValue GetKeysPressed;
            public LuaValue GetMouseLocation;
        }

        private static UserInputMethods GetUserInputMethods(RbxUserInputService service)
        {
            return InputMethodCache.GetValue(service, s => new UserInputMethods
            {
                IsKeyDown = new LuaValue(Fn("UserInputService.IsKeyDown", ctx =>
                {
                    RbxEnumItem keyCode = ReadKeyCodeArg(ctx, 1, "UserInputService:IsKeyDown");
                    return s.IsKeyDown(keyCode.Value);
                })),
                GetKeysPressed = new LuaValue(Fn("UserInputService.GetKeysPressed", _ =>
                {
                    LuaTable list = new();
                    int index = 1;
                    foreach (RbxInputObject input in s.GetKeysPressed())
                    {
                        list[index++] = LuaCsRbxDatatypeBindings.Wrap(input);
                    }

                    return new LuaValue(list);
                })),
                GetMouseLocation = new LuaValue(Fn("UserInputService.GetMouseLocation",
                    _ => LuaCsRbxDatatypeBindings.Wrap(s.GetMouseLocation())))
            });
        }

        /// <summary>UserInputService.MouseBehavior assignment guarded as a shared world property.
        /// TODO: apply LockCenter/LockCurrentPosition to the host cursor with the pointer-lock
        /// slice.</summary>
        private static bool TryWriteUserInput(LuaCsRbxModContext context, RbxInstance self,
            string key, LuaValue value)
        {
            if (!(self is RbxUserInputService service) || key != "MouseBehavior")
            {
                return false;
            }

            context.RequireWorldEditForWrite(self, "MouseBehavior");
            service.MouseBehavior = ReadAssignedEnumItem(
                value, self.ClassName, "MouseBehavior", "MouseBehavior");
            context.RecordMutation(self);
            return true;
        }

        /// <summary>An Enum.KeyCode method argument; <paramref name="index"/> is both the VM slot
        /// and the author's argument number, since slot 0 is self.</summary>
        private static RbxEnumItem ReadKeyCodeArg(LuaFunctionExecutionContext ctx, int index,
            string what)
        {
            if (TryUnbox(Arg(ctx, index), out RbxEnumItem item) && item.EnumType.Name == "KeyCode")
            {
                return item;
            }

            throw ExpectedArgument(what, "an Enum.KeyCode item", Arg(ctx, index), index);
        }

        // ---- RunService (per-frame game-loop signals over the host Step pump) ----------------

        /// <summary>RunService members: the modern PreAnimation/PreSimulation/PostSimulation/PreRender
        /// signals and their legacy aliases Stepped/Heartbeat/RenderStepped. Reads are open at the
        /// Read tier — connecting a per-frame handler observes the loop, it mutates nothing.</summary>
        private static bool TryReadRunService(LuaCsRbxModContext context, RbxInstance self, string key,
            out LuaValue value)
        {
            if (!(self is RbxRunService runService))
            {
                value = LuaValue.Nil;
                return false;
            }

            switch (key)
            {
                case "Heartbeat":
                    value = LuaCsRbxDatatypeBindings.Wrap(runService.Heartbeat, context);
                    return true;
                case "Stepped":
                    value = LuaCsRbxDatatypeBindings.Wrap(runService.Stepped, context);
                    return true;
                case "RenderStepped":
                    value = LuaCsRbxDatatypeBindings.Wrap(runService.RenderStepped, context);
                    return true;
                case "PreAnimation":
                    value = LuaCsRbxDatatypeBindings.Wrap(runService.PreAnimation, context);
                    return true;
                case "PreSimulation":
                    value = LuaCsRbxDatatypeBindings.Wrap(runService.PreSimulation, context);
                    return true;
                case "PostSimulation":
                    value = LuaCsRbxDatatypeBindings.Wrap(runService.PostSimulation, context);
                    return true;
                case "PreRender":
                    value = LuaCsRbxDatatypeBindings.Wrap(runService.PreRender, context);
                    return true;
                default:
                    value = LuaValue.Nil;
                    return false;
            }
        }

        // ---- ClickDetector (MouseClick over the host pick pump) ------------------------------

        /// <summary>ClickDetector members: the MouseClick/MouseHoverEnter/MouseHoverLeave signals and
        /// MaxActivationDistance. Signal reads carry the mod context so the returned connection is
        /// tracked for teardown (like RunService/UserInputService); reads are open at the Read tier —
        /// connecting a click handler observes the world, it mutates nothing.</summary>
        private static bool TryReadClickDetector(LuaCsRbxModContext context, RbxInstance self,
            string key, out LuaValue value)
        {
            if (!(self is RbxClickDetector detector))
            {
                value = LuaValue.Nil;
                return false;
            }

            switch (key)
            {
                case "MouseClick":
                    value = LuaCsRbxDatatypeBindings.Wrap(detector.MouseClick, context);
                    return true;
                case "MouseHoverEnter":
                    value = LuaCsRbxDatatypeBindings.Wrap(detector.MouseHoverEnter, context);
                    return true;
                case "MouseHoverLeave":
                    value = LuaCsRbxDatatypeBindings.Wrap(detector.MouseHoverLeave, context);
                    return true;
                case "MaxActivationDistance":
                    value = detector.MaxActivationDistance;
                    return true;
                default:
                    value = LuaValue.Nil;
                    return false;
            }
        }

        /// <summary>ClickDetector.MaxActivationDistance assignment (studs). Roblox lets any script set
        /// it, but it mutates shared world state, so it takes the WorldEdit gate like part properties.</summary>
        private static bool TryWriteClickDetector(LuaCsRbxModContext context, RbxInstance self,
            string key, LuaValue value)
        {
            if (!(self is RbxClickDetector detector) || key != "MaxActivationDistance")
            {
                return false;
            }

            context.RequireWorldEditForWrite(self, "MaxActivationDistance");
            detector.MaxActivationDistance =
                ReadAssignedFloat(value, self.ClassName, "MaxActivationDistance");
            context.RecordMutation(self);
            return true;
        }

        // ---- MaterialVariant (script-authored overrides under MaterialService) -------------

        /// <summary>MaterialVariant members: BaseMaterial as its Enum.Material item, the four
        /// map references as strings, and StudsPerTile. Reads are ungated; writes take WorldEdit.</summary>
        private static bool TryReadMaterialVariant(LuaCsRbxModContext context, RbxInstance self,
            string key, out LuaValue value)
        {
            if (!(self is RbxMaterialVariant variant))
            {
                value = LuaValue.Nil;
                return false;
            }

            switch (key)
            {
                case "BaseMaterial":
                    value = WrapMaterial(context, variant.BaseMaterial);
                    return true;
                case "ColorMap":
                    value = variant.ColorMap ?? string.Empty;
                    return true;
                case "NormalMap":
                    value = variant.NormalMap ?? string.Empty;
                    return true;
                case "RoughnessMap":
                    value = variant.RoughnessMap ?? string.Empty;
                    return true;
                case "MetalnessMap":
                    value = variant.MetalnessMap ?? string.Empty;
                    return true;
                case "StudsPerTile":
                    value = variant.StudsPerTile;
                    return true;
                default:
                    value = LuaValue.Nil;
                    return false;
            }
        }

        /// <summary>MaterialVariant member assignment (BaseMaterial, map strings, StudsPerTile).</summary>
        private static bool TryWriteMaterialVariant(LuaCsRbxModContext context, RbxInstance self,
            string key, LuaValue value)
        {
            if (!(self is RbxMaterialVariant variant))
            {
                return false;
            }

            switch (key)
            {
                case "BaseMaterial":
                    context.RequireWorldEditForWrite(self, "BaseMaterial");
                    variant.BaseMaterial = ReadAssignedMaterial(value, self.ClassName, "BaseMaterial");
                    context.RecordMutation(self);
                    context.PartSink.RefreshMaterialVariant(variant.Name);
                    return true;
                case "ColorMap":
                    context.RequireWorldEditForWrite(self, "ColorMap");
                    variant.ColorMap = ReadAssignedString(value, self.ClassName, "ColorMap");
                    context.RecordMutation(self);
                    context.PartSink.RefreshMaterialVariant(variant.Name);
                    return true;
                case "NormalMap":
                    context.RequireWorldEditForWrite(self, "NormalMap");
                    variant.NormalMap = ReadAssignedString(value, self.ClassName, "NormalMap");
                    context.RecordMutation(self);
                    context.PartSink.RefreshMaterialVariant(variant.Name);
                    return true;
                case "RoughnessMap":
                    context.RequireWorldEditForWrite(self, "RoughnessMap");
                    variant.RoughnessMap =
                        ReadAssignedString(value, self.ClassName, "RoughnessMap");
                    context.RecordMutation(self);
                    context.PartSink.RefreshMaterialVariant(variant.Name);
                    return true;
                case "MetalnessMap":
                    context.RequireWorldEditForWrite(self, "MetalnessMap");
                    variant.MetalnessMap =
                        ReadAssignedString(value, self.ClassName, "MetalnessMap");
                    context.RecordMutation(self);
                    context.PartSink.RefreshMaterialVariant(variant.Name);
                    return true;
                case "StudsPerTile":
                    context.RequireWorldEditForWrite(self, "StudsPerTile");
                    variant.StudsPerTile =
                        ReadAssignedFloat(value, self.ClassName, "StudsPerTile");
                    context.RecordMutation(self);
                    context.PartSink.RefreshMaterialVariant(variant.Name);
                    return true;
                default:
                    return false;
            }
        }

        // ---- ValueBase (Value + Changed over the engine-free value classes) -----------------

        /// <summary>Value reads (Changed is dispatched with every other Instance signal). Reads
        /// are ungated; writes take the WorldEdit+ACL gate in <see cref="TryWriteValue"/> like
        /// every other part property.</summary>
        private static bool TryReadValue(LuaCsRbxModContext context, RbxInstance self,
            string key, out LuaValue value)
        {
            if (!(self is RbxValueBase valueBase))
            {
                value = LuaValue.Nil;
                return false;
            }

            switch (key)
            {
                case "Value":
                    value = ValueToLua(context, valueBase);
                    return true;
                default:
                    value = LuaValue.Nil;
                    return false;
            }
        }

        /// <summary>Boxes a live value payload for Lua (IntValue crosses as a number; the
        /// mirror documents precision loss past 2^53, so double is the faithful shape).</summary>
        private static LuaValue ValueToLua(LuaCsRbxModContext context, RbxValueBase valueBase)
        {
            switch (valueBase)
            {
                case RbxIntValue intValue: return (double)intValue.Value;
                case RbxNumberValue numberValue: return numberValue.Value;
                case RbxStringValue stringValue: return stringValue.Value;
                case RbxBoolValue boolValue: return boolValue.Value;
                case RbxObjectValue objectValue: return context.WrapInstance(objectValue.Value);
                case RbxVector3Value vector3Value:
                    return LuaCsRbxDatatypeBindings.Wrap(vector3Value.Value);
                case RbxCFrameValue cframeValue:
                    return LuaCsRbxDatatypeBindings.Wrap(cframeValue.Value);
                case RbxColor3Value color3Value:
                    return LuaCsRbxDatatypeBindings.Wrap(color3Value.Value);
                default: return LuaValue.Nil;
            }
        }

        /// <summary>Value assignment through the same WorldEdit+ACL sink path other part
        /// properties use; the per-type reader raises BAD_ARGUMENT on a mistyped assignment
        /// and nothing is written. ObjectValue accepts an Instance or nil.</summary>
        private static bool TryWriteValue(LuaCsRbxModContext context, RbxInstance self,
            string key, LuaValue value)
        {
            if (!(self is RbxValueBase valueBase) || key != "Value")
            {
                return false;
            }

            context.RequireWorldEditForWrite(self, "Value");
            switch (valueBase)
            {
                case RbxIntValue intValue:
                    intValue.SetFromDouble(ReadAssignedNumber(value, self.ClassName, "Value"));
                    break;
                case RbxNumberValue numberValue:
                    numberValue.Value = ReadAssignedNumber(value, self.ClassName, "Value");
                    break;
                case RbxStringValue stringValue:
                    stringValue.Value = ReadAssignedString(value, self.ClassName, "Value");
                    break;
                case RbxBoolValue boolValue:
                    boolValue.Value = ReadAssignedBoolean(value, self.ClassName, "Value");
                    break;
                case RbxObjectValue objectValue:
                    objectValue.Value =
                        ReadAssignedOptionalInstance(value, self.ClassName, "Value");
                    break;
                case RbxVector3Value vector3Value:
                    vector3Value.Value = ReadAssignedVector3(value, self.ClassName, "Value");
                    break;
                case RbxCFrameValue cframeValue:
                    cframeValue.Value = ReadAssignedCFrame(value, self.ClassName, "Value");
                    break;
                case RbxColor3Value color3Value:
                    color3Value.Value = ReadAssignedColor3(value, self.ClassName, "Value");
                    break;
                default:
                    return false;
            }

            // WHY no RecordMutation here: the setter itself advances the revision, and only when the
            // value ACTUALLY changed (`FireValueChanged`, guarded by an equality check in every
            // typed setter). Advancing again from the binding double-counted a real write and — worse
            // — counted a no-op write, because the guard that suppresses `Changed` cannot suppress a
            // bump that happens outside it. Revision drives stale-write rejection and the MVP12 dirty
            // set, so a phantom bump means a replicated update carrying nothing and a stale-revision
            // refusal for a write that was never in conflict.
            return true;
        }

        // ---- Tween / TweenService (MVP8 slice 8.4) ----------------------------------------

        /// <summary>Tween reads: Instance/TweenInfo/PlaybackState/Completed. Reads are ungated;
        /// PlaybackState resolves through the enum registry like PartType does.</summary>
        private static bool TryReadTween(LuaCsRbxModContext context, RbxInstance self,
            string key, out LuaValue value)
        {
            if (!(self is RbxTween tween))
            {
                value = LuaValue.Nil;
                return false;
            }

            switch (key)
            {
                case "Instance":
                    value = context.WrapInstance(tween.Target);
                    return true;
                case "TweenInfo":
                    value = tween.Info == null
                        ? LuaValue.Nil
                        : LuaCsRbxDatatypeBindings.Wrap(
                            tween.Info, context.Bindings.Enums);
                    return true;
                case "PlaybackState":
                    value = WrapPlaybackState(context, tween.PlaybackState);
                    return true;
                case "Completed":
                    value = LuaCsRbxDatatypeBindings.Wrap(tween.Completed, context);
                    return true;
                default:
                    value = LuaValue.Nil;
                    return false;
            }
        }

        /// <summary>
        /// The calling actor as the tween layer sees it, copied from the trusted context and never
        /// from a Lua argument; the owner mod id makes the mod's unload tear its tweens down.
        /// </summary>
        private static TweenCaller CreateTweenCaller(LuaCsRbxModContext context)
        {
            return new TweenCaller(
                context.ActorContext.ActorId,
                context.ActorContext.Grants.IsUnrestricted,
                context.ActorContext.WorldId,
                context.OwnerModId);
        }

        private static LuaValue WrapPlaybackState(LuaCsRbxModContext context,
            RbxTweenPlaybackState state)
        {
            if (context.Bindings.Enums.TryGet("PlaybackState", out RbxEnum playbackState)
                && playbackState.TryGetItem(state.ToString(), out RbxEnumItem item))
            {
                return LuaCsRbxDatatypeBindings.Wrap(item);
            }

            return (double)(int)state;
        }

        /// <summary>Reads the Create property table into goal boxes: the tweenable types (double,
        /// Vector3, CFrame, Color3, UDim2) and the Roblox-tweenable types CoreAI cannot tween yet
        /// (boolean, EnumItem, UDim, Vector2), which the service answers per member. Anything else
        /// is not tweenable in Roblox either and is refused here.</summary>
        private static List<KeyValuePair<string, object>> ReadPropertyTable(LuaValue value)
        {
            if (value.Type != LuaValueType.Table)
            {
                throw RbxError.BadArgument(
                    "TweenService:Create expects a table at argument 3",
                    "pass a dictionary like {Transparency = 1} at argument 3, got "
                    + Describe(value));
            }

            LuaTable table = value.Read<LuaTable>();
            List<KeyValuePair<string, object>> goals = new();
            foreach (KeyValuePair<LuaValue, LuaValue> pair in table)
            {
                if (pair.Key.Type != LuaValueType.String)
                {
                    throw RbxError.BadArgument(
                        "TweenService:Create expects string property names in the property table",
                        "pass a dictionary like {Transparency = 1}, got a "
                        + Describe(pair.Key) + " key");
                }

                string propertyName = pair.Key.Read<string>();
                goals.Add(new KeyValuePair<string, object>(
                    propertyName, ReadTweenGoal(pair.Value, propertyName)));
            }

            return goals;
        }

        private static object ReadTweenGoal(LuaValue value, string propertyName)
        {
            if (value.Type == LuaValueType.Number)
            {
                return value.Read<double>();
            }

            if (TryUnbox(value, out RbxVector3 vector))
            {
                return vector;
            }

            if (TryUnbox(value, out RbxCFrame cframe))
            {
                return cframe;
            }

            if (TryUnbox(value, out RbxColor3 color))
            {
                return color;
            }

            if (TryUnbox(value, out RbxUDim2 udim2))
            {
                return udim2;
            }

            // WHY handed to the service instead of refused here: only the service samples the
            // member, so only it can tell `{CanCollide = false}` (a real boolean member it cannot
            // tween yet: the NOT_IMPLEMENTED stub) from `{Transparency = true}` (a type mistake:
            // BAD_ARGUMENT) and `{Nope = true}` (no such member). One raise site keeps the stub's
            // wording identical whether a script or host code creates the tween (M8-14).
            if (value.Type == LuaValueType.Boolean)
            {
                return value.Read<bool>();
            }

            if (TryUnbox(value, out RbxEnumItem enumItem))
            {
                return enumItem;
            }

            if (TryUnbox(value, out RbxUDim udim))
            {
                return udim;
            }

            if (TryUnbox(value, out RbxVector2 vector2))
            {
                return vector2;
            }

            throw RbxError.BadArgument(
                "TweenService:Create goal for '" + propertyName
                + "' expects a number, Vector3, CFrame, Color3, or UDim2, got "
                + Describe(value),
                "pass a tweenable goal value for '" + propertyName + "'");
        }

        private static RbxEasingStyle ReadEasingStyle(LuaValue value)
        {
            RbxEnumItem item = ReadEasingItem(value, "EasingStyle", 2);
            if (Enum.TryParse(item.Name, out RbxEasingStyle style)
                && Enum.IsDefined(typeof(RbxEasingStyle), style))
            {
                return style;
            }

            throw RbxError.BadArgument(
                "got an unknown Enum.EasingStyle item '" + item.Name + "' at argument 2",
                "use one of Enum.EasingStyle:GetEnumItems()");
        }

        private static RbxEasingDirection ReadEasingDirection(LuaValue value)
        {
            RbxEnumItem item = ReadEasingItem(value, "EasingDirection", 3);
            if (Enum.TryParse(item.Name, out RbxEasingDirection direction)
                && Enum.IsDefined(typeof(RbxEasingDirection), direction))
            {
                return direction;
            }

            throw RbxError.BadArgument(
                "got an unknown Enum.EasingDirection item '" + item.Name + "' at argument 3",
                "use one of Enum.EasingDirection:GetEnumItems()");
        }

        private static RbxEnumItem ReadEasingItem(LuaValue value, string enumName,
            int argumentNumber)
        {
            if (TryUnbox(value, out RbxEnumItem item) && item.EnumType != null
                && item.EnumType.Name == enumName)
            {
                return item;
            }

            throw RbxError.BadArgument(
                "expects Enum." + enumName + " at argument " + argumentNumber,
                "pass Enum." + enumName + ".Quad, got " + Describe(value)
                + " at argument " + argumentNumber);
        }

        // ---- Camera (workspace.CurrentCamera over the camera rig) ---------------------------
        /// <summary>workspace.CurrentCamera plus the Camera instance's CFrame (over the rig),
        /// CameraType, and CameraSubject. Reads are ungated; writes require WorldEdit.</summary>
        private static bool TryReadCamera(LuaCsRbxModContext context, RbxInstance self,
            string key, out LuaValue value)
        {
            if (key == "CurrentCamera" && self.IsA("Workspace"))
            {
                value = context.WrapInstance(self.FindFirstChildOfClass("Camera"));
                return true;
            }

            if (self.ClassName != "Camera")
            {
                value = LuaValue.Nil;
                return false;
            }

            switch (key)
            {
                case "CFrame":
                    value = LuaCsRbxDatatypeBindings.Wrap(context.Bindings.CameraRig.GetCFrame());
                    return true;
                case "CameraType":
                    RbxEnumItem type = context.Bindings.CameraTypeItem;
                    value = type != null ? LuaCsRbxDatatypeBindings.Wrap(type) : LuaValue.Nil;
                    return true;
                case "CameraSubject":
                    value = context.WrapInstance(context.Bindings.CameraSubject);
                    return true;
                default:
                    value = LuaValue.Nil;
                    return false;
            }
        }

        private static bool TryWriteCamera(LuaCsRbxModContext context, RbxInstance self,
            string key, LuaValue value)
        {
            if (self.ClassName != "Camera")
            {
                return false;
            }

            // WHY the camera notifies here: its state lives on the rig and the bindings, not on
            // the Camera instance, so no setter of the instance could fire Changed for it.
            switch (key)
            {
                case "CFrame":
                    context.RequireWorldEditForWrite(self, "CFrame");
                    RbxCFrame cameraCFrame = ReadAssignedCFrame(value, self.ClassName, "CFrame");
                    SetCameraCFrame(context.Bindings.CameraRig, self, in cameraCFrame);
                    context.RecordMutation(self);
                    return true;
                case "CameraType":
                    context.RequireWorldEditForWrite(self, "CameraType");
                    RbxEnumItem cameraType = ReadAssignedEnumItem(
                        value, self.ClassName, "CameraType", "CameraType");
                    RbxEnumItem previousType = context.Bindings.CameraTypeItem;
                    context.Bindings.CameraTypeItem = cameraType;
                    context.RecordMutation(self);
                    if (!ReferenceEquals(previousType, context.Bindings.CameraTypeItem))
                    {
                        self.NotifyPropertyChanged("CameraType");
                    }

                    return true;
                case "CameraSubject":
                    context.RequireWorldEditForWrite(self, "CameraSubject");
                    RbxInstance subject = ReadAssignedOptionalInstance(
                        value, self.ClassName, "CameraSubject");
                    RbxInstance previousSubject = context.Bindings.CameraSubject;
                    context.Bindings.SetCameraSubject(subject);
                    context.RecordMutation(self);
                    if (!ReferenceEquals(previousSubject, context.Bindings.CameraSubject))
                    {
                        self.NotifyPropertyChanged("CameraSubject");
                    }

                    return true;
                default:
                    return false;
            }
        }

        /// <summary>
        /// Moves the camera rig and fires Camera's CFrame change when the pose actually moved;
        /// shared by the property write, PivotTo and the tween host so all three notify alike.
        /// </summary>
        internal static void SetCameraCFrame(IRbxCameraRig rig, RbxInstance camera,
            in RbxCFrame cframe)
        {
            RbxCFrame before = rig.GetCFrame();
            rig.SetCFrame(in cframe);
            if (before != rig.GetCFrame())
            {
                camera.NotifyPropertyChanged("CFrame");
            }
        }

        // ---- Property-assignment readers ----------------------------------------------------
        // WHY a family of their own: a property write has no argument list, so its BAD_ARGUMENT
        // names the property ("Part.Position expects a Vector3, got string") through
        // PropertyAssignmentError instead of a position the author never typed (M1-07). The owner
        // name is the instance's ClassName, read on the failure path only.

        private static RbxEnumItem ReadAssignedEnumItem(LuaValue value, string ownerName,
            string property, string enumName)
        {
            if (TryUnbox(value, out RbxEnumItem item) && item.EnumType != null
                && item.EnumType.Name == enumName)
            {
                return item;
            }

            throw PropertyAssignmentError(ownerName, property, "an Enum." + enumName + " item",
                value);
        }

        private static RbxVector3 ReadAssignedVector3(LuaValue value, string ownerName,
            string property)
        {
            if (TryUnbox(value, out RbxVector3 vector))
            {
                return vector;
            }

            throw PropertyAssignmentError(ownerName, property, "a Vector3", value);
        }

        private static RbxCFrame ReadAssignedCFrame(LuaValue value, string ownerName,
            string property)
        {
            if (TryUnbox(value, out RbxCFrame cframe))
            {
                return cframe;
            }

            throw PropertyAssignmentError(ownerName, property, "a CFrame", value);
        }

        private static RbxColor3 ReadAssignedColor3(LuaValue value, string ownerName,
            string property)
        {
            if (TryUnbox(value, out RbxColor3 color))
            {
                return color;
            }

            throw PropertyAssignmentError(ownerName, property, "a Color3", value);
        }

        private static float ReadAssignedFloat(LuaValue value, string ownerName, string property)
        {
            return (float)ReadAssignedNumber(value, ownerName, property);
        }

        /// <summary>Nil or empty clears to no override (null); otherwise the variant name.</summary>
        private static string ReadAssignedOptionalString(LuaValue value, string ownerName,
            string property)
        {
            if (value.Type == LuaValueType.Nil)
            {
                return null;
            }

            if (value.Type == LuaValueType.String)
            {
                string text = value.Read<string>();
                return string.IsNullOrEmpty(text) ? null : text;
            }

            throw PropertyAssignmentError(ownerName, property, "a string or nil", value);
        }

        /// <summary>
        /// Reads a Vector3 for a spatial write, refusing NaN and infinite components: the engine
        /// refuses such a pose while the registry would keep answering it, so the two diverge.
        /// </summary>
        private static RbxVector3 ReadAssignedFiniteVector3(LuaValue value, string ownerName,
            string property)
        {
            RbxVector3 vector = ReadAssignedVector3(value, ownerName, property);
            if (!IsFinite(vector))
            {
                throw RbxError.BadArgument(
                    ownerName + "." + property
                    + " expects a Vector3 with finite components, got NaN or infinity",
                    "check the arithmetic that produced it (0/0, math.huge) before assigning");
            }

            return vector;
        }

        /// <summary>A CFrame property write that becomes a part's pose (Part.CFrame, WorldPivot).</summary>
        private static RbxCFrame ReadAssignedPartCFrame(LuaValue value, string ownerName,
            string property)
        {
            RbxCFrame cframe = ReadAssignedCFrame(value, ownerName, property);
            PartPoseCheck check = TryMakePartPose(cframe, out RbxCFrame pose);
            if (check != PartPoseCheck.Valid)
            {
                throw PartPoseError(check, ownerName + "." + property, "");
            }

            return pose;
        }

        /// <summary>A CFrame method argument that becomes a pose (PVInstance:PivotTo).</summary>
        private static RbxCFrame ReadPartCFrameArgument(LuaFunctionExecutionContext ctx, int index,
            string what, int argumentNumber)
        {
            RbxCFrame cframe = ReadCFrame(ctx, index, what, argumentNumber);
            PartPoseCheck check = TryMakePartPose(cframe, out RbxCFrame pose);
            if (check != PartPoseCheck.Valid)
            {
                throw PartPoseError(check, what, " at argument " + argumentNumber);
            }

            return pose;
        }

        private enum PartPoseCheck
        {
            Valid,
            NonFinite,
            DegenerateRotation
        }

        /// <summary>
        /// Turns a CFrame into a part pose: finite components required, and a rotation that is
        /// scaled, skewed or mirrored is orthonormalized as the mirror does for
        /// <c>BasePart.CFrame</c>. An already orthonormal CFrame is kept bit-for-bit.
        /// </summary>
        private static PartPoseCheck TryMakePartPose(RbxCFrame cframe, out RbxCFrame pose)
        {
            pose = cframe;
            if (!IsFinite(cframe))
            {
                return PartPoseCheck.NonFinite;
            }

            if (IsOrthonormal(cframe))
            {
                return PartPoseCheck.Valid;
            }

            RbxCFrame orthonormal = cframe.Orthonormalize();
            if (!IsFinite(orthonormal))
            {
                return PartPoseCheck.DegenerateRotation;
            }

            pose = orthonormal;
            return PartPoseCheck.Valid;
        }

        private static RbxError PartPoseError(PartPoseCheck check, string subject, string position)
        {
            if (check == PartPoseCheck.NonFinite)
            {
                return RbxError.BadArgument(
                    subject + " expects a CFrame with finite components" + position
                    + ", got NaN or infinity",
                    "check the arithmetic that produced it (0/0, math.huge) before assigning");
            }

            return RbxError.BadArgument(
                subject + " expects a CFrame with a usable rotation" + position
                + "; its axes are zero or parallel",
                "build it with CFrame.new, CFrame.lookAt or CFrame.fromMatrix using two "
                + "non-zero, non-parallel axes");
        }

        private static bool IsFinite(float component)
        {
            return !float.IsNaN(component) && !float.IsInfinity(component);
        }

        private static bool IsFinite(RbxVector3 vector)
        {
            return IsFinite(vector.X) && IsFinite(vector.Y) && IsFinite(vector.Z);
        }

        private static bool IsFinite(RbxCFrame cframe)
        {
            return IsFinite(cframe.Position) && IsFinite(cframe.XVector)
                                             && IsFinite(cframe.YVector)
                                             && IsFinite(cframe.ZVector);
        }

        private static bool IsOrthonormal(RbxCFrame cframe)
        {
            const float tolerance = 1e-4f;
            RbxVector3 x = cframe.XVector;
            RbxVector3 y = cframe.YVector;
            RbxVector3 z = cframe.ZVector;
            return MathF.Abs(x.Dot(x) - 1f) <= tolerance
                   && MathF.Abs(y.Dot(y) - 1f) <= tolerance
                   && MathF.Abs(z.Dot(z) - 1f) <= tolerance
                   && MathF.Abs(x.Dot(y)) <= tolerance
                   && MathF.Abs(x.Dot(z)) <= tolerance
                   && MathF.Abs(y.Dot(z)) <= tolerance
                   && MathF.Abs(x.Cross(y).Dot(z) - 1f) <= tolerance;
        }

        /// <summary>
        /// Mirror <c>BasePart.Size</c> bounds: each axis "as low as 0.001 and as high as 2048".
        /// A zero or negative axis clamps up to the minimum instead of mirroring the mesh.
        /// </summary>
        private static RbxVector3 ClampPartSize(RbxVector3 size)
        {
            return new RbxVector3(ClampPartSizeAxis(size.X), ClampPartSizeAxis(size.Y),
                ClampPartSizeAxis(size.Z));
        }

        private static float ClampPartSizeAxis(float axis)
        {
            const float minimum = 0.001f;
            const float maximum = 2048f;
            return axis < minimum ? minimum : axis > maximum ? maximum : axis;
        }

        /// <summary>
        /// Reads Players.RespawnTime, refusing anything a respawn timer could not honour.
        /// </summary>
        /// <remarks>
        /// WHY negative and non-finite are refused rather than clamped: a respawn delay is a
        /// duration, and a script that computed one wrongly gets told so at the assignment instead
        /// of discovering it when nothing ever respawns.
        /// </remarks>
        private static double ReadRespawnTime(LuaValue value, string ownerName)
        {
            double seconds = ReadAssignedNumber(value, ownerName, "RespawnTime");
            if (seconds < 0d || double.IsNaN(seconds) || double.IsInfinity(seconds))
            {
                throw RbxError.BadArgument(
                    ownerName + ".RespawnTime expects a finite number of seconds >= 0",
                    "pass a duration in seconds, got " + seconds.ToString(
                        System.Globalization.CultureInfo.InvariantCulture));
            }

            return seconds;
        }

        // ---- Raycast userdata ---------------------------------------------------------------

        private static bool Ok(LuaValue produced, out LuaValue value)
        {
            value = produced;
            return true;
        }

        private static RbxEnumItem ResolveHumanoidStateItem(LuaCsRbxModContext context,
            RbxHumanoidState state)
        {
            if (context.Bindings.Enums.TryGet("HumanoidStateType", out RbxEnum enumType)
                && enumType.TryGetItemByValue((int)state, out RbxEnumItem item))
            {
                return item;
            }

            throw RbxError.BadArgument(
                "Humanoid:GetState cannot resolve Enum.HumanoidStateType." + state,
                "use the default enum registry, which ships HumanoidStateType with Humanoid");
        }

        private static RbxEnumItem ReadHumanoidStateItem(LuaValue value)
        {
            if (TryUnbox(value, out RbxEnumItem item)
                && string.Equals(item.EnumType.Name, "HumanoidStateType", StringComparison.Ordinal))
            {
                return item;
            }

            throw ExpectedArgument("Humanoid:ChangeState", "an Enum.HumanoidStateType item",
                value, 1);
        }

        private static RbxError NotAValidMember(string key, string typeName)
        {
            return RbxError.BadArgument(
                key + " is not a valid member of " + typeName,
                "check the " + typeName + " member list in the Roblox API reference");
        }

        private static readonly LuaTable RaycastParamsMeta = BuildRaycastParamsMeta();

        private static readonly LuaTable RaycastResultMeta = BuildRaycastResultMeta();

        /// <summary>Builds the <c>RaycastParams</c> global: only <c>new</c>, as the mirror has it.</summary>
        internal static LuaValue BuildRaycastParamsGlobal(LuaCsRbxModContext context)
        {
            LuaTable global = new();
            global["new"] = Fn("RaycastParams.new",
                _ => Box(new RaycastParamsBox(context, new RbxRaycastParams()), RaycastParamsMeta));
            return new LuaValue(global);
        }

        private static RbxRaycastParams ReadRaycastParams(LuaValue value)
        {
            if (TryUnbox(value, out RaycastParamsBox box))
            {
                return box.Params;
            }

            throw RbxError.BadArgument(
                "WorldRoot:Raycast expects a RaycastParams at argument 3",
                "pass RaycastParams.new() or nil, got " + Describe(value));
        }

        private static LuaTable BuildRaycastParamsMeta()
        {
            LuaTable meta = new();
            meta[Metamethods.Index] = Fn("RaycastParams.__index", ctx =>
            {
                RaycastParamsBox box = SelfRaycastParams(ctx);
                RbxRaycastParams self = box.Params;
                string key = ReadString(ctx, 1, "RaycastParams member access");
                switch (key)
                {
                    case "FilterType":
                        return LuaCsRbxDatatypeBindings.Wrap(ResolveFilterTypeItem(box.Context, self.FilterType));
                    case "IgnoreWater": return self.IgnoreWater;
                    case "BruteForceAllSlow": return self.BruteForceAllSlow;
                    case "RespectCanCollide": return self.RespectCanCollide;
                    case "CollisionGroup": return self.CollisionGroup;
                    case "FilterDescendantsInstances":
                        return new LuaValue(BuildInstanceTable(box, self.FilterDescendantsInstances));
                    case "ExcludeInstances":
                        return self.ExcludeInstances == null
                            ? LuaValue.Nil
                            : new LuaValue(BuildInstanceTable(box, self.ExcludeInstances));
                    case "IncludeInstances":
                        return self.IncludeInstances == null
                            ? LuaValue.Nil
                            : new LuaValue(BuildInstanceTable(box, self.IncludeInstances));
                    case "AddToFilter":
                        return new LuaValue(Fn("RaycastParams:AddToFilter", inner =>
                        {
                            RbxRaycastParams target = SelfRaycastParams(inner).Params;
                            LuaValue added = Arg(inner, 1);
                            // WHY both shapes: the mirror types the parameter `Instance | Array`,
                            // and `params:AddToFilter(character)` is the spelling scripts use most.
                            if (TryGetInstance(added, out LuaCsRbxInstanceProxy single))
                            {
                                target.AddToFilter(single.Instance);
                                return LuaValue.Nil;
                            }

                            target.AddToFilter(
                                ReadInstanceList(added, "RaycastParams:AddToFilter"));
                            return LuaValue.Nil;
                        }));
                    default: throw NotAValidMember(key, "RaycastParams");
                }
            });
            meta[Metamethods.NewIndex] = Fn("RaycastParams.__newindex", ctx =>
            {
                RbxRaycastParams self = SelfRaycastParams(ctx).Params;
                string key = ReadString(ctx, 1, "RaycastParams member assignment");
                LuaValue value = Arg(ctx, 2);
                switch (key)
                {
                    case "FilterType":
                        self.FilterType = ReadFilterType(value);
                        return LuaValue.Nil;
                    case "IgnoreWater":
                        self.IgnoreWater = ReadAssignedBoolean(
                            value, "RaycastParams", "IgnoreWater");
                        return LuaValue.Nil;
                    case "BruteForceAllSlow":
                        self.BruteForceAllSlow = ReadAssignedBoolean(
                            value, "RaycastParams", "BruteForceAllSlow");
                        return LuaValue.Nil;
                    case "RespectCanCollide":
                        self.RespectCanCollide = ReadAssignedBoolean(
                            value, "RaycastParams", "RespectCanCollide");
                        return LuaValue.Nil;
                    case "CollisionGroup":
                        self.CollisionGroup = ReadAssignedString(
                            value, "RaycastParams", "CollisionGroup");
                        return LuaValue.Nil;
                    case "FilterDescendantsInstances":
                        self.SetFilterDescendantsInstances(ReadInstanceList(
                            value, "RaycastParams.FilterDescendantsInstances assignment"));
                        return LuaValue.Nil;
                    // WHY nil is kept distinct from {}: the mirror pins IncludeInstances = nil as
                    // "include everything" and {} as "include nothing".
                    case "ExcludeInstances":
                        self.SetExcludeInstances(value.Type == LuaValueType.Nil
                            ? null
                            : ReadInstanceList(value, "RaycastParams.ExcludeInstances assignment"));
                        return LuaValue.Nil;
                    case "IncludeInstances":
                        self.SetIncludeInstances(value.Type == LuaValueType.Nil
                            ? null
                            : ReadInstanceList(value, "RaycastParams.IncludeInstances assignment"));
                        return LuaValue.Nil;
                    default: throw NotAValidMember(key, "RaycastParams");
                }
            });
            meta[Metamethods.ToString] = Fn("RaycastParams.__tostring", _ => "RaycastParams");
            return Lock(meta);
        }

        private static RbxEnumItem ResolveFilterTypeItem(LuaCsRbxModContext context,
            RbxRaycastFilterType filterType)
        {
            if (context.Bindings.Enums.TryGet("RaycastFilterType", out RbxEnum enumType)
                && enumType.TryGetItemByValue((int)filterType, out RbxEnumItem item))
            {
                return item;
            }

            throw RbxError.BadArgument(
                "RaycastParams.FilterType cannot resolve Enum.RaycastFilterType." + filterType,
                "use the default enum registry, which ships RaycastFilterType with Raycast");
        }

        private static RbxRaycastFilterType ReadFilterType(LuaValue value)
        {
            if (TryUnbox(value, out RbxEnumItem item)
                && string.Equals(item.EnumType.Name, "RaycastFilterType", StringComparison.Ordinal))
            {
                return (RbxRaycastFilterType)item.Value;
            }

            throw RbxError.BadArgument(
                "RaycastParams.FilterType expects an Enum.RaycastFilterType",
                "assign Enum.RaycastFilterType.Exclude or .Include, got " + Describe(value));
        }

        private static LuaTable BuildInstanceTable(RaycastParamsBox box,
            IReadOnlyList<RbxInstance> filter)
        {
            // WHY a fresh table: Roblox hands back an array the script may keep and mutate, and that
            // mutation must not silently re-filter a query the params are still used for.
            LuaTable table = new();
            for (int index = 0; index < filter.Count; index++)
            {
                table[index + 1] = box.Context.WrapInstance(filter[index]);
            }

            return table;
        }

        private static IEnumerable<RbxInstance> ReadInstanceList(LuaValue value, string what)
        {
            if (value.Type != LuaValueType.Table)
            {
                throw RbxError.BadArgument(
                    what + " expects an array of Instances",
                    "pass a table such as {part, model}, got " + Describe(value));
            }

            List<RbxInstance> instances = new();
            LuaTable table = value.Read<LuaTable>();
            for (int index = 1; index <= table.ArrayLength; index++)
            {
                LuaValue entry = table[index];
                if (entry.Type == LuaValueType.Nil)
                {
                    continue;
                }

                if (!TryGetInstance(entry, out LuaCsRbxInstanceProxy proxy))
                {
                    throw RbxError.BadArgument(
                        what + " expects Instances; entry " + index + " is " + Describe(entry),
                        "remove the entry or pass the Instance it should have been");
                }

                instances.Add(proxy.Instance);
            }

            return instances;
        }

        private static RaycastParamsBox SelfRaycastParams(LuaFunctionExecutionContext ctx)
        {
            if (TryUnbox(Arg(ctx, 0), out RaycastParamsBox self))
            {
                return self;
            }

            throw RbxError.BadArgument(
                "RaycastParams member access expects a RaycastParams as self",
                "read members off a RaycastParams.new() value");
        }

        private static LuaValue WrapRaycastResult(LuaCsRbxModContext context, RbxRaycastResult result)
        {
            return Box(new RaycastResultBox(context, result), RaycastResultMeta);
        }

        private static LuaTable BuildRaycastResultMeta()
        {
            LuaTable meta = new();
            meta[Metamethods.Index] = Fn("RaycastResult.__index", ctx =>
            {
                RaycastResultBox self = SelfRaycastResult(ctx);
                string key = ReadString(ctx, 1, "RaycastResult member access");
                switch (key)
                {
                    case "Instance": return self.Context.WrapInstance(self.Result.Instance);
                    case "Position": return LuaCsRbxDatatypeBindings.Wrap(self.Result.Position);
                    case "Normal": return LuaCsRbxDatatypeBindings.Wrap(self.Result.Normal);
                    case "Distance": return self.Result.Distance;
                    case "Material": return WrapMaterial(self.Context, self.Result.Material);
                    default: throw NotAValidMember(key, "RaycastResult");
                }
            });
            meta[Metamethods.NewIndex] = Fn("RaycastResult.__newindex",
                _ => throw RbxError.BadArgument(
                    "RaycastResult values are immutable",
                    "read the members workspace:Raycast filled in; they describe one past query"));
            meta[Metamethods.ToString] = Fn("RaycastResult.__tostring", _ => "RaycastResult");
            return Lock(meta);
        }

        private static RaycastResultBox SelfRaycastResult(LuaFunctionExecutionContext ctx)
        {
            if (TryUnbox(Arg(ctx, 0), out RaycastResultBox self))
            {
                return self;
            }

            throw RbxError.BadArgument(
                "RaycastResult member access expects a RaycastResult as self",
                "read members off the value workspace:Raycast returned");
        }

        /// <summary>Pairs params with the mod context that wraps instances read back out of them.</summary>
        private sealed class RaycastParamsBox
        {
            public RaycastParamsBox(LuaCsRbxModContext context, RbxRaycastParams raycastParams)
            {
                Context = context;
                Params = raycastParams;
            }

            public LuaCsRbxModContext Context { get; }

            public RbxRaycastParams Params { get; }
        }

        /// <summary>Pairs a result with the mod context that has to wrap its instance.</summary>
        private sealed class RaycastResultBox
        {
            public RaycastResultBox(LuaCsRbxModContext context, RbxRaycastResult result)
            {
                Context = context;
                Result = result;
            }

            public LuaCsRbxModContext Context { get; }

            public RbxRaycastResult Result { get; }
        }
    }

    /// <summary>
    /// The Instance method table: bindings keyed by member name AND declaring class, resolved for
    /// an instance by its nearest declaring class (a Model method before a PVInstance one before
    /// an Instance-wide one).
    /// </summary>
    /// <remarks>
    /// WHY not one entry per name: a name-keyed table let the second <c>MoveTo</c> registration
    /// silently replace the first, so binding <c>Model:MoveTo</c> next to <c>Humanoid:MoveTo</c>
    /// would have broken every character script (M1-31). A repeated name and class is a build
    /// error instead.
    /// </remarks>
    internal sealed class LuaCsRbxMethodTable
    {
        private readonly ClassCatalog _catalog;
        private readonly Dictionary<string, List<Binding>> _byName = new(StringComparer.Ordinal);

        public LuaCsRbxMethodTable(ClassCatalog catalog)
        {
            _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        }

        /// <summary>Number of bindings, every class counted.</summary>
        public int Count { get; private set; }

        /// <summary>
        /// Adds a method declared on <paramref name="declaringClassName"/>; null declares it on
        /// every Instance.
        /// </summary>
        public void Add(string name, LuaValue value, string declaringClassName)
        {
            if (string.IsNullOrEmpty(name))
            {
                throw new ArgumentException("A method name is required.", nameof(name));
            }

            if (!_byName.TryGetValue(name, out List<Binding> bindings))
            {
                bindings = new List<Binding>(1);
                _byName.Add(name, bindings);
            }

            for (int index = 0; index < bindings.Count; index++)
            {
                if (string.Equals(bindings[index].DeclaringClassName, declaringClassName,
                        StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        "Instance method " + (declaringClassName ?? "Instance") + ":" + name
                        + " is already bound");
                }
            }

            bindings.Add(new Binding(value, declaringClassName));
            Count++;
        }

        /// <summary>The binding of <paramref name="name"/> nearest to the instance's class.</summary>
        public bool TryResolve(RbxInstance instance, string name, out LuaValue value)
        {
            value = LuaValue.Nil;
            if (instance == null || name == null
                || !_byName.TryGetValue(name, out List<Binding> bindings))
            {
                return false;
            }

            bool found = false;
            string foundClassName = null;
            for (int index = 0; index < bindings.Count; index++)
            {
                Binding binding = bindings[index];
                string declaringClassName = binding.DeclaringClassName;
                if (declaringClassName != null && !instance.IsA(declaringClassName))
                {
                    continue;
                }

                if (found && !IsNearer(declaringClassName, foundClassName))
                {
                    continue;
                }

                value = binding.Value;
                foundClassName = declaringClassName;
                found = true;
            }

            return found;
        }

        /// <summary>True when <paramref name="candidate"/> is a more derived declaration than
        /// <paramref name="current"/>; both are ancestors of the same instance class.</summary>
        private bool IsNearer(string candidate, string current)
        {
            if (candidate == null)
            {
                return false;
            }

            return current == null
                   || (!string.Equals(candidate, current, StringComparison.Ordinal)
                       && _catalog.IsA(candidate, current));
        }

        private readonly struct Binding
        {
            public Binding(LuaValue value, string declaringClassName)
            {
                Value = value;
                DeclaringClassName = declaringClassName;
            }

            public LuaValue Value { get; }

            public string DeclaringClassName { get; }
        }
    }
}
