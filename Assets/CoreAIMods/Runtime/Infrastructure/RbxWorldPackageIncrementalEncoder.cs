using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Threading;
using CoreAI.Mods.Rbx.Instances;
using CoreAI.Mods.Rbx.Instances.Terrain;
using Cysharp.Threading.Tasks;

namespace CoreAI.Mods.WorldPackages
{
    public static partial class RbxWorldPackageSerializer
    {
        /// <summary>
        /// <see cref="WritePackage(RbxWorldPackagePayload, RbxWorldPackageLimits)"/> and
        /// <see cref="ComputeEntryDigest"/> cut into units of bounded work, so a caller can spread one encode over
        /// many frames: <see cref="Step"/> runs units until its budget says stop. The bytes, the digest, the limits
        /// and every refusal are those of the one-block methods, in their order.
        /// </summary>
        /// <remarks>
        /// A unit is the validation, one manifest, the <c>world.json</c> head, tail or a run of
        /// <see cref="WorldJsonInstancesPerUnit"/> instances, one mod's two entries, one Terrain chunk, up to
        /// <see cref="DigestSliceBytes"/> hashed, up to <see cref="DeflateSliceBytes"/> deflated, or the close of the
        /// ZIP. The JSON and the Terrain writers get the same calls in the same order as in one block. WHY deflate
        /// gets each entry in slices at fixed offsets, whatever the budget: the bytes then never depend on how the
        /// work fell into frames. On Mono and .NET Framework deflate emits the same stream however its input is
        /// split, so they are also the bytes of one write of the entry; CoreCLR's (.NET 8) does not, so there a change
        /// of <see cref="DeflateSliceBytes"/> changes the compressed bytes (never the entries). Pinned by
        /// <c>WorldPackageChunkedEncodeEditModeTests</c>. The payload must not change while an encode runs; a
        /// captured payload is never written to.
        /// </remarks>
        internal sealed class IncrementalEncoder : IDisposable
        {
            /// <summary>Instances of <c>world.json</c> one unit writes.</summary>
            internal const int WorldJsonInstancesPerUnit = 16;

            /// <summary>Bytes of one entry one unit hands to deflate.</summary>
            internal const int DeflateSliceBytes = 4 * 1024;

            /// <summary>Bytes one unit adds to the entry digest.</summary>
            internal const int DigestSliceBytes = 64 * 1024;

            private readonly RbxWorldPackagePayload _payload;
            private readonly bool _validate;
            private readonly RbxWorldPackagePayload _digestManifestPayload;
            private RbxWorldPackageLimits _limits;
            private Phase _phase;
            private bool _failed;

            private PackageManifestDto _manifest;
            private byte[] _manifestBytes;
            private PackageManifestDto _digestManifest;
            private byte[] _digestManifestBytes;
            private RbxWorldPackageWorldJson.ResumableWriter _world;
            private RbxWorldPackageSegmentedBytes _worldBytes;
            private int _worldCount;
            private byte[][] _modManifests;
            private byte[][] _modSources;
            private int _modIndex;
            private TerrainChunkStore _terrainStore;
            private List<TerrainChunkCoord> _terrainCoords;
            private MemoryStream _terrainOutput;
            private int _terrainIndex;
            private byte[] _terrainBytes;
            private long _expandedBytes;

            private List<EntryBytes> _entries;
            private int _entryIndex;
            private int _entryOffset;
            private IncrementalHash _hash;
            private byte[] _digest;
            private MemoryStream _output;
            private ZipArchive _archive;
            private Stream _entryStream;
            private byte[] _result;

            private IncrementalEncoder(
                RbxWorldPackagePayload payload,
                RbxWorldPackageLimits limits,
                bool validate,
                RbxWorldPackagePayload digestManifestPayload)
            {
                _payload = payload ?? throw new ArgumentNullException(nameof(payload));
                _limits = limits;
                _validate = validate;
                _digestManifestPayload = digestManifestPayload;
                _phase = validate ? Phase.Validate : Phase.Manifest;
            }

            private enum Phase
            {
                Validate,
                Manifest,
                WorldJson,
                Mods,
                TerrainStart,
                TerrainChunks,
                Digest,
                DigestReady,
                Checks,
                Archive,
                Close,
                Done
            }

            /// <summary>
            /// An encode of the package <see cref="WritePackage(RbxWorldPackagePayload, RbxWorldPackageLimits)"/>
            /// writes. <paramref name="validate"/> false skips only the payload validation, for a payload that passed
            /// <see cref="TryProveEncodable"/> (which validates exactly as the writer does) and was not changed since.
            /// </summary>
            internal static IncrementalEncoder ForPackage(
                RbxWorldPackagePayload payload,
                RbxWorldPackageLimits limits,
                bool validate = true)
            {
                if (limits == null)
                {
                    throw new ArgumentNullException(nameof(limits));
                }

                return new IncrementalEncoder(payload, limits, validate, null);
            }

            /// <summary>
            /// An encode that stops at <see cref="ComputeEntryDigest"/> of <paramref name="payload"/> with its capture
            /// time replaced by <paramref name="digestCapturedAtUtc"/> (the payload's own when null), and may then
            /// go on to <paramref name="payload"/>'s package with <see cref="ContinueWithPackage"/>, reusing every
            /// entry but the manifest.
            /// </summary>
            internal static IncrementalEncoder ForEntryDigest(
                RbxWorldPackagePayload payload,
                DateTime? digestCapturedAtUtc = null)
            {
                if (payload == null)
                {
                    throw new ArgumentNullException(nameof(payload));
                }

                return new IncrementalEncoder(
                    payload,
                    null,
                    true,
                    digestCapturedAtUtc.HasValue ? payload.WithCapturedAtUtc(digestCapturedAtUtc.Value) : payload);
            }

            /// <summary>Units run so far (tests read it to bound a step).</summary>
            internal int UnitsDone { get; private set; }

            /// <summary>The phase the next unit belongs to (benchmarks name the longest unit by it).</summary>
            internal string NextUnitPhase => _phase.ToString();

            /// <summary>True once the package is encoded.</summary>
            internal bool IsComplete => _phase == Phase.Done;

            /// <summary>True once the entry digest is computed; a package encode never computes one.</summary>
            internal bool IsDigestReady => _digest != null;

            /// <summary>The encoded package; null until <see cref="IsComplete"/>.</summary>
            internal byte[] Result => _result;

            /// <summary>The entry digest; null until <see cref="IsDigestReady"/>.</summary>
            internal byte[] EntryDigest => _digest;

            /// <summary>The payload this encode writes.</summary>
            internal RbxWorldPackagePayload Payload => _payload;

            /// <summary>
            /// The host clock when this encode's last slice ended, 0 before the first: kept with the encode by
            /// <see cref="RbxWorldPackageEncodeFrames"/>, so the package after a digest paces on from it.
            /// </summary>
            internal long LastSliceEndTimestamp { get; set; }

            /// <summary>
            /// True once a slow frame or a request for the encode at once was met: every later slice of this encode
            /// runs to its end (<see cref="RbxWorldPackageEncodeFrames"/>).
            /// </summary>
            internal bool FinishesAtOnce { get; set; }

            /// <summary>
            /// Turns a finished digest encode into the encode of its payload's package held to
            /// <paramref name="limits"/>: the entry and expanded-size checks run first, in the writer's order, then
            /// the ZIP. The payload was validated by the digest.
            /// </summary>
            internal void ContinueWithPackage(RbxWorldPackageLimits limits)
            {
                if (limits == null)
                {
                    throw new ArgumentNullException(nameof(limits));
                }

                if (_failed || _phase != Phase.DigestReady)
                {
                    throw new InvalidOperationException("Only a finished entry digest can go on to its package.");
                }

                _limits = limits;
                _phase = Phase.Checks;
            }

            /// <summary>
            /// Runs at least one unit, then more until <paramref name="overBudget"/> answers true; true once the encode
            /// is done (the package, or for a digest encode the digest). A refusal throws what the one-block method
            /// throws, and the encode cannot be stepped again.
            /// </summary>
            internal bool Step(Func<bool> overBudget)
            {
                if (overBudget == null)
                {
                    throw new ArgumentNullException(nameof(overBudget));
                }

                if (_failed)
                {
                    throw new InvalidOperationException("This encode failed and cannot continue.");
                }

                while (!IsStopped)
                {
                    try
                    {
                        RunUnit();
                    }
                    catch
                    {
                        _failed = true;
                        Release();
                        throw;
                    }

                    UnitsDone++;
                    if (IsStopped)
                    {
                        break;
                    }

                    if (overBudget())
                    {
                        return false;
                    }
                }

                return true;
            }

            /// <summary>Runs every unit at once; the one-block methods are this.</summary>
            internal void RunToEnd()
            {
                Step(Never);
            }

            public void Dispose()
            {
                Release();
            }

            private bool IsStopped => _phase == Phase.Done || _phase == Phase.DigestReady;

            private static bool Never()
            {
                return false;
            }

            private void RunUnit()
            {
                switch (_phase)
                {
                    case Phase.Validate:
                        ValidatePayload(_payload, ClassCatalog.CreateMvp1());
                        _phase = Phase.Manifest;
                        break;
                    case Phase.Manifest:
                        WriteManifests();
                        break;
                    case Phase.WorldJson:
                        WriteWorldJson();
                        break;
                    case Phase.Mods:
                        WriteMod();
                        break;
                    case Phase.TerrainStart:
                        StartTerrain();
                        break;
                    case Phase.TerrainChunks:
                        WriteTerrainChunk();
                        break;
                    case Phase.Digest:
                        HashSlice();
                        break;
                    case Phase.Checks:
                        CheckEveryEntry();
                        break;
                    case Phase.Archive:
                        DeflateSlice();
                        break;
                    case Phase.Close:
                        CloseArchive();
                        break;
                    default:
                        throw new InvalidOperationException("The encode has no unit left in phase " + _phase + ".");
                }
            }

            private bool ChecksInline => _limits != null && _digestManifestPayload == null;

            private void WriteManifests()
            {
                if (_digestManifestPayload != null)
                {
                    _digestManifest = BuildManifest(_digestManifestPayload);
                    _digestManifestBytes = SerializeJson(_digestManifest);
                }
                else
                {
                    _manifest = BuildManifest(_payload);
                    _manifestBytes = SerializeJson(_manifest);
                    ValidateWritableEntry(ManifestEntryName, _manifestBytes.LongLength, _limits);
                }

                _world = new RbxWorldPackageWorldJson.ResumableWriter(_payload);
                _phase = Phase.WorldJson;
            }

            private void WriteWorldJson()
            {
                if (!_world.WriteSome(WorldJsonInstancesPerUnit))
                {
                    return;
                }

                _worldBytes = _world.Finish();
                _world = null;
                _worldCount = (int)_worldBytes.Length;
                if (ChecksInline)
                {
                    CheckWorld();
                }

                _modManifests = new byte[_payload.Mods.Count][];
                _modSources = new byte[_payload.Mods.Count][];
                _modIndex = 0;
                _phase = _payload.Mods.Count > 0 ? Phase.Mods : Phase.TerrainStart;
            }

            private void WriteMod()
            {
                RbxWorldModSource mod = _payload.Mods[_modIndex];
                _modManifests[_modIndex] = SerializeJson(mod.Manifest);
                _modSources[_modIndex] = StrictUtf8.GetBytes(ReplaceLoneSurrogates(mod.Source));
                if (ChecksInline)
                {
                    CheckMod(_modIndex);
                }

                _modIndex++;
                if (_modIndex == _payload.Mods.Count)
                {
                    _phase = Phase.TerrainStart;
                }
            }

            private void StartTerrain()
            {
                if (!HasTerrainCells(_payload))
                {
                    FinishEntries();
                    return;
                }

                _terrainStore = new TerrainChunkStore();
                _terrainStore.RestoreSnapshot(_payload.Terrain);
                _terrainCoords = new List<TerrainChunkCoord>(_terrainStore.ChunkCount);
                _terrainStore.CopyChunkCoords(_terrainCoords);
                _terrainOutput = new MemoryStream(64 + _terrainCoords.Count * 64);
                TerrainVoxelCodec.WriteHeader(_terrainOutput, _terrainCoords.Count);
                _terrainIndex = 0;
                _phase = Phase.TerrainChunks;
                if (_terrainCoords.Count == 0)
                {
                    FinishTerrain();
                }
            }

            private void WriteTerrainChunk()
            {
                _terrainStore.TryGetChunk(_terrainCoords[_terrainIndex], out TerrainChunk chunk);
                TerrainVoxelCodec.WriteChunk(_terrainOutput, chunk);
                _terrainIndex++;
                if (_terrainIndex == _terrainCoords.Count)
                {
                    FinishTerrain();
                }
            }

            private void FinishTerrain()
            {
                _terrainBytes = _terrainOutput.ToArray();
                _terrainOutput.Dispose();
                _terrainOutput = null;
                _terrainStore = null;
                _terrainCoords = null;
                if (ChecksInline)
                {
                    CheckTerrain();
                }

                FinishEntries();
            }

            /// <summary>Every entry is built: the digest is hashed next, or the ZIP is written.</summary>
            private void FinishEntries()
            {
                if (_digestManifestPayload != null)
                {
                    _entries = ListEntries(_digestManifestBytes);
                    _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                    _entryIndex = 0;
                    _entryOffset = -1;
                    _phase = Phase.Digest;
                    return;
                }

                StartArchive();
            }

            private List<EntryBytes> ListEntries(byte[] manifestBytes)
            {
                List<EntryBytes> entries = new(3 + 2 * _payload.Mods.Count)
                {
                    new EntryBytes(ManifestEntryName, manifestBytes, manifestBytes.Length),
                    new EntryBytes(WorldEntryName, _worldBytes)
                };
                PackageManifestDto names = _manifest ?? _digestManifest;
                for (int index = 0; index < _payload.Mods.Count; index++)
                {
                    PackageModIndexDto modIndex = names.Mods[index];
                    entries.Add(new EntryBytes(modIndex.ManifestEntry, _modManifests[index], _modManifests[index].Length));
                    entries.Add(new EntryBytes(modIndex.SourceEntry, _modSources[index], _modSources[index].Length));
                }

                if (_terrainBytes != null)
                {
                    entries.Add(new EntryBytes(TerrainEntryName, _terrainBytes, _terrainBytes.Length));
                }

                return entries;
            }

            /// <summary>One slice of <see cref="ComputeEntryDigest"/>: an entry's header, or up to a slice of its bytes.</summary>
            private void HashSlice()
            {
                EntryBytes entry = _entries[_entryIndex];
                if (_entryOffset < 0)
                {
                    _hash.AppendData(StrictUtf8.GetBytes(
                        entry.Name + "\n" + entry.Count.ToString(CultureInfo.InvariantCulture) + "\n"));
                    _entryOffset = 0;
                }

                int slice = entry.Slice(_entryOffset, DigestSliceBytes, out byte[] array, out int arrayOffset);
                if (slice > 0)
                {
                    _hash.AppendData(array, arrayOffset, slice);
                    _entryOffset += slice;
                }

                if (_entryOffset < entry.Count)
                {
                    return;
                }

                _entryIndex++;
                _entryOffset = -1;
                if (_entryIndex < _entries.Count)
                {
                    return;
                }

                _digest = _hash.GetHashAndReset();
                _hash.Dispose();
                _hash = null;
                _entries = null;
                _phase = Phase.DigestReady;
            }

            /// <summary>The checks the writer makes as it builds the entries, all at once for entries already built.</summary>
            private void CheckEveryEntry()
            {
                _manifest = BuildManifest(_payload);
                _manifestBytes = SerializeJson(_manifest);
                ValidateWritableEntry(ManifestEntryName, _manifestBytes.LongLength, _limits);
                CheckWorld();
                for (int index = 0; index < _payload.Mods.Count; index++)
                {
                    CheckMod(index);
                }

                if (_terrainBytes != null)
                {
                    CheckTerrain();
                }

                StartArchive();
            }

            private void CheckWorld()
            {
                _expandedBytes = _manifestBytes.LongLength + _worldCount;
                ValidateWritableEntry(WorldEntryName, _worldCount, _limits);
                ValidateExpandedSize(_expandedBytes, _limits);
            }

            private void CheckMod(int index)
            {
                PackageModIndexDto modIndex = _manifest.Mods[index];
                ValidateWritableEntry(modIndex.ManifestEntry, _modManifests[index].LongLength, _limits);
                ValidateWritableEntry(modIndex.SourceEntry, _modSources[index].Length, _limits);
                _expandedBytes += _modManifests[index].LongLength + _modSources[index].Length;
                ValidateExpandedSize(_expandedBytes, _limits);
            }

            private void CheckTerrain()
            {
                ValidateWritableEntry(TerrainEntryName, _terrainBytes.LongLength, _limits);
                _expandedBytes += _terrainBytes.LongLength;
                ValidateExpandedSize(_expandedBytes, _limits);
            }

            private void StartArchive()
            {
                _entries = ListEntries(_manifestBytes);
                _output = new MemoryStream();
                _archive = new ZipArchive(_output, ZipArchiveMode.Create, true);
                _entryIndex = 0;
                _entryOffset = 0;
                _phase = Phase.Archive;
            }

            /// <summary>
            /// One slice of the ZIP, as <c>WriteEntry</c> writes it: the entry is created and opened before its first
            /// slice and closed after its last, and its bytes reach deflate a slice at a time.
            /// </summary>
            private void DeflateSlice()
            {
                EntryBytes entry = _entries[_entryIndex];
                if (_entryStream == null)
                {
                    ValidateWritableEntry(entry.Name, entry.Count, _limits);
                    ZipArchiveEntry created = _archive.CreateEntry(entry.Name, CompressionLevel.Optimal);
                    created.LastWriteTime = StableZipTimestamp;
                    _entryStream = created.Open();
                    _entryOffset = 0;
                }

                int slice = entry.Slice(_entryOffset, DeflateSliceBytes, out byte[] array, out int arrayOffset);
                _entryStream.Write(array, arrayOffset, slice);
                _entryOffset += slice;
                if (_entryOffset < entry.Count)
                {
                    return;
                }

                _entryStream.Dispose();
                _entryStream = null;
                _entryIndex++;
                if (_entryIndex == _entries.Count)
                {
                    _phase = Phase.Close;
                }
            }

            private void CloseArchive()
            {
                _archive.Dispose();
                _archive = null;
                _entries = null;
                _worldBytes = null;
                _modManifests = null;
                _modSources = null;
                _terrainBytes = null;
                if (_output.Length > _limits.MaximumPackageBytes)
                {
                    throw new RbxWorldPackageException(
                        "World package is " + _output.Length + " bytes; format version 1 limit is "
                        + _limits.MaximumPackageBytes + " bytes.");
                }

                _result = _output.ToArray();
                _output.Dispose();
                _output = null;
                _phase = Phase.Done;
            }

            private void Release()
            {
                _world?.Dispose();
                _world = null;
                _terrainOutput?.Dispose();
                _terrainOutput = null;
                _hash?.Dispose();
                _hash = null;
                try
                {
                    _entryStream?.Dispose();
                    _archive?.Dispose();
                }
                catch (Exception)
                {
                    // WHY swallowed: the encode is abandoned, and closing a half-written ZIP may itself fail; the
                    // bytes are thrown away either way.
                }

                _entryStream = null;
                _archive = null;
                _output?.Dispose();
                _output = null;
                _entries = null;
                _worldBytes = null;
                _modManifests = null;
                _modSources = null;
                _terrainBytes = null;
            }

            /// <summary>One entry's bytes, in one array or in segments.</summary>
            private readonly struct EntryBytes
            {
                private readonly byte[] _bytes;
                private readonly RbxWorldPackageSegmentedBytes _segments;

                public EntryBytes(string name, byte[] bytes, int count)
                {
                    Name = name;
                    _bytes = bytes;
                    _segments = null;
                    Count = count;
                }

                public EntryBytes(string name, RbxWorldPackageSegmentedBytes segments)
                {
                    Name = name;
                    _bytes = null;
                    _segments = segments;
                    Count = (int)segments.Length;
                }

                public string Name { get; }

                public int Count { get; }

                /// <summary>
                /// The bytes from <paramref name="offset"/>, at most <paramref name="maximum"/> of them and never past the
                /// end of a segment; <paramref name="maximum"/> divides the segment size, so a slice is cut at the same
                /// offsets as from one array.
                /// </summary>
                public int Slice(int offset, int maximum, out byte[] array, out int arrayOffset)
                {
                    int remaining = Count - offset;
                    if (_segments == null)
                    {
                        array = _bytes;
                        arrayOffset = offset;
                        return Math.Min(maximum, remaining);
                    }

                    int segment = offset / RbxWorldPackageSegmentedBytes.SegmentBytes;
                    arrayOffset = offset % RbxWorldPackageSegmentedBytes.SegmentBytes;
                    array = _segments.Segment(segment);
                    return Math.Min(Math.Min(maximum, remaining), RbxWorldPackageSegmentedBytes.SegmentBytes - arrayOffset);
                }
            }
        }
    }

    /// <summary>
    /// A write-only stream that keeps what it is given in segments of <see cref="SegmentBytes"/>: an entry that grows
    /// to tens of megabytes never copies itself into a bigger array, and no segment is large enough for the large
    /// object heap.
    /// </summary>
    internal sealed class RbxWorldPackageSegmentedBytes : Stream
    {
        /// <summary>Bytes of every segment but the last; a multiple of every slice an encode cuts.</summary>
        internal const int SegmentBytes = 256 * 1024;

        private readonly List<byte[]> _segments = new();
        private int _lastCount = SegmentBytes;
        private long _length;

        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => _length;

        public override long Position
        {
            get => _length;
            set => throw new NotSupportedException();
        }

        /// <summary>The segment at <paramref name="index"/>; only its first bytes up to <see cref="Length"/> are written.</summary>
        internal byte[] Segment(int index)
        {
            return _segments[index];
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            if (buffer == null)
            {
                throw new ArgumentNullException(nameof(buffer));
            }

            while (count > 0)
            {
                if (_lastCount == SegmentBytes)
                {
                    _segments.Add(new byte[SegmentBytes]);
                    _lastCount = 0;
                }

                int copied = Math.Min(count, SegmentBytes - _lastCount);
                Buffer.BlockCopy(buffer, offset, _segments[_segments.Count - 1], _lastCount, copied);
                _lastCount += copied;
                _length += copied;
                offset += copied;
                count -= copied;
            }
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            throw new NotSupportedException();
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            throw new NotSupportedException();
        }

        public override void SetLength(long value)
        {
            throw new NotSupportedException();
        }
    }

    /// <summary>
    /// Runs an <see cref="RbxWorldPackageSerializer.IncrementalEncoder"/> a slice of work per frame, measured with the
    /// host clock (<see cref="Stopwatch.GetTimestamp"/>, which every player has, WebGL included). A slice stops at the
    /// first unit that ends past its budget, and the budget follows the frames that actually come:
    /// <list type="bullet">
    /// <item>the gap is the time from the end of the encode's previous slice to the start of this one (the rest of
    /// the frame: the game, the render, a wait for vsync or a throttled timer; the encode's own work is not in it, so
    /// a long unit never feeds back into the next budget);</item>
    /// <item>the budget is the store's budget or a third of the gap, whichever is more: at most about a quarter of the
    /// frame, the store's 4 ms at 60 fps;</item>
    /// <item>a gap of <see cref="FinishAtOnceGap"/> or more (a hidden or throttled tab, below 4 fps, where no player
    /// sees a hitch) runs all the remaining work in that slice, and so does every later slice of the encode, its
    /// package after a digest included;</item>
    /// <item>an <see cref="RbxWorldPackageEncodeUrgency"/> that is requested (someone waits for the encode) does the
    /// same, and ends the wait for the next frame at once;</item>
    /// <item>a budget of zero runs exactly one unit a slice whatever the frames (tests).</item>
    /// </list>
    /// </summary>
    internal static class RbxWorldPackageEncodeFrames
    {
        /// <summary>The work an encode may do in one frame unless its store was given another budget.</summary>
        internal static readonly TimeSpan DefaultFrameBudget = TimeSpan.FromMilliseconds(4d);

        /// <summary>A gap between two slices at least this long makes the encode finish in its slice.</summary>
        internal static readonly TimeSpan FinishAtOnceGap = TimeSpan.FromMilliseconds(250d);

        /// <summary>The budget is at least the gap divided by this (a slice of a quarter of the frame).</summary>
        internal const int GapShareDivisor = 3;

        /// <summary>
        /// Steps <paramref name="encoder"/> until it is done, awaiting <paramref name="waitForNextFrameAsync"/>
        /// between slices (and before the first one when <paramref name="waitBeforeFirstSlice"/>, whose gap is then
        /// measured too); a refusal or a cancellation leaves the encoder released.
        /// </summary>
        /// <param name="clock">The host clock in <see cref="Stopwatch"/> ticks; tests pass a fake one.</param>
        internal static async UniTask RunAsync(
            RbxWorldPackageSerializer.IncrementalEncoder encoder,
            TimeSpan frameBudget,
            Func<CancellationToken, UniTask> waitForNextFrameAsync,
            CancellationToken cancellationToken,
            bool waitBeforeFirstSlice = false,
            RbxWorldPackageEncodeUrgency urgency = null,
            Func<long> clock = null)
        {
            if (encoder == null)
            {
                throw new ArgumentNullException(nameof(encoder));
            }

            Func<CancellationToken, UniTask> wait = waitForNextFrameAsync ?? WaitForNextFrameAsync;
            Pacer pacer = new(encoder, frameBudget, clock ?? Stopwatch.GetTimestamp, urgency);
            try
            {
                if (waitBeforeFirstSlice)
                {
                    pacer.EndSlice();
                    await WaitAsync(wait, urgency, cancellationToken);
                }

                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    pacer.BeginSlice();
                    bool done = encoder.Step(pacer.IsSpent);
                    pacer.EndSlice();
                    if (done)
                    {
                        return;
                    }

                    await WaitAsync(wait, urgency, cancellationToken);
                }
            }
            catch
            {
                encoder.Dispose();
                throw;
            }
        }

        /// <summary>
        /// Until the next player-loop frame in a running player (a frame later than the caller's, which a plain
        /// yield does not promise); none where no player loop runs (Edit Mode, the engine-free build), whose
        /// callers must not wait for a frame that never comes, so there the whole encode runs at once.
        /// </summary>
        internal static UniTask WaitForNextFrameAsync(CancellationToken token)
        {
#if UNITY_EDITOR
            bool loopRunning;
            try
            {
                loopRunning = UnityEngine.Application.isPlaying;
            }
            catch (Exception)
            {
                loopRunning = false;
            }

            return loopRunning ? UniTask.NextFrame(PlayerLoopTiming.Update, token) : UniTask.CompletedTask;
#elif UNITY_5_3_OR_NEWER
            return UniTask.NextFrame(PlayerLoopTiming.Update, token);
#else
            return UniTask.CompletedTask;
#endif
        }

        /// <summary>The frame wait, ended early by a request for the encode at once.</summary>
        private static async UniTask WaitAsync(
            Func<CancellationToken, UniTask> wait,
            RbxWorldPackageEncodeUrgency urgency,
            CancellationToken cancellationToken)
        {
            if (urgency == null)
            {
                await wait(cancellationToken);
                return;
            }

            if (urgency.IsRequested)
            {
                return;
            }

            await UniTask.WhenAny(wait(cancellationToken), urgency.Requested);
        }

        private sealed class Pacer
        {
            private readonly RbxWorldPackageSerializer.IncrementalEncoder _encoder;
            private readonly long _budgetTicks;
            private readonly long _finishGapTicks;
            private readonly Func<long> _clock;
            private readonly RbxWorldPackageEncodeUrgency _urgency;
            private long _sliceStart;
            private long _sliceBudget;
            private bool _unbounded;

            public Pacer(
                RbxWorldPackageSerializer.IncrementalEncoder encoder,
                TimeSpan budget,
                Func<long> clock,
                RbxWorldPackageEncodeUrgency urgency)
            {
                _encoder = encoder;
                _budgetTicks = budget <= TimeSpan.Zero ? 0L : (long)(budget.TotalSeconds * Stopwatch.Frequency);
                _finishGapTicks = (long)(FinishAtOnceGap.TotalSeconds * Stopwatch.Frequency);
                _clock = clock;
                _urgency = urgency;
                IsSpent = () => !_unbounded && _clock() - _sliceStart >= _sliceBudget;
            }

            public Func<bool> IsSpent { get; }

            public void BeginSlice()
            {
                long now = _clock();
                _sliceStart = now;
                if (_urgency != null && _urgency.IsRequested)
                {
                    _encoder.FinishesAtOnce = true;
                }

                if (_encoder.FinishesAtOnce)
                {
                    _unbounded = true;
                    return;
                }

                _unbounded = false;
                _sliceBudget = _budgetTicks;
                long previousEnd = _encoder.LastSliceEndTimestamp;
                if (_budgetTicks == 0L || previousEnd == 0L)
                {
                    return;
                }

                long gap = now - previousEnd;
                if (gap >= _finishGapTicks)
                {
                    _encoder.FinishesAtOnce = true;
                    _unbounded = true;
                    return;
                }

                _sliceBudget = Math.Max(_budgetTicks, gap / GapShareDivisor);
            }

            public void EndSlice()
            {
                long now = _clock();
                _encoder.LastSliceEndTimestamp = now == 0L ? 1L : now;
            }
        }
    }

    /// <summary>
    /// Asks an encode spread over frames to finish at once: the next mutation of the world waits for the backup
    /// written behind the previous one, so the player is waiting already and a hitch is better than more frames.
    /// </summary>
    internal sealed class RbxWorldPackageEncodeUrgency
    {
        private readonly UniTaskCompletionSource _requested = new();

        /// <summary>True once <see cref="Request"/> was called.</summary>
        internal bool IsRequested { get; private set; }

        /// <summary>Completes when <see cref="Request"/> is called.</summary>
        internal UniTask Requested => _requested.Task;

        /// <summary>
        /// Requests the rest of the encode at once: an encode that waits for its next frame resumes now and runs to
        /// its end in that slice. Idempotent.
        /// </summary>
        internal void Request()
        {
            if (IsRequested)
            {
                return;
            }

            IsRequested = true;
            _requested.TrySetResult();
        }
    }
}
