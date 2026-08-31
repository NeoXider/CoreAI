using System;
using System.Collections.Generic;

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

        internal void Initialize(string actorId, long userId)
        {
            if (NetworkActorId != null)
            {
                throw new InvalidOperationException("RbxPlayer is already initialized.");
            }

            NetworkActorId = actorId ?? throw new ArgumentNullException(nameof(actorId));
            UserId = userId;
            Name = "Player" + userId;
        }
    }

    /// <summary>Minimum Players service pulled forward for Roblox-compatible remotes.</summary>
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

            // WHY: without this, every mod written against remotes today would need rewriting at MVP8.
            RbxPlayer player = (RbxPlayer)registry.Create("Player");
            player.Initialize(actor, _nextUserId++);
            player.Parent = this;
            _byActor.Add(actor, player);
            _players.Add(player);
            PlayerAdded.Fire(player);
            return player;
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

        public bool TryGetByActorId(string actorId, out RbxPlayer player)
        {
            string actor = RequireActorId(actorId);
            return _byActor.TryGetValue(actor, out player);
        }

        public bool RemoveActor(string actorId)
        {
            string actor = RequireActorId(actorId);
            if (!_byActor.TryGetValue(actor, out RbxPlayer player))
            {
                return false;
            }

            PlayerRemoving.Fire(player);
            _byActor.Remove(actor);
            _players.Remove(player);
            player.Destroy();
            return true;
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
