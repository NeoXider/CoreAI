using System;
using System.Collections.Generic;
using CoreAI.Mods.Rbx.Datatypes;
using CoreAI.Mods.Rbx.Instances;

namespace CoreAI.Mods.Rbx.Agents
{
    /// <summary>What a character does for one decided action.</summary>
    public enum RbxCharacterCommandKind
    {
        /// <summary>Nothing in C#: the action is only published to the world for a script to carry out.</summary>
        Publish = 0,

        /// <summary>Stops the walk this driver started.</summary>
        Stop = 1,

        /// <summary>Walks to the target, stopping <see cref="RbxCharacterAction.Distance"/> studs short of it.</summary>
        WalkTo = 2,

        /// <summary>Walks <see cref="RbxCharacterAction.Distance"/> studs straight away from the target.</summary>
        WalkAway = 3,

        /// <summary>Turns to the target without moving.</summary>
        Face = 4,

        /// <summary>Jumps once.</summary>
        Jump = 5,

        /// <summary>Walks to a random point within <see cref="RbxCharacterAction.Distance"/> studs of home.</summary>
        Wander = 6
    }

    /// <summary>What happened when a command ran.</summary>
    public enum RbxCharacterCommandOutcome
    {
        /// <summary>The character was told to do it.</summary>
        Done = 0,

        /// <summary>Nothing to do in C#; the action was only published.</summary>
        Published = 1,

        /// <summary>The subject has no character now.</summary>
        NoCharacter = 2,

        /// <summary>The character is dead.</summary>
        Dead = 3,

        /// <summary>The target could not be found (nobody near, no such point).</summary>
        NoTarget = 4,

        /// <summary>The character already stands where the command would take it.</summary>
        AlreadyThere = 5,

        /// <summary>The world cannot do it (a pose write without a part sink, a body without a Humanoid).</summary>
        Unsupported = 6
    }

    /// <summary>Target names every command understands; any other name is read as a Vector3 attribute.</summary>
    public static class RbxCharacterTargets
    {
        /// <summary>The character itself.</summary>
        public const string Self = "self";

        /// <summary>Where the character stood when it was first seen (or <see cref="RbxCharacterCommandRunner.Home"/>).</summary>
        public const string Home = "home";

        /// <summary>The local player's character.</summary>
        public const string Player = "player";

        /// <summary>The nearest other living character within the sensing radius.</summary>
        public const string NearestCharacter = "nearest_character";
    }

    /// <summary>One decided action id and what the character does for it. Plain data.</summary>
    public sealed class RbxCharacterAction
    {
        /// <summary>Creates an action.</summary>
        /// <param name="id">The decision action or goal id.</param>
        /// <param name="kind">What the character does.</param>
        /// <param name="target">A name from <see cref="RbxCharacterTargets"/>, or the name of a Vector3 attribute
        /// on the character, its Player or the Workspace.</param>
        /// <param name="distance">Stop distance (WalkTo), flee distance (WalkAway) or wander radius, in studs.</param>
        public RbxCharacterAction(string id, RbxCharacterCommandKind kind, string target = "", double distance = 0d)
        {
            Id = (id ?? "").Trim();
            Kind = kind;
            Target = (target ?? "").Trim();
            Distance = double.IsNaN(distance) || distance < 0d ? 0d : distance;
        }

        /// <summary>The decision action or goal id.</summary>
        public string Id { get; }

        /// <summary>What the character does.</summary>
        public RbxCharacterCommandKind Kind { get; }

        /// <summary>The target name.</summary>
        public string Target { get; }

        /// <summary>The distance in studs.</summary>
        public double Distance { get; }
    }

    /// <summary>
    /// Which decided actions move the character and how. An id the map does not hold is only published to the world,
    /// so a mod carries it out in Lua (gather, build: the game's rules stay in the game).
    /// </summary>
    public sealed class RbxCharacterActionMap
    {
        private readonly Dictionary<string, RbxCharacterAction> _byId = new(StringComparer.Ordinal);
        private readonly List<RbxCharacterAction> _ordered = new();

        /// <summary>Creates a map; a later action with the same id replaces an earlier one.</summary>
        public RbxCharacterActionMap(IEnumerable<RbxCharacterAction> actions = null)
        {
            foreach (RbxCharacterAction action in actions ?? Array.Empty<RbxCharacterAction>())
            {
                if (action == null || action.Id.Length == 0)
                {
                    continue;
                }

                if (_byId.TryGetValue(action.Id, out RbxCharacterAction existing))
                {
                    _ordered.Remove(existing);
                }

                _byId[action.Id] = action;
                _ordered.Add(action);
            }
        }

        /// <summary>The actions, in order.</summary>
        public IReadOnlyList<RbxCharacterAction> Actions => _ordered;

        /// <summary>The action for <paramref name="id"/>; false when it is only published.</summary>
        public bool TryGet(string id, out RbxCharacterAction action)
        {
            return _byId.TryGetValue((id ?? "").Trim(), out action);
        }

        /// <summary>A copy with <paramref name="actions"/> added or replaced.</summary>
        public RbxCharacterActionMap With(params RbxCharacterAction[] actions)
        {
            List<RbxCharacterAction> merged = new(_ordered);
            merged.AddRange(actions ?? Array.Empty<RbxCharacterAction>());
            return new RbxCharacterActionMap(merged);
        }

        /// <summary>
        /// The autopilot's movement goals: <c>follow</c> the nearest character, <c>flee</c> from it, <c>wander</c>,
        /// <c>idle</c>, <c>jump</c>. Goals such as <c>gather</c> or <c>build</c> are not here: a mod carries them out.
        /// </summary>
        public static RbxCharacterActionMap AutopilotDefaults()
        {
            return new RbxCharacterActionMap(new[]
            {
                new RbxCharacterAction("follow", RbxCharacterCommandKind.WalkTo, RbxCharacterTargets.NearestCharacter, 4d),
                new RbxCharacterAction("flee", RbxCharacterCommandKind.WalkAway, RbxCharacterTargets.NearestCharacter, 16d),
                new RbxCharacterAction("wander", RbxCharacterCommandKind.Wander, RbxCharacterTargets.Home, 12d),
                new RbxCharacterAction("idle", RbxCharacterCommandKind.Stop),
                new RbxCharacterAction("jump", RbxCharacterCommandKind.Jump)
            });
        }

        /// <summary>
        /// A villager's movement: <c>idle</c>, <c>wander</c> near home, <c>go_home</c>, <c>follow_player</c>,
        /// <c>face_player</c>, <c>flee</c> from the player, <c>jump</c>; <c>greet</c> and <c>talk</c> turn to the
        /// player while the dialogue actuator speaks.
        /// </summary>
        public static RbxCharacterActionMap NpcDefaults()
        {
            return new RbxCharacterActionMap(new[]
            {
                new RbxCharacterAction("idle", RbxCharacterCommandKind.Stop),
                new RbxCharacterAction("wander", RbxCharacterCommandKind.Wander, RbxCharacterTargets.Home, 8d),
                new RbxCharacterAction("go_home", RbxCharacterCommandKind.WalkTo, RbxCharacterTargets.Home, 0d),
                new RbxCharacterAction("follow_player", RbxCharacterCommandKind.WalkTo, RbxCharacterTargets.Player, 5d),
                new RbxCharacterAction("face_player", RbxCharacterCommandKind.Face, RbxCharacterTargets.Player),
                new RbxCharacterAction("flee", RbxCharacterCommandKind.WalkAway, RbxCharacterTargets.Player, 12d),
                new RbxCharacterAction("jump", RbxCharacterCommandKind.Jump),
                new RbxCharacterAction("greet", RbxCharacterCommandKind.Face, RbxCharacterTargets.Player),
                new RbxCharacterAction("talk", RbxCharacterCommandKind.Face, RbxCharacterTargets.Player)
            });
        }
    }

    /// <summary>
    /// The movement primitives over a Roblox-shaped <c>Humanoid</c>, the same calls a control script makes: walk to a
    /// point (<c>Humanoid:MoveTo</c>), face a point (the root part's pose through the part sink), jump
    /// (<c>Humanoid.Jump</c>) and stop the walk this driver started. It never calls <c>Humanoid:Move</c>, which is
    /// the player's own control path, so a key the player presses always overrides a walk in progress.
    /// </summary>
    public sealed class RbxHumanoidDriver
    {
        private const float MinimumPlanarDistance = 0.05f;

        private RbxHumanoid _walking;

        /// <summary>Drives <paramref name="subject"/>.</summary>
        public RbxHumanoidDriver(RbxCharacterSubject subject)
        {
            Subject = subject ?? throw new ArgumentNullException(nameof(subject));
        }

        /// <summary>The character this driver moves.</summary>
        public RbxCharacterSubject Subject { get; }

        /// <summary>The point of the walk this driver started; null when it is not walking.</summary>
        public RbxVector3? WalkTarget { get; private set; }

        /// <summary>Walks to <paramref name="target"/> (studs) and turns toward it.</summary>
        public RbxCharacterCommandOutcome WalkTo(RbxVector3 target)
        {
            RbxCharacterCommandOutcome ready = Ready(out RbxCharacterView view);
            if (ready != RbxCharacterCommandOutcome.Done)
            {
                return ready;
            }

            if (view.Humanoid == null)
            {
                return RbxCharacterCommandOutcome.Unsupported;
            }

            if (view.Position.HasValue && Planar(target - view.Position.Value).Magnitude <= RbxHumanoid.ArrivalRadiusStuds)
            {
                Stop();
                return RbxCharacterCommandOutcome.AlreadyThere;
            }

            if (view.Position.HasValue)
            {
                TurnRoot(view, target);
            }

            view.Humanoid.MoveTo(target);
            _walking = view.Humanoid;
            WalkTarget = target;
            return RbxCharacterCommandOutcome.Done;
        }

        /// <summary>Turns the character to <paramref name="point"/> about the vertical axis, without moving it.</summary>
        public RbxCharacterCommandOutcome Face(RbxVector3 point)
        {
            RbxCharacterCommandOutcome ready = Ready(out RbxCharacterView view);
            if (ready != RbxCharacterCommandOutcome.Done)
            {
                return ready;
            }

            if (!view.Position.HasValue || Planar(point - view.Position.Value).Magnitude < MinimumPlanarDistance)
            {
                return RbxCharacterCommandOutcome.AlreadyThere;
            }

            return TurnRoot(view, point) ? RbxCharacterCommandOutcome.Done : RbxCharacterCommandOutcome.Unsupported;
        }

        /// <summary>Jumps once (<c>Humanoid.Jump = true</c>).</summary>
        public RbxCharacterCommandOutcome Jump()
        {
            RbxCharacterCommandOutcome ready = Ready(out RbxCharacterView view);
            if (ready != RbxCharacterCommandOutcome.Done)
            {
                return ready;
            }

            if (view.Humanoid == null)
            {
                return RbxCharacterCommandOutcome.Unsupported;
            }

            view.Humanoid.RequestJump();
            return RbxCharacterCommandOutcome.Done;
        }

        /// <summary>
        /// Ends the walk this driver started (<c>Humanoid:MoveTo</c> to where the character stands, the Roblox idiom),
        /// unless the player is steering the character already, whose input has ended it.
        /// </summary>
        public RbxCharacterCommandOutcome Stop()
        {
            RbxHumanoid walking = _walking;
            _walking = null;
            WalkTarget = null;
            if (walking == null || walking.IsDestroyed || walking.IsDead)
            {
                return RbxCharacterCommandOutcome.AlreadyThere;
            }

            if (walking.RequestedMoveDirection.Magnitude > 1e-4f)
            {
                return RbxCharacterCommandOutcome.AlreadyThere;
            }

            if (!Subject.TryResolve(out RbxCharacterView view) || !ReferenceEquals(view.Humanoid, walking) ||
                !view.Position.HasValue)
            {
                return RbxCharacterCommandOutcome.NoCharacter;
            }

            walking.MoveTo(view.Position.Value);
            return RbxCharacterCommandOutcome.Done;
        }

        private RbxCharacterCommandOutcome Ready(out RbxCharacterView view)
        {
            if (!Subject.TryResolve(out view))
            {
                return RbxCharacterCommandOutcome.NoCharacter;
            }

            if (view.Humanoid != null && !view.IsAlive)
            {
                return RbxCharacterCommandOutcome.Dead;
            }

            return RbxCharacterCommandOutcome.Done;
        }

        private bool TurnRoot(RbxCharacterView view, RbxVector3 point)
        {
            if (view.RootPart == null || !Subject.World.TryGetPartCFrame(view.RootPart, out RbxCFrame current))
            {
                return false;
            }

            RbxVector3 at = current.Position;
            RbxVector3 look = new(point.X, at.Y, point.Z);
            if (Planar(look - at).Magnitude < MinimumPlanarDistance)
            {
                return false;
            }

            return Subject.World.TrySetPartCFrame(view.RootPart, RbxCFrame.LookAt(at, look));
        }

        internal static RbxVector3 Planar(RbxVector3 vector)
        {
            return new RbxVector3(vector.X, 0f, vector.Z);
        }
    }

    /// <summary>
    /// Runs decided actions on one character through an <see cref="RbxCharacterActionMap"/>: resolves the target,
    /// computes the point and hands it to the <see cref="RbxHumanoidDriver"/>. Deterministic: wandering draws from a
    /// seeded Roblox <c>Random</c>.
    /// </summary>
    public sealed class RbxCharacterCommandRunner
    {
        private readonly RbxCharacterActionMap _map;
        private readonly RbxRandom _random;

        /// <summary>Creates a runner.</summary>
        /// <param name="subject">The character.</param>
        /// <param name="map">The actions; null uses <see cref="RbxCharacterActionMap.AutopilotDefaults"/>.</param>
        /// <param name="senseRadius">How far, in studs, <see cref="RbxCharacterTargets.NearestCharacter"/> looks.</param>
        /// <param name="seed">Seed of the wander points.</param>
        public RbxCharacterCommandRunner(RbxCharacterSubject subject, RbxCharacterActionMap map = null,
            double senseRadius = 48d, double seed = 1d)
        {
            Driver = new RbxHumanoidDriver(subject);
            _map = map ?? RbxCharacterActionMap.AutopilotDefaults();
            SenseRadius = double.IsNaN(senseRadius) || senseRadius <= 0d ? 48d : senseRadius;
            _random = new RbxRandom(seed);
        }

        /// <summary>The primitives.</summary>
        public RbxHumanoidDriver Driver { get; }

        /// <summary>The actions.</summary>
        public RbxCharacterActionMap Map => _map;

        /// <summary>How far the nearest-character target looks, in studs.</summary>
        public double SenseRadius { get; }

        /// <summary>The home point; set the first time the character is seen when nobody set it before.</summary>
        public RbxVector3? Home { get; set; }

        /// <summary>The last action id run.</summary>
        public string LastActionId { get; private set; } = "";

        /// <summary>Runs the action for <paramref name="actionId"/>; an id the map does not hold is only published.</summary>
        public RbxCharacterCommandOutcome Run(string actionId)
        {
            LastActionId = (actionId ?? "").Trim();
            return _map.TryGet(LastActionId, out RbxCharacterAction action)
                ? Run(action)
                : RbxCharacterCommandOutcome.Published;
        }

        /// <summary>Runs <paramref name="action"/>.</summary>
        public RbxCharacterCommandOutcome Run(RbxCharacterAction action)
        {
            if (action == null || action.Kind == RbxCharacterCommandKind.Publish)
            {
                return RbxCharacterCommandOutcome.Published;
            }

            if (action.Kind == RbxCharacterCommandKind.Stop)
            {
                RbxCharacterCommandOutcome stopped = Driver.Stop();
                return stopped == RbxCharacterCommandOutcome.NoCharacter ? stopped : RbxCharacterCommandOutcome.Done;
            }

            if (action.Kind == RbxCharacterCommandKind.Jump)
            {
                return Driver.Jump();
            }

            if (!Driver.Subject.TryResolve(out RbxCharacterView self))
            {
                return RbxCharacterCommandOutcome.NoCharacter;
            }

            if (!self.Position.HasValue)
            {
                return RbxCharacterCommandOutcome.Unsupported;
            }

            RbxVector3 here = self.Position.Value;
            Home ??= here;
            if (!TryResolveTarget(action.Target, self, out RbxVector3 target))
            {
                return RbxCharacterCommandOutcome.NoTarget;
            }

            switch (action.Kind)
            {
                case RbxCharacterCommandKind.Face:
                    return Driver.Face(target);
                case RbxCharacterCommandKind.WalkTo:
                    return Driver.WalkTo(ShortOf(here, target, action.Distance));
                case RbxCharacterCommandKind.WalkAway:
                    return Driver.WalkTo(AwayFrom(here, target, action.Distance));
                case RbxCharacterCommandKind.Wander:
                    return Driver.WalkTo(WanderPoint(target, action.Distance));
                default:
                    return RbxCharacterCommandOutcome.Published;
            }
        }

        /// <summary>Resolves a target name for <paramref name="self"/>; false when there is no such point now.</summary>
        public bool TryResolveTarget(string target, RbxCharacterView self, out RbxVector3 point)
        {
            point = default;
            string name = string.IsNullOrWhiteSpace(target) ? RbxCharacterTargets.Self : target.Trim();
            switch (name)
            {
                case RbxCharacterTargets.Self:
                    return Assign(self.Position, out point);
                case RbxCharacterTargets.Home:
                    return Assign(Home ?? self.Position, out point);
                case RbxCharacterTargets.Player:
                    if (!Driver.Subject.TryResolveLocalPlayer(out RbxCharacterView player) ||
                        ReferenceEquals(player.Character, self.Character))
                    {
                        return false;
                    }

                    return Assign(player.Position, out point);
                case RbxCharacterTargets.NearestCharacter:
                    return Assign(Nearest(self), out point);
                default:
                    RbxVector3? named = RbxCharacterQueries.VectorAttribute(self.Character, name)
                                        ?? RbxCharacterQueries.VectorAttribute(self.Player, name)
                                        ?? RbxCharacterQueries.VectorAttribute(
                                            RbxCharacterQueries.Workspace(Driver.Subject.World.Game), name);
                    return Assign(named, out point);
            }
        }

        private RbxVector3? Nearest(RbxCharacterView self)
        {
            if (!self.Position.HasValue)
            {
                return null;
            }

            RbxVector3? best = null;
            double bestDistance = SenseRadius;
            foreach (RbxCharacterView other in RbxCharacterQueries.Characters(Driver.Subject.World, self.Character))
            {
                if (!other.IsAlive || !other.Position.HasValue)
                {
                    continue;
                }

                double distance = (other.Position.Value - self.Position.Value).Magnitude;
                if (distance <= bestDistance)
                {
                    bestDistance = distance;
                    best = other.Position.Value;
                }
            }

            return best;
        }

        private RbxVector3 WanderPoint(RbxVector3 centre, double radius)
        {
            double reach = radius > 0d ? radius : 8d;
            double angle = _random.NextNumber(0d, Math.PI * 2d);
            double distance = _random.NextNumber(reach * 0.5d, reach);
            return new RbxVector3(centre.X + (float)(Math.Cos(angle) * distance), centre.Y,
                centre.Z + (float)(Math.Sin(angle) * distance));
        }

        private static RbxVector3 ShortOf(RbxVector3 here, RbxVector3 target, double stopDistance)
        {
            RbxVector3 planar = RbxHumanoidDriver.Planar(target - here);
            float length = planar.Magnitude;
            if (stopDistance <= 0d || length <= 1e-4f)
            {
                return target;
            }

            if (length <= stopDistance)
            {
                return here;
            }

            return target - planar * (float)(stopDistance / length);
        }

        private static RbxVector3 AwayFrom(RbxVector3 here, RbxVector3 threat, double distance)
        {
            RbxVector3 planar = RbxHumanoidDriver.Planar(here - threat);
            RbxVector3 direction = planar.Magnitude <= 1e-4f ? RbxVector3.XAxis : planar.Unit;
            return here + direction * (float)(distance > 0d ? distance : 12d);
        }

        private static bool Assign(RbxVector3? value, out RbxVector3 point)
        {
            point = value ?? default;
            return value.HasValue;
        }
    }
}
