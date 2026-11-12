using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using CoreAI.Mods.Rbx.Instances.Terrain;

namespace CoreAI.Mods.WorldPackages
{
    /// <summary>
    /// The change journal of a world kept between starts: the Terrain writes made after its newest startup entry, each
    /// kept as the cells of the changed box as they are after the write, appended to <c>&lt;entry&gt;.journal</c> next
    /// to that entry within a frame of the write. A start opens the entry and replays its journal over the entry's
    /// Terrain; the next full record of the world takes the journal's place (<see cref="Rebase"/>).
    /// </summary>
    /// <remarks>
    /// WHY next to the entry and absolute: a block is a few dozen bytes on disk at once, while a record of the whole
    /// world waits for the player to stop and takes many frames. Records hold values, not operations, so a write
    /// replayed twice in its order gives the same cells, and a write made while a record encodes ends up in that
    /// package or in the journal after it.
    /// WHY only chunks the entry holds: a world generator may fill the land the entry lacks again at the next start
    /// (one that fills only empty cells puts back a block the player broke there), so a write there waits for the
    /// full record, which then holds that land.
    /// File layout: 8 header bytes ("CAIJ", version 1, three zero bytes), then records of a little-endian u32 length,
    /// a u32 CRC-32 of the payload and the payload: kind 1, the box's minimum cell (three i32), its size (three u16),
    /// its materials and its occupancy, one byte per cell each.
    /// </remarks>
    internal sealed class RbxKeptWorldJournal
    {
        /// <summary>The file extension of a journal, after its entry's ten-digit sequence.</summary>
        internal const string Extension = ".journal";

        /// <summary>The most cells one journaled write may change; a larger write waits for the full record.</summary>
        internal const int MaximumRecordCells = 4096;

        /// <summary>
        /// The most bytes a journal holds before writes wait for the full record.
        /// WHY small: the browser's storage writes every changed file whole after each frame that changed it.
        /// </summary>
        internal const long MaximumBytes = 256L * 1024L;

        internal const int HeaderBytes = 8;
        internal const int RecordFramingBytes = 8;
        private const int BoxBytes = 19;
        private const byte TerrainBoxKind = 1;
        private const string TemporarySuffix = ".tmp";

        private static readonly byte[] Header = { (byte)'C', (byte)'A', (byte)'I', (byte)'J', 1, 0, 0, 0 };
        private static readonly uint[] CrcTable = BuildCrcTable();

        private readonly object _lock = new();
        private readonly List<Entry> _recent = new();
        private readonly byte[] _materials = new byte[MaximumRecordCells];
        private readonly byte[] _occupancy = new byte[MaximumRecordCells];
        private string _directory;
        private int _baseSequence;
        private TerrainSnapshot _baseTerrain;
        private int _firstUnwritten;
        private long _fileBytes;
        private long _unwrittenBytes;
        private long _position;
        private bool _full;

        /// <summary>True while writes are journaled: there is an entry to extend and nothing stopped the journal.</summary>
        internal bool IsActive
        {
            get
            {
                lock (_lock)
                {
                    return _baseSequence > 0;
                }
            }
        }

        /// <summary>The sequence of the startup entry the journal extends; 0 while it is not active.</summary>
        internal int BaseSequence
        {
            get
            {
                lock (_lock)
                {
                    return _baseSequence;
                }
            }
        }

        /// <summary>The position of the newest journaled write; a capture taken now holds every write up to it.</summary>
        internal long Position
        {
            get
            {
                lock (_lock)
                {
                    return _position;
                }
            }
        }

        /// <summary>True while a journaled write is not yet in the file.</summary>
        internal bool HasUnwritten
        {
            get
            {
                lock (_lock)
                {
                    return _firstUnwritten < _recent.Count;
                }
            }
        }

        /// <summary>The bytes of the journal file as this journal last wrote it.</summary>
        internal long FileBytes
        {
            get
            {
                lock (_lock)
                {
                    return _fileBytes;
                }
            }
        }

        /// <summary>The path of the journal of entry <paramref name="sequence"/> in <paramref name="directory"/>.</summary>
        internal static string PathOf(string directory, int sequence)
        {
            return Path.Combine(directory, sequence.ToString("D10", CultureInfo.InvariantCulture) + Extension);
        }

        /// <summary>
        /// Starts journaling the writes made after entry <paramref name="baseSequence"/>, whose Terrain is
        /// <paramref name="baseTerrain"/> and whose journal file already holds <paramref name="fileBytes"/> bytes.
        /// </summary>
        internal void Begin(string directory, int baseSequence, TerrainSnapshot baseTerrain, long fileBytes)
        {
            lock (_lock)
            {
                bool usable = !string.IsNullOrEmpty(directory) && baseSequence > 0 && baseTerrain != null;
                _directory = usable ? directory : null;
                _baseSequence = usable ? baseSequence : 0;
                _baseTerrain = usable ? baseTerrain : null;
                _fileBytes = usable ? Math.Max(0L, fileBytes) : 0L;
                ClearRecentLocked();
            }
        }

        /// <summary>
        /// Stops journaling until the next <see cref="Rebase"/> (the world was replaced, its selection cleared, or its
        /// file could not be written); writes made meanwhile wait for the full record. The files stay.
        /// </summary>
        internal void Suspend()
        {
            lock (_lock)
            {
                _baseSequence = 0;
                _baseTerrain = null;
                ClearRecentLocked();
            }
        }

        /// <summary>
        /// Keeps the cells <paramref name="changes"/> left in <paramref name="store"/> as the newest journaled write;
        /// false when this write cannot be journaled (journal not active, a reset, a box past
        /// <see cref="MaximumRecordCells"/>, a chunk the entry does not hold, or the journal full), and then the
        /// caller leaves it to the full record. Reads the store, never writes it.
        /// </summary>
        internal bool TryRecord(TerrainChangeSet changes, TerrainChunkStore store)
        {
            if (changes == null || store == null || changes.IsReset)
            {
                return false;
            }

            lock (_lock)
            {
                if (_baseSequence <= 0 || _full)
                {
                    return false;
                }

                TerrainCellBox box = changes.ChangedCells;
                if (box.IsEmpty || box.CellCount > MaximumRecordCells
                                || box.SizeX > ushort.MaxValue || box.SizeY > ushort.MaxValue
                                || box.SizeZ > ushort.MaxValue)
                {
                    return false;
                }

                IReadOnlyList<TerrainChunkCoord> chunks = changes.ChangedChunks;
                for (int index = 0; index < chunks.Count; index++)
                {
                    if (!_baseTerrain.TryGetChunk(chunks[index], out _))
                    {
                        return false;
                    }
                }

                int cells = (int)box.CellCount;
                int length = BoxBytes + 2 * cells;
                long header = _fileBytes >= HeaderBytes ? 0L : HeaderBytes;
                if (_fileBytes + header + _unwrittenBytes + RecordFramingBytes + length > MaximumBytes)
                {
                    _full = true;
                    return false;
                }

                store.ReadBox(box, _materials, _occupancy);
                byte[] record = new byte[RecordFramingBytes + length];
                int offset = RecordFramingBytes;
                record[offset++] = TerrainBoxKind;
                offset = WriteInt32(record, offset, box.MinX);
                offset = WriteInt32(record, offset, box.MinY);
                offset = WriteInt32(record, offset, box.MinZ);
                offset = WriteUInt16(record, offset, box.SizeX);
                offset = WriteUInt16(record, offset, box.SizeY);
                offset = WriteUInt16(record, offset, box.SizeZ);
                Buffer.BlockCopy(_materials, 0, record, offset, cells);
                Buffer.BlockCopy(_occupancy, 0, record, offset + cells, cells);
                WriteUInt32(record, 0, (uint)length);
                WriteUInt32(record, 4, Crc32(record, RecordFramingBytes, length));
                _position++;
                _recent.Add(new Entry(_position, record));
                _unwrittenBytes += record.Length;
                return true;
            }
        }

        /// <summary>
        /// Appends the journaled writes not yet in the file, in one open and close; the bytes appended, 0 when there
        /// was nothing to append. A failure suspends the journal (<see cref="Suspend"/>) and throws, so the caller
        /// leaves those writes to the full record.
        /// </summary>
        internal int Flush()
        {
            string path;
            int from;
            long knownBytes;
            List<byte[]> records = new();
            lock (_lock)
            {
                if (_baseSequence <= 0 || _firstUnwritten >= _recent.Count)
                {
                    return 0;
                }

                path = PathOf(_directory, _baseSequence);
                from = _firstUnwritten;
                knownBytes = _fileBytes;
                for (int index = from; index < _recent.Count; index++)
                {
                    records.Add(_recent[index].Bytes);
                }
            }

            int written = 0;
            long length;
            try
            {
                // WHY the file is checked too: a journal deleted under this page (another page of the game, a cleared
                // folder) must start again with its header, or the records appended to it could never be read.
                bool fresh = knownBytes < HeaderBytes || !File.Exists(path);
                using (FileStream stream = new(path, fresh ? FileMode.Create : FileMode.Append, FileAccess.Write,
                           FileShare.Read))
                {
                    if (fresh)
                    {
                        stream.Write(Header, 0, Header.Length);
                        written += Header.Length;
                    }

                    foreach (byte[] record in records)
                    {
                        stream.Write(record, 0, record.Length);
                        written += record.Length;
                    }

                    length = stream.Length;
                }
            }
            catch (Exception)
            {
                Suspend();
                throw;
            }

            lock (_lock)
            {
                if (_firstUnwritten == from && _recent.Count >= from + records.Count)
                {
                    for (int index = 0; index < records.Count; index++)
                    {
                        _unwrittenBytes -= records[index].Length;
                    }

                    _firstUnwritten = from + records.Count;
                    _fileBytes = length;
                }
            }

            return written;
        }

        /// <summary>
        /// Moves the journal onto entry <paramref name="newSequence"/>, which was captured when the journal stood at
        /// <paramref name="capturedThrough"/>: the writes journaled after that capture become the new entry's journal,
        /// every other journal file in <paramref name="directory"/> is deleted, and journaling goes on over
        /// <paramref name="newBase"/>. A failure suspends the journal and throws.
        /// </summary>
        internal void Rebase(string directory, int newSequence, long capturedThrough, TerrainSnapshot newBase)
        {
            List<Entry> tail = new();
            lock (_lock)
            {
                for (int index = 0; index < _recent.Count; index++)
                {
                    if (_recent[index].Position > capturedThrough)
                    {
                        tail.Add(_recent[index]);
                    }
                }
            }

            string path = PathOf(directory, newSequence);
            long bytes = 0L;
            try
            {
                if (tail.Count > 0)
                {
                    string temporary = path + TemporarySuffix;
                    using (FileStream stream = new(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
                    {
                        stream.Write(Header, 0, Header.Length);
                        bytes += Header.Length;
                        foreach (Entry entry in tail)
                        {
                            stream.Write(entry.Bytes, 0, entry.Bytes.Length);
                            bytes += entry.Bytes.Length;
                        }
                    }

                    if (File.Exists(path))
                    {
                        File.Delete(path);
                    }

                    File.Move(temporary, path);
                }

                DeleteJournals(directory, path);
            }
            catch (Exception)
            {
                Suspend();
                throw;
            }

            lock (_lock)
            {
                _directory = directory;
                _baseSequence = newBase != null ? newSequence : 0;
                _baseTerrain = newBase;
                ClearRecentLocked();
                _recent.AddRange(tail);
                _firstUnwritten = _recent.Count;
                _fileBytes = bytes;
            }
        }

        /// <summary>Deletes every journal file in <paramref name="directory"/> but <paramref name="keptPath"/> (null: all).</summary>
        internal static void DeleteJournals(string directory, string keptPath)
        {
            if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
            {
                return;
            }

            foreach (string file in Directory.GetFiles(directory, "*" + Extension + "*"))
            {
                bool journal = file.EndsWith(Extension, StringComparison.OrdinalIgnoreCase)
                               || file.EndsWith(Extension + TemporarySuffix, StringComparison.OrdinalIgnoreCase);
                if (journal && !string.Equals(file, keptPath, StringComparison.OrdinalIgnoreCase))
                {
                    File.Delete(file);
                }
            }
        }

        /// <summary>
        /// Replays the journal of entry <paramref name="sequence"/> over <paramref name="terrain"/> (the entry's own
        /// cells) in its order. A record that cannot be read ends the replay there: the records before it are applied,
        /// the rest is reported, and the file is cut back to the readable part so later appends stay readable. Never
        /// throws; the entry itself is never touched.
        /// </summary>
        internal static RbxKeptWorldJournalReplay Replay(string directory, int sequence, TerrainSnapshot terrain)
        {
            if (string.IsNullOrEmpty(directory) || sequence <= 0)
            {
                return new RbxKeptWorldJournalReplay(terrain, 0, 0L, "");
            }

            string path = PathOf(directory, sequence);
            byte[] bytes;
            try
            {
                string leftover = path + TemporarySuffix;
                if (File.Exists(leftover))
                {
                    File.Delete(leftover);
                }

                if (!File.Exists(path))
                {
                    return new RbxKeptWorldJournalReplay(terrain, 0, 0L, "");
                }

                bytes = File.ReadAllBytes(path);
            }
            catch (Exception ex)
            {
                return new RbxKeptWorldJournalReplay(terrain, 0, 0L, "the change journal could not be read: " + ex.Message);
            }

            if (bytes.Length < HeaderBytes || !StartsWithHeader(bytes))
            {
                TryCut(path, 0);
                return new RbxKeptWorldJournalReplay(terrain, 0, 0L,
                    "the change journal (" + bytes.Length.ToString(CultureInfo.InvariantCulture)
                    + " bytes) is not a journal this release writes, so none of it was applied");
            }

            TerrainChunkStore store = new();
            if (terrain != null)
            {
                store.RestoreSnapshot(terrain);
            }

            int offset = HeaderBytes;
            int applied = 0;
            string problem = "";
            byte[] materials = new byte[MaximumRecordCells];
            byte[] occupancy = new byte[MaximumRecordCells];
            while (offset < bytes.Length)
            {
                problem = TryApply(bytes, offset, store, materials, occupancy, out int recordBytes);
                if (problem.Length > 0)
                {
                    break;
                }

                applied++;
                offset += recordBytes;
            }

            if (problem.Length > 0)
            {
                problem = "the change journal was read up to byte " + offset.ToString(CultureInfo.InvariantCulture)
                          + " of " + bytes.Length.ToString(CultureInfo.InvariantCulture) + " (" + applied.ToString(CultureInfo.InvariantCulture)
                          + " changes applied): " + problem;
                TryCut(path, offset);
            }

            TerrainSnapshot replayed = applied > 0 ? store.CaptureSnapshot() : terrain;
            return new RbxKeptWorldJournalReplay(replayed, applied, offset, problem);
        }

        private static string TryApply(byte[] bytes, int offset, TerrainChunkStore store, byte[] materials,
            byte[] occupancy, out int recordBytes)
        {
            recordBytes = 0;
            if (bytes.Length - offset < RecordFramingBytes)
            {
                return "it ends inside a record";
            }

            uint length = ReadUInt32(bytes, offset);
            uint crc = ReadUInt32(bytes, offset + 4);
            if (length < BoxBytes || length > (uint)(bytes.Length - offset - RecordFramingBytes))
            {
                return "a record is cut short";
            }

            int payload = offset + RecordFramingBytes;
            if (Crc32(bytes, payload, (int)length) != crc)
            {
                return "a record's checksum does not match";
            }

            if (bytes[payload] != TerrainBoxKind)
            {
                return "a record of an unknown kind";
            }

            int minX = ReadInt32(bytes, payload + 1);
            int minY = ReadInt32(bytes, payload + 5);
            int minZ = ReadInt32(bytes, payload + 9);
            int sizeX = ReadUInt16(bytes, payload + 13);
            int sizeY = ReadUInt16(bytes, payload + 15);
            int sizeZ = ReadUInt16(bytes, payload + 17);
            long cells = (long)sizeX * sizeY * sizeZ;
            if (cells == 0L || cells > MaximumRecordCells || length != BoxBytes + 2L * cells)
            {
                return "a record's box does not match its size";
            }

            Buffer.BlockCopy(bytes, payload + BoxBytes, materials, 0, (int)cells);
            Buffer.BlockCopy(bytes, payload + BoxBytes + (int)cells, occupancy, 0, (int)cells);
            try
            {
                store.WriteBox(new TerrainCellBox(minX, minY, minZ, sizeX, sizeY, sizeZ), materials, occupancy);
            }
            catch (Exception ex)
            {
                return "a record could not be applied (" + ex.Message + ")";
            }

            recordBytes = RecordFramingBytes + (int)length;
            return "";
        }

        private static void TryCut(string path, int keptBytes)
        {
            try
            {
                if (keptBytes < HeaderBytes)
                {
                    File.Delete(path);
                    return;
                }

                using FileStream stream = new(path, FileMode.Open, FileAccess.Write, FileShare.None);
                stream.SetLength(keptBytes);
            }
            catch (Exception)
            {
            }
        }

        private void ClearRecentLocked()
        {
            _recent.Clear();
            _firstUnwritten = 0;
            _unwrittenBytes = 0L;
            _full = false;
        }

        private static bool StartsWithHeader(byte[] bytes)
        {
            for (int index = 0; index < 5; index++)
            {
                if (bytes[index] != Header[index])
                {
                    return false;
                }
            }

            return true;
        }

        private static int WriteInt32(byte[] buffer, int offset, int value)
        {
            WriteUInt32(buffer, offset, unchecked((uint)value));
            return offset + 4;
        }

        private static int WriteUInt16(byte[] buffer, int offset, int value)
        {
            buffer[offset] = (byte)value;
            buffer[offset + 1] = (byte)(value >> 8);
            return offset + 2;
        }

        private static void WriteUInt32(byte[] buffer, int offset, uint value)
        {
            buffer[offset] = (byte)value;
            buffer[offset + 1] = (byte)(value >> 8);
            buffer[offset + 2] = (byte)(value >> 16);
            buffer[offset + 3] = (byte)(value >> 24);
        }

        private static uint ReadUInt32(byte[] buffer, int offset)
        {
            return buffer[offset] | ((uint)buffer[offset + 1] << 8) | ((uint)buffer[offset + 2] << 16)
                   | ((uint)buffer[offset + 3] << 24);
        }

        private static int ReadInt32(byte[] buffer, int offset)
        {
            return unchecked((int)ReadUInt32(buffer, offset));
        }

        private static int ReadUInt16(byte[] buffer, int offset)
        {
            return buffer[offset] | (buffer[offset + 1] << 8);
        }

        private static uint[] BuildCrcTable()
        {
            uint[] table = new uint[256];
            for (uint index = 0; index < 256; index++)
            {
                uint value = index;
                for (int bit = 0; bit < 8; bit++)
                {
                    value = (value & 1) != 0 ? 0xEDB88320u ^ (value >> 1) : value >> 1;
                }

                table[index] = value;
            }

            return table;
        }

        /// <summary>The CRC-32 (IEEE) of <paramref name="count"/> bytes of <paramref name="buffer"/> from <paramref name="offset"/>.</summary>
        internal static uint Crc32(byte[] buffer, int offset, int count)
        {
            uint crc = 0xFFFFFFFFu;
            for (int index = offset; index < offset + count; index++)
            {
                crc = CrcTable[(crc ^ buffer[index]) & 0xFF] ^ (crc >> 8);
            }

            return ~crc;
        }

        private readonly struct Entry
        {
            public Entry(long position, byte[] bytes)
            {
                Position = position;
                Bytes = bytes;
            }

            public long Position { get; }

            public byte[] Bytes { get; }
        }
    }

    /// <summary>What <see cref="RbxKeptWorldJournal.Replay"/> applied.</summary>
    internal readonly struct RbxKeptWorldJournalReplay
    {
        public RbxKeptWorldJournalReplay(TerrainSnapshot terrain, int applied, long readableBytes, string problem)
        {
            Terrain = terrain;
            Applied = applied;
            ReadableBytes = readableBytes;
            Problem = problem ?? "";
        }

        /// <summary>The entry's Terrain with the journal applied; the entry's own when nothing was applied.</summary>
        public TerrainSnapshot Terrain { get; }

        /// <summary>How many journaled writes were applied.</summary>
        public int Applied { get; }

        /// <summary>The bytes of the journal file that were read and kept (the file's length after the replay).</summary>
        public long ReadableBytes { get; }

        /// <summary>Why a part of the journal could not be applied; empty when all of it was.</summary>
        public string Problem { get; }
    }
}
