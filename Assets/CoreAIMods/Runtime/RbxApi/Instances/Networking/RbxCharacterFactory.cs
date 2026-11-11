using System;
using CoreAI.Mods.Rbx.Datatypes;
using CoreAI.Mods.Rbx.Instances.CharacterRig;

namespace CoreAI.Mods.Rbx.Instances.Networking
{
    /// <summary>
    /// Builds and replaces a player's character, parented into the world: the classic R6 body (a Model
    /// with <c>HumanoidRootPart</c>, <c>Torso</c>, <c>Head</c>, <c>Right Arm</c>, <c>Left Arm</c>,
    /// <c>Right Leg</c>, <c>Left Leg</c> and a <c>Humanoid</c>, PrimaryPart the root) when the world can seed
    /// part state, otherwise the minimum Roblox shape of a Humanoid and a HumanoidRootPart.
    /// </summary>
    /// <remarks>
    /// WHY a factory rather than a method on <see cref="RbxPlayers"/>: loading a character needs the
    /// registry and the world root, which the Players service does not hold, and the same build has
    /// to run from three places - a join with <c>CharacterAutoLoads</c>, an explicit
    /// <c>LoadCharacterAsync</c>, and a host that spawns a character itself.
    /// <para>
    /// The body is real instances, so a script finds <c>character.Head</c> and recolours it like any Part;
    /// <see cref="RbxCharacterRigs"/> animates it from the Humanoid, as it animates any Model with those
    /// names. The root keeps the 2x2x1 collidable box the controls are tuned on and is invisible; the other
    /// parts do not collide (see <see cref="RbxR6Rig.SeedFor"/>). Appearance loading, accessories, joints and
    /// animation tracks stay loud stubs.
    /// </para>
    /// </remarks>
    public static class RbxCharacterFactory
    {
        /// <summary>The mirror's name for the part a Humanoid drives.</summary>
        public const string RootPartName = "HumanoidRootPart";

        /// <summary>Roblox's HumanoidRootPart size in studs (2x2x1) — the generic Part default
        /// (4x1x2) is a plain building block, not the shape a script expects when it reads
        /// Character.HumanoidRootPart.Size.</summary>
        public static readonly RbxVector3 RootPartSize = new(2f, 2f, 1f);

        /// <summary>Default spawn position in studs: clear of whatever sits at the world origin,
        /// so an unanchored, collidable root part falls onto the ground below it instead of
        /// spawning embedded inside it.</summary>
        public static readonly RbxVector3 DefaultSpawnPosition = new(0f, 5f, 0f);

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
            IRbxCharacterPartSeeder partSeeder =
                player.CharacterPartSeeder ?? (player.Parent as RbxPlayers)?.CharacterPartSeeder;
            RbxInstance character = registry.Create(
                "Model",
                ownerActorId: actorId,
                accessScope: InstanceAccessScope.Owned,
                isRuntimeInfrastructure: true);
            try
            {
                character.Name = player.Name;
                // WHY: Roblox spawns a character Model with Archivable false, so character:Clone()
                // returns nil (Instance.yaml Clone) until a script sets Archivable = true first, the
                // idiom every avatar-copy script opens with. Only the root Model carries the flag, as
                // in Roblox; its parts stay archivable so that idiom copies the whole rig. The
                // mirror states no default for a character, so this one is the engine's observed one.
                character.Archivable = false;

                RbxInstance rootPart = registry.Create(
                    "Part",
                    ownerActorId: actorId,
                    accessScope: InstanceAccessScope.Owned,
                    isRuntimeInfrastructure: true);
                rootPart.Name = RootPartName;
                rootPart.Parent = character;

                // WHY seeded here, before the model enters the world: OnEnteredWorld materializes
                // a Part's backing object from whatever the external part-property store holds for
                // its id (Roblox default when nothing was pushed — a 4x1x2 box at the origin), and
                // that fires the moment `character.Parent = worldRoot` runs below. Seeding after
                // that point would be too late; the box would already have materialized wrong.
                RbxCFrame spawn = RbxCFrame.FromPosition(DefaultSpawnPosition);
                partSeeder?.Seed(rootPart, RbxR6Rig.SeedFor(RbxR6Part.HumanoidRootPart, spawn));
                player.RootPartSpawnSeeder?.Invoke(rootPart, RootPartSize, DefaultSpawnPosition);
                if (partSeeder != null)
                {
                    BuildR6Body(registry, character, actorId, partSeeder, spawn);
                    (character as RbxModel)?.SetPrimaryPart(rootPart);
                }

                RbxInstance humanoid = registry.Create(
                    "Humanoid",
                    ownerActorId: actorId,
                    accessScope: InstanceAccessScope.Owned,
                    isRuntimeInfrastructure: true);
                humanoid.Parent = character;
    
                // WHY the parts are attached before the model enters the world: parenting is
                // what materializes the subtree in the world, and a Model that entered the world
                // before its Humanoid existed would be observed by a ChildAdded handler in a shape
                // no finished character ever has.
            }
            catch
            {
                character.Destroy();
                throw;
            }

            // WHY the outgoing character is unloaded before the new model is parented: a respawn
            // would otherwise leave both models in the world at once, which a synchronous registry
            // listener observes even though the deferred signals hide it.
            // WHY the build above still runs first: a throw there destroys only the detached model,
            // so the player keeps the old character instead of ending up with neither.
            Unload(player);

            // WHY published before parenting, not after: the Parent setter materializes the
            // subtree synchronously and fires the registry's world-entry/membership callbacks
            // (InstanceRegistry.SceneMembershipChanged, the backing binder's OnEnteredWorld) from
            // inside that very call. A host listening for the character entering the world calls
            // Players:GetPlayerFromCharacter from one of those synchronous callbacks — publishing
            // Character (and the lifecycle's own trusted LoadedCharacter) first is what makes that
            // lookup resolve. Publishing only after Parent returns left every such observer with
            // no owner to find.
            player.LoadedCharacter = character;
            player.Character = character;

            try
            {
                character.Parent = worldRoot;
            }
            catch
            {
                // WHY the rollback is conditional, not unconditional: a handler invoked
                // synchronously from the failed parenting attempt above (the same world-entry path
                // described in the WHY above) may itself have disconnected this player or loaded a
                // different character for it before this catch runs — reentrancy this method has
                // to survive. Clearing unconditionally would stomp whatever that handler already
                // established; clearing only when the fields still point at OUR character undoes
                // exactly (and only) this failed publish, leaving newer state untouched. On success
                // of this guard, the player ends with no character and no detached model, matching
                // the caller's expectations for a parenting failure exactly as before this reorder.
                if (ReferenceEquals(player.LoadedCharacter, character))
                {
                    player.LoadedCharacter = null;
                }

                if (ReferenceEquals(player.Character, character))
                {
                    player.Character = null;
                }

                character.Destroy();
                throw;
            }

            // WHY re-checked instead of assumed: the same reentrant handler can run on the success
            // path too, and may already have unloaded or replaced this very character before the
            // Parent setter returns. Firing CharacterAdded for a character that is no longer this
            // player's current one would tell every later observer something false.
            if (ReferenceEquals(player.LoadedCharacter, character))
            {
                player.CharacterAdded.Fire(character);
            }

            return character;
        }

        /// <summary>
        /// Adds the Torso, Head, arms and legs to a character that already holds its root part, each
        /// seeded at its rest pose around <paramref name="rootCFrame"/> before the model enters the world.
        /// </summary>
        private static void BuildR6Body(InstanceRegistry registry, RbxInstance character, string actorId,
            IRbxCharacterPartSeeder seeder, RbxCFrame rootCFrame)
        {
            for (int index = (int)RbxR6Part.Torso; index < RbxR6Rig.PartCount; index++)
            {
                RbxR6Part part = (RbxR6Part)index;
                RbxInstance limb = registry.Create(
                    "Part",
                    ownerActorId: actorId,
                    accessScope: InstanceAccessScope.Owned,
                    isRuntimeInfrastructure: true);
                limb.Name = RbxR6Rig.NameOf(part);
                limb.Parent = character;
                seeder.Seed(limb, RbxR6Rig.SeedFor(part, rootCFrame));
            }
        }

        /// <summary>
        /// Fires CharacterRemoving and destroys the character this player's lifecycle actually
        /// built (<see cref="RbxPlayer.LoadedCharacter"/>), if any. Safe to call when there is
        /// none.
        /// </summary>
        /// <remarks>
        /// WHY the trusted reference is torn down, not whatever <see cref="RbxPlayer.Character"/>
        /// currently reads: see <see cref="RbxPlayer.LoadedCharacter"/>'s remarks — Character is
        /// reassignable from Lua with no ownership check, so using it here would let a script that
        /// pointed its own Player at another player's character aim this teardown at that foreign
        /// character (e.g. by kicking itself, or disconnecting).
        /// </remarks>
        public static void Unload(RbxPlayer player)
        {
            if (player == null)
            {
                return;
            }

            RbxInstance outgoing = player.LoadedCharacter;
            if (outgoing == null)
            {
                return;
            }

            player.LoadedCharacter = null;
            // WHY conditional: Character keeps reading whatever it was last assigned to (including
            // a foreign character a script pointed it at) until something legitimately changes it.
            // Clearing it here only when it still points at the character we are tearing down
            // preserves that read semantics instead of silently overwriting a value the script
            // itself set.
            if (ReferenceEquals(player.Character, outgoing))
            {
                player.Character = null;
            }

            if (!outgoing.IsDestroyed)
            {
                player.CharacterRemoving.FireForDestruction(outgoing, outgoing);
                outgoing.Destroy();
            }
        }
    }
}
