using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using CoreAI.Mods.Rbx.Datatypes;
using CoreAI.Mods.Rbx.Instances;
using CoreAI.Mods.Rbx.Instances.Replication;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode.RbxApi.Replication
{
    /// <summary>A hand-built value source: node states and part states by id, counting what was read.</summary>
    internal sealed class FakeWireSource : IReplicationWireSource
    {
        public Dictionary<ulong, InstanceSnapshot> Nodes { get; } = new();

        public Dictionary<ulong, ReplicationPartState> Parts { get; } = new();

        public int CaptureCalls { get; private set; }

        public InstanceSnapshot CaptureNode(InstanceId id)
        {
            CaptureCalls++;
            return Nodes.TryGetValue(id.Value, out InstanceSnapshot node) ? node : null;
        }

        public bool TryReadPart(InstanceId id, out ReplicationPartState state)
        {
            return Parts.TryGetValue(id.Value, out state);
        }
    }

    /// <summary>
    /// The part layer both sides would have: the server reads current part members from it, a
    /// replica's sink merges the fields a batch carried into it.
    /// </summary>
    internal sealed class PartStore : IReplicationPartReader, IReplicationPartSink
    {
        public Dictionary<ulong, ReplicationPartState> States { get; } = new();

        public int Applied { get; private set; }

        public void Set(InstanceId id, ReplicationPartState state)
        {
            States[id.Value] = state;
        }

        public void SetCFrame(InstanceId id, RbxCFrame cframe)
        {
            ReplicationPartState current = States.TryGetValue(id.Value, out ReplicationPartState existing)
                ? existing
                : default;
            States[id.Value] = new ReplicationPartState(current.Fields | ReplicationPartFields.CFrame, cframe,
                current.Color, current.Transparency, current.Anchored);
        }

        public bool TryReadPart(InstanceId id, out ReplicationPartState state)
        {
            return States.TryGetValue(id.Value, out state);
        }

        public void ApplyPart(RbxInstance instance, in ReplicationPartState state)
        {
            Applied++;
            ReplicationPartState current = States.TryGetValue(instance.Id.Value, out ReplicationPartState existing)
                ? existing
                : default;
            States[instance.Id.Value] = new ReplicationPartState(current.Fields | state.Fields,
                state.Has(ReplicationPartFields.CFrame) ? state.CFrame : current.CFrame,
                state.Has(ReplicationPartFields.Color) ? state.Color : current.Color,
                state.Has(ReplicationPartFields.Transparency) ? state.Transparency : current.Transparency,
                state.Has(ReplicationPartFields.Anchored) ? state.Anchored : current.Anchored);
        }
    }

    /// <summary>A packet as the send function saw it, copied out of the publisher's reused buffer.</summary>
    internal sealed class SentPacket
    {
        public SentPacket(in ReplicationOutgoingPacket packet)
        {
            RecipientActorId = packet.RecipientActorId;
            Sequence = packet.Sequence;
            Kind = packet.Kind;
            OperationCount = packet.OperationCount;
            Bytes = new byte[packet.Bytes.Count];
            Buffer.BlockCopy(packet.Bytes.Array, packet.Bytes.Offset, Bytes, 0, packet.Bytes.Count);
        }

        public string RecipientActorId { get; }

        public long Sequence { get; }

        public ReplicationPacketKind Kind { get; }

        public int OperationCount { get; }

        public byte[] Bytes { get; }
    }

    /// <summary>One client over the wire: a replica registry fed only by packet bytes.</summary>
    internal sealed class WireClient : IDisposable
    {
        public WireClient(string actorId)
        {
            ActorId = actorId;
            Registry = new InstanceRegistry(
                binder: new InMemoryInstanceBackingBinder(),
                worldAclVersion: InstanceRegistry.CurrentWorldAclVersion,
                worldId: ReplicatedWorldHarness.WorldId,
                authority: RegistryAuthority.Replica);
            Registry.Diagnostics = Diagnostics.Add;
            Receiver = new ReplicationWireReceiver(Registry, actorId, Parts);
            Receiver.ResyncRequested += ResyncRequests.Add;
        }

        public string ActorId { get; }

        public InstanceRegistry Registry { get; }

        public ReplicationWireReceiver Receiver { get; }

        public PartStore Parts { get; } = new();

        public List<string> Diagnostics { get; } = new();

        public List<string> ResyncRequests { get; } = new();

        public List<ReplicationReceiveResult> Results { get; } = new();

        /// <summary>When set, packets queue in <see cref="Held"/> instead of being applied.</summary>
        public bool Hold { get; set; }

        public List<SentPacket> Held { get; } = new();

        public ReplicationReceiveResult LastResult => Results[Results.Count - 1];

        public void Deliver(SentPacket packet)
        {
            if (Hold)
            {
                Held.Add(packet);
                return;
            }

            Results.Add(Receiver.Receive(packet.Bytes));
        }

        public RbxInstance Find(RbxInstance serverInstance)
        {
            return Registry.TryGet(serverInstance.Id, out RbxInstance instance) ? instance : null;
        }

        public void Dispose()
        {
            Receiver.Dispose();
        }
    }

    /// <summary>
    /// A server publishing to clients over bytes: registry, dirty set, part layer and publisher on one
    /// side, a <see cref="WireClient"/> per recipient on the other, a fake clock between them.
    /// </summary>
    internal sealed class WireWorld : IDisposable
    {
        private readonly Dictionary<string, WireClient> _clients = new(StringComparer.Ordinal);

        public WireWorld(ReplicationPublisherOptions options = null)
        {
            Server = new InstanceRegistry(
                binder: new InMemoryInstanceBackingBinder(),
                worldAclVersion: InstanceRegistry.CurrentWorldAclVersion,
                worldId: ReplicatedWorldHarness.WorldId);
            Server.Diagnostics = ServerDiagnostics.Add;
            Dirty = new ReplicationDirtySet(Server);
            Game = DataModelBootstrap.CreateGame(Server);
            options ??= new ReplicationPublisherOptions();
            options.Diagnostics ??= PublisherDiagnostics.Add;
            Publisher = new ReplicationPublisher(Dirty, () => Now, OnSend,
                new RegistryReplicationWireSource(Server, ServerParts), options);
        }

        public InstanceRegistry Server { get; }

        public RbxDataModel Game { get; }

        public ReplicationDirtySet Dirty { get; }

        public PartStore ServerParts { get; } = new();

        public ReplicationPublisher Publisher { get; }

        public double Now { get; set; }

        public List<SentPacket> Sent { get; } = new();

        public List<string> ServerDiagnostics { get; } = new();

        public List<string> PublisherDiagnostics { get; } = new();

        /// <summary>Runs inside the send function, after the packet was recorded; a throw here is the transport failing.</summary>
        public Action<SentPacket> OnSent { get; set; }

        public RbxInstance Workspace => Server.WorldRoot;

        public WireClient Join(string actorId, ReplicationRecipientBudget? budget = null)
        {
            Publisher.AddRecipient(actorId, budget);
            WireClient client = new(actorId);
            _clients.Add(actorId, client);
            return client;
        }

        public WireClient Client(string actorId)
        {
            return _clients[actorId];
        }

        public RbxInstance Create(string className, string name, RbxInstance parent)
        {
            RbxInstance instance = Server.Create(className);
            instance.Name = name;
            instance.Parent = parent;
            return instance;
        }

        public RbxInstance CreatePart(string name, RbxInstance parent, RbxCFrame cframe)
        {
            RbxInstance part = Server.Create("Part");
            part.Name = name;
            ServerParts.Set(part.Id, ReplicationPartState.Of(cframe, new RbxColor3(0.5f, 0.5f, 0.5f), 0f, true));
            part.Parent = parent;
            return part;
        }

        /// <summary>A part-layer write as the Unity binder reports it: the value, then the named member.</summary>
        public void Move(RbxInstance part, RbxCFrame cframe)
        {
            ServerParts.SetCFrame(part.Id, cframe);
            Server.AdvanceRevision(part.Id, ReplicationPartMembers.CFrame);
        }

        /// <summary>Advances the clock and lets the publisher run whatever tick is due.</summary>
        public bool Advance(double seconds)
        {
            Now += seconds;
            return Publisher.Update();
        }

        public List<SentPacket> SentTo(string actorId)
        {
            List<SentPacket> packets = new();
            foreach (SentPacket packet in Sent)
            {
                if (packet.RecipientActorId == actorId)
                {
                    packets.Add(packet);
                }
            }

            return packets;
        }

        /// <summary>
        /// The client's server-assigned tree must equal the server's projection for it, node for node,
        /// and every part the client holds must carry the server's part members within the codec's
        /// stated bounds.
        /// </summary>
        public void AssertConverged(string actorId)
        {
            WireClient client = Client(actorId);
            List<string> expected = new();
            foreach (RbxInstance instance in Server.GetLiveInstances())
            {
                if (!instance.IsDestroyed && Dirty.Filter.IsVisibleTo(actorId, instance))
                {
                    expected.Add(ReplicatedWorldHarness.Describe(instance));
                }
            }

            List<string> actual = new();
            foreach (RbxInstance instance in client.Registry.GetLiveInstances())
            {
                if (!instance.IsDestroyed && instance.Id.IsServerAssigned)
                {
                    actual.Add(ReplicatedWorldHarness.Describe(instance));
                }
            }

            expected.Sort(StringComparer.Ordinal);
            actual.Sort(StringComparer.Ordinal);
            CollectionAssert.AreEqual(expected, actual,
                "replica '" + actorId + "' does not match the server's projection for it");

            foreach (KeyValuePair<ulong, ReplicationPartState> pair in ServerParts.States)
            {
                if (!Server.TryGet(new InstanceId(pair.Key), out RbxInstance part) || part.IsDestroyed
                    || !Dirty.Filter.IsVisibleTo(actorId, part))
                {
                    continue;
                }

                Assert.IsTrue(client.Parts.States.TryGetValue(pair.Key, out ReplicationPartState held),
                    "replica '" + actorId + "' holds no part state for " + part.Name);
                WireAssert.PartStatesAgree(pair.Value, held, part.Name);
            }
        }

        public void Dispose()
        {
            foreach (WireClient client in _clients.Values)
            {
                client.Dispose();
            }

            Dirty.Dispose();
        }

        private void OnSend(in ReplicationOutgoingPacket packet)
        {
            SentPacket copy = new(packet);
            Sent.Add(copy);
            OnSent?.Invoke(copy);
            if (_clients.TryGetValue(packet.RecipientActorId, out WireClient client))
            {
                client.Deliver(copy);
            }
        }
    }

    internal static class WireAssert
    {
        public static void PartStatesAgree(ReplicationPartState server, ReplicationPartState replica, string what)
        {
            Assert.AreEqual(server.Fields, replica.Fields & server.Fields, what + ": fields");
            if (server.Has(ReplicationPartFields.CFrame))
            {
                CFramesAgree(server.CFrame, replica.CFrame, what);
            }

            if (server.Has(ReplicationPartFields.Color))
            {
                Assert.AreEqual(Clamp01(server.Color.R), replica.Color.R, ReplicationWireFormat.ColorErrorBound + 1e-6, what + ": R");
                Assert.AreEqual(Clamp01(server.Color.G), replica.Color.G, ReplicationWireFormat.ColorErrorBound + 1e-6, what + ": G");
                Assert.AreEqual(Clamp01(server.Color.B), replica.Color.B, ReplicationWireFormat.ColorErrorBound + 1e-6, what + ": B");
            }

            if (server.Has(ReplicationPartFields.Transparency))
            {
                Assert.AreEqual(server.Transparency, replica.Transparency, what + ": Transparency is exact");
            }

            if (server.Has(ReplicationPartFields.Anchored))
            {
                Assert.AreEqual(server.Anchored, replica.Anchored, what + ": Anchored is exact");
            }
        }

        public static void CFramesAgree(RbxCFrame server, RbxCFrame replica, string what)
        {
            Assert.AreEqual(server.X, replica.X, ReplicationWireFormat.PositionErrorBoundStuds, what + ": X");
            Assert.AreEqual(server.Y, replica.Y, ReplicationWireFormat.PositionErrorBoundStuds, what + ": Y");
            Assert.AreEqual(server.Z, replica.Z, ReplicationWireFormat.PositionErrorBoundStuds, what + ": Z");
            Assert.LessOrEqual(RotationAngle(server, replica), ReplicationWireFormat.RotationErrorBoundRadians,
                what + ": rotation");
        }

        /// <summary>
        /// The angle of the rotation between two CFrames, in double and from the matrix difference:
        /// |R1 - R2| (Frobenius) = 2 sqrt(2) sin(angle / 2) for rotation matrices, which stays accurate
        /// near zero where an acos of the trace would round every small angle away.
        /// </summary>
        public static double RotationAngle(RbxCFrame a, RbxCFrame b)
        {
            double sum = Square(a.XVector.X - (double)b.XVector.X) + Square(a.XVector.Y - (double)b.XVector.Y)
                         + Square(a.XVector.Z - (double)b.XVector.Z) + Square(a.YVector.X - (double)b.YVector.X)
                         + Square(a.YVector.Y - (double)b.YVector.Y) + Square(a.YVector.Z - (double)b.YVector.Z)
                         + Square(a.ZVector.X - (double)b.ZVector.X) + Square(a.ZVector.Y - (double)b.ZVector.Y)
                         + Square(a.ZVector.Z - (double)b.ZVector.Z);
            double halfSine = Math.Sqrt(sum) / (2d * Math.Sqrt(2d));
            return 2d * Math.Asin(Math.Min(1d, halfSine));
        }

        public static void AreExactly(RbxCFrame expected, RbxCFrame actual, string what)
        {
            float[] left = expected.GetComponents();
            float[] right = actual.GetComponents();
            for (int index = 0; index < 12; index++)
            {
                bool same = left[index] == right[index] || (float.IsNaN(left[index]) && float.IsNaN(right[index]));
                Assert.IsTrue(same, what + ": component " + index + " must travel exactly (" + left[index] + " vs "
                                    + right[index] + ")");
            }
        }

        /// <summary>
        /// Comma-joined floats as this runtime's round-trip formatting renders them: the string the
        /// tree serializer would capture for the same values, on Mono and .NET alike.
        /// </summary>
        public static string CanonicalFloats(string joined)
        {
            string[] parts = joined.Split(',');
            for (int index = 0; index < parts.Length; index++)
            {
                parts[index] = float.Parse(parts[index], NumberStyles.Float, CultureInfo.InvariantCulture)
                    .ToString("R", CultureInfo.InvariantCulture);
            }

            return string.Join(",", parts);
        }

        public static string CanonicalDouble(string text)
        {
            return double.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture)
                .ToString("R", CultureInfo.InvariantCulture);
        }

        public static string Hex(byte[] bytes)
        {
            StringBuilder text = new(bytes.Length * 3);
            for (int index = 0; index < bytes.Length; index++)
            {
                if (index > 0)
                {
                    text.Append(' ');
                }

                text.Append(bytes[index].ToString("X2", CultureInfo.InvariantCulture));
            }

            return text.ToString();
        }

        public static byte[] FromHex(string hex)
        {
            string[] parts = hex.Split(new[] { ' ', '\n', '\r', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            byte[] bytes = new byte[parts.Length];
            for (int index = 0; index < parts.Length; index++)
            {
                bytes[index] = byte.Parse(parts[index], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            }

            return bytes;
        }

        public static ReplicationWireBatch Decode(byte[] bytes, string recipient = "alice")
        {
            ReplicationWireDecoder decoder = new();
            bool decoded = decoder.TryDecode(bytes, recipient, out ReplicationWireBatch batch,
                out ReplicationWireError error, out string detail);
            Assert.IsTrue(decoded, "the packet must decode: " + error + " " + detail);
            Assert.AreEqual(ReplicationWireError.None, error);
            return batch;
        }

        public static ReplicationWireError Refusal(byte[] bytes, int maxPacketBytes = ReplicationWireFormat.DefaultMaxPacketBytes)
        {
            ReplicationWireDecoder decoder = new(maxPacketBytes);
            bool decoded = decoder.TryDecode(bytes, "alice", out ReplicationWireBatch batch,
                out ReplicationWireError error, out string detail);
            Assert.IsFalse(decoded, "the packet must be refused, but it decoded");
            Assert.IsNull(batch);
            Assert.AreNotEqual(ReplicationWireError.None, error);
            Assert.IsFalse(string.IsNullOrEmpty(detail), "a refusal says why");
            return error;
        }

        private static float Clamp01(float value)
        {
            return value < 0f ? 0f : value > 1f ? 1f : value;
        }

        private static double Square(double value)
        {
            return value * value;
        }
    }
}
