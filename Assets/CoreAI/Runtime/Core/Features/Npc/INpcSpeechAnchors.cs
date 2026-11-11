using System;
using System.Collections.Generic;

namespace CoreAI.Ai.Npc
{
    /// <summary>
    /// Where each NPC's speech bubble is drawn: a point in the renderer's world space (above the head). The bubble
    /// view asks every frame, so a walking NPC keeps its bubble; an NPC with no anchor gets no bubble (its line
    /// still reaches the chat).
    /// </summary>
    public interface INpcSpeechAnchors
    {
        /// <summary>The bubble anchor of <paramref name="npcId"/> now; false when it has none.</summary>
        bool TryGetAnchor(string npcId, out NpcPosition worldPosition);
    }

    /// <summary>
    /// Where the local player stands, in the same world units as the NPC positions, so chat lines the player says
    /// are held to the NPCs' hearing radius. Without one the distance rules are not enforced for chat.
    /// </summary>
    public interface INpcListenerPosition
    {
        /// <summary>The local player's position now; null when unknown.</summary>
        NpcPosition? Current { get; }
    }

    /// <summary>
    /// A registry of bubble anchors fed by the game: one delegate per NPC id, read every frame on the view's thread.
    /// Several sources can be chained with <see cref="Fallback"/> (a host registry first, a mod's bodies second).
    /// </summary>
    public sealed class NpcSpeechAnchorRegistry : INpcSpeechAnchors
    {
        private readonly Dictionary<string, Func<NpcPosition?>> _anchors = new(StringComparer.Ordinal);

        /// <summary>Asked for ids this registry does not hold; null for none.</summary>
        public INpcSpeechAnchors Fallback { get; set; }

        /// <summary>Number of registered anchors.</summary>
        public int Count => _anchors.Count;

        /// <summary>Registers (or replaces) the anchor of <paramref name="npcId"/>; a null source removes it.</summary>
        public void Register(string npcId, Func<NpcPosition?> anchor)
        {
            string id = (npcId ?? "").Trim();
            if (id.Length == 0)
            {
                throw new ArgumentException("An anchor needs a non-empty NPC id.", nameof(npcId));
            }

            if (anchor == null)
            {
                _anchors.Remove(id);
                return;
            }

            _anchors[id] = anchor;
        }

        /// <summary>Removes the anchor of <paramref name="npcId"/>; false when there was none.</summary>
        public bool Unregister(string npcId)
        {
            return _anchors.Remove((npcId ?? "").Trim());
        }

        /// <inheritdoc />
        public bool TryGetAnchor(string npcId, out NpcPosition worldPosition)
        {
            if (_anchors.TryGetValue(npcId ?? "", out Func<NpcPosition?> anchor))
            {
                NpcPosition? position = null;
                try
                {
                    position = anchor();
                }
                catch (Exception)
                {
                    position = null;
                }

                if (position.HasValue)
                {
                    worldPosition = position.Value;
                    return true;
                }

                worldPosition = default;
                return false;
            }

            INpcSpeechAnchors fallback = Fallback;
            if (fallback != null && !ReferenceEquals(fallback, this))
            {
                return fallback.TryGetAnchor(npcId, out worldPosition);
            }

            worldPosition = default;
            return false;
        }
    }
}
