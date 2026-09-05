using System;
using System.Collections.Generic;
using CoreAI.Mods.Rbx.Datatypes;

namespace CoreAI.Mods.Rbx.Instances.Networking
{
    /// <summary>Minimum runtime-created Player identity required by remote callbacks.</summary>
    public sealed class RbxPlayer : RbxInstance
    {
        internal RbxPlayer(ClassDescriptor descriptor)
            : base(descriptor)
        {
        }

        public long UserId { get; private set; }

        public string NetworkActorId { get; private set; }

        /// <summary>
        /// Mirror <c>Player.DisplayName</c> (writable; mirror tags carry no ReadOnly). Defaults to
        /// the username — the mirror states no fallback, so this default is OURS.
        /// </summary>
        public string DisplayName { get; set; }

        /// <summary>
        /// Mirror <c>Player.Character</c>: the Model driven for this player, or nil before the
        /// character pipeline (a later MVP8 slice) assigns one. Lua may read and assign it; the
        /// CharacterAdded/CharacterRemoving signals stay loud stubs until that slice fires them.
        /// </summary>
        public RbxInstance Character { get; internal set; }

        internal void Initialize(string actorId, long userId, string username, string displayName)
        {
            if (NetworkActorId != null)
            {
                throw new InvalidOperationException("RbxPlayer is already initialized.");
            }

            NetworkActorId = actorId ?? throw new ArgumentNullException(nameof(actorId));
            UserId = userId;
            Name = string.IsNullOrEmpty(username) ? "Player" + userId : username;
            DisplayName = string.IsNullOrEmpty(displayName) ? Name : displayName;
        }
    }

    /// <summary>Minimum Players service of real client identities for Roblox-compatible remotes.</summary>
    public sealed class RbxPlayers : RbxInstance
    {
        private readonly Dictionary<string, RbxPlayer> _byActor =
            new(StringComparer.Ordinal);
        private readonly List<RbxPlayer> _players = new();
        private long _nextUserId = 1;

        internal RbxPlayers(ClassDescriptor descriptor)
            : base(descriptor)
        {
            PlayerAdded = new RbxScriptSignal("Players.PlayerAdded");
            PlayerRemoving = new RbxScriptSignal("Players.PlayerRemoving");
        }

        public RbxScriptSignal PlayerAdded { get; }

        public RbxScriptSignal PlayerRemoving { get; }

        /// <summary>
        /// Identity backend behind <c>Player.Name</c>/<c>Player.DisplayName</c>. Defaults to the
        /// synthetic profile; a host assigns a real provider (ideally before any actor joins —
        /// the profile is read once per join in <see cref="EnsureActor"/>).
        /// </summary>
        /// <summary>
        /// Where an admitted actor's durable identity comes from; null keeps the session counter.
        /// </summary>
        /// <remarks>
        /// WHY it is consulted first: a UserId decided at admission is the same on every join, and
        /// the counter's is not. A script that saves by UserId depends on that difference.
        /// </remarks>
        public IRbxActorIdentitySource IdentitySource { get; set; }

        public IRbxPlayerProfileProvider ProfileProvider { get; set; } =
            SyntheticPlayerProfileProvider.Instance;

        /// <summary>Returns the real Player registered for an actor, creating it once if needed.</summary>
        public RbxPlayer EnsureActor(InstanceRegistry registry, string actorId)
        {
            if (registry == null)
            {
                throw new ArgumentNullException(nameof(registry));
            }

            string actor = RequireActorId(actorId);
            if (_byActor.TryGetValue(actor, out RbxPlayer existing))
            {
                return existing;
            }

            // WHY: Player identities are runtime-created authorization infrastructure, not authored
            // world content, while actor ownership prevents a foreign client from rewriting identity.
            RbxPlayer player = (RbxPlayer)registry.Create(
                "Player",
                ownerActorId: actor,
                accessScope: InstanceAccessScope.Owned,
                isRuntimeInfrastructure: true);
            try
            {
                string username;
                string displayName;
                long userId;
                if (IdentitySource == null
                    || !IdentitySource.TryGetIdentity(actor, out userId, out username,
                        out displayName)
                    || userId <= 0L
                    || string.IsNullOrWhiteSpace(username))
                {
                    userId = _nextUserId++;
                    IRbxPlayerProfileProvider provider =
                        ProfileProvider ?? SyntheticPlayerProfileProvider.Instance;
                    if (!provider.TryGetProfile(userId, out username, out displayName))
                    {
                        username = "Player" + userId;
                        displayName = username;
                    }
                }
                else if (string.IsNullOrWhiteSpace(displayName))
                {
                    displayName = username;
                }

                player.Initialize(actor, userId, username, displayName);
                player.Parent = this;
                CreatePlayerContainer(registry, player, actor, "Backpack");
                CreatePlayerContainer(registry, player, actor, "PlayerGui");
                CreatePlayerContainer(registry, player, actor, "PlayerScripts");
                _byActor.Add(actor, player);
                _players.Add(player);
                PlayerAdded.Fire(player);
                return player;
            }
            catch
            {
                _byActor.Remove(actor);
                _players.Remove(player);
                player.Destroy();
                throw;
            }
        }

        public RbxPlayer GetLocalPlayer(string actorId)
        {
            string actor = RequireActorId(actorId);
            return _byActor.TryGetValue(actor, out RbxPlayer player) ? player : null;
        }

        public IReadOnlyList<RbxPlayer> GetPlayers()
        {
            return _players.ToArray();
        }

        /// <summary>
        /// Mirror <c>Players:GetPlayerByUserId</c>: the connected Player with this UserId, or nil
        /// when no connected player has it (a disconnected player is no longer findable).
        /// </summary>
        public RbxPlayer GetPlayerByUserId(long userId)
        {
            for (int index = 0; index < _players.Count; index++)
            {
                RbxPlayer player = _players[index];
                if (player.UserId == userId)
                {
                    return player;
                }
            }

            return null;
        }

        /// <summary>
        /// Mirror <c>Players:GetPlayerFromCharacter</c>: the Player whose Character is this
        /// instance, or nil (nil argument and non-character models match nothing, as in the
        /// mirror's equivalent loop).
        /// </summary>
        public RbxPlayer GetPlayerFromCharacter(RbxInstance character)
        {
            if (character == null)
            {
                return null;
            }

            for (int index = 0; index < _players.Count; index++)
            {
                RbxPlayer player = _players[index];
                if (ReferenceEquals(player.Character, character))
                {
                    return player;
                }
            }

            return null;
        }

        /// <summary>
        /// Mirror <c>Player:Kick</c>: disconnects the player — it leaves the tree and
        /// <c>PlayerRemoving</c> fires with the caller-supplied reason (the Lua boundary passes
        /// <c>CreatorKick</c>). Returns false and fires nothing when the player is null or no
        /// longer connected (already removed). The kick message is validated at the Lua boundary
        /// and dropped here: headless runtime has no presentation surface for it.
        /// </summary>
        public bool KickPlayer(RbxPlayer player, RbxEnumItem kickReason)
        {
            if (player == null || kickReason == null)
            {
                return false;
            }

            for (int index = 0; index < _players.Count; index++)
            {
                if (ReferenceEquals(_players[index], player))
                {
                    return RemoveActor(player.NetworkActorId, kickReason);
                }
            }

            return false;
        }

        public bool TryGetByActorId(string actorId, out RbxPlayer player)
        {
            string actor = RequireActorId(actorId);
            return _byActor.TryGetValue(actor, out player);
        }

        public bool RemoveActor(string actorId)
        {
            return RemoveActor(actorId, null);
        }

        /// <summary>Fires PlayerRemoving before detaching the Player, with the documented reason.</summary>
        public bool RemoveActor(string actorId, RbxEnumItem reason)
        {
            string actor = RequireActorId(actorId);
            if (!_byActor.TryGetValue(actor, out RbxPlayer player))
            {
                return false;
            }

            PlayerRemoving.FireForDestruction(player, player, reason);
            _byActor.Remove(actor);
            _players.Remove(player);
            player.Destroy();
            return true;
        }

        private static void CreatePlayerContainer(InstanceRegistry registry, RbxPlayer player,
            string actorId, string className)
        {
            // WHY: per-player containers are runtime infrastructure owned by the joined actor
            // (same scope as the Player itself), so teardown and kick destroy them with it and
            // no other actor can claim them. Created empty: contents are MVP10/MVP14.
            RbxInstance container = registry.Create(
                className,
                ownerActorId: actorId,
                accessScope: InstanceAccessScope.Owned,
                isRuntimeInfrastructure: true);
            container.Parent = player;
        }

        private static string RequireActorId(string actorId)
        {
            if (string.IsNullOrWhiteSpace(actorId))
            {
                throw RbxError.BadArgument(
                    "Player actor id cannot be empty",
                    "use the trusted ActorContext.ActorId");
            }

            return actorId.Trim();
        }
    }
}
