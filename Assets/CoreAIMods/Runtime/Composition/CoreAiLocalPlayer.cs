using CoreAI.Ai.LuaCs;
using CoreAI.Authority;
using CoreAI.Mods.Rbx.Instances;
using CoreAI.Mods.Rbx.Instances.Networking;

namespace CoreAI.Composition
{
    /// <summary>
    /// The player at this screen in a solo world. A network transport admits remote players itself, but nothing
    /// admits the local one, so the play world connects it into every published world (the first one and each one
    /// a load or an undo replaces it with); its character then loads where the world spawns characters.
    /// </summary>
    public static class CoreAiLocalPlayer
    {
        /// <summary>
        /// Makes <paramref name="actor"/> this screen's player in <paramref name="world"/>: names it the local player
        /// (camera, clicks) and admits it, which creates its Player and, with <c>CharacterAutoLoads</c>, its
        /// character. Idempotent. Returns the player, or null without a world or a trusted actor.
        /// </summary>
        public static RbxPlayer Connect(LuaCsRbxApiBindings world, ActorContext actor)
        {
            if (world == null || !actor.IsTrusted)
            {
                return null;
            }

            world.LocalPlayerActorId = actor.ActorId;
            return world.ConnectActor(actor);
        }

        /// <summary>The local player's Humanoid in <paramref name="world"/>, or null while there is none.</summary>
        public static RbxHumanoid FindHumanoid(LuaCsRbxApiBindings world)
        {
            if (world == null)
            {
                return null;
            }

            RbxPlayer player = null;
            string actorId = world.LocalPlayerActorId;
            if (!string.IsNullOrEmpty(actorId))
            {
                world.Players.TryGetByActorId(actorId, out player);
            }

            player ??= world.Players.GetSoleListedPlayer();
            RbxInstance character = player != null && !player.IsDestroyed ? player.Character : null;
            if (character == null || character.IsDestroyed)
            {
                return null;
            }

            return character.FindFirstChildOfClass("Humanoid") as RbxHumanoid;
        }
    }
}
