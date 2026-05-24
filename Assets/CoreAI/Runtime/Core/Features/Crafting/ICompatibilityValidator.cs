using System.Collections.Generic;

namespace CoreAI.Crafting
{
    /// <summary>
    /// Validates crafting compatibility requests.
    /// </summary>
    public interface ICompatibilityValidator
    {
        /// <summary>
/// Executes Validate API operation.
        ///
        /// </summary>
        CompatibilityResult Validate(IReadOnlyList<string> ingredients);
    }
}
