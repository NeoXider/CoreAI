using System;

namespace CoreAI.Mods.Rbx.Instances.Replication
{
    /// <summary>What the server decided about one client intent.</summary>
    public sealed class IntentOutcome
    {
        private IntentOutcome(bool applied, long revision, string reasonCode, string reason)
        {
            Applied = applied;
            Revision = revision;
            ReasonCode = reasonCode ?? "";
            Reason = reason ?? "";
        }

        /// <summary>Whether the world changed.</summary>
        public bool Applied { get; }

        /// <summary>The revision after applying; zero on a refusal.</summary>
        public long Revision { get; }

        /// <summary>Machine-readable refusal code, empty when applied.</summary>
        public string ReasonCode { get; }

        /// <summary>Why it was refused, naming the actor and what was asked.</summary>
        public string Reason { get; }

        /// <summary>The intent changed the world.</summary>
        public static IntentOutcome Ok(long revision)
        {
            return new IntentOutcome(true, revision, "", "");
        }

        /// <summary>The intent was refused; canonical state is unchanged.</summary>
        public static IntentOutcome Refused(string reasonCode, string reason)
        {
            return new IntentOutcome(false, 0L, reasonCode, reason);
        }
    }

    /// <summary>
    /// The one place a client's request to change the world is judged.
    /// </summary>
    /// <remarks>
    /// WHY one gateway and not a check at each call site: a second path is a second place to forget
    /// a check, and the checks here are the ones whose omission is invisible until someone exploits
    /// it. Every client mutation in an online world goes through this method or it does not happen.
    /// <para>
    /// The order is deliberate and each step leaves canonical state untouched on failure:
    /// <list type="number">
    /// <item><description>the actor is the bridge-stamped sender, never a field from the packet;</description></item>
    /// <item><description>an unrestricted actor is REFUSED — the host never uses this path, so a
    /// spoofed host id can only ever produce a refusal rather than an elevation;</description></item>
    /// <item><description>the rate budget, in its own bucket, tighter than remotes;</description></item>
    /// <item><description>the payload size;</description></item>
    /// <item><description>the host's grant for exactly this action on exactly this target;</description></item>
    /// <item><description>the world ACL, unchanged and unwidened — a grant never buys past it;</description></item>
    /// <item><description>the mutation envelope, which makes a replay idempotent and a stale
    /// revision a refusal.</description></item>
    /// </list>
    /// </para>
    /// <para>
    /// WHY it is engine-free with an injected applier: decoding the value needs the codec, which
    /// lives a layer up. Keeping the judgement here and the decoding there means the security order
    /// above is testable without a serializer, a transport or a scene.
    /// </para>
    /// </remarks>
    public sealed class IntentGateway
    {
        private readonly InstanceRegistry _registry;
        private readonly WriteGrantLedger _ledger;
        private readonly RbxNetworkRateLimiterAdapter _rate;
        private readonly Func<string, MutationIntent, long> _apply;
        private readonly ReplicationDirtySet _dirty;
        private readonly int _maxPayloadBytes;
        private readonly Action<string> _audit;

        /// <summary>Creates the gateway over one world.</summary>
        /// <param name="applyIntent">
        /// Applies an already-authorized intent through the same write helpers server Lua uses, and
        /// returns the resulting revision. It must not re-check authorization: that already happened
        /// here, and a second check in a second place is a second thing to get wrong.
        /// </param>
        public IntentGateway(InstanceRegistry registry, WriteGrantLedger ledger,
            Func<string, MutationIntent, long> applyIntent,
            ReplicationDirtySet dirty = null,
            RbxNetworkRateLimiterAdapter rate = null,
            int maxPayloadBytes = 65536,
            Action<string> audit = null)
        {
            _registry = registry ?? throw new ArgumentNullException(nameof(registry));
            _ledger = ledger ?? throw new ArgumentNullException(nameof(ledger));
            _apply = applyIntent ?? throw new ArgumentNullException(nameof(applyIntent));
            _dirty = dirty;
            _rate = rate;
            _maxPayloadBytes = maxPayloadBytes;
            _audit = audit;
        }

        /// <summary>Judges and, if allowed, applies one client intent.</summary>
        public IntentOutcome Handle(string senderActorId, bool senderIsUnrestricted,
            string senderWorldId, MutationIntent intent)
        {
            if (intent == null)
            {
                return IntentOutcome.Refused("BAD_ARGUMENT", "the intent was empty");
            }

            if (string.IsNullOrWhiteSpace(senderActorId))
            {
                return IntentOutcome.Refused("NOT_AUTHORITY",
                    "the intent carried no bridge-stamped sender; an unadmitted connection reaches "
                    + "nothing");
            }

            if (senderIsUnrestricted)
            {
                // The host's writes originate in the server process and never travel as intents, so
                // an unrestricted actor arriving here is either a bug or a forgery. Refusing means
                // a spoofed host id can only ever produce a refusal.
                return IntentOutcome.Refused("NOT_AUTHORITY",
                    "intents are for clients; host writes do not travel this path");
            }

            if (_rate != null && !_rate.TryAdmit(senderActorId, out string rateReason))
            {
                return IntentOutcome.Refused("BUDGET_EXCEEDED", rateReason);
            }

            if (intent.EncodedValue.Length > _maxPayloadBytes)
            {
                return IntentOutcome.Refused("PAYLOAD_TOO_LARGE",
                    "intent payload of " + intent.EncodedValue.Length + " bytes exceeds the "
                    + _maxPayloadBytes + " byte limit");
            }

            if (!_registry.TryGet(intent.TargetInstanceId, out RbxInstance target)
                || target.IsDestroyed)
            {
                return IntentOutcome.Refused("INSTANCE_DESTROYED",
                    "intent target " + intent.TargetInstanceId.Value + " is not in this world");
            }

            WriteGrantActions required;
            try
            {
                required = intent.RequiredGrant();
            }
            catch (RbxError error)
            {
                return IntentOutcome.Refused("BAD_ARGUMENT", error.Message);
            }

            if (!_ledger.Allows(senderActorId, intent.TargetInstanceId, required))
            {
                return IntentOutcome.Refused("NOT_AUTHORITY",
                    "actor '" + senderActorId + "' has no host grant covering " + intent.Action
                    + " on " + target.Name);
            }

            try
            {
                // WHY the ACL still runs: a grant says the host allowed this actor to write here, not
                // that the world's own rules stopped applying. A client granted the Workspace subtree
                // still cannot destroy a host-protected singleton or another actor's owned instance.
                WorldAclAuthorizer.Demand(_registry, senderActorId, isUnrestricted: false,
                    senderWorldId, target, AclDecisionFor(intent.Action), intent.Action.ToString());
            }
            catch (RbxError error)
            {
                return IntentOutcome.Refused("NOT_AUTHORITY", error.Message);
            }

            try
            {
                long revision = _registry.ApplyMutation(
                    new MutationEnvelope(senderActorId, intent.TargetInstanceId,
                        intent.OperationId, intent.ExpectedRevision),
                    () => _apply(senderActorId, intent));

                _dirty?.MarkDirty(intent.TargetInstanceId, revision);
                _audit?.Invoke("[intent] " + senderActorId + " " + intent.Action + " on "
                               + target.Name + " -> revision " + revision);
                return IntentOutcome.Ok(revision);
            }
            catch (RbxError error)
            {
                return IntentOutcome.Refused(error.Code.ToString(), error.Message);
            }
        }

        private static WorldAclDecision AclDecisionFor(MutationIntentAction action)
        {
            switch (action)
            {
                case MutationIntentAction.WriteProperty: return WorldAclDecision.WriteProperty;
                case MutationIntentAction.SetAttribute:
                case MutationIntentAction.AddTag:
                case MutationIntentAction.RemoveTag: return WorldAclDecision.MutateMetadata;
                case MutationIntentAction.Reparent: return WorldAclDecision.ReparentSelf;
                case MutationIntentAction.Destroy: return WorldAclDecision.Destroy;
                case MutationIntentAction.Create: return WorldAclDecision.AcceptChild;
                default:
                    // An unmapped action must not fall through to the most permissive decision.
                    throw RbxError.BadArgument(
                        "mutation action " + action + " has no ACL decision mapping",
                        "add the action to this mapping when adding it to MutationIntentAction");
            }
        }
    }

    /// <summary>
    /// The intent bucket of the shared rate limiter, as a refusal-returning adapter.
    /// </summary>
    /// <remarks>
    /// WHY a separate bucket: an intent costs the server a world mutation and a broadcast, which is
    /// far more than a RemoteEvent fire. Sharing one budget would let a client spend its whole
    /// allowance on writes and still look like ordinary traffic.
    /// </remarks>
    public sealed class RbxNetworkRateLimiterAdapter
    {
        private readonly Networking.RbxNetworkRateLimiter _limiter;
        private readonly Networking.RbxNetworkRateGroup _group;

        /// <summary>Wraps one bucket of a shared limiter.</summary>
        public RbxNetworkRateLimiterAdapter(Networking.RbxNetworkRateLimiter limiter,
            Networking.RbxNetworkRateGroup group)
        {
            _limiter = limiter ?? throw new ArgumentNullException(nameof(limiter));
            _group = group;
        }

        /// <summary>Counts one intent, returning false with the reason when the budget is spent.</summary>
        public bool TryAdmit(string actorId, out string reason)
        {
            try
            {
                _limiter.Admit(actorId, _group);
                reason = "";
                return true;
            }
            catch (RbxError error)
            {
                reason = error.Message;
                return false;
            }
        }
    }
}
