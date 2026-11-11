using System;
using System.Collections.Generic;
using CoreAI.Ai.Npc;
using CoreAI.ChatOverlay;
using CoreAI.Decisions;
using CoreAI.Mods.Rbx.Agents;
using CoreAI.Mods.Rbx.Instances.Scheduling;

namespace CoreAI.Composition
{
    /// <summary>
    /// The agents a mods composition runs over its worlds: the local character as the NPCs' listener, the player
    /// autopilot behind <c>/autopilot</c>, and the villager brains of characters marked <c>NpcAgent</c>. Each ticks on
    /// the Heartbeat of every world the composition builds (<see cref="AttachWorld"/>), so it pauses with the world
    /// and follows a load or an undo. The autopilot and the brains need the decision block (<c>RegisterCoreAiDecisions</c>
    /// in the CoreAI scope); without it only the listener is connected. Disposing hands control back and detaches all.
    /// </summary>
    public sealed class CoreAiWorldAgents : IDisposable
    {
        private readonly List<IDisposable> _handles = new();
        private readonly List<WeakReference<ModScheduler>> _attached = new();
        private bool _disposed;

        /// <summary>Connects what the given services allow; any of them except <paramref name="world"/> may be null.</summary>
        public CoreAiWorldAgents(
            IRbxCharacterWorld world,
            IDecisionClientProvider decisions,
            IDecisionClock clock,
            IDecisionTraceSink trace = null,
            IDecisionRouter router = null,
            RbxCharacterAutopilotOptions autopilotOptions = null,
            IChatCommandRegistry commands = null,
            IChatOverlayMessages messages = null,
            INpcDirector npcs = null,
            NpcListenerPositionRelay listener = null)
        {
            if (world == null)
            {
                throw new ArgumentNullException(nameof(world));
            }

            if (listener != null)
            {
                _handles.Add(listener.Attach(new RbxCharacterListenerPosition(world)));
            }

            if (decisions == null || clock == null)
            {
                return;
            }

            Autopilot = new RbxCharacterAutopilot(world, decisions.ForConsumer(DecisionConsumers.Autopilot), clock,
                trace, router, autopilotOptions);
            if (commands != null)
            {
                _handles.Add(AutopilotChatCommand.Register(commands, Autopilot.Controller, messages));
            }

            if (npcs != null)
            {
                BrainHost = new RbxNpcBrainHost(world, npcs, decisions.ForConsumer(DecisionConsumers.NpcBrain), clock,
                    trace);
            }
        }

        /// <summary>The player autopilot; null without the decision block.</summary>
        public RbxCharacterAutopilot Autopilot { get; }

        /// <summary>The villager brains; null without the decision block or an NPC director.</summary>
        public RbxNpcBrainHost BrainHost { get; }

        /// <summary>How many world schedulers were attached and are still alive.</summary>
        public int AttachedWorldCount
        {
            get
            {
                int alive = 0;
                foreach (WeakReference<ModScheduler> attached in _attached)
                {
                    alive += attached.TryGetTarget(out _) ? 1 : 0;
                }

                return alive;
            }
        }

        /// <summary>True when <paramref name="scheduler"/> was attached.</summary>
        public bool IsAttached(ModScheduler scheduler)
        {
            foreach (WeakReference<ModScheduler> attached in _attached)
            {
                if (attached.TryGetTarget(out ModScheduler target) && ReferenceEquals(target, scheduler))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Ticks the autopilot and the brains on <paramref name="scheduler"/>'s Heartbeat. Called for every world the
        /// composition builds; only the published world's scheduler advances, and attaching twice changes nothing.
        /// </summary>
        public void AttachWorld(ModScheduler scheduler)
        {
            if (_disposed || scheduler == null || IsAttached(scheduler))
            {
                return;
            }

            _attached.Add(new WeakReference<ModScheduler>(scheduler));
            Autopilot?.AttachScheduler(scheduler);
            BrainHost?.AttachScheduler(scheduler);
        }

        /// <inheritdoc />
        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            for (int index = _handles.Count - 1; index >= 0; index--)
            {
                _handles[index]?.Dispose();
            }

            _handles.Clear();
            Autopilot?.Dispose();
            BrainHost?.Dispose();
        }
    }
}
