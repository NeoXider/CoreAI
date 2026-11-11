using System;
using System.Collections.Generic;

namespace CoreAI.Ai.SubAgents
{
    /// <summary>An immutable view of one queued player instruction.</summary>
    public sealed class QueuedInstructionSnapshot
    {
        internal QueuedInstructionSnapshot(string id, string text, int position)
        {
            Id = id;
            Text = text;
            Position = position;
        }

        /// <summary>Queue-local id (<c>q1</c>, <c>q2</c>, ...).</summary>
        public string Id { get; }

        /// <summary>The instruction exactly as the player wrote it.</summary>
        public string Text { get; }

        /// <summary>0 for the instruction that runs next.</summary>
        public int Position { get; }
    }

    /// <summary>The answer to <see cref="PlayerInstructionQueue.Enqueue"/>.</summary>
    public sealed class InstructionQueueResult
    {
        internal InstructionQueueResult(bool accepted, QueuedInstructionSnapshot entry, string refusal)
        {
            Accepted = accepted;
            Entry = entry;
            Refusal = refusal ?? "";
        }

        /// <summary>True when the instruction joined the queue.</summary>
        public bool Accepted { get; }

        /// <summary>The queued entry; <c>null</c> when refused.</summary>
        public QueuedInstructionSnapshot Entry { get; }

        /// <summary>Why the instruction was refused; empty when accepted.</summary>
        public string Refusal { get; }
    }

    /// <summary>
    /// Player instructions that arrived while a plan was running. The queue holds text only and has no
    /// reference to a plan, a request or a sub-agent: a queued instruction cannot reach work already in
    /// progress, it only becomes the next plan once the current one ends. The player may move an entry to the
    /// front (<see cref="Promote"/>) or drop it (<see cref="Cancel"/>). Thread-safe;
    /// <see cref="Changed"/> is raised outside the lock.
    /// </summary>
    public sealed class PlayerInstructionQueue
    {
        /// <summary>Refusal text for a full queue.</summary>
        public const string QueueFullRefusal = "queue_full";

        /// <summary>Refusal text for a blank instruction.</summary>
        public const string EmptyInstructionRefusal = "empty_instruction";

        private readonly object _lock = new();
        private readonly List<Entry> _entries = new();
        private readonly int _capacity;
        private long _nextOrdinal;

        /// <summary>Creates a queue holding at most <paramref name="capacity"/> instructions (at least 1).</summary>
        public PlayerInstructionQueue(int capacity)
        {
            _capacity = capacity < 1 ? 1 : capacity;
        }

        /// <summary>Raised after every change, outside the lock, on the thread that made it.</summary>
        public event Action Changed;

        /// <summary>Most instructions the queue holds.</summary>
        public int Capacity => _capacity;

        /// <summary>Instructions waiting now.</summary>
        public int Count
        {
            get
            {
                lock (_lock)
                {
                    return _entries.Count;
                }
            }
        }

        /// <summary>The instruction that runs next, or <c>null</c> when the queue is empty.</summary>
        public QueuedInstructionSnapshot Next
        {
            get
            {
                lock (_lock)
                {
                    return _entries.Count == 0 ? null : ToSnapshot(_entries[0], 0);
                }
            }
        }

        /// <summary>Adds <paramref name="text"/> at the back, or refuses it when blank or when the queue is full.</summary>
        public InstructionQueueResult Enqueue(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return new InstructionQueueResult(false, null, EmptyInstructionRefusal);
            }

            QueuedInstructionSnapshot snapshot;
            lock (_lock)
            {
                if (_entries.Count >= _capacity)
                {
                    return new InstructionQueueResult(false, null, QueueFullRefusal);
                }

                _nextOrdinal++;
                Entry entry = new() { Id = "q" + _nextOrdinal, Text = text };
                _entries.Add(entry);
                snapshot = ToSnapshot(entry, _entries.Count - 1);
            }

            RaiseChanged();
            return new InstructionQueueResult(true, snapshot, "");
        }

        /// <summary>Moves the instruction <paramref name="id"/> to the front; returns false when it is not queued.</summary>
        public bool Promote(string id)
        {
            bool moved;
            lock (_lock)
            {
                int index = IndexOfLocked(id);
                moved = index > 0;
                if (moved)
                {
                    Entry entry = _entries[index];
                    _entries.RemoveAt(index);
                    _entries.Insert(0, entry);
                }
                else if (index < 0)
                {
                    return false;
                }
            }

            if (moved)
            {
                RaiseChanged();
            }

            return true;
        }

        /// <summary>Drops the instruction <paramref name="id"/>; returns false when it is not queued.</summary>
        public bool Cancel(string id)
        {
            lock (_lock)
            {
                int index = IndexOfLocked(id);
                if (index < 0)
                {
                    return false;
                }

                _entries.RemoveAt(index);
            }

            RaiseChanged();
            return true;
        }

        /// <summary>Drops every queued instruction and returns them, so a UI can offer to restore them.</summary>
        public IReadOnlyList<QueuedInstructionSnapshot> CancelAll()
        {
            List<QueuedInstructionSnapshot> dropped;
            lock (_lock)
            {
                dropped = SnapshotLocked();
                _entries.Clear();
            }

            if (dropped.Count > 0)
            {
                RaiseChanged();
            }

            return dropped.AsReadOnly();
        }

        /// <summary>Removes and returns the instruction that runs next.</summary>
        public bool TryDequeue(out QueuedInstructionSnapshot next)
        {
            lock (_lock)
            {
                if (_entries.Count == 0)
                {
                    next = null;
                    return false;
                }

                next = ToSnapshot(_entries[0], 0);
                _entries.RemoveAt(0);
            }

            RaiseChanged();
            return true;
        }

        /// <summary>Every queued instruction, front first.</summary>
        public IReadOnlyList<QueuedInstructionSnapshot> Snapshot()
        {
            lock (_lock)
            {
                return SnapshotLocked().AsReadOnly();
            }
        }

        private List<QueuedInstructionSnapshot> SnapshotLocked()
        {
            List<QueuedInstructionSnapshot> list = new(_entries.Count);
            for (int i = 0; i < _entries.Count; i++)
            {
                list.Add(ToSnapshot(_entries[i], i));
            }

            return list;
        }

        private int IndexOfLocked(string id)
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                return -1;
            }

            string trimmed = id.Trim();
            for (int i = 0; i < _entries.Count; i++)
            {
                if (string.Equals(_entries[i].Id, trimmed, StringComparison.Ordinal))
                {
                    return i;
                }
            }

            return -1;
        }

        private static QueuedInstructionSnapshot ToSnapshot(Entry entry, int position)
        {
            return new QueuedInstructionSnapshot(entry.Id, entry.Text, position);
        }

        private void RaiseChanged()
        {
            Changed?.Invoke();
        }

        private sealed class Entry
        {
            public string Id;
            public string Text;
        }
    }
}
