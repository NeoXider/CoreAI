using System;
using System.Collections.Generic;
using CoreAI.Authority;
using CoreAI.ChatOverlay;

namespace CoreAI.Ai.Npc
{
    /// <summary>
    /// Lets NPCs hear the local player's chat: every player line goes through the director's arbiter, so the NPC
    /// it names (or the one the player is talking with, or the nearest one that answers unaddressed lines) answers.
    /// A line addressed to the player's own AI (<see cref="AiAddressAliases"/>, <see cref="AiDisplayName"/>) is never
    /// offered to an NPC, even while a villager's follow-up window is open.
    /// <para>
    /// Two ways to attach. <see cref="AttachTo(IChatOverlayPreSendHooks)"/> (the overlay's controller) hears the
    /// line before it is sent and keeps a line an NPC took away from the player's own agent; a line no NPC takes
    /// still reaches the agent. <see cref="AttachTo(ChatFeed)"/> only listens to the feed, so the agent gets every
    /// line as well. The two are exclusive: attaching one way detaches the other, and a line is never heard twice.
    /// </para>
    /// </summary>
    public sealed class NpcChatHearing : IChatOverlayPreSendHook, IDisposable
    {
        private readonly INpcDirector _director;
        private readonly Func<NpcPosition?> _playerPosition;
        private readonly IActorIdentityProvider _identity;
        private readonly string _playerId;
        private readonly string _playerName;
        private ChatFeed _feed;
        private IDisposable _hook;

        /// <summary>Creates the adapter.</summary>
        /// <param name="director">The NPCs that hear the player.</param>
        /// <param name="playerId">Stable id of the local player; NPCs remember conversations per id.</param>
        /// <param name="playerName">Name the NPCs call the player; empty uses the chat line's speaker label.</param>
        /// <param name="playerPosition">Where the player stands; null when the game tracks no positions.</param>
        /// <param name="identity">The host's identity provider, so turns run as the local player's actor.</param>
        public NpcChatHearing(INpcDirector director, string playerId = "player", string playerName = "",
            Func<NpcPosition?> playerPosition = null, IActorIdentityProvider identity = null)
        {
            _director = director ?? throw new ArgumentNullException(nameof(director));
            _playerId = string.IsNullOrWhiteSpace(playerId) ? "player" : playerId.Trim();
            _playerName = (playerName ?? "").Trim();
            _playerPosition = playerPosition;
            _identity = identity;
        }

        /// <summary>True while attached as a pre-send hook, so lines an NPC takes never reach the player's agent.</summary>
        public bool IsPreSendHook => _hook != null;

        /// <summary>
        /// Words that address the player's own AI at the start of a line (any case, optionally after <c>@</c>); such a
        /// line is never offered to an NPC (<see cref="NpcAiAddress"/>). Defaults to <c>ai</c>; empty turns the words off.
        /// </summary>
        public IReadOnlyList<string> AiAddressAliases { get; set; } = NpcAiAddress.DefaultAliases;

        /// <summary>
        /// The AI's display name in the chat, read for every line; a line that starts with it goes to the AI unless an
        /// NPC answers to the same name. Null or an empty name turns this rule off.
        /// </summary>
        public Func<string> AiDisplayName { get; set; }

        /// <summary>Subscribes to the player lines of <paramref name="feed"/>; a second call moves the subscription.</summary>
        public void AttachTo(ChatFeed feed)
        {
            Detach();
            _feed = feed;
            if (_feed != null)
            {
                _feed.LineAdded += HandleLineAdded;
            }
        }

        /// <summary>
        /// Hears every plain line before the chat sends it: a line an NPC takes is answered by that NPC and is not
        /// sent to the player's agent. A second call moves the hook.
        /// </summary>
        public void AttachTo(IChatOverlayPreSendHooks hooks)
        {
            Detach();
            if (hooks != null)
            {
                _hook = hooks.AddPreSendHook(this);
            }
        }

        /// <summary>Stops listening to the feed and removes the pre-send hook.</summary>
        public void Detach()
        {
            if (_feed != null)
            {
                _feed.LineAdded -= HandleLineAdded;
                _feed = null;
            }

            IDisposable hook = _hook;
            _hook = null;
            hook?.Dispose();
        }

        /// <summary>Routes one player line to the NPCs; true when an NPC took it. A line addressed to the AI is never offered.</summary>
        public bool HearPlayerLine(string speakerName, string text)
        {
            if (string.IsNullOrWhiteSpace(text) || AddressesTheAi(text))
            {
                return false;
            }

            NpcPosition? position = null;
            try
            {
                position = _playerPosition?.Invoke();
            }
            catch (Exception)
            {
                position = null;
            }

            string name = _playerName.Length > 0 ? _playerName : speakerName;
            NpcHeardLine line = NpcHeardLine.Chat(_playerId, name, text, position);
            if (_identity != null)
            {
                line.SpeakerActor = _identity.GetActorContext(BuiltInAgentRoleIds.AiNpc);
            }

            return _director.Hear(line).IsTaken;
        }

        /// <inheritdoc />
        bool IChatOverlayPreSendHook.TryTake(ChatOverlayPlayerLine line)
        {
            return HearPlayerLine(line.SpeakerName, line.Text);
        }

        private bool AddressesTheAi(string text)
        {
            string aiName;
            try
            {
                aiName = AiDisplayName?.Invoke();
            }
            catch (Exception)
            {
                aiName = null;
            }

            return NpcAiAddress.IsAddressedToAi(text, AiAddressAliases, aiName, _director);
        }

        /// <inheritdoc />
        public void Dispose()
        {
            Detach();
        }

        private void HandleLineAdded(ChatFeedLine line)
        {
            if (line == null || line.Kind != ChatFeedLineKind.Player)
            {
                return;
            }

            HearPlayerLine(line.Speaker, line.Text);
        }
    }
}
