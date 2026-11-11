using System;
using System.Collections.Generic;

namespace CoreAI.ChatOverlay
{
    /// <summary>
    /// Engine-free configuration of the in-world chat: behaviour, fade, speaker names and every string the chat
    /// writes. Keys and layout live in the Unity settings that wrap this object.
    /// </summary>
    public sealed class ChatOverlayOptions
    {
        /// <summary>Agent role that answers; empty uses <c>CoreAiChatOptions.DefaultRoleId</c>.</summary>
        public string RoleId { get; set; } = "";

        /// <summary>False forces buffered answers; true follows the role and global streaming settings.</summary>
        public bool EnableStreaming { get; set; } = true;

        /// <summary>First character of a slash command.</summary>
        public char CommandPrefix { get; set; } = ChatCommandParser.DefaultPrefix;

        /// <summary>Seconds a line stays after it last changed while the input is closed; 0 or less never fades.</summary>
        public double FadeDelaySeconds { get; set; } = 10d;

        /// <summary>Most lines the feed keeps.</summary>
        public int MaxFeedLines { get; set; } = 100;

        /// <summary>Longest text of one feed line; 0 is unlimited.</summary>
        public int MaxLineCharacters { get; set; } = 4000;

        /// <summary>Longest text the player can type.</summary>
        public int MaxInputLength { get; set; } = 512;

        /// <summary>True closes the input after Enter (the Minecraft behaviour).</summary>
        public bool CloseInputOnSubmit { get; set; } = true;

        /// <summary>Speaker shown before the player's lines.</summary>
        public string PlayerSpeakerName { get; set; } = "You";

        /// <summary>Speaker shown before the agent's lines; <c>{role}</c> is replaced with the role id.</summary>
        public string AssistantSpeakerName { get; set; } = "AI";

        /// <summary>Placeholder shown in the empty input.</summary>
        public string InputPlaceholder { get; set; } = "Say something or type {prefix}help";

        /// <summary>Reply to an unknown command. Placeholders: <c>{prefix}</c>, <c>{command}</c>.</summary>
        public string UnknownCommandFormat { get; set; } = "Unknown command {prefix}{command}. Type {prefix}help for the list.";

        /// <summary>Reply to a bare prefix.</summary>
        public string EmptyCommandText { get; set; } = "Type a command after {prefix}, or {prefix}help for the list.";

        /// <summary>Reply when a command throws. Placeholders: <c>{prefix}</c>, <c>{command}</c>, <c>{error}</c>.</summary>
        public string CommandFailedFormat { get; set; } = "{prefix}{command} failed: {error}";

        /// <summary>Shown when no AI service is connected.</summary>
        public string ServiceUnavailableText { get; set; } = "The AI is not connected.";

        /// <summary>Shown when the player sends while the agent is still answering.</summary>
        public string BusyText { get; set; } = "The AI is still answering. Type {prefix}stop to cancel it.";

        /// <summary>Shown when a turn is stopped; empty shows nothing.</summary>
        public string StoppedText { get; set; } = "Stopped.";

        /// <summary>Shown when a turn ends without visible text.</summary>
        public string NoResponseText { get; set; } = "The AI returned no answer.";

        /// <summary>Put before every turn failure sentence.</summary>
        public string ErrorPrefix { get; set; } = "";

        /// <summary>Status while the model works without a running tool.</summary>
        public string ThinkingText { get; set; } = "thinking";

        /// <summary>Status while the answer streams.</summary>
        public string AnsweringText { get; set; } = "answering";

        /// <summary>Status while a tool runs. Placeholder: <c>{tool}</c>.</summary>
        public string RunningToolFormat { get; set; } = "running {tool}";

        /// <summary>Status after a tool failed, before the agent tries again. Placeholder: <c>{tool}</c>.</summary>
        public string RetryingToolFormat { get; set; } = "{tool} failed, retrying";

        /// <summary>Added to a status on a repeated attempt. Placeholders: <c>{status}</c>, <c>{attempt}</c>.</summary>
        public string RetryAttemptFormat { get; set; } = "{status} (retry {attempt})";

        /// <summary>The status with the turn's elapsed time. Placeholders: <c>{status}</c>, <c>{elapsed}</c>.</summary>
        public string ElapsedFormat { get; set; } = "{status} \u00b7 {elapsed}s";

        /// <summary>Elapsed time is shown from this many seconds into a turn.</summary>
        public double ShowElapsedAfterSeconds { get; set; } = 1d;

        /// <summary>
        /// Status text per tool name, replacing <see cref="RunningToolFormat"/> for that tool (matched without
        /// regard to case).
        /// </summary>
        public IDictionary<string, string> ToolStatusTexts { get; set; } =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["manage_mods"] = "loading mod"
            };

        /// <summary>A copy that can be changed without touching this instance.</summary>
        public ChatOverlayOptions Clone()
        {
            ChatOverlayOptions copy = (ChatOverlayOptions)MemberwiseClone();
            copy.ToolStatusTexts = ToolStatusTexts == null
                ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, string>(ToolStatusTexts, StringComparer.OrdinalIgnoreCase);
            return copy;
        }

        /// <summary>Replaces <c>{prefix}</c> in <paramref name="template"/>.</summary>
        public string WithPrefix(string template)
        {
            return (template ?? "").Replace("{prefix}", CommandPrefix.ToString());
        }
    }
}
