using System;

namespace CoreAI.Ai
{
    /// <summary>
    /// Which side of the conversation a piece of text belongs to when a filter evaluates it.
    /// </summary>
    public enum ContentFilterDirection
    {
        /// <summary>Text typed (or otherwise produced) by the player, about to be sent to the model.</summary>
        UserInput = 0,

        /// <summary>Text produced by the model, about to be shown to the player.</summary>
        ModelOutput = 1
    }

    /// <summary>What the caller must do with the evaluated text.</summary>
    public enum ContentFilterAction
    {
        /// <summary>Pass the original text through unchanged. Default: <c>default(ContentFilterVerdict)</c> is Allow.</summary>
        Allow = 0,

        /// <summary>Replace the original text with <see cref="ContentFilterVerdict.RedactedText"/> and continue.</summary>
        Redact = 1,

        /// <summary>Drop the message entirely (the caller decides how to surface the refusal).</summary>
        Block = 2
    }

    /// <summary>
    /// Evaluation context for one <see cref="IContentFilter.Evaluate"/> call: the direction of the
    /// text and the agent role it belongs to. Readonly struct so the Allow path allocates nothing.
    /// </summary>
    public readonly struct ContentFilterContext
    {
        /// <summary>Creates a context. A null <paramref name="roleId"/> is normalized to "".</summary>
        public ContentFilterContext(ContentFilterDirection direction, string roleId)
        {
            Direction = direction;
            RoleId = roleId ?? "";
        }

        /// <summary>Whether the text is player input or model output.</summary>
        public ContentFilterDirection Direction { get; }

        /// <summary>Agent role the conversation belongs to ("" when unknown). Never null after construction.</summary>
        public string RoleId { get; }
    }

    /// <summary>
    /// Result of one filter evaluation. Readonly struct; <see cref="Allow"/> is <c>default</c>, so
    /// the common Allow path performs zero allocations.
    /// </summary>
    public readonly struct ContentFilterVerdict
    {
        private ContentFilterVerdict(ContentFilterAction action, string redactedText, string reason)
        {
            Action = action;
            RedactedText = redactedText;
            Reason = reason;
        }

        /// <summary>What the caller must do with the text.</summary>
        public ContentFilterAction Action { get; }

        /// <summary>
        /// Replacement text when <see cref="Action"/> is <see cref="ContentFilterAction.Redact"/>;
        /// null for Allow and Block verdicts.
        /// </summary>
        public string RedactedText { get; }

        /// <summary>Optional human-readable explanation (diagnostics/UI); null when the filter gave none.</summary>
        public string Reason { get; }

        /// <summary>Allow verdict: pass the original text through unchanged. Equals <c>default</c>.</summary>
        public static ContentFilterVerdict Allow => default;

        /// <summary>Redact verdict carrying the sanitized replacement text.</summary>
        public static ContentFilterVerdict Redact(string redactedText, string reason = null)
        {
            if (redactedText == null)
            {
                throw new ArgumentNullException(nameof(redactedText));
            }

            return new ContentFilterVerdict(ContentFilterAction.Redact, redactedText, reason);
        }

        /// <summary>Block verdict: the message must not be delivered.</summary>
        public static ContentFilterVerdict Block(string reason = null)
        {
            return new ContentFilterVerdict(ContentFilterAction.Block, null, reason);
        }
    }

    /// <summary>
    /// Pluggable content-safety hook for player-facing text (education and console segments in
    /// particular). Hosts call it on both directions: player input before it reaches the model, and
    /// model output before it reaches the screen. CoreAI ships the mechanism, not the policy — see
    /// <see cref="PassthroughContentFilter"/> (default no-op) and <see cref="WordlistContentFilter"/>
    /// (baseline); real deployments should implement this interface over a proper moderation
    /// model/service. Implementations must be thread-safe and must never throw on arbitrary text.
    /// </summary>
    public interface IContentFilter
    {
        /// <summary>
        /// Evaluates one piece of text. Returns <see cref="ContentFilterVerdict.Allow"/> to pass the
        /// original through, a Redact verdict with the sanitized replacement, or a Block verdict.
        /// Null/empty text must be allowed. Must not allocate on the Allow path.
        /// </summary>
        ContentFilterVerdict Evaluate(string text, ContentFilterContext context);
    }
}
