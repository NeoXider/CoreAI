using System;
using System.Collections.Generic;
using System.Linq;
using CoreAI.Mods.Rbx.Datatypes;
using CoreAI.Mods.Rbx.Instances;
using CoreAI.Mods.Rbx.Instances.Networking;
using CoreAI.Mods.Rbx.Instances.Replication;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode.RbxApi.Replication
{
    /// <summary>
    /// The per-tick publisher over the binary codec: when it publishes, what it coalesces, the order a
    /// recipient sees, what a budget holds back, and that a client fed only bytes converges through the
    /// existing applier — a late joiner and a resync included.
    /// </summary>
    [TestFixture]
    public sealed class ReplicationPublisherEditModeTests
    {
        private const double Tick = 0.0625d;

        [Test]
        public void TheFirstTick_SendsEachRecipientItsWorld_ThenOnlyDeltas()
        {
            using WireWorld world = NewWorld();
            WireClient alice = world.Join("alice");
            RbxInstance brick = world.CreatePart("Brick", world.Workspace, RbxCFrame.FromPosition(0f, 5f, 0f));

            Assert.IsTrue(world.Advance(0d), "the first update publishes at once");

            List<SentPacket> sent = world.SentTo("alice");
            Assert.AreEqual(1, sent.Count);
            Assert.AreEqual(ReplicationPacketKind.World, sent[0].Kind);
            Assert.AreEqual(1L, sent[0].Sequence);
            Assert.IsTrue(alice.LastResult.IsApplied, alice.LastResult.Applied?.ToString());
            world.AssertConverged("alice");

            world.Move(brick, RbxCFrame.FromPosition(1f, 5f, 0f));
            Assert.IsTrue(world.Advance(Tick));

            sent = world.SentTo("alice");
            Assert.AreEqual(2, sent.Count);
            Assert.AreEqual(ReplicationPacketKind.Delta, sent[1].Kind);
            Assert.AreEqual(1, sent[1].OperationCount, "one moved part is one record");
            Assert.IsTrue(alice.LastResult.IsApplied);
            Assert.AreEqual(1, alice.LastResult.PartsApplied);
            world.AssertConverged("alice");
            Assert.IsEmpty(alice.ResyncRequests);
        }

        [Test]
        public void Coalescing_TheLastWriteOfAMemberInATick_Wins()
        {
            using WireWorld world = NewWorld();
            WireClient alice = world.Join("alice");
            RbxInstance brick = world.CreatePart("Brick", world.Workspace, RbxCFrame.Identity);
            world.Advance(0d);

            brick.Name = "A";
            brick.Name = "B";
            brick.Name = "C";
            world.Move(brick, RbxCFrame.FromPosition(1f, 0f, 0f));
            world.Move(brick, RbxCFrame.FromPosition(2f, 0f, 0f));
            world.Move(brick, RbxCFrame.FromPosition(3f, 0f, 0f));
            world.Advance(Tick);

            List<SentPacket> sent = world.SentTo("alice");
            Assert.AreEqual(2, sent.Count, "six writes in one tick are one packet");
            ReplicationWireBatch batch = WireAssert.Decode(sent[1].Bytes);
            Assert.AreEqual(1, batch.Plan.Operations.Count, "and one record");
            CollectionAssert.AreEqual(new[] { "Name" }, batch.Plan.Operations[0].Members, "naming Name once");
            Assert.AreEqual("C", batch.Describe(brick.Id).Name);
            Assert.IsTrue(batch.TryGetPartState(brick.Id, out ReplicationPartState part));
            Assert.AreEqual(3f, part.CFrame.X, "the last CFrame of the tick is the one sent");
            Assert.AreEqual("C", alice.Find(brick).Name);
            world.AssertConverged("alice");
        }

        [Test]
        public void Negative_TheSameWritesInThreeTicks_AreThreePackets()
        {
            using WireWorld world = NewWorld();
            world.Join("alice");
            RbxInstance brick = world.CreatePart("Brick", world.Workspace, RbxCFrame.Identity);
            world.Advance(0d);

            foreach (string name in new[] { "A", "B", "C" })
            {
                brick.Name = name;
                world.Advance(Tick);
            }

            Assert.AreEqual(4, world.SentTo("alice").Count);
            world.AssertConverged("alice");
        }

        [Test]
        public void Coalescing_AnInstanceSpawnedAndRemovedInOneTick_SendsNothingForIt()
        {
            using WireWorld world = NewWorld();
            WireClient alice = world.Join("alice");
            world.Advance(0d);

            RbxInstance ghost = world.CreatePart("Ghost", world.Workspace, RbxCFrame.Identity);
            ghost.Destroy();
            world.Advance(Tick);

            foreach (SentPacket packet in world.SentTo("alice"))
            {
                foreach (ReplicationOperation operation in WireAssert.Decode(packet.Bytes).Plan.Operations)
                {
                    Assert.AreNotEqual(ghost.Id, operation.InstanceId,
                        "the ghost was born and died between two ticks; no record may name it");
                }
            }

            Assert.IsNull(alice.Find(ghost));
            world.AssertConverged("alice");
        }

        [Test]
        public void Negative_AnInstanceSpawnedAndKept_IsASpawn()
        {
            using WireWorld world = NewWorld();
            WireClient alice = world.Join("alice");
            world.Advance(0d);

            RbxInstance kept = world.CreatePart("Kept", world.Workspace, RbxCFrame.Identity);
            world.Advance(Tick);

            List<SentPacket> sent = world.SentTo("alice");
            ReplicationWireBatch batch = WireAssert.Decode(sent[sent.Count - 1].Bytes);
            Assert.IsTrue(batch.Plan.Operations.Any(op =>
                op.InstanceId == kept.Id && op.Kind == ReplicationOperationKind.Spawn));
            Assert.IsNotNull(alice.Find(kept));
            world.AssertConverged("alice");
        }

        [Test]
        public void Coalescing_ARemovalWinsOverEveryChangeInTheSameTick()
        {
            using WireWorld world = NewWorld();
            WireClient alice = world.Join("alice");
            RbxInstance brick = world.CreatePart("Brick", world.Workspace, RbxCFrame.Identity);
            world.Advance(0d);

            brick.Name = "Renamed";
            world.Move(brick, RbxCFrame.FromPosition(9f, 9f, 9f));
            brick.Destroy();
            world.Advance(Tick);

            List<SentPacket> sent = world.SentTo("alice");
            ReplicationWireBatch batch = WireAssert.Decode(sent[sent.Count - 1].Bytes);
            List<ReplicationOperation> forBrick = batch.Plan.Operations.Where(op => op.InstanceId == brick.Id).ToList();
            Assert.AreEqual(1, forBrick.Count);
            Assert.AreEqual(ReplicationOperationKind.Remove, forBrick[0].Kind);
            Assert.IsNull(alice.Find(brick));
            world.AssertConverged("alice");
        }

        [Test]
        public void Update_PublishesOnlyAtTickBoundaries_OnTheInjectedClock()
        {
            using WireWorld world = NewWorld();
            world.Join("alice");

            Assert.IsTrue(world.Advance(0d));
            for (int step = 0; step < 3; step++)
            {
                Assert.IsFalse(world.Advance(Tick / 4d), "a quarter interval at a time does not reach the tick");
            }

            Assert.IsTrue(world.Advance(Tick / 4d), "the fourth quarter does");
            Assert.AreEqual(2L, world.Publisher.TickCount);
            Assert.AreEqual(Tick, world.Publisher.TickIntervalSeconds);
        }

        [Test]
        public void Update_AfterAStall_PublishesOnce_AndResumesOneIntervalLater()
        {
            using WireWorld world = NewWorld();
            world.Join("alice");
            world.Advance(0d);

            Assert.IsTrue(world.Advance(1d), "a one-second stall is caught up by one tick");
            Assert.IsFalse(world.Advance(0d), "not by sixteen");
            Assert.IsFalse(world.Advance(Tick / 2d));
            Assert.IsTrue(world.Advance(Tick / 2d));
            Assert.AreEqual(3L, world.Publisher.TickCount);
        }

        [Test]
        public void Ordering_EachRecipientSeesConsecutiveSequences_WorldFirst_AndEveryBatchApplies()
        {
            using WireWorld world = NewWorld();
            WireClient alice = world.Join("alice");
            WireClient bob = world.Join("bob");
            RbxInstance folder = world.Create("Folder", "Map", world.Workspace);
            List<RbxInstance> parts = new();
            for (int index = 0; index < 8; index++)
            {
                parts.Add(world.CreatePart("P" + index, folder, RbxCFrame.FromPosition(index, 0f, 0f)));
            }

            world.Advance(0d);
            for (int step = 1; step <= 12; step++)
            {
                world.Move(parts[step % parts.Count], RbxCFrame.FromPosition(step, step, 0f));
                if (step % 3 == 0)
                {
                    RbxInstance child = world.CreatePart("Child" + step, parts[0], RbxCFrame.Identity);
                    child.SetAttribute("Step", (double)step);
                }

                if (step % 4 == 0)
                {
                    parts[1].Parent = step % 8 == 0 ? folder : world.Workspace;
                }

                world.Advance(Tick);
            }

            foreach (WireClient client in new[] { alice, bob })
            {
                List<SentPacket> sent = world.SentTo(client.ActorId);
                Assert.AreEqual(ReplicationPacketKind.World, sent[0].Kind);
                for (int index = 0; index < sent.Count; index++)
                {
                    Assert.AreEqual(index + 1L, sent[index].Sequence, client.ActorId + " packet " + index);
                    Assert.IsTrue(client.Results[index].IsApplied, client.Results[index].Applied?.ToString());
                }

                Assert.IsEmpty(client.ResyncRequests);
                world.AssertConverged(client.ActorId);
            }
        }

        [Test]
        public void Budget_AnEmptyBucketDefersTheRecipient_AndItsNextPacketCarriesEverythingCoalesced()
        {
            using WireWorld world = NewWorld();
            WireClient alice = world.Join("alice");
            WireClient bob = world.Join("bob", new ReplicationRecipientBudget(256, 64));
            List<RbxInstance> parts = new();
            for (int index = 0; index < 10; index++)
            {
                parts.Add(world.CreatePart("P" + index, world.Workspace, RbxCFrame.FromPosition(index, 0f, 0f)));
            }

            world.Advance(0d);
            Assert.IsTrue(world.Publisher.TryGetRecipient("bob", out ReplicationRecipientInfo afterWorld));
            Assert.Less(afterWorld.Tokens, 0d, "the world went out over budget and is paid for afterwards");

            for (int step = 1; step <= 4; step++)
            {
                foreach (RbxInstance part in parts)
                {
                    world.Move(part, RbxCFrame.FromPosition(step * 10f, 0f, part.Name.Length));
                }

                parts[0].Name = "Moved" + step;
                world.Advance(Tick);
            }

            Assert.AreEqual(5, world.SentTo("alice").Count, "the unlimited recipient got a delta every tick");
            Assert.AreEqual(1, world.SentTo("bob").Count, "bob is still paying off his world");
            world.Publisher.TryGetRecipient("bob", out ReplicationRecipientInfo deferred);
            Assert.AreEqual(4L, deferred.DeferredTicks);
            Assert.AreEqual(10, deferred.BacklogCount, "ten instances changed, however many writes it took");

            int guard = 0;
            while (world.SentTo("bob").Count < 2 && guard++ < 400)
            {
                world.Advance(Tick);
            }

            List<SentPacket> bobs = world.SentTo("bob");
            Assert.AreEqual(2, bobs.Count);
            Assert.AreEqual(2L, bobs[1].Sequence, "the held-back changes go out as the very next sequence");
            Assert.AreEqual(10, bobs[1].OperationCount, "one record per changed instance");
            Assert.AreEqual("Moved4", bob.Find(parts[0]).Name, "with the last value written");
            world.AssertConverged("alice");
            world.AssertConverged("bob");
            world.Publisher.TryGetRecipient("alice", out ReplicationRecipientInfo unlimited);
            Assert.AreEqual(0L, unlimited.DeferredTicks, "the negative twin: an unlimited budget never defers");
        }

        [Test]
        public void Budget_AWorldIsSentWhenDue_EvenFromAnEmptyBucket()
        {
            using WireWorld world = NewWorld();
            WireClient bob = world.Join("bob", new ReplicationRecipientBudget(16, 16));
            world.Advance(0d);
            world.Publisher.RequestWorld("bob");

            world.Advance(Tick);

            List<SentPacket> sent = world.SentTo("bob");
            Assert.AreEqual(2, sent.Count);
            Assert.AreEqual(ReplicationPacketKind.World, sent[1].Kind, "nothing can reach a replica before its world");
            Assert.IsTrue(bob.LastResult.IsApplied, bob.LastResult.Applied?.ToString());
            world.AssertConverged("bob");
        }

        [Test]
        public void EnqueueAfterState_RunsRightAfterThePacketCarryingEarlierWrites()
        {
            using WireWorld world = NewWorld();
            WireClient alice = world.Join("alice");
            RbxInstance lamp = world.CreatePart("Lamp", world.Workspace, RbxCFrame.Identity);
            world.Advance(0d);
            List<string> order = new();
            world.OnSent = packet => order.Add("packet " + packet.Sequence);

            lamp.Name = "Lit";
            string seenByTheClient = null;
            Assert.IsTrue(world.Publisher.EnqueueAfterState("alice", () =>
            {
                order.Add("remote");
                seenByTheClient = alice.Find(lamp).Name;
            }));
            Assert.IsEmpty(order, "nothing runs before the tick");

            world.Advance(Tick);

            CollectionAssert.AreEqual(new[] { "packet 2", "remote" }, order);
            Assert.AreEqual("Lit", seenByTheClient, "the event reaches a client that already has the write");

            world.Publisher.EnqueueAfterState("alice", () => order.Add("idle remote"));
            world.Advance(Tick);
            Assert.AreEqual("idle remote", order[order.Count - 1], "with nothing to send it runs at the next tick");
            Assert.IsFalse(world.Publisher.EnqueueAfterState("nobody", () => { }));
        }

        [Test]
        public void EnqueueAfterState_WaitsWhileTheBudgetHoldsTheRecipientsStateBack()
        {
            using WireWorld world = NewWorld();
            WireClient bob = world.Join("bob", new ReplicationRecipientBudget(128, 32));
            RbxInstance lamp = world.CreatePart("Lamp", world.Workspace, RbxCFrame.Identity);
            world.Advance(0d);
            lamp.Name = "Lit";
            string seenByTheClient = null;
            world.Publisher.EnqueueAfterState("bob", () => seenByTheClient = bob.Find(lamp).Name);

            world.Advance(Tick);
            Assert.IsNull(seenByTheClient, "the remote must not overtake the state it follows");

            int guard = 0;
            while (seenByTheClient == null && guard++ < 400)
            {
                world.Advance(Tick);
            }

            Assert.AreEqual("Lit", seenByTheClient);
        }

        [Test]
        public void ALateJoiner_ConvergesThroughTheApplier_FromBytesAlone()
        {
            using WireWorld world = NewWorld();
            WireClient alice = world.Join("alice");
            RbxInstance map = world.Create("Folder", "Map", world.Workspace);
            RbxInstance house = world.Create("Model", "House", map);
            List<RbxInstance> parts = new();
            for (int index = 0; index < 20; index++)
            {
                RbxInstance part = world.CreatePart("Block" + index, index < 4 ? house : map,
                    RbxCFrame.FromPosition(index * 4f, 2f, -index));
                part.SetAttribute("Hp", 100d - index);
                if (index % 5 == 0)
                {
                    part.AddTag("Ore");
                }

                parts.Add(part);
            }

            ((RbxModel)house).SetPrimaryPart(parts[0]);
            RbxIntValue score = (RbxIntValue)world.Create("IntValue", "Score", world.Game.GetService("ReplicatedStorage"));
            score.Value = 7L;
            world.Create("Folder", "Secrets", world.Game.GetService("ServerStorage"));
            world.Advance(0d);

            Random random = new(3);
            for (int step = 1; step <= 6; step++)
            {
                foreach (RbxInstance part in parts)
                {
                    if (!part.IsDestroyed)
                    {
                        world.Move(part, RbxCFrame.FromQuaternion((float)random.NextDouble() * 50f, 2f,
                            (float)random.NextDouble() * 50f, 0f, (float)random.NextDouble(), 0f, 1f));
                    }
                }

                parts[step].SetAttribute("Hp", null);
                score.Value = step * 10L;
                if (step == 3)
                {
                    parts[19].Destroy();
                }

                world.Advance(Tick);
            }

            WireClient bob = world.Join("bob");
            world.Advance(Tick);

            List<SentPacket> bobs = world.SentTo("bob");
            Assert.AreEqual(1, bobs.Count);
            Assert.AreEqual(ReplicationPacketKind.World, bobs[0].Kind);
            Assert.IsTrue(bob.LastResult.IsApplied, bob.LastResult.Applied?.ToString());
            world.AssertConverged("bob");
            Assert.AreSame(bob.Find(parts[0]), ((RbxModel)bob.Find(house)).PrimaryPart,
                "the PrimaryPart reference resolves on the replica");
            Assert.AreEqual(60L, ((RbxIntValue)bob.Find(score)).Value);
            Assert.IsNull(bob.Find(parts[19]), "a part destroyed before the join is not in the world");

            for (int step = 0; step < 4; step++)
            {
                world.Move(parts[step], RbxCFrame.FromPosition(-step, 0f, step));
                world.Advance(Tick);
            }

            world.AssertConverged("alice");
            world.AssertConverged("bob");
            foreach (WireClient client in new[] { alice, bob })
            {
                Assert.IsEmpty(client.ResyncRequests, client.ActorId);
                Assert.IsFalse(client.Diagnostics.Exists(line => line.Contains("not applied by the engine-free core")),
                    client.ActorId + ": part members must not reach the engine-free applier as member names");
            }
        }

        [Test]
        public void Players_ArriveWithTheirIdentity_AndABackpackReachesOnlyItsOwner_OverTheWire()
        {
            using WireWorld world = NewWorld();
            WireClient alice = world.Join("alice");
            RbxPlayers players = (RbxPlayers)world.Game.GetService("Players");
            players.CharacterAutoLoads = false;
            RbxPlayer alicePlayer = players.EnsureActor(world.Server, "alice");
            RbxPlayer bobPlayer = players.EnsureActor(world.Server, "bob");
            RbxInstance sword = world.Create("Folder", "Sword", alicePlayer.FindFirstChildOfClass("Backpack"));
            world.Advance(0d);
            bobPlayer.DisplayName = "Bobby";
            world.Advance(Tick);

            WireClient bob = world.Join("bob");
            world.Advance(Tick);

            foreach (WireClient client in new[] { alice, bob })
            {
                RbxPlayers replicaPlayers = (RbxPlayers)client.Find(players);
                Assert.AreEqual(2, replicaPlayers.GetPlayers().Count, client.ActorId + " admits both players");
                Assert.IsTrue(replicaPlayers.TryGetByActorId("bob", out RbxPlayer replicaBob));
                Assert.AreEqual(bobPlayer.UserId, replicaBob.UserId);
                Assert.AreEqual("Bobby", replicaBob.DisplayName);
                Assert.IsTrue(client.Results.TrueForAll(result => result.IsApplied), client.ActorId);
                world.AssertConverged(client.ActorId);
            }

            Assert.IsNotNull(alice.Find(sword), "a player's own inventory arrives");
            Assert.IsNull(bob.Find(sword), "another player's inventory never arrives, late joiner included");
        }

        [Test]
        public void AGap_IsAnsweredByRequestWorld_AndConvergesInPlace()
        {
            using WireWorld world = NewWorld();
            WireClient alice = world.Join("alice");
            RbxInstance brick = world.CreatePart("Brick", world.Workspace, RbxCFrame.Identity);
            world.Advance(0d);
            RbxInstance replicaBrick = alice.Find(brick);

            alice.Hold = true;
            world.Move(brick, RbxCFrame.FromPosition(1f, 0f, 0f));
            world.Advance(Tick);
            brick.Name = "Renamed";
            world.Advance(Tick);
            alice.Held.RemoveAt(0);
            alice.Hold = false;
            alice.Deliver(alice.Held[0]);
            alice.Held.Clear();

            Assert.AreEqual(ReplicationApplyStatus.GapDetected, alice.LastResult.Applied.Status);
            Assert.AreEqual(1, alice.ResyncRequests.Count);

            Assert.IsTrue(world.Publisher.RequestWorld("alice"));
            world.Advance(Tick);

            Assert.IsTrue(alice.LastResult.IsApplied, alice.LastResult.Applied?.ToString());
            Assert.AreEqual(ReplicationPacketKind.World, world.SentTo("alice")[3].Kind);
            Assert.AreSame(replicaBrick, alice.Find(brick), "the world is applied onto the replica in place");
            world.AssertConverged("alice");
        }

        [Test]
        public void AnOversizedBatch_FaultsItsRecipientInsteadOfSendingAPartialPacket()
        {
            ReplicationPublisherOptions options = new() { TickRateHz = 1d / Tick, MaxPacketBytes = 4096 };
            using WireWorld world = new(options);
            WireClient alice = world.Join("alice");
            world.Advance(0d);
            List<RbxInstance> parts = new();
            for (int index = 0; index < 200; index++)
            {
                parts.Add(world.CreatePart("Bulk" + index, world.Workspace,
                    RbxCFrame.FromQuaternion(index, 1f, 2f, 0.3f, 0.4f, 0.5f, 0.6f)));
            }

            world.Advance(Tick);

            Assert.AreEqual(1, world.SentTo("alice").Count, "no partial packet left the server");
            world.Publisher.TryGetRecipient("alice", out ReplicationRecipientInfo faulted);
            Assert.IsTrue(faulted.IsFaulted);
            StringAssert.Contains("PacketTooLarge", faulted.FaultReason, "the reason names the refusal");
            StringAssert.Contains("4096", faulted.FaultReason);
            Assert.AreEqual(1, world.PublisherDiagnostics.Count, "reported once");

            world.Move(parts[0], RbxCFrame.Identity);
            world.Advance(Tick);
            Assert.AreEqual(1, world.SentTo("alice").Count, "a faulted recipient gets nothing until its world is requested");
            Assert.AreEqual(1, world.PublisherDiagnostics.Count, "and is not reported every tick");

            for (int index = 20; index < parts.Count; index++)
            {
                parts[index].Destroy();
            }

            world.Publisher.RequestWorld("alice");
            world.Advance(Tick);

            Assert.IsTrue(alice.LastResult.IsApplied, alice.LastResult.Applied?.ToString());
            world.AssertConverged("alice");
        }

        [Test]
        public void Negative_TheSameBulkSpawn_FitsTheDefaultCap()
        {
            using WireWorld world = NewWorld();
            WireClient alice = world.Join("alice");
            world.Advance(0d);
            for (int index = 0; index < 200; index++)
            {
                world.CreatePart("Bulk" + index, world.Workspace,
                    RbxCFrame.FromQuaternion(index, 1f, 2f, 0.3f, 0.4f, 0.5f, 0.6f));
            }

            world.Advance(Tick);

            Assert.AreEqual(2, world.SentTo("alice").Count);
            Assert.IsTrue(alice.LastResult.IsApplied);
            world.AssertConverged("alice");
        }

        [Test]
        public void ASendFunctionThatThrows_FaultsOnlyThatRecipient()
        {
            using WireWorld world = NewWorld();
            WireClient alice = world.Join("alice");
            WireClient bob = world.Join("bob");
            RbxInstance brick = world.CreatePart("Brick", world.Workspace, RbxCFrame.Identity);
            world.Advance(0d);
            world.OnSent = packet =>
            {
                if (packet.RecipientActorId == "bob")
                {
                    throw new InvalidOperationException("socket closed");
                }
            };

            world.Move(brick, RbxCFrame.FromPosition(4f, 0f, 0f));
            world.Advance(Tick);
            world.Move(brick, RbxCFrame.FromPosition(5f, 0f, 0f));
            world.Advance(Tick);

            world.AssertConverged("alice");
            world.Publisher.TryGetRecipient("bob", out ReplicationRecipientInfo info);
            Assert.IsTrue(info.IsFaulted);
            StringAssert.Contains("socket closed", info.FaultReason);
            Assert.AreEqual(1, world.PublisherDiagnostics.Count);

            world.OnSent = null;
            world.Publisher.RequestWorld("bob");
            world.Advance(Tick);

            Assert.IsTrue(bob.LastResult.IsApplied, bob.LastResult.Applied?.ToString());
            world.AssertConverged("bob");
        }

        [Test]
        public void RemovingARecipient_StopsItsPackets_AndTheOthersContinue()
        {
            using WireWorld world = NewWorld();
            world.Join("alice");
            world.Join("bob");
            RbxInstance brick = world.CreatePart("Brick", world.Workspace, RbxCFrame.Identity);
            world.Advance(0d);

            Assert.IsTrue(world.Publisher.RemoveRecipient("bob"));
            Assert.IsFalse(world.Publisher.RemoveRecipient("bob"));
            world.Move(brick, RbxCFrame.FromPosition(2f, 0f, 0f));
            world.Advance(Tick);

            Assert.AreEqual(1, world.SentTo("bob").Count);
            Assert.AreEqual(2, world.SentTo("alice").Count);
            Assert.AreEqual(1, world.Publisher.RecipientCount);
            Assert.IsFalse(world.Publisher.TryGetRecipient("bob", out _));
        }

        [Test]
        public void TheConstructor_RefusesWhatCannotPublish()
        {
            using WireWorld world = NewWorld();
            Assert.Throws<ArgumentNullException>(() =>
                new ReplicationPublisher(null, () => 0d, (in ReplicationOutgoingPacket packet) => { }));
            Assert.Throws<ArgumentNullException>(() =>
                new ReplicationPublisher(world.Dirty, null, (in ReplicationOutgoingPacket packet) => { }));
            Assert.Throws<ArgumentNullException>(() => new ReplicationPublisher(world.Dirty, () => 0d, null));
            Assert.Throws<ArgumentOutOfRangeException>(() => new ReplicationPublisher(world.Dirty, () => 0d,
                (in ReplicationOutgoingPacket packet) => { }, null, new ReplicationPublisherOptions { TickRateHz = 0d }));
            Assert.Throws<ArgumentException>(() => world.Publisher.AddRecipient(" "));
            world.Publisher.AddRecipient("alice");
            Assert.Throws<ArgumentException>(() => world.Publisher.AddRecipient("alice"));
        }

        private static WireWorld NewWorld()
        {
            return new WireWorld(new ReplicationPublisherOptions { TickRateHz = 1d / Tick });
        }
    }
}
