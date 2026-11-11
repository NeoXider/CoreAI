using System;
using System.Collections.Generic;

namespace CoreAI.Ai.Npc
{
    /// <summary>
    /// An <see cref="INpcListenerPosition"/> a scope registers once while the source arrives later: a game world
    /// that loads after the NPC block (a child scope, a world reload) attaches the local player's position here.
    /// The most recent live attachment answers; disposing it hands back to the one before. With none attached the
    /// position is unknown and the distance rules are not enforced for chat, as without a listener.
    /// </summary>
    public sealed class NpcListenerPositionRelay : INpcListenerPosition
    {
        private readonly List<Attachment> _sources = new();

        /// <summary>True while at least one source is attached.</summary>
        public bool HasSource => _sources.Count > 0;

        /// <inheritdoc />
        public NpcPosition? Current
        {
            get
            {
                if (_sources.Count == 0)
                {
                    return null;
                }

                try
                {
                    return _sources[_sources.Count - 1].Source.Current;
                }
                catch (Exception)
                {
                    return null;
                }
            }
        }

        /// <summary>Makes <paramref name="source"/> answer until the handle is disposed.</summary>
        public IDisposable Attach(INpcListenerPosition source)
        {
            if (source == null)
            {
                throw new ArgumentNullException(nameof(source));
            }

            if (ReferenceEquals(source, this))
            {
                throw new ArgumentException("A relay cannot be attached to itself.", nameof(source));
            }

            Attachment attachment = new(this, source);
            _sources.Add(attachment);
            return attachment;
        }

        private sealed class Attachment : IDisposable
        {
            private NpcListenerPositionRelay _owner;

            public Attachment(NpcListenerPositionRelay owner, INpcListenerPosition source)
            {
                _owner = owner;
                Source = source;
            }

            public INpcListenerPosition Source { get; }

            public void Dispose()
            {
                NpcListenerPositionRelay owner = _owner;
                _owner = null;
                owner?._sources.Remove(this);
            }
        }
    }
}
