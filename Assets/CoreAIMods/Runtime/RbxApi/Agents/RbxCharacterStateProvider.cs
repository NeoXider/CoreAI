using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using CoreAI.Decisions;
using CoreAI.Mods.Rbx.Datatypes;
using CoreAI.Mods.Rbx.Instances;

namespace CoreAI.Mods.Rbx.Agents
{
    /// <summary>What a character's state text includes. Plain data.</summary>
    public sealed class RbxCharacterStateOptions
    {
        /// <summary>What the character is, after its name (for example "the player's character").</summary>
        public string Role { get; set; } = "";

        /// <summary>Characters farther than this many studs are not mentioned.</summary>
        public double SenseRadius { get; set; } = 48d;

        /// <summary>At most this many nearby characters are listed, nearest first.</summary>
        public int MaxNearby { get; set; } = 5;

        /// <summary>The string attribute a mod writes world facts into (on the character, then on its Player); empty reads none.</summary>
        public string FactsAttribute { get; set; } = "";
    }

    /// <summary>
    /// Describes what a character sees, in the plain English the decision policies put into their state text: who
    /// it is, its health and motion, where it stands, the nearest characters with distance and compass direction
    /// (north is -Z, east is +X), and the world facts a mod publishes in an attribute. Read only when the policy
    /// asks (on events and the slow tick), never per frame.
    /// </summary>
    public sealed class RbxCharacterStateProvider : INpcStateProvider, IAutopilotStateProvider
    {
        private static readonly string[] Compass =
            { "north", "north-east", "east", "south-east", "south", "south-west", "west", "north-west" };

        private readonly RbxCharacterSubject _subject;
        private readonly RbxCharacterStateOptions _options;
        private readonly Func<string> _goal;

        /// <summary>Creates the provider.</summary>
        /// <param name="subject">The character described.</param>
        /// <param name="options">What to include; null uses the defaults.</param>
        /// <param name="currentGoal">The goal pursued now, mentioned when not empty; null leaves it out.</param>
        public RbxCharacterStateProvider(RbxCharacterSubject subject, RbxCharacterStateOptions options = null,
            Func<string> currentGoal = null)
        {
            _subject = subject ?? throw new ArgumentNullException(nameof(subject));
            _options = options ?? new RbxCharacterStateOptions();
            _goal = currentGoal;
        }

        /// <inheritdoc />
        public string DescribeState()
        {
            if (!_subject.TryResolve(out RbxCharacterView self))
            {
                return "You have no body in the world right now.";
            }

            StringBuilder text = new(256);
            text.Append("You are ").Append(self.Name.Length > 0 ? self.Name : "a character");
            if (!string.IsNullOrWhiteSpace(_options.Role))
            {
                text.Append(", ").Append(_options.Role.Trim());
            }

            text.Append('.');
            AppendBody(text, self);
            AppendNearby(text, self);
            string goal = SafeGoal();
            if (goal.Length > 0)
            {
                text.Append(" Your current goal: ").Append(goal).Append('.');
            }

            string facts = Facts(self);
            if (facts.Length > 0)
            {
                text.Append('\n').Append(facts);
            }

            return text.ToString();
        }

        /// <summary>The compass word for a planar offset (north is -Z, east is +X).</summary>
        public static string Direction(RbxVector3 offset)
        {
            double angle = Math.Atan2(offset.X, -offset.Z) * 180d / Math.PI;
            if (angle < 0d)
            {
                angle += 360d;
            }

            int sector = (int)Math.Floor((angle + 22.5d) / 45d) % 8;
            return Compass[sector];
        }

        private static void AppendBody(StringBuilder text, RbxCharacterView self)
        {
            RbxHumanoid humanoid = self.Humanoid;
            if (humanoid != null)
            {
                text.Append(" Health ").Append(Whole(humanoid.Health)).Append(" of ").Append(Whole(humanoid.MaxHealth))
                    .Append('.');
                text.Append(' ').Append(Motion(humanoid)).Append('.');
            }

            if (self.Position.HasValue)
            {
                RbxVector3 at = self.Position.Value;
                text.Append(" You stand at (").Append(Whole(at.X)).Append(", ").Append(Whole(at.Y)).Append(", ")
                    .Append(Whole(at.Z)).Append(").");
            }
        }

        private void AppendNearby(StringBuilder text, RbxCharacterView self)
        {
            if (!self.Position.HasValue)
            {
                return;
            }

            List<(double Distance, RbxCharacterView View)> near = new();
            foreach (RbxCharacterView other in RbxCharacterQueries.Characters(_subject.World, self.Character))
            {
                if (!other.Position.HasValue)
                {
                    continue;
                }

                double distance = RbxHumanoidDriver.Planar(other.Position.Value - self.Position.Value).Magnitude;
                if (distance <= _options.SenseRadius)
                {
                    near.Add((distance, other));
                }
            }

            string radius = Whole(_options.SenseRadius);
            if (near.Count == 0)
            {
                text.Append(" Nobody is within ").Append(radius).Append(" studs.");
                return;
            }

            near.Sort((a, b) => a.Distance.CompareTo(b.Distance));
            text.Append(" Nearby within ").Append(radius).Append(" studs: ");
            int listed = Math.Min(near.Count, Math.Max(1, _options.MaxNearby));
            for (int i = 0; i < listed; i++)
            {
                (double distance, RbxCharacterView other) = near[i];
                if (i > 0)
                {
                    text.Append("; ");
                }

                text.Append(other.Name.Length > 0 ? other.Name : "someone");
                if (other.Player != null)
                {
                    text.Append(" (a player)");
                }

                if (!other.IsAlive)
                {
                    text.Append(" (dead)");
                }

                text.Append(' ').Append(Whole(distance)).Append(" studs ")
                    .Append(Direction(other.Position.Value - self.Position.Value));
            }

            text.Append('.');
        }

        private string Facts(RbxCharacterView self)
        {
            string name = (_options.FactsAttribute ?? "").Trim();
            if (name.Length == 0)
            {
                return "";
            }

            string own = RbxCharacterQueries.StringAttribute(self.Character, name).Trim();
            if (own.Length > 0)
            {
                return own;
            }

            return RbxCharacterQueries.StringAttribute(self.Player ?? _subject.FindLocalPlayer(), name).Trim();
        }

        private string SafeGoal()
        {
            try
            {
                return (_goal?.Invoke() ?? "").Trim();
            }
            catch (Exception)
            {
                return "";
            }
        }

        private static string Motion(RbxHumanoid humanoid)
        {
            if (humanoid.IsDead)
            {
                return "You are dead";
            }

            switch (humanoid.GetState())
            {
                case RbxHumanoidState.Flying:
                    return "You are flying";
                case RbxHumanoidState.Jumping:
                case RbxHumanoidState.Freefall:
                    return "You are in the air";
                default:
                    return humanoid.MoveDirection.Magnitude > 1e-3f ? "You are walking" : "You are standing still";
            }
        }

        private static string Whole(double value)
        {
            return Math.Round(value).ToString("0", CultureInfo.InvariantCulture);
        }
    }
}
