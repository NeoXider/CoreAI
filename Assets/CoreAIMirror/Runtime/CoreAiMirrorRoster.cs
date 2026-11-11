using System;
using System.Collections.Generic;
using System.Text;
using CoreAI.Authority;

namespace CoreAI.Net.Mirror
{
    /// <summary>One member of the room: who joined, and whether it is the host's own player.</summary>
    public readonly struct CoreAiRosterMember
    {
        /// <summary>Creates a member.</summary>
        public CoreAiRosterMember(uint memberId, long userId, string name, string displayName,
            bool isHost, string actorId)
        {
            MemberId = memberId;
            UserId = userId;
            Name = name ?? "";
            DisplayName = displayName ?? "";
            IsHost = isHost;
            ActorId = actorId;
        }

        /// <summary>The server's key for this member, unique within the server's run.</summary>
        public uint MemberId { get; }

        /// <summary>The admitted UserId.</summary>
        public long UserId { get; }

        /// <summary>The admitted name.</summary>
        public string Name { get; }

        /// <summary>The admitted display name.</summary>
        public string DisplayName { get; }

        /// <summary>Whether this member is the host's own player.</summary>
        public bool IsHost { get; }

        /// <summary>The actor the member was admitted as; on the server only, null on a client.</summary>
        public string ActorId { get; }
    }

    /// <summary>
    /// The server's list of who is in the room, one member per admitted actor, in join order; the
    /// session host keeps it and publishes its changes to every ready client.
    /// </summary>
    /// <remarks>
    /// WHY per actor and not per connection: a player who reconnects before the old link timed out
    /// keeps its Player, so it keeps its place in the room too — no leave and join for a supersede.
    /// </remarks>
    public sealed class CoreAiMirrorRoster
    {
        /// <summary>UTF-8 bytes of a name or display name sent at most; a longer one is cut.</summary>
        public const int MaxNameBytes = 64;

        /// <summary>Entries one roster message carries at most; a larger snapshot is split.</summary>
        public const int MaxEntriesPerMessage = 64;

        /// <summary>
        /// Wire bytes of one entry at most: the member id and UserId varints, two strings with their
        /// headers, and the host flag.
        /// </summary>
        public const int MaxEntryWireBytes = 5 + 10 + (2 + MaxNameBytes) * 2 + 1;

        /// <summary>Wire bytes of a roster message besides its entries, at most.</summary>
        public const int EnvelopeWireBytes = 1 + 5 + 5 + 5;

        private readonly List<CoreAiRosterMember> _members = new();
        private uint _lastMemberId;

        /// <summary>Who is in the room, in join order.</summary>
        public IReadOnlyList<CoreAiRosterMember> Members => _members;

        /// <summary>Bumped by every join, leave and change; a snapshot carries it.</summary>
        public uint Revision { get; private set; }

        /// <summary>The member an actor is, when it is in the room.</summary>
        public bool TryGetMember(string actorId, out CoreAiRosterMember member)
        {
            int index = IndexOf(actorId);
            member = index >= 0 ? _members[index] : default;
            return index >= 0;
        }

        /// <summary>
        /// Adds an admitted actor, or updates the member it already is; true when the roster
        /// changed, with the member as it now reads.
        /// </summary>
        internal bool TryAddOrUpdate(string actorId, ActorAdmissionResult admission, bool isHost,
            out CoreAiRosterMember member)
        {
            string name = CutToBytes(admission?.Name, MaxNameBytes);
            string displayName = CutToBytes(admission?.DisplayName, MaxNameBytes);
            long userId = admission?.UserId ?? 0L;
            int index = IndexOf(actorId);
            if (index >= 0)
            {
                CoreAiRosterMember current = _members[index];
                if (current.UserId == userId && current.IsHost == isHost
                    && string.Equals(current.Name, name, StringComparison.Ordinal)
                    && string.Equals(current.DisplayName, displayName, StringComparison.Ordinal))
                {
                    member = current;
                    return false;
                }

                member = new CoreAiRosterMember(current.MemberId, userId, name, displayName, isHost, actorId);
                _members[index] = member;
                Revision++;
                return true;
            }

            _lastMemberId = checked(_lastMemberId + 1u);
            member = new CoreAiRosterMember(_lastMemberId, userId, name, displayName, isHost, actorId);
            _members.Add(member);
            Revision++;
            return true;
        }

        /// <summary>Removes an actor's member; true when it was in the room.</summary>
        internal bool TryRemove(string actorId, out CoreAiRosterMember member)
        {
            int index = IndexOf(actorId);
            if (index < 0)
            {
                member = default;
                return false;
            }

            member = _members[index];
            _members.RemoveAt(index);
            Revision++;
            return true;
        }

        /// <summary>The message that tells ready clients one member joined, changed or left.</summary>
        internal CoreAiRosterMessage Delta(CoreAiRosterChange change, CoreAiRosterMember member)
        {
            return new CoreAiRosterMessage
            {
                Change = (byte)change,
                Revision = Revision,
                SelfMemberId = 0u,
                Entries = new[] { ToEntry(member) }
            };
        }

        /// <summary>
        /// The whole roster for one recipient, as one snapshot message and as many continuations as
        /// <paramref name="maxEntriesPerMessage"/> needs.
        /// </summary>
        internal List<CoreAiRosterMessage> SnapshotFor(string recipientActorId, int maxEntriesPerMessage)
        {
            int perMessage = Math.Max(1, maxEntriesPerMessage);
            uint self = TryGetMember(recipientActorId, out CoreAiRosterMember recipient) ? recipient.MemberId : 0u;
            List<CoreAiRosterMessage> messages = new();
            int offset = 0;
            do
            {
                int count = Math.Min(perMessage, _members.Count - offset);
                CoreAiRosterEntry[] entries = new CoreAiRosterEntry[count];
                for (int index = 0; index < count; index++)
                {
                    entries[index] = ToEntry(_members[offset + index]);
                }

                messages.Add(new CoreAiRosterMessage
                {
                    Change = (byte)(offset == 0 ? CoreAiRosterChange.Snapshot : CoreAiRosterChange.SnapshotContinued),
                    Revision = Revision,
                    SelfMemberId = self,
                    Entries = entries
                });
                offset += count;
            }
            while (offset < _members.Count);

            return messages;
        }

        /// <summary>
        /// <paramref name="text"/> cut to at most <paramref name="maxBytes"/> UTF-8 bytes after the
        /// last whole character that fits; null reads as empty.
        /// </summary>
        internal static string CutToBytes(string text, int maxBytes)
        {
            if (string.IsNullOrEmpty(text) || maxBytes <= 0)
            {
                return "";
            }

            if (Encoding.UTF8.GetByteCount(text) <= maxBytes)
            {
                return text;
            }

            int bytes = 0;
            int length = 0;
            while (length < text.Length)
            {
                bool pair = char.IsHighSurrogate(text[length]) && length + 1 < text.Length
                                                              && char.IsLowSurrogate(text[length + 1]);
                int width = pair ? 4 : Encoding.UTF8.GetByteCount(text.ToCharArray(length, 1));
                if (bytes + width > maxBytes)
                {
                    break;
                }

                bytes += width;
                length += pair ? 2 : 1;
            }

            return text.Substring(0, length);
        }

        private static CoreAiRosterEntry ToEntry(CoreAiRosterMember member)
        {
            return new CoreAiRosterEntry
            {
                MemberId = member.MemberId,
                UserId = member.UserId,
                Name = member.Name,
                DisplayName = member.DisplayName,
                IsHost = member.IsHost
            };
        }

        private int IndexOf(string actorId)
        {
            if (string.IsNullOrEmpty(actorId))
            {
                return -1;
            }

            for (int index = 0; index < _members.Count; index++)
            {
                if (string.Equals(_members[index].ActorId, actorId, StringComparison.Ordinal))
                {
                    return index;
                }
            }

            return -1;
        }
    }

    /// <summary>
    /// A client's copy of the server's roster, built from the roster messages the server sends it.
    /// </summary>
    public sealed class CoreAiMirrorRosterReplica
    {
        /// <summary>Members a replica holds at most; a message that would pass it is refused.</summary>
        public const int MaxMembers = 4096;

        private readonly List<CoreAiRosterMember> _members = new();

        /// <summary>Who the server says is in the room, in its order; empty until the first snapshot.</summary>
        public IReadOnlyList<CoreAiRosterMember> Members => _members;

        /// <summary>The revision of the last message applied.</summary>
        public uint Revision { get; private set; }

        /// <summary>This client's own member id, from the last snapshot; zero when it is not a member.</summary>
        public uint SelfMemberId { get; private set; }

        /// <summary>Whether a snapshot has arrived on this connection.</summary>
        public bool HasSnapshot { get; private set; }

        /// <summary>
        /// Joins and leaves whose revision did not follow the one before; they are applied anyway,
        /// and a count above zero means a message was lost or reordered.
        /// </summary>
        public int RevisionGaps { get; private set; }

        /// <summary>This client's own member, when the snapshot named it.</summary>
        public bool TryGetSelf(out CoreAiRosterMember self)
        {
            for (int index = 0; index < _members.Count; index++)
            {
                if (SelfMemberId != 0u && _members[index].MemberId == SelfMemberId)
                {
                    self = _members[index];
                    return true;
                }
            }

            self = default;
            return false;
        }

        /// <summary>
        /// Applies one roster message; false, and nothing changed, when it is malformed — an unknown
        /// change, a continuation with no snapshot before it, or one that would pass
        /// <see cref="MaxMembers"/>.
        /// </summary>
        internal bool TryApply(CoreAiRosterMessage message)
        {
            CoreAiRosterEntry[] entries = message.Entries ?? Array.Empty<CoreAiRosterEntry>();
            switch ((CoreAiRosterChange)message.Change)
            {
                case CoreAiRosterChange.Snapshot:
                    if (entries.Length > MaxMembers)
                    {
                        return false;
                    }

                    _members.Clear();
                    AddAll(entries);
                    Revision = message.Revision;
                    SelfMemberId = message.SelfMemberId;
                    HasSnapshot = true;
                    return true;
                case CoreAiRosterChange.SnapshotContinued:
                    if (!HasSnapshot || message.Revision != Revision || _members.Count + entries.Length > MaxMembers)
                    {
                        return false;
                    }

                    AddAll(entries);
                    return true;
                case CoreAiRosterChange.Joined:
                    if (_members.Count + entries.Length > MaxMembers)
                    {
                        return false;
                    }

                    CountGap(message.Revision);
                    for (int index = 0; index < entries.Length; index++)
                    {
                        Upsert(entries[index]);
                    }

                    return true;
                case CoreAiRosterChange.Left:
                    CountGap(message.Revision);
                    for (int index = 0; index < entries.Length; index++)
                    {
                        RemoveMember(entries[index].MemberId);
                    }

                    return true;
                default:
                    return false;
            }
        }

        /// <summary>Forgets the roster, for a client whose connection changed.</summary>
        internal void Clear()
        {
            _members.Clear();
            Revision = 0u;
            SelfMemberId = 0u;
            HasSnapshot = false;
        }

        private void CountGap(uint revision)
        {
            if (!HasSnapshot || revision != unchecked(Revision + 1u))
            {
                RevisionGaps++;
            }

            Revision = revision;
        }

        private void AddAll(CoreAiRosterEntry[] entries)
        {
            for (int index = 0; index < entries.Length; index++)
            {
                Upsert(entries[index]);
            }
        }

        private void Upsert(CoreAiRosterEntry entry)
        {
            CoreAiRosterMember member = new(entry.MemberId, entry.UserId,
                CoreAiMirrorRoster.CutToBytes(entry.Name, CoreAiMirrorRoster.MaxNameBytes),
                CoreAiMirrorRoster.CutToBytes(entry.DisplayName, CoreAiMirrorRoster.MaxNameBytes),
                entry.IsHost, null);
            for (int index = 0; index < _members.Count; index++)
            {
                if (_members[index].MemberId == entry.MemberId)
                {
                    _members[index] = member;
                    return;
                }
            }

            _members.Add(member);
        }

        private void RemoveMember(uint memberId)
        {
            for (int index = _members.Count - 1; index >= 0; index--)
            {
                if (_members[index].MemberId == memberId)
                {
                    _members.RemoveAt(index);
                }
            }
        }
    }
}
