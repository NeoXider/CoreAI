using System;
using System.Collections.Generic;
using CoreAI.Mods.Rbx.Datatypes;
using CoreAI.Mods.Rbx.Instances;
using CoreAI.Mods.Rbx.Instances.Networking;
using CoreAI.Mods.Rbx.Instances.Replication;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode.RbxApi.Replication
{
    /// <summary>
    /// The binary replication wire format, version 1: golden bytes that pin the layout written in
    /// <c>dev-docs/REPLICATION_CODEC.md</c>, round trips of every member kind, and the quantization
    /// error bounds a replica relies on.
    /// </summary>
    /// <remarks>
    /// WHY goldens and not only round trips: a round trip passes for any format both ends agree on,
    /// including one that silently doubled in size or changed between two builds that must talk to
    /// each other. The bytes are the contract a peer of another build reads.
    /// </remarks>
    [TestFixture]
    public sealed class ReplicationWireCodecEditModeTests
    {
        private const string Recipient = "alice";

        [Test]
        public void Golden_ARemoveOnlyBatch_IsMagicVersionFlagsSequenceCountThenIdDeltas()
        {
            ReplicationBatchPlan plan = new(Recipient, 1L, new List<ReplicationOperation>
            {
                new(ReplicationOperationKind.Remove, new InstanceId(5UL), 0L),
                new(ReplicationOperationKind.Remove, new InstanceId(7UL), 0L)
            });

            byte[] bytes = new ReplicationWireEncoder().EncodeToArray(plan, new FakeWireSource());

            Assert.AreEqual("CA 01 00 01 02 03 0A 03 04", WireAssert.Hex(bytes));
            ReplicationWireBatch batch = WireAssert.Decode(bytes);
            Assert.AreEqual(1L, batch.Sequence);
            Assert.IsFalse(batch.IsWorld);
            Assert.AreEqual(2, batch.Plan.Operations.Count);
            Assert.AreEqual(ReplicationOperationKind.Remove, batch.Plan.Operations[1].Kind);
            Assert.AreEqual(7UL, batch.Plan.Operations[1].InstanceId.Value);
            Assert.AreEqual(Recipient, batch.Plan.RecipientActorId);
        }

        [Test]
        public void Golden_ACFramePatchWithoutRotation_IsSixteenBytesInAOneRecordPacket()
        {
            FakeWireSource source = new();
            source.Parts[300UL] = ReplicationPartState.Of(RbxCFrame.FromPosition(1.5f, 2f, -3f),
                new RbxColor3(1f, 0f, 0f), 0.5f, false);
            ReplicationBatchPlan plan = Patch(300UL, 12L, ReplicationPartMembers.CFrame);

            byte[] bytes = new ReplicationWireEncoder().EncodeToArray(plan, source);

            // WHY these bytes: head 0x12 (Patch, part block), id 300 zigzag 600, revision 12 zigzag 24,
            // mask 0x01 (CFrame, identity rotation), then x 1.5, y 2, z -3 as zigzag counts of 1/1024.
            Assert.AreEqual("CA 01 00 01 01 12 D8 04 18 01 80 18 80 20 FF 2F", WireAssert.Hex(bytes));
            Assert.AreEqual(0, source.CaptureCalls, "a part-only patch never captures the node");
            ReplicationWireBatch batch = WireAssert.Decode(bytes);
            ReplicationOperation patch = batch.Plan.Operations[0];
            Assert.AreEqual(ReplicationOperationKind.Patch, patch.Kind);
            Assert.AreEqual(12L, patch.Revision);
            CollectionAssert.IsEmpty(patch.Members, "part members ride beside the plan, not in it");
            Assert.IsTrue(batch.TryGetPartState(new InstanceId(300UL), out ReplicationPartState part));
            Assert.AreEqual(ReplicationPartFields.CFrame, part.Fields, "only the member the plan named travels");
            WireAssert.AreExactly(RbxCFrame.FromPosition(1.5f, 2f, -3f), part.CFrame, "grid position");
        }

        [Test]
        public void Golden_AnAxisAlignedRotation_TravelsExactlyInOneByte()
        {
            RbxCFrame turned = new(8f, 4f, -2f, 0f, 0f, 1f, 0f, 1f, 0f, -1f, 0f, 0f);
            FakeWireSource source = new();
            source.Parts[9UL] = ReplicationPartState.Of(turned, new RbxColor3(0f, 0f, 0f), 0f, true);

            byte[] bytes = new ReplicationWireEncoder().EncodeToArray(Patch(9UL, 1L, ReplicationPartMembers.CFrame),
                source);

            // WHY 0x07 then 0x15: mask CFrame with both rotation bits (axis-aligned); right vector on -Z
            // (code 5), up vector on +Y (code 2), so the byte is 5 | 2 << 3.
            Assert.AreEqual("CA 01 00 01 01 12 12 02 07 80 80 01 80 40 FF 1F 15", WireAssert.Hex(bytes));
            ReplicationWireBatch batch = WireAssert.Decode(bytes);
            Assert.IsTrue(batch.TryGetPartState(new InstanceId(9UL), out ReplicationPartState part));
            WireAssert.AreExactly(turned, part.CFrame, "axis-aligned CFrame");
        }

        [Test]
        public void Golden_ASpawnAPatchAndARemove_MatchTheFormatNote()
        {
            FakeWireSource source = new();
            source.Nodes[10UL] = new InstanceSnapshot
            {
                Id = 10UL,
                ParentId = 2UL,
                ClassName = "Part",
                Name = "Block",
                Archivable = true,
                Revision = 3L,
                OwnerModId = "secret-mod",
                OwnerActorId = "owner-actor",
                Tags = new List<string> { "Ore" },
                Attributes = new List<AttributeSnapshot>
                {
                    new() { Name = "Hp", Kind = AttributeValueKind.Number, NumberValue = 10d }
                }
            };
            source.Parts[10UL] = ReplicationPartState.Of(RbxCFrame.FromPosition(4f, 0.5f, 0f),
                new RbxColor3(1f, 0.5f, 0f), 0.25f, true);
            source.Nodes[11UL] = new InstanceSnapshot { Id = 11UL, ClassName = "Part", Name = "Block" };
            ReplicationBatchPlan plan = new(Recipient, 7L, new List<ReplicationOperation>
            {
                new(ReplicationOperationKind.Spawn, new InstanceId(10UL), 3L,
                    new[] { "Name", "Archivable", "Attribute:Hp", "Tag:Ore" }),
                new(ReplicationOperationKind.Patch, new InstanceId(11UL), 3L, new[] { "Name" }),
                new(ReplicationOperationKind.Remove, new InstanceId(4UL), 0L)
            });

            byte[] bytes = new ReplicationWireEncoder().EncodeToArray(plan, source);

            string expected =
                "CA 01 00 07 03 "
                + "31 14 06 02 01 04 50 61 72 74 "
                + "79 80 40 80 08 00 FF 80 00 00 00 80 3E "
                + "04 01 01 05 42 6C 6F 63 6B 02 01 04 01 02 48 70 01 00 00 00 00 00 00 24 40 06 01 03 4F 72 65 "
                + "22 02 00 01 01 03 "
                + "03 0D";
            Assert.AreEqual(expected, WireAssert.Hex(bytes));
            string text = System.Text.Encoding.UTF8.GetString(bytes);
            StringAssert.DoesNotContain("secret-mod", text, "ownership never leaves the server");
            StringAssert.DoesNotContain("owner-actor", text, "ownership never leaves the server");

            ReplicationWireBatch batch = WireAssert.Decode(bytes);
            ReplicationOperation spawn = batch.Plan.Operations[0];
            CollectionAssert.AreEqual(new[] { "Name", "Archivable", "Attribute:Hp", "Tag:Ore" }, spawn.Members);
            InstanceSnapshot node = batch.Describe(new InstanceId(10UL));
            Assert.AreEqual("Part", node.ClassName);
            Assert.AreEqual(2UL, node.ParentId);
            Assert.AreEqual("Block", node.Name);
            Assert.IsTrue(node.Archivable);
            Assert.AreEqual(10d, node.Attributes[0].NumberValue);
            CollectionAssert.AreEqual(new[] { "Ore" }, node.Tags);
            Assert.IsNull(node.OwnerModId);
            Assert.IsNull(node.OwnerActorId);
            Assert.IsTrue(batch.TryGetPartState(new InstanceId(10UL), out ReplicationPartState part));
            Assert.AreEqual(ReplicationPartFields.All, part.Fields, "a spawn carries every part member");
            Assert.AreEqual(128f / 255f, part.Color.G, "0.5 travels as the eight-bit 128, as Color3uint8 does");
            Assert.AreEqual(0.25f, part.Transparency);
            Assert.IsTrue(part.Anchored);
            Assert.AreEqual("Block", batch.Describe(new InstanceId(11UL)).Name,
                "the second Block is a reference to the string the spawn carried");
            Assert.AreEqual(4UL, batch.Plan.Operations[2].InstanceId.Value);
        }

        [Test]
        public void Golden_AQuantizedRotation_IsSixBytesAfterThePosition()
        {
            RbxCFrame turned = RbxCFrame.FromQuaternion(0f, 0f, 0f, 0f, 0.38268343f, 0f, 0.9238795f);
            FakeWireSource source = new();
            source.Parts[1UL] = ReplicationPartState.Of(turned, new RbxColor3(0f, 0f, 0f), 0f, false);

            byte[] bytes = new ReplicationWireEncoder().EncodeToArray(Patch(1UL, 0L, ReplicationPartMembers.CFrame),
                source);

            // WHY: a 45-degree turn about Y. w is the largest component (index 3); x and z sit on the
            // zero level 16383, y = 0.382683 on level 25249 (25249.42 before rounding); packed LSB first
            // as 3 | x << 2 | y << 17 | z << 32.
            Assert.AreEqual("CA 01 00 01 01 12 02 00 03 00 00 00 FF FF 42 C5 FF 3F", WireAssert.Hex(bytes));
            ReplicationWireBatch batch = WireAssert.Decode(bytes);
            Assert.IsTrue(batch.TryGetPartState(new InstanceId(1UL), out ReplicationPartState part));
            Assert.LessOrEqual(WireAssert.RotationAngle(turned, part.CFrame), ReplicationWireFormat.RotationErrorBoundRadians);
            Assert.AreEqual(0f, part.CFrame.XVector.Y, "a turn about Y keeps the right vector level");
            Assert.AreEqual(0f, part.CFrame.ZVector.Y, "a turn about Y keeps the back vector level");
        }

        [Test]
        public void AWorldBatch_IsFlagged_AndADeltaIsNot()
        {
            ReplicationBatchPlan plan = new(Recipient, 3L, new List<ReplicationOperation>());

            byte[] world = new ReplicationWireEncoder().EncodeToArray(plan, new FakeWireSource(), isWorld: true);
            byte[] delta = new ReplicationWireEncoder().EncodeToArray(plan, new FakeWireSource());

            Assert.AreEqual("CA 01 01 03 00", WireAssert.Hex(world));
            Assert.AreEqual("CA 01 00 03 00", WireAssert.Hex(delta));
            Assert.IsTrue(WireAssert.Decode(world).IsWorld);
            Assert.IsFalse(WireAssert.Decode(delta).IsWorld);
        }

        [Test]
        public void EveryEngineFreeMemberKind_RoundTripsToTheStateTheApplierReads()
        {
            FakeWireSource source = new();
            InstanceSnapshot player = new()
            {
                Id = 40UL,
                ParentId = 3UL,
                ClassName = "Player",
                Name = "ann",
                Archivable = false,
                Revision = 9L,
                Player = new PlayerSnapshot { ActorId = "actor-ann", UserId = 4242L, DisplayName = "Ann", CharacterId = 41UL },
                Tags = new List<string> { "Vip" },
                Attributes = new List<AttributeSnapshot>
                {
                    new() { Name = "Title", Kind = AttributeValueKind.String, StringValue = "Builder" },
                    new() { Name = "Score", Kind = AttributeValueKind.Number, NumberValue = -0.1d },
                    new() { Name = "Alive", Kind = AttributeValueKind.Bool, BoolValue = true },
                    new() { Name = "Spawn", Kind = AttributeValueKind.Vector3, StringValue = "1.5,-2,3.25" },
                    new() { Name = "Uv", Kind = AttributeValueKind.Vector2, StringValue = "0.1,0.2" },
                    new() { Name = "Tint", Kind = AttributeValueKind.Color3, StringValue = "0.3,0.4,0.5" },
                    new() { Name = "Pad", Kind = AttributeValueKind.UDim, StringValue = "0.5,-12" }
                }
            };
            source.Nodes[40UL] = player;
            source.Nodes[50UL] = new InstanceSnapshot
            {
                Id = 50UL,
                ParentId = 2UL,
                ClassName = "Model",
                Name = "House",
                Model = new ModelSnapshot
                {
                    PrimaryPartId = 51UL,
                    HasStoredWorldPivot = true,
                    StoredWorldPivot = "1,2,3,1,0,0,0,1,0,0,0,1"
                }
            };
            source.Nodes[60UL] = new InstanceSnapshot
            {
                Id = 60UL,
                ParentId = 2UL,
                ClassName = "ClickDetector",
                Name = "Click",
                ClickDetector = new ClickDetectorSnapshot { MaxActivationDistance = "12.5" }
            };
            source.Nodes[61UL] = new InstanceSnapshot
            {
                Id = 61UL,
                ParentId = 2UL,
                ClassName = "Humanoid",
                Name = "Humanoid",
                Humanoid = new HumanoidSnapshot
                {
                    Health = "80", MaxHealth = "100", WalkSpeed = "16", JumpPower = "50", JumpHeight = "7.2",
                    UseJumpPower = true, DisplayName = "Npc"
                }
            };
            source.Nodes[62UL] = new InstanceSnapshot
            {
                Id = 62UL,
                ParentId = 2UL,
                ClassName = "MaterialVariant",
                Name = "Moss",
                MaterialVariant = new MaterialVariantSnapshot
                {
                    BaseMaterial = "Grass", BaseMaterialValue = 1280, ColorMap = "rbxassetid://1", NormalMap = "",
                    RoughnessMap = "", MetalnessMap = "", StudsPerTile = "4"
                }
            };
            ReplicationBatchPlan plan = new(Recipient, 2L, new List<ReplicationOperation>
            {
                new(ReplicationOperationKind.Spawn, new InstanceId(40UL), 9L, new[]
                {
                    "Name", "Archivable", "Attribute:Title", "Attribute:Score", "Attribute:Alive", "Attribute:Spawn",
                    "Attribute:Uv", "Attribute:Tint", "Attribute:Pad", "Attribute:Gone", "Tag:Vip", "Tag:Old",
                    RbxPlayer.CharacterMember, RbxPlayer.DisplayNameMember, "Size"
                }),
                new(ReplicationOperationKind.Patch, new InstanceId(50UL), 4L,
                    new[] { "Parent", "PrimaryPart", "WorldPivot" }),
                new(ReplicationOperationKind.Spawn, new InstanceId(60UL), 1L, new[] { "Name" }),
                new(ReplicationOperationKind.Spawn, new InstanceId(61UL), 1L, new[] { "Name" }),
                new(ReplicationOperationKind.Spawn, new InstanceId(62UL), 1L, new[] { "Name" })
            });

            ReplicationWireBatch batch = WireAssert.Decode(new ReplicationWireEncoder().EncodeToArray(plan, source));

            for (int index = 0; index < plan.Operations.Count; index++)
            {
                CollectionAssert.AreEqual(plan.Operations[index].Members, batch.Plan.Operations[index].Members,
                    "operation " + index + " names the same members in the same order");
                Assert.AreEqual(plan.Operations[index].Revision, batch.Plan.Operations[index].Revision);
            }

            InstanceSnapshot node = batch.Describe(new InstanceId(40UL));
            Assert.AreEqual("ann", node.Name);
            Assert.IsFalse(node.Archivable);
            Assert.AreEqual("actor-ann", node.Player.ActorId);
            Assert.AreEqual(4242L, node.Player.UserId);
            Assert.AreEqual("Ann", node.Player.DisplayName);
            Assert.AreEqual(41UL, node.Player.CharacterId);
            CollectionAssert.AreEqual(new[] { "Vip" }, node.Tags, "a removed tag is named, never added");
            Dictionary<string, AttributeSnapshot> attributes = new();
            foreach (AttributeSnapshot attribute in node.Attributes)
            {
                attributes.Add(attribute.Name, attribute);
            }

            Assert.AreEqual(7, attributes.Count, "the cleared attribute travels as a name only");
            Assert.AreEqual("Builder", attributes["Title"].StringValue);
            Assert.AreEqual(-0.1d, attributes["Score"].NumberValue, "a number attribute is exact");
            Assert.IsTrue(attributes["Alive"].BoolValue);
            Assert.AreEqual(WireAssert.CanonicalFloats("1.5,-2,3.25"), attributes["Spawn"].StringValue);
            Assert.AreEqual(WireAssert.CanonicalFloats("0.1,0.2"), attributes["Uv"].StringValue);
            Assert.AreEqual(WireAssert.CanonicalFloats("0.3,0.4,0.5"), attributes["Tint"].StringValue);
            Assert.AreEqual(WireAssert.CanonicalFloats("0.5") + ",-12", attributes["Pad"].StringValue);

            InstanceSnapshot model = batch.Describe(new InstanceId(50UL));
            Assert.AreEqual(2UL, model.ParentId);
            Assert.AreEqual(51UL, model.Model.PrimaryPartId);
            Assert.IsTrue(model.Model.HasStoredWorldPivot);
            Assert.AreEqual(WireAssert.CanonicalFloats("1,2,3,1,0,0,0,1,0,0,0,1"), model.Model.StoredWorldPivot);
            Assert.AreEqual("12.5", batch.Describe(new InstanceId(60UL)).ClickDetector.MaxActivationDistance);
            HumanoidSnapshot humanoid = batch.Describe(new InstanceId(61UL)).Humanoid;
            Assert.AreEqual("7.2", humanoid.JumpHeight);
            Assert.IsTrue(humanoid.UseJumpPower);
            Assert.AreEqual("Npc", humanoid.DisplayName);
            MaterialVariantSnapshot variant = batch.Describe(new InstanceId(62UL)).MaterialVariant;
            Assert.AreEqual("Grass", variant.BaseMaterial);
            Assert.AreEqual(1280, variant.BaseMaterialValue);
            Assert.AreEqual("rbxassetid://1", variant.ColorMap);
            Assert.AreEqual("4", variant.StudsPerTile);
        }

        [Test]
        public void EveryValueClass_RoundTripsItsPayloadExactly()
        {
            string[][] cases =
            {
                new[] { "IntValue", "-9007199254740993" },
                new[] { "NumberValue", "0.30000000000000004" },
                new[] { "StringValue", "hello, world" },
                new[] { "BoolValue", "true" },
                new[] { "Vector3Value", "1.1,2.2,-3.3" },
                new[] { "CFrameValue", "1,2,3,0.70710677,0,0.70710677,0,1,0,-0.70710677,0,0.70710677" },
                new[] { "Color3Value", "0.1,0.2,0.3" }
            };
            FakeWireSource source = new();
            List<ReplicationOperation> operations = new();
            for (int index = 0; index < cases.Length; index++)
            {
                ulong id = 100UL + (ulong)index;
                source.Nodes[id] = new InstanceSnapshot
                {
                    Id = id, ClassName = cases[index][0], Value = new ValueSnapshot { StringValue = cases[index][1] }
                };
                operations.Add(new ReplicationOperation(ReplicationOperationKind.Patch, new InstanceId(id), 1L,
                    new[] { "Value" }));
            }

            source.Nodes[200UL] = new InstanceSnapshot
            {
                Id = 200UL, ClassName = "ObjectValue", Value = new ValueSnapshot { ObjectTargetId = 77UL }
            };
            operations.Add(new ReplicationOperation(ReplicationOperationKind.Patch, new InstanceId(200UL), 1L,
                new[] { "Value" }));

            ReplicationWireBatch batch = WireAssert.Decode(new ReplicationWireEncoder().EncodeToArray(
                new ReplicationBatchPlan(Recipient, 1L, operations), source));

            for (int index = 0; index < cases.Length; index++)
            {
                string captured = cases[index][1];
                string expected = cases[index][0] switch
                {
                    "NumberValue" => WireAssert.CanonicalDouble(captured),
                    "Vector3Value" => WireAssert.CanonicalFloats(captured),
                    "CFrameValue" => WireAssert.CanonicalFloats(captured),
                    "Color3Value" => WireAssert.CanonicalFloats(captured),
                    _ => captured
                };
                Assert.AreEqual(expected, batch.Describe(new InstanceId(100UL + (ulong)index)).Value.StringValue,
                    cases[index][0] + " must rebuild the very string the server captured");
            }

            Assert.AreEqual(77UL, batch.Describe(new InstanceId(200UL)).Value.ObjectTargetId);
        }

        [Test]
        public void APatchNamingOnlyPartMembers_IsDescribedByANodeCarryingItsOwnId()
        {
            FakeWireSource source = new();
            source.Parts[5UL] = ReplicationPartState.Of(RbxCFrame.Identity, new RbxColor3(0f, 0f, 0f), 0f, false);
            source.Parts[6UL] = source.Parts[5UL];
            ReplicationBatchPlan plan = new(Recipient, 1L, new List<ReplicationOperation>
            {
                new(ReplicationOperationKind.Patch, new InstanceId(5UL), 2L, new[] { ReplicationPartMembers.Anchored }),
                new(ReplicationOperationKind.Patch, new InstanceId(6UL), 3L, new[] { ReplicationPartMembers.Color })
            });

            ReplicationWireBatch batch = WireAssert.Decode(new ReplicationWireEncoder().EncodeToArray(plan, source));

            Assert.AreEqual(5UL, batch.Describe(new InstanceId(5UL)).Id);
            Assert.AreEqual(6UL, batch.Describe(new InstanceId(6UL)).Id);
            Assert.AreEqual(5UL, batch.Describe(new InstanceId(5UL)).Id, "asked out of order, the id still matches");
            Assert.IsNull(batch.Describe(new InstanceId(7UL)), "an id the batch does not carry has no state");
            Assert.IsTrue(batch.TryGetPartState(new InstanceId(6UL), out ReplicationPartState color));
            Assert.AreEqual(ReplicationPartFields.Color, color.Fields);
        }

        [Test]
        public void Negative_APatchNamingAPartMemberTheSourceDoesNotHave_SendsOnlyTheRevision()
        {
            ReplicationBatchPlan plan = Patch(5UL, 8L, ReplicationPartMembers.CFrame);

            byte[] bytes = new ReplicationWireEncoder().EncodeToArray(plan, new FakeWireSource());

            Assert.AreEqual("CA 01 00 01 01 02 0A 10", WireAssert.Hex(bytes));
            Assert.AreEqual(0, WireAssert.Decode(bytes).PartStateCount);
        }

        [Test]
        public void PositionQuantization_StaysWithinTheStatedBound_AndIsExactOnTheGridAndFarAway()
        {
            Random random = new(1234);
            double worst = 0d;
            for (int index = 0; index < 20000; index++)
            {
                float coordinate = (float)((random.NextDouble() * 2d - 1d) * 8191d);
                float decoded = RoundTripCFrame(RbxCFrame.FromPosition(coordinate, -coordinate, coordinate * 0.5f)).X;
                worst = Math.Max(worst, Math.Abs(decoded - (double)coordinate));
            }

            TestContext.WriteLine("worst position error below 8192 studs: " + worst + " stud");
            Assert.LessOrEqual(worst, ReplicationWireFormat.PositionErrorBoundStuds);
            foreach (float exact in new[] { 0f, 1f, -1f, 0.5f, 10.25f, -255.75f, 8192f, 9000.125f, -1048575.5f, 2000000f })
            {
                RbxCFrame original = RbxCFrame.FromPosition(exact, exact, exact);
                WireAssert.AreExactly(original, RoundTripCFrame(original), "grid or far coordinate " + exact);
            }
        }

        [Test]
        public void RotationQuantization_StaysWithinTheStatedBound_ForRandomRotations()
        {
            Random random = new(99);
            double worst = 0d;
            for (int index = 0; index < 20000; index++)
            {
                RbxCFrame original = RbxCFrame.FromQuaternion(
                    (float)(random.NextDouble() * 200d - 100d), (float)(random.NextDouble() * 20d), 3f,
                    (float)Gaussian(random), (float)Gaussian(random), (float)Gaussian(random), (float)Gaussian(random));
                worst = Math.Max(worst, WireAssert.RotationAngle(original, RoundTripCFrame(original)));
            }

            TestContext.WriteLine("worst rotation error: " + worst + " rad (" + worst * 180d / Math.PI + " deg)");
            Assert.LessOrEqual(worst, ReplicationWireFormat.RotationErrorBoundRadians);
            Assert.Greater(worst, 0d, "a quantized rotation is not exact; the bound is the honest claim");
        }

        [Test]
        public void TheIdentityAndAll24AxisAlignedRotations_TravelExactly()
        {
            int aligned = 0;
            for (int right = 0; right < 6; right++)
            {
                for (int up = 0; up < 6; up++)
                {
                    if (right / 2 == up / 2)
                    {
                        continue;
                    }

                    RbxVector3 r = Axis(right);
                    RbxVector3 u = Axis(up);
                    RbxVector3 b = new(r.Y * u.Z - r.Z * u.Y, r.Z * u.X - r.X * u.Z, r.X * u.Y - r.Y * u.X);
                    RbxCFrame original = new(3f, -7.5f, 12f, r.X, u.X, b.X, r.Y, u.Y, b.Y, r.Z, u.Z, b.Z);
                    WireAssert.AreExactly(original, RoundTripCFrame(original), "axis-aligned rotation " + right + "/" + up);
                    aligned++;
                }
            }

            Assert.AreEqual(24, aligned);
            WireAssert.AreExactly(RbxCFrame.Identity, RoundTripCFrame(RbxCFrame.Identity), "identity");
        }

        [Test]
        public void Negative_AReflectionASkewAndNonFiniteValues_TravelRawAndExact()
        {
            RbxCFrame reflection = new(1f, 2f, 3f, -1f, 0f, 0f, 0f, 1f, 0f, 0f, 0f, 1f);
            RbxCFrame skewed = new(1f, 2f, 3f, 1f, 0.2f, 0f, 0f, 1f, 0f, 0f, 0f, 1f);
            RbxCFrame farAway = RbxCFrame.FromPosition(3e6f, 0f, 0f);
            RbxCFrame notANumber = RbxCFrame.FromPosition(float.NaN, 1f, 2f);

            foreach (RbxCFrame original in new[] { reflection, skewed, farAway })
            {
                WireAssert.AreExactly(original, RoundTripCFrame(original, out int bytes), "raw CFrame");
                Assert.AreEqual(5 + 4 + 48, bytes, "a raw CFrame is twelve floats after the record head");
            }

            Assert.IsTrue(float.IsNaN(RoundTripCFrame(notANumber).X), "NaN reaches the replica as NaN, not as 0");
            RoundTripCFrame(RbxCFrame.FromPosition(1.5f, 2f, -3f), out int quantized);
            Assert.AreEqual(15, quantized, "the negative twin: an ordinary CFrame stays small");
        }

        [Test]
        public void ColorQuantization_IsEightBitsPerChannel_ClampedLikeColor3uint8()
        {
            Random random = new(5);
            double worst = 0d;
            for (int index = 0; index < 5000; index++)
            {
                RbxColor3 original = new((float)random.NextDouble(), (float)random.NextDouble(), (float)random.NextDouble());
                RbxColor3 decoded = RoundTripPart(new ReplicationPartState(ReplicationPartFields.Color,
                    RbxCFrame.Identity, original, 0f, false)).Color;
                worst = Math.Max(worst, Math.Abs(decoded.R - (double)original.R));
                worst = Math.Max(worst, Math.Abs(decoded.G - (double)original.G));
                worst = Math.Max(worst, Math.Abs(decoded.B - (double)original.B));
            }

            Assert.LessOrEqual(worst, ReplicationWireFormat.ColorErrorBound + 1e-7);
            RbxColor3 clamped = RoundTripPart(new ReplicationPartState(ReplicationPartFields.Color, RbxCFrame.Identity,
                new RbxColor3(2f, -1f, float.NaN), 0f, false)).Color;
            Assert.AreEqual(1f, clamped.R);
            Assert.AreEqual(0f, clamped.G);
            Assert.AreEqual(0f, clamped.B);
        }

        [Test]
        public void TransparencyAndAnchored_TravelExactly()
        {
            ReplicationPartState decoded = RoundTripPart(new ReplicationPartState(
                ReplicationPartFields.Transparency | ReplicationPartFields.Anchored, RbxCFrame.Identity,
                new RbxColor3(0f, 0f, 0f), 0.123456789f, false));

            Assert.AreEqual(ReplicationPartFields.Transparency | ReplicationPartFields.Anchored, decoded.Fields);
            Assert.AreEqual(0.123456789f, decoded.Transparency);
            Assert.IsFalse(decoded.Anchored, "Anchored = false is carried, not mistaken for absent");
        }

        [Test]
        public void Negative_ALocalIdAMissingSpawnAndAnOversizedBatch_AreRefusedAtEncode()
        {
            ReplicationWireEncoder encoder = new(64);
            ReplicationBatchPlan local = new(Recipient, 1L, new List<ReplicationOperation>
            {
                new(ReplicationOperationKind.Remove, new InstanceId(InstanceId.AuthorityBit | 5UL), 0L)
            });
            ReplicationBatchPlan missing = new(Recipient, 1L, new List<ReplicationOperation>
            {
                new(ReplicationOperationKind.Spawn, new InstanceId(5UL), 1L, new[] { "Name" })
            });
            List<ReplicationOperation> many = new();
            for (int index = 1; index <= 40; index++)
            {
                many.Add(new ReplicationOperation(ReplicationOperationKind.Remove, new InstanceId((ulong)index), 0L));
            }

            Assert.AreEqual(ReplicationWireError.InvalidInstanceId,
                Assert.Throws<ReplicationWireException>(() => encoder.Encode(local, new FakeWireSource())).Error);
            Assert.AreEqual(ReplicationWireError.MissingState,
                Assert.Throws<ReplicationWireException>(() => encoder.Encode(missing, new FakeWireSource())).Error);
            Assert.AreEqual(ReplicationWireError.PacketTooLarge,
                Assert.Throws<ReplicationWireException>(() =>
                    encoder.Encode(new ReplicationBatchPlan(Recipient, 1L, many), new FakeWireSource())).Error);
            Assert.AreEqual(ReplicationWireError.InvalidSequence,
                Assert.Throws<ReplicationWireException>(() =>
                    encoder.Encode(new ReplicationBatchPlan(Recipient, 0L, new List<ReplicationOperation>()),
                        new FakeWireSource())).Error);
            Assert.AreEqual(5 + 2 * 29, encoder.Encode(new ReplicationBatchPlan(Recipient, 1L, many.GetRange(0, 29)),
                new FakeWireSource()).Count, "the negative twin: a batch that fits the same cap encodes");
        }

        [Test]
        public void EncodingCFramePatches_AllocatesNothingOnceTheBufferHasGrown()
        {
            FakeWireSource source = new();
            List<ReplicationOperation> operations = new();
            for (int index = 1; index <= 200; index++)
            {
                source.Parts[(ulong)index] = ReplicationPartState.Of(
                    RbxCFrame.FromQuaternion(index * 1.25f, 3f, -index, 0.1f, 0.7f, 0.1f, 0.7f),
                    new RbxColor3(0f, 0f, 0f), 0f, false);
                operations.Add(new ReplicationOperation(ReplicationOperationKind.Patch, new InstanceId((ulong)index),
                    1000L + index, new[] { ReplicationPartMembers.CFrame }));
            }

            ReplicationBatchPlan plan = new(Recipient, 1L, operations);
            ReplicationWireEncoder encoder = new();
            encoder.Encode(plan, source);
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int round = 0; round < 10; round++)
            {
                encoder.Encode(plan, source);
            }

            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.AreEqual(0, source.CaptureCalls);
            Assert.AreEqual(0L, allocated, "2000 CFrame patches must encode without a managed allocation");
        }

        internal static ReplicationBatchPlan Patch(ulong id, long revision, params string[] members)
        {
            return new ReplicationBatchPlan(Recipient, 1L, new List<ReplicationOperation>
            {
                new(ReplicationOperationKind.Patch, new InstanceId(id), revision, members)
            });
        }

        private static RbxCFrame RoundTripCFrame(RbxCFrame original)
        {
            return RoundTripCFrame(original, out _);
        }

        private static RbxCFrame RoundTripCFrame(RbxCFrame original, out int bytes)
        {
            ReplicationPartState decoded = RoundTripPart(new ReplicationPartState(ReplicationPartFields.CFrame, original,
                new RbxColor3(0f, 0f, 0f), 0f, false), out bytes);
            return decoded.CFrame;
        }

        private static ReplicationPartState RoundTripPart(ReplicationPartState state)
        {
            return RoundTripPart(state, out _);
        }

        private static ReplicationPartState RoundTripPart(ReplicationPartState state, out int bytes)
        {
            FakeWireSource source = new();
            source.Parts[1UL] = state;
            List<string> members = new();
            if (state.Has(ReplicationPartFields.CFrame))
            {
                members.Add(ReplicationPartMembers.CFrame);
            }

            if (state.Has(ReplicationPartFields.Color))
            {
                members.Add(ReplicationPartMembers.Color);
            }

            if (state.Has(ReplicationPartFields.Transparency))
            {
                members.Add(ReplicationPartMembers.Transparency);
            }

            if (state.Has(ReplicationPartFields.Anchored))
            {
                members.Add(ReplicationPartMembers.Anchored);
            }

            byte[] encoded = new ReplicationWireEncoder().EncodeToArray(Patch(1UL, 0L, members.ToArray()), source);
            bytes = encoded.Length;
            ReplicationWireBatch batch = WireAssert.Decode(encoded);
            Assert.IsTrue(batch.TryGetPartState(new InstanceId(1UL), out ReplicationPartState decoded));
            return decoded;
        }

        private static RbxVector3 Axis(int code)
        {
            float sign = (code & 1) == 0 ? 1f : -1f;
            return (code / 2) switch
            {
                0 => new RbxVector3(sign, 0f, 0f),
                1 => new RbxVector3(0f, sign, 0f),
                _ => new RbxVector3(0f, 0f, sign)
            };
        }

        private static double Gaussian(Random random)
        {
            double u = 1d - random.NextDouble();
            double v = random.NextDouble();
            return Math.Sqrt(-2d * Math.Log(u)) * Math.Cos(2d * Math.PI * v);
        }
    }
}
