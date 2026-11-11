using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Ai;
using CoreAI.Ai.LuaCs;
using CoreAI.Mods.Rbx.Binding;
using CoreAI.Mods.Rbx.Datatypes;
using CoreAI.Mods.Rbx.Instances;
using CoreAI.Mods.WorldPackages;
using CoreAI.Sandbox.LuaCs;
using CoreAI.Tests.EditMode;
using Cysharp.Threading.Tasks;
using Newtonsoft.Json;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode.RbxApi.Acceptance
{
    /// <summary>
    /// The encode in front of an AI edit: <c>world.json</c> is written straight from the payload (byte for byte what
    /// the DTO serializer wrote), its size is bounded without formatting a number, and under write-behind a world
    /// proven encodable is encoded behind the edit, while every refusal is still known before it.
    /// </summary>
    [TestFixture]
    public sealed class WorldPackageEncodeBehindEditModeTests
    {
        private static readonly DateTime CapturedAtUtc = new(2026, 11, 11, 12, 30, 0, DateTimeKind.Utc);

        private readonly List<string> _directories = new();

        [TearDown]
        public void DeleteTemporaryDirectories()
        {
            foreach (string directory in _directories)
            {
                if (Directory.Exists(directory))
                {
                    Directory.Delete(directory, true);
                }
            }

            _directories.Clear();
        }

        // ==================== world.json writer and its bound ====================

        /// <summary>Every block and every awkward string writes the same bytes as the DTO serializer of 7.56.0.</summary>
        [Test]
        public void WorldJson_DirectWriter_MatchesTheDtoSerializerByteForByte()
        {
            foreach (RbxWorldPackagePayload payload in EveryShapeOfPayload())
            {
                CollectionAssert.AreEqual(
                    RbxWorldPackageSerializer.SerializeWorldThroughDtosForTests(payload),
                    WriteWorldJson(payload));
            }
        }

        /// <summary>
        /// The exact measure equals the written bytes, so the bound's structure and text counts are right; the bound,
        /// which differs only in taking every number at its longest, is never below them.
        /// </summary>
        [Test]
        public void WorldJson_ExactMeasureEqualsTheBytes_AndTheBoundIsNeverBelow()
        {
            foreach (RbxWorldPackagePayload payload in EveryShapeOfPayload())
            {
                long written = WriteWorldJson(payload).LongLength;
                Assert.AreEqual(written, RbxWorldPackageWorldJson.MeasureExact(payload));
                Assert.GreaterOrEqual(RbxWorldPackageWorldJson.MeasureUpperBound(payload), written);
            }
        }

        /// <summary>
        /// A null in a property the reader requires is refused by the direct writer and by the bound as the DTO
        /// serializer refused it, so neither writes a package its reader would refuse.
        /// </summary>
        [Test]
        public void WorldJson_NullRequiredText_IsRefusedLikeTheDtoSerializer()
        {
            RbxWorldPackagePayload nullWorldId = HandBuiltPayload(true);
            nullWorldId.Settings.WorldId = null;
            RbxWorldPackagePayload nullName = HandBuiltPayload(true);
            nullName.Tree.Instances[1].Name = null;
            RbxWorldPackagePayload nullAttributeName = HandBuiltPayload(true);
            nullAttributeName.Tree.Instances[1].Attributes[0].Name = null;

            foreach (RbxWorldPackagePayload payload in new[] { nullWorldId, nullName, nullAttributeName })
            {
                Assert.Throws<JsonSerializationException>(() => RbxWorldPackageSerializer.SerializeWorldThroughDtosForTests(payload));
                Assert.Throws<JsonSerializationException>(() => WriteWorldJson(payload));
                Assert.Throws<JsonSerializationException>(() => RbxWorldPackageWorldJson.MeasureUpperBound(payload));
            }
        }

        /// <summary>
        /// The widths the bound gives a number hold for the longest texts this runtime formats: the extremes, the
        /// subnormals and a sweep of bit patterns.
        /// </summary>
        [Test]
        public void WorldJson_LongestNumberTexts_FitTheBoundWidths()
        {
            float[] floats =
            {
                float.MaxValue, float.MinValue, float.Epsilon, -float.Epsilon, 1.17549435E-38f, -1.17549435E-38f,
                -1.23456791E-20f, 16777216f, -16777217f, 123456792f, -0.000123456791f, 3.40282347E+37f
            };
            int longestFloat = 0;
            foreach (float value in floats)
            {
                longestFloat = Math.Max(longestFloat, JsonConvert.ToString(value).Length);
            }

            double[] doubles =
            {
                double.MaxValue, double.MinValue, double.Epsilon, -double.Epsilon, -2.2250738585072014E-308,
                -1.2345678901234567E-300, -0.00012345678901234567, 9007199254740993d, -123456789012345680d
            };
            int longestDouble = 0;
            foreach (double value in doubles)
            {
                longestDouble = Math.Max(longestDouble, JsonConvert.ToString(value).Length);
            }

            Random random = new(7);
            byte[] bits = new byte[8];
            for (int sample = 0; sample < 200000; sample++)
            {
                random.NextBytes(bits);
                float single = BitConverter.ToSingle(bits, 0);
                if (!float.IsNaN(single) && !float.IsInfinity(single))
                {
                    longestFloat = Math.Max(longestFloat, JsonConvert.ToString(single).Length);
                }

                double wide = BitConverter.ToDouble(bits, 0);
                if (!double.IsNaN(wide) && !double.IsInfinity(wide))
                {
                    longestDouble = Math.Max(longestDouble, JsonConvert.ToString(wide).Length);
                }
            }

            Assert.LessOrEqual(longestFloat, RbxWorldPackageWorldJson.MaximumFloatCharacters);
            Assert.LessOrEqual(longestDouble, RbxWorldPackageWorldJson.MaximumDoubleCharacters);
            Assert.LessOrEqual(Environment.NewLine.Length, RbxWorldPackageWorldJson.MaximumNewLineCharacters);
        }

        // ==================== The proof ====================

        /// <summary>An ordinary world is proven encodable, and the package it encodes to stays within the bound.</summary>
        [Test]
        public void Proof_OrdinaryWorld_IsProven_AndThePackageStaysWithinTheBound()
        {
            RbxWorldPackagePayload payload = WithMods(WorldPackageTestWorlds.CaptureHeavyWorld(256, 4, 2, CapturedAtUtc), 3);

            Assert.IsTrue(RbxWorldPackageSerializer.TryProveEncodable(payload, RbxWorldPackageLimits.Format, out long bound));

            Assert.GreaterOrEqual(bound, RbxWorldPackageSerializer.WritePackage(payload).LongLength);
        }

        /// <summary>
        /// Negative twin: when the bound passes a limit the proof claims nothing, and only the encode decides: here it
        /// fits (the bound was loose), and one byte less it is refused.
        /// </summary>
        [Test]
        public void Proof_BoundPastALimit_IsNotProven_AndTheEncodeDecides()
        {
            RbxWorldPackagePayload payload = WorldPackageTestWorlds.CaptureHeavyWorld(64, 2, 2, CapturedAtUtc);
            int exact = WriteWorldJson(payload).Length;
            RbxWorldPackageLimits justFits = new(4 * 1024 * 1024, exact, 8 * 1024 * 1024, 64);
            RbxWorldPackageLimits oneByteShort = new(4 * 1024 * 1024, exact - 1, 8 * 1024 * 1024, 64);

            Assert.IsFalse(RbxWorldPackageSerializer.TryProveEncodable(payload, justFits, out long _));
            Assert.IsNotNull(RbxWorldPackageSerializer.WritePackage(payload, justFits));
            Assert.IsFalse(RbxWorldPackageSerializer.TryProveEncodable(payload, oneByteShort, out long _));
            Assert.Throws<RbxWorldPackageException>(() => RbxWorldPackageSerializer.WritePackage(payload, oneByteShort));
        }

        /// <summary>A payload the writer refuses on validation makes the proof throw the same refusal.</summary>
        [Test]
        public void Proof_PayloadTheWriterRefuses_ThrowsTheWritersRefusal()
        {
            RbxWorldPackagePayload payload = WithLoadOrderPastTheMaximum(P3Fixtures.CapturePayload("Before"));

            RbxWorldPackageException written = Assert.Throws<RbxWorldPackageException>(
                () => RbxWorldPackageSerializer.WritePackage(payload));
            RbxWorldPackageException proved = Assert.Throws<RbxWorldPackageException>(
                () => RbxWorldPackageSerializer.TryProveEncodable(payload, RbxWorldPackageLimits.Format, out long _));

            Assert.AreEqual(written.Message, proved.Message);
        }

        // ==================== The store and the gate ====================

        /// <summary>
        /// Under write-behind a proven world's autosave is encoded behind the edit: the edit runs and returns while no
        /// byte is encoded and nothing is on disk; once the wait for the next frame ends, the autosave holds exactly the
        /// pre-edit world, and the in-memory snapshot trades its payload for the compact package.
        /// </summary>
        [Test]
        public async Task Store_ProvenWorld_IsEncodedBehindTheEdit_AfterItsFrame()
        {
            string root = NewDirectory();
            UniTaskCompletionSource frame = new();
            FileRbxWorldPackageStore store = new(
                root,
                persistenceSyncAsync: _ => UniTask.FromResult(true),
                utcNow: () => CapturedAtUtc,
                waitBeforeDeferredEncodeAsync: _ => frame.Task);
            RbxWorldPackagePayload before = P3Fixtures.CapturePayload("Before");
            ConfirmedWorldMutationGate gate = new(_ => UniTask.FromResult(before), store, true);
            SnapshotObserver observer = new();
            gate.AddObserver(observer);
            P3RecordingBindings bindings = new();
            LuaCsGameToolExecutor executor = new(new LuaCsSecureEnvironment(), bindings, new NullLuaExecutionObserver(), null, gate);

            LuaTool.LuaResult result = await executor.ExecuteAsync("mutate_world()", CancellationToken.None);

            Assert.IsTrue(result.Success, result.Error);
            Assert.AreEqual("new-tree", bindings.TreeState, "the edit ran before its autosave was encoded");
            Assert.IsTrue(gate.HasPendingBackup);
            Assert.IsFalse(observer.LastBefore.HoldsEncodedPackage, "nothing is encoded before the frame");
            CollectionAssert.IsEmpty(store.ListAutoSaves());

            frame.TrySetResult();
            RbxWorldPackageWriteResult written = await gate.WaitForPendingBackupAsync();

            Assert.IsTrue(written.Success, written.Error);
            IReadOnlyList<RbxAutoSaveInfo> autosaves = store.ListAutoSaves();
            Assert.AreEqual(1, autosaves.Count);
            CollectionAssert.AreEqual(
                RbxWorldPackageSerializer.WritePackage(before),
                File.ReadAllBytes(Path.Combine(root, "Auto", autosaves[0].FileName)));
            Assert.IsTrue(observer.LastBefore.HoldsEncodedPackage, "the snapshot keeps only the compact package");
            Assert.IsNotNull(P3InstantEditLoopEditModeTests.FindFolder(observer.LastBefore.ToPayload(), "Before"));
            Assert.AreEqual(autosaves[0].SizeBytes, gate.LatestBackupMetrics.PackageBytes);
        }

        /// <summary>
        /// Negative twin: a world the writer refuses is still refused before the edit under write-behind; the edit
        /// never runs and nothing is written.
        /// </summary>
        [Test]
        public async Task Store_WorldTheWriterRefuses_IsStillRefusedBeforeTheEdit()
        {
            string root = NewDirectory();
            FileRbxWorldPackageStore store = new(
                root,
                persistenceSyncAsync: _ => UniTask.FromResult(true),
                utcNow: () => CapturedAtUtc,
                waitBeforeDeferredEncodeAsync: _ => throw new InvalidOperationException("no deferred encode expected"));
            RbxWorldPackagePayload refused = WithLoadOrderPastTheMaximum(P3Fixtures.CapturePayload("Before"));
            ConfirmedWorldMutationGate gate = new(_ => UniTask.FromResult(refused), store, true);
            P3RecordingBindings bindings = new();
            LuaCsGameToolExecutor executor = new(new LuaCsSecureEnvironment(), bindings, new NullLuaExecutionObserver(), null, gate);

            LuaTool.LuaResult result = await executor.ExecuteAsync("mutate_world()", CancellationToken.None);

            Assert.IsFalse(result.Success);
            StringAssert.Contains("load order", result.Error);
            Assert.AreEqual("old-tree", bindings.TreeState, "the edit did not run");
            Assert.IsFalse(gate.HasPendingBackup);
            CollectionAssert.IsEmpty(store.ListAutoSaves());
        }

        /// <summary>A store composed without deferral encodes in front of the edit, as before.</summary>
        [Test]
        public async Task Store_DeferralSwitchedOff_EncodesInFront()
        {
            FileRbxWorldPackageStore deferring = new(NewDirectory(), persistenceSyncAsync: _ => UniTask.FromResult(true));
            FileRbxWorldPackageStore encodingInFront = new(
                NewDirectory(), persistenceSyncAsync: _ => UniTask.FromResult(true), defersProvenEncodes: false);
            RbxWorldPackagePayload payload = P3Fixtures.CapturePayload("Front");

            RbxPreparedAutoSave deferred = await deferring.PrepareAutoAsync("execute_lua", payload);
            RbxPreparedAutoSave encoded = await encodingInFront.PrepareAutoAsync("execute_lua", payload);

            Assert.IsTrue(deferred.Success);
            Assert.IsTrue(deferred.IsEncodeDeferred);
            Assert.IsNull(deferred.PackageBytes);
            Assert.Greater(deferred.PackageBytesUpperBound, 0L);
            Assert.IsTrue(encoded.Success);
            Assert.IsFalse(encoded.IsEncodeDeferred);
            CollectionAssert.AreEqual(RbxWorldPackageSerializer.WritePackage(payload), encoded.PackageBytes);
        }

        /// <summary>
        /// A snapshot of a deferred autosave holds the payload until the encode, then only the package; one of an
        /// unencodable autosave keeps the payload.
        /// </summary>
        [Test]
        public void Snapshot_OfADeferredAutosave_TradesThePayloadForThePackageWhenEncoded()
        {
            RbxWorldPackagePayload payload = P3Fixtures.CapturePayload("Kept");
            RbxPreparedAutoSave deferred = RbxPreparedAutoSave.ProvenEncodable("execute_lua", CapturedAtUtc, payload, 1L);
            RbxWorldSnapshot snapshot = RbxWorldSnapshot.Of("execute_lua", CapturedAtUtc, payload, deferred);
            RbxWorldSnapshot unencodable = RbxWorldSnapshot.Of(
                "execute_lua", CapturedAtUtc, payload, RbxPreparedAutoSave.Unencodable("execute_lua", CapturedAtUtc, "too big"));

            Assert.IsFalse(snapshot.HoldsEncodedPackage);
            byte[] package = RbxWorldPackageSerializer.WritePackage(payload);
            deferred.CompleteDeferredEncode(package);

            Assert.IsTrue(snapshot.HoldsEncodedPackage);
            Assert.AreEqual(package.LongLength, snapshot.EncodedSizeBytes);
            Assert.IsFalse(deferred.IsEncodeDeferred);
            Assert.IsNotNull(P3InstantEditLoopEditModeTests.FindFolder(snapshot.ToPayload(), "Kept"));
            Assert.IsFalse(unencodable.HoldsEncodedPackage);
            Assert.AreSame(payload, unencodable.ToPayload());
        }

        // ==================== Digest and reader ====================

        /// <summary>Two captures of one world give one entry digest; a moved Part gives another.</summary>
        [Test]
        public void EntryDigest_EqualForTheSameWorld_DiffersWhenAPartMoves()
        {
            RbxWorldPackagePayload first = WorldPackageTestWorlds.CaptureHeavyWorld(32, 2, 2, CapturedAtUtc);
            RbxWorldPackagePayload second = WorldPackageTestWorlds.CaptureHeavyWorld(32, 2, 2, CapturedAtUtc);
            RbxWorldPackagePayload moved = WorldPackageTestWorlds.CaptureHeavyWorld(32, 2, 2, CapturedAtUtc);
            InstanceId anyPart = default;
            foreach (KeyValuePair<InstanceId, PartProperties> entry in moved.Parts)
            {
                anyPart = entry.Key;
                break;
            }

            Dictionary<InstanceId, PartProperties> movedParts = new(moved.Parts);
            PartProperties shifted = movedParts[anyPart];
            shifted.Position = shifted.Position + new RbxVector3(0f, 1f, 0f);
            movedParts[anyPart] = shifted;
            RbxWorldPackagePayload movedPayload = new(
                moved.CapturedAtUtc, moved.Settings, moved.Tree, movedParts, moved.CameraCFrame, moved.Mods, moved.Diagnostics);

            CollectionAssert.AreEqual(
                RbxWorldPackageSerializer.ComputeEntryDigest(first),
                RbxWorldPackageSerializer.ComputeEntryDigest(second));
            CollectionAssert.AreNotEqual(
                RbxWorldPackageSerializer.ComputeEntryDigest(first),
                RbxWorldPackageSerializer.ComputeEntryDigest(movedPayload));
        }

        /// <summary>
        /// The reader that decodes straight from the entry bytes still refuses text after the JSON and invalid UTF-8,
        /// as the string decode did; the untouched package still reads.
        /// </summary>
        [Test]
        public void Reader_TrailingTextAndInvalidUtf8_AreStillRefused()
        {
            string manifest = WorldPackageTestWorlds.SmallPackageEntryText(RbxWorldPackageSerializer.ManifestEntryName, CapturedAtUtc);
            string world = WorldPackageTestWorlds.SmallPackageEntryText(RbxWorldPackageSerializer.WorldEntryName, CapturedAtUtc);
            UTF8Encoding utf8 = new(false, true);
            byte[] valid = WorldPackageTestWorlds.BuildZip(
                (RbxWorldPackageSerializer.ManifestEntryName, utf8.GetBytes(manifest)),
                (RbxWorldPackageSerializer.WorldEntryName, utf8.GetBytes(world)));
            byte[] trailing = WorldPackageTestWorlds.BuildZip(
                (RbxWorldPackageSerializer.ManifestEntryName, utf8.GetBytes(manifest)),
                (RbxWorldPackageSerializer.WorldEntryName, utf8.GetBytes(world + " {}")));
            byte[] invalidBytes = utf8.GetBytes(world);
            invalidBytes[invalidBytes.Length - 2] = 0xFF;
            byte[] invalid = WorldPackageTestWorlds.BuildZip(
                (RbxWorldPackageSerializer.ManifestEntryName, utf8.GetBytes(manifest)),
                (RbxWorldPackageSerializer.WorldEntryName, invalidBytes));

            Assert.IsNotNull(RbxWorldPackageSerializer.ReadPackage(valid));
            StringAssert.Contains("invalid JSON", Assert.Throws<RbxWorldPackageException>(
                () => RbxWorldPackageSerializer.ReadPackage(trailing)).Message);
            StringAssert.Contains("invalid JSON", Assert.Throws<RbxWorldPackageException>(
                () => RbxWorldPackageSerializer.ReadPackage(invalid)).Message);
        }

        // ==================== Helpers ====================

        /// <summary>
        /// Captured and hand-built payloads that together reach every block of <c>world.json</c> and every class of
        /// character the JSON writer escapes or the package replaces.
        /// </summary>
        private static IEnumerable<RbxWorldPackagePayload> EveryShapeOfPayload()
        {
            yield return WorldPackageTestWorlds.CaptureHeavyWorld(96, 8, 4, CapturedAtUtc);
            yield return P3Fixtures.CapturePayload("Plain", "Second");
            yield return HandBuiltPayload(true);
            yield return HandBuiltPayload(false);
        }

        private static RbxWorldPackagePayload HandBuiltPayload(bool withOptionalState)
        {
            string awkward = "q\"b\\s/t\tn\nr\rf\fb\b c\u0001 d\u007f nel" + (char)0x0085 + " ls" + (char)0x2028 + " ps"
                             + (char)0x2029 + " cyr" + (char)0x0436 + " emoji" + char.ConvertFromUtf32(0x1F600) + " lone" + (char)0xD800 + " end";
            InstanceTreeSnapshot tree = new() { WorldAclVersion = withOptionalState ? 3 : (int?)null };
            tree.Instances.Add(new InstanceSnapshot
            {
                Id = 9007199254740993UL, ParentId = 0UL, ClassName = "DataModel", Name = "Game", Archivable = true,
                AccessScope = InstanceAccessScope.HostProtected, Revision = 1L
            });
            tree.Instances.Add(new InstanceSnapshot
            {
                Id = 9007199254740994UL, ParentId = 9007199254740993UL, ClassName = "Workspace", Name = awkward,
                Archivable = false, OriginTag = "console:" + awkward, OwnerActorId = awkward,
                AccessScope = InstanceAccessScope.SharedWritable, Revision = long.MaxValue,
                Tags = new List<string> { awkward, "", "plain" },
                Attributes = new List<AttributeSnapshot>
                {
                    new() { Name = awkward, Kind = AttributeValueKind.String, StringValue = awkward },
                    new() { Name = "Big", Kind = AttributeValueKind.Number, NumberValue = -1.7976931348623157E+308 },
                    new() { Name = "Tiny", Kind = AttributeValueKind.Number, NumberValue = double.Epsilon },
                    new() { Name = "Flag", Kind = AttributeValueKind.Bool, BoolValue = true },
                    new() { Name = "Pos", Kind = AttributeValueKind.Vector3, StringValue = "1,2.5,-3" }
                },
                Model = new ModelSnapshot
                {
                    PrimaryPartId = 9007199254740995UL, HasStoredWorldPivot = withOptionalState,
                    StoredWorldPivot = withOptionalState ? "1,2,3,1,0,0,0,1,0,0,0,1" : null
                }
            });
            tree.Instances.Add(new InstanceSnapshot
            {
                Id = 9007199254740995UL, ParentId = 9007199254740994UL, ClassName = "Part", Name = "",
                Archivable = true, OwnerModId = withOptionalState ? null : "mod-x", AccessScope = null, Revision = -1L,
                ClickDetector = new ClickDetectorSnapshot { MaxActivationDistance = "1.7976931348623157E+308" },
                Value = new ValueSnapshot { StringValue = withOptionalState ? awkward : null, ObjectTargetId = ulong.MaxValue },
                Humanoid = new HumanoidSnapshot
                {
                    Health = "100", MaxHealth = "1E+300", WalkSpeed = "16.000000000000004", JumpPower = "-0.0001234",
                    JumpHeight = "7.2", UseJumpPower = withOptionalState, DisplayName = withOptionalState ? awkward : null
                },
                MaterialVariant = new MaterialVariantSnapshot
                {
                    BaseMaterial = "Slate", BaseMaterialValue = 800, ColorMap = awkward, NormalMap = null,
                    RoughnessMap = "", MetalnessMap = "rbxassetid://1", StudsPerTile = "3.4028235E+38"
                }
            });
            tree.Instances.Add(new InstanceSnapshot
            {
                Id = 9007199254740996UL, ParentId = 9007199254740995UL, ClassName = "PointLight", Name = "Torch",
                Archivable = true, Revision = 4L
            });
            Dictionary<InstanceId, RbxWorldPackagePointLight> lights = new()
            {
                [new InstanceId(9007199254740996UL)] = new RbxWorldPackagePointLight(
                    3.4028235E+38f, new RbxColor3(1f, 0.333333343f, -0f), withOptionalState, !withOptionalState, 60f)
            };
            RbxLightingDurableState? lighting = withOptionalState
                ? new RbxLightingDurableState(23.9999981f, -89.9999924f, 1.17549435E-38f,
                    new RbxColor3(0.1f, 0.2f, 0.3f), new RbxColor3(1f, 1f, 1f))
                : (RbxLightingDurableState?)null;
            Dictionary<InstanceId, PartProperties> parts = new();
            PartProperties part = PartProperties.CreateDefault();
            part.CFrame = new RbxCFrame(
                -1.17549435E-38f, 3.40282347E+38f, float.Epsilon, -0.70710677f, 0.70710677f, 0f,
                0.5f, -0.5f, 0.70710677f, 1f, -1f, 16777216f);
            part.Size = new RbxVector3(1.23456791E-20f, 123456792f, -0f);
            part.Color = new RbxColor3(0.1f, 0.2f, 0.3f);
            part.ColorWasExplicitlySet = withOptionalState;
            part.MaterialVariant = withOptionalState ? awkward : "";
            part.Transparency = 0.333333343f;
            parts.Add(new InstanceId(9007199254740995UL), part);
            RbxWorldSettings settings = new()
            {
                WorldId = withOptionalState ? awkward : "", MetersPerStud = 0.28f, GravityStudsPerSecondSquared = 196.2d,
                SignalBehavior = RbxWorldSettings.DeferredSignalBehavior
            };
            RbxCFrame? camera = withOptionalState
                ? new RbxCFrame(10f, 5f, -4f, 1f, 0f, 0f, 0f, 1f, 0f, 0f, 0f, 1f)
                : (RbxCFrame?)null;
            return new RbxWorldPackagePayload(
                CapturedAtUtc, settings, tree, parts, camera, Array.Empty<RbxWorldModSource>(), null,
                new RbxWorldPackageOptionalState
                {
                    PointLights = lights,
                    Lighting = lighting,
                    StarterPlayer = new RbxWorldPackageStarterPlayer(
                        RbxDevComputerMovementMode.ClickToMove, RbxCameraMode.LockFirstPerson,
                        withOptionalState ? 1.7976931348623157E+308 : 0.5d, withOptionalState ? 4.9406564584124654E-324 : 0d)
                });
        }

        private static RbxWorldPackagePayload WithMods(RbxWorldPackagePayload payload, int count)
        {
            List<RbxWorldModSource> mods = new();
            for (int index = 0; index < count; index++)
            {
                string id = "mod-" + index.ToString(CultureInfo.InvariantCulture);
                mods.Add(new RbxWorldModSource(
                    new LuaModManifest { Id = id, Name = id, Active = true, LoadOrder = index + 1 },
                    "-- @coreai\nlocal n = " + index.ToString(CultureInfo.InvariantCulture) + "\nreturn n"));
            }

            return new RbxWorldPackagePayload(
                payload.CapturedAtUtc, payload.Settings, payload.Tree, payload.Parts, payload.CameraCFrame, mods,
                payload.Diagnostics);
        }

        private static RbxWorldPackagePayload WithLoadOrderPastTheMaximum(RbxWorldPackagePayload payload)
        {
            RbxWorldModSource mod = new(
                new LuaModManifest { Id = "refused-mod", Active = true, LoadOrder = LuaModManifest.MaximumLoadOrder + 1L },
                "return 1");
            return new RbxWorldPackagePayload(
                payload.CapturedAtUtc, payload.Settings, payload.Tree, payload.Parts, payload.CameraCFrame,
                new[] { mod }, payload.Diagnostics);
        }

        private static byte[] WriteWorldJson(RbxWorldPackagePayload payload)
        {
            using MemoryStream stream = RbxWorldPackageWorldJson.Write(payload, 0L);
            return stream.ToArray();
        }

        private string NewDirectory()
        {
            string path = WorldPackageTestWorlds.NewTemporaryDirectory();
            _directories.Add(path);
            return path;
        }

        private sealed class SnapshotObserver : IRbxWorldMutationObserver
        {
            public RbxWorldSnapshot LastBefore { get; private set; }

            public void OnMutationStarting(RbxWorldMutationInfo mutation)
            {
            }

            public void OnMutationFinished(RbxWorldMutationInfo mutation, RbxWorldSnapshot before, bool completed)
            {
                LastBefore = before;
            }
        }
    }
}
