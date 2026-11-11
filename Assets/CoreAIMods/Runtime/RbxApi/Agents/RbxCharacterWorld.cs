using System;
using System.Collections.Generic;
using CoreAI.Mods.Rbx.Datatypes;
using CoreAI.Mods.Rbx.Instances;
using CoreAI.Mods.Rbx.Instances.Networking;

namespace CoreAI.Mods.Rbx.Agents
{
    /// <summary>
    /// The live Rbx world the character pieces read and drive. Read on every call, so a world load (a new
    /// <see cref="RbxDataModel"/>) is followed without rewiring anything.
    /// </summary>
    public interface IRbxCharacterWorld
    {
        /// <summary>The published world now; null while none is loaded.</summary>
        RbxDataModel Game { get; }

        /// <summary>The live pose of <paramref name="part"/> in studs; false when this world cannot tell.</summary>
        bool TryGetPartCFrame(RbxInstance part, out RbxCFrame cframe);

        /// <summary>
        /// Turns or moves <paramref name="part"/> without going through a script, so a walk in progress is not ended
        /// the way a scripted root-part move ends it; false when this world cannot write poses.
        /// </summary>
        bool TrySetPartCFrame(RbxInstance part, RbxCFrame cframe);
    }

    /// <summary>
    /// An <see cref="IRbxCharacterWorld"/> over delegates, for the composition root: the current world, and the part
    /// sink's live pose read and write. Either part delegate may be null; characters are then located through their
    /// Humanoid's motor and cannot be turned.
    /// </summary>
    public sealed class DelegateRbxCharacterWorld : IRbxCharacterWorld
    {
        private readonly Func<RbxDataModel> _game;
        private readonly Func<RbxInstance, RbxCFrame?> _readPart;
        private readonly Action<RbxInstance, RbxCFrame> _writePart;

        /// <summary>Creates the world.</summary>
        /// <param name="game">The published world now; may answer null.</param>
        /// <param name="readPart">The live pose of a part, or null when unknown.</param>
        /// <param name="writePart">Writes a part's pose through the part sink.</param>
        public DelegateRbxCharacterWorld(Func<RbxDataModel> game, Func<RbxInstance, RbxCFrame?> readPart = null,
            Action<RbxInstance, RbxCFrame> writePart = null)
        {
            _game = game ?? throw new ArgumentNullException(nameof(game));
            _readPart = readPart;
            _writePart = writePart;
        }

        /// <inheritdoc />
        public RbxDataModel Game
        {
            get
            {
                try
                {
                    RbxDataModel game = _game();
                    return game != null && !game.IsDestroyed ? game : null;
                }
                catch (Exception)
                {
                    return null;
                }
            }
        }

        /// <inheritdoc />
        public bool TryGetPartCFrame(RbxInstance part, out RbxCFrame cframe)
        {
            cframe = default;
            if (_readPart == null || part == null || part.IsDestroyed)
            {
                return false;
            }

            try
            {
                RbxCFrame? read = _readPart(part);
                if (!read.HasValue)
                {
                    return false;
                }

                cframe = read.Value;
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <inheritdoc />
        public bool TrySetPartCFrame(RbxInstance part, RbxCFrame cframe)
        {
            if (_writePart == null || part == null || part.IsDestroyed)
            {
                return false;
            }

            try
            {
                _writePart(part, cframe);
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }
    }

    /// <summary>One character as seen this frame.</summary>
    public readonly struct RbxCharacterView
    {
        /// <summary>Creates a view.</summary>
        public RbxCharacterView(RbxInstance character, RbxHumanoid humanoid, RbxInstance rootPart, RbxPlayer player,
            RbxVector3? position, string name)
        {
            Character = character;
            Humanoid = humanoid;
            RootPart = rootPart;
            Player = player;
            Position = position;
            Name = name ?? "";
        }

        /// <summary>The character Model (or the bare part an NPC body is).</summary>
        public RbxInstance Character { get; }

        /// <summary>Its Humanoid; null for a body without one.</summary>
        public RbxHumanoid Humanoid { get; }

        /// <summary>The part that carries its position.</summary>
        public RbxInstance RootPart { get; }

        /// <summary>The Player it belongs to; null for an NPC.</summary>
        public RbxPlayer Player { get; }

        /// <summary>Where it stands, in studs; null when unknown.</summary>
        public RbxVector3? Position { get; }

        /// <summary>The name a person would use for it.</summary>
        public string Name { get; }

        /// <summary>True while it has a living Humanoid.</summary>
        public bool IsAlive => Humanoid != null && !Humanoid.IsDestroyed && !Humanoid.IsDead && Humanoid.Health > 0d;
    }

    /// <summary>Engine-free lookups over an Rbx world: the local player, a character's Humanoid, root part and position.</summary>
    public static class RbxCharacterQueries
    {
        /// <summary>The Players service, or null.</summary>
        public static RbxPlayers Players(RbxDataModel game)
        {
            return Child(game, "Players") as RbxPlayers;
        }

        /// <summary>The Workspace, or null.</summary>
        public static RbxInstance Workspace(RbxDataModel game)
        {
            return Child(game, "Workspace");
        }

        /// <summary>The UserInputService, or null.</summary>
        public static RbxUserInputService UserInput(RbxDataModel game)
        {
            return Child(game, "UserInputService") as RbxUserInputService;
        }

        /// <summary>
        /// The player at this screen: the one with <paramref name="localActorId"/>, else the only listed player, else
        /// the first listed one (the bundled control mod's rule for a solo world).
        /// </summary>
        public static RbxPlayer LocalPlayer(RbxDataModel game, string localActorId = "")
        {
            RbxPlayers players = Players(game);
            if (players == null)
            {
                return null;
            }

            try
            {
                string actorId = (localActorId ?? "").Trim();
                if (actorId.Length > 0)
                {
                    return Live(players.GetLocalPlayer(actorId));
                }

                RbxPlayer sole = Live(players.GetSoleListedPlayer());
                if (sole != null)
                {
                    return sole;
                }

                IReadOnlyList<RbxPlayer> listed = players.GetPlayers();
                return listed.Count > 0 ? Live(listed[0]) : null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>The Humanoid of <paramref name="character"/> (a Model, or a part inside one); null when none.</summary>
        public static RbxHumanoid Humanoid(RbxInstance character)
        {
            if (character == null || character.IsDestroyed)
            {
                return null;
            }

            if (character is RbxHumanoid humanoid)
            {
                return humanoid;
            }

            RbxHumanoid own = character.FindFirstChildOfClass("Humanoid") as RbxHumanoid;
            if (own != null || !character.IsA("BasePart"))
            {
                return own;
            }

            RbxInstance parent = character.Parent;
            return parent != null && !parent.IsDestroyed && parent.IsA("Model")
                ? parent.FindFirstChildOfClass("Humanoid") as RbxHumanoid
                : null;
        }

        /// <summary>
        /// The part that carries <paramref name="character"/>'s position: the Humanoid's RootPart, the Model's
        /// PrimaryPart, its HumanoidRootPart, its first part, or the character itself when it is a part.
        /// </summary>
        public static RbxInstance RootPart(RbxInstance character, RbxHumanoid humanoid = null)
        {
            if (character == null || character.IsDestroyed)
            {
                return null;
            }

            RbxInstance root = humanoid?.RootPart;
            if (root != null && !root.IsDestroyed)
            {
                return root;
            }

            if (character.IsA("BasePart"))
            {
                return character;
            }

            if (character is RbxModel model && model.PrimaryPart != null && !model.PrimaryPart.IsDestroyed)
            {
                return model.PrimaryPart;
            }

            RbxInstance named = character.FindFirstChild("HumanoidRootPart");
            if (named != null && named.IsA("BasePart"))
            {
                return named;
            }

            return character.FindFirstChildWhichIsA("BasePart");
        }

        /// <summary>
        /// Where a character stands, in studs: the live pose of its root part, else the position its Humanoid's motor
        /// reports (not the null motor of a headless world, which always answers the origin).
        /// </summary>
        public static RbxVector3? Position(IRbxCharacterWorld world, RbxInstance rootPart, RbxHumanoid humanoid)
        {
            if (world != null && rootPart != null && world.TryGetPartCFrame(rootPart, out RbxCFrame cframe))
            {
                return cframe.Position;
            }

            IRbxCharacterMotor motor = humanoid?.Motor;
            if (motor == null || motor is NullRbxCharacterMotor)
            {
                return null;
            }

            try
            {
                return motor.IsAvailable ? motor.Position : (RbxVector3?)null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>A view of <paramref name="character"/> now; false when it is gone.</summary>
        public static bool TryView(IRbxCharacterWorld world, RbxInstance character, out RbxCharacterView view)
        {
            view = default;
            if (character == null || character.IsDestroyed)
            {
                return false;
            }

            try
            {
                RbxHumanoid humanoid = Humanoid(character);
                RbxInstance model = humanoid?.Parent ?? character;
                RbxInstance root = RootPart(model, humanoid);
                RbxPlayer player = Players(world?.Game)?.GetPlayerFromCharacter(model);
                view = new RbxCharacterView(model, humanoid, root, player, Position(world, root, humanoid),
                    NameOf(model, humanoid, player));
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>The name a person would use: the player's display name, the Humanoid's, then the instance's.</summary>
        public static string NameOf(RbxInstance character, RbxHumanoid humanoid, RbxPlayer player)
        {
            if (player != null)
            {
                return !string.IsNullOrWhiteSpace(player.DisplayName) ? player.DisplayName.Trim() : player.Name;
            }

            if (humanoid != null && !string.IsNullOrWhiteSpace(humanoid.DisplayName))
            {
                return humanoid.DisplayName.Trim();
            }

            return character?.Name ?? "";
        }

        /// <summary>Every other character with a Humanoid directly under the Workspace (players' characters included).</summary>
        public static List<RbxCharacterView> Characters(IRbxCharacterWorld world, RbxInstance except = null)
        {
            List<RbxCharacterView> found = new();
            RbxInstance workspace = Workspace(world?.Game);
            if (workspace == null)
            {
                return found;
            }

            foreach (RbxInstance child in workspace.GetChildren())
            {
                if (child == null || child.IsDestroyed || ReferenceEquals(child, except) || !child.IsA("Model"))
                {
                    continue;
                }

                if (child.FindFirstChildOfClass("Humanoid") == null)
                {
                    continue;
                }

                if (TryView(world, child, out RbxCharacterView view))
                {
                    found.Add(view);
                }
            }

            return found;
        }

        /// <summary>A Vector3 attribute of <paramref name="instance"/>, or null.</summary>
        public static RbxVector3? VectorAttribute(RbxInstance instance, string name)
        {
            return ReadAttribute(instance, name) is RbxVector3 vector ? vector : (RbxVector3?)null;
        }

        /// <summary>A string attribute of <paramref name="instance"/>, or empty.</summary>
        public static string StringAttribute(RbxInstance instance, string name)
        {
            return ReadAttribute(instance, name) as string ?? "";
        }

        /// <summary>A boolean attribute of <paramref name="instance"/>, or null.</summary>
        public static bool? BoolAttribute(RbxInstance instance, string name)
        {
            return ReadAttribute(instance, name) is bool value ? value : (bool?)null;
        }

        /// <summary>Writes an attribute; false when the instance is gone or refuses it.</summary>
        public static bool TryWriteAttribute(RbxInstance instance, string name, object value)
        {
            if (instance == null || instance.IsDestroyed)
            {
                return false;
            }

            try
            {
                if (Equals(instance.GetAttribute(name), value))
                {
                    return true;
                }

                instance.SetAttribute(name, value);
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static object ReadAttribute(RbxInstance instance, string name)
        {
            if (instance == null || instance.IsDestroyed)
            {
                return null;
            }

            try
            {
                return instance.GetAttribute(name);
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static RbxInstance Child(RbxDataModel game, string className)
        {
            if (game == null || game.IsDestroyed)
            {
                return null;
            }

            try
            {
                RbxInstance child = game.FindFirstChildOfClass(className);
                return child != null && !child.IsDestroyed ? child : null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static RbxPlayer Live(RbxPlayer player)
        {
            return player != null && !player.IsDestroyed ? player : null;
        }
    }

    /// <summary>
    /// Which character a driver, an actuator or a state provider works on, resolved anew on every use: the local
    /// player's current character (it respawns), or an NPC's body.
    /// </summary>
    public sealed class RbxCharacterSubject
    {
        private readonly Func<RbxInstance> _body;
        private readonly string _localActorId;

        private RbxCharacterSubject(IRbxCharacterWorld world, Func<RbxInstance> body, string localActorId,
            bool isLocalPlayer)
        {
            World = world ?? throw new ArgumentNullException(nameof(world));
            _body = body;
            _localActorId = (localActorId ?? "").Trim();
            IsLocalPlayer = isLocalPlayer;
        }

        /// <summary>The world the subject lives in.</summary>
        public IRbxCharacterWorld World { get; }

        /// <summary>True for the local player's character.</summary>
        public bool IsLocalPlayer { get; }

        /// <summary>The local player's actor id; empty means the one player of a solo world.</summary>
        public string LocalActorId => _localActorId;

        /// <summary>The local player's character, whichever it is when asked.</summary>
        public static RbxCharacterSubject LocalPlayer(IRbxCharacterWorld world, string localActorId = "")
        {
            return new RbxCharacterSubject(world, null, localActorId, true);
        }

        /// <summary>A fixed body: an NPC's Model, or the part it is.</summary>
        public static RbxCharacterSubject Of(IRbxCharacterWorld world, RbxInstance body, string localActorId = "")
        {
            if (body == null)
            {
                throw new ArgumentNullException(nameof(body));
            }

            return new RbxCharacterSubject(world, () => body, localActorId, false);
        }

        /// <summary>A body found anew on every use (a script may replace it).</summary>
        public static RbxCharacterSubject Of(IRbxCharacterWorld world, Func<RbxInstance> body, string localActorId = "")
        {
            return new RbxCharacterSubject(world, body ?? throw new ArgumentNullException(nameof(body)),
                localActorId, false);
        }

        /// <summary>The local player now (also for an NPC subject, as the one it talks to).</summary>
        public RbxPlayer FindLocalPlayer()
        {
            return RbxCharacterQueries.LocalPlayer(World.Game, _localActorId);
        }

        /// <summary>The subject's character now; false when it has none (not spawned, destroyed, no world).</summary>
        public bool TryResolve(out RbxCharacterView view)
        {
            view = default;
            RbxInstance character;
            try
            {
                character = IsLocalPlayer ? FindLocalPlayer()?.Character : _body?.Invoke();
            }
            catch (Exception)
            {
                return false;
            }

            return RbxCharacterQueries.TryView(World, character, out view);
        }

        /// <summary>The local player's character now; false when there is none.</summary>
        public bool TryResolveLocalPlayer(out RbxCharacterView view)
        {
            view = default;
            RbxInstance character = FindLocalPlayer()?.Character;
            return RbxCharacterQueries.TryView(World, character, out view);
        }
    }
}
