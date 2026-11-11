using System;
using System.Collections.Generic;
using System.Text;
using CoreAI.Ai;
using CoreAI.Authority;
using CoreAI.Mods.Rbx.Instances;
using CoreAI.Mods.Rbx.Instances.Networking;
using Mirror;
using NUnit.Framework;
using UnityEngine;

namespace CoreAI.Net.Mirror.Tests
{
    /// <summary>
    /// The roster — who is in the room: a ready client gets the whole of it when it acknowledges
    /// readiness, then every join and leave; a client still joining gets nothing until then; a
    /// reconnect of the same player is no leave and join.
    /// </summary>
    [TestFixture]
    public sealed class MirrorRosterEditModeTests
    {
        private OfflineMirror _mirror;
        private MirrorNetworkBridge _bridge;
        private CoreAiMirrorSessionHost _sessionHost;
        private double _now;

        [SetUp]
        public void StartServer()
        {
            _mirror = new OfflineMirror();
            _now = 0d;
            _bridge = new MirrorNetworkBridge(isServer: true, clockSeconds: () => _now, log: _ => { });
            _sessionHost = new CoreAiMirrorSessionHost(_bridge, _ => true, _ => true, _ => { });
        }

        [TearDown]
        public void RestoreMirror()
        {
            OfflineMirror.RunAll(
                () => _sessionHost.Dispose(),
                () => _bridge.Dispose(),
                () => _mirror.Dispose());
        }

        [Test]
        public void AReadyClient_GetsTheWholeRoster_AfterItsClockAnchor_AndBeforeWhatWasHeld()
        {
            OfflineMirror.StartServer();
            Join(11, "a");
            Join(13, "b");
            _bridge.SendEvent(new RbxNetworkEventMessage(new InstanceId(5UL), RbxNetworkDirection.ServerToClient,
                RbxNetworkReliability.ReliableOrdered, null, "actor-b", new byte[] { 1 }));
            using SentMessages<CoreAiRosterMessage> rosters = new();

            OfflineMirror.DeliverToServer(13, new CoreAiClientReadyMessage());
            _mirror.FlushServer();

            CollectionAssert.AreEqual(new[]
                {
                    NetworkMessageId<CoreAiServerClockMessage>.Id,
                    NetworkMessageId<CoreAiRosterMessage>.Id,
                    NetworkMessageId<CoreAiRemoteEventMessage>.Id
                },
                BridgeMessagesTo(13),
                "the clock, then who is in the room, then what the server held for the client");
            Assert.AreEqual(1, rosters.Messages.Count);
            CoreAiRosterMessage snapshot = rosters.Messages[0];
            Assert.AreEqual((byte)CoreAiRosterChange.Snapshot, snapshot.Change);
            Assert.AreEqual(2, snapshot.Entries.Length);
            Assert.AreEqual("a", snapshot.Entries[0].Name);
            Assert.AreEqual(1001L, snapshot.Entries[0].UserId);
            Assert.AreEqual("b", snapshot.Entries[1].Name);
            Assert.AreEqual(snapshot.Entries[1].MemberId, snapshot.SelfMemberId, "the snapshot says which member the recipient is");
            Assert.AreEqual(_sessionHost.Roster.Revision, snapshot.Revision);
            Assert.AreEqual(1, _sessionHost.RosterMessagesSent);
        }

        [Test]
        public void AJoinAndALeave_ReachEveryReadyClient_AndNoClientStillJoining()
        {
            OfflineMirror.StartServer();
            Join(11, "a");
            OfflineMirror.DeliverToServer(11, new CoreAiClientReadyMessage());
            using SentMessages<CoreAiRosterMessage> rosters = new();

            Join(13, "b");
            _bridge.NotifyDisconnected(13, RbxNetworkDisconnectReason.TransportLost);
            _mirror.FlushServer();

            Assert.AreEqual(2, rosters.Messages.Count);
            Assert.AreEqual((byte)CoreAiRosterChange.Joined, rosters.Messages[0].Change);
            Assert.AreEqual("b", rosters.Messages[0].Entries[0].Name);
            Assert.AreEqual((byte)CoreAiRosterChange.Left, rosters.Messages[1].Change);
            Assert.AreEqual(rosters.Messages[0].Entries[0].MemberId, rosters.Messages[1].Entries[0].MemberId);
            Assert.AreEqual(rosters.Messages[0].Revision + 1u, rosters.Messages[1].Revision);
            CollectionAssert.AreEqual(new[] { 11, 11, 11 }, _mirror.ServerSendTargetsOf<CoreAiRosterMessage>(),
                "the ready client's snapshot, the join and the leave; the client still joining was sent none");
            Assert.AreEqual(1, _sessionHost.Roster.Members.Count);
        }

        [Test]
        public void Negative_AReconnectOfTheSamePlayer_IsNoLeaveAndJoin_AndTheNewConnectionGetsTheRoster()
        {
            OfflineMirror.StartServer();
            Join(11, "a");
            OfflineMirror.DeliverToServer(11, new CoreAiClientReadyMessage());
            Join(13, "b");
            Assert.IsTrue(_sessionHost.Roster.TryGetMember("actor-b", out CoreAiRosterMember before));
            using SentMessages<CoreAiRosterMessage> rosters = new();

            Join(14, "b");
            OfflineMirror.DeliverToServer(14, new CoreAiClientReadyMessage());

            Assert.AreEqual(1, _sessionHost.SupersededSessions);
            Assert.IsTrue(_sessionHost.Roster.TryGetMember("actor-b", out CoreAiRosterMember after));
            Assert.AreEqual(before.MemberId, after.MemberId, "the player kept its place in the room");
            Assert.AreEqual(1, rosters.Messages.Count, "the only roster message is the new connection's snapshot");
            Assert.AreEqual((byte)CoreAiRosterChange.Snapshot, rosters.Messages[0].Change);
            Assert.AreEqual(after.MemberId, rosters.Messages[0].SelfMemberId);
            Assert.AreEqual(2, _sessionHost.Roster.Members.Count);
        }

        [Test]
        public void Negative_WithPublishRosterOff_NoRosterIsSent_ButTheHostStillKeepsIt()
        {
            _sessionHost.Dispose();
            _sessionHost = new CoreAiMirrorSessionHost(_bridge, _ => true, _ => true, _ => { },
                new CoreAiMirrorSessionOptions { PublishRoster = false });
            OfflineMirror.StartServer();
            using SentMessages<CoreAiRosterMessage> rosters = new();

            Join(11, "a");
            OfflineMirror.DeliverToServer(11, new CoreAiClientReadyMessage());
            Join(13, "b");

            CollectionAssert.IsEmpty(rosters.Messages);
            Assert.AreEqual(0, _sessionHost.RosterMessagesSent);
            Assert.IsFalse(_sessionHost.PublishesRoster);
            Assert.AreEqual(2, _sessionHost.Roster.Members.Count);
        }

        [Test]
        public void ALargeRoster_IsSplitIntoASnapshotAndContinuations_ThatRebuildIt()
        {
            _mirror.UsePacketSizes(1200, 1200);
            OfflineMirror.StartServer();
            for (int index = 0; index < 20; index++)
            {
                Join(21 + index, "p" + index);
            }

            using SentMessages<CoreAiRosterMessage> rosters = new();

            OfflineMirror.DeliverToServer(40, new CoreAiClientReadyMessage());

            Assert.Greater(rosters.Messages.Count, 1, "a roster larger than one message on this transport is split");
            Assert.AreEqual((byte)CoreAiRosterChange.Snapshot, rosters.Messages[0].Change);
            CoreAiMirrorRosterReplica replica = new();
            for (int index = 0; index < rosters.Messages.Count; index++)
            {
                if (index > 0)
                {
                    Assert.AreEqual((byte)CoreAiRosterChange.SnapshotContinued, rosters.Messages[index].Change);
                }

                Assert.IsTrue(replica.TryApply(rosters.Messages[index]));
            }

            Assert.AreEqual(20, replica.Members.Count);
            Assert.IsTrue(replica.TryGetSelf(out CoreAiRosterMember self));
            Assert.AreEqual("p19", self.Name);
        }

        [Test]
        public void NamesAreCutToTheRosterBound_WithoutSplittingACharacter()
        {
            string longName = new string('n', 63) + "é" + new string('x', 100);
            CoreAiMirrorRoster roster = new();

            Assert.IsTrue(roster.TryAddOrUpdate("actor-long", ActorAdmissionResult.Admit(ContextFor("actor-long"),
                5L, longName, longName), false, out CoreAiRosterMember member));

            Assert.LessOrEqual(Encoding.UTF8.GetByteCount(member.Name), CoreAiMirrorRoster.MaxNameBytes);
            Assert.AreEqual(new string('n', 63), member.Name, "a two-byte character that does not fit is left out whole");
        }

        [Test]
        public void Replica_AppliesSnapshotJoinAndLeave_AndCountsARevisionGap()
        {
            CoreAiMirrorRosterReplica replica = new();

            Assert.IsTrue(replica.TryApply(Message(CoreAiRosterChange.Snapshot, 3u, 2u, Entry(1u, "a"), Entry(2u, "b"))));
            Assert.IsTrue(replica.TryApply(Message(CoreAiRosterChange.Joined, 4u, 0u, Entry(3u, "c"))));
            Assert.IsTrue(replica.TryApply(Message(CoreAiRosterChange.Left, 5u, 0u, Entry(1u, ""))));

            CollectionAssert.AreEqual(new[] { "b", "c" }, NamesOf(replica));
            Assert.AreEqual(5u, replica.Revision);
            Assert.AreEqual(0, replica.RevisionGaps);
            Assert.IsTrue(replica.TryGetSelf(out CoreAiRosterMember self));
            Assert.AreEqual("b", self.Name);
            Assert.IsNull(self.ActorId, "a client is never told actor ids");

            Assert.IsTrue(replica.TryApply(Message(CoreAiRosterChange.Joined, 9u, 0u, Entry(4u, "d"))));
            Assert.AreEqual(1, replica.RevisionGaps, "a join that skips revisions is applied and counted");
            Assert.IsTrue(replica.TryApply(Message(CoreAiRosterChange.Joined, 10u, 0u, Entry(4u, "d2"))));
            CollectionAssert.AreEqual(new[] { "b", "c", "d2" }, NamesOf(replica), "a join of a known member replaces it");
        }

        [Test]
        public void Negative_Replica_RefusesWhatItCannotApply_AndChangesNothing()
        {
            CoreAiMirrorRosterReplica replica = new();

            Assert.IsFalse(replica.TryApply(Message(CoreAiRosterChange.SnapshotContinued, 1u, 0u, Entry(1u, "a"))),
                "a continuation with no snapshot before it");
            Assert.IsFalse(replica.TryApply(new CoreAiRosterMessage { Change = 99, Entries = new[] { Entry(1u, "a") } }),
                "a change this build does not know");
            Assert.IsFalse(replica.TryApply(new CoreAiRosterMessage
            {
                Change = (byte)CoreAiRosterChange.Snapshot,
                Entries = new CoreAiRosterEntry[CoreAiMirrorRosterReplica.MaxMembers + 1]
            }), "a snapshot past the bound");
            Assert.IsTrue(replica.TryApply(Message(CoreAiRosterChange.Snapshot, 1u, 0u, Entry(1u, "a"))));
            Assert.IsFalse(replica.TryApply(Message(CoreAiRosterChange.SnapshotContinued, 2u, 0u, Entry(2u, "b"))),
                "a continuation of another revision");

            CollectionAssert.AreEqual(new[] { "a" }, NamesOf(replica));
        }

        [Test]
        public void AClientBridge_KeepsTheRoster_ThroughAJoinAndALeave_OverTheWire()
        {
            _sessionHost.Dispose();
            _bridge.Dispose();
            _mirror.Dispose();
            _mirror = new OfflineMirror(loopback: true);
            GameObject go = new("CoreAI_MirrorRosterEndToEnd");
            MirrorNetworkBridge client = null;
            try
            {
                CoreAiMirrorAuthenticator authenticator = go.AddComponent<CoreAiMirrorAuthenticator>();
                authenticator.Configure(new NamedProvider(), "world-a");
                authenticator.ConfigureClientCredential(() => Encoding.UTF8.GetBytes("me"));
                _bridge = new MirrorNetworkBridge(isServer: true, authenticator, clockSeconds: () => _now, log: _ => { });
                _sessionHost = new CoreAiMirrorSessionHost(_bridge, _ => true, _ => true, _ => { });
                client = new MirrorNetworkBridge(isServer: false, clockSeconds: () => _now, log: _ => { });
                client.RoundTripSeconds = () => 0d;
                MirrorNetworkBridge bound = client;
                List<int> changes = new();
                client.RosterChanged += replica => changes.Add(replica.Members.Count);
                authenticator.OnServerAuthenticated.AddListener(conn =>
                {
                    conn.isAuthenticated = true;
                    _sessionHost.Admit(conn.connectionId, authenticator.ResultFor(conn.connectionId), sessionId: null);
                });
                authenticator.OnClientAuthenticated.AddListener(() =>
                {
                    NetworkClient.connection.isAuthenticated = true;
                    bound.BindAdmittedActor(authenticator.ClientActorId);
                });
                OfflineMirror.StartServer();
                authenticator.OnStartServer();
                _mirror.ConnectLoopback();
                authenticator.OnStartClient();
                authenticator.OnClientAuthenticate();
                _mirror.PumpLoopback();
                Assert.AreEqual(1, client.Roster.Members.Count, "the snapshot came with the readiness");

                OfflineMirror.AdmitServerConnection(5);
                _sessionHost.Admit(5, Admission("friend"), sessionId: null);
                _mirror.PumpLoopback();

                CollectionAssert.AreEqual(new[] { "me", "friend" }, NamesOf(client.Roster));
                Assert.IsTrue(client.Roster.TryGetSelf(out CoreAiRosterMember self));
                Assert.AreEqual("me", self.Name);

                _bridge.NotifyDisconnected(5, RbxNetworkDisconnectReason.TransportLost);
                _mirror.PumpLoopback();

                CollectionAssert.AreEqual(new[] { "me" }, NamesOf(client.Roster));
                CollectionAssert.AreEqual(new[] { 1, 2, 1 }, changes, "the snapshot, the join, the leave");
                Assert.AreEqual(3, client.RosterMessagesReceived);
                Assert.AreEqual(0, client.Roster.RevisionGaps);
            }
            finally
            {
                client?.Dispose();
                UnityEngine.Object.DestroyImmediate(go);
            }
        }

        [Test]
        public void Negative_AClientBridge_DropsARosterBeforeItsAdmission_AndAMalformedOneAfter()
        {
            MirrorNetworkBridge client = new(isServer: false, clockSeconds: () => _now, log: _ => { });
            try
            {
                OfflineMirror.StartClient();
                OfflineMirror.DeliverToClient(Message(CoreAiRosterChange.Snapshot, 1u, 1u, Entry(1u, "a")));
                Assert.AreEqual(1, client.UnadmittedPacketsDropped);
                Assert.AreEqual(0, client.Roster.Members.Count);

                client.BindAdmittedActor("actor-a");
                OfflineMirror.DeliverToClient(new CoreAiRosterMessage { Change = 99, Entries = Array.Empty<CoreAiRosterEntry>() });

                Assert.AreEqual(1, client.MalformedPacketsDropped);
                Assert.AreEqual(0, client.RosterMessagesReceived);
            }
            finally
            {
                client.Dispose();
            }
        }

        private void Join(int connectionId, string name)
        {
            OfflineMirror.AdmitServerConnection(connectionId);
            Assert.IsTrue(_sessionHost.Admit(connectionId, Admission(name), sessionId: null));
        }

        private static ActorAdmissionResult Admission(string name)
        {
            return ActorAdmissionResult.Admit(ContextFor("actor-" + name), 1000L + UserIdOf(name), name, name);
        }

        private static long UserIdOf(string name)
        {
            return name == "a" ? 1L : name.Length + 100L;
        }

        private static ActorContext ContextFor(string actorId)
        {
            return new LocalActorIdentityProvider(actorId, "session-" + actorId, "world-a",
                    ActorGrantSet.Create(new[] { "read" }), AgentMemoryScope.Empty)
                .GetActorContext(BuiltInAgentRoleIds.SmartChat);
        }

        private static CoreAiRosterEntry Entry(uint memberId, string name)
        {
            return new CoreAiRosterEntry { MemberId = memberId, UserId = memberId, Name = name, DisplayName = name };
        }

        private static CoreAiRosterMessage Message(CoreAiRosterChange change, uint revision, uint self,
            params CoreAiRosterEntry[] entries)
        {
            return new CoreAiRosterMessage
            {
                Change = (byte)change,
                Revision = revision,
                SelfMemberId = self,
                Entries = entries
            };
        }

        private static List<string> NamesOf(CoreAiMirrorRosterReplica replica)
        {
            List<string> names = new();
            for (int index = 0; index < replica.Members.Count; index++)
            {
                names.Add(replica.Members[index].Name);
            }

            return names;
        }

        /// <summary>The wire ids of what the transport carried to one connection, Mirror's pings left out.</summary>
        private List<ushort> BridgeMessagesTo(int connectionId)
        {
            List<ushort> ids = new();
            foreach (OfflineMirror.SentMessage sent in _mirror.ServerSends)
            {
                if (sent.ConnectionId == connectionId && sent.MessageId != NetworkMessageId<NetworkPingMessage>.Id)
                {
                    ids.Add(sent.MessageId);
                }
            }

            return ids;
        }

        private sealed class NamedProvider : IActorAdmissionProvider
        {
            public ActorAdmissionResult TryAdmit(in ActorCredential credential, string worldId)
            {
                return Admission(Encoding.UTF8.GetString(credential.Opaque));
            }
        }
    }
}
