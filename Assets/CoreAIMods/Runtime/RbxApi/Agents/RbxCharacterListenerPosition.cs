using CoreAI.Ai.Npc;
using CoreAI.Mods.Rbx.Datatypes;

namespace CoreAI.Mods.Rbx.Agents
{
    /// <summary>
    /// Where the local player stands in a Lua world, for the NPC block: the live position of the player's character
    /// in studs, the unit the NPCs of a mod are placed in, so the player's chat lines are held to each NPC's
    /// hearing radius. Unknown (null) while the player has no character or no world is loaded, and the distance
    /// rules are then not enforced, as the NPC guide describes.
    /// </summary>
    public sealed class RbxCharacterListenerPosition : INpcListenerPosition
    {
        private readonly RbxCharacterSubject _player;

        /// <summary>Follows the local player of <paramref name="world"/> (the one with <paramref name="localActorId"/>, or the solo player).</summary>
        public RbxCharacterListenerPosition(IRbxCharacterWorld world, string localActorId = "")
        {
            _player = RbxCharacterSubject.LocalPlayer(world, localActorId);
        }

        /// <inheritdoc />
        public NpcPosition? Current
        {
            get
            {
                if (!_player.TryResolve(out RbxCharacterView view) || !view.Position.HasValue)
                {
                    return null;
                }

                RbxVector3 studs = view.Position.Value;
                return new NpcPosition(studs.X, studs.Y, studs.Z);
            }
        }
    }
}
