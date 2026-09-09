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
        /// Mirror <c>Player.Character</c>: the Model driven for this player, or nil until a
        /// character is loaded. Assigning it directly does NOT fire the signals — only
        /// <c>LoadCharacterAsync</c> does, exactly as in Roblox.
        /// </summary>
        public RbxInstance Character { get; internal set; }

        /// <summary>
        /// The character <see cref="RbxCharacterFactory"/> actually built and owns for this
        /// player — the only reference its teardown (<see cref="RbxCharacterFactory.Unload"/>)
        /// destroys.
        /// </summary>
        /// <remarks>
        /// WHY separate from <see cref="Character"/>: Character carries no ownership check on
        /// assignment (see its own remarks — no signal, no check, exactly the mirror's contract),
        /// so a script authorized only over its own Player can point Character at ANOTHER
        /// player's character. If teardown destroyed whatever Character currently pointed at, that
        /// reassignment would let the script aim destruction at a character it was never
        /// authorized to touch (e.g. by kicking itself, or simply disconnecting — neither goes
        /// through LoadCharacterAsync's authorization check, which guards only the load path).
        /// Tracking what the lifecycle itself built for this player, and tearing down THAT, closes
        /// the hole. Deliberate choice: assignment from Lua still changes what scripts read via
        /// Character — it just can no longer redirect what gets destroyed.
        /// </remarks>
        internal RbxInstance LoadedCharacter { get; set; }

        /// <summary>Mirror <c>Player.CharacterAdded(character)</c>.</summary>
        public RbxScriptSignal CharacterAdded => GetOrCreateSignal("CharacterAdded");

        /// <summary>Mirror <c>Player.CharacterRemoving(character)</c>.</summary>
        public RbxScriptSignal CharacterRemoving => GetOrCreateSignal("CharacterRemoving");

        /// <summary>
        /// Mirror <c>Player:DistanceFromCharacter(point)</c>: studs from the character's root part
        /// to the point, and <c>0</c> when the player has no character.
        /// </summary>
        /// <remarks>
        /// WHY zero and not an error for a characterless player: the mirror documents the method as
        /// returning 0 in that case, and a proximity check written against Roblox would otherwise
        /// have to guard every call.
        /// </remarks>
        public double DistanceFromCharacter(RbxVector3 point)
        {
            RbxInstance root = ResolveRootPart();
            if (root == null || PartPositionReader == null)
            {
                return 0d;
            }

            RbxVector3 position = PartPositionReader(root);
            return (position - point).Magnitude;
        }

        /// <summary>
        /// Reads a part's position in studs. Set by the composition, because BasePart spatial state
        /// lives in an external sink that this engine-free assembly cannot reference.
        /// </summary>
        internal Func<RbxInstance, RbxVector3> PartPositionReader { get; set; }

        /// <summary>
        /// Seeds a newly built root part's size and spawn position into the same external sink,
        /// for the same reason <see cref="PartPositionReader"/> is a delegate: BasePart spatial
        /// state lives outside this engine-free assembly. Null skips seeding — a raw-registry
        /// caller with no part sink has nothing to seed and the part keeps the sink's plain
        /// default until something writes to it.
        /// </summary>
        /// <remarks>
        /// WHY public and not internal like <see cref="PartPositionReader"/>: a test that wants to
        /// pin the spawn transform needs to wire a fake sink from outside this assembly, and this
        /// assembly grants InternalsVisibleTo only to the network transport, not the test assembly.
        /// </remarks>
        public Action<RbxInstance, RbxVector3, RbxVector3> RootPartSpawnSeeder { get; set; }

        private RbxInstance ResolveRootPart()
        {
            if (Character == null || Character.IsDestroyed)
            {
                return null;
            }

            RbxInstance root = Character.FindFirstChild(RbxCharacterFactory.RootPartName);
            return root != null && root.IsA("BasePart") ? root : null;
        }

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

        /// <summary>
        /// Scheduler the per-player CharacterAdded/CharacterRemoving signals are bound to. Set by
        /// the composition alongside the service-level PlayerAdded/PlayerRemoving binding.
        /// </summary>
        /// <remarks>
        /// WHY the service holds it: those two signals are created per Player, so there is no single
        /// place a host could bind them after the fact — a player that joined before the host got
        /// around to it would refuse every Connect with "has no scheduler".
        /// </remarks>
        internal Scheduling.ModScheduler Scheduler { get; set; }

        /// <summary>Mirror default for <c>Players.RespawnTime</c>: 5 seconds.</summary>
        public const double DefaultRespawnTime = 5d;

        /// <summary>
        /// Whether characters spawn (on join) and respawn (on death) on their own, per the
        /// mirror's default of true. Read at the moment the deferred join-spawn and the
        /// death-triggered respawn actually fire — see <see cref="EnsureActor"/>'s deferred spawn
        /// and the Lua-CSharp bindings' Humanoid.Died wiring — not when the join or death
        /// happened, so a script that flips this off still wins the race.
        /// </summary>
        public bool CharacterAutoLoads { get; set; } = true;

        /// <summary>Seconds after a character's Humanoid dies before it respawns, when
        /// <see cref="CharacterAutoLoads"/> is (still) true when the timer elapses. Consumed by
        /// the Lua-CSharp bindings' Humanoid.Died wiring. Mirror default 5.0; negative values are
        /// refused by the write path.</summary>
        public double RespawnTime { get; set; } = DefaultRespawnTime;

        /// <summary>
        /// How many players this host admits. Read-only to Lua, as in the mirror.
        /// </summary>
        /// <remarks>
        /// WHY it defaults to 1 rather than to a Roblox place default: the mirror documents no
        /// default at all, and a CoreAI world with no transport installed genuinely admits exactly
        /// one actor. Inventing a Roblox-looking number would be a value a script could branch on
        /// that nothing in CoreAI honours; 1 is the true capacity until a host sets its own.
        /// </remarks>
        public int MaxPlayers { get; set; } = 1;

        /// <summary>
        /// Reads a part's position in studs, handed to every Player this service creates so
        /// <c>DistanceFromCharacter</c> can answer. Null in a world with no part sink.
        /// </summary>
        internal Func<RbxInstance, Datatypes.RbxVector3> PartPositionReader { get; set; }

        /// <summary>
        /// Seeds a newly spawned character's root part (size, spawn position), handed to every
        /// Player this service creates. See <see cref="RbxPlayer.RootPartSpawnSeeder"/>.
        /// </summary>
        public Action<RbxInstance, Datatypes.RbxVector3, Datatypes.RbxVector3> RootPartSpawnSeeder
        {
            get;
            set;
        }

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
                if (Scheduler != null)
                {
                    player.CharacterAdded.BindScheduler(Scheduler);
                    player.CharacterRemoving.BindScheduler(Scheduler);
                }

                player.PartPositionReader = PartPositionReader;
                player.RootPartSpawnSeeder = RootPartSpawnSeeder;
                CreatePlayerContainer(registry, player, actor, "Backpack");
                CreatePlayerContainer(registry, player, actor, "PlayerGui");
                CreatePlayerContainer(registry, player, actor, "PlayerScripts");
                _byActor.Add(actor, player);
                _players.Add(player);
                PlayerAdded.Fire(player);

                // WHY after PlayerAdded and not before: the mirror's join order is the player first,
                // then its character, and a PlayerAdded handler that reads Player.Character expects
                // nil on a world where CharacterAutoLoads is off. Firing them the other way round
                // would make that read depend on a setting the handler cannot see.
                //
                // WHY deferred rather than spawned inline (F7): EnsureActor runs from mod-context
                // creation and from inside network message dispatch, so an inline spawn could commit
                // before a script had any chance to flip CharacterAutoLoads, and could land the
                // spawn's registry mutations in the middle of a remote-dispatch try/catch. Routing it
                // through the scheduler's host-callback slot re-reads CharacterAutoLoads (and checks
                // the player still has no character — an explicit LoadCharacterAsync may have already
                // beaten this to it) once the current dispatch has fully drained, on the next
                // scheduler Advance. No Scheduler bound yet (raw-registry callers with no Lua-CSharp
                // bindings) falls back to the old inline spawn — there is no drain to defer past.
                if (Scheduler != null)
                {
                    Scheduler.ScheduleHostCallback(0d, () =>
                    {
                        if (player.IsDestroyed || player.Character != null
                            || !CharacterAutoLoads)
                        {
                            return;
                        }

                        if (registry.WorldRoot == null)
                        {
                            LogSkippedAutoLoad(registry, player);
                            return;
                        }

                        // WHY caught here rather than left to propagate: this callback runs from
                        // the scheduler's host-callback slot, outside any mod's dispatch try/catch
                        // (ResumeDelayedThreads rethrows and neither Advance nor the tick driver
                        // catches), so an unguarded failure here — an instance cap, an ACL refusal
                        // on the world-root parent — would kill the whole scheduler frame for
                        // every mod. A failed spawn should cost only this player its character.
                        try
                        {
                            RbxCharacterFactory.Load(registry, registry.WorldRoot, player);
                        }
                        catch (Exception exception)
                        {
                            LogFailedAutoLoad(registry, player, exception);
                        }
                    });
                }
                else if (CharacterAutoLoads)
                {
                    if (registry.WorldRoot == null)
                    {
                        LogSkippedAutoLoad(registry, player);
                    }
                    else
                    {
                        RbxCharacterFactory.Load(registry, registry.WorldRoot, player);
                    }
                }

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

            // WHY the character goes with the player: it lives under Workspace, not under the
            // Player, so destroying the Player alone would leave a body standing in the world with
            // nobody driving it — the "ghost character" every multiplayer game gets wrong once.
            RbxCharacterFactory.Unload(player);
            player.Destroy();
            return true;
        }

        private static void LogSkippedAutoLoad(InstanceRegistry registry, RbxPlayer player)
        {
            // WHY the registry seam carries this: the service is engine-free and holds no logger,
            // while the host wires Diagnostics to the console.
            Action<string> diagnostics = registry.Diagnostics;
            if (diagnostics != null)
            {
                diagnostics("[CoreAI.RbxApi] Skipped the join-time character auto-load for '"
                    + player.Name + "': CharacterAutoLoads is on but the registry has no world "
                    + "root, so Character stays nil.");
            }
        }

        private static void LogFailedAutoLoad(InstanceRegistry registry, RbxPlayer player,
            Exception exception)
        {
            // WHY the registry seam carries this: same reasoning as LogSkippedAutoLoad — the
            // service is engine-free and holds no logger.
            Action<string> diagnostics = registry.Diagnostics;
            if (diagnostics != null)
            {
                diagnostics("[CoreAI.RbxApi] The deferred join-time character auto-load for '"
                    + player.Name + "' failed and was skipped, so Character stays nil: "
                    + exception);
            }
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
