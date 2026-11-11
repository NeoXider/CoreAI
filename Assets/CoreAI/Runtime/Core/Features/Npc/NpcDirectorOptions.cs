using System;
using System.Collections.Generic;

namespace CoreAI.Ai.Npc
{
    /// <summary>What the director does with a turn when the world's calls-per-minute budget is spent.</summary>
    public enum NpcBudgetExceededBehavior
    {
        /// <summary>The NPC says one of its fallback lines without calling the model (the default: nobody is ignored).</summary>
        FallbackLine = 0,

        /// <summary>The NPC stays silent; <see cref="INpcDirector.LineIgnored"/> is not raised, the turn ends as cancelled.</summary>
        Silence = 1
    }

    /// <summary>World-wide settings of the NPC block, as data.</summary>
    public sealed class NpcDirectorOptions
    {
        private IReadOnlyList<string> _defaultFallbackLines = new[]
        {
            "Hm? Give me a moment.",
            "Sorry, I lost my train of thought.",
            "Ask me again in a little while."
        };

        /// <summary>Model calls all NPCs of the world may start per minute (sliding window); 0 or less is unlimited.</summary>
        public int MaxCallsPerMinute { get; set; } = 30;

        /// <summary>
        /// NPC turns that may run at once across the world; more wait their turn. With the default 1 NPC chatter
        /// holds at most one slot of the orchestration queue, so the player's own agent always finds one free.
        /// </summary>
        public int MaxConcurrentTurns { get; set; } = 1;

        /// <summary>Stream answers so the bubble grows as text arrives; false waits for the whole answer.</summary>
        public bool UseStreaming { get; set; } = true;

        /// <summary>
        /// Seconds a turn may run before it is stopped and a fallback line spoken; 0 or less relies on the
        /// pipeline's own timeouts. Checked on <see cref="INpcDirector.Tick"/>, so no timer thread is needed.
        /// </summary>
        public double TurnTimeoutSeconds { get; set; } = 45d;

        /// <summary>Most NPCs the director holds at once.</summary>
        public int MaxAgents { get; set; } = 64;

        /// <summary>What happens when <see cref="MaxCallsPerMinute"/> is spent.</summary>
        public NpcBudgetExceededBehavior WhenBudgetExceeded { get; set; } = NpcBudgetExceededBehavior.FallbackLine;

        /// <summary><c>AiTaskRequest.SourceTag</c> of host NPC turns; an agent created by a mod uses <c>Mod:&lt;modId&gt;</c>.</summary>
        public string SourceTag { get; set; } = "Npc";

        /// <summary>Fallback lines of an NPC whose persona has none.</summary>
        public IReadOnlyList<string> DefaultFallbackLines
        {
            get => _defaultFallbackLines;
            set => _defaultFallbackLines = value ?? Array.Empty<string>();
        }

        /// <summary>The last-resort line when neither the persona nor <see cref="DefaultFallbackLines"/> has one.</summary>
        public string LastResortLine { get; set; } = "...";
    }

    /// <summary>Everything needed to add one NPC to a director, as data.</summary>
    public sealed class NpcAgentDefinition
    {
        /// <summary>Who the NPC is.</summary>
        public NpcPersona Persona { get; set; } = new();

        /// <summary>When it considers a line meant for it.</summary>
        public NpcAddressingRules Addressing { get; set; } = new();

        /// <summary>What one of its turns may spend.</summary>
        public NpcBudget Budget { get; set; } = new();

        /// <summary>Key of its memory partition (topic <c>npc:&lt;key&gt;</c>); empty uses the persona id.</summary>
        public string MemoryKey { get; set; } = "";

        /// <summary>Origin tag carried by its events and speech; <c>ai:&lt;modId&gt;</c> for an agent a mod created.</summary>
        public string OriginTag { get; set; } = "";

        /// <summary><c>AiTaskRequest.SourceTag</c> of its turns; empty uses <see cref="NpcDirectorOptions.SourceTag"/>.</summary>
        public string SourceTag { get; set; } = "";

        /// <summary>Where it stands, read on the director's thread whenever a line is arbitrated; null is "unknown".</summary>
        public Func<NpcPosition?> PositionSource { get; set; }
    }
}
