using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using CoreAI.Mods.Rbx.Instances;
using CoreAI.Mods.Rbx.Instances.Networking;
using CoreAI.Mods.Rbx.Instances.Replication;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode.RbxApi.Replication
{
    /// <summary>
    /// Two registries in one process — one authoritative, one replica per client — joined by the
    /// loopback bridge. The server mutates, the harness steps, the replicas converge.
    /// </summary>
    /// <remarks>
    /// WHY this is the shape every later phase is tested through: the wire codec replaces the
    /// snapshot table a <see cref="CapturedBatch"/> carries with bytes, the Mirror adapter replaces
    /// the loopback bridge, the join projection replaces the first step's spawn-everything batch —
    /// and <see cref="AssertConverged"/> keeps asking the same question of all of them.
    /// </remarks>
    internal sealed class ReplicatedWorldHarness : IDisposable
    {
        public const string ServerActorId = "server";
        public const string WorldId = "replicated-world";

        private readonly Dictionary<string, ReplicaEndpoint> _clients = new(StringComparer.Ordinal);

        /// <summary>
        /// Builds the server. By default the dirty set precedes the bootstrap, so the first
        /// <see cref="Step"/> spawns the whole tree — the join phase 0 has instead of a projection.
        /// With <paramref name="worldBeforeReplication"/> the world is built first, the order a
        /// composition root reaches naturally; the dirty set then never saw it, and only
        /// <see cref="Seed"/> can hand it to a client.
        /// </summary>
        public ReplicatedWorldHarness(IReplicationFilter gameFilter = null, bool worldBeforeReplication = false)
        {
            Server = new InstanceRegistry(
                binder: new InMemoryInstanceBackingBinder(),
                worldAclVersion: InstanceRegistry.CurrentWorldAclVersion,
                worldId: WorldId);
            Server.Diagnostics = ServerDiagnostics.Add;
            if (worldBeforeReplication)
            {
                ServerGame = DataModelBootstrap.CreateGame(Server);
                Dirty = new ReplicationDirtySet(Server, gameFilter);
            }
            else
            {
                Dirty = new ReplicationDirtySet(Server, gameFilter);
                ServerGame = DataModelBootstrap.CreateGame(Server);
            }

            Bridge = new NullNetworkBridge();
            Bridge.EventReceived += OnBridgeEvent;
        }

        public InstanceRegistry Server { get; }

        public RbxDataModel ServerGame { get; }

        public ReplicationDirtySet Dirty { get; }

        public NullNetworkBridge Bridge { get; }

        public List<string> ServerDiagnostics { get; } = new();

        /// <summary>When set, planned batches queue instead of shipping; release them in any order.</summary>
        public bool HoldOutgoing { get; set; }

        /// <summary>
        /// Runs after each client's plan inside <see cref="Step"/>: a write made here lands mid-step,
        /// after one recipient read the dirty set and before the next did.
        /// </summary>
        public Action<ReplicaEndpoint> AfterPlan { get; set; }

        public int ShippedBatches { get; private set; }

        public RbxInstance ServerWorkspace => Server.WorldRoot;

        public IEnumerable<ReplicaEndpoint> Clients => _clients.Values;

        public RbxInstance ServerService(string className)
        {
            return ServerGame.GetService(className);
        }

        /// <summary>
        /// Admits a client. Add every client before the first <see cref="Step"/> or <see cref="Seed"/>
        /// it: a client that is neither receives deltas against a world it never got, and its applier
        /// asks for a resync — the pinned, correct answer until the join projection lands.
        /// </summary>
        public ReplicaEndpoint AddClient(string actorId)
        {
            Bridge.RegisterActor(actorId);
            ReplicaEndpoint endpoint = new(this, actorId);
            _clients.Add(actorId, endpoint);
            return endpoint;
        }

        /// <summary>Plans the client's whole visible world from the registry and ships or holds it.</summary>
        public ReplicationBatchPlan Seed(string actorId)
        {
            ReplicaEndpoint endpoint = Client(actorId);
            ReplicationBatchPlan plan = endpoint.Stream.PlanWorld();
            if (plan != null)
            {
                Dispatch(endpoint, plan);
            }

            return plan;
        }

        public ReplicaEndpoint Client(string actorId)
        {
            return _clients[actorId];
        }

        public RbxInstance CreateOnServer(string className, string name, RbxInstance parent)
        {
            RbxInstance instance = Server.Create(className);
            instance.Name = name;
            instance.Parent = parent;
            return instance;
        }

        /// <summary>One server step: plan per client, ship or hold each batch, clear the dirty set.</summary>
        public void Step()
        {
            foreach (ReplicaEndpoint endpoint in _clients.Values)
            {
                ReplicationBatchPlan plan = endpoint.Stream.Plan();
                if (plan != null)
                {
                    Dispatch(endpoint, plan);
                }

                AfterPlan?.Invoke(endpoint);
            }

            Dirty.Clear();
        }

        private void Dispatch(ReplicaEndpoint endpoint, ReplicationBatchPlan plan)
        {
            CapturedBatch batch = new(plan, CaptureState(plan));
            endpoint.Planned.Add(batch);
            endpoint.InFlight[plan.Sequence] = batch;
            if (HoldOutgoing)
            {
                endpoint.Held.Add(batch);
            }
            else
            {
                Ship(batch);
            }
        }

        /// <summary>Ships every held batch for the client, in planning order.</summary>
        public void ReleaseHeld(string actorId)
        {
            ReplicaEndpoint endpoint = Client(actorId);
            List<CapturedBatch> held = new(endpoint.Held);
            endpoint.Held.Clear();
            foreach (CapturedBatch batch in held)
            {
                Ship(batch);
            }
        }

        /// <summary>Ships one held batch out of order, leaving the rest held.</summary>
        public void ReleaseHeld(string actorId, long sequence)
        {
            Ship(TakeHeld(actorId, sequence));
        }

        /// <summary>Discards one held batch, as a lost packet would.</summary>
        public void DropHeld(string actorId, long sequence)
        {
            TakeHeld(actorId, sequence);
        }

        /// <summary>Ships a batch the client already received, as a duplicate delivery would.</summary>
        public void Resend(string actorId, long sequence)
        {
            Ship(Client(actorId).InFlight[sequence]);
        }

        /// <summary>
        /// The server's tree as the recipient may see it must equal the replica's server-assigned
        /// tree, node for node: identity, placement, name, revision, attributes, tags and payloads.
        /// </summary>
        public void AssertConverged(string actorId)
        {
            List<string> expected = new();
            foreach (RbxInstance instance in Server.GetLiveInstances())
            {
                if (!instance.IsDestroyed && Dirty.Filter.IsVisibleTo(actorId, instance))
                {
                    expected.Add(Describe(instance));
                }
            }

            List<string> actual = new();
            foreach (RbxInstance instance in Client(actorId).Registry.GetLiveInstances())
            {
                if (!instance.IsDestroyed && instance.Id.IsServerAssigned)
                {
                    actual.Add(Describe(instance));
                }
            }

            expected.Sort(StringComparer.Ordinal);
            actual.Sort(StringComparer.Ordinal);
            CollectionAssert.AreEqual(expected, actual,
                "replica '" + actorId + "' does not match the server's projection for it");
        }

        /// <summary>One canonical line per node, so two trees compare as sorted string lists.</summary>
        public static string Describe(RbxInstance instance)
        {
            InstanceSnapshot node = InstanceTreeSerializer.CaptureNode(instance);
            StringBuilder text = new();
            text.Append(node.Id).Append('|').Append(node.ParentId).Append('|').Append(node.ClassName)
                .Append('|').Append(node.Name).Append('|')
                .Append(node.Archivable ? "archivable" : "transient")
                .Append("|rev=").Append(node.Revision);

            List<string> attributes = new();
            foreach (AttributeSnapshot attribute in node.Attributes)
            {
                string value = attribute.Kind switch
                {
                    AttributeValueKind.Number => attribute.NumberValue.ToString("R", CultureInfo.InvariantCulture),
                    AttributeValueKind.Bool => attribute.BoolValue ? "true" : "false",
                    _ => attribute.StringValue
                };
                attributes.Add(attribute.Name + "=" + attribute.Kind + ":" + value);
            }

            attributes.Sort(StringComparer.Ordinal);
            text.Append("|attrs=").Append(string.Join(",", attributes));

            List<string> tags = new(node.Tags);
            tags.Sort(StringComparer.Ordinal);
            text.Append("|tags=").Append(string.Join(",", tags));

            if (node.Value != null)
            {
                text.Append("|value=").Append(node.Value.StringValue ?? "").Append('/')
                    .Append(node.Value.ObjectTargetId);
            }

            if (node.Model != null)
            {
                text.Append("|primary=").Append(node.Model.PrimaryPartId).Append("|pivot=")
                    .Append(node.Model.HasStoredWorldPivot ? node.Model.StoredWorldPivot : "-");
            }

            return text.ToString();
        }

        public void Dispose()
        {
            Bridge.EventReceived -= OnBridgeEvent;
            Dirty.Dispose();
        }

        private CapturedBatch TakeHeld(string actorId, long sequence)
        {
            ReplicaEndpoint endpoint = Client(actorId);
            int index = endpoint.Held.FindIndex(batch => batch.Plan.Sequence == sequence);
            if (index < 0)
            {
                throw new InvalidOperationException("no held batch #" + sequence + " for " + actorId);
            }

            CapturedBatch taken = endpoint.Held[index];
            endpoint.Held.RemoveAt(index);
            return taken;
        }

        private void Ship(CapturedBatch batch)
        {
            ShippedBatches++;
            // WHY the payload is only the sequence: Phase 0 has no wire format. The bridge carries
            // routing and ordering; the captured state rides beside it by reference. The wire phase
            // swaps that reference for bytes and nothing above this line changes.
            Bridge.SendEvent(new RbxNetworkEventMessage(ServerGame.Id,
                RbxNetworkDirection.ServerToClient, RbxNetworkReliability.ReliableOrdered,
                ServerActorId, batch.Plan.RecipientActorId,
                BitConverter.GetBytes(batch.Plan.Sequence)));
        }

        private void OnBridgeEvent(RbxNetworkEventMessage message)
        {
            if (message.Direction != RbxNetworkDirection.ServerToClient)
            {
                return;
            }

            ReplicaEndpoint endpoint = Client(message.RecipientActorId);
            long sequence = BitConverter.ToInt64(message.Payload, 0);
            endpoint.Receive(endpoint.InFlight[sequence]);
        }

        private Dictionary<ulong, InstanceSnapshot> CaptureState(ReplicationBatchPlan plan)
        {
            Dictionary<ulong, InstanceSnapshot> state = new();
            foreach (ReplicationOperation operation in plan.Operations)
            {
                if (operation.Kind == ReplicationOperationKind.Remove)
                {
                    continue;
                }

                if (Server.TryGet(operation.InstanceId, out RbxInstance instance) && !instance.IsDestroyed)
                {
                    state[operation.InstanceId.Value] = InstanceTreeSerializer.CaptureNode(instance);
                }
            }

            return state;
        }
    }

    /// <summary>A plan plus the node state captured when it was planned: what a wire packet holds.</summary>
    internal sealed class CapturedBatch
    {
        public CapturedBatch(ReplicationBatchPlan plan, Dictionary<ulong, InstanceSnapshot> state)
        {
            Plan = plan;
            State = state;
        }

        public ReplicationBatchPlan Plan { get; }

        public Dictionary<ulong, InstanceSnapshot> State { get; }
    }

    /// <summary>One client: its replica registry, stream, applier, and everything it received.</summary>
    internal sealed class ReplicaEndpoint : IReplicationStateSource
    {
        private Dictionary<ulong, InstanceSnapshot> _current;

        public ReplicaEndpoint(ReplicatedWorldHarness harness, string actorId)
        {
            ActorId = actorId;
            Registry = new InstanceRegistry(
                binder: new InMemoryInstanceBackingBinder(),
                worldAclVersion: InstanceRegistry.CurrentWorldAclVersion,
                worldId: ReplicatedWorldHarness.WorldId,
                authority: RegistryAuthority.Replica);
            Registry.Diagnostics = Diagnostics.Add;
            Stream = new ReplicationStream(harness.Dirty, actorId);
            Applier = new ReplicationApplier(Registry);
            Applier.ResyncRequested += ResyncRequests.Add;
        }

        public string ActorId { get; }

        public InstanceRegistry Registry { get; }

        public ReplicationStream Stream { get; }

        public ReplicationApplier Applier { get; }

        public List<string> Diagnostics { get; } = new();

        public List<string> ResyncRequests { get; } = new();

        public List<CapturedBatch> Planned { get; } = new();

        public Dictionary<long, CapturedBatch> InFlight { get; } = new();

        public List<CapturedBatch> Held { get; } = new();

        public List<ReplicationApplyResult> Results { get; } = new();

        public ReplicationBatchPlan LastPlan => Planned.Count == 0 ? null : Planned[Planned.Count - 1].Plan;

        public ReplicationApplyResult LastResult => Results.Count == 0 ? null : Results[Results.Count - 1];

        public RbxDataModel Game
        {
            get
            {
                foreach (RbxInstance instance in Registry.GetLiveInstances())
                {
                    if (instance is RbxDataModel game)
                    {
                        return game;
                    }
                }

                return null;
            }
        }

        /// <summary>The replica's instance for a server instance's id, or null.</summary>
        public RbxInstance Find(RbxInstance serverInstance)
        {
            return Registry.TryGet(serverInstance.Id, out RbxInstance instance) ? instance : null;
        }

        public void Receive(CapturedBatch batch)
        {
            _current = batch.State;
            try
            {
                Results.Add(Applier.Apply(batch.Plan, this));
            }
            finally
            {
                _current = null;
            }
        }

        InstanceSnapshot IReplicationStateSource.Describe(InstanceId id)
        {
            return _current != null && _current.TryGetValue(id.Value, out InstanceSnapshot node)
                ? node
                : null;
        }
    }
}
