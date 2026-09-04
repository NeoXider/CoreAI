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
        private readonly MutationEnvelope? _mutationEnvelope;
        private readonly bool _serverGeneratesMutationEnvelopes;
        private readonly Dictionary<RbxInstance, LuaValue> _proxyCache = new();
        private readonly LuaTable _instanceMeta;

        public LuaCsRbxModContext(LuaCsRbxApiBindings bindings, LuaCapabilities capabilities,
            string ownerModId, string originTag)
            : this(bindings, capabilities, ownerModId, originTag,
                ResolveActorContext(bindings, ownerModId, originTag), null, false)
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

            // WHY: stamp this load's connection generation BEFORE the mod chunk runs, so every Connect
            // the chunk makes is tracked under it. On reload a fresh context bumps the generation first
            // (BuildMod runs before the reload teardown), letting teardown disconnect only the previous
            // generation and keep this chunk's connections. Mirrors the logic-slot keepState exclusion.
            ConnectionGeneration = bindings.Connections?.BeginGeneration(ownerModId) ?? 0;
            _instanceMeta = LuaCsRbxInstanceBindings.BuildInstanceMeta(this);
        }

        internal static ActorContext ResolveActorContext(LuaCsRbxApiBindings bindings,
            string ownerModId, string originTag)
        {
            if (bindings.Registry.TryGetActorAttribution(
                    ownerModId, originTag, out string ownerActorId))
            {
                LocalActorIdentityProvider provider = new(
                    ownerActorId,
                    Guid.NewGuid().ToString("N"),
                    bindings.Registry.WorldId,
                    ActorGrantSet.None,
                    AgentMemoryScope.Empty);
                return provider.GetActorContext(BuiltInAgentRoleIds.Programmer);
            }

            return CoreServicesInstaller.DefaultLocalHostIdentityProvider
                .GetActorContext(BuiltInAgentRoleIds.Programmer);
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

        /// <summary>Sink storing BasePart spatial/appearance state in Roblox space (shared world).</summary>
        public IPartPropertySink PartSink => Bindings.PartSink;

        /// <summary>
        /// Wraps an instance keeping one proxy per instance so Lua <c>==</c> and table keys behave
        /// like Roblox reference identity.
        /// TODO: MVP5 — prune destroyed entries during the hot-reload teardown sweep.
        /// </summary>
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

            LuaValue proxy = new(new LuaCsRbxInstanceProxy(instance, this, _instanceMeta));
            _proxyCache[instance] = proxy;
            return proxy;
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
            RequireWorldEdit(target.ClassName + ":PivotTo");
            RequireMutationTarget(target, "pivot");
            Bindings.Registry.AuthorizeMutation(ActorContext.ActorId,
                ActorContext.Grants.IsUnrestricted, ActorContext.WorldId, target,
                RegistryWorldAclDecision.WriteProperty, "pivot");
            foreach (RbxInstance descendant in target.GetDescendants())
            {
                if (descendant.IsA("PVInstance"))
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
        private readonly struct RbxMethodBinding
        {
            public RbxMethodBinding(LuaValue value, string declaringClassName)
            {
                Value = value;
                DeclaringClassName = declaringClassName;
            }

            public LuaValue Value { get; }

            public string DeclaringClassName { get; }
        }

        public static LuaTable BuildInstanceMeta(LuaCsRbxModContext context)
        {
            Dictionary<string, RbxMethodBinding> methods = BuildMethods(context);
            RbxEnumItem deferredSignalBehavior = EnsureDeferredSignalBehavior(context.Bindings.Enums);

            LuaTable meta = new();
            meta[Metamethods.Index] = Fn("Instance.__index", ctx =>
            {
                RbxInstance self = Self(ctx, context);
                string key = ReadString(ctx, 1, "Instance member access");
                ThrowIfDestroyedForLua(self, key);
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
                    case "WaitForChild": return ReadWaitForChildBridge(ctx);
                }

                if (TryReadNetworkMember(context, self, key, ctx,
                        out LuaValue networkValue))
                {
                    return networkValue;
                }

                if (methods.TryGetValue(key, out RbxMethodBinding method)
                    && (method.DeclaringClassName == null || self.IsA(method.DeclaringClassName)))
                {
                    return method.Value;
                }

                if (key == "SignalBehavior" && self.IsA("Workspace"))
                {
                    return LuaCsRbxDatatypeBindings.Wrap(deferredSignalBehavior);
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

                if (TryReadModelPivot(context, self, key, out LuaValue modelPivotValue))
                {
                    return modelPivotValue;
                }

                if (TryReadSpatial(context, self, key, out LuaValue spatial))
                {
                    return spatial;
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
                            self.Name = ReadString(ctx, 2, "Instance.Name assignment");
                            return LuaValue.Nil;
                        case "Parent":
                        // WHY: no destroyed pre-check here — the Domain setter raises the exact
                        // D6 PARENT_LOCKED message for destroyed instances.
                        RbxInstance destination = ReadOptionalInstance(
                            value, "Instance.Parent assignment");
                        if (!context.Bindings.Registry.IsWorldAclEnabled
                            && IsProtectedSingleton(self))
                        {
                            // WHY: a service's Parent is locked in Roblox — reparenting (or nil-ing)
                            // it would detach it so game:GetService stops resolving it for the world.
                            throw RbxError.BadArgument(
                                self.ClassName + ".Parent is locked — it is a shared singleton",
                                "services and workspace.CurrentCamera are fixed for the world's lifetime");
                        }

                            context.RequireReparent(self, destination);
                            self.Parent = destination;
                            return LuaValue.Nil;
                        case "Archivable":
                            ThrowIfDestroyedForLua(self, key);
                            context.RequireWorldEditForWrite(self, "Archivable");
                            self.Archivable = value.ToBoolean();
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

                if (TryWriteModelPivot(context, self, key, value))
                {
                    return LuaValue.Nil;
                }

                if (TryWriteSpatial(context, self, key, value))
                {
                    return LuaValue.Nil;
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
            LuaValue taskValue = ctx.State.Environment["task"];
            if (taskValue.Type != LuaValueType.Table)
            {
                throw RbxError.BadArgument(
                    "Instance.WaitForChild requires the task scheduler bridge",
                    "run WaitForChild from a loaded mod scheduler thread");
            }

            LuaValue bridge = taskValue.Read<LuaTable>()["_waitForChildBridge"];
            if (bridge.Type != LuaValueType.Function)
            {
                throw RbxError.BadArgument(
                    "Instance.WaitForChild bridge is unavailable",
                    "run WaitForChild after the mod scheduler initializes");
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
                }
            }

            if (instance is RbxPlayer player && member == "UserId")
            {
                value = (double)player.UserId;
                return true;
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

        private static Dictionary<string, RbxMethodBinding> BuildMethods(LuaCsRbxModContext context)
        {
            Dictionary<string, RbxMethodBinding> methods = new(StringComparer.Ordinal);

            void Method(string name, Func<LuaFunctionExecutionContext, RbxInstance, LuaValue> body,
                string declaringClassName = null)
            {
                LuaValue value = new(Fn("Instance." + name, ctx =>
                    {
                        RbxInstance self = Self(ctx, context);
                        ThrowIfDestroyedForLua(self, name);
                        if (!IsMutatingMethod(name))
                        {
                            return body(ctx, self);
                        }

                        return context.ApplyServerGeneratedMutation(
                            "invoke Instance:" + name,
                            () => body(ctx, self));
                    }, context));
                methods[name] = new RbxMethodBinding(value, declaringClassName);
            }

            // ---- Navigation ----
            Method("FindFirstChild", (ctx, self) => context.WrapInstance(self.FindFirstChild(
                ReadString(ctx, 1, "FindFirstChild"), Arg(ctx, 2).ToBoolean())));
            Method("FindFirstChildOfClass", (ctx, self) => context.WrapInstance(
                self.FindFirstChildOfClass(ReadString(ctx, 1, "FindFirstChildOfClass"))));
            Method("FindFirstChildWhichIsA", (ctx, self) => context.WrapInstance(
                self.FindFirstChildWhichIsA(
                    ReadString(ctx, 1, "FindFirstChildWhichIsA"), Arg(ctx, 2).ToBoolean())));
            Method("FindFirstAncestor", (ctx, self) => context.WrapInstance(
                self.FindFirstAncestor(ReadString(ctx, 1, "FindFirstAncestor"))));
            Method("FindFirstAncestorOfClass", (ctx, self) => context.WrapInstance(
                self.FindFirstAncestorOfClass(ReadString(ctx, 1, "FindFirstAncestorOfClass"))));
            Method("FindFirstAncestorWhichIsA", (ctx, self) => context.WrapInstance(
                self.FindFirstAncestorWhichIsA(ReadString(ctx, 1, "FindFirstAncestorWhichIsA"))));
            Method("GetChildren", (_, self) => WrapList(context, self.GetChildren()));
            Method("GetDescendants", (_, self) => WrapList(context, self.GetDescendants()));
            Method("IsA", (ctx, self) => self.IsA(ReadString(ctx, 1, "IsA")));
            Method("IsDescendantOf", (ctx, self) => self.IsDescendantOf(
                ReadOptionalInstance(Arg(ctx, 1), "IsDescendantOf")));
            Method("IsAncestorOf", (ctx, self) => self.IsAncestorOf(
                ReadOptionalInstance(Arg(ctx, 1), "IsAncestorOf")));
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
                context.RequireDestroyTree(self, "destroy");
                if (!context.Bindings.Registry.IsWorldAclEnabled
                    && IsProtectedSingleton(self))
                {
                    // WHY: a shared service is cached once at composition and never re-resolved, so
                    // destroying it would brick input/lighting/etc for every mod; Roblox locks these
                    // against destruction too.
                    throw RbxError.BadArgument(
                        self.ClassName + " cannot be destroyed — it is a shared singleton",
                        "services and workspace.CurrentCamera live for the world's lifetime; "
                        + "never Destroy them");
                }

                self.Destroy();
                return LuaValue.Nil;
            });
            Method("ClearAllChildren", (_, self) =>
            {
                // WHY: game:ClearAllChildren() must not wipe the world's services (Roblox locks
                // them). GetChildren returns a snapshot, so destroying non-protected children while
                // iterating is safe; protected singletons (services/Camera) are left intact.
                List<RbxInstance> destroyRoots = new();
                foreach (RbxInstance child in self.GetChildren())
                {
                    if (!IsProtectedSingleton(child))
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

            // ---- Attributes / tags ----
            Method("GetAttribute", (ctx, self) => AttributeToLua(
                self.GetAttribute(ReadString(ctx, 1, "GetAttribute"))));
            Method("SetAttribute", (ctx, self) =>
            {
                context.RequireMetadataMutation(self, "set attribute");
                self.SetAttribute(
                    ReadString(ctx, 1, "SetAttribute"), AttributeFromLua(Arg(ctx, 2)));
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
                context.RequireMetadataMutation(self, "add tag");
                self.AddTag(ReadString(ctx, 1, "AddTag"));
                return LuaValue.Nil;
            });
            Method("RemoveTag", (ctx, self) =>
            {
                context.RequireMetadataMutation(self, "remove tag");
                self.RemoveTag(ReadString(ctx, 1, "RemoveTag"));
                return LuaValue.Nil;
            });
            Method("HasTag", (ctx, self) => self.HasTag(ReadString(ctx, 1, "HasTag")));
            Method("GetTags", (_, self) =>
            {
                LuaTable table = new();
                int index = 1;
                foreach (string tag in self.GetTags())
                {
                    table[index++] = tag;
                }

                return new LuaValue(table);
            });
            Method("GetAttributeChangedSignal", (ctx, self) => LuaCsRbxDatatypeBindings.Wrap(
                self.GetAttributeChangedSignal(ReadString(ctx, 1, "GetAttributeChangedSignal")), context));
            Method("GetPropertyChangedSignal", (ctx, self) => LuaCsRbxDatatypeBindings.Wrap(
                self.GetPropertyChangedSignal(ReadString(ctx, 1, "GetPropertyChangedSignal")), context));

            Method("GetPivot", (_, self) => LuaCsRbxDatatypeBindings.Wrap(
                GetPivot(context.PartSink, self)), "PVInstance");
            Method("PivotTo", (ctx, self) =>
            {
                context.RequirePivotMutation(self);
                PivotTo(context, self,
                    ReadCFrameValue(Arg(ctx, 1), "PVInstance:PivotTo argument 1"));
                return LuaValue.Nil;
            }, "PVInstance");

            // ---- ServiceProvider (DataModel) ----
            Method("GetService", (ctx, self) => context.WrapInstance(
                    RequireDataModel(self, "GetService").GetService(ReadString(ctx, 1, "GetService"))),
                "ServiceProvider");
            Method("FindService", (ctx, self) => context.WrapInstance(
                    RequireDataModel(self, "FindService").FindService(ReadString(ctx, 1, "FindService"))),
                "ServiceProvider");
            Method("BindToClose", (ctx, self) =>
            {
                LuaValue callback = Arg(ctx, 1);
                if (callback.Type != LuaValueType.Function)
                {
                    throw RbxError.BadArgument(
                        "game:BindToClose expects a function at argument 1, got " + Describe(callback),
                        "pass the function to run at shutdown");
                }

                RequireDataModel(self, "BindToClose").BindToClose(callback.Read<LuaFunction>());
                return LuaValue.Nil;
            }, "DataModel");

            Method("GetPlayers", (_, self) => WrapList(
                context, ((RbxPlayers)self).GetPlayers()), "Players");

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
                   || name == "PivotTo";
        }

        private static RbxPlayer ReadPlayer(LuaFunctionExecutionContext ctx,
            int index, string functionName)
        {
            if (TryGetInstance(Arg(ctx, index), out LuaCsRbxInstanceProxy proxy)
                && proxy.Instance is RbxPlayer player)
            {
                return player;
            }

            throw RbxError.BadArgument(
                functionName + " expects a Player at argument " + index,
                "pass a Player returned by Players:GetPlayers()");
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

        /// <summary>Enforces DEV-7, including the destruction-handler tombstone exception.</summary>
        // WHY: services (UserInputService/Lighting/Workspace/…) and the canonical Camera are
        // world-lifetime singletons; the lifecycle bindings refuse to Clone/Destroy them so one mod
        // cannot brick a shared service for every other mod.
        private static bool IsProtectedSingleton(RbxInstance instance)
        {
            return instance.IsService || instance.ClassName == "Camera";
        }

        private static void ThrowIfDestroyedForLua(RbxInstance instance, string memberName)
        {
            bool tombstoneMember = memberName == "Name"
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
        // be walked and copied separately; Clone preserves archivable child order, so the trees align.
        // TODO: MVP2 — move this sink-copy into a registry-level clone seam so completeness no
        // longer depends on each Clone call site (the registry already owns the binder/sink).
        private static void CopyPartSinkState(IPartPropertySink sink, RbxInstance source,
            RbxInstance copy)
        {
            if (sink == null || source == null || copy == null)
            {
                return;
            }

            if (sink.TryGetPartProperties(source.Id, out PartProperties properties))
            {
                sink.SetPartProperties(copy.Id, in properties);
            }

            IReadOnlyList<RbxInstance> sourceChildren = source.GetChildren();
            IReadOnlyList<RbxInstance> copyChildren = copy.GetChildren();
            int copyIndex = 0;
            for (int i = 0; i < sourceChildren.Count && copyIndex < copyChildren.Count; i++)
            {
                // WHY: Clone drops Archivable == false subtrees, so a non-archivable source child
                // has no counterpart in the copy — advance only the source side past it.
                if (!sourceChildren[i].Archivable)
                {
                    continue;
                }

                CopyPartSinkState(sink, sourceChildren[i], copyChildren[copyIndex]);
                copyIndex++;
            }
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

        private static RbxInstance ReadOptionalInstance(LuaValue value, string what)
        {
            if (value.Type == LuaValueType.Nil)
            {
                return null;
            }

            if (TryGetInstance(value, out LuaCsRbxInstanceProxy proxy))
            {
                return proxy.Instance;
            }

            throw RbxError.BadArgument(
                what + " expects an Instance or nil",
                "pass an Instance, got " + Describe(value));
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
                    model.SetPrimaryPart(ReadOptionalInstance(
                        value, "Model.PrimaryPart assignment"));
                    return true;
                case "WorldPivot":
                    context.RequireWorldEditForWrite(self, "WorldPivot");
                    RbxCFrame worldPivot = ReadCFrameValue(
                        value, "Model.WorldPivot assignment");
                    model.SetWorldPivot(in worldPivot);
                    return true;
                default:
                    return false;
            }
        }

        private static RbxCFrame GetPivot(IPartPropertySink sink, RbxInstance instance)
        {
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

            throw RbxError.BadArgument(
                "GetPivot is not available on " + instance.ClassName,
                "call GetPivot on a BasePart or Model");
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

        private static void PivotTo(LuaCsRbxModContext context, RbxInstance instance,
            RbxCFrame target)
        {
            IPartPropertySink sink = context.PartSink;
            if (instance.IsA("BasePart"))
            {
                sink.SetCFrame(instance.Id, target);
                context.RecordMutation(instance);
                return;
            }

            if (!(instance is RbxModel model))
            {
                throw RbxError.BadArgument(
                    "PivotTo is not available on " + instance.ClassName,
                    "call PivotTo on a BasePart or Model");
            }

            RbxCFrame transform = target * GetPivot(sink, model).Inverse();
            List<RbxInstance> parts = new();
            List<RbxCFrame> partCFrames = new();
            List<RbxModel> models = new() { model };
            List<RbxCFrame> modelWorldPivots = new() { GetWorldPivot(sink, model) };
            foreach (RbxInstance descendant in model.GetDescendants())
            {
                if (descendant.IsA("BasePart"))
                {
                    parts.Add(descendant);
                    partCFrames.Add(sink.GetPartPropertiesOrDefault(descendant.Id).CFrame);
                }

                if (descendant is RbxModel descendantModel)
                {
                    models.Add(descendantModel);
                    modelWorldPivots.Add(GetWorldPivot(sink, descendantModel));
                }
            }

            for (int partIndex = 0; partIndex < parts.Count; partIndex++)
            {
                RbxInstance part = parts[partIndex];
                sink.SetCFrame(part.Id, transform * partCFrames[partIndex]);
                context.RecordMutation(part);
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
        /// setting Position keeps orientation, setting CFrame sets both).</summary>
        private static bool TryWriteSpatial(LuaCsRbxModContext context, RbxInstance self, string key,
            LuaValue value)
        {
            if (!self.IsA("BasePart"))
            {
                return false;
            }

            IPartPropertySink sink = context.PartSink;
            InstanceId id = self.Id;
            switch (key)
            {
                case "Shape":
                    context.RequireWorldEditForWrite(self, "Shape");
                    sink.SetShape(id, ReadPartShapeValue(value));
                    context.RecordMutation(self);
                    return true;
                case "Material":
                    context.RequireWorldEditForWrite(self, "Material");
                    RbxMaterialId material = ReadMaterialValue(value);
                    sink.SetMaterial(id, in material);
                    context.RecordMutation(self);
                    return true;
                case "MaterialVariant":
                    context.RequireWorldEditForWrite(self, "MaterialVariant");
                    sink.SetMaterialVariant(id,
                        ReadOptionalString(value, "Part.MaterialVariant assignment"));
                    context.RecordMutation(self);
                    return true;
                case "Position":
                    context.RequireWorldEditForWrite(self, "Position");
                    sink.SetPosition(id, ReadVector3Value(value, "Part.Position assignment"));
                    context.RecordMutation(self);
                    return true;
                case "Size":
                    context.RequireWorldEditForWrite(self, "Size");
                    sink.SetSize(id, ReadVector3Value(value, "Part.Size assignment"));
                    context.RecordMutation(self);
                    return true;
                case "CFrame":
                    context.RequireWorldEditForWrite(self, "CFrame");
                    sink.SetCFrame(id, ReadCFrameValue(value, "Part.CFrame assignment"));
                    context.RecordMutation(self);
                    return true;
                case "Orientation":
                    context.RequireWorldEditForWrite(self, "Orientation");
                    RbxVector3 orientation = ReadVector3Value(value, "Part.Orientation assignment");
                    PartProperties orientationProperties = sink.GetPartPropertiesOrDefault(id);
                    RbxCFrame orientationCFrame = RbxCFrame.FromOrientation(
                        orientation.X * MathF.PI / 180f,
                        orientation.Y * MathF.PI / 180f,
                        orientation.Z * MathF.PI / 180f);
                    sink.SetCFrame(id,
                        RbxCFrame.FromPosition(orientationProperties.Position) * orientationCFrame);
                    context.RecordMutation(self);
                    return true;
                case "Rotation":
                    context.RequireWorldEditForWrite(self, "Rotation");
                    RbxVector3 rotation = ReadVector3Value(value, "Part.Rotation assignment");
                    PartProperties rotationProperties = sink.GetPartPropertiesOrDefault(id);
                    RbxCFrame rotationCFrame = RbxCFrame.FromEulerAnglesXYZ(
                        rotation.X * MathF.PI / 180f,
                        rotation.Y * MathF.PI / 180f,
                        rotation.Z * MathF.PI / 180f);
                    sink.SetCFrame(id,
                        RbxCFrame.FromPosition(rotationProperties.Position) * rotationCFrame);
                    context.RecordMutation(self);
                    return true;
                case "Color":
                    context.RequireWorldEditForWrite(self, "Color");
                    sink.SetColor(id, ReadColor3Value(value, "Part.Color assignment"));
                    context.RecordMutation(self);
                    return true;
                case "Transparency":
                    context.RequireWorldEditForWrite(self, "Transparency");
                    sink.SetTransparency(id, ReadNumberValue(value, "Part.Transparency assignment"));
                    context.RecordMutation(self);
                    return true;
                case "Anchored":
                    context.RequireWorldEditForWrite(self, "Anchored");
                    sink.SetAnchored(id, value.ToBoolean());
                    context.RecordMutation(self);
                    return true;
                case "CanCollide":
                    context.RequireWorldEditForWrite(self, "CanCollide");
                    sink.SetCanCollide(id, value.ToBoolean());
                    context.RecordMutation(self);
                    return true;
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

        private static RbxPartShape ReadPartShapeValue(LuaValue value)
        {
            if (TryUnbox(value, out RbxEnumItem item) && item.EnumType.Name == "PartType")
            {
                return (RbxPartShape)item.Value;
            }

            throw RbxError.BadArgument(
                "Part.Shape assignment expects an Enum.PartType item",
                "pass Enum.PartType.Block/Ball/Cylinder/Wedge/CornerWedge, got "
                + Describe(value));
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

        private static RbxMaterialId ReadMaterialValue(LuaValue value)
        {
            if (TryUnbox(value, out RbxEnumItem item) && item.EnumType.Name == "Material")
            {
                return new RbxMaterialId(item.Name, item.Value);
            }

            throw RbxError.BadArgument(
                "Part.Material assignment expects an Enum.Material item",
                "pass an item like Enum.Material.Plastic/Neon/Wood, got " + Describe(value));
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
            if (TryUnbox(value, out RbxEnumItem item) && item.EnumType.Name == "MouseBehavior")
            {
                service.MouseBehavior = item;
                context.RecordMutation(self);
                return true;
            }

            throw RbxError.BadArgument(
                "UserInputService.MouseBehavior assignment expects an Enum.MouseBehavior item",
                "pass Enum.MouseBehavior.Default/LockCenter/LockCurrentPosition, got "
                + Describe(value));
        }

        private static RbxEnumItem ReadKeyCodeArg(LuaFunctionExecutionContext ctx, int index,
            string what)
        {
            if (TryUnbox(Arg(ctx, index), out RbxEnumItem item) && item.EnumType.Name == "KeyCode")
            {
                return item;
            }

            throw RbxError.BadArgument(
                what + " expects an Enum.KeyCode item at argument " + index,
                "pass e.g. Enum.KeyCode.Space, got " + Describe(Arg(ctx, index))
                                                     + " at argument " + index);
        }

        // ---- RunService (per-frame game-loop signals over the host Step pump) ----------------

        /// <summary>RunService members: the Heartbeat/Stepped/RenderStepped signals. Reads are open
        /// at the Read tier — connecting a per-frame handler observes the loop, it mutates nothing.</summary>
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
            detector.MaxActivationDistance = ReadNumberValue(value, "ClickDetector.MaxActivationDistance");
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
                    variant.BaseMaterial = ReadMaterialValue(value);
                    context.RecordMutation(self);
                    context.PartSink.RefreshMaterialVariant(variant.Name);
                    return true;
                case "ColorMap":
                    context.RequireWorldEditForWrite(self, "ColorMap");
                    variant.ColorMap = ReadStringValue(value, "MaterialVariant.ColorMap assignment");
                    context.RecordMutation(self);
                    context.PartSink.RefreshMaterialVariant(variant.Name);
                    return true;
                case "NormalMap":
                    context.RequireWorldEditForWrite(self, "NormalMap");
                    variant.NormalMap = ReadStringValue(value, "MaterialVariant.NormalMap assignment");
                    context.RecordMutation(self);
                    context.PartSink.RefreshMaterialVariant(variant.Name);
                    return true;
                case "RoughnessMap":
                    context.RequireWorldEditForWrite(self, "RoughnessMap");
                    variant.RoughnessMap =
                        ReadStringValue(value, "MaterialVariant.RoughnessMap assignment");
                    context.RecordMutation(self);
                    context.PartSink.RefreshMaterialVariant(variant.Name);
                    return true;
                case "MetalnessMap":
                    context.RequireWorldEditForWrite(self, "MetalnessMap");
                    variant.MetalnessMap =
                        ReadStringValue(value, "MaterialVariant.MetalnessMap assignment");
                    context.RecordMutation(self);
                    context.PartSink.RefreshMaterialVariant(variant.Name);
                    return true;
                case "StudsPerTile":
                    context.RequireWorldEditForWrite(self, "StudsPerTile");
                    variant.StudsPerTile =
                        ReadNumberValue(value, "MaterialVariant.StudsPerTile assignment");
                    context.RecordMutation(self);
                    context.PartSink.RefreshMaterialVariant(variant.Name);
                    return true;
                default:
                    return false;
            }
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

            switch (key)
            {
                case "CFrame":
                    context.RequireWorldEditForWrite(self, "CFrame");
                    context.Bindings.CameraRig.SetCFrame(
                        ReadCFrameValue(value, "Camera.CFrame assignment"));
                    context.RecordMutation(self);
                    return true;
                case "CameraType":
                    context.RequireWorldEditForWrite(self, "CameraType");
                    context.Bindings.CameraTypeItem = ReadCameraTypeValue(value);
                    context.RecordMutation(self);
                    return true;
                case "CameraSubject":
                    context.RequireWorldEditForWrite(self, "CameraSubject");
                    context.Bindings.SetCameraSubject(
                        ReadOptionalInstance(value, "Camera.CameraSubject assignment"));
                    context.RecordMutation(self);
                    return true;
                default:
                    return false;
            }
        }

        private static RbxEnumItem ReadCameraTypeValue(LuaValue value)
        {
            if (TryUnbox(value, out RbxEnumItem item) && item.EnumType.Name == "CameraType")
            {
                return item;
            }

            throw RbxError.BadArgument(
                "Camera.CameraType assignment expects an Enum.CameraType item",
                "pass e.g. Enum.CameraType.Scriptable, got " + Describe(value));
        }

        private static RbxVector3 ReadVector3Value(LuaValue value, string what)
        {
            if (TryUnbox(value, out RbxVector3 vector))
            {
                return vector;
            }

            throw RbxError.BadArgument(
                what + " expects a Vector3",
                "pass a Vector3, got " + Describe(value));
        }

        private static RbxCFrame ReadCFrameValue(LuaValue value, string what)
        {
            if (TryUnbox(value, out RbxCFrame cframe))
            {
                return cframe;
            }

            throw RbxError.BadArgument(
                what + " expects a CFrame",
                "pass a CFrame, got " + Describe(value));
        }

        private static RbxColor3 ReadColor3Value(LuaValue value, string what)
        {
            if (TryUnbox(value, out RbxColor3 color))
            {
                return color;
            }

            throw RbxError.BadArgument(
                what + " expects a Color3",
                "pass a Color3, got " + Describe(value));
        }

        private static float ReadNumberValue(LuaValue value, string what)
        {
            if (value.Type == LuaValueType.Number)
            {
                return (float)value.Read<double>();
            }

            throw RbxError.BadArgument(
                what + " expects a number",
                "pass a number, got " + Describe(value));
        }

        private static string ReadStringValue(LuaValue value, string what)
        {
            if (value.Type == LuaValueType.String)
            {
                return value.Read<string>();
            }

            throw RbxError.BadArgument(
                what + " expects a string",
                "pass a string, got " + Describe(value));
        }

        /// <summary>Nil or empty clears to no override (null); otherwise the variant name.</summary>
        private static string ReadOptionalString(LuaValue value, string what)
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

            throw RbxError.BadArgument(
                what + " expects a string or nil",
                "pass a variant name, \"\" or nil for plain material, got " + Describe(value));
        }

    }
}
