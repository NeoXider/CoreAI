using System;
using System.Collections.Generic;
using CoreAI.Mods.Rbx.Instances.Scheduling;

namespace CoreAI.Mods.Rbx.Instances.Networking
{
    /// <summary>Engine-free RemoteEvent behavior over an injected byte bridge.</summary>
    public class RbxRemoteEvent : RbxInstance
    {
        private readonly Dictionary<string, RbxScriptSignal> _clientSignals =
            new(StringComparer.Ordinal);
        private ModScheduler _scheduler;

        internal RbxRemoteEvent(ClassDescriptor descriptor,
            RbxNetworkReliability reliability = RbxNetworkReliability.ReliableOrdered)
            : base(descriptor)
        {
            Reliability = reliability;
            OnServerEvent = new RbxScriptSignal(descriptor.Name + ".OnServerEvent");
        }

        public RbxNetworkReliability Reliability { get; }

        public RbxScriptSignal OnServerEvent { get; }

        public RbxScriptSignal GetOnClientEvent(string actorId)
        {
            string actor = RequireActorId(actorId);
            if (!_clientSignals.TryGetValue(actor, out RbxScriptSignal signal))
            {
                signal = new RbxScriptSignal(ClassName + ".OnClientEvent[" + actor + "]");
                if (_scheduler != null)
                {
                    signal.BindScheduler(_scheduler);
                }

                _clientSignals.Add(actor, signal);
            }

            return signal;
        }

        public void FireServer(INetworkBridge bridge, string actorId, byte[] payload)
        {
            RequireBridge(bridge).SendEvent(new RbxNetworkEventMessage(
                Id, RbxNetworkDirection.ClientToServer, Reliability,
                RequireActorId(actorId), null, payload));
        }

        public void FireClient(INetworkBridge bridge, RbxPlayer player, byte[] payload)
        {
            if (player == null)
            {
                throw RbxError.BadArgument(
                    ClassName + ":FireClient expects a Player at argument 1",
                    "pass a Player returned by Players:GetPlayers()");
            }

            RequireBridge(bridge).SendEvent(new RbxNetworkEventMessage(
                Id, RbxNetworkDirection.ServerToClient, Reliability,
                null, player.NetworkActorId, payload));
        }

        public void FireAllClients(INetworkBridge bridge, byte[] payload)
        {
            RequireBridge(bridge).SendEvent(new RbxNetworkEventMessage(
                Id, RbxNetworkDirection.ServerToAllClients, Reliability,
                null, null, payload));
        }

        internal void AttachScheduler(ModScheduler scheduler)
        {
            _scheduler = scheduler ?? throw new ArgumentNullException(nameof(scheduler));
            OnServerEvent.BindScheduler(scheduler);
            foreach (RbxScriptSignal signal in _clientSignals.Values)
            {
                signal.BindScheduler(scheduler);
            }
        }

        internal void DeliverToServer(RbxPlayer player, object[] arguments)
        {
            object[] payload = arguments ?? Array.Empty<object>();
            object[] delivered = new object[payload.Length + 1];
            delivered[0] = player;
            Array.Copy(payload, 0, delivered, 1, payload.Length);
            OnServerEvent.Fire(delivered);
        }

        internal void DeliverToClient(string actorId, object[] arguments)
        {
            GetOnClientEvent(actorId).Fire(arguments ?? Array.Empty<object>());
        }

        /// <summary>Removes per-actor client signal state; used by the disconnect seam.</summary>
        public bool RemoveActor(string actorId)
        {
            string actor = RequireActorId(actorId);
            if (_clientSignals.TryGetValue(actor, out RbxScriptSignal signal))
            {
                signal.DisconnectAll();
                return _clientSignals.Remove(actor);
            }

            return false;
        }

        /// <summary>Current per-actor signal count for leak tests.</summary>
        public int ClientSignalCount => _clientSignals.Count;

        /// <summary>Reports whether this remote retains a client signal for the actor.</summary>
        public bool HasActor(string actorId)
        {
            return _clientSignals.ContainsKey(RequireActorId(actorId));
        }

        private static INetworkBridge RequireBridge(INetworkBridge bridge)
        {
            return bridge ?? throw new ArgumentNullException(nameof(bridge));
        }

        private static string RequireActorId(string actorId)
        {
            if (string.IsNullOrWhiteSpace(actorId))
            {
                throw RbxError.BadArgument(
                    "remote actor id cannot be empty",
                    "use the trusted ActorContext.ActorId");
            }

            return actorId.Trim();
        }
    }

    /// <summary>UnreliableRemoteEvent with the same Lua shape and a weaker delivery contract.</summary>
    public sealed class RbxUnreliableRemoteEvent : RbxRemoteEvent
    {
        internal RbxUnreliableRemoteEvent(ClassDescriptor descriptor)
            : base(descriptor, RbxNetworkReliability.UnreliableUnordered)
        {
        }
    }

    /// <summary>Engine-free RemoteFunction request sender over an injected byte bridge.</summary>
    public sealed class RbxRemoteFunction : RbxInstance
    {
        internal RbxRemoteFunction(ClassDescriptor descriptor)
            : base(descriptor)
        {
        }

        public void InvokeServer(INetworkBridge bridge, string actorId, byte[] payload,
            Action<RbxNetworkResponse> response)
        {
            RequireBridge(bridge).SendRequest(new RbxNetworkRequestMessage(
                Id, RbxNetworkDirection.ClientToServer, actorId, null, payload), response);
        }

        public void InvokeClient(INetworkBridge bridge, RbxPlayer player, byte[] payload,
            Action<RbxNetworkResponse> response)
        {
            if (player == null)
            {
                throw RbxError.BadArgument(
                    "RemoteFunction:InvokeClient expects a Player at argument 1",
                    "pass a Player returned by Players:GetPlayers()");
            }

            RequireBridge(bridge).SendRequest(new RbxNetworkRequestMessage(
                Id, RbxNetworkDirection.ServerToClient, null,
                player.NetworkActorId, payload), response);
        }

        private static INetworkBridge RequireBridge(INetworkBridge bridge)
        {
            return bridge ?? throw new ArgumentNullException(nameof(bridge));
        }
    }
}
