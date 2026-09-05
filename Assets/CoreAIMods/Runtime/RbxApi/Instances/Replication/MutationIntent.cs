using System;

namespace CoreAI.Mods.Rbx.Instances.Replication
{
    /// <summary>What a client is asking the server to change.</summary>
    public enum MutationIntentAction
    {
        /// <summary>Assign a property.</summary>
        WriteProperty,

        /// <summary>Set or clear an attribute.</summary>
        SetAttribute,

        /// <summary>Add a tag.</summary>
        AddTag,

        /// <summary>Remove a tag.</summary>
        RemoveTag,

        /// <summary>Move to a different parent.</summary>
        Reparent,

        /// <summary>Create a new instance.</summary>
        Create,

        /// <summary>Destroy an instance.</summary>
        Destroy
    }

    /// <summary>
    /// One request from a client to change the world, as it travels.
    /// </summary>
    /// <remarks>
    /// <b>It carries no actor, owner, role, grant id or capability</b> — and that absence is the
    /// design. Every one of those would be a field a client could set, and a field a client can set
    /// is a field a server must not trust; leaving them out means there is nothing to validate and
    /// nothing to forget to validate. The sender is stamped by the bridge from its own connection
    /// map, and the permission is looked up server-side against the ledger.
    /// <para>
    /// <c>ExpectedRevision</c> is what makes a stale intent refusable: a client that acted on a view
    /// the server has since moved past gets a refusal instead of overwriting someone else's change.
    /// <c>OperationId</c> is what makes a replayed intent harmless — the same id applied twice is
    /// applied once.
    /// </para>
    /// </remarks>
    public sealed class MutationIntent
    {
        /// <summary>Creates an intent. Every field is data the server re-validates.</summary>
        public MutationIntent(string operationId, InstanceId targetInstanceId, long expectedRevision,
            MutationIntentAction action, string member, byte[] encodedValue)
        {
            if (string.IsNullOrWhiteSpace(operationId))
            {
                throw new ArgumentException(
                    "An intent without an operation id cannot be replayed safely: the server would "
                    + "apply a duplicate as a second change.", nameof(operationId));
            }

            OperationId = operationId;
            TargetInstanceId = targetInstanceId;
            ExpectedRevision = expectedRevision;
            Action = action;
            Member = member ?? "";
            EncodedValue = encodedValue ?? Array.Empty<byte>();
        }

        /// <summary>Client-generated id, unique per intent; makes a replay idempotent.</summary>
        public string OperationId { get; }

        /// <summary>The instance the client wants changed.</summary>
        public InstanceId TargetInstanceId { get; }

        /// <summary>The revision the client believed it was acting on.</summary>
        public long ExpectedRevision { get; }

        /// <summary>What the client wants done.</summary>
        public MutationIntentAction Action { get; }

        /// <summary>The property, attribute or tag name; empty for actions that need none.</summary>
        public string Member { get; }

        /// <summary>The encoded new value; empty for actions that carry none.</summary>
        public byte[] EncodedValue { get; }

        /// <summary>The grant action this intent needs, so one mapping serves every check.</summary>
        public WriteGrantActions RequiredGrant()
        {
            switch (Action)
            {
                case MutationIntentAction.WriteProperty: return WriteGrantActions.WriteProperty;
                case MutationIntentAction.SetAttribute: return WriteGrantActions.SetAttribute;
                case MutationIntentAction.AddTag:
                case MutationIntentAction.RemoveTag: return WriteGrantActions.Tag;
                case MutationIntentAction.Reparent: return WriteGrantActions.Reparent;
                case MutationIntentAction.Create: return WriteGrantActions.Create;
                case MutationIntentAction.Destroy: return WriteGrantActions.Destroy;
                default:
                    // A new action with no grant mapping must not fall through to "allowed".
                    throw RbxError.BadArgument(
                        "mutation action " + Action + " has no write-grant mapping",
                        "add the action to WriteGrantActions and to this mapping together");
            }
        }
    }

    /// <summary>How a client's local write behaves before the server has answered.</summary>
    /// <remarks>
    /// WHY there are exactly two values and no <c>Open</c>: an "apply locally and let it stand"
    /// mode is a client authoritative over the world, which is the thing server-authoritative
    /// replication exists to prevent. Its absence is deliberate — a third value would be reachable
    /// by configuration, and configuration is what gets copied from a tutorial.
    /// </remarks>
    public enum ClientWritePolicy
    {
        /// <summary>
        /// Roblox's own behaviour: the write applies on the client and is overwritten by the next
        /// server delta. Nothing is replicated; the client simply sees its own change until the
        /// server disagrees.
        /// </summary>
        RobloxParity,

        /// <summary>
        /// The write raises <c>NOT_AUTHORITY</c> immediately. Stricter than Roblox, and clearer:
        /// a script that would have silently reverted fails where the mistake is.
        /// </summary>
        Strict
    }

    /// <summary>What a client write should do, decided before anything is applied.</summary>
    public enum ClientWriteDisposition
    {
        /// <summary>Apply locally, replicate nothing; the next server delta wins.</summary>
        ApplyLocallyOnly,

        /// <summary>Refuse the write with NOT_AUTHORITY.</summary>
        Reject,

        /// <summary>Send an intent and wait for the authoritative answer; apply nothing locally.</summary>
        ForwardAsIntent
    }

    /// <summary>
    /// Decides what a client's attempted write does, given the policy and the host's grants.
    /// </summary>
    /// <remarks>
    /// WHY a granted write forwards instead of also applying locally: applying and forwarding means
    /// predicting, and a prediction that the server later refuses leaves the client showing a world
    /// that never existed. Waiting costs a round trip and never diverges.
    /// </remarks>
    public static class ClientWriteAuthority
    {
        /// <summary>Resolves the disposition for one attempted client write.</summary>
        public static ClientWriteDisposition Resolve(ClientWritePolicy policy,
            WriteGrantLedger ledger, string actorId, InstanceId target,
            WriteGrantActions action)
        {
            if (ledger != null && ledger.Allows(actorId, target, action))
            {
                return ClientWriteDisposition.ForwardAsIntent;
            }

            return policy == ClientWritePolicy.Strict
                ? ClientWriteDisposition.Reject
                : ClientWriteDisposition.ApplyLocallyOnly;
        }
    }
}
