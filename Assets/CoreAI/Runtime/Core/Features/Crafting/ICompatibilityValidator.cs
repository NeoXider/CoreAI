using System.Collections.Generic;

namespace CoreAI.Crafting
{
    /// <summary>
    /// Validates crafting compatibility requests.
    /// </summary>
    public interface ICompatibilityValidator
    {
        /// <summary>
        /// Validates an ingredient set and returns a crafting compatibility result.
        /// </summary>
        CompatibilityResult Validate(IReadOnlyList<string> ingredients);
    }
}
