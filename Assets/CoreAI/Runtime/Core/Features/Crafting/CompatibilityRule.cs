using System.Collections.Generic;

namespace CoreAI.Crafting
{
    /// <summary>
    /// One crafting compatibility rule.
    /// </summary>
    public sealed class CompatibilityRule
    {
        /// <summary>
        /// Elements.
        /// </summary>
        public List<string> Elements { get; set; } = new();

        /// <summary>Compatibility score produced by the rule.</summary>
        public float Score { get; set; } = 1.0f;

        /// <summary>Explanation for the compatibility result.</summary>
        public string Reason { get; set; }

        /// <summary>Is blocking.</summary>
        public bool IsBlocking => Score <= 0f;

        /// <summary>Size.</summary>
        public int Size => Elements.Count;

        /// <summary>
/// Executes Pair API operation.
        /// </summary>
        public static CompatibilityRule Pair(string a, string b, float score, string reason = null)
        {
            return new CompatibilityRule
            {
                Elements = new List<string> { a, b },
                Score = score,
                Reason = reason
            };
        }

        /// <summary>
/// Executes Group API operation.
        /// </summary>
        public static CompatibilityRule Group(float score, string reason, params string[] elements)
        {
            return new CompatibilityRule
            {
                Elements = new List<string>(elements),
                Score = score,
                Reason = reason
            };
        }
    }
}
