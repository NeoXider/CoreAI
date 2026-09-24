using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text.RegularExpressions;
using CoreAI.Mods.Rbx.Datatypes;
using CoreAI.Mods.Rbx.Instances.Networking;
using Mirror;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace CoreAI.Net.Mirror.Tests
{
    /// <summary>
    /// A wall clock a fixture sets by hand: a Unix time and a process uptime that move only when
    /// told, so a clock rule can be proven with any skew between two machines and no sleep.
    /// </summary>
    internal sealed class FakeWallClock : IRbxClockSource
    {
        public FakeWallClock(double unixSeconds, double processSeconds)
        {
            UnixTimeSecondsFractional = unixSeconds;
            ProcessTimeSeconds = processSeconds;
        }

        public double GameTimeSeconds => 0d;

        public long UnixTimeSeconds => (long)Math.Floor(UnixTimeSecondsFractional);

        public double ProcessTimeSeconds { get; set; }

        public double UnixTimeSecondsFractional { get; set; }

        /// <summary>Moves both readings by the same real interval, as a running machine does.</summary>
        public void Advance(double seconds)
        {
            UnixTimeSecondsFractional += seconds;
            ProcessTimeSeconds += seconds;
        }
    }

    /// <summary>
    /// Every message of one type either side hands Mirror's send path, with its channel, until
    /// disposed: the witness for control messages whose content matters, not only their target.
    /// </summary>
    internal sealed class SentMessages<T> : IDisposable where T : struct, NetworkMessage
    {
        public SentMessages()
        {
            NetworkDiagnostics.OutMessageEvent += Record;
        }

        public List<T> Messages { get; } = new();

        public List<int> ChannelIds { get; } = new();

        public void Dispose()
        {
            NetworkDiagnostics.OutMessageEvent -= Record;
        }

        private void Record(NetworkDiagnostics.MessageInfo info)
        {
            if (info.message is T message)
            {
                Messages.Add(message);
                ChannelIds.Add(info.channel);
            }
        }
    }

    /// <summary>
    /// Runs a real Mirror server or client in edit mode without a socket: a transport that goes
    /// nowhere, Mirror's own Listen, Connect and Shutdown, and inbound packets pushed through the
    /// transport's receive event so they cross Mirror's real unbatcher and handler table. In
    /// loopback mode the transport instead carries what each side sends to the other, so a server
    /// and a client in one process exchange real batches through their real senders and handlers,
    /// and a disconnect either side asks for comes back the way kcp2k reports it: to the side that
    /// asked before the call returns, to the other side on the next pump. A peer that is closed,
    /// by its own side's disconnect or by the other side's notice, surfaces nothing further,
    /// queued or later, as a kcp2k peer past Disconnect does.
    /// </summary>
    /// <remarks>
    /// WHY a real Listen instead of a faked <c>NetworkServer.active</c>: the defect these fixtures
    /// exist for lives in Mirror's own Shutdown, which clears the handler table; a fixture that
    /// bypassed that table would pass whether or not the bridge re-registers. Everything touched here
    /// is static and this project runs without domain reload, so <see cref="Dispose"/> restores all
    /// of it unconditionally and every fixture must reach it from its teardown.
    /// <para>
    /// Modelled on request: kcp2k's per-channel packet sizes (<see cref="UsePacketSizes"/>; by
    /// default both channels report the same large number) and kcp2k's send order, in which an
    /// unreliable datagram leaves before reliable data queued in the same frame
    /// (<see cref="UnreliableOvertakesReliable"/>). NOT modelled, and a test that needs any of it
    /// needs a real transport rather than this: an unreliable channel that actually loses packets
    /// (both queues here are lossless), and time — the authenticator's admission deadline runs on
    /// its own injected clock and is pumped by the fixture, never by this harness.
    /// </para>
    /// </remarks>
    internal sealed class OfflineMirror : IDisposable
    {
        /// <summary>The one server connection a loopback client gets.</summary>
        public const int LoopbackConnectionId = 1;

        /// <summary>kcp2k's reliable max message size at its defaults: MTU 1200, receive window 255 fragments.</summary>
        public const int KcpReliableMaxPacketSize = (1200 - 24 - 5) * (255 - 1) - 1;

        /// <summary>kcp2k's unreliable max message size at its default MTU of 1200.</summary>
        public const int KcpUnreliableMaxPacketSize = 1200 - 5 - 1;

        /// <summary>One message the server handed the transport: the connection, and its type's wire id.</summary>
        public readonly struct SentMessage
        {
            public SentMessage(int connectionId, ushort messageId)
            {
                ConnectionId = connectionId;
                MessageId = messageId;
            }

            public int ConnectionId { get; }

            public ushort MessageId { get; }
        }

        private const int MaxLoopbackRounds = 8;
        private static readonly MethodInfo FlushConnection = typeof(NetworkConnection).GetMethod(
            "Update", BindingFlags.NonPublic | BindingFlags.Instance);

        private readonly GameObject _go;
        private readonly Transport _previousTransport;
        private readonly SilentTransport _transport;

        public OfflineMirror(bool loopback = false)
        {
            EnsureWireCodecs();
            _go = new GameObject("CoreAI_OfflineMirror");
            _previousTransport = Transport.active;
            _transport = _go.AddComponent<SilentTransport>();
            _transport.Loopback = loopback;
            Transport.active = _transport;
        }

        /// <summary>Every connection the server asked the transport to drop, in order.</summary>
        public IReadOnlyList<int> ServerDisconnectRequests => _transport.ServerDisconnectLog;

        /// <summary>
        /// When set, each pump hands the client every queued unreliable batch before the reliable
        /// ones queued with it — kcp2k sends an unreliable datagram at once, while reliable data
        /// waits for the next outgoing tick, so the unreliable one arrives first.
        /// </summary>
        public bool UnreliableOvertakesReliable
        {
            get => _transport.UnreliableFirst;
            set => _transport.UnreliableFirst = value;
        }

        /// <summary>
        /// Makes the transport report these max packet sizes, and kcp2k's batch threshold (the
        /// unreliable size); call before anything is sent, since Mirror sizes a connection's
        /// batchers on its first send.
        /// </summary>
        public void UsePacketSizes(int reliable, int unreliable)
        {
            _transport.ReliableMaxPacketSize = reliable;
            _transport.UnreliableMaxPacketSize = unreliable;
        }

        /// <summary>
        /// Every message the server handed the transport, in order, Mirror's own included; they
        /// reach it through <see cref="FlushServer"/>.
        /// </summary>
        public IReadOnlyList<SentMessage> ServerSends => _transport.ServerSendLog;

        /// <summary>The connections a message of one type was handed to the transport for, in order.</summary>
        /// <remarks>
        /// WHY by type and never by batch: Mirror pings every connection on its first flush, below
        /// admission, on the unreliable channel's own batch — so a witness that counted batches
        /// would see the transport reach a connection the bridge never addressed, and see two
        /// batches where the bridge sent once. Only the wire id inside a batch says whose message
        /// it carried.
        /// </remarks>
        public List<int> ServerSendTargetsOf<T>() where T : struct, NetworkMessage
        {
            List<int> targets = new();
            foreach (SentMessage sent in _transport.ServerSendLog)
            {
                if (sent.MessageId == NetworkMessageId<T>.Id)
                {
                    targets.Add(sent.ConnectionId);
                }
            }

            return targets;
        }

        /// <summary>
        /// Connects the client to the listening server over the loopback: both sides see a
        /// connection and neither is authenticated, which is the state admission starts from.
        /// </summary>
        public void ConnectLoopback()
        {
            Assert.IsTrue(_transport.Loopback, "construct the harness with loopback: true");
            _transport.OpenPeers();
            NetworkClient.Connect("localhost");
            _transport.OnClientConnected?.Invoke();
            _transport.OnServerConnectedWithAddress?.Invoke(LoopbackConnectionId, "loopback");
        }

        /// <summary>
        /// Flushes what both sides batched and delivers it to the other side, repeating until a
        /// round moves nothing; a handler that sends in reply is served by the next round.
        /// </summary>
        public void PumpLoopback()
        {
            Assert.IsNotNull(FlushConnection, "Mirror's NetworkConnection.Update() is where batches are flushed");
            for (int round = 0; round < MaxLoopbackRounds; round++)
            {
                FlushServer();

                if (NetworkClient.connection != null)
                {
                    FlushConnection.Invoke(NetworkClient.connection, null);
                }

                if (_transport.ToClient.Count == 0 && _transport.ToServer.Count == 0)
                {
                    return;
                }

                _transport.DrainToClient();
                _transport.DrainToServer();
            }

            Assert.Fail("the loopback kept exchanging packets for " + MaxLoopbackRounds + " rounds");
        }

        /// <summary>
        /// Hands the transport what every server connection has batched, so a send the bridge made
        /// shows in <see cref="ServerSends"/>; <see cref="PumpLoopback"/> does this each round.
        /// </summary>
        public void FlushServer()
        {
            Assert.IsNotNull(FlushConnection, "Mirror's NetworkConnection.Update() is where batches are flushed");
            if (!NetworkServer.active)
            {
                return;
            }

            foreach (NetworkConnectionToClient conn in NetworkServer.connections.Values)
            {
                FlushConnection.Invoke(conn, null);
            }
        }

        public static void StartServer()
        {
            NetworkServer.Listen(4);
        }

        public static void StopServer()
        {
            NetworkServer.Shutdown();
        }

        /// <summary>Connects the client the way NetworkManager does and marks it authenticated.</summary>
        public static void StartClient()
        {
            NetworkClient.Connect("localhost");
            NetworkClient.connection.isAuthenticated = true;
        }

        public static void StopClient()
        {
            NetworkClient.Shutdown();
        }

        /// <summary>Registers an authenticated connection with the running server.</summary>
        public static NetworkConnectionToClient AdmitServerConnection(int connectionId)
        {
            NetworkConnectionToClient conn = new(connectionId) { isAuthenticated = true };
            NetworkServer.AddConnection(conn);
            return conn;
        }

        /// <summary>Registers a connection that has connected and authenticated nothing yet.</summary>
        public static NetworkConnectionToClient OpenServerConnection(int connectionId)
        {
            NetworkConnectionToClient conn = new(connectionId);
            NetworkServer.AddConnection(conn);
            return conn;
        }

        public static void DeliverToServer<T>(int connectionId, T message)
            where T : struct, NetworkMessage
        {
            Transport.active.OnServerDataReceived?.Invoke(connectionId, Batch(message),
                Channels.Reliable);
        }

        public static void DeliverToClient<T>(T message) where T : struct, NetworkMessage
        {
            Transport.active.OnClientDataReceived?.Invoke(Batch(message), Channels.Reliable);
        }

        public static void DropServerConnection(int connectionId)
        {
            Transport.active.OnServerDisconnected?.Invoke(connectionId);
        }

        /// <summary>
        /// Requires that the next server packet finds no handler. The expectation doubles as the
        /// proof that the packet reached Mirror's dispatcher at all: an unstarted server would
        /// swallow it silently and a vacuous test would pass.
        /// </summary>
        public static void ExpectNoServerHandler()
        {
            LogAssert.Expect(LogType.Warning, new Regex("^Unknown message id: \\d+ for connection"));
            LogAssert.Expect(LogType.Error, new Regex("^NetworkServer: failed to unpack and invoke"));
        }

        public static void ExpectNoClientHandler()
        {
            LogAssert.Expect(LogType.Warning, new Regex("^Unknown message id: \\d+\\. This can happen"));
            LogAssert.Expect(LogType.Error, new Regex("^NetworkClient: failed to unpack and invoke"));
        }

        public static CoreAiRemoteEventMessage Event(ulong remoteId)
        {
            return new CoreAiRemoteEventMessage
            {
                RemoteId = remoteId,
                Direction = (byte)RbxNetworkDirection.ClientToServer,
                Reliability = (byte)RbxNetworkReliability.ReliableOrdered,
                Payload = new byte[] { 1 }
            };
        }

        /// <summary>Runs every step even when one throws, then rethrows the first failure.</summary>
        public static void RunAll(params Action[] steps)
        {
            Exception first = null;
            for (int index = 0; index < steps.Length; index++)
            {
                try
                {
                    steps[index]();
                }
                catch (Exception e)
                {
                    first ??= e;
                }
            }

            if (first != null)
            {
                ExceptionDispatchInfo.Capture(first).Throw();
            }
        }

        /// <inheritdoc />
        public void Dispose()
        {
            RunAll(
                NetworkServer.Shutdown,
                NetworkClient.Shutdown,
                () => Transport.active = _previousTransport,
                () => UnityEngine.Object.DestroyImmediate(_go));
        }

        private static ArraySegment<byte> Batch<T>(T message) where T : struct, NetworkMessage
        {
            NetworkWriter packed = new();
            NetworkMessages.Pack(message, packed);
            Batcher batcher = new(65536);
            batcher.AddMessage(packed.ToArraySegment(), 0d);
            NetworkWriter batch = new();
            Assert.IsTrue(batcher.GetBatch(batch));
            return batch.ToArraySegment();
        }

        /// <summary>
        /// Invokes the weaver's generated codec registration for the bridge's assembly.
        /// </summary>
        /// <remarks>
        /// WHY: the weaver marks InitReadWriters with [RuntimeInitializeOnLoadMethod] only for a
        /// runtime assembly, so in edit mode nobody registers the wire readers and every packet would
        /// deserialize to default with an error.
        /// </remarks>
        private static void EnsureWireCodecs()
        {
            if (Reader<CoreAiRemoteEventMessage>.read != null)
            {
                return;
            }

            Type generated = typeof(MirrorNetworkBridge).Assembly.GetType("Mirror.GeneratedNetworkCode");
            Assert.IsNotNull(generated, "CoreAI.Net.Mirror was not weaved; Mirror's ILPostProcessor did not run");
            MethodInfo init = generated.GetMethod("InitReadWriters",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            Assert.IsNotNull(init, "Mirror.GeneratedNetworkCode.InitReadWriters");
            init.Invoke(null, null);
            Assert.IsNotNull(Reader<CoreAiRemoteEventMessage>.read,
                "the weaver generated no reader for the bridge's wire message");
        }

        /// <summary>
        /// A transport that delivers nothing, so Mirror can be started and stopped offline — or, in
        /// loopback mode, queues each side's sends and disconnects for <see cref="PumpLoopback"/> to
        /// hand across, each side's peer open until a disconnect closes it.
        /// </summary>
        private sealed class SilentTransport : Transport
        {
            /// <summary>One thing the wire carries: a batch, or the news that the other side hung up.</summary>
            public readonly struct Packet
            {
                public static readonly Packet Disconnected = new(null, 0);

                public Packet(byte[] bytes, int channel)
                {
                    Bytes = bytes;
                    Channel = channel;
                }

                public byte[] Bytes { get; }

                public int Channel { get; }

                public bool IsDisconnect => Bytes == null;
            }

            public bool Loopback;
            public bool UnreliableFirst;
            public int ReliableMaxPacketSize = 65536;
            public int UnreliableMaxPacketSize = 65536;
            public readonly Queue<Packet> ToClient = new();
            public readonly Queue<Packet> ToServer = new();
            public readonly List<int> ServerDisconnectLog = new();
            public readonly List<SentMessage> ServerSendLog = new();
            private bool _clientPeerClosed;
            private bool _serverPeerClosed;

            /// <summary>Starts a session: both peers open, nothing left over from an earlier one.</summary>
            public void OpenPeers()
            {
                _clientPeerClosed = false;
                _serverPeerClosed = false;
                ToClient.Clear();
                ToServer.Clear();
            }

            /// <summary>
            /// Hands the client what the server put on the wire, in order — every unreliable batch
            /// first when <see cref="UnreliableFirst"/> is set — until the client's peer is closed;
            /// from then on the rest is discarded.
            /// </summary>
            /// <remarks>
            /// WHY a closed peer discards rather than delivers: kcp2k surfaces nothing for a peer
            /// past Disconnect — TickIncoming does nothing while Disconnected, unreliable input
            /// is dropped unless Authenticated — and its server ignores datagrams for a connection
            /// it removed. A harness that delivered them would hand Mirror data for a connection
            /// it no longer holds, which no real server ever sees.
            /// </remarks>
            public void DrainToClient()
            {
                if (UnreliableFirst)
                {
                    Queue<Packet> held = new();
                    while (ToClient.Count > 0)
                    {
                        Packet packet = ToClient.Dequeue();
                        if (!packet.IsDisconnect && packet.Channel == Channels.Unreliable)
                        {
                            HandToClient(packet);
                        }
                        else
                        {
                            held.Enqueue(packet);
                        }
                    }

                    while (held.Count > 0)
                    {
                        HandToClient(held.Dequeue());
                    }
                }

                while (ToClient.Count > 0)
                {
                    HandToClient(ToClient.Dequeue());
                }
            }

            private void HandToClient(Packet packet)
            {
                if (_clientPeerClosed)
                {
                    return;
                }

                if (packet.IsDisconnect)
                {
                    _clientPeerClosed = true;
                    OnClientDisconnected?.Invoke();
                    return;
                }

                OnClientDataReceived?.Invoke(new ArraySegment<byte>(packet.Bytes), packet.Channel);
            }

            /// <summary>The server-bound half of <see cref="DrainToClient"/>.</summary>
            public void DrainToServer()
            {
                while (ToServer.Count > 0)
                {
                    Packet packet = ToServer.Dequeue();
                    if (_serverPeerClosed)
                    {
                        continue;
                    }

                    if (packet.IsDisconnect)
                    {
                        _serverPeerClosed = true;
                        OnServerDisconnected?.Invoke(LoopbackConnectionId);
                        continue;
                    }

                    OnServerDataReceived?.Invoke(LoopbackConnectionId,
                        new ArraySegment<byte>(packet.Bytes), packet.Channel);
                }
            }

            public override bool Available() => true;

            public override bool ClientConnected() => false;

            public override void ClientConnect(string address)
            {
            }

            public override void ClientSend(ArraySegment<byte> segment, int channelId = Channels.Reliable)
            {
                if (Loopback)
                {
                    ToServer.Enqueue(new Packet(Copy(segment), channelId));
                }
            }

            public override void ClientDisconnect()
            {
                if (!Loopback)
                {
                    return;
                }

                _clientPeerClosed = true;
                ToServer.Enqueue(Packet.Disconnected);
                OnClientDisconnected?.Invoke();
            }

            public override Uri ServerUri() => null;

            public override bool ServerActive() => false;

            public override void ServerStart()
            {
            }

            public override void ServerSend(int connectionId, ArraySegment<byte> segment,
                int channelId = Channels.Reliable)
            {
                RecordServerSend(connectionId, segment);
                if (Loopback && connectionId == LoopbackConnectionId)
                {
                    ToClient.Enqueue(new Packet(Copy(segment), channelId));
                }
            }

            /// <summary>
            /// Records each message in a batch by its wire id, through Mirror's own unbatcher, so a
            /// fixture can tell the bridge's sends from Mirror's.
            /// </summary>
            private void RecordServerSend(int connectionId, ArraySegment<byte> batch)
            {
                Unbatcher unbatcher = new();
                Assert.IsTrue(unbatcher.AddBatch(batch),
                    "the server handed the transport something shorter than a batch header");
                while (unbatcher.GetNextMessage(out ArraySegment<byte> message, out _))
                {
                    using NetworkReaderPooled reader = NetworkReaderPool.Get(message);
                    Assert.IsTrue(NetworkMessages.UnpackId(reader, out ushort messageId),
                        "a batched message with no type id in front of it");
                    ServerSendLog.Add(new SentMessage(connectionId, messageId));
                }
            }

            /// <remarks>
            /// WHY the server hears of the drop before this returns: kcp2k's <c>KcpPeer.Disconnect</c>
            /// raises its disconnect callback synchronously, and Mirror's handler for it clears the
            /// connection's unsent batches — so a harness that deferred the callback would flush
            /// batches a real server never sends.
            /// </remarks>
            public override void ServerDisconnect(int connectionId)
            {
                ServerDisconnectLog.Add(connectionId);
                if (!Loopback || connectionId != LoopbackConnectionId)
                {
                    return;
                }

                _serverPeerClosed = true;
                ToClient.Enqueue(Packet.Disconnected);
                OnServerDisconnected?.Invoke(connectionId);
            }

            public override string ServerGetClientAddress(int connectionId) => "";

            public override void ServerStop()
            {
            }

            public override int GetMaxPacketSize(int channelId = Channels.Reliable) =>
                channelId == Channels.Unreliable ? UnreliableMaxPacketSize : ReliableMaxPacketSize;

            /// <remarks>
            /// WHY the unreliable size for every channel: that is kcp2k's own answer, so a large
            /// reliable message is its own batch here exactly as it is on kcp2k.
            /// </remarks>
            public override int GetBatchThreshold(int channelId = Channels.Reliable) =>
                UnreliableMaxPacketSize;

            public override void Shutdown()
            {
            }

            /// <summary>Mirror reuses the segment's buffer after Send returns; a queue must own its bytes.</summary>
            private static byte[] Copy(ArraySegment<byte> segment)
            {
                byte[] bytes = new byte[segment.Count];
                Buffer.BlockCopy(segment.Array, segment.Offset, bytes, 0, segment.Count);
                return bytes;
            }
        }
    }
}
