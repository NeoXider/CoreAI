using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Ai;
using CoreAI.Ai.LuaCs;
using CoreAI.Authority;
using CoreAI.Mods.Rbx.Instances;
using CoreAI.Mods.Rbx.Instances.Terrain;
using CoreAI.Mods.WorldPackages;
using CoreAI.Sandbox.LuaCs;
using CoreAI.Tests.EditMode.RbxApi.BlockWorld;
using Cysharp.Threading.Tasks;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode.RbxApi.Acceptance
{
    /// <summary>
    /// The world package encode in units of bounded work (<c>RbxWorldPackageSerializer.IncrementalEncoder</c>): cut
    /// into its smallest steps it writes the bytes and the entry digest of the one-block methods for every shape of
    /// payload, a step ends at the first unit past its budget, and every refusal is the writer's, in its order.
    /// </summary>
    [TestFixture]
    public sealed class WorldPackageChunkedEncodeEditModeTests
    {
        private static readonly DateTime CapturedAtUtc = new(2026, 11, 12, 9, 0, 0, DateTimeKind.Utc);

        private static readonly string[] BlockWorldMods =
        {
            "blockworld_autopilot", "blockworld_build", "blockworld_daynight", "blockworld_falling", "blockworld_hud",
            "blockworld_survival", "blockworld_torches", "blockworld_villagers", "blockworld_world"
        };

        // ==================== Bytes ====================

        /// <summary>
        /// One unit per step and all units in one step, under the format's limits and the 7.56.0 entry limit, for every
        /// payload the writer tests build (a Block World-like one with Terrain, the HUD and the nine bundled mods
        /// included): the bytes of <c>WritePackage</c>; the entries of the one-block writer before the encoder, and its
        /// bytes too where deflate ignores how its input is split (Mono, which the editor and Mono players run); and a
        /// payload the one-block writer refuses is refused with its exception.
        /// </summary>
        [Test]
        public void Encoder_InUnitsOrInOneStep_WritesTheOneBlockWritersPackage_ForEveryShapeOfPayload()
        {
            int written = 0;
            int refused = 0;
            foreach (RbxWorldPackagePayload payload in EveryShapeOfPayload())
            {
                foreach (RbxWorldPackageLimits limits in new[] { RbxWorldPackageLimits.Format, RbxWorldPackageLimits.Version1Baseline })
                {
                    byte[] oneBlock;
                    try
                    {
                        oneBlock = RbxWorldPackageSerializer.WritePackageInOneBlockForTests(payload, limits);
                    }
                    catch (Exception expected)
                    {
                        AssertSameRefusal(expected, () => EncodeOneUnitAtATime(payload, limits, out int _));
                        AssertSameRefusal(expected, () => RbxWorldPackageSerializer.WritePackage(payload, limits));
                        refused++;
                        continue;
                    }

                    byte[] units = EncodeOneUnitAtATime(payload, limits, out int _);

                    CollectionAssert.AreEqual(units, EncodeInOneStep(payload, limits));
                    CollectionAssert.AreEqual(units, RbxWorldPackageSerializer.WritePackage(payload, limits));
                    AssertSamePackage(oneBlock, units);
                    written++;
                }
            }

            Assert.GreaterOrEqual(written, 8, "precondition: most shapes are packages");
            Assert.GreaterOrEqual(refused, 1, "precondition: a shape the writer refuses is in the set");
        }

        /// <summary>
        /// Where the editor and the Mono players run, deflate gives the same stream however its input is split, so a
        /// package encoded a slice at a time is byte for byte the one written in one block before the encoder. CoreCLR
        /// (the engine-free suite) does not promise it; there only the entries are compared.
        /// </summary>
        [Test]
        public void Deflate_OnMono_GivesTheSameStreamHoweverItsInputIsSplit()
        {
            if (!IsMono)
            {
                Assert.Ignore("Not Mono: this runtime's deflate " + (DeflateIgnoresHowItsInputIsSplit()
                    ? "happens to ignore the split too."
                    : "depends on the split, so the engine-free suite compares entries, not bytes."));
            }

            Assert.IsTrue(DeflateIgnoresHowItsInputIsSplit());
        }

        /// <summary>
        /// The Block World-like payload is the one that tests the slicing: its Terrain entry and its largest mod source
        /// each reach deflate in several slices, and its world.json in several runs of instances.
        /// </summary>
        [Test]
        public void BlockWorldLikePayload_HasEntriesThatTakeSeveralSlices()
        {
            RbxWorldPackagePayload payload = BlockWorldLikePayload();
            byte[] terrain = TerrainVoxelCodec.Encode(StoreOf(payload.Terrain));
            int largestSource = 0;
            foreach (RbxWorldModSource mod in payload.Mods)
            {
                largestSource = Math.Max(largestSource, mod.Source.Length);
            }

            Assert.Greater(payload.Terrain.ChunkCount, 32);
            Assert.Greater(terrain.Length, 3 * RbxWorldPackageSerializer.IncrementalEncoder.DeflateSliceBytes);
            Assert.Greater(largestSource, 2 * RbxWorldPackageSerializer.IncrementalEncoder.DeflateSliceBytes);
            Assert.Greater(payload.Tree.Instances.Count, 2 * RbxWorldPackageSerializer.IncrementalEncoder.WorldJsonInstancesPerUnit);
            Assert.Greater(payload.Gui.Count, 0, "the HUD is in the package");
        }

        /// <summary>
        /// Negative twin of the slicing: the same payload with one Terrain cell changed encodes to other bytes, so the
        /// equality above is not met by an encoder that ignores what it slices.
        /// </summary>
        [Test]
        public void Encoder_ADifferentCell_GivesOtherBytes()
        {
            RbxWorldPackagePayload payload = BlockWorldLikePayload();
            TerrainChunkStore store = StoreOf(payload.Terrain);
            TerrainMaterials.TryGetSlot("Brick", out byte brick);
            store.SetCell(3, 2, 5, brick, TerrainOccupancy.Full);
            RbxWorldPackageOptionalState changed = payload.OptionalState;
            changed.Terrain = store.CaptureSnapshot();
            RbxWorldPackagePayload other = new(
                payload.CapturedAtUtc, payload.Settings, payload.Tree, payload.Parts, payload.CameraCFrame, payload.Mods,
                payload.Diagnostics, changed);

            CollectionAssert.AreNotEqual(
                RbxWorldPackageSerializer.WritePackage(payload),
                EncodeOneUnitAtATime(other, RbxWorldPackageLimits.Format, out int _));
            CollectionAssert.AreEqual(
                RbxWorldPackageSerializer.WritePackage(other),
                EncodeOneUnitAtATime(other, RbxWorldPackageLimits.Format, out int _));
        }

        /// <summary>A payload the proof validated encodes without the validation unit to the same bytes, one unit fewer.</summary>
        [Test]
        public void Encoder_ProvenPayloadWithoutValidation_WritesTheSameBytes_InOneUnitLess()
        {
            RbxWorldPackagePayload payload = BlockWorldLikePayload();
            Assert.IsTrue(RbxWorldPackageSerializer.TryProveEncodable(payload, RbxWorldPackageLimits.Format, out long _));
            byte[] validated = EncodeOneUnitAtATime(payload, RbxWorldPackageLimits.Format, out int validatedUnits);

            using RbxWorldPackageSerializer.IncrementalEncoder proven =
                RbxWorldPackageSerializer.IncrementalEncoder.ForPackage(payload, RbxWorldPackageLimits.Format, false);
            while (!proven.Step(Always))
            {
            }

            CollectionAssert.AreEqual(validated, proven.Result);
            Assert.AreEqual(validatedUnits - 1, proven.UnitsDone);
        }

        // ==================== Digest ====================

        /// <summary>
        /// The digest of the entries, one unit per step, is <c>ComputeEntryDigest</c>'s, with the payload's own capture
        /// time and with the startup record's (<see cref="DateTime.MinValue"/>).
        /// </summary>
        [Test]
        public void Encoder_EntryDigestInUnits_IsTheOneBlockDigest()
        {
            foreach (RbxWorldPackagePayload payload in EveryShapeOfPayload())
            {
                byte[] own;
                try
                {
                    own = RbxWorldPackageSerializer.ComputeEntryDigestInOneBlockForTests(payload);
                }
                catch (Exception expected)
                {
                    AssertSameRefusal(expected, () => DigestOneUnitAtATime(payload, null));
                    AssertSameRefusal(expected, () => RbxWorldPackageSerializer.ComputeEntryDigest(payload));
                    continue;
                }

                CollectionAssert.AreEqual(own, DigestOneUnitAtATime(payload, null));
                CollectionAssert.AreEqual(own, RbxWorldPackageSerializer.ComputeEntryDigest(payload));
                byte[] startup = RbxWorldPackageSerializer.ComputeEntryDigestInOneBlockForTests(
                    payload.WithCapturedAtUtc(DateTime.MinValue));
                CollectionAssert.AreEqual(startup, DigestOneUnitAtATime(payload, DateTime.MinValue));
                CollectionAssert.AreNotEqual(own, startup, "the manifest's capture time is in the digest");
            }
        }

        /// <summary>
        /// A digest encode that goes on to its package reuses its entries and writes the bytes of
        /// <c>WritePackage</c> of the payload with its real capture time: the startup record's digest and package from
        /// one encode.
        /// </summary>
        [Test]
        public void Encoder_DigestThenPackage_WritesTheBytesOfWritePackage()
        {
            foreach (RbxWorldPackagePayload payload in EveryShapeOfPayload())
            {
                if (!IsWritable(payload))
                {
                    continue;
                }

                using RbxWorldPackageSerializer.IncrementalEncoder encoder =
                    RbxWorldPackageSerializer.IncrementalEncoder.ForEntryDigest(payload, DateTime.MinValue);
                while (!encoder.Step(Always))
                {
                }

                Assert.IsTrue(encoder.IsDigestReady);
                Assert.IsFalse(encoder.IsComplete);
                encoder.ContinueWithPackage(RbxWorldPackageLimits.Format);
                while (!encoder.Step(Always))
                {
                }

                CollectionAssert.AreEqual(RbxWorldPackageSerializer.WritePackage(payload), encoder.Result);
                CollectionAssert.AreEqual(
                    RbxWorldPackageSerializer.ComputeEntryDigestInOneBlockForTests(payload.WithCapturedAtUtc(DateTime.MinValue)),
                    encoder.EntryDigest);
            }
        }

        /// <summary>Negative twin: a package encode has no digest to continue from, and a digest encode does not continue twice.</summary>
        [Test]
        public void Encoder_OnlyAFinishedDigestEncodeContinues()
        {
            RbxWorldPackagePayload payload = P3Fixtures.CapturePayload("Digest");
            using RbxWorldPackageSerializer.IncrementalEncoder package =
                RbxWorldPackageSerializer.IncrementalEncoder.ForPackage(payload, RbxWorldPackageLimits.Format);
            using RbxWorldPackageSerializer.IncrementalEncoder digest =
                RbxWorldPackageSerializer.IncrementalEncoder.ForEntryDigest(payload);

            Assert.Throws<InvalidOperationException>(() => package.ContinueWithPackage(RbxWorldPackageLimits.Format));
            Assert.Throws<InvalidOperationException>(() => digest.ContinueWithPackage(RbxWorldPackageLimits.Format));
            digest.RunToEnd();
            digest.ContinueWithPackage(RbxWorldPackageLimits.Format);
            Assert.Throws<InvalidOperationException>(() => digest.ContinueWithPackage(RbxWorldPackageLimits.Format));
        }

        // ==================== Budget ====================

        /// <summary>
        /// A step runs units until its budget says stop and ends at that unit: with a budget that is spent after three
        /// units every step but the last runs exactly three, and a spent budget still lets one unit run.
        /// </summary>
        [Test]
        public void Encoder_AStepEndsAtTheFirstUnitPastItsBudget()
        {
            RbxWorldPackagePayload payload = BlockWorldLikePayload();
            EncodeOneUnitAtATime(payload, RbxWorldPackageLimits.Format, out int units);
            using RbxWorldPackageSerializer.IncrementalEncoder encoder =
                RbxWorldPackageSerializer.IncrementalEncoder.ForPackage(payload, RbxWorldPackageLimits.Format);
            List<int> perStep = new();
            bool done = false;
            while (!done)
            {
                int before = encoder.UnitsDone;
                int checks = 0;
                done = encoder.Step(() => ++checks >= 3);
                perStep.Add(encoder.UnitsDone - before);
            }

            Assert.AreEqual(units, encoder.UnitsDone);
            Assert.AreEqual((units + 2) / 3, perStep.Count);
            for (int index = 0; index < perStep.Count - 1; index++)
            {
                Assert.AreEqual(3, perStep[index], "step " + index);
            }

            Assert.That(perStep[perStep.Count - 1], Is.InRange(1, 3));
            Assert.Greater(units, payload.Terrain.ChunkCount + payload.Mods.Count, "a Terrain chunk and a mod are units of their own");
        }

        /// <summary>
        /// With a clock that a unit advances by its cost, a step never spends more than the budget plus one unit: the
        /// budget is checked after every unit.
        /// </summary>
        [Test]
        public void Encoder_WithAFakeClock_AStepStaysWithinTheBudgetPlusOneUnit()
        {
            RbxWorldPackagePayload payload = BlockWorldLikePayload();
            using RbxWorldPackageSerializer.IncrementalEncoder encoder =
                RbxWorldPackageSerializer.IncrementalEncoder.ForPackage(payload, RbxWorldPackageLimits.Format);
            const int budget = 10;
            const int unitCost = 4;
            int steps = 0;
            bool done = false;
            while (!done)
            {
                int stepStart = encoder.UnitsDone;
                done = encoder.Step(() => (encoder.UnitsDone - stepStart) * unitCost >= budget);
                int spent = (encoder.UnitsDone - stepStart) * unitCost;
                Assert.LessOrEqual(spent, budget + unitCost, "step " + steps);
                steps++;
            }

            CollectionAssert.AreEqual(RbxWorldPackageSerializer.WritePackage(payload), encoder.Result);
        }

        // ==================== Refusals ====================

        /// <summary>
        /// Every limit the writer enforces refuses the payload with the writer's message, in units and after a digest:
        /// the manifest, world.json, a mod source, the Terrain entry, the expanded total and the package.
        /// </summary>
        [Test]
        public void Encoder_EveryLimit_RefusesWithTheWritersMessage()
        {
            RbxWorldPackagePayload payload = BlockWorldLikePayload();
            byte[] package = RbxWorldPackageSerializer.WritePackage(payload);
            SortedSet<long> sizes = new();
            long expanded = 0L;
            foreach (System.IO.Compression.ZipArchiveEntry entry in Entries(package))
            {
                expanded += entry.Length;
                sizes.Add(entry.Length);
            }

            int big = RbxWorldPackageSerializer.MaximumPackageBytes;
            List<RbxWorldPackageLimits> refusing = new();
            foreach (long size in sizes)
            {
                if (size > 0L)
                {
                    refusing.Add(new RbxWorldPackageLimits(big, (int)size - 1, (int)expanded, 64));
                }
            }

            refusing.Add(new RbxWorldPackageLimits(big, (int)sizes.Max, (int)expanded - 1, 64));
            int smallerPackage = Math.Min(
                package.Length,
                RbxWorldPackageSerializer.WritePackageInOneBlockForTests(payload, RbxWorldPackageLimits.Format).Length);
            refusing.Add(new RbxWorldPackageLimits(smallerPackage - 1, (int)sizes.Max, (int)expanded, 64));
            HashSet<string> messages = new(StringComparer.Ordinal);
            foreach (RbxWorldPackageLimits limits in refusing)
            {
                string written = Assert.Throws<RbxWorldPackageException>(
                    () => RbxWorldPackageSerializer.WritePackage(payload, limits)).Message;
                string oneBlock = Assert.Throws<RbxWorldPackageException>(
                    () => RbxWorldPackageSerializer.WritePackageInOneBlockForTests(payload, limits)).Message;
                messages.Add(written);

                Assert.AreEqual(written, Assert.Throws<RbxWorldPackageException>(
                    () => EncodeOneUnitAtATime(payload, limits, out int _)).Message);
                Assert.AreEqual(written, Assert.Throws<RbxWorldPackageException>(
                    () => DigestThenPackage(payload, limits)).Message);
                // WHY the package-size refusal may differ: it names the compressed size, which on a runtime whose
                // deflate depends on the input split is not the one-block writer's.
                if (DeflateIgnoresHowItsInputIsSplit() || !written.StartsWith("World package is ", StringComparison.Ordinal))
                {
                    Assert.AreEqual(oneBlock, written);
                }
                else
                {
                    StringAssert.StartsWith("World package is ", oneBlock);
                }
            }

            foreach (string refused in new[]
                     {
                         RbxWorldPackageSerializer.ManifestEntryName, RbxWorldPackageSerializer.WorldEntryName, "main.lua",
                         RbxWorldPackageSerializer.TerrainEntryName, "expands to", "World package is"
                     })
            {
                Assert.IsTrue(AnyContains(messages, refused), "a limit refuses at " + refused + ": " + string.Join(" | ", messages));
            }

            CollectionAssert.AreEqual(package, DigestThenPackage(payload, RbxWorldPackageLimits.Format), "the twin that fits");
        }

        /// <summary>A payload the writer refuses on validation is refused by the first unit with the same message.</summary>
        [Test]
        public void Encoder_InvalidPayload_IsRefusedByItsFirstUnit_WithTheWritersMessage()
        {
            RbxWorldPackagePayload payload = P3Fixtures.CapturePayload("Before");
            RbxWorldPackagePayload refused = new(
                payload.CapturedAtUtc, payload.Settings, payload.Tree, payload.Parts, payload.CameraCFrame,
                new[]
                {
                    new RbxWorldModSource(
                        new LuaModManifest { Id = "refused-mod", Active = true, LoadOrder = LuaModManifest.MaximumLoadOrder + 1L },
                        "return 1")
                },
                payload.Diagnostics);
            string written = Assert.Throws<RbxWorldPackageException>(() => RbxWorldPackageSerializer.WritePackage(refused)).Message;
            using RbxWorldPackageSerializer.IncrementalEncoder encoder =
                RbxWorldPackageSerializer.IncrementalEncoder.ForPackage(refused, RbxWorldPackageLimits.Format);

            Assert.AreEqual(written, Assert.Throws<RbxWorldPackageException>(() => encoder.Step(Always)).Message);
            Assert.AreEqual(0, encoder.UnitsDone);
            Assert.Throws<InvalidOperationException>(() => encoder.Step(Always), "a refused encode does not go on");
        }

        // ==================== Frames that come slowly or fast ====================

        /// <summary>
        /// At one frame a second (a hidden browser tab) a Block World-sized encode settles in two slices, one wait: the
        /// first slice keeps the 4 ms budget (no gap is known yet), and the gap of a second before the next finishes the
        /// rest; a deferred encode, which measures the gap across its first wait, settles in its first slice.
        /// </summary>
        [Test]
        public async Task Pacing_AtOneFrameASecond_ABlockWorldSizedEncodeSettlesInTwoSlices()
        {
            RbxWorldPackagePayload payload = BlockWorldLikePayload();
            Assert.Greater(UnitsOf(payload, true), 100, "precondition: far more units than one 4 ms slice runs");
            byte[] expected = RbxWorldPackageSerializer.WritePackage(payload);

            FakeFrames written = new(TimeSpan.FromSeconds(1d));
            using RbxWorldPackageSerializer.IncrementalEncoder encoder =
                RbxWorldPackageSerializer.IncrementalEncoder.ForPackage(payload, RbxWorldPackageLimits.Format);
            await Within(RbxWorldPackageEncodeFrames.RunAsync(
                encoder, RbxWorldPackageEncodeFrames.DefaultFrameBudget, written.WaitAsync, CancellationToken.None,
                clock: written.Clock));

            Assert.AreEqual(1, written.Waits, "two slices");
            CollectionAssert.AreEqual(expected, encoder.Result);

            FakeFrames deferred = new(TimeSpan.FromSeconds(1d));
            using RbxWorldPackageSerializer.IncrementalEncoder behind =
                RbxWorldPackageSerializer.IncrementalEncoder.ForPackage(payload, RbxWorldPackageLimits.Format, false);
            await Within(RbxWorldPackageEncodeFrames.RunAsync(
                behind, RbxWorldPackageEncodeFrames.DefaultFrameBudget, deferred.WaitAsync, CancellationToken.None,
                true, null, deferred.Clock));

            Assert.AreEqual(1, deferred.Waits, "the wait before the first slice, then one slice");
            CollectionAssert.AreEqual(expected, behind.Result);
        }

        /// <summary>
        /// At 60 frames a second no slice runs past its budget by more than one unit, the budget being the store's 4 ms
        /// or a third of the rest of the frame (12.7 ms here), and the encode takes many frames; negative twin of the
        /// slow tab. Just under the finishing gap (a tab at about 4 fps) slices grow to a third of the gap but still stop.
        /// </summary>
        [Test]
        public async Task Pacing_At60Fps_NoSliceRunsPastItsBudgetByMoreThanOneUnit()
        {
            RbxWorldPackagePayload payload = BlockWorldLikePayload();
            byte[] expected = RbxWorldPackageSerializer.WritePackage(payload);
            foreach (double gapMs in new[] { 12.7d, 240d })
            {
                FakeFrames frames = new(TimeSpan.FromMilliseconds(gapMs));
                using RbxWorldPackageSerializer.IncrementalEncoder encoder =
                    RbxWorldPackageSerializer.IncrementalEncoder.ForPackage(payload, RbxWorldPackageLimits.Format);
                await Within(RbxWorldPackageEncodeFrames.RunAsync(
                    encoder, RbxWorldPackageEncodeFrames.DefaultFrameBudget, frames.WaitAsync, CancellationToken.None,
                    clock: frames.Clock));

                double budgetMs = Math.Max(4d, (gapMs + FakeFrames.ReadMs) / RbxWorldPackageEncodeFrames.GapShareDivisor);
                TestContext.WriteLine("gap " + gapMs + " ms: " + (frames.Waits + 1) + " slices, longest "
                                      + frames.LongestSliceMs.ToString("0.0") + " ms of a " + budgetMs.ToString("0.0")
                                      + " ms budget");
                Assert.Greater(frames.Waits, gapMs < 100d ? 2 : 0, "the encode is spread over frames");
                Assert.LessOrEqual(frames.LongestSliceMs, budgetMs + 3d * FakeFrames.ReadMs, "budget plus one unit");
                CollectionAssert.AreEqual(expected, encoder.Result);
            }
        }

        /// <summary>
        /// A settle arriving while a deferred encode waits for its next frame (a frame that, here, never comes) ends the
        /// wait and finishes the encode in that one slice, before the next mutation runs, with the bytes of
        /// WritePackage; the next backup behind its own mutation is encoded when its frame comes.
        /// </summary>
        [Test]
        public async Task Store_ASettleWhileTheEncodeWaitsForItsFrame_FinishesItInOneSlice()
        {
            string root = NewDirectory();
            UniTaskCompletionSource frame = new();
            int waits = 0;
            FileRbxWorldPackageStore store = new(
                root,
                persistenceSyncAsync: _ => UniTask.FromResult(true),
                utcNow: () => CapturedAtUtc,
                waitBeforeDeferredEncodeAsync: _ =>
                {
                    waits++;
                    return frame.Task;
                },
                encodeFrameBudget: TimeSpan.Zero);
            RbxWorldPackagePayload first = P3Fixtures.CapturePayload("First", "Settled");
            RbxWorldPackagePayload second = P3Fixtures.CapturePayload("Second");
            Assert.Greater(UnitsOf(first, false), 4, "precondition: without the request the encode takes several frames");
            int captures = 0;
            ConfirmedWorldMutationGate gate = new(_ => UniTask.FromResult(captures++ == 0 ? first : second), store, true);
            P3RecordingBindings bindings = new();
            LuaCsGameToolExecutor executor = new(new LuaCsSecureEnvironment(), bindings, new NullLuaExecutionObserver(), null, gate);

            LuaTool.LuaResult mutated = await Within(executor.ExecuteAsync("mutate_world()", CancellationToken.None));
            Assert.IsTrue(mutated.Success, mutated.Error);
            Assert.IsTrue(gate.HasPendingBackup);
            Assert.AreEqual(1, waits, "the first backup waits for a frame that does not come");
            CollectionAssert.IsEmpty(store.ListAutoSaves());

            LuaTool.LuaResult next = await Within(executor.ExecuteAsync("mutate_world()", CancellationToken.None));

            Assert.IsTrue(next.Success, next.Error);
            Assert.AreEqual(2, waits, "the first encode ran to its end in one slice; the second waits for its frame");
            IReadOnlyList<RbxAutoSaveInfo> settled = store.ListAutoSaves();
            Assert.AreEqual(1, settled.Count, "the first backup was written before the second mutation ran");
            CollectionAssert.AreEqual(
                RbxWorldPackageSerializer.WritePackage(first),
                File.ReadAllBytes(Path.Combine(root, "Auto", settled[0].FileName)));

            frame.TrySetResult();
            RbxWorldPackageWriteResult behind = await Within(gate.WaitForPendingBackupAsync());
            Assert.IsTrue(behind.Success, behind.Error);
            Assert.AreEqual(2, store.ListAutoSaves().Count);
        }

        /// <summary>
        /// The kept world's record at one frame a second: the digest settles in two slices and its package goes on at
        /// once in the second, so the record waits one frame; at 60 frames a second the same record is spread over
        /// many frames.
        /// </summary>
        [Test]
        public async Task KeptWorldRecord_AtOneFrameASecond_WaitsOneFrame()
        {
            foreach (double gapMs in new[] { 1000d, 12.7d })
            {
                string root = NewDirectory();
                FakeFrames frames = new(TimeSpan.FromMilliseconds(gapMs));
                FileRbxWorldPackageStore store = new(
                    root,
                    persistenceSyncAsync: _ => UniTask.FromResult(true),
                    utcNow: () => CapturedAtUtc,
                    waitBeforeDeferredEncodeAsync: frames.WaitAsync)
                {
                    EncodeClock = frames.Clock
                };
                using P3HeadlessSession session = new(store);
                session.Controller.KeepWorldBetweenStarts(null);
                session.Controller.AdoptDefaultWorldIfKept();
                ActorContext actor = new LocalActorIdentityProvider("slow-actor").GetActorContext(BuiltInAgentRoleIds.Programmer);
                LuaTool.LuaResult built = await Within(session.ExecuteAsAsync(actor,
                    "for i = 1, 1000 do local f = Instance.new('Folder') f.Name = 'Slow' .. i f.Parent = workspace end return true"));
                Assert.IsTrue(built.Success, built.Error);
                await Within(session.Gate.WaitForPendingBackupAsync());
                frames.TakeWaits();

                string saved = await Within(session.Controller.SaveKeptWorldNowAsync());

                StringAssert.StartsWith("Saved ", saved);
                int waits = frames.TakeWaits();
                TestContext.WriteLine("gap " + gapMs + " ms: the record waited " + waits + " frames");
                if (gapMs >= 250d)
                {
                    Assert.AreEqual(1, waits);
                }
                else
                {
                    Assert.Greater(waits, 2);
                }
            }
        }

        // ==================== The store ====================

        /// <summary>
        /// A deferred backup is encoded after its mutation's frame and then one unit a frame under a zero budget: the
        /// commit waits once before the encode and once between every two units of the proven encode (no validation
        /// unit), the store's transaction is free in every wait, and the autosave holds the bytes of WritePackage.
        /// </summary>
        [Test]
        public async Task Store_DeferredBackup_IsEncodedAFrameAtATime_AndHoldsTheBytesOfWritePackage()
        {
            string root = NewDirectory();
            FrameCounter frames = new(root);
            FileRbxWorldPackageStore store = new(
                root,
                persistenceSyncAsync: _ => UniTask.FromResult(true),
                utcNow: () => CapturedAtUtc,
                waitBeforeDeferredEncodeAsync: frames.WaitAsync,
                encodeFrameBudget: TimeSpan.Zero);
            RbxWorldPackagePayload payload = StoreSizedPayload();

            RbxPreparedAutoSave prepared = await Within(store.PrepareAutoAsync("execute_lua", payload));
            Assert.IsTrue(prepared.IsEncodeDeferred);
            Assert.AreEqual(0, frames.Waits, "nothing is encoded in front of the mutation");
            RbxWorldPackageWriteResult written = await Within(store.CommitAutoAsync(prepared));

            Assert.IsTrue(written.Success, written.Error);
            Assert.AreEqual(UnitsOf(payload, false), frames.Waits, "one wait before the encode, then one between every two units");
            Assert.AreEqual(0, frames.HeldTransactions, "no frame of the encode holds the store's transaction");
            CollectionAssert.AreEqual(RbxWorldPackageSerializer.WritePackage(payload), File.ReadAllBytes(written.Path));
            CollectionAssert.AreEqual(RbxWorldPackageSerializer.WritePackage(payload), prepared.PackageBytes);
        }

        /// <summary>
        /// Every other write a store makes is encoded a frame at a time too, before it takes the store's transaction:
        /// a manual save, a durable-first autosave and a startup selection each wait between the units of their
        /// encode, and each installs the bytes of WritePackage.
        /// </summary>
        [Test]
        public async Task Store_ManualAutoAndStartupWrites_AreEncodedAFrameAtATime_OutsideTheTransaction()
        {
            string root = NewDirectory();
            FrameCounter frames = new(root);
            FileRbxWorldPackageStore store = new(
                root,
                persistenceSyncAsync: _ => UniTask.FromResult(true),
                utcNow: () => CapturedAtUtc,
                waitBeforeDeferredEncodeAsync: frames.WaitAsync,
                encodeFrameBudget: TimeSpan.Zero);
            RbxWorldPackagePayload payload = StoreSizedPayload();
            byte[] expected = RbxWorldPackageSerializer.WritePackage(payload);
            int units = UnitsOf(payload, true);

            RbxWorldPackageWriteResult manual = await Within(store.CreateManualAsync("slot-a", payload));
            Assert.AreEqual(units - 1, frames.TakeWaits(), "manual save");
            RbxWorldPackageWriteResult auto = await Within(store.CreateAutoAsync("manage_mods-load", payload));
            Assert.AreEqual(units - 1, frames.TakeWaits(), "durable-first autosave");
            RbxWorldPackageWriteResult startup = await Within(store.SelectStartupAsync(payload, "manual", "slot-a"));
            Assert.AreEqual(units - 1, frames.TakeWaits(), "startup selection");

            foreach (RbxWorldPackageWriteResult written in new[] { manual, auto, startup })
            {
                Assert.IsTrue(written.Success, written.Error);
                CollectionAssert.AreEqual(expected, File.ReadAllBytes(written.Path), written.Path);
            }

            Assert.AreEqual(0, frames.HeldTransactions, "no frame of an encode holds the store's transaction");
        }

        /// <summary>
        /// Negative twin: a store write whose encode is refused returns the encode's typed failure where it used to,
        /// writes nothing, and leaves no metadata of a startup entry behind.
        /// </summary>
        [Test]
        public async Task Store_ARefusedEncode_IsATypedFailure_AndWritesNothing()
        {
            string root = NewDirectory();
            FileRbxWorldPackageStore store = new(
                root, persistenceSyncAsync: _ => UniTask.FromResult(true), utcNow: () => CapturedAtUtc,
                encodeFrameBudget: TimeSpan.Zero);
            RbxWorldPackagePayload payload = P3Fixtures.CapturePayload("Refused");
            RbxWorldPackagePayload refused = new(
                payload.CapturedAtUtc, payload.Settings, payload.Tree, payload.Parts, payload.CameraCFrame,
                new[]
                {
                    new RbxWorldModSource(
                        new LuaModManifest { Id = "refused-mod", Active = true, LoadOrder = LuaModManifest.MaximumLoadOrder + 1L },
                        "return 1")
                },
                payload.Diagnostics);

            RbxWorldPackageWriteResult manual = await Within(store.CreateManualAsync("slot-b", refused));
            RbxWorldPackageWriteResult auto = await Within(store.CreateAutoAsync("execute_lua", refused));
            RbxWorldPackageWriteResult startup = await Within(store.SelectStartupAsync(refused, "manual", "slot-b"));

            foreach (RbxWorldPackageWriteResult written in new[] { manual, auto, startup })
            {
                Assert.IsFalse(written.Success);
                Assert.IsTrue(written.PackageCannotBeEncoded, written.Error);
                StringAssert.Contains("load order", written.Error);
            }

            Assert.AreEqual(0, Directory.GetFiles(root, "*", SearchOption.AllDirectories).Length,
                string.Join(", ", Directory.GetFiles(root, "*", SearchOption.AllDirectories)));
        }

        /// <summary>
        /// A deferred encode that fails (here the payload was changed after its proof, so its world.json cannot be
        /// written) is still a lost backup: the commit reports it typed, and the next mutation backs itself up durably
        /// before it runs.
        /// </summary>
        [Test]
        public async Task Store_AFailingDeferredEncode_IsALostBackup_AndTheNextMutationBacksUpDurablyFirst()
        {
            string root = NewDirectory();
            RbxWorldPackagePayload first = P3Fixtures.CapturePayload("First");
            RbxWorldPackagePayload second = P3Fixtures.CapturePayload("Second");
            int waits = 0;
            FileRbxWorldPackageStore store = new(
                root,
                persistenceSyncAsync: _ => UniTask.FromResult(true),
                utcNow: () => CapturedAtUtc,
                waitBeforeDeferredEncodeAsync: _ =>
                {
                    if (waits++ == 0)
                    {
                        first.Tree.Instances[1].Model = new ModelSnapshot { HasStoredWorldPivot = true, StoredWorldPivot = "1,2" };
                    }

                    return UniTask.CompletedTask;
                },
                encodeFrameBudget: TimeSpan.Zero);
            int captures = 0;
            ConfirmedWorldMutationGate gate = new(_ => UniTask.FromResult(captures++ == 0 ? first : second), store, true);
            P3RecordingBindings bindings = new();
            LuaCsGameToolExecutor executor = new(new LuaCsSecureEnvironment(), bindings, new NullLuaExecutionObserver(), null, gate);

            LuaTool.LuaResult mutated = await Within(executor.ExecuteAsync("mutate_world()", CancellationToken.None));
            RbxWorldPackageWriteResult lost = await Within(gate.WaitForPendingBackupAsync());
            LuaTool.LuaResult next = await Within(executor.ExecuteAsync("mutate_world()", CancellationToken.None));

            Assert.IsTrue(mutated.Success, mutated.Error);
            Assert.IsFalse(lost.Success);
            Assert.IsTrue(lost.PackageCannotBeEncoded);
            StringAssert.Contains("could not be encoded behind it", lost.Error);
            Assert.IsTrue(next.Success, next.Error);
            Assert.IsFalse(gate.HasPendingBackup, "the next backup was written before its mutation");
            IReadOnlyList<RbxAutoSaveInfo> autosaves = store.ListAutoSaves();
            Assert.AreEqual(1, autosaves.Count, "the lost backup left nothing, the durable one is there");
            CollectionAssert.AreEqual(
                RbxWorldPackageSerializer.WritePackage(second),
                File.ReadAllBytes(Path.Combine(root, "Auto", autosaves[0].FileName)));
        }

        // ==================== The kept world's record ====================

        /// <summary>
        /// The record of a kept world (here /save) digests and encodes the world a frame at a time with one encode:
        /// the waits are the digest's and the package's units with no second pass over the entries, no frame holds the
        /// store's transaction, and the startup entry holds the world.
        /// </summary>
        [Test]
        public async Task KeptWorldRecord_DigestsAndEncodesAFrameAtATime_WithOneEncode()
        {
            string root = NewDirectory();
            FrameCounter frames = new(root);
            FileRbxWorldPackageStore store = new(
                root,
                persistenceSyncAsync: _ => UniTask.FromResult(true),
                utcNow: () => CapturedAtUtc,
                waitBeforeDeferredEncodeAsync: frames.WaitAsync,
                encodeFrameBudget: TimeSpan.Zero);
            using P3HeadlessSession session = new(store);
            session.Controller.KeepWorldBetweenStarts(null);
            session.Controller.AdoptDefaultWorldIfKept();
            ActorContext actor = new LocalActorIdentityProvider("kept-actor").GetActorContext(BuiltInAgentRoleIds.Programmer);
            LuaTool.LuaResult built = await Within(session.ExecuteAsAsync(actor,
                "local f = Instance.new('Folder') f.Name = 'KeptFolder' f.Parent = workspace return true"));
            Assert.IsTrue(built.Success, built.Error);
            await Within(session.Gate.WaitForPendingBackupAsync());
            RbxWorldPackagePayload world = session.Controller.CaptureCurrent();
            int digestUnits;
            int packageUnits;
            using (RbxWorldPackageSerializer.IncrementalEncoder reference =
                   RbxWorldPackageSerializer.IncrementalEncoder.ForEntryDigest(world, DateTime.MinValue))
            {
                while (!reference.Step(Always))
                {
                }

                digestUnits = reference.UnitsDone;
                reference.ContinueWithPackage(RbxWorldPackageLimits.Format);
                while (!reference.Step(Always))
                {
                }

                packageUnits = reference.UnitsDone - digestUnits;
            }

            frames.TakeWaits();
            string saved = await Within(session.Controller.SaveKeptWorldNowAsync());

            StringAssert.StartsWith("Saved ", saved);
            Assert.AreEqual(digestUnits - 1 + 1 + packageUnits - 1, frames.TakeWaits(),
                "the digest's units, a frame, then only the package's: the entries are encoded once");
            Assert.AreEqual(0, frames.HeldTransactions);
            string[] entries = Directory.GetFiles(store.StartupDirectoryForTests, "*" + RbxWorldPackageNames.Extension);
            Assert.AreEqual(1, entries.Length, "the record is the one startup entry");
            RbxWorldPackagePayload recorded = RbxWorldPackageSerializer.ReadPackage(File.ReadAllBytes(entries[0]));
            Assert.IsNotNull(P3InstantEditLoopEditModeTests.FindFolder(recorded, "KeptFolder"));

            string again = await Within(session.Controller.SaveKeptWorldNowAsync());
            StringAssert.StartsWith("Saved ", again);
            Assert.AreEqual(digestUnits - 1, frames.TakeWaits(), "an unchanged world is digested, compared and not written");
            StringAssert.Contains("already on disk", session.Controller.LastKeptWorldRecord);
        }

        /// <summary>
        /// A kept world's record and the autosave behind a build, both waiting for a frame that never comes (a closed
        /// page, a quitting player), are finished at once when the play world stops: the record is written with the
        /// world it captured and the autosave is installed, instead of both being left half-encoded.
        /// </summary>
        [Test]
        public async Task KeptWorldRecordAndBackupBehind_MidSlicesWhenThePlayWorldStops_AreFinishedAtOnce()
        {
            string root = NewDirectory();
            UniTaskCompletionSource frameThatNeverComes = new();
            FileRbxWorldPackageStore store = new(
                root,
                persistenceSyncAsync: _ => UniTask.FromResult(true),
                utcNow: () => CapturedAtUtc,
                waitBeforeDeferredEncodeAsync: _ => frameThatNeverComes.Task,
                encodeFrameBudget: TimeSpan.Zero);
            P3HeadlessSession session = new(store);
            try
            {
                session.Controller.KeepWorldBetweenStarts(null);
                session.Controller.AdoptDefaultWorldIfKept();
                ActorContext actor = new LocalActorIdentityProvider("stopping-actor").GetActorContext(BuiltInAgentRoleIds.Programmer);
                LuaTool.LuaResult built = await Within(session.ExecuteAsAsync(actor,
                    "local f = Instance.new('Folder') f.Name = 'StoppedFolder' f.Parent = workspace return true"));
                Assert.IsTrue(built.Success, built.Error);
                Task<string> save = session.Controller.SaveKeptWorldNowAsync().AsTask();
                Assert.IsTrue(session.Gate.HasPendingBackup, "precondition: the autosave behind the build waits for its frame");
                Assert.IsFalse(save.IsCompleted, "precondition: the record waits for its next frame");
                CollectionAssert.IsEmpty(store.ListAutoSaves());

                session.Dispose();

                StringAssert.StartsWith("Saved ", await Within(save));
                RbxWorldPackageWriteResult behind = await Within(session.Gate.WaitForPendingBackupAsync());
                Assert.IsTrue(behind.Success, behind.Error);
                Assert.AreEqual(1, store.ListAutoSaves().Count, "the autosave behind the build is installed");
                string[] entries = Directory.GetFiles(store.StartupDirectoryForTests, "*" + RbxWorldPackageNames.Extension);
                Assert.AreEqual(1, entries.Length, "the record is the one startup entry");
                Assert.IsNotNull(P3InstantEditLoopEditModeTests.FindFolder(
                    RbxWorldPackageSerializer.ReadPackage(File.ReadAllBytes(entries[0])), "StoppedFolder"));
            }
            finally
            {
                session.Dispose();
            }
        }

        /// <summary>
        /// The host's own call for a pause: a record still encoding is finished in one step while the play world keeps
        /// running, and a record that starts after the play world stopped is not spread over frames either.
        /// </summary>
        [Test]
        public async Task KeptWorldRecord_FinishWritesInFlightAtOnce_EndsTheWaitForFrames()
        {
            string root = NewDirectory();
            UniTaskCompletionSource frameThatNeverComes = new();
            FileRbxWorldPackageStore store = new(
                root,
                persistenceSyncAsync: _ => UniTask.FromResult(true),
                utcNow: () => CapturedAtUtc,
                waitBeforeDeferredEncodeAsync: _ => frameThatNeverComes.Task,
                encodeFrameBudget: TimeSpan.Zero);
            using P3HeadlessSession session = new(store);
            session.Controller.KeepWorldBetweenStarts(null);
            session.Controller.AdoptDefaultWorldIfKept();
            ActorContext actor = new LocalActorIdentityProvider("pausing-actor").GetActorContext(BuiltInAgentRoleIds.Programmer);
            LuaTool.LuaResult built = await Within(session.ExecuteAsAsync(actor,
                "local f = Instance.new('Folder') f.Name = 'PausedFolder' f.Parent = workspace return true"));
            Assert.IsTrue(built.Success, built.Error);
            Task<string> save = session.Controller.SaveKeptWorldNowAsync().AsTask();
            Assert.IsFalse(save.IsCompleted, "precondition: the record waits for its next frame");

            session.Controller.FinishWritesInFlightAtOnce();

            StringAssert.StartsWith("Saved ", await Within(save));
            Assert.IsTrue((await Within(session.Gate.WaitForPendingBackupAsync())).Success);
            Assert.IsFalse(session.Controller.IsDisposed, "a pause leaves the play world running");
            LuaTool.LuaResult again = await Within(session.ExecuteAsAsync(actor,
                "local f = Instance.new('Folder') f.Name = 'AfterThePause' f.Parent = workspace return true"));
            Assert.IsTrue(again.Success, again.Error);
            Assert.IsTrue(session.Gate.HasPendingBackup, "a later autosave is spread over frames again");
        }

        /// <summary>
        /// A world change made while a record of the kept world is still encoding is not swallowed by the record already
        /// running: that record holds the world it captured, and the change gets a record of its own once the world has
        /// been quiet again.
        /// </summary>
        [Test]
        public async Task KeptWorld_AChangeWhileARecordIsMidSlices_GetsARecordOfItsOwn()
        {
            string root = NewDirectory();
            ManualFrames frames = new();
            FileRbxWorldPackageStore store = new(
                root,
                persistenceSyncAsync: _ => UniTask.FromResult(true),
                utcNow: () => CapturedAtUtc,
                waitBeforeDeferredEncodeAsync: frames.WaitAsync,
                encodeFrameBudget: TimeSpan.Zero);
            using P3HeadlessSession session = new(store);
            DateTime now = CapturedAtUtc;
            session.Controller.ConfigurePendingLoadClockForTests(() => now, TimeSpan.FromMinutes(5d));
            session.Controller.KeepWorldBetweenStarts(null);
            session.Controller.AdoptDefaultWorldIfKept();
            ActorContext actor = new LocalActorIdentityProvider("busy-actor").GetActorContext(BuiltInAgentRoleIds.Programmer);
            LuaTool.LuaResult first = await Within(session.ExecuteAsAsync(actor,
                "local f = Instance.new('Folder') f.Name = 'BeforeTheRecord' f.Parent = workspace return true"));
            Assert.IsTrue(first.Success, first.Error);
            frames.AdvanceUntil(() => !session.Gate.HasPendingBackup, "the autosave behind the first build");

            now += RbxWorldRuntimeSessionController.KeptWorldQuietTime + TimeSpan.FromSeconds(1d);
            session.Controller.PumpFrame(actor, 0.016f);
            Task record = session.Controller.StartupRefreshForTests;
            Assert.IsFalse(record.IsCompleted, "precondition: the quiet world's record is spread over frames");
            LuaTool.LuaResult second = await Within(session.ExecuteAsAsync(actor,
                "local f = Instance.new('Folder') f.Name = 'DuringTheRecord' f.Parent = workspace return true"));
            Assert.IsTrue(second.Success, second.Error);
            frames.AdvanceUntil(() => record.IsCompleted && !session.Gate.HasPendingBackup, "the record and the second autosave");
            await Within(record);
            RbxWorldPackagePayload recorded = NewestStartupEntry(store);
            Assert.IsNotNull(P3InstantEditLoopEditModeTests.FindFolder(recorded, "BeforeTheRecord"));
            Assert.IsNull(P3InstantEditLoopEditModeTests.FindFolder(recorded, "DuringTheRecord"),
                "precondition: the running record holds the world it captured before the second build");

            now += RbxWorldRuntimeSessionController.KeptWorldQuietTime + TimeSpan.FromSeconds(1d);
            session.Controller.PumpFrame(actor, 0.016f);
            Task next = session.Controller.StartupRefreshForTests;
            Assert.AreNotSame(record, next, "the change made during the record starts a record of its own");
            frames.AdvanceUntil(() => next.IsCompleted, "the second record");
            await Within(next);
            Assert.IsNotNull(P3InstantEditLoopEditModeTests.FindFolder(NewestStartupEntry(store), "DuringTheRecord"));
        }

        // ==================== Helpers ====================

        /// <summary>The newest startup entry of <paramref name="store"/>, read back.</summary>
        private static RbxWorldPackagePayload NewestStartupEntry(FileRbxWorldPackageStore store)
        {
            string[] entries = Directory.GetFiles(store.StartupDirectoryForTests, "*" + RbxWorldPackageNames.Extension);
            Assert.Greater(entries.Length, 0, "a startup entry was written");
            Array.Sort(entries, StringComparer.Ordinal);
            return RbxWorldPackageSerializer.ReadPackage(File.ReadAllBytes(entries[entries.Length - 1]));
        }

        /// <summary>Frames a test hands out one at a time: every wait completes on the next <see cref="Advance"/>.</summary>
        private sealed class ManualFrames
        {
            private UniTaskCompletionSource _next = new();

            public UniTask WaitAsync(CancellationToken cancellationToken)
            {
                return _next.Task;
            }

            public void Advance()
            {
                UniTaskCompletionSource current = _next;
                _next = new UniTaskCompletionSource();
                current.TrySetResult();
            }

            public void AdvanceUntil(Func<bool> done, string what)
            {
                for (int frame = 0; frame < 100000 && !done(); frame++)
                {
                    Advance();
                }

                Assert.IsTrue(done(), what + " finishes as frames come");
            }
        }

        private readonly List<string> _directories = new();

        [TearDown]
        public void DeleteTemporaryDirectories()
        {
            foreach (string directory in _directories)
            {
                if (System.IO.Directory.Exists(directory))
                {
                    System.IO.Directory.Delete(directory, true);
                }
            }

            _directories.Clear();
        }

        private string NewDirectory()
        {
            string path = WorldPackageTestWorlds.NewTemporaryDirectory();
            _directories.Add(path);
            return path;
        }

        /// <summary>
        /// A payload of many units whose package stays under the 64 KiB a file write takes without a frame, so a store
        /// test runs in the engine-free suite too.
        /// </summary>
        private static RbxWorldPackagePayload StoreSizedPayload()
        {
            RbxWorldPackagePayload payload = WorldPackageTestWorlds.CaptureHeavyWorld(96, 8, 4, CapturedAtUtc);
            Assert.Less(RbxWorldPackageSerializer.WritePackage(payload).Length, 64 * 1024, "precondition: one write chunk");
            Assert.Greater(UnitsOf(payload, true), 10, "precondition: many units");
            return payload;
        }

        /// <summary>Units the encode of <paramref name="payload"/>'s package takes, with or without its validation.</summary>
        private static int UnitsOf(RbxWorldPackagePayload payload, bool validate)
        {
            using RbxWorldPackageSerializer.IncrementalEncoder encoder =
                RbxWorldPackageSerializer.IncrementalEncoder.ForPackage(payload, RbxWorldPackageLimits.Format, validate);
            while (!encoder.Step(Always))
            {
            }

            return encoder.UnitsDone;
        }

        /// <summary>
        /// A fake host clock and frame wait: every clock read costs <see cref="ReadMs"/> (one unit of work reads it once)
        /// and every frame wait adds the gap, the rest of a frame at the frame rate played.
        /// </summary>
        private sealed class FakeFrames
        {
            public const double ReadMs = 0.2d;

            private readonly long _gapTicks;
            private readonly long _readTicks = (long)(ReadMs / 1000d * System.Diagnostics.Stopwatch.Frequency);
            private long _now = 1L;
            private long _resumedAt = 1L;

            public FakeFrames(TimeSpan gap)
            {
                _gapTicks = (long)(gap.TotalSeconds * System.Diagnostics.Stopwatch.Frequency);
                Clock = () => _now += _readTicks;
            }

            public Func<long> Clock { get; }

            public int Waits { get; private set; }

            public double LongestSliceMs { get; private set; }

            public int TakeWaits()
            {
                int waits = Waits;
                Waits = 0;
                return waits;
            }

            public UniTask WaitAsync(CancellationToken cancellationToken)
            {
                Waits++;
                double sliceMs = (_now - _resumedAt) * 1000d / System.Diagnostics.Stopwatch.Frequency;
                LongestSliceMs = Math.Max(LongestSliceMs, sliceMs);
                _now += _gapTicks;
                _resumedAt = _now;
                return UniTask.CompletedTask;
            }
        }

        /// <summary>How long any await of this fixture may take before the test fails instead of hanging.</summary>
        private static readonly TimeSpan AwaitTimeout = TimeSpan.FromSeconds(30d);

        /// <summary>
        /// <paramref name="task"/>, or a failure after <see cref="AwaitTimeout"/>: an encode that waits for a frame nobody
        /// pumps then fails its test instead of hanging the runner.
        /// </summary>
        private static async Task<T> Within<T>(Task<T> task)
        {
            if (!ReferenceEquals(await Task.WhenAny(task, Task.Delay(AwaitTimeout)), task))
            {
                Assert.Fail("Not finished within " + AwaitTimeout.TotalSeconds + " s: something waits for a frame that never comes.");
            }

            return await task;
        }

        private static Task<T> Within<T>(UniTask<T> task)
        {
            return Within(task.AsTask());
        }

        private static async Task Within(Task task)
        {
            if (!ReferenceEquals(await Task.WhenAny(task, Task.Delay(AwaitTimeout)), task))
            {
                Assert.Fail("Not finished within " + AwaitTimeout.TotalSeconds + " s: something waits for a frame that never comes.");
            }

            await task;
        }

        private static Task Within(UniTask task)
        {
            return Within(task.AsTask());
        }

        /// <summary>A frame wait that counts and checks, in every frame, that the store's transaction is free.</summary>
        private sealed class FrameCounter
        {
            private readonly string _transactionRoot;

            public FrameCounter(string root)
            {
                _transactionRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            }

            public int Waits { get; private set; }

            public int HeldTransactions { get; private set; }

            public int TakeWaits()
            {
                int waits = Waits;
                Waits = 0;
                return waits;
            }

            public UniTask WaitAsync(CancellationToken cancellationToken)
            {
                Waits++;
                using (IDisposable transaction = WorldPackageTransactions.TryEnter(
                           _transactionRoot, typeof(SystemRbxWorldPackageFileSystem)))
                {
                    if (transaction == null)
                    {
                        HeldTransactions++;
                    }
                }

                return UniTask.CompletedTask;
            }
        }

        internal static IEnumerable<RbxWorldPackagePayload> EveryShapeOfPayload()
        {
            foreach (RbxWorldPackagePayload payload in WorldPackageEncodeBehindEditModeTests.EveryShapeOfPayload())
            {
                yield return payload;
            }

            yield return BlockWorldLikePayload();
        }

        /// <summary>
        /// A world like a Block World's: Terrain columns of 16 x 32 x 16 cells (two chunks each) with hills, sea, sand,
        /// snow, caves and ore, the world-owned HUD, the torch list in a Workspace attribute and the nine bundled Block
        /// World mods.
        /// </summary>
        internal static RbxWorldPackagePayload BlockWorldLikePayload()
        {
            WorldPackageGuiWorld world = WorldPackageGuiWorld.WithHud("worldpkg-chunked-blockworld");
            RbxInstance workspace = world.Game.FindFirstChildOfClass("Workspace");
            RbxTerrain terrain = RbxTerrainBootstrap.Find(workspace);
            FillColumns(terrain.Store, 3);
            workspace.SetAttribute("BlockWorldTorches", "4,1,-3;12,0,7;-9,2,15");
            workspace.SetAttribute("BlockWorldSeed", 1337d);
            RbxWorldPackagePayload captured = world.Capture(CapturedAtUtc);
            List<RbxWorldModSource> mods = new();
            for (int index = 0; index < BlockWorldMods.Length; index++)
            {
                string id = BlockWorldMods[index];
                mods.Add(new RbxWorldModSource(
                    new LuaModManifest { Id = id, Name = id, Active = true, LoadOrder = index + 1 },
                    BlockWorldTestWorld.Source(id)));
            }

            return new RbxWorldPackagePayload(
                captured.CapturedAtUtc, captured.Settings, captured.Tree, captured.Parts, captured.CameraCFrame, mods,
                captured.Diagnostics, captured.OptionalState);
        }

        /// <summary>Every column whose centre lies within <paramref name="radius"/> columns of the origin, as the Block World streams them.</summary>
        private static void FillColumns(TerrainChunkStore store, int radius)
        {
            byte basalt = Slot("Basalt");
            byte rock = Slot("Rock");
            byte ground = Slot("Ground");
            byte grass = Slot("Grass");
            byte sand = Slot("Sand");
            byte snow = Slot("Snow");
            byte water = Slot("Water");
            byte ore = Slot("Slate");
            for (int columnX = -radius - 1; columnX <= radius + 1; columnX++)
            {
                for (int columnZ = -radius - 1; columnZ <= radius + 1; columnZ++)
                {
                    if (columnX * columnX + columnZ * columnZ > radius * radius + 1)
                    {
                        continue;
                    }

                    TerrainCellBox box = new(columnX * 16, -16, columnZ * 16, 16, 32, 16);
                    byte[] materials = new byte[box.CellCount];
                    byte[] occupancy = new byte[box.CellCount];
                    for (int x = box.MinX; x < box.MinX + 16; x++)
                    {
                        for (int z = box.MinZ; z < box.MinZ + 16; z++)
                        {
                            double value = 1d + 6d * Math.Sin(x * 0.07d) + 5d * Math.Cos(z * 0.05d) + 3d * Math.Sin((x + z) * 0.11d);
                            int surface = Math.Max(-8, Math.Min(13, (int)Math.Floor(value)));
                            for (int y = -16; y < 16; y++)
                            {
                                byte material = TerrainMaterials.Air;
                                if (y == -16)
                                {
                                    material = basalt;
                                }
                                else if (y < surface - 3)
                                {
                                    double cave = Math.Sin(x * 0.19d) * Math.Cos(y * 0.23d)
                                                  + Math.Sin(z * 0.17d) * Math.Cos(x * 0.13d) + Math.Sin(y * 0.29d + z * 0.07d);
                                    material = cave > 1.4d ? TerrainMaterials.Air : (x * 7 + y * 13 + z * 3) % 29 == 0 ? ore : rock;
                                }
                                else if (y < surface)
                                {
                                    material = ground;
                                }
                                else if (y == surface)
                                {
                                    material = surface >= 12 ? snow : surface <= -1 ? sand : grass;
                                }
                                else if (y <= -2)
                                {
                                    material = water;
                                }

                                int index = box.RegionIndexOf(x, y, z);
                                materials[index] = material;
                                occupancy[index] = material == TerrainMaterials.Air ? (byte)0 : TerrainOccupancy.Full;
                            }
                        }
                    }

                    store.WriteBox(box, materials, occupancy);
                }
            }
        }

        private static byte Slot(string material)
        {
            Assert.IsTrue(TerrainMaterials.TryGetSlot(material, out byte slot), material + " is a Terrain material");
            return slot;
        }

        private static TerrainChunkStore StoreOf(TerrainSnapshot snapshot)
        {
            TerrainChunkStore store = new();
            store.RestoreSnapshot(snapshot);
            return store;
        }

        private static bool Always()
        {
            return true;
        }

        private static byte[] EncodeOneUnitAtATime(RbxWorldPackagePayload payload, RbxWorldPackageLimits limits, out int units)
        {
            using RbxWorldPackageSerializer.IncrementalEncoder encoder =
                RbxWorldPackageSerializer.IncrementalEncoder.ForPackage(payload, limits);
            int steps = 0;
            while (!encoder.Step(Always))
            {
                steps++;
            }

            units = encoder.UnitsDone;
            Assert.AreEqual(units, steps + 1, "one unit per step");
            return encoder.Result;
        }

        private static byte[] EncodeInOneStep(RbxWorldPackagePayload payload, RbxWorldPackageLimits limits)
        {
            using RbxWorldPackageSerializer.IncrementalEncoder encoder =
                RbxWorldPackageSerializer.IncrementalEncoder.ForPackage(payload, limits);
            Assert.IsTrue(encoder.Step(() => false));
            Assert.IsTrue(encoder.IsComplete);
            return encoder.Result;
        }

        private static byte[] DigestOneUnitAtATime(RbxWorldPackagePayload payload, DateTime? digestCapturedAtUtc)
        {
            using RbxWorldPackageSerializer.IncrementalEncoder encoder =
                RbxWorldPackageSerializer.IncrementalEncoder.ForEntryDigest(payload, digestCapturedAtUtc);
            while (!encoder.Step(Always))
            {
            }

            Assert.IsNull(encoder.Result, "a digest encode writes no package");
            return encoder.EntryDigest;
        }

        private static byte[] DigestThenPackage(RbxWorldPackagePayload payload, RbxWorldPackageLimits limits)
        {
            using RbxWorldPackageSerializer.IncrementalEncoder encoder =
                RbxWorldPackageSerializer.IncrementalEncoder.ForEntryDigest(payload, DateTime.MinValue);
            while (!encoder.Step(Always))
            {
            }

            encoder.ContinueWithPackage(limits);
            while (!encoder.Step(Always))
            {
            }

            return encoder.Result;
        }

        private static IReadOnlyList<System.IO.Compression.ZipArchiveEntry> Entries(byte[] package)
        {
            System.IO.Compression.ZipArchive archive = new(new System.IO.MemoryStream(package, false));
            return archive.Entries;
        }

        private static bool IsMono => Type.GetType("Mono.Runtime") != null;

        private static bool? _deflateIgnoresSplit;

        /// <summary>True when this runtime's deflate gives one stream for an input written at once and in slices.</summary>
        private static bool DeflateIgnoresHowItsInputIsSplit()
        {
            if (_deflateIgnoresSplit.HasValue)
            {
                return _deflateIgnoresSplit.Value;
            }

            byte[] input = TerrainVoxelCodec.Encode(StoreOf(BlockWorldLikePayload().Terrain));
            byte[] whole = Deflate(input, input.Length);
            bool same = true;
            foreach (int slice in new[] { RbxWorldPackageSerializer.IncrementalEncoder.DeflateSliceBytes, 4096, 1000 })
            {
                same &= System.Linq.Enumerable.SequenceEqual(whole, Deflate(input, slice));
            }

            _deflateIgnoresSplit = same;
            return same;
        }

        private static byte[] Deflate(byte[] input, int slice)
        {
            using System.IO.MemoryStream output = new();
            using (System.IO.Compression.DeflateStream deflate = new(output, System.IO.Compression.CompressionLevel.Optimal, true))
            {
                for (int offset = 0; offset < input.Length; offset += slice)
                {
                    deflate.Write(input, offset, Math.Min(slice, input.Length - offset));
                }
            }

            return output.ToArray();
        }

        /// <summary>
        /// The same entries in the same order with the same names, sizes, times and bytes; the same package bytes
        /// where this runtime's deflate ignores the split.
        /// </summary>
        private static void AssertSamePackage(byte[] expected, byte[] actual)
        {
            if (DeflateIgnoresHowItsInputIsSplit())
            {
                CollectionAssert.AreEqual(expected, actual);
                return;
            }

            IReadOnlyList<System.IO.Compression.ZipArchiveEntry> left = Entries(expected);
            IReadOnlyList<System.IO.Compression.ZipArchiveEntry> right = Entries(actual);
            Assert.AreEqual(left.Count, right.Count);
            for (int index = 0; index < left.Count; index++)
            {
                Assert.AreEqual(left[index].FullName, right[index].FullName);
                Assert.AreEqual(left[index].Length, right[index].Length);
                Assert.AreEqual(left[index].LastWriteTime, right[index].LastWriteTime);
                CollectionAssert.AreEqual(Read(left[index]), Read(right[index]), left[index].FullName);
            }
        }

        private static byte[] Read(System.IO.Compression.ZipArchiveEntry entry)
        {
            using System.IO.Stream stream = entry.Open();
            using System.IO.MemoryStream copy = new();
            stream.CopyTo(copy);
            return copy.ToArray();
        }

        private static void AssertSameRefusal(Exception expected, TestDelegate encode)
        {
            Exception actual = Assert.Catch(encode, "refused like " + expected.GetType().Name + ": " + expected.Message);
            Assert.AreEqual(expected.GetType(), actual.GetType());
            Assert.AreEqual(expected.Message, actual.Message);
        }

        private static bool IsWritable(RbxWorldPackagePayload payload)
        {
            try
            {
                RbxWorldPackageSerializer.WritePackageInOneBlockForTests(payload, RbxWorldPackageLimits.Format);
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static bool AnyContains(IEnumerable<string> messages, string text)
        {
            foreach (string message in messages)
            {
                if (message.IndexOf(text, StringComparison.Ordinal) >= 0)
                {
                    return true;
                }
            }

            return false;
        }
    }
}
