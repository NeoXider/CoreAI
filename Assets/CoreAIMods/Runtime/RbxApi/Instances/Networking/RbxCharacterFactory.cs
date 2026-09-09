using System;

namespace CoreAI.Mods.Rbx.Instances.Networking
{
    /// <summary>
    /// Builds and replaces a player's character: the minimum Roblox shape of a Model holding a
    /// Humanoid and a HumanoidRootPart, parented into the world.
    /// </summary>
    /// <remarks>
    /// WHY a factory rather than a method on <see cref="RbxPlayers"/>: loading a character needs the
    /// registry and the world root, which the Players service does not hold, and the same build has
    /// to run from three places — a join with <c>CharacterAutoLoads</c>, an explicit
    /// <c>LoadCharacterAsync</c>, and a host that spawns a character itself.
    /// <para>
    /// WHY the character carries no appearance, accessories or animation: those need a rig CoreAI
    /// does not model, and every one of them is a loud stub on the Humanoid. What ships is what a
    /// script can actually use — a Humanoid to damage and move, and a root part to position.
    /// </para>
    /// </remarks>
    public static class RbxCharacterFactory
    {
        /// <summary>The mirror's name for the part a Humanoid drives.</summary>
        public const string RootPartName = "HumanoidRootPart";

        /// <summary>
        /// Loads (or reloads) <paramref name="player"/>'s character, firing CharacterRemoving for
        /// the outgoing one and CharacterAdded for the new one, and returns the new Model.
        /// </summary>
        /// <remarks>
        /// WHY the old character is destroyed rather than detached: Roblox's respawn replaces it,
        /// and a detached model left in the registry is a leak a script cannot see or reach.
        /// </remarks>
        public static RbxInstance Load(InstanceRegistry registry, RbxInstance worldRoot,
            RbxPlayer player)
        {
            if (registry == null)
            {
                throw new ArgumentNullException(nameof(registry));
            }

            if (worldRoot == null)
            {
                throw new ArgumentNullException(nameof(worldRoot));
            }

            if (player == null)
            {
                throw new ArgumentNullException(nameof(player));
            }

            if (player.IsDestroyed || worldRoot.IsDestroyed ||
                !ReferenceEquals(player.Registry, registry) ||
                !ReferenceEquals(worldRoot.Registry, registry))
            {
                throw new ArgumentException("Player and world root must be live instances in the supplied registry.");
            }

            string actorId = player.NetworkActorId;
            RbxInstance character = registry.Create(
                "Model",
                ownerActorId: actorId,
                accessScope: InstanceAccessScope.Owned,
                isRuntimeInfrastructure: true);
            try
            {
                character.Name = player.Name;

                RbxInstance rootPart = registry.Create(
                    "Part",
                    ownerActorId: actorId,
                    accessScope: InstanceAccessScope.Owned,
                    isRuntimeInfrastructure: true);
                rootPart.Name = RootPartName;
                rootPart.Parent = character;

                RbxInstance humanoid = registry.Create(
                    "Humanoid",
                    ownerActorId: actorId,
                    accessScope: InstanceAccessScope.Owned,
                    isRuntimeInfrastructure: true);
                humanoid.Parent = character;

                // WHY the model is parented last: parenting is what materializes the subtree in the
                // world, and a Model that entered the world before its Humanoid existed would be
                // observed by a ChildAdded handler in a shape no finished character ever has.
                character.Parent = worldRoot;
                Unload(player);
                player.Character = character;
                player.CharacterAdded.Fire(character);
                return character;
            }
            catch
            {
                character.Destroy();
                throw;
            }
        }

        /// <summary>
        /// Fires CharacterRemoving and destroys the player's current character, if any. Safe to
        /// call when there is none.
        /// </summary>
        public static void Unload(RbxPlayer player)
        {
            if (player?.Character == null)
            {
                return;
            }

            RbxInstance outgoing = player.Character;
            player.Character = null;
            if (!outgoing.IsDestroyed)
            {
                player.CharacterRemoving.FireForDestruction(outgoing, outgoing);
                outgoing.Destroy();
            }
        }
    }
}
