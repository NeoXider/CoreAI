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

        private enum RateGroup
        {
            ReliableRemoteEvent,
            UnreliableRemoteEvent,
            RemoteFunction
        }

        private sealed class RateWindow
        {
            public double StartedAt;
            public int Accepted;
        }

        private readonly int _maxClientRequestsPerSecond;
        private readonly Func<double> _clockSeconds;
        private readonly HashSet<string> _actors = new(StringComparer.Ordinal);
        private readonly List<string> _actorOrder = new();
        private readonly Dictionary<string, Dictionary<RateGroup, RateWindow>> _rateWindows =
            new(StringComparer.Ordinal);
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

            _maxClientRequestsPerSecond = maxClientRequestsPerSecond;
            _clockSeconds = clockSeconds ?? MonotonicSeconds;
            if (unreliableBehavior != RbxNullNetworkUnreliableBehavior.PassThrough
                && unreliableBehavior != RbxNullNetworkUnreliableBehavior.DropAll
                && unreliableBehavior != RbxNullNetworkUnreliableBehavior.ReverseAdjacentPairs)
            {
                throw new ArgumentOutOfRangeException(nameof(unreliableBehavior));
            }

            _unreliableBehavior = unreliableBehavior;
        }

        public RbxNetworkTopology Topology => RbxNetworkTopology.Solo;

        public int MaxClientRequestsPerSecond => _maxClientRequestsPerSecond;

        public IReadOnlyList<string> ActorIds => _actorOrder.AsReadOnly();

        /// <summary>Current actor rate-window count for bounded-churn verification.</summary>
        public int RateWindowCount => _rateWindows.Count;

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
            _rateWindows.Remove(actor);
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
            RateGroup rateGroup = message.Reliability
                                  == RbxNetworkReliability.UnreliableUnordered
                ? RateGroup.UnreliableRemoteEvent
                : RateGroup.ReliableRemoteEvent;
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
                RateGroup.RemoteFunction);

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
            RateGroup rateGroup)
        {
            if (direction != RbxNetworkDirection.ClientToServer)
            {
                return;
            }

            string actor = RequireRegisteredActor(actorId, "send a client request");
            double now = _clockSeconds();
            if (!_rateWindows.TryGetValue(actor,
                    out Dictionary<RateGroup, RateWindow> actorWindows))
            {
                actorWindows = new Dictionary<RateGroup, RateWindow>();
                _rateWindows.Add(actor, actorWindows);
            }

            if (!actorWindows.TryGetValue(rateGroup, out RateWindow window)
                || now - window.StartedAt >= 1d
                || now < window.StartedAt)
            {
                window = new RateWindow { StartedAt = now };
                actorWindows[rateGroup] = window;
            }

            if (window.Accepted >= _maxClientRequestsPerSecond)
            {
                throw new RbxError(
                    RbxErrorCode.BudgetExceeded,
                    "actor '" + actor + "' cannot send a client network request: network request "
                    + "rate quota reached (limit " + _maxClientRequestsPerSecond
                    + " requests/s) for " + RateGroupName(rateGroup),
                    "reduce the request rate or configure a higher loopback admission limit");
            }

            window.Accepted++;
        }

        private static string RateGroupName(RateGroup rateGroup)
        {
            switch (rateGroup)
            {
                case RateGroup.ReliableRemoteEvent:
                    return "RemoteEvent";
                case RateGroup.UnreliableRemoteEvent:
                    return "UnreliableRemoteEvent";
                case RateGroup.RemoteFunction:
                    return "RemoteFunction (OURS)";
                default:
                    throw new ArgumentOutOfRangeException(nameof(rateGroup), rateGroup, null);
            }
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
