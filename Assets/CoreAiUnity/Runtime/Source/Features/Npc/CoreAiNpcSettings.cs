using System;
using UnityEngine;
using UnityEngine.UIElements;

namespace CoreAI.Ai.Npc
{
    /// <summary>
    /// Scene-level settings of the NPC block: the world's budgets, where speech goes (chat feed, bubbles) and whether
    /// NPCs hear the local player's chat. Serializable for an inspector field; every value is also settable from
    /// code. Personas and addressing rules are per NPC (<see cref="NpcAgentDefinition"/>), not here.
    /// </summary>
    [Serializable]
    public sealed class CoreAiNpcSettings
    {
        [Tooltip("Registers the NPC services and the bubble entry point. Off registers nothing.")]
        [SerializeField] private bool enabled = true;

        [Header("World budget")]
        [Tooltip("Model calls all NPCs may start per minute; 0 is unlimited.")]
        [SerializeField] private int maxCallsPerMinute = 30;

        [Tooltip("NPC turns that may run at once. 1 keeps one orchestration slot free for the player's own agent.")]
        [SerializeField] private int maxConcurrentTurns = 1;

        [Tooltip("Stream answers so the bubble grows as the text arrives.")]
        [SerializeField] private bool useStreaming = true;

        [Tooltip("Seconds before a turn is stopped and a fallback line spoken; 0 relies on the pipeline timeouts.")]
        [SerializeField] private float turnTimeoutSeconds = 45f;

        [Header("Speech")]
        [Tooltip("Post every finished NPC line into the in-world chat feed (needs the chat overlay).")]
        [SerializeField] private bool postToChat = true;

        [Tooltip("Show a speech bubble over the head of an NPC that has an anchor.")]
        [SerializeField] private bool showBubbles = true;

        [Tooltip("Text shown in a bubble while the NPC thinks; empty shows nothing until text arrives.")]
        [SerializeField] private string thinkingText = "...";

        [Tooltip("Seconds a finished line stays at least.")]
        [SerializeField] private float minHoldSeconds = 3f;

        [Tooltip("Longest time a finished line stays.")]
        [SerializeField] private float maxHoldSeconds = 12f;

        [Tooltip("Bubbles farther than this from the camera are hidden (world units).")]
        [SerializeField] private float bubbleMaxDistance = 40f;

        [Tooltip("Panel of the bubble document; empty creates one at runtime.")]
        [SerializeField] private PanelSettings panelSettings;

        [Tooltip("Sorting order of a runtime-created bubble panel (below the chat overlay by default).")]
        [SerializeField] private int sortingOrder = 40;

        [Tooltip("Camera the bubbles follow; empty uses Camera.main.")]
        [SerializeField] private Camera camera;

        [Header("Hearing")]
        [Tooltip("NPCs hear the local player's chat lines (needs the chat overlay).")]
        [SerializeField] private bool hearChat = true;

        [Tooltip("Stable id of the local player; NPCs remember conversations per id.")]
        [SerializeField] private string playerId = "player";

        [Tooltip("Name NPCs call the local player; empty uses the chat's speaker label.")]
        [SerializeField] private string playerName = "";

        [Tooltip("Words that address the player's own AI at the start of a chat line ('ai, ...' or '@ai ...', any " +
                 "case); such a line never reaches a villager. The AI's chat name addresses it too unless a villager " +
                 "answers to it. Empty turns the words off.")]
        [SerializeField] private string[] aiAddressAliases = { "ai" };

        [Tooltip("A line that starts with the AI's name in the chat (the overlay's assistant speaker) goes to the AI.")]
        [SerializeField] private bool addressAiByName = true;

        /// <summary>Registers the NPC services and the entry point.</summary>
        public bool Enabled { get => enabled; set => enabled = value; }

        /// <summary>World calls per minute; 0 is unlimited.</summary>
        public int MaxCallsPerMinute { get => maxCallsPerMinute; set => maxCallsPerMinute = value; }

        /// <summary>NPC turns that may run at once.</summary>
        public int MaxConcurrentTurns { get => maxConcurrentTurns; set => maxConcurrentTurns = value; }

        /// <summary>Stream answers.</summary>
        public bool UseStreaming { get => useStreaming; set => useStreaming = value; }

        /// <summary>Turn timeout in seconds; 0 relies on the pipeline.</summary>
        public float TurnTimeoutSeconds { get => turnTimeoutSeconds; set => turnTimeoutSeconds = value; }

        /// <summary>Post finished lines into the chat feed.</summary>
        public bool PostToChat { get => postToChat; set => postToChat = value; }

        /// <summary>Show speech bubbles.</summary>
        public bool ShowBubbles { get => showBubbles; set => showBubbles = value; }

        /// <summary>Bubble text while the NPC thinks.</summary>
        public string ThinkingText { get => thinkingText; set => thinkingText = value; }

        /// <summary>Least hold time of a finished line.</summary>
        public float MinHoldSeconds { get => minHoldSeconds; set => minHoldSeconds = value; }

        /// <summary>Longest hold time of a finished line.</summary>
        public float MaxHoldSeconds { get => maxHoldSeconds; set => maxHoldSeconds = value; }

        /// <summary>Distance beyond which bubbles hide.</summary>
        public float BubbleMaxDistance { get => bubbleMaxDistance; set => bubbleMaxDistance = value; }

        /// <summary>Panel of the bubble document; null creates one.</summary>
        public PanelSettings PanelSettings { get => panelSettings; set => panelSettings = value; }

        /// <summary>Sorting order of a runtime-created panel.</summary>
        public int SortingOrder { get => sortingOrder; set => sortingOrder = value; }

        /// <summary>Camera the bubbles follow; null uses <see cref="Camera.main"/>.</summary>
        public Camera Camera { get => camera; set => camera = value; }

        /// <summary>NPCs hear the local player's chat.</summary>
        public bool HearChat { get => hearChat; set => hearChat = value; }

        /// <summary>Stable id of the local player.</summary>
        public string PlayerId { get => playerId; set => playerId = value; }

        /// <summary>Name NPCs call the local player.</summary>
        public string PlayerName { get => playerName; set => playerName = value; }

        /// <summary>
        /// Words that address the player's own AI at the start of a chat line (<see cref="NpcAiAddress"/>); such a
        /// line never reaches a villager. Defaults to <c>ai</c>; null or empty turns the words off.
        /// </summary>
        public string[] AiAddressAliases { get => aiAddressAliases; set => aiAddressAliases = value; }

        /// <summary>Whether a line that starts with the AI's chat name goes to the AI (unless a villager answers to it).</summary>
        public bool AddressAiByName { get => addressAiByName; set => addressAiByName = value; }

        /// <summary>The director options these settings describe.</summary>
        public NpcDirectorOptions ResolveDirectorOptions()
        {
            return new NpcDirectorOptions
            {
                MaxCallsPerMinute = maxCallsPerMinute,
                MaxConcurrentTurns = maxConcurrentTurns,
                UseStreaming = useStreaming,
                TurnTimeoutSeconds = turnTimeoutSeconds
            };
        }

        /// <summary>The bubble options these settings describe.</summary>
        public NpcSpeechBubbleOptions ResolveBubbleOptions()
        {
            return new NpcSpeechBubbleOptions
            {
                ThinkingText = thinkingText ?? "",
                MinHoldSeconds = Mathf.Max(0f, minHoldSeconds),
                MaxHoldSeconds = Mathf.Max(0f, maxHoldSeconds)
            };
        }
    }

    /// <summary>The NPC block's clock in a Unity player: real time, unaffected by <see cref="Time.timeScale"/>.</summary>
    public sealed class UnityNpcClock : INpcClock
    {
        /// <inheritdoc />
        public double NowSeconds => Time.realtimeSinceStartupAsDouble;
    }
}
