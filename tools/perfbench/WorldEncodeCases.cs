using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;
using CoreAI.Ai;
using CoreAI.Mods.Rbx.Instances;
using CoreAI.Mods.Rbx.Instances.Terrain;
using CoreAI.Mods.WorldPackages;
using Cysharp.Threading.Tasks;

namespace CoreAI.PerfBench
{
    /// <summary>
    /// The world package encode spread over frames (<c>RbxWorldPackageSerializer.IncrementalEncoder</c> run by
    /// <c>RbxWorldPackageEncodeFrames</c>): per payload, the validation unit alone, and whole encodes played at 60 fps
    /// (the store's 4 ms budget, 12.7 ms of the frame left to the game) and at 1 fps (a hidden browser tab). The time
    /// per operation is the whole encode's work; the details line gives the slices (frames) it took and the longest
    /// one, which is the hitch a player can see. The frames are played with the real clock plus a jump at every wait,
    /// so every unit costs what it costs.
    /// </summary>
    internal static class WorldEncodeCases
    {
        private const double SixtyFpsGapMs = 12.7d;
        private const double HiddenTabGapMs = 1000d;

        private static readonly string[] BlockWorldMods =
        {
            "blockworld_autopilot", "blockworld_build", "blockworld_daynight", "blockworld_falling", "blockworld_hud",
            "blockworld_survival", "blockworld_torches", "blockworld_villagers", "blockworld_world"
        };

        public static void Add(List<MicroCase> cases)
        {
            foreach (int parts in new[] { 2048, 16384 })
            {
                int count = parts;
                AddFor(cases, count + " Parts", () => GridPayload(count), false);
            }

            AddFor(cases, "Block World-sized (213 columns, 9 mods)", BlockWorldPayload, true);
        }

        private static void AddFor(List<MicroCase> cases, string size, Func<PayloadHolder> build, bool record)
        {
            PayloadHolder holder = null;
            void Setup()
            {
                holder ??= build();
            }

            // WHY every case builds its payload again: a payload kept from an earlier case stays in the heap the
            // collector marks, and on Mono's Boehm collector that alone lengthens the pauses a later case measures.
            void Teardown()
            {
                holder = null;
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }

            cases.Add(new MicroCase("edit-gate", "payload validation (the encoder's first unit), " + size, ops =>
            {
                long sum = 0;
                for (int i = 0; i < ops; i++)
                {
                    using RbxWorldPackageSerializer.IncrementalEncoder encoder =
                        RbxWorldPackageSerializer.IncrementalEncoder.ForPackage(holder.Payload, RbxWorldPackageLimits.Format);
                    encoder.Step(() => true);
                    sum += encoder.UnitsDone;
                }

                return sum;
            }, "the one unit the deferred backup skips (its proof ran it before the mutation)", Setup, Teardown, fixedOps: 1));

            UnitStats units = new();
            cases.Add(new MicroCase("edit-gate", "every unit timed alone (" + (record ? "record: digest, then package" : "deferred backup") + "), " + size, ops =>
            {
                long sum = 0;
                for (int i = 0; i < ops; i++)
                {
                    using RbxWorldPackageSerializer.IncrementalEncoder encoder = record
                        ? RbxWorldPackageSerializer.IncrementalEncoder.ForEntryDigest(holder.Payload, DateTime.MinValue)
                        : RbxWorldPackageSerializer.IncrementalEncoder.ForPackage(holder.Payload, RbxWorldPackageLimits.Format, false);
                    units.Run(encoder);
                    if (record)
                    {
                        encoder.ContinueWithPackage(RbxWorldPackageLimits.Format);
                        units.Run(encoder);
                    }

                    units.EndRun();
                    sum += encoder.UnitsDone;
                }

                return sum;
            }, "one unit a step, each timed: the floor of the longest frame any budget gives", Setup, Teardown, fixedOps: 1,
                details: units.Describe));

            if (record)
            {
                cases.Add(new MicroCase("edit-gate", "kept-world record in one block as before (digest + WritePackage), " + size, ops =>
                {
                    long sum = 0;
                    for (int i = 0; i < ops; i++)
                    {
                        sum += RbxWorldPackageSerializer.ComputeEntryDigestInOneBlockForTests(
                            holder.Payload.WithCapturedAtUtc(DateTime.MinValue)).Length;
                        sum += RbxWorldPackageSerializer.WritePackageInOneBlockForTests(
                            holder.Payload, RbxWorldPackageLimits.Format).Length;
                    }

                    return sum;
                }, "the one frame the record took before the encoder: its digest, then the package from scratch",
                    Setup, Teardown, fixedOps: 1, details: () => "Terrain entry " + holder.TerrainBytes + " bytes, package "
                                                       + holder.Expected.Length + " bytes"));
            }

            foreach (double gapMs in new[] { SixtyFpsGapMs, HiddenTabGapMs })
            {
                double gap = gapMs;
                string rate = gap < 100d ? "60 fps" : "1 fps";
                FrameStats deferred = new();
                cases.Add(new MicroCase("edit-gate", "deferred backup encode in frames, " + rate + ", " + size, ops =>
                {
                    long sum = 0;
                    for (int i = 0; i < ops; i++)
                    {
                        using RbxWorldPackageSerializer.IncrementalEncoder encoder =
                            RbxWorldPackageSerializer.IncrementalEncoder.ForPackage(
                                holder.Payload, RbxWorldPackageLimits.Format, false);
                        PlayedFrames frames = new(gap);
                        RbxWorldPackageEncodeFrames.RunAsync(encoder, RbxWorldPackageEncodeFrames.DefaultFrameBudget,
                            frames.WaitAsync, CancellationToken.None, true, null, frames.Clock).GetAwaiter().GetResult();
                        frames.End();
                        deferred.Add(frames, encoder.Result, holder.Expected);
                        sum += encoder.Result.Length;
                    }

                    return sum;
                }, "the write behind an AI edit: a wait for the next frame, then slices of the 4 ms budget, a third of "
                   + "the frame's gap, or all at a gap of 250 ms", Setup, Teardown, fixedOps: 1, details: deferred.Describe));

                if (!record)
                {
                    continue;
                }

                FrameStats kept = new();
                cases.Add(new MicroCase("edit-gate", "kept-world record (digest, then package) in frames, " + rate + ", " + size, ops =>
                {
                    long sum = 0;
                    for (int i = 0; i < ops; i++)
                    {
                        using RbxWorldPackageSerializer.IncrementalEncoder encoder =
                            RbxWorldPackageSerializer.IncrementalEncoder.ForEntryDigest(holder.Payload, DateTime.MinValue);
                        PlayedFrames frames = new(gap);
                        RbxWorldPackageEncodeFrames.RunAsync(encoder, RbxWorldPackageEncodeFrames.DefaultFrameBudget,
                            frames.WaitAsync, CancellationToken.None, false, null, frames.Clock).GetAwaiter().GetResult();
                        encoder.ContinueWithPackage(RbxWorldPackageLimits.Format);
                        RbxWorldPackageEncodeFrames.RunAsync(encoder, RbxWorldPackageEncodeFrames.DefaultFrameBudget,
                            frames.WaitAsync, CancellationToken.None, !encoder.FinishesAtOnce, null, frames.Clock)
                            .GetAwaiter().GetResult();
                        frames.End();
                        kept.Add(frames, encoder.Result, holder.Expected);
                        sum += encoder.Result.Length;
                    }

                    return sum;
                }, "what a kept world's record encodes after the capture: the entry digest it compares, then the "
                   + "package from the same entries", Setup, Teardown, fixedOps: 1, details: kept.Describe));
            }
        }

        private static PayloadHolder GridPayload(int parts)
        {
            HeadlessWorld world = HeadlessWorld.Create();
            world.World.BuildStaticGrid(parts);
            RbxWorldPackagePayload payload = RbxWorldPackageSerializer.Capture(world.CaptureContext());
            world.Dispose();
            return new PayloadHolder(payload);
        }

        /// <summary>
        /// A payload the size of a Block World at view radius 8: 213 columns of 16 x 32 x 16 cells (two Terrain chunks
        /// each) with hills, sea, sand, snow, caves and ore, and nine bundled Block World mods (all of them but
        /// blockworld_graphics) read from the checkout. The real seed-1337 world (379 chunks, a 342,334-byte Terrain entry) is of the same order.
        /// </summary>
        private static PayloadHolder BlockWorldPayload()
        {
            HeadlessWorld world = HeadlessWorld.Create();
            RbxTerrain terrain = RbxTerrainBootstrap.Find(world.World.Game.FindFirstChildOfClass("Workspace"));
            FillColumns(terrain.Store, 8);
            RbxWorldPackagePayload captured = RbxWorldPackageSerializer.Capture(world.CaptureContext());
            world.Dispose();
            string folder = Path.Combine(BenchEnvironment.FindRepoRoot(), "Assets", "CoreAIMods", "Runtime", "Resources",
                "CoreAIMods", "BlockWorld");
            List<RbxWorldModSource> mods = new();
            for (int index = 0; index < BlockWorldMods.Length; index++)
            {
                string id = BlockWorldMods[index];
                mods.Add(new RbxWorldModSource(
                    new LuaModManifest { Id = id, Name = id, Active = true, LoadOrder = index + 1 },
                    File.ReadAllText(Path.Combine(folder, id + ".lua"))));
            }

            return new PayloadHolder(new RbxWorldPackagePayload(
                captured.CapturedAtUtc, captured.Settings, captured.Tree, captured.Parts, captured.CameraCFrame, mods,
                captured.Diagnostics, captured.OptionalState));
        }

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
            if (!TerrainMaterials.TryGetSlot(material, out byte slot))
            {
                throw new InvalidOperationException(material + " is not a Terrain material");
            }

            return slot;
        }

        private sealed class PayloadHolder
        {
            public PayloadHolder(RbxWorldPackagePayload payload)
            {
                Payload = payload;
                Expected = RbxWorldPackageSerializer.WritePackage(payload);
                TerrainBytes = payload.Terrain == null ? 0 : EncodedTerrainLength(payload.Terrain);
            }

            public RbxWorldPackagePayload Payload { get; }

            public byte[] Expected { get; }

            public int TerrainBytes { get; }

            private static int EncodedTerrainLength(TerrainSnapshot snapshot)
            {
                TerrainChunkStore store = new();
                store.RestoreSnapshot(snapshot);
                return TerrainVoxelCodec.Encode(store).Length;
            }
        }

        /// <summary>The real clock plus a jump of the frame's gap at every wait; records every slice it timed.</summary>
        private sealed class PlayedFrames
        {
            private readonly long _gapTicks;
            private long _offset;
            private long _resumedAt;

            public PlayedFrames(double gapMs)
            {
                _gapTicks = (long)(gapMs / 1000d * Stopwatch.Frequency);
                Clock = () => Stopwatch.GetTimestamp() + _offset;
                _resumedAt = Clock();
            }

            public Func<long> Clock { get; }

            public int Slices { get; private set; }

            public double LongestSliceMs { get; private set; }

            public UniTask WaitAsync(CancellationToken cancellationToken)
            {
                CloseSlice();
                _offset += _gapTicks;
                _resumedAt = Clock();
                return UniTask.CompletedTask;
            }

            public void End()
            {
                CloseSlice();
            }

            private void CloseSlice()
            {
                double sliceMs = (Clock() - _resumedAt) * 1000d / Stopwatch.Frequency;
                if (sliceMs > 0.01d)
                {
                    Slices++;
                    LongestSliceMs = Math.Max(LongestSliceMs, sliceMs);
                }
            }
        }

        /// <summary>The longest single unit seen after the first (JIT) run, and its phase.</summary>
        private sealed class UnitStats
        {
            private bool _warm;
            private double _longestMs;
            private string _longestPhase = "";
            private int _units;

            public void Run(RbxWorldPackageSerializer.IncrementalEncoder encoder)
            {
                bool done = false;
                while (!done)
                {
                    string phase = encoder.NextUnitPhase;
                    long start = Stopwatch.GetTimestamp();
                    done = encoder.Step(() => true);
                    double ms = (Stopwatch.GetTimestamp() - start) * 1000d / Stopwatch.Frequency;
                    if (_warm && ms > _longestMs)
                    {
                        _longestMs = ms;
                        _longestPhase = phase;
                    }
                }

                _units = encoder.UnitsDone;
            }

            /// <summary>Ends one encode; the first is left out (it compiles the encoder).</summary>
            public void EndRun()
            {
                _warm = true;
            }

            public string Describe()
            {
                return _units + " units; the longest " + _longestMs.ToString("0.00", CultureInfo.InvariantCulture)
                       + " ms (" + _longestPhase + ")";
            }
        }

        private sealed class FrameStats
        {
            private bool _warm;
            private int _runs;
            private int _maxSlices;
            private double _longestSliceMs;
            private bool _sameBytes = true;

            public void Add(PlayedFrames frames, byte[] result, byte[] expected)
            {
                _sameBytes &= System.Linq.Enumerable.SequenceEqual(result, expected);
                // WHY the first run is left out: it compiles the encoder (JIT), which no frame of a warm game pays.
                if (!_warm)
                {
                    _warm = true;
                    return;
                }

                _runs++;
                _maxSlices = Math.Max(_maxSlices, frames.Slices);
                _longestSliceMs = Math.Max(_longestSliceMs, frames.LongestSliceMs);
            }

            public string Describe()
            {
                return _runs + " runs: at most " + _maxSlices.ToString(CultureInfo.InvariantCulture)
                       + " frames with work, longest frame of work "
                       + _longestSliceMs.ToString("0.00", CultureInfo.InvariantCulture) + " ms; "
                       + (_sameBytes ? "the bytes of WritePackage" : "BYTES DIFFER FROM WritePackage");
            }
        }
    }
}
