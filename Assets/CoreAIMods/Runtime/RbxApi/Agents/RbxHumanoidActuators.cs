using System;
using CoreAI.Decisions;
using CoreAI.Mods.Rbx.Instances;
using CoreAI.Mods.Rbx.Instances.Networking;

namespace CoreAI.Mods.Rbx.Agents
{
    /// <summary>
    /// The attributes the character pieces read and write: the whole contract between the C# decision block and a
    /// mod's game rules, in plain Roblox attributes (no new Lua API).
    /// </summary>
    public static class RbxAgentAttributes
    {
        /// <summary>
        /// On the local Player, boolean. The autopilot writes whether it drives; a mod writes <c>true</c> to ask for
        /// it and <c>false</c> to end it.
        /// </summary>
        public const string Autopilot = "Autopilot";

        /// <summary>On the local Player, string, written by the autopilot: the goal it pursues now; empty when off.</summary>
        public const string AutopilotGoal = "AutopilotGoal";

        /// <summary>
        /// On the local Player, string, written by a mod: the goals the autopilot may choose, as
        /// <c>gather, build, follow</c> or <c>gather: collect wood and stone; build: place blocks</c> (with a
        /// <c>;</c> anywhere, entries are split on <c>;</c> only, so descriptions may hold commas).
        /// </summary>
        public const string AutopilotGoals = "AutopilotGoals";

        /// <summary>On the local Player, string, written by a mod: world facts the model should know (what is nearby, what is needed).</summary>
        public const string AutopilotState = "AutopilotState";

        /// <summary>On an NPC's character, string, written by its brain: the action it took last.</summary>
        public const string NpcAction = "NpcAction";

        /// <summary>On an NPC's character, string, written by a mod: world facts the NPC's model should know.</summary>
        public const string NpcState = "NpcState";
    }

    /// <summary>
    /// Carries out an NPC brain's movement decisions on the NPC's Humanoid (walk, face, jump, stop, wander) and
    /// publishes every decided action as the <see cref="RbxAgentAttributes.NpcAction"/> attribute, so a mod can add
    /// what C# does not know (an animation, a trade window). Never throws into the policy.
    /// </summary>
    public sealed class RbxHumanoidNpcActuator : INpcActuator
    {
        private readonly bool _publish;

        /// <summary>Creates the actuator.</summary>
        /// <param name="subject">The NPC's body.</param>
        /// <param name="map">The movement actions; null uses <see cref="RbxCharacterActionMap.NpcDefaults"/>.</param>
        /// <param name="publish">False keeps the action off the world's attributes.</param>
        /// <param name="seed">Seed of the wander points.</param>
        public RbxHumanoidNpcActuator(RbxCharacterSubject subject, RbxCharacterActionMap map = null, bool publish = true,
            double seed = 1d)
        {
            Runner = new RbxCharacterCommandRunner(subject ?? throw new ArgumentNullException(nameof(subject)),
                map ?? RbxCharacterActionMap.NpcDefaults(), 48d, seed);
            _publish = publish;
        }

        /// <summary>The command runner (driver, map, home point).</summary>
        public RbxCharacterCommandRunner Runner { get; }

        /// <summary>What the last decision did to the body.</summary>
        public RbxCharacterCommandOutcome LastOutcome { get; private set; } = RbxCharacterCommandOutcome.Published;

        /// <summary>The type of the exception the last decision threw inside the world; empty when none.</summary>
        public string LastError { get; private set; } = "";

        /// <inheritdoc />
        public void Apply(NpcDecision decision)
        {
            if (decision == null)
            {
                return;
            }

            try
            {
                LastError = "";
                LastOutcome = Runner.Run(decision.ActionId);
                if (_publish && Runner.Driver.Subject.TryResolve(out RbxCharacterView view))
                {
                    RbxCharacterQueries.TryWriteAttribute(view.Character, RbxAgentAttributes.NpcAction,
                        decision.ActionId ?? "");
                }
            }
            catch (Exception ex)
            {
                LastError = ex.GetType().Name;
                LastOutcome = RbxCharacterCommandOutcome.Unsupported;
            }
        }
    }

    /// <summary>
    /// Drives the local player's character for the autopilot through the same Humanoid calls a control script uses,
    /// and publishes the goal as <see cref="RbxAgentAttributes.AutopilotGoal"/> on the Player, so the game's own mod
    /// carries out the goals C# cannot (gather, build). <see cref="Release"/> stops only the walk the autopilot
    /// started; it never fights the player's own input. Never throws into the policy.
    /// </summary>
    public sealed class RbxHumanoidAutopilotActuator : IAutopilotActuator, IAutopilotAvailability
    {
        private readonly bool _publish;

        /// <summary>Creates the actuator.</summary>
        /// <param name="subject">The local player's character (<see cref="RbxCharacterSubject.LocalPlayer"/>).</param>
        /// <param name="map">The movement goals; null uses <see cref="RbxCharacterActionMap.AutopilotDefaults"/>.</param>
        /// <param name="publish">False keeps the goal off the Player's attributes.</param>
        /// <param name="seed">Seed of the wander points.</param>
        public RbxHumanoidAutopilotActuator(RbxCharacterSubject subject, RbxCharacterActionMap map = null,
            bool publish = true, double seed = 1d)
        {
            Runner = new RbxCharacterCommandRunner(subject ?? throw new ArgumentNullException(nameof(subject)),
                map ?? RbxCharacterActionMap.AutopilotDefaults(), 48d, seed);
            _publish = publish;
        }

        /// <summary>The command runner (driver, map, home point).</summary>
        public RbxCharacterCommandRunner Runner { get; }

        /// <summary>What the last goal did to the character.</summary>
        public RbxCharacterCommandOutcome LastOutcome { get; private set; } = RbxCharacterCommandOutcome.Published;

        /// <summary>The goal pursued now; empty after <see cref="Release"/>.</summary>
        public string CurrentGoalId { get; private set; } = "";

        /// <summary>The type of the exception the last call threw inside the world; empty when none.</summary>
        public string LastError { get; private set; } = "";

        /// <inheritdoc />
        public void Pursue(AutopilotDecision decision)
        {
            if (decision == null)
            {
                return;
            }

            try
            {
                LastError = "";
                CurrentGoalId = decision.GoalId ?? "";
                LastOutcome = Runner.Run(CurrentGoalId);
                PublishGoal(CurrentGoalId);
            }
            catch (Exception ex)
            {
                LastError = ex.GetType().Name;
                LastOutcome = RbxCharacterCommandOutcome.Unsupported;
            }
        }

        /// <inheritdoc />
        public void Release()
        {
            CurrentGoalId = "";
            try
            {
                Runner.Driver.Stop();
                PublishGoal("");
            }
            catch (Exception ex)
            {
                LastError = ex.GetType().Name;
            }
        }

        /// <inheritdoc />
        public bool CanDrive(out string reason)
        {
            RbxCharacterSubject subject = Runner.Driver.Subject;
            if (subject.World.Game == null)
            {
                reason = "No world is loaded.";
                return false;
            }

            if (subject.FindLocalPlayer() == null)
            {
                reason = "No player is in the world yet.";
                return false;
            }

            if (!subject.TryResolve(out RbxCharacterView view) || view.Humanoid == null)
            {
                reason = "Your character has not spawned yet.";
                return false;
            }

            if (!view.IsAlive)
            {
                reason = "Your character is dead.";
                return false;
            }

            reason = "";
            return true;
        }

        private void PublishGoal(string goalId)
        {
            if (!_publish)
            {
                return;
            }

            RbxPlayer player = Runner.Driver.Subject.FindLocalPlayer();
            RbxCharacterQueries.TryWriteAttribute(player, RbxAgentAttributes.AutopilotGoal, goalId ?? "");
        }
    }
}
