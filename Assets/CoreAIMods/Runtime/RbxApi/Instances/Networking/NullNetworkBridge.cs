using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace CoreAI.Mods.Rbx.Instances.Networking
{
    /// <summary>Deterministic unreliable-delivery behavior used by the loopback bridge.</summary>
    public enum RbxNullNetworkUnreliableBehavior
    {
        PassThrough,
        DropAll,
        ReverseAdjacentPairs
    }

    /// <summary>
    /// In-process solo bridge. Reliable events are delivered FIFO; unreliable events carry the
    /// weaker contract but the null transport is free to deliver them unchanged. Client admission
    /// uses Roblox's per-client, same-event-type grouping for reliable and unreliable events.
    /// OURS: RemoteFunction uses a third independent bucket with the same configurable limit because
    /// the offline Roblox reference does not specify its quota.
    /// </summary>
    public sealed class NullNetworkBridge : INetworkBridge
    {
        public const int DefaultMaxClientRequestsPerSecond = 500;

        private readonly RbxNetworkRateLimiter _rateLimiter;
        private readonly Func<double> _clockSeconds;
        private readonly HashSet<string> _actors = new(StringComparer.Ordinal);
        private readonly List<string> _actorOrder = new();
        private readonly Queue<RbxNetworkEventMessage> _eventQueue = new();
        private readonly RbxNullNetworkUnreliableBehavior _unreliableBehavior;
        private RbxNetworkEventMessage _heldUnreliableEvent;
        private bool _deliveringEvents;

        public NullNetworkBridge(
            int maxClientRequestsPerSecond = DefaultMaxClientRequestsPerSecond,
            Func<double> clockSeconds = null,
            RbxNullNetworkUnreliableBehavior unreliableBehavior =
                RbxNullNetworkUnreliableBehavior.PassThrough)
        {
            if (maxClientRequestsPerSecond <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(maxClientRequestsPerSecond));
            }

            _clockSeconds = clockSeconds ?? MonotonicSeconds;
            _rateLimiter = new RbxNetworkRateLimiter(maxClientRequestsPerSecond, _clockSeconds);
            if (unreliableBehavior != RbxNullNetworkUnreliableBehavior.PassThrough
                && unreliableBehavior != RbxNullNetworkUnreliableBehavior.DropAll
                && unreliableBehavior != RbxNullNetworkUnreliableBehavior.ReverseAdjacentPairs)
            {
                throw new ArgumentOutOfRangeException(nameof(unreliableBehavior));
            }

            _unreliableBehavior = unreliableBehavior;
        }

        public RbxNetworkTopology Topology => RbxNetworkTopology.Solo;

        public int MaxClientRequestsPerSecond => _rateLimiter.MaxClientRequestsPerSecond;

        public IReadOnlyList<string> ActorIds => _actorOrder.AsReadOnly();

        /// <summary>Current actor rate-window count for bounded-churn verification.</summary>
        public int RateWindowCount => _rateLimiter.TrackedActorCount;

        /// <summary>
        /// The codec's own ceiling: 64 KiB. The loopback has no MTU, so this is the only bound.
        /// </summary>
        /// <remarks>
        /// WHY a bound at all in solo: a payload that only fails once a real transport is attached
        /// would make "works in solo" mean nothing for the online run, which is precisely when the
        /// failure would first be seen.
        /// </remarks>
        public int MaxPayloadBytes => 65536;

        /// <summary>Always zero: the loopback IS the server, so there is nothing to correct for.</summary>
        public double ServerClockOffsetSeconds => 0d;

        /// <inheritdoc />
        /// <remarks>Never raised: a loopback peer cannot be dropped by a network that does not exist.</remarks>
        public event Action<RbxNetworkPeerDisconnected> PeerDisconnected
        {
            add { }
            remove { }
        }

        public event Action<RbxNetworkEventMessage> EventReceived;

        public event Action<RbxNetworkRequestMessage, RbxNetworkRequestResponder> RequestReceived;

        public void RegisterActor(string actorId)
        {
            string actor = RequireActorId(actorId);
            if (_actors.Add(actor))
            {
                _actorOrder.Add(actor);
            }
        }

        public void UnregisterActor(string actorId)
        {
            string actor = RequireActorId(actorId);
            if (!_actors.Remove(actor))
            {
                return;
            }

            _actorOrder.Remove(actor);
            _rateLimiter.Forget(actor);
            if (_heldUnreliableEvent != null
                && (string.Equals(_heldUnreliableEvent.SenderActorId, actor,
                        StringComparison.Ordinal)
                    || string.Equals(_heldUnreliableEvent.RecipientActorId, actor,
                        StringComparison.Ordinal)))
            {
                _heldUnreliableEvent = null;
            }
        }

        public void SendEvent(RbxNetworkEventMessage message)
        {
            if (message == null)
            {
                throw new ArgumentNullException(nameof(message));
            }

            ValidateRoute(message.Direction, message.SenderActorId,
                message.RecipientActorId);
            RbxNetworkRateGroup rateGroup = message.Reliability
                                  == RbxNetworkReliability.UnreliableUnordered
                ? RbxNetworkRateGroup.UnreliableRemoteEvent
                : RbxNetworkRateGroup.ReliableRemoteEvent;
            AdmitClientRequest(message.Direction, message.SenderActorId, rateGroup);
            if (message.Reliability == RbxNetworkReliability.UnreliableUnordered)
            {
                if (_unreliableBehavior == RbxNullNetworkUnreliableBehavior.DropAll)
                {
                    return;
                }

                if (_unreliableBehavior ==
                    RbxNullNetworkUnreliableBehavior.ReverseAdjacentPairs)
                {
                    QueueReversedUnreliablePair(message);
                    DrainEvents();
                    return;
                }
            }

            _eventQueue.Enqueue(message);
            DrainEvents();
        }

        public void SendRequest(RbxNetworkRequestMessage message,
            Action<RbxNetworkResponse> response)
        {
            if (message == null)
            {
                throw new ArgumentNullException(nameof(message));
            }

            if (response == null)
            {
                throw new ArgumentNullException(nameof(response));
            }

            ValidateRoute(message.Direction, message.SenderActorId,
                message.RecipientActorId);
            AdmitClientRequest(message.Direction, message.SenderActorId,
                RbxNetworkRateGroup.RemoteFunction);

            Action<RbxNetworkRequestMessage, RbxNetworkRequestResponder> receiver =
                RequestReceived;
            if (receiver == null)
            {
                return;
            }

            RbxNetworkRequestResponder responder = new(response);
            try
            {
                receiver(message, responder);
            }
            catch (Exception ex)
            {
                if (!responder.IsCompleted)
                {
                    responder.Fail(ex.Message);
                }
            }
        }

        private void DrainEvents()
        {
            if (_deliveringEvents)
            {
                return;
            }

            _deliveringEvents = true;
            try
            {
                while (_eventQueue.Count > 0)
                {
                    RbxNetworkEventMessage message = _eventQueue.Dequeue();
                    EventReceived?.Invoke(message);
                }
            }
            finally
            {
                _deliveringEvents = false;
            }
        }

        private void AdmitClientRequest(RbxNetworkDirection direction, string actorId,
            RbxNetworkRateGroup rateGroup)
        {
            if (direction != RbxNetworkDirection.ClientToServer)
            {
                return;
            }

            _rateLimiter.Admit(
                RequireRegisteredActor(actorId, "send a client request"), rateGroup);
        }

        private void QueueReversedUnreliablePair(RbxNetworkEventMessage message)
        {
            if (_heldUnreliableEvent == null)
            {
                _heldUnreliableEvent = message;
                return;
            }

            _eventQueue.Enqueue(message);
            _eventQueue.Enqueue(_heldUnreliableEvent);
            _heldUnreliableEvent = null;
        }

        private void ValidateRoute(RbxNetworkDirection direction, string senderActorId,
            string recipientActorId)
        {
            switch (direction)
            {
                case RbxNetworkDirection.ClientToServer:
                    RequireRegisteredActor(senderActorId, "send to the server");
                    return;
                case RbxNetworkDirection.ServerToClient:
                    RequireRegisteredActor(recipientActorId, "receive from the server");
                    return;
                case RbxNetworkDirection.ServerToAllClients:
                    return;
                default:
                    throw new ArgumentOutOfRangeException(nameof(direction), direction, null);
            }
        }

        private string RequireRegisteredActor(string actorId, string operation)
        {
            string actor = RequireActorId(actorId);
            if (_actors.Contains(actor))
            {
                return actor;
            }

            throw new RbxError(
                RbxErrorCode.NotAuthority,
                "actor '" + actor + "' cannot " + operation
                + " because the actor is not registered with the loopback bridge",
                "register the actor context before using remotes");
        }

        private static string RequireActorId(string actorId)
        {
            if (string.IsNullOrWhiteSpace(actorId))
            {
                throw RbxError.BadArgument(
                    "network actor id cannot be empty",
                    "use the trusted ActorContext.ActorId");
            }

            return actorId.Trim();
        }

        private static double MonotonicSeconds()
        {
            return (double)Stopwatch.GetTimestamp() / Stopwatch.Frequency;
        }
    }
}
